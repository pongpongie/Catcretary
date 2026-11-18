using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Catcretary.Services;

namespace Catcretary.Pet;

/// <summary>
/// 투명한 최상위 창에 고양이만 그린다. 그려지지 않은 픽셀은 OS 가 뒤 창으로 클릭을 넘긴다.
///
/// 상태는 두 층이다.
///  - 의미 상태(Idle/Listening/Thinking/Done/Alert): 앱이 <see cref="SetState"/> 로 정한다.
///  - 대기 행동(Curious/Groom/Walk/Sleep/Scared): Idle·Alert 일 때만 스스로 끼워 넣고, 끝나면 의미 상태로 돌아간다.
/// </summary>
public partial class PetWindow : Window
{
    private readonly SpriteAnimator _animator = new();
    /// <summary>설정 창에서 저장하면 App.Config 객체가 바뀐다. 옛 객체를 저장해 새 설정을 덮지 않게 매번 읽는다.</summary>
    private static AppConfig _config => App.Current.Config;
    private readonly Random _rng = new();

    private readonly DispatcherTimer _blink = new();      // placeholder 눈 깜빡임
    private readonly DispatcherTimer _revert = new();     // 짧은 행동 뒤 복귀
    private readonly DispatcherTimer _behavior = new();   // 대기 행동 스케줄
    private readonly DispatcherTimer _walk = new();       // 걷기 프레임 이동

    private PetState _state = PetState.Idle;
    private bool _alert;
    private bool _hover;
    private DateTime _lastInteraction = DateTime.Now;

    // 걷기
    private Point _home;               // 드래그로 정한 자리. 산책은 이 근처에서만 한다.
    private double _walkDir;           // +1 오른쪽, -1 왼쪽
    private double _walkRemaining;     // 남은 거리(px)

    private const double WanderRange = 140;                       // home 기준 좌우 반경
    private const double WalkSpeed = 36;                          // px/s
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(6);

    public event Action? Clicked;
    public event Action? BriefingRequested;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public PetWindow(AppConfig config)
    {
        InitializeComponent();

        _animator.Load();
        if (_animator.Loaded)
        {
            Placeholder.Visibility = Visibility.Collapsed;
            Sprite.Visibility = Visibility.Visible;
            Sprite.Width = _animator.FrameWidth * config.PetScale;
            Sprite.Height = _animator.FrameHeight * config.PetScale;
            _animator.FrameChanged += (frame, flip) =>
            {
                Sprite.Source = frame;
                FaceFlip.ScaleX = flip ? -1 : 1;
                BadgeFlip.ScaleX = flip ? -1 : 1;
            };
            _animator.ClipFinished += _ => ReturnToBase();
            _animator.Play(PetState.Idle);
        }
        else
        {
            var s = config.PetScale / 4.0;
            Placeholder.LayoutTransform = new ScaleTransform(s, s);
            _blink.Interval = TimeSpan.FromSeconds(3.5);
            _blink.Tick += (_, _) => _ = BlinkAsync();
            _blink.Start();
        }

        _revert.Tick += (_, _) => { _revert.Stop(); ReturnToBase(); };

        _behavior.Tick += (_, _) => PickBehavior();
        ScheduleBehavior();

        _walk.Interval = TimeSpan.FromMilliseconds(33);
        _walk.Tick += (_, _) => WalkStep();

        Loaded += (_, _) => Place();
    }

    // ── 배치 ─────────────────────────────────────────────────

    private void Place()
    {
        if (_config.PetLeft is { } l && _config.PetTop is { } t && IsOnAnyScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 24;
            Top = wa.Bottom - ActualHeight - 24;
        }
        _home = new Point(Left, Top);
    }

    private static bool IsOnAnyScreen(double left, double top)
    {
        var r = SystemParameters.VirtualScreenLeft;
        var b = SystemParameters.VirtualScreenTop;
        var w = SystemParameters.VirtualScreenWidth;
        var h = SystemParameters.VirtualScreenHeight;
        return left >= r - 64 && left <= r + w - 32 && top >= b - 64 && top <= b + h - 32;
    }

