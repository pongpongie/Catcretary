using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Catcretary.Models;
using Catcretary.Services;
using Microsoft.Win32;

namespace Catcretary.Widget;

/// <summary>
/// 바탕화면에 붙는 월간 캘린더. 미완료 Plane 항목(내 담당 또는 전체, 프로젝트 선택)을 마감일 칸에 그린다.
/// 항목 막대를 다른 날로 끌어 놓으면 시작일·마감일을 같은 날 수만큼 옮긴다.
/// 설정은 우클릭 메뉴에서 바꾸고, 바꾸는 즉시 config.json 의 widget 에 저장한다.
/// 칸은 코드로 만든다. 한 칸에 들어갈 줄 수는 칸 높이로 계산하고, 넘치면 "+N건" 으로 접는다.
/// </summary>
public partial class CalendarWidgetWindow : Window
{
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _dayTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private DateOnly _today = Today;
    private DateOnly _month;           // 표시 중인 달의 1일
    private int _weekOffset;           // 주 focus 모드에서 이번 주 기준으로 옮긴 주 수
    private List<BriefingItem> _all = new();
    private List<CycleInfo> _cycles = new();
    private List<PlaneProject> _projects = new();
    private DateTime? _updatedAt;
    private string _status = "";
    private DateOnly _viewStart;       // 화면 첫 칸의 날짜. 끌어 놓은 위치를 날짜로 바꿀 때 쓴다.

    // 막대 끌기 상태
    private BriefingItem? _dragItem;
    private DateOnly _dragGrab;        // 막대를 잡은 날
    private Point _dragOrigin;
    private bool _dragging;
    private bool _suppressClick;
    private int _capacity = -1;
    private bool _placed;

    public event Action? RefreshRequested;
    public event Action<BriefingItem>? OpenRequested;
    /// <summary>막대를 다른 날로 끌어 놓았다. 새 시작일(없던 항목은 null), 새 마감일(없던 항목은 null).</summary>
    public event Action<BriefingItem, DateOnly?, DateOnly?>? DatesMoveRequested;
    public event Action? BoardRequested;
    public event Action<DateOnly>? DayDoubleClicked;
    public event Action? SettingsRequested;
    public event Action? HideRequested;

    private static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>설정 창에서 저장하면 App.Config 객체가 바뀌므로 참조를 들고 있지 않고 매번 읽는다.</summary>
    private static WidgetConfig W => App.Current.Config.Widget;

    public CalendarWidgetWindow()
    {
        InitializeComponent();
        _month = FirstOfMonth(_today);

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveBounds(); };
        _dayTimer.Tick += (_, _) => CheckDayChanged();
        _dayTimer.Start();

