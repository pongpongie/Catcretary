using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using Catcretary.Board;
using Catcretary.Models;
using Catcretary.Pet;
using Catcretary.Services;
using Catcretary.Views;
using Catcretary.Widget;
using Forms = System.Windows.Forms;

namespace Catcretary;

/// <summary>
/// 트레이 아이콘 + 고양이 창 + 말풍선을 묶는다.
/// 흐름: 고양이 클릭 → 입력 → 파싱 → (쓰기면) 확인 → Plane 호출. 브리핑 시각 → 새로고침 → 말풍선.
/// </summary>
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon _tray = null!;
    private DispatcherTimer _timer = null!;
    private PetWindow _pet = null!;
    private BubbleWindow _bubble = null!;
    private CalendarWidgetWindow? _widget;
    private BoardWindow? _board;
    private Forms.ToolStripMenuItem _widgetMenu = null!;
    private Forms.ToolStripMenuItem _widgetTopMenu = null!;
    private Forms.ToolStripMenuItem _boardMenu = null!;
    private Forms.ToolStripMenuItem _boardTopMenu = null!;

    public AppConfig Config { get; private set; } = new();
    public BriefingResult? Latest { get; private set; }

    /// <summary>설정의 기본 프로젝트. 등록 대상(멤버·미보관·제외 목록 밖)에 없으면 null 이라 등록할 때 되묻는다.</summary>
    public PlaneProject? DefaultProject =>
        Config.DefaultProject.Length == 0 ? null
        : Latest?.Projects.FirstOrDefault(p => string.Equals(p.Identifier, Config.DefaultProject, StringComparison.OrdinalIgnoreCase));

    private AppState _state = new();
    private bool _loading;
    private bool _exiting;
    private DateTime _lastRefresh = DateTime.MinValue;
    /// <summary>앞 조회에서 본 댓글·변경 키. null 이면 첫 조회라 알리지 않는다.</summary>
    private HashSet<string>? _knownNoticeKeys;

    public static new App Current => (App)System.Windows.Application.Current;

    /// <summary>
    /// 기본 글꼴. Pretendard 가 설치돼 있지 않으면 WPF 가 목록의 다음 글꼴(맑은 고딕)로 그린다.
    /// XAML 스타일은 x:Static 으로 이 값을 쓰고, 스타일이 없는 창·툴팁은 아래 정적 생성자에서 기본값을 바꾼다.
    /// </summary>
    public static readonly System.Windows.Media.FontFamily UiFont = new("Pretendard, Malgun Gothic");

    static App()
    {
        foreach (var type in new[] { typeof(Window), typeof(System.Windows.Controls.ToolTip), typeof(System.Windows.Controls.ContextMenu) })
            System.Windows.Documents.TextElement.FontFamilyProperty.OverrideMetadata(type,
                new FrameworkPropertyMetadata(UiFont, FrameworkPropertyMetadataOptions.Inherits));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Init();
        DispatcherUnhandledException += (_, a) => AppLog.Error("처리되지 않은 UI 예외", a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            AppLog.Error("처리되지 않은 예외", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            AppLog.Error("관찰되지 않은 Task 예외", a.Exception);
            a.SetObserved();
        };

        Config = AppConfig.Load();
        _state = AppState.Load();

        SetupTray();
        SetupPet();
        if (Config.Widget.Visible) ShowWidget();
        if (Config.Board.Visible) ShowBoard();
        if (!Config.IsComplete) _widget?.SetStatus("Plane 연결 설정이 비어 있습니다");

        // --show: 시작하자마자 브리핑 말풍선. --say "문장": 시작하자마자 그 문장을 말한 것으로 처리.
        var showNow = e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        var sayIndex = Array.FindIndex(e.Args, a => a.Equals("--say", StringComparison.OrdinalIgnoreCase));
        var sayText = sayIndex >= 0 && sayIndex + 1 < e.Args.Length ? e.Args[sayIndex + 1] : null;

        if (!Config.IsComplete)
            ShowSettings();
        else if (sayText is not null)
            _ = HandleUtteranceAsync(sayText);
        else
            _ = RefreshAsync(showBriefing: showNow);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void SetupPet()
    {
        _pet = new PetWindow(Config);
        _bubble = new BubbleWindow(_pet);

        _pet.Clicked += () =>
        {
            if (_bubble.Mode == BubbleMode.Input) _bubble.HideBubble();
            else _bubble.ShowInput();
        };
        _pet.BriefingRequested += () => ShowBriefing();
        _pet.RefreshRequested += UserRefresh;
        _pet.SettingsRequested += ShowSettings;
        _pet.ExitRequested += ExitApp;

        _bubble.Submitted += text => _ = HandleUtteranceAsync(text);
        _bubble.Confirmed += draft => _ = CreateTodoAsync(draft);
        _bubble.CompleteRequested += item => _ = CompleteAsync(item);
        _bubble.OpenRequested += OpenInPlane;
        _bubble.Hidden += () => _pet.SetState(PetState.Idle);

        _pet.Show();
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("말 걸기", null, (_, _) => _bubble.ShowInput());
        menu.Items.Add("오늘 브리핑", null, (_, _) => ShowBriefing());
        menu.Items.Add("지금 새로고침", null, (_, _) => UserRefresh());
        _widgetMenu = new Forms.ToolStripMenuItem("캘린더 위젯", null, (_, _) => SetWidgetVisible(!Config.Widget.Visible))
        {
            Checked = Config.Widget.Visible,
        };
        menu.Items.Add(_widgetMenu);
        // 바탕화면 층에 있으면 창에 가려 위젯 우클릭 메뉴를 못 여니 트레이에서도 바꿀 수 있게 한다.
        _widgetTopMenu = new Forms.ToolStripMenuItem("위젯 항상 위에 표시", null, (_, _) => SetWidgetOnTop(!Config.Widget.AlwaysOnTop))
        {
            Checked = Config.Widget.AlwaysOnTop,
        };
        menu.Items.Add(_widgetTopMenu);
        _boardMenu = new Forms.ToolStripMenuItem("상태별 보드", null, (_, _) => SetBoardVisible(!Config.Board.Visible))
        {
            Checked = Config.Board.Visible,
        };
        menu.Items.Add(_boardMenu);
        _boardTopMenu = new Forms.ToolStripMenuItem("보드 항상 위에 표시", null, (_, _) => SetBoardOnTop(!Config.Board.AlwaysOnTop))
        {
            Checked = Config.Board.AlwaysOnTop,
        };
        menu.Items.Add(_boardTopMenu);
        menu.Opening += (_, _) =>
        {
            // 위젯·보드 우클릭 메뉴에서 바꾼 값 반영
            _widgetTopMenu.Checked = Config.Widget.AlwaysOnTop;
            _boardTopMenu.Checked = Config.Board.AlwaysOnTop;
            _boardMenu.Checked = Config.Board.Visible;
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("설정", null, (_, _) => ShowSettings());
        menu.Items.Add("로그 폴더 열기", null, (_, _) => OpenLogFolder());
        menu.Items.Add("종료", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(alert: false),
            Visible = true,
            Text = "Catcretary",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowBriefing();
    }

    // ── 캘린더 위젯 ──────────────────────────────────────────

    private void ShowWidget()
    {
        if (_widget is not null) return;

        _widget = new CalendarWidgetWindow();
        _widget.RefreshRequested += UserRefresh;
        _widget.OpenRequested += OpenInPlane;
        _widget.DayDoubleClicked += d => _bubble.ShowInput($"{d.Month}월 {d.Day}일 ");
        _widget.SettingsRequested += ShowSettings;
        _widget.HideRequested += () => SetWidgetVisible(false);
        _widget.BoardRequested += RequestBoard;
        _widget.DatesMoveRequested += (item, start, due) => _ = MoveDatesAsync(item, start, due);
        _widget.Closed += OnWidgetClosed;
        if (Latest is not null) _widget.SetData(Latest.AllItems, Latest.Briefing.Cycles, Latest.Projects, _lastRefresh);
        _widget.Show();
        UpdateBusy();
    }

    /// <summary>
    /// 숨기기가 아닌데 창이 닫혔으면 탐색기 재시작으로 소유자(Progman)와 함께 파괴된 것이다.
    /// 새 바탕화면 창이 뜰 시간을 두고 다시 만든다.
    /// </summary>
    private void OnWidgetClosed(object? sender, EventArgs e)
    {
        _widget = null;
        if (_exiting || !Config.Widget.Visible) return;

        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            if (!_exiting && Config.Widget.Visible) ShowWidget();
        };
        retry.Start();
    }

    private void SetWidgetVisible(bool visible)
    {
        Config.Widget.Visible = visible;
        Config.Save();
        _widgetMenu.Checked = visible;

        if (visible) ShowWidget();
        else _widget?.Close(); // Visible=false 라 OnWidgetClosed 가 다시 만들지 않는다.
    }

    private void SetWidgetOnTop(bool onTop)
    {
        Config.Widget.AlwaysOnTop = onTop;
        Config.Save();
        if (!Config.Widget.Visible) SetWidgetVisible(true);
        else _widget?.Reapply();
    }

    // ── 상태별 보드 ──────────────────────────────────────────

    private void ShowBoard()
    {
        if (_board is not null) return;

        _board = new BoardWindow();
        _board.RefreshRequested += UserRefresh;
        _board.OpenRequested += OpenInPlane;
        _board.StateMoveRequested += (item, state) => _ = MoveStateAsync(item, state);
        _board.CreateHandler = CreateFromBoardAsync;
        _board.UpdateHandler = UpdateFromBoardAsync;
        _board.OptionsLoader = LoadCreateOptionsAsync;
        _board.HideRequested += () => SetBoardVisible(false);
        _board.CalendarRequested += () => SetWidgetVisible(true);
        _board.Closed += OnBoardClosed;
        if (Latest is not null) _board.SetData(Latest, _lastRefresh);
        else if (Config.IsComplete) _ = RefreshAsync(showBriefing: false, background: true);
        _board.Show();
        UpdateBusy();
    }

    /// <summary>위젯과 같다. 숨기기가 아닌데 닫혔으면 탐색기 재시작으로 파괴된 것이라 잠시 뒤 다시 만든다.</summary>
    private void OnBoardClosed(object? sender, EventArgs e)
    {
        _board = null;
        if (_exiting || !Config.Board.Visible) return;

        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            if (!_exiting && Config.Board.Visible) ShowBoard();
        };
        retry.Start();
    }

    private void SetBoardVisible(bool visible)
    {
        Config.Board.Visible = visible;
        Config.Save();
        _boardMenu.Checked = visible;

        if (visible) ShowBoard();
        else
        {
            _board?.SaveBounds();
            _board?.Close(); // Visible=false 라 OnBoardClosed 가 다시 만들지 않는다.
        }
    }

    private void SetBoardOnTop(bool onTop)
    {
        Config.Board.AlwaysOnTop = onTop;
        Config.Save();
        if (!Config.Board.Visible) SetBoardVisible(true);
        else _board?.Reapply();
    }

    /// <summary>
    /// 보드 열 아래 빠른 추가. 그 열의 state 와 입력 칸에서 고른 우선순위·날짜·담당자·라벨로 만들고,
    /// 만든 항목을 새로고침을 기다리지 않고 바로 보드·위젯 목록에 넣는다.
    /// </summary>
    private async Task<bool> CreateFromBoardAsync(PlaneProject project, PlaneState state, NewItemDraft draft)
    {
        BeginWrite();
        try
        {
            using var client = new PlaneClient(Config);
            var created = await client.CreateIssueAsync(project.Id, draft.Name, draft.AssigneeIds,
                draft.Start?.ToString("yyyy-MM-dd"), draft.Due?.ToString("yyyy-MM-dd"), draft.Priority, draft.LabelIds, state.Id);
            var key = created is null ? project.Identifier : $"{project.Identifier}-{created.SequenceId}";
            if (created is not null) AddCreatedItem(created, project, state);

            _pet.SetState(PetState.Done);
            var mine = Latest is { } l && draft.AssigneeIds.Contains(l.MyUserId);
            var hidden = !mine && Config.Board.Scope == "mine" ? " (내 담당이 아니라 '내 담당' 보기에는 안 보임)" : "";
            _board?.SetStatus($"{key} 등록: {draft.Name} → {state.Name}{hidden}");
            EndWrite(project.Id, ok: true);
            return true;
        }
        catch (Exception ex)
        {
            EndWrite(project.Id, ok: false);
            AppLog.Error($"항목 등록 실패 (보드, {project.Identifier})", ex);
            _board?.SetStatus($"{project.Identifier} 등록 실패: {ex.Message}");
            _bubble.ShowMessage($"보드에서 등록 못 했다냥: {ex.Message}", TimeSpan.FromSeconds(10));
            return false;
        }
    }

    /// <summary>
    /// 보드 카드 수정. 입력 칸 값과 지금 값이 다른 필드만 PATCH 하고, 응답으로 받은 항목으로 보드·위젯 목록의 그 항목을 바꾼다.
    /// </summary>
    private async Task<bool> UpdateFromBoardAsync(BriefingItem item, NewItemDraft draft)
    {
        var changes = new Dictionary<string, object?>();
        if (draft.Name != item.Name) changes["name"] = draft.Name;
        if (draft.Priority != item.Priority) changes["priority"] = draft.Priority;
        if (draft.Start != item.StartDate) changes["start_date"] = draft.Start?.ToString("yyyy-MM-dd");
        if (draft.Due != item.Due) changes["target_date"] = draft.Due?.ToString("yyyy-MM-dd");
        if (!draft.AssigneeIds.ToHashSet().SetEquals(item.AssigneeIds)) changes["assignees"] = draft.AssigneeIds;
        if (!draft.LabelIds.ToHashSet().SetEquals(item.LabelIds)) changes["labels"] = draft.LabelIds;
        if (changes.Count == 0)
        {
            _board?.SetStatus($"{item.Key} 바뀐 내용 없음");
            return true;
        }

        BeginWrite();
        try
        {
            using var client = new PlaneClient(Config);
            var updated = await client.UpdateIssueAsync(item.ProjectId, item.IssueId, changes);
            if (updated is not null) ReplaceItem(item, updated);

            _pet.SetState(PetState.Done);
            _board?.SetStatus($"{item.Key} 수정 저장했습니다");
            EndWrite(item.ProjectId, ok: true);
            return true;
        }
        catch (Exception ex)
        {
            EndWrite(item.ProjectId, ok: false);
            AppLog.Error($"{item.Key} 수정 실패 (보드)", ex);
            _board?.SetStatus($"{item.Key} 수정 실패: {ex.Message}");
            _bubble.ShowMessage($"{item.Key} 을(를) 못 고쳤다냥: {ex.Message}", TimeSpan.FromSeconds(10));
            return false;
        }
    }

    /// <summary>고친 항목을 새로고침 전에 목록에서 바꿔 끼우고 위젯·보드를 다시 그린다.</summary>
    private void ReplaceItem(BriefingItem old, PlaneIssue updated)
    {
        if (Latest is not { } latest) return;
        var index = latest.AllItems.FindIndex(i => i.IssueId == old.IssueId);
        if (index < 0) return;
        var project = latest.Projects.FirstOrDefault(p => p.Id == old.ProjectId);
        if (project is null) return;
        var states = latest.States.GetValueOrDefault(project.Id) ?? new List<PlaneState>();
        var item = BriefingService.ToItem(updated, project, states.FirstOrDefault(s => s.Id == updated.State),
            BriefingService.DoneState(states)?.Id, latest.MemberNames, latest.MyUserId);
        item.NoticeText = old.NoticeText;
        item.IsDone = old.IsDone;
        latest.AllItems[index] = item;
        _widget?.SetData(latest.AllItems, latest.Briefing.Cycles, latest.Projects, _lastRefresh);
        _board?.Redraw();
    }

    /// <summary>빠른 추가에서 고를 멤버·라벨. 프로젝트마다 한 번 받아 두고, 사용자가 누른 새로고침에서 비운다.</summary>
    private readonly Dictionary<string, CreateOptions> _createOptions = new();

    private async Task<CreateOptions> LoadCreateOptionsAsync(PlaneProject project)
    {
        if (_createOptions.TryGetValue(project.Id, out var hit)) return hit;
        using var client = new PlaneClient(Config);
        var members = (await client.GetProjectMembersAsync(project.Id)).OrderBy(m => m.DisplayName, StringComparer.CurrentCulture).ToList();
        var labels = (await client.GetLabelsAsync(project.Id)).OrderBy(l => l.Name, StringComparer.CurrentCulture).ToList();
        return _createOptions[project.Id] = new CreateOptions(members, labels);
    }

    /// <summary>
    /// 방금 만든 항목을 새로고침 전에 목록에 넣고 위젯·보드를 다시 그린다.
    /// 위젯은 SetData 때 목록을 복사해 두므로 Redraw 가 아니라 SetData 로 다시 넘긴다.
    /// </summary>
    private void AddCreatedItem(PlaneIssue created, PlaneProject project, PlaneState? fallbackState = null)
    {
        if (Latest is not { } latest || latest.AllItems.Any(i => i.IssueId == created.Id)) return;
        var states = latest.States.GetValueOrDefault(project.Id) ?? new List<PlaneState>();
        latest.AllItems.Add(BriefingService.ToItem(created, project, states.FirstOrDefault(s => s.Id == created.State) ?? fallbackState,
            BriefingService.DoneState(states)?.Id, latest.MemberNames, latest.MyUserId));
        _widget?.SetData(latest.AllItems, latest.Briefing.Cycles, latest.Projects, _lastRefresh);
        _board?.Redraw();
    }

    /// <summary>캘린더 위젯 메뉴의 "상태별 보드 보이기". 숨겨져 있으면 켠다.</summary>
    private void RequestBoard()
    {
        if (!Config.Board.Visible) SetBoardVisible(true);
    }

    /// <summary>끌어 옮긴 항목을 위젯·보드에 바로 다시 그린다.</summary>
    private void RedrawViews()
    {
        _widget?.Redraw();
        _board?.Redraw();
    }

    /// <summary>
    /// 보드에서 카드를 다른 열에 놓았다. 화면부터 바꾸고 PATCH 한다. 실패하면 되돌리고 알린다.
    /// 성공하면 백그라운드 새로고침으로 브리핑 분류·진행률을 맞춘다.
    /// </summary>
    private async Task MoveStateAsync(BriefingItem item, PlaneState target)
    {
        var old = (item.StateId, item.StateName, item.StateGroup, item.StateColor);
        (item.StateId, item.StateName, item.StateGroup, item.StateColor) = (target.Id, target.Name, target.Group, target.Color);
        RedrawViews();
        BeginWrite();
        try
        {
            using var client = new PlaneClient(Config);
            await client.UpdateStateAsync(item.ProjectId, item.IssueId, target.Id);
            _pet.SetState(PetState.Done);
            _board?.SetStatus($"{item.Key} → {target.Name} 저장했습니다");
            EndWrite(item.ProjectId, ok: true);
        }
        catch (Exception ex)
        {
            EndWrite(item.ProjectId, ok: false);
            AppLog.Error($"{item.Key} 상태 변경 실패 (보드)", ex);
            (item.StateId, item.StateName, item.StateGroup, item.StateColor) = old;
            RedrawViews();
            _board?.SetStatus($"{item.Key} 상태 변경 실패: {ex.Message}");
            _bubble.ShowMessage($"{item.Key} 상태를 못 바꿨다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>캘린더에서 막대를 다른 날에 놓았다. 있는 날짜만 같은 날 수만큼 옮긴다. 실패하면 되돌린다.</summary>
    private async Task MoveDatesAsync(BriefingItem item, DateOnly? start, DateOnly? due)
    {
        var old = (item.StartDate, item.Due);
        (item.StartDate, item.Due) = (start, due);
        RedrawViews();
        BeginWrite();
        try
        {
            using var client = new PlaneClient(Config);
            await client.UpdateDatesAsync(item.ProjectId, item.IssueId, start, due);
            _pet.SetState(PetState.Done);
            var text = due is { } d ? $"마감 {old.Due:M'/'d} → {d:M'/'d}" : $"시작 {old.StartDate:M'/'d} → {start:M'/'d}";
            _bubble.ShowMessage($"{item.Key} {text} 옮겼다냥", TimeSpan.FromSeconds(4));
            EndWrite(item.ProjectId, ok: true);
        }
        catch (Exception ex)
        {
            EndWrite(item.ProjectId, ok: false);
            AppLog.Error($"{item.Key} 날짜 변경 실패 (캘린더)", ex);
            (item.StartDate, item.Due) = old;
            RedrawViews();
            _bubble.ShowMessage($"{item.Key} 날짜를 못 옮겼다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
    }

    // ── 보드·캘린더 쓰기 뒤 동기화 ───────────────────────────
    // 쓰기마다 전체 새로고침(호출 30~40회)을 돌리면 연달아 고칠 때 분당 60회 한도에 걸린다.
    // 그래서 쓴 프로젝트의 항목만 잠시 뒤 한 번 다시 받고(호출 1~2회), 브리핑 분류는 쓰기가 멈춘 뒤 한 번 맞춘다.

    /// <summary>
    /// 쓰기가 시작·끝날 때마다 올린다. 조회를 시작할 때와 끝날 때 값이 다르면 그 사이 쓰기가 있었으므로,
    /// 받은 목록이 쓰기 전 값일 수 있어 화면에 덮지 않는다.
    /// </summary>
    private int _writeVersion;
    private readonly HashSet<string> _dirtyProjects = new();
    private bool _syncing;
    private DispatcherTimer? _syncTimer;
    private DispatcherTimer? _fullRefreshTimer;
    private static readonly TimeSpan SyncDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan FullRefreshDelay = TimeSpan.FromSeconds(30);

    private void BeginWrite() => _writeVersion++;

    /// <summary>실패해 되돌린 쓰기는 다시 받을 것이 없다. 버전만 올려 그동안 시작한 조회 결과를 버리게 한다.</summary>
    private void EndWrite(string projectId, bool ok)
    {
        _writeVersion++;
        if (!ok) return;
        _dirtyProjects.Add(projectId);
        Restart(ref _syncTimer, SyncDelay, () => _ = SyncDirtyProjectsAsync());
        ScheduleFullRefresh();
    }

    /// <summary>마지막 쓰기에서 FullRefreshDelay 뒤 한 번. 연달아 쓰는 동안은 계속 미룬다.</summary>
    private void ScheduleFullRefresh() =>
        Restart(ref _fullRefreshTimer, FullRefreshDelay, () => _ = RefreshAsync(showBriefing: false, background: true));

    private void Restart(ref DispatcherTimer? timer, TimeSpan delay, Action action)
    {
        if (timer is null)
        {
            var t = new DispatcherTimer { Interval = delay };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!_exiting) action();
            };
            timer = t;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>전체 새로고침이나 프로젝트 다시 받기가 도는 동안 위젯·보드의 새로고침 아이콘을 돌린다.</summary>
    private void UpdateBusy()
    {
        var busy = _loading || _syncing;
        _widget?.SetBusy(busy);
        _board?.SetBusy(busy);
    }

    /// <summary>쓴 프로젝트의 항목 목록만 다시 받아 바꿔 끼운다. 받는 사이 다른 쓰기가 있었으면 그 결과는 버리고 다시 예약한다.</summary>
    private async Task SyncDirtyProjectsAsync()
    {
        if (_syncing) return; // 도는 중이면 그 반복이 새로 들어온 프로젝트까지 처리한다.
        _syncing = true;
        UpdateBusy();
        try
        {
            while (_dirtyProjects.Count > 0 && !_exiting)
            {
                var projectId = _dirtyProjects.First();
                _dirtyProjects.Remove(projectId);
                var project = Latest?.Projects.FirstOrDefault(p => p.Id == projectId);
                if (project is null) continue;

                var version = _writeVersion;
                List<PlaneIssue> issues;
                try
                {
                    using var client = new PlaneClient(Config);
                    issues = await client.GetIssuesAsync(projectId);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"{project.Identifier} 항목 다시 받기 실패 (보드 쓰기 뒤)", ex);
                    continue; // 예약해 둔 전체 새로고침이 맞춘다.
                }

                if (version != _writeVersion)
                {
                    _dirtyProjects.Add(projectId);
                    Restart(ref _syncTimer, SyncDelay, () => _ = SyncDirtyProjectsAsync());
                    break;
                }
                if (Latest is { } latest) MergeProjectItems(latest, project, issues);
            }
        }
        finally
        {
            _syncing = false;
            UpdateBusy();
        }
    }

    /// <summary>
    /// 한 프로젝트의 항목을 새로 받은 목록으로 바꾼다. 브리핑 섹션은 손대지 않는다(전체 새로고침이 맞춘다).
    /// 댓글 알림과 완료 체크처럼 목록 조회로는 알 수 없는 값은 앞 항목에서 옮긴다.
    /// </summary>
    private void MergeProjectItems(BriefingResult latest, PlaneProject project, List<PlaneIssue> issues)
    {
        var states = latest.States.GetValueOrDefault(project.Id) ?? new List<PlaneState>();
        var doneStateId = BriefingService.DoneState(states)?.Id;
        var old = latest.AllItems.Where(i => i.ProjectId == project.Id).ToDictionary(i => i.IssueId);
        var fresh = issues.Where(i => !i.IsDraft).Select(issue =>
        {
            var item = BriefingService.ToItem(issue, project, states.FirstOrDefault(s => s.Id == issue.State),
                doneStateId, latest.MemberNames, latest.MyUserId);
            if (old.TryGetValue(issue.Id, out var prev))
            {
                item.NoticeText = prev.NoticeText;
                item.IsDone = prev.IsDone;
            }
            return item;
        }).ToList();

        var index = latest.AllItems.FindIndex(i => i.ProjectId == project.Id);
        latest.AllItems.RemoveAll(i => i.ProjectId == project.Id);
        latest.AllItems.InsertRange(index < 0 ? latest.AllItems.Count : Math.Min(index, latest.AllItems.Count), fresh);
        _widget?.SetData(latest.AllItems, latest.Briefing.Cycles, latest.Projects, _lastRefresh);
        _board?.Redraw();
    }

    private static IEnumerable<BriefingItem> AllMine(BriefingResult result) =>
        result.Briefing.Sections.SelectMany(s => s.Items);

    // ── 브리핑 스케줄 ─────────────────────────────────────────

    /// <summary>브리핑 시각이 지났고 오늘 아직 안 띄웠으면 띄운다. PC 를 늦게 켠 날도 그날 첫 판정에서 뜬다.</summary>
    private void Tick()
    {
        if (!Config.IsComplete) return;

        var now = DateTime.Now;

        // 위젯을 최신으로 유지하는 주기 새로고침. 최소 5분.
        var interval = TimeSpan.FromMinutes(Math.Max(5, Config.RefreshIntervalMinutes));
        if (!_loading && now - _lastRefresh >= interval)
            _ = RefreshAsync(showBriefing: false, background: true);

        // 키에 시각을 넣어서, 설정에서 시각을 바꾸면 그날 다시 뜬다.
        var key = $"{now:yyyy-MM-dd}@{Config.BriefingTime}";

        if (_state.LastBriefingKey == key) return;
        if (Config.WeekdaysOnly && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        if (!TimeSpan.TryParseExact(Config.BriefingTime, @"h\:mm", CultureInfo.InvariantCulture, out var at)) return;
        if (now.TimeOfDay < at) return;
        if (_loading) return; // 조회 중이면 표시를 건너뛰지 말고 다음 판정에서 다시 시도한다.

        _state.LastBriefingKey = key;
        _state.RotateForAnnounce(DateTime.UtcNow);
        _state.Save();
        _ = RefreshAsync(showBriefing: false, announce: true);
    }

    /// <summary>사용자가 누른 새로고침. state 캐시도 비워 Plane 에서 바꾼 state 를 바로 반영한다.</summary>
    private void UserRefresh()
    {
        PlaneClient.ClearCache();
        _createOptions.Clear();
        _ = RefreshAsync(showBriefing: false);
    }

    /// <summary>조회 중에 들어온 새로고침 요청. 끝나면 한 번 더 조회한다.</summary>
    private bool _refreshQueued;

    /// <param name="background">주기 새로고침. 고양이 상태를 건드리지 않고, 실패해도 말풍선 대신 위젯 상태 줄에만 남긴다.</param>
    /// <remarks>
    /// 조회 중에 다시 부르면 버리지 않고 끝난 뒤 한 번 더 조회한다. 등록·수정 직후의 새로고침이
    /// 주기 새로고침과 겹쳐 빠지면, 쓰기 전에 시작한 조회 결과가 화면을 덮은 채 다음 주기까지 남기 때문이다.
    /// </remarks>
    public async Task RefreshAsync(bool showBriefing, bool announce = false, bool background = false)
    {
        if (!Config.IsComplete) return;
        if (_loading)
        {
            _refreshQueued = true;
            return;
        }
        _loading = true;
        UpdateBusy();
        if (!background) _pet.SetState(PetState.Thinking);

        var version = _writeVersion;
        try
        {
            var options = new BriefingOptions(_state.BaselineIds?.ToHashSet(), _state.NoticeSinceUtc(DateTime.UtcNow));
            var result = await new BriefingService(Config).BuildAsync(options);
            if (version != _writeVersion)
            {
                // 조회 중에 보드·캘린더에서 쓴 것이 있어, 이 결과에는 쓰기 전 값이 섞였을 수 있다.
                // 주기 새로고침이면 버리고 쓰기가 멈춘 뒤 다시 받는다. 사용자가 요청한 조회면 보여 주되 다시 맞춘다.
                ScheduleFullRefresh();
                if (background && !announce)
                {
                    AppLog.Info("새로고침 중 쓰기가 있어 이번 결과는 버리고 다시 받는다");
                    return;
                }
            }
            Latest = result;

            if (announce)
            {
                _state.AnnouncedIds = result.MineIds.ToList();
                _state.Save();
            }
            var freshNotices = _knownNoticeKeys is null ? 0 : result.NoticeKeys.Count(k => !_knownNoticeKeys.Contains(k));
            _knownNoticeKeys = result.NoticeKeys;

            var overdue = result.Briefing.MineOverdueTotal > 0;
            _tray.Icon = TrayIconFactory.Create(alert: overdue);
            _tray.Text = Truncate($"Catcretary — {result.Briefing.Headline}", 63);
            _pet.SetAlert(overdue);
            if (!background) _pet.SetState(overdue ? PetState.Alert : PetState.Idle);
            _widget?.SetData(result.AllItems, result.Briefing.Cycles, result.Projects, DateTime.Now);
            _board?.SetData(result, DateTime.Now);

            if (_bubble.Mode == BubbleMode.Briefing) _bubble.ShowBriefing(result);

            if (announce)
            {
                _pet.Wave();
                _bubble.ShowMessage(result.Briefing.HeadlineCatWithExtra, TimeSpan.FromSeconds(30), () => ShowBriefing());
            }
            else if (showBriefing)
            {
                ShowBriefing();
            }
            else if (freshNotices > 0 && _bubble.Mode is BubbleMode.Hidden or BubbleMode.Message)
            {
                // 입력·확인 중에는 끼어들지 않는다. 다음 브리핑 목록에 남아 있다.
                _bubble.ShowMessage($"내 담당 항목에 새 소식 {freshNotices}건 있다냥", TimeSpan.FromSeconds(20), () => ShowBriefing());
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(background ? "주기 새로고침 실패" : "새로고침 실패", ex);
            _widget?.SetStatus($"연결 실패 {DateTime.Now:HH:mm} · {ex.Message}");
            _board?.SetStatus($"연결 실패 {DateTime.Now:HH:mm} · {ex.Message}");
            if (!background)
            {
                _pet.SetState(PetState.Idle);
                _bubble.ShowMessage($"Plane 이 대답이 없다냥: {ex.Message}", TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            _loading = false;
            UpdateBusy();
            _lastRefresh = DateTime.Now; // 실패해도 갱신해서, 서버가 죽어 있을 때 30초마다 두드리지 않게 한다.
            if (_refreshQueued && !_exiting)
            {
                _refreshQueued = false;
                _ = RefreshAsync(showBriefing: false, background: true);
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void ShowBriefing()
    {
        if (Latest is null)
        {
            _ = RefreshAsync(showBriefing: true);
            return;
        }
        _bubble.ShowBriefing(Latest);
    }

    // ── 말 걸기 ──────────────────────────────────────────────

    private async Task HandleUtteranceAsync(string text)
    {
        if (Latest is null)
        {
            _pet.SetState(PetState.Thinking);
            await RefreshAsync(showBriefing: false);
            if (Latest is null) return;
        }

        var parser = new TodoParser(Latest.Projects, Latest.AllProjects, Config.ProjectAliases, DateOnly.FromDateTime(DateTime.Today),
            DefaultProject);
        var draft = parser.Parse(text);

        switch (draft.Intent)
        {
            case TodoIntent.ListToday:
                _bubble.ShowBriefing(Latest);
                return;
            case TodoIntent.ChangeState or TodoIntent.Comment or TodoIntent.OpenItem:
                await HandleItemCommandAsync(draft);
                return;
            case TodoIntent.Search:
                await SearchAsync(draft.Query!);
                return;
            case TodoIntent.CreateProject:
                ShowCreateProject(draft);
                return;
        }

        _pet.SetState(PetState.Listening);
        _bubble.ShowConfirm(draft, Latest.Projects);
    }

    // ── 할 일 등록 ───────────────────────────────────────────

    private async Task CreateTodoAsync(TodoDraft draft)
    {
        if (draft.Project is null || draft.Title.Length == 0) return;

        _bubble.ShowMessage("등록하는 중이다냥…", TimeSpan.FromSeconds(30));
        _pet.SetState(PetState.Thinking);
        try
        {
            using var client = new PlaneClient(Config);
            var projectId = draft.Project.Id;

            // 라벨은 이름으로 찾는다. 없는 라벨은 만들지 않고 빼고 알려준다.
            var labelIds = new List<string>();
            var missingLabels = new List<string>();
            if (draft.Labels.Count > 0)
            {
                var labels = await client.GetLabelsAsync(projectId);
                foreach (var name in draft.Labels)
                {
                    var hit = labels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) missingLabels.Add(name);
                    else labelIds.Add(hit.Id);
                }
            }

            var created = await client.CreateIssueAsync(
                projectId, draft.Title, Latest is { } latest ? new[] { latest.MyUserId } : Array.Empty<string>(),
                draft.StartDate?.ToString("yyyy-MM-dd"), draft.TargetDate?.ToString("yyyy-MM-dd"), draft.Priority, labelIds);

            var key = created is null ? draft.Project.Identifier : $"{draft.Project.Identifier}-{created.SequenceId}";
            if (created is not null) AddCreatedItem(created, draft.Project);
            var notes = new List<string>();
            if (missingLabels.Count > 0) notes.Add($"없는 라벨 {string.Join(" ", missingLabels.Select(l => "#" + l))} 은 뺐다냥");
            if (draft.AddToCycle && created is not null) notes.Add(await AddToCurrentCycleAsync(client, projectId, created.Id));

            _pet.SetState(PetState.Done);
            _bubble.ShowMessage($"{key} 등록했다냥: {draft.Title}" + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : ""),
                TimeSpan.FromSeconds(notes.Count > 0 ? 10 : 6));

            _ = RefreshAsync(showBriefing: false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"항목 등록 실패 ({draft.Project.Identifier})", ex);
            _pet.SetState(PetState.Idle);
            _bubble.ShowMessage($"등록 못 했다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>등록은 이미 끝났으므로 여기서 실패해도 등록 실패로 알리지 않고 결과 문구만 돌려준다.</summary>
    private static async Task<string> AddToCurrentCycleAsync(PlaneClient client, string projectId, string issueId)
    {
        try
        {
            var cycle = (await client.GetCurrentCyclesAsync(projectId)).FirstOrDefault();
            if (cycle is null) return "진행 중인 cycle 이 없어서 cycle 에는 못 넣었다냥";
            await client.AddToCycleAsync(projectId, cycle.Id, issueId);
            return $"cycle {cycle.Name} 에 넣었다냥";
        }
        catch (Exception ex)
        {
            AppLog.Error("cycle 에 넣기 실패", ex);
            return $"cycle 에는 못 넣었다냥: {ex.Message}";
        }
    }

    // ── 항목 명령: 상태 변경 / 댓글 / 열기 ───────────────────

    private async Task HandleItemCommandAsync(TodoDraft draft)
    {
        var key = draft.IssueKey;
        _pet.SetState(PetState.Thinking);
        try
        {
            using var client = new PlaneClient(Config);
            var issue = await client.GetIssueByKeyAsync(draft.KeyProject!.Identifier, draft.SequenceId);

            if (draft.Intent == TodoIntent.OpenItem)
            {
                _pet.SetState(PetState.Idle);
                OpenIssuePage(issue.Project, issue.Id, key);
                return;
            }

            var states = await client.GetStatesAsync(issue.Project);
            var current = states.FirstOrDefault(s => s.Id == issue.State);
            var rows = new List<(string, string?)>
            {
                ("항목", $"{key} {issue.Name}"),
                ("현재 상태", current?.Name ?? "알 수 없음"),
            };
            _pet.SetState(PetState.Listening);

            if (draft.Intent == TodoIntent.Comment)
            {
                var body = draft.CommentText ?? "";
                rows.Add(("댓글", body));
                _bubble.ShowAction($"{key} 에 댓글을 남길까냥?", rows, "남기기", body.Length > 0,
                    () => _ = RunWriteAsync($"{key} 댓글", c => c.AddCommentAsync(issue.Project, issue.Id, body), $"{key} 에 댓글 남겼다냥"));
                return;
            }

            var target = BriefingService.FirstInGroup(states, draft.TargetGroup!);
            if (target is null)
            {
                _pet.SetState(PetState.Idle);
                _bubble.ShowMessage($"{key} 프로젝트에는 '{draft.TargetGroupLabel}' 상태가 없다냥", TimeSpan.FromSeconds(8));
                return;
            }
            if (current?.Group == target.Group)
            {
                _pet.SetState(PetState.Idle);
                _bubble.ShowMessage($"{key} 는 이미 {current.Name} 상태다냥", TimeSpan.FromSeconds(6));
                return;
            }

            rows.Add(("바꿀 상태", target.Name));
            _bubble.ShowAction($"{key} 상태를 바꿀까냥?", rows, "바꾸기", true,
                () => _ = RunWriteAsync($"{key} 상태 변경", c => c.UpdateStateAsync(issue.Project, issue.Id, target.Id), $"{key} 를 {target.Name} 로 바꿨다냥"));
        }
        catch (PlaneApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _pet.SetState(PetState.Idle);
            _bubble.ShowMessage($"{key} 를 못 찾았다냥", TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            AppLog.Error($"{key} 조회 실패", ex);
            _pet.SetState(PetState.Idle);
            _bubble.ShowMessage($"{key} 를 못 불러왔다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>확인 말풍선에서 누른 쓰기 하나. 끝나면 브리핑을 새로 받는다.</summary>
    private async Task RunWriteAsync(string what, Func<PlaneClient, Task> write, string doneMessage)
    {
        _bubble.ShowMessage("처리하는 중이다냥…", TimeSpan.FromSeconds(30));
        _pet.SetState(PetState.Thinking);
        try
        {
            using var client = new PlaneClient(Config);
            await write(client);
            _pet.SetState(PetState.Done);
            _bubble.ShowMessage(doneMessage, TimeSpan.FromSeconds(6));
            _ = RefreshAsync(showBriefing: false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{what} 실패", ex);
            _pet.SetState(PetState.Idle);
            _bubble.ShowMessage($"{what} 못 했다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
    }

    // ── 검색 ─────────────────────────────────────────────────

    /// <summary>검색 결과 최대 건수. 내 담당이 아닌 결과는 한 건마다 다시 조회하므로 작게 둔다.</summary>
    private const int SearchLimit = 8;

    /// <summary>
    /// work-items/search 는 이름·번호만 준다. 내 담당이면 브리핑 항목을 그대로 쓰고,
    /// 아니면 식별자로 다시 조회해 상태·마감을 채운다.
    /// </summary>
    private async Task SearchAsync(string query)
    {
        _pet.SetState(PetState.Thinking);
        try
        {
            using var client = new PlaneClient(Config);
            var hits = await client.SearchAsync(query, SearchLimit);
            var mine = AllMine(Latest!).ToDictionary(i => i.IssueId);
            var projects = Latest!.AllProjects.ToDictionary(p => p.Id);

            var section = new BriefingSection { Kind = SectionKind.Other, Title = "검색 결과", Accent = "#2563EB" };
            foreach (var hit in hits)
            {
                if (mine.TryGetValue(hit.Id, out var item)) { section.Items.Add(item); continue; }
                if (!projects.TryGetValue(hit.ProjectId, out var project)) continue;

                var issue = await client.GetIssueByKeyAsync(hit.ProjectIdentifier, hit.SequenceId);
                var states = await client.GetStatesAsync(project.Id);
                section.Items.Add(BriefingService.ToItem(issue, project, states.FirstOrDefault(s => s.Id == issue.State),
                    BriefingService.DoneState(states)?.Id));
            }

            _pet.SetState(PetState.Listening);
            var sections = section.Items.Count > 0 ? new List<BriefingSection> { section } : new List<BriefingSection>();
            _bubble.ShowList($"검색 \"{query}\" {section.Items.Count}건", "", sections, "못 찾았다냥.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"검색 실패 ({query})", ex);
            _pet.SetState(PetState.Idle);
            _bubble.ShowMessage($"검색 못 했다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
    }

    // ── 프로젝트 생성 ────────────────────────────────────────

    private void ShowCreateProject(TodoDraft draft)
    {
        _pet.SetState(PetState.Listening);
        var rows = new List<(string, string?)>
        {
            ("이름", draft.NewProjectName.Length > 0 ? draft.NewProjectName : "—"),
            ("식별자", draft.NewProjectIdentifier.Length > 0 ? draft.NewProjectIdentifier : "—"),
            ("템플릿", draft.Template is { } t ? $"{t.Name} — 상태·라벨·멤버 복사" : null),
            ("별칭", draft.Aliases.Count > 0 ? string.Join(", ", draft.Aliases) : null),
        };
        var ok = draft.Clarification is null;
        _bubble.ShowAction(draft.Clarification ?? "이 프로젝트를 만들까냥?", rows, "만들기", ok, () => _ = CreateProjectAsync(draft));
    }

    /// <summary>
    /// 프로젝트를 만들고, 템플릿이 있으면 state·라벨·멤버를 복사한다. 같은 이름의 state·라벨과 이미 멤버인 사람은 건너뛴다.
    /// 서버가 만든 기본 state 는 지우지 않는다. 복사 중 일부가 실패해도 프로젝트 생성은 성공으로 보고 실패 수만 알린다.
    /// </summary>
    private async Task CreateProjectAsync(TodoDraft draft)
    {
        _bubble.ShowMessage("프로젝트를 만드는 중이다냥…", TimeSpan.FromSeconds(60));
        _pet.SetState(PetState.Thinking);
        try
        {
            using var client = new PlaneClient(Config);
            var project = await client.CreateProjectAsync(draft.NewProjectName, draft.NewProjectIdentifier);

            var detail = "";
            if (draft.Template is { } template)
            {
                var (states, labels, members, failed) = await CopyTemplateAsync(client, template.Id, project.Id);
                detail = $"\n{template.Identifier} 에서 상태 {states}·라벨 {labels}·멤버 {members} 복사"
                         + (failed > 0 ? $", 실패 {failed}건 (로그 참고)" : "");
            }

            if (draft.Aliases.Count > 0)
            {
                Config.ProjectAliases[project.Identifier] = draft.Aliases.ToList();
                Config.Save();
            }

            _pet.SetState(PetState.Done);
            _bubble.ShowMessage($"{project.Identifier} 프로젝트를 만들었다냥: {project.Name}{detail}", TimeSpan.FromSeconds(10));
            _ = RefreshAsync(showBriefing: false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"프로젝트 생성 실패 ({draft.NewProjectIdentifier})", ex);
            _pet.SetState(PetState.Idle);
            _bubble.ShowMessage($"프로젝트를 못 만들었다냥: {ex.Message}", TimeSpan.FromSeconds(12));
        }
    }

    private static async Task<(int States, int Labels, int Members, int Failed)> CopyTemplateAsync(PlaneClient client, string fromId, string toId)
    {
        int states = 0, labels = 0, members = 0, failed = 0;

        async Task Try(string what, Func<Task> action, Action onSuccess)
        {
            try { await action(); onSuccess(); }
            catch (Exception ex) { failed++; AppLog.Error($"템플릿 복사 실패: {what}", ex); }
        }

        var existingStates = (await client.GetStatesAsync(toId)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in await client.GetStatesAsync(fromId))
            if (!existingStates.Contains(s.Name))
                await Try($"state {s.Name}", () => client.CreateStateAsync(toId, s.Name, s.Color, s.Group), () => states++);

        // 라벨 계층(parent)은 옮기지 않고 평면으로 만든다.
        var existingLabels = (await client.GetLabelsAsync(toId)).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in await client.GetLabelsAsync(fromId))
            if (!existingLabels.Contains(l.Name))
                await Try($"label {l.Name}", () => client.CreateLabelAsync(toId, l.Name, l.Color), () => labels++);

        // project-members 응답에는 role 이 없어서 모두 Member(15)로 넣는다.
        var existingMembers = (await client.GetProjectMembersAsync(toId)).Select(m => m.Id).ToHashSet();
        foreach (var m in await client.GetProjectMembersAsync(fromId))
            if (!existingMembers.Contains(m.Id))
                await Try($"member {m.DisplayName}", () => client.AddProjectMemberAsync(toId, m.Id, 15), () => members++);

        return (states, labels, members, failed);
    }

    // ── 완료 처리·열기 ───────────────────────────────────────

    private async Task CompleteAsync(BriefingItem item)
    {
        if (item.DoneStateId is null) return;
        item.IsBusy = true;
        try
        {
            using var client = new PlaneClient(Config);
            await client.UpdateStateAsync(item.ProjectId, item.IssueId, item.DoneStateId);
            item.IsDone = true;
            // 보드에서도 완료 열로 옮겨 보이게 state 를 맞춘다.
            if (Latest?.States.GetValueOrDefault(item.ProjectId)?.FirstOrDefault(s => s.Id == item.DoneStateId) is { } done)
                (item.StateId, item.StateName, item.StateGroup, item.StateColor) = (done.Id, done.Name, done.Group, done.Color);
            RedrawViews();
            _pet.SetState(PetState.Done);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{item.Key} 완료 처리 실패", ex);
            _bubble.ShowMessage($"{item.Key} 완료 처리 못 했다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private void OpenInPlane(BriefingItem item) => OpenIssuePage(item.ProjectId, item.IssueId, item.Key);

    /// <summary>기본 브라우저로 항목의 Plane 페이지를 연다. 말풍선은 포커스를 잃어 접힌다.</summary>
    private void OpenIssuePage(string projectId, string issueId, string key)
    {
        var url = $"{Config.BaseUrl.TrimEnd('/')}/{Config.WorkspaceSlug}/projects/{projectId}/issues/{issueId}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"{key} 페이지 열기 실패", ex);
            _bubble.ShowMessage($"{key} 페이지를 못 열었다냥: {ex.Message}", TimeSpan.FromSeconds(10));
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLog.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppLog.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("로그 폴더 열기 실패", ex);
        }
    }

    // ── 설정 ─────────────────────────────────────────────────

    public void ShowSettings()
    {
        var dialog = new SettingsWindow(Config);
        if (dialog.ShowDialog() == true)
        {
            // 설정 창이 모르는 필드(별칭·위치)는 기존 값을 유지한다.
            dialog.Result.ProjectAliases = Config.ProjectAliases;
            dialog.Result.PetLeft = Config.PetLeft;
            dialog.Result.PetTop = Config.PetTop;
            dialog.Result.PetScale = Config.PetScale;
            dialog.Result.Widget = Config.Widget;
            dialog.Result.Board = Config.Board;
            Config = dialog.Result;
            Config.Save();
            _ = RefreshAsync(showBriefing: false);
        }
    }

    /// <summary>창이 닫히기 전에 위젯 위치를 저장하고, 닫힌 위젯을 다시 만들지 않게 표시한 뒤 종료한다.</summary>
    private void ExitApp()
    {
        _exiting = true;
        _widget?.SaveBounds();
        _board?.SaveBounds();
        Shutdown();
    }

    /// <summary>Windows 로그오프·종료. 이 경로는 ExitApp 을 거치지 않는다.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        _widget?.SaveBounds();
        _board?.SaveBounds();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _tray.Visible = false;
        _tray.Dispose();
        base.OnExit(e);
    }
}