    /// <summary>말풍선을 붙일 기준. 캐릭터의 현재 사각형(화면 좌표, DIP).</summary>
    public Rect Bounds => new(Left, Top, ActualWidth, ActualHeight);

    // ── 의미 상태 ─────────────────────────────────────────────

    public PetState State => _state;

    private PetState Base => _alert ? PetState.Alert : PetState.Idle;

    private bool IsResting => _state is PetState.Idle or PetState.Alert;

    /// <summary>앱이 정하는 상태. Idle 을 주면 Alert 여부에 따라 기본 상태로 해석한다.</summary>
    public void SetState(PetState state)
    {
        if (state == PetState.Idle) state = Base;
        StopWalk();
        Apply(state);

        if (state == PetState.Done) StartRevert(TimeSpan.FromSeconds(2));
        else if (state is PetState.Listening or PetState.Thinking) _lastInteraction = DateTime.Now;
    }

    /// <summary>마감 지난 항목이 남아 있는 동안 배지를 붙인다.</summary>
    public void SetAlert(bool alert)
    {
        _alert = alert;
        AlertBadge.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
        if (IsResting) Apply(Base);
    }

    /// <summary>브리핑 시각처럼 눈에 띄어야 할 때. 뛰어오른다.</summary>
    public void Wave()
    {
        Wake();
        StopWalk();
        Hop2();
        if (_animator.Has(PetState.Done)) Apply(PetState.Done);
        StartRevert(TimeSpan.FromSeconds(1.5));
    }

    private void Apply(PetState state)
    {
        _state = state;
        _revert.Stop();

        if (_animator.Loaded)
        {
            _animator.Play(state);
        }
        else
        {
            ThinkDots.Visibility = state == PetState.Thinking ? Visibility.Visible : Visibility.Collapsed;
            EyeL.Height = EyeR.Height = state is PetState.Listening or PetState.Curious ? 18 : 14;
            EyeL.Visibility = EyeR.Visibility = state == PetState.Sleep ? Visibility.Hidden : Visibility.Visible;
            BlinkL.Visibility = BlinkR.Visibility = state == PetState.Sleep ? Visibility.Visible : Visibility.Collapsed;
            if (state == PetState.Done) Hop2();
        }
    }

    private void ReturnToBase()
    {
        if (_state is PetState.Listening or PetState.Thinking) return; // 앱이 풀어줄 때까지 유지
        Apply(_hover && !_alert ? PetState.Curious : Base);
    }

    private void StartRevert(TimeSpan after)
    {
        _revert.Stop();
        _revert.Interval = after;
        _revert.Start();
    }

    /// <summary>사용자가 뭔가 했다. 자고 있었으면 깨운다.</summary>
    private void Wake()
    {
        _lastInteraction = DateTime.Now;
        if (_state == PetState.Sleep) Apply(Base);
    }

    // ── 대기 행동 ─────────────────────────────────────────────

    private void ScheduleBehavior()
    {
        _behavior.Stop();
        _behavior.Interval = TimeSpan.FromSeconds(_rng.Next(6, 14));
        _behavior.Start();
    }

    private void PickBehavior()
    {
        ScheduleBehavior();
        if (!IsResting || _hover) return;

        if (DateTime.Now - _lastInteraction > SleepAfter)
        {
            if (_state != PetState.Sleep) Apply(PetState.Sleep);
            return;
        }

        var roll = _rng.Next(100);
        if (roll < 45) StartWalk();
        else if (roll < 70) { Apply(PetState.Groom); StartRevert(TimeSpan.FromSeconds(_rng.Next(2, 4))); }
        else if (roll < 85) { Apply(PetState.Curious); StartRevert(TimeSpan.FromSeconds(_rng.Next(2, 3))); }
        // 나머지는 가만히 있는다.
    }