        SourceInitialized += (_, _) => DesktopPin.Attach(this, keepBottom: () => !W.AlwaysOnTop);
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _dayTimer.Stop();
            _saveTimer.Stop();
        };

        Place();
        ApplySettings();
        Render();
    }

    // ── 외부에서 부르는 것 ───────────────────────────────────

    /// <param name="all">대상 프로젝트의 모든 항목. 범위·프로젝트·미완료 필터는 위젯이 건다.</param>
    public void SetData(IEnumerable<BriefingItem> all, IEnumerable<CycleInfo> cycles, IEnumerable<PlaneProject> projects, DateTime at)
    {
        _all = all.ToList();
        _cycles = cycles.ToList();
        _projects = projects.ToList();
        _updatedAt = at;
        Render();
    }

    /// <summary>끌어 옮긴 항목을 응답 전에 먼저 그리거나, 실패해 되돌린 뒤 다시 그린다.</summary>
    public void Redraw() => Render();

    private bool InProjects(string identifier) =>
        W.Projects.Count == 0 || W.Projects.Contains(identifier, StringComparer.OrdinalIgnoreCase);

    /// <summary>지금 범위·프로젝트 선택에 들어가는 미완료 항목.</summary>
    private List<BriefingItem> VisibleItems() => _all
        .Where(i => i.IsOpen && !i.IsDone)
        .Where(i => W.Scope != "mine" || i.IsMine)
        .Where(i => InProjects(i.ProjectIdentifier))
        .ToList();

    private string ScopeLabel =>
        (W.Scope == "mine" ? "내 담당" : "전체")
        + (W.Projects.Count == 0 ? "" : $" · {string.Join(", ", W.Projects)}");

    private void UpdateStatus(List<BriefingItem> visible)
    {
        if (_updatedAt is not { } at) return;
        var undated = visible.Count(i => i.Due is null && i.StartDate is null);
        _status = $"업데이트 {at:HH:mm} · {ScopeLabel} 미완료 {visible.Count}건" + (undated > 0 ? $" (날짜 없음 {undated})" : "");
    }

    /// <summary>트레이 메뉴처럼 창 밖에서 설정을 바꿨을 때 다시 적용한다.</summary>
    public void Reapply()
    {
        ApplySettings();
        Render();
    }

    public void SetStatus(string status)
    {
        _status = status;
        StatusText.Text = status;
    }

    /// <summary>종료 직전에도 부른다. 배치가 끝나기 전 값(기본 크기)으로 덮어쓰지 않게 막는다.</summary>
    public void SaveBounds()
    {
        if (!_placed || WindowState != WindowState.Normal) return;
        var w = W;
        w.Left = Math.Round(Left);
        w.Top = Math.Round(Top);
        w.Width = Math.Round(ActualWidth > 0 ? ActualWidth : Width);
        w.Height = Math.Round(ActualHeight > 0 ? ActualHeight : Height);
        App.Current.Config.Save();
    }

    // ── 배치 ─────────────────────────────────────────────────

    private void Place()
    {
        var w = W;
        var width = Math.Max(MinWidth, w.Width ?? 560);
        var height = Math.Max(MinHeight, w.Height ?? 480);
        Width = width;
        Height = height;

        if (w.Left is { } l && w.Top is { } t && IsVisibleOnScreen(l, t, width, height))
        {
            Left = l;
            Top = t;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - width - 24;
            Top = wa.Top + 24;
        }
        _placed = true;
    }

    /// <summary>모니터 구성이 바뀌어 저장된 자리가 화면 밖이면 기본 위치로 되돌린다. 제목 줄이 조금이라도 보이면 유지.</summary>
    private static bool IsVisibleOnScreen(double left, double top, double width, double height)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBar = new Rect(left, top, width, Math.Min(height, 40));
        titleBar.Intersect(screen);
        return !titleBar.IsEmpty && titleBar.Width >= 60 && titleBar.Height >= 16;
    }

    private void QueueSave()
    {
        if (!_placed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (W.Locked || e.ClickCount > 1) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    // ── 테마·고정 ────────────────────────────────────────────

    private void ApplySettings()
    {
        var w = W;
        var p = WidgetPalette.For(w.Theme);

        p.ApplyTo(Resources);

        // 알파 0 픽셀은 클릭이 뒤로 통과하므로 투명도 100% 여도 1/255 는 남긴다.
        var alpha = (byte)Math.Max(1, Math.Round(255 * (100 - Math.Clamp(w.Transparency, 0, 100)) / 100.0));
        Frame.Background = new SolidColorBrush(Color.FromArgb(alpha, p.Background.R, p.Background.G, p.Background.B));

        ResizeMode = w.Locked ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(w.Locked ? 0 : 6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Topmost = w.AlwaysOnTop;
        if (!w.AlwaysOnTop) DesktopPin.SendToBottom(this);

        Header.Cursor = w.Locked ? null : Cursors.SizeAll;
        LockIcon.Visibility = w.Locked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || W.Theme != "system") return;
        Dispatcher.BeginInvoke(() => { ApplySettings(); Render(); });
    }

    // ── 날짜 이동 ────────────────────────────────────────────

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Today_Click(object sender, RoutedEventArgs e) => GoToday();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    /// <summary>Plane 조회 중이면 새로고침 아이콘을 돌린다.</summary>
    public void SetBusy(bool busy) => Views.Spinner.Set(RefreshIcon, busy);

    /// <summary>월간 보기는 한 달, 주 focus 모드는 한 주씩 옮긴다.</summary>
    private void Move(int step)
    {
        if (W.FocusWeek) _weekOffset += step;
        else _month = _month.AddMonths(step);
        Render();
    }

    private void GoToday()
    {
        _month = FirstOfMonth(_today);
        _weekOffset = 0;
        Render();
    }

    /// <summary>
    /// 자정이 지나면 오늘 표시를 옮긴다. 이번 달을 보고 있었으면 새 달로 따라간다.
    /// 주 focus 모드는 이번 주 기준으로 계산하므로 옮기지 않은 상태면 주가 바뀔 때 저절로 따라간다.
    /// </summary>
    private void CheckDayChanged()
    {
        var now = Today;
        if (now == _today) return;
        var wasCurrent = _month == FirstOfMonth(_today);
        _today = now;
        if (wasCurrent) _month = FirstOfMonth(now);
        Render();
    }

    // ── 그리기 ───────────────────────────────────────────────

    private double BaseFont => Math.Clamp(W.FontSize, 9, 20);

    /// <summary>주 focus 모드에서 보여줄 주 수와 이번 주 위에 둘 주 수. 이번 주가 가운데(3번째) 줄에 온다.</summary>
    private const int FocusWeeks = 5;
    private const int FocusWeeksBefore = 2;

    /// <summary>한 항목이 달력에 차지하는 날짜 구간. 기간 표시를 끄면 First == Last.</summary>
    private sealed record Span(BriefingItem Item, DateOnly First, DateOnly Last);

    private void Render()
    {
        var w = W;
        var visible = VisibleItems();
        UpdateStatus(visible);
        StatusText.Text = _status;
        CancelDrag();

        var firstDow = w.WeekStartsMonday ? DayOfWeek.Monday : DayOfWeek.Sunday;

        WeekHeader.Children.Clear();
        for (var i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)firstDow + i) % 7);
            var t = new TextBlock
            {
                Text = DayNames[(int)dow],
                FontSize = BaseFont - 1,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, dow switch
            {
                DayOfWeek.Sunday => "W.Sunday",
                DayOfWeek.Saturday => "W.Saturday",
                _ => "W.Muted",
            });
            WeekHeader.Children.Add(t);
        }

        DateOnly start;
        int rows;
        if (w.FocusWeek)
        {
            var thisWeek = _today.AddDays(-(((int)_today.DayOfWeek - (int)firstDow + 7) % 7));
            start = thisWeek.AddDays(7 * (_weekOffset - FocusWeeksBefore));
            rows = FocusWeeks;
        }
        else
        {
            var offset = ((int)_month.DayOfWeek - (int)firstDow + 7) % 7;
            start = _month.AddDays(-offset);
            rows = (int)Math.Ceiling((offset + DateTime.DaysInMonth(_month.Year, _month.Month)) / 7.0);
        }
        var end = start.AddDays(rows * 7 - 1);
        _viewStart = start;

        TitleText.Text = w.FocusWeek ? RangeTitle(start, end) : $"{_month.Year}년 {_month.Month}월";
        PrevButton.ToolTip = w.FocusWeek ? "이전 주" : "이전 달";
        NextButton.ToolTip = w.FocusWeek ? "다음 주" : "다음 달";
        TodayButton.ToolTip = w.FocusWeek ? "이번 주를 가운데로" : "이번 달";

        var spans = CollectSpans(visible, start, end);
        _capacity = ComputeCapacity(rows);

        DayGrid.Rows = rows;
        DayGrid.Children.Clear();
        for (var r = 0; r < rows; r++) DayGrid.Children.Add(BuildWeek(start.AddDays(r * 7), spans));
    }

    private static string RangeTitle(DateOnly start, DateOnly end) =>
        start.Year != end.Year ? $"{start.Year}년 {start.Month}월 – {end.Year}년 {end.Month}월"
        : start.Month != end.Month ? $"{start.Year}년 {start.Month}월 – {end.Month}월"
        : $"{start.Year}년 {start.Month}월";

    /// <summary>
    /// 화면 범위에 걸치는 항목 구간을 모은다. 기간 표시를 켜면 시작일~마감일, 끄면 마감일(없으면 시작일) 하루.
    /// 줄 배정 순서: 먼저 시작하는 것, 긴 것, 마감 지난 것, 우선순위 높은 것 순. 주가 바뀌어도 같은 순서라 막대 위치가 덜 흔들린다.
    /// </summary>
    private List<Span> CollectSpans(List<BriefingItem> items, DateOnly from, DateOnly to)
    {
        var spans = new List<Span>();
        foreach (var item in items)
        {
            Span span;
            if (item.Due is { } due)
                span = W.ShowRange && item.StartDate is { } s && s < due ? new(item, s, due) : new(item, due, due);
            else if (item.StartDate is { } s2)
                span = new(item, s2, s2);
            else continue;

            if (span.Last >= from && span.First <= to) spans.Add(span);
        }

        spans.Sort((a, b) =>
        {
            var c = a.First.CompareTo(b.First);
            if (c != 0) return c;
            c = (b.Last.DayNumber - b.First.DayNumber).CompareTo(a.Last.DayNumber - a.First.DayNumber);
            if (c != 0) return c;
            c = b.Item.IsOverdue.CompareTo(a.Item.IsOverdue);
            if (c != 0) return c;
            c = PriorityRank(a.Item.Priority).CompareTo(PriorityRank(b.Item.Priority));
            return c != 0 ? c : string.CompareOrdinal(a.Item.Key, b.Item.Key);
        });
        return spans;
    }

    private double HeadLineHeight => (BaseFont + 1) * 1.4 + 2;
    private double ItemLineHeight => (BaseFont - 1) * 1.4 + 1;

    private int ComputeCapacity(int rows)
    {
        var gridHeight = DayGrid.ActualHeight > 0 ? DayGrid.ActualHeight : Math.Max(0, Height - 100);
        var cellHeight = gridHeight / Math.Max(1, rows) - 6; // 칸 안쪽 여백
        return Math.Max(0, (int)Math.Floor((cellHeight - HeadLineHeight) / ItemLineHeight));
    }

    private void DayGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ComputeCapacity(DayGrid.Rows) != _capacity) Render();
    }

    /// <summary>월간 보기에서 표시 중인 달 밖의 날은 흐리게. 주 focus 모드는 여러 달에 걸치므로 흐리게 하지 않는다.</summary>
    private bool IsDimmed(DateOnly date) => !W.FocusWeek && date.Month != _month.Month;

    /// <summary>
    /// 한 주 = 7열 Grid. 0행은 날짜 머리, 1~capacity 행은 항목 줄.
    /// 항목은 여러 열에 걸친 막대 하나로 그리고, 겹치지 않는 가장 위 줄에 넣는다. 줄이 모자라면 해당 날 머리에 +N.
    /// </summary>
    private UIElement BuildWeek(DateOnly weekStart, List<Span> spans)
    {
        var weekEnd = weekStart.AddDays(6);
        var grid = new Grid { Background = Brushes.Transparent }; // 빈 곳도 더블클릭을 받게.
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeadLineHeight + 1) });
        for (var i = 0; i < _capacity; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ItemLineHeight) });
        grid.RowDefinitions.Add(new RowDefinition());
        var rowCount = grid.RowDefinitions.Count;

        var lanes = new List<bool[]>();
        var hidden = Enumerable.Range(0, 7).Select(_ => new List<BriefingItem>()).ToArray();
        var bars = new List<UIElement>();
        foreach (var span in spans)
        {
            if (span.Last < weekStart || span.First > weekEnd) continue;
            var a = Math.Max(0, span.First.DayNumber - weekStart.DayNumber);
            var b = Math.Min(6, span.Last.DayNumber - weekStart.DayNumber);

            var lane = 0;
            while (lane < lanes.Count && Occupied(lanes[lane], a, b)) lane++;
            if (lane == lanes.Count) lanes.Add(new bool[7]);
            for (var c = a; c <= b; c++) lanes[lane][c] = true;

            if (lane < _capacity)
                bars.Add(BuildBar(span, weekStart, a, b, lane));
            else
                for (var c = a; c <= b; c++) hidden[c].Add(span.Item);
        }

        for (var c = 0; c < 7; c++)
        {
            var date = weekStart.AddDays(c);
            var isToday = date == _today;
            var opacity = IsDimmed(date) ? 0.4 : 1;

            var back = new Border
            {
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(isToday ? 1 : 0),
                Opacity = opacity,
            };
            if (isToday)
            {
                back.SetResourceReference(Border.BackgroundProperty, "W.TodayFill");
                back.SetResourceReference(Border.BorderBrushProperty, "W.TodayRing");
            }
            Grid.SetColumn(back, c);
            Grid.SetRowSpan(back, rowCount);
            grid.Children.Add(back);

            var head = BuildHead(date, hidden[c]);
            head.Opacity = opacity;
            Grid.SetColumn(head, c);
            grid.Children.Add(head);

            if (BuildCycleBand(date, rowCount) is { } band)
            {
                band.Opacity *= opacity;
                Grid.SetColumn(band, c);
                grid.Children.Add(band);
            }
        }
        foreach (var bar in bars) grid.Children.Add(bar);

        var cell = new Border
        {
            Child = grid,
            BorderThickness = new Thickness(0, 1, 0, 0),
            ClipToBounds = true,
        };
        cell.SetResourceReference(Border.BorderBrushProperty, "W.Grid");
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || grid.ActualWidth <= 0) return;
            var col = Math.Clamp((int)(e.GetPosition(grid).X / (grid.ActualWidth / 7)), 0, 6);
            e.Handled = true;
            DayDoubleClicked?.Invoke(weekStart.AddDays(col));
        };
        return cell;
    }

    /// <summary>
    /// 진행 중인 cycle 기간이면 칸 맨 아래 얇은 띠. 여러 프로젝트 cycle 이 겹쳐도 띠는 하나이고 툴팁에 모두 적는다.
    /// 시작·끝 날에만 모서리를 둥글게 해서 기간이 이어져 보이게 한다.
    /// </summary>
    private Border? BuildCycleBand(DateOnly date, int rowCount)
    {
        if (!W.ShowCycles) return null;
        var hits = _cycles.Where(c => c.Start <= date && date <= c.End && InProjects(c.ProjectIdentifier)).ToList();
        if (hits.Count == 0) return null;

        var first = hits.Any(c => c.Start == date);
        var last = hits.Any(c => c.End == date);
        var band = new Border
        {
            Height = 3,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(first ? 4 : 0, 0, last ? 4 : 0, 2),
            CornerRadius = new CornerRadius(first ? 1.5 : 0, last ? 1.5 : 0, last ? 1.5 : 0, first ? 1.5 : 0),
            Opacity = 0.7,
            ToolTip = string.Join("\n", hits.Select(c => $"{c.ProjectIdentifier} · {c.Summary}" + (c.End == date ? " · 이 날 끝남" : ""))),
        };
        band.SetResourceReference(Border.BackgroundProperty, "W.TodayRing");
        Grid.SetRowSpan(band, rowCount);
        return band;
    }

    private static bool Occupied(bool[] lane, int a, int b)
    {
        for (var c = a; c <= b; c++) if (lane[c]) return true;
        return false;
    }

    private FrameworkElement BuildHead(DateOnly date, List<BriefingItem> hidden)
    {
        var w = W;
        var isToday = date == _today;
        var holiday = w.ShowHolidays ? KoreanCalendar.HolidayName(date, w.ExtraHolidays) : null;

        var head = new DockPanel { LastChildFill = true, Height = HeadLineHeight, Margin = new Thickness(5, 1, 5, 0) };
        var number = new TextBlock
        {
            // 5주 보기와 월 보기 앞뒤 칸은 달이 섞이므로 모든 칸에 월을 붙인다.
            Text = $"{date.Month}/{date.Day}",
            FontSize = BaseFont + 1,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        number.SetResourceReference(TextBlock.ForegroundProperty,
            holiday is not null || date.DayOfWeek == DayOfWeek.Sunday ? "W.Sunday"
            : date.DayOfWeek == DayOfWeek.Saturday ? "W.Saturday" : "W.Text");
        DockPanel.SetDock(number, Dock.Left);
        head.Children.Add(number);

        var lunar = w.ShowLunar ? KoreanCalendar.ToLunar(date) : null;

        // 넘치는 항목 수는 날짜 옆에 붙여 항목 줄을 하나라도 더 쓴다.
        if (hidden.Count > 0)
        {
            var more = new TextBlock
            {
                Text = $"+{hidden.Count}",
                FontSize = BaseFont - 2,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = string.Join("\n", hidden.Select(i => $"{i.Key}  {i.Name}")),
            };
            more.SetResourceReference(TextBlock.ForegroundProperty, "W.TodayRing");
            DockPanel.SetDock(more, Dock.Left);
            head.Children.Add(more);
        }

        // 오른쪽 끝 표기: 공휴일이면 공휴일 이름(칸이 좁으니 음력은 툴팁으로), 아니면 음력.
        var label = new TextBlock
        {
            FontSize = BaseFont - 2.5,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        if (holiday is not null)
        {
            label.Text = holiday;
            label.ToolTip = lunar is { } l1 ? $"{holiday}\n{l1.Long}" : holiday;
            label.SetResourceReference(TextBlock.ForegroundProperty, "W.Sunday");
        }
        else if (lunar is { } l2)
        {
            label.Text = l2.Short;
            label.ToolTip = l2.Long;
            label.FontWeight = l2.Day == 1 ? FontWeights.SemiBold : FontWeights.Normal;
            label.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        }
        head.Children.Add(label);
        return head;
    }

    /// <summary>
    /// 한 주 안의 구간 [a, b] 열에 걸친 막대. 앞 주에서 이어지거나 다음 주로 이어지는 쪽은 모서리만 각지게 한다.
    /// 여백은 주 줄끼리 이어 보일 수 없으므로 다른 막대와 같게 둬서 같은 열의 막대 끝을 맞춘다.
    /// 표시 중인 달 밖의 날에 걸친 부분만 흐리게 한다.
    /// </summary>
    private UIElement BuildBar(Span span, DateOnly weekStart, int a, int b, int lane)
    {
        var item = span.Item;
        var openLeft = span.First < weekStart.AddDays(a);
        var openRight = span.Last > weekStart.AddDays(b);

        var text = new TextBlock
        {
            Text = item.Name,
            FontSize = BaseFont - 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (item.IsOverdue) text.SetResourceReference(TextBlock.ForegroundProperty, "W.Danger");

        var bar = new SolidColorBrush(PriorityColor(item));
        bar.Freeze();

        var border = new Border
        {
            Child = text,
            Height = ItemLineHeight - 1,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(5, 1, 5, 0),
            Padding = new Thickness(4, 0, 2, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = bar,
            CornerRadius = new CornerRadius(openLeft ? 0 : 2, openRight ? 0 : 2, openRight ? 0 : 2, openLeft ? 0 : 2),
            Cursor = Cursors.Hand,
            ToolTip = Tooltip(item),
            Tag = item,
        };
        border.SetResourceReference(Border.BackgroundProperty, "W.ItemFill");
        ApplyDimming(border, weekStart, a, b);
        Grid.SetColumn(border, a);
        Grid.SetColumnSpan(border, b - a + 1);
        Grid.SetRow(border, 1 + lane);

        // 칸의 더블클릭(새 항목)으로 번지지 않게 여기서 멈춘다. 그냥 누르면 열기, 끌면 날짜 옮기기.
        border.MouseLeftButtonDown += Bar_MouseLeftButtonDown;
        border.MouseMove += Bar_MouseMove;
        border.MouseLeftButtonUp += Bar_MouseLeftButtonUp;
        border.LostMouseCapture += (_, _) => CancelDrag();
        return border;
    }

    // ── 막대 끌기 ────────────────────────────────────────────

    /// <summary>DayGrid 안의 점 → 그 칸의 날짜. 주 줄과 요일 열은 모두 같은 크기다.</summary>
    private DateOnly DateAt(Point p)
    {
        var rows = Math.Max(1, DayGrid.Rows);
        var r = Math.Clamp((int)(p.Y / (DayGrid.ActualHeight / rows)), 0, rows - 1);
        var c = Math.Clamp((int)(p.X / (DayGrid.ActualWidth / 7)), 0, 6);
        return _viewStart.AddDays(r * 7 + c);
    }

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: BriefingItem item } bar || DayGrid.ActualWidth <= 0) return;
        _dragItem = item;
        _dragOrigin = e.GetPosition(DayGrid);
        _dragGrab = DateAt(_dragOrigin);
        _dragging = false;
        _suppressClick = false;
        bar.CaptureMouse();
    }

    private void Bar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(DayGrid);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
        }

        var target = DateAt(p);
        var rows = Math.Max(1, DayGrid.Rows);
        var cellW = DayGrid.ActualWidth / 7;
        var cellH = DayGrid.ActualHeight / rows;
        var index = target.DayNumber - _viewStart.DayNumber;
        Canvas.SetLeft(DropMarker, index % 7 * cellW + 1);
        Canvas.SetTop(DropMarker, index / 7 * cellH + 1);
        DropMarker.Width = Math.Max(0, cellW - 2);
        DropMarker.Height = Math.Max(0, cellH - 2);
        DropMarker.Visibility = Visibility.Visible;

        var (start, due) = Shifted(_dragItem, target.DayNumber - _dragGrab.DayNumber);
        StatusText.Text = $"{_dragItem.Key} → " + DatesText(start, due) + "  (놓으면 Plane 에 저장, 제자리에 놓으면 취소)";
    }

    private void Bar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var item = _dragItem;
        var dragging = _dragging;
        var p = e.GetPosition(DayGrid);
        CancelDrag();
        if (item is null) return;

        if (!dragging)
        {
            if (!_suppressClick) OpenRequested?.Invoke(item);
            return;
        }
        var delta = DateAt(p).DayNumber - _dragGrab.DayNumber;
        if (delta == 0) return;
        var (start, due) = Shifted(item, delta);
        DatesMoveRequested?.Invoke(item, start, due);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragItem is not null)
        {
            _suppressClick = true;
            CancelDrag();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>끌기를 끝낸다. 렌더링으로 막대가 사라지거나 캡처를 잃어도 여기로 온다.</summary>
    private void CancelDrag()
    {
        if (_dragItem is null && DropMarker.Visibility != Visibility.Visible) return;
        var bar = Mouse.Captured as FrameworkElement;
        _dragItem = null;
        _dragging = false;
        DropMarker.Visibility = Visibility.Collapsed;
        StatusText.Text = _status;
        if (bar is { Tag: BriefingItem }) bar.ReleaseMouseCapture();
    }

    /// <summary>있는 날짜만 같은 날 수만큼 민다. 시작~마감 길이는 그대로다.</summary>
    private static (DateOnly? Start, DateOnly? Due) Shifted(BriefingItem item, int days) =>
        (item.StartDate?.AddDays(days), item.Due?.AddDays(days));

    private static string DatesText(DateOnly? start, DateOnly? due) =>
        start is { } s && due is { } d ? $"{s:M'/'d}~{d:M'/'d}"
        : due is { } d2 ? $"마감 {d2:M'/'d}"
        : start is { } s2 ? $"시작 {s2:M'/'d}" : "";

    private void ApplyDimming(UIElement bar, DateOnly weekStart, int a, int b)
    {
        var dim = Enumerable.Range(a, b - a + 1).Select(c => IsDimmed(weekStart.AddDays(c))).ToArray();
        if (dim.All(d => !d)) return;
        if (dim.All(d => d)) { bar.Opacity = 0.4; return; }

        // 일부만 달 밖이면 열 경계에서 끊기는 불투명도 마스크.
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i < dim.Length; i++)
        {
            var color = Color.FromArgb((byte)(dim[i] ? 102 : 255), 0, 0, 0);
            mask.GradientStops.Add(new GradientStop(color, (double)i / dim.Length));
            mask.GradientStops.Add(new GradientStop(color, (double)(i + 1) / dim.Length));
        }
        mask.Freeze();
        bar.OpacityMask = mask;
    }

    private static string Tooltip(BriefingItem item)
    {
        var priority = item.PriorityLabel.Length > 0 ? $"우선순위 {item.PriorityLabel}" : "우선순위 없음";
        var period = item.StartDate is { } s ? $"시작 {s:MM'/'dd} · " : "";
        return $"{item.Key}  {item.Name}\n{item.ProjectName}\n{item.StateName} · {priority} · {item.AssigneeText}\n{period}마감 {item.DueText}\n클릭하면 Plane 에서 열고, 다른 날로 끌면 날짜를 옮긴다";
    }

    private static Color PriorityColor(BriefingItem item) => item.Priority switch
    {
        "urgent" => Color.FromRgb(0xDC, 0x26, 0x26),
        "high" => Color.FromRgb(0xEA, 0x58, 0x0C),
        "medium" => Color.FromRgb(0xCA, 0x8A, 0x04),
        "low" => Color.FromRgb(0x25, 0x63, 0xEB),
        _ => TryParseColor(item.StateColor) ?? Color.FromRgb(0x9C, 0xA3, 0xAF),
    };

    private static Color? TryParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "urgent" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4,
    };

    // ── 우클릭 메뉴 ──────────────────────────────────────────

    private void Frame_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var w = W;
        // 메뉴는 창 밖 팝업이라 창 Resources 를 못 본다. 같은 테마 색을 메뉴에도 넣는다.
        var menu = new ContextMenu();
        WidgetPalette.For(w.Theme).ApplyTo(menu.Resources);
        menu.Items.Add(Item("새로고침", () => RefreshRequested?.Invoke()));
        menu.Items.Add(Item(w.FocusWeek ? "이번 주로" : "이번 달로", GoToday));
        menu.Items.Add(Item("상태별 보드 보이기", () => BoardRequested?.Invoke()));
        menu.Items.Add(new Separator());

        menu.Items.Add(Choice("표시 범위", w.Scope, v => w.Scope = v, ("내 담당", "mine"), ("전체 (담당자 무관)", "all")));
        menu.Items.Add(ProjectMenu());
        menu.Items.Add(new Separator());

        menu.Items.Add(Choice("테마", w.Theme, v => w.Theme = v,
            ("밝게", "light"), ("어둡게", "dark"), ("Windows 설정 따라감", "system")));
        menu.Items.Add(Choice("투명도", w.Transparency, v => w.Transparency = v,
            Enumerable.Range(0, 11).Select(i => (i == 0 ? "0% (불투명)" : i == 10 ? "100% (배경 없음)" : $"{i * 10}%", i * 10)).ToArray()));
        menu.Items.Add(Choice("글자 크기", w.FontSize, v => w.FontSize = v,
            ("작게", 11.0), ("보통", 12.0), ("크게", 14.0), ("아주 크게", 16.0)));
        menu.Items.Add(Choice("주 시작", w.WeekStartsMonday, v => w.WeekStartsMonday = v,
            ("일요일", false), ("월요일", true)));
        menu.Items.Add(Toggle("음력 표시", w.ShowLunar, v => w.ShowLunar = v));
        menu.Items.Add(Toggle("공휴일 표시", w.ShowHolidays, v => w.ShowHolidays = v));
        menu.Items.Add(Toggle("시작일~마감일 기간 모두 표시", w.ShowRange, v => w.ShowRange = v));
        menu.Items.Add(Toggle("진행 중인 cycle 기간 표시", w.ShowCycles, v => w.ShowCycles = v));
        menu.Items.Add(Toggle("이번 주 가운데 (5주 보기)", w.FocusWeek, v => { w.FocusWeek = v; _weekOffset = 0; _month = FirstOfMonth(_today); }));
        menu.Items.Add(new Separator());

        menu.Items.Add(Toggle("항상 위에 표시", w.AlwaysOnTop, v => w.AlwaysOnTop = v));
        menu.Items.Add(Toggle("위치·크기 고정", w.Locked, v => w.Locked = v));
        menu.Items.Add(Item("위젯 숨기기", () => HideRequested?.Invoke()));
        menu.Items.Add(Item("설정", () => SettingsRequested?.Invoke()));

        menu.PlacementTarget = Frame;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>프로젝트 여러 개를 골라 볼 수 있다. 아무것도 고르지 않으면 모든 프로젝트.</summary>
    private MenuItem ProjectMenu()
    {
        var w = W;
        var parent = new MenuItem { Header = "프로젝트" };
        var all = new MenuItem { Header = "모든 프로젝트", IsChecked = w.Projects.Count == 0 };
        all.Click += (_, _) => { w.Projects.Clear(); Changed(); };
        parent.Items.Add(all);
        parent.Items.Add(new Separator());
        foreach (var p in _projects.OrderBy(p => p.Identifier))
        {
            var on = w.Projects.Contains(p.Identifier, StringComparer.OrdinalIgnoreCase);
            var item = new MenuItem { Header = $"{p.Identifier}  {p.Name}", IsCheckable = true, IsChecked = on, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                w.Projects.RemoveAll(x => string.Equals(x, p.Identifier, StringComparison.OrdinalIgnoreCase));
                if (item.IsChecked) w.Projects.Add(p.Identifier);
                all.IsChecked = w.Projects.Count == 0;
                Changed();
            };
            parent.Items.Add(item);
        }
        return parent;
    }

    private void Changed()
    {
        App.Current.Config.Save();
        ApplySettings();
        Render();
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem Toggle(string header, bool value, Action<bool> set)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
        item.Click += (_, _) => { set(item.IsChecked); Changed(); };
        return item;
    }

    private MenuItem Choice<T>(string header, T current, Action<T> set, params (string Label, T Value)[] options)
    {
        var parent = new MenuItem { Header = header };
        foreach (var (label, value) in options)
        {
            var child = new MenuItem { Header = label, IsChecked = EqualityComparer<T>.Default.Equals(current, value) };
            child.Click += (_, _) => { set(value); Changed(); };
            parent.Items.Add(child);
        }
        return parent;
    }
}