    private void StartWalk()
    {
        var area = ScreenHelper.WorkAreaFor(this);
        var minX = Math.Max(area.Left, _home.X - WanderRange);
        var maxX = Math.Min(area.Right - ActualWidth, _home.X + WanderRange);
        if (maxX - minX < 40) return;

        var target = minX + _rng.NextDouble() * (maxX - minX);
        _walkDir = target >= Left ? 1 : -1;
        _walkRemaining = Math.Abs(target - Left);
        if (_walkRemaining < 16) return;

        _state = PetState.Walk;
        _revert.Stop();
        if (_animator.Loaded) _animator.Play(PetState.Walk, faceRight: _walkDir > 0);
        else FaceFlip.ScaleX = _walkDir > 0 ? 1 : -1;
        _walk.Start();
    }

    private void WalkStep()
    {
        var step = WalkSpeed * _walk.Interval.TotalSeconds;
        Left += _walkDir * step;
        _walkRemaining -= step;
        if (_walkRemaining <= 0) { StopWalk(); ReturnToBase(); }
    }

    private void StopWalk()
    {
        if (!_walk.IsEnabled) return;
        _walk.Stop();
        FaceFlip.ScaleX = 1;
        BadgeFlip.ScaleX = 1;
    }

    // ── 점프·깜빡임 ──────────────────────────────────────────

    private void Hop2()
    {
        var anim = new DoubleAnimation
        {
            From = 0, To = -18, Duration = TimeSpan.FromMilliseconds(160),
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Hop.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private async Task BlinkAsync()
    {
        if (_state == PetState.Sleep) return;
        BlinkL.Visibility = BlinkR.Visibility = Visibility.Visible;
        EyeL.Visibility = EyeR.Visibility = Visibility.Hidden;
        await Task.Delay(120);
        BlinkL.Visibility = BlinkR.Visibility = Visibility.Collapsed;
        EyeL.Visibility = EyeR.Visibility = Visibility.Visible;
    }

    // ── 입력 ─────────────────────────────────────────────────

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _hover = true;
        if (IsResting && !_alert) Apply(PetState.Curious);
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        _hover = false;
        if (_state == PetState.Curious) Apply(Base);
    }

    /// <summary>DragMove 는 버튼을 뗄 때까지 막힌다. 돌아왔을 때 위치가 그대로면 클릭으로 본다.</summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Wake();
        StopWalk();

        var before = new Point(Left, Top);
        var wasBusy = _state is PetState.Listening or PetState.Thinking;
        if (!wasBusy) Apply(PetState.Scared); // 들어 올려지는 중

        try { DragMove(); } catch (InvalidOperationException) { ReturnToBase(); return; }

        var moved = Math.Abs(Left - before.X) > 3 || Math.Abs(Top - before.Y) > 3;
        if (moved)
        {
            _home = new Point(Left, Top);
            _config.PetLeft = Left;
            _config.PetTop = Top;
            _config.Save();
            if (!wasBusy) { Apply(PetState.Groom); StartRevert(TimeSpan.FromSeconds(1.5)); } // 내려놓으면 털 고르기
        }
        else
        {
            // 클릭 반응은 매번 조금 다르게.
            if (!wasBusy)
            {
                if (_rng.Next(3) == 0) { Hop2(); Apply(PetState.Done); StartRevert(TimeSpan.FromSeconds(1)); }
                else Apply(PetState.Listening);
            }
            Clicked?.Invoke();
        }
    }

    private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Wake();
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("말 걸기", () => Clicked?.Invoke()));
        menu.Items.Add(MakeItem("오늘 브리핑", () => BriefingRequested?.Invoke()));
        menu.Items.Add(MakeItem("새로고침", () => RefreshRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("설정", () => SettingsRequested?.Invoke()));
        menu.Items.Add(MakeItem("종료", () => ExitRequested?.Invoke()));
        menu.IsOpen = true;
    }

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
