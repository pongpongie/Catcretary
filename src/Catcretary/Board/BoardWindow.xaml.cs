using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Catcretary.Models;
using Catcretary.Services;
using Catcretary.Widget;
using Microsoft.Win32;

namespace Catcretary.Board;

/// <summary>
/// 상태별 보드(Kanban). 프로젝트 하나를 고르면 열 = 그 프로젝트의 state(sequence 순),
/// 모든 프로젝트면 열 = state 그룹 5개. 카드를 다른 열로 끌어 놓으면 상태를 바꾼다.
/// 그룹 열에 놓으면 그 항목 프로젝트의 해당 그룹 첫 state 로 보낸다.
/// 데이터는 브리핑 새로고침이 받아 온 것을 그대로 쓴다(추가 호출 없음).
/// 캘린더 위젯처럼 테두리 없는 바탕화면 위젯이다. 고정·테마·투명도·항상 위는 우클릭 메뉴에서 바꾸고
/// config.json 의 board 에 저장한다.
/// </summary>
public partial class BoardWindow : Window
{
    private sealed record ColumnDef(string Title, string Color, string? StateId, string Group);

    private static readonly ColumnDef[] GroupColumns =
    {
        new("백로그", "#9CA3AF", null, "backlog"),
        new("할 일", "#6B7280", null, "unstarted"),
        new("진행 중", "#F59E0B", null, "started"),
        new("완료", "#16A34A", null, "completed"),
        new("취소", "#9AA4BC", null, "cancelled"),
    };

    private const double ColumnMinWidth = 180;
    private const double ColumnGap = 8;

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private BriefingResult? _data;
    private DateTime? _updatedAt;
    private string _status = "";
    private bool _ready;
    private bool _placed;
    private readonly List<(Border View, ColumnDef Def)> _columns = new();

    // 카드 끌기 상태
    private BriefingItem? _dragItem;
    private Point _dragOrigin;
    private bool _dragging;
    private (Border View, ColumnDef Def)? _dropTarget;

    // 빠른 추가 상태. Render 가 열을 다시 만들어도 입력 중이던 열·글자·프로젝트가 남도록 필드에 둔다.
    // 카드 수정도 같은 입력 칸과 같은 값 필드를 쓴다. 빠른 추가와 동시에 열리지 않는다.
    private string? _addColumn;        // 입력 칸을 연 열 키 (state id 또는 그룹)
    private string? _editItemId;       // 수정 중인 카드의 항목 id. 새로고침으로 항목 객체가 바뀌어도 id 로 다시 찾는다.
    private string _addDraft = "";
    private string? _addProjectId;     // 모든 프로젝트 보기에서 고른 프로젝트
    private string _addPriority = "none";
    private DateOnly? _addStart;
    private DateOnly? _addDue;
    private List<string>? _addAssignees; // null 이면 범위를 따른다(내 담당이면 나에게 배정)
    private readonly List<string> _addLabels = new();
    private string? _addPanel;         // 입력 칸 안에 펼친 선택 패널: priority / date / assignee / label
    private bool _addBusy;
    private bool _addFocusPending;
    private TextBox? _addTitleBox;
    // 입력 칸에 키보드 포커스가 있는 동안 새로고침 결과로 다시 그리면 입력 칸이 새로 만들어져 포커스와 IME 조합이 끊긴다.
    // 그동안은 다시 그리기를 미뤘다가 포커스가 입력 칸을 떠나면 그린다.
    private Border? _editorView;
    private bool _renderPending;
    // 패널 안에서 고르는 동안은 다시 그리지 않고 칩 글자만 바꾼다(체크 목록 스크롤이 처음으로 돌아가지 않게).
    private readonly Dictionary<string, TextBlock> _chipTexts = new();

    public event Action? RefreshRequested;
    public event Action<BriefingItem>? OpenRequested;
    /// <summary>카드를 다른 열에 놓았다. 보낼 state 는 이미 그 항목 프로젝트의 것으로 정해져 있다.</summary>
    public event Action<BriefingItem, PlaneState>? StateMoveRequested;
    public event Action? CalendarRequested;

    /// <summary>
    /// 열 아래 빠른 추가에서 Enter. (프로젝트, 넣을 state, 입력값) → 성공 여부.
    /// 성공하면 입력 칸을 비우고 열어 둔 채로 다음 항목을 받는다(Plane 의 빠른 추가와 같다).
    /// </summary>
    public Func<PlaneProject, PlaneState, NewItemDraft, Task<bool>>? CreateHandler { get; set; }

    /// <summary>카드를 눌러 연 수정 칸에서 Enter. (항목, 고친 값) → 성공 여부. 성공하면 수정 칸을 닫는다.</summary>
    public Func<BriefingItem, NewItemDraft, Task<bool>>? UpdateHandler { get; set; }

    /// <summary>담당자·라벨 패널을 처음 펼칠 때 그 프로젝트의 멤버·라벨을 받는다.</summary>
    public Func<PlaneProject, Task<CreateOptions>>? OptionsLoader { get; set; }
    private readonly Dictionary<string, CreateOptions> _options = new();
    private string? _optionsLoading;
    private string? _optionsError;
    public event Action? HideRequested;

    /// <summary>설정 창에서 저장하면 App.Config 객체가 바뀌므로 참조를 들고 있지 않고 매번 읽는다.</summary>
    private static BoardConfig B => App.Current.Config.Board;

    public BoardWindow()
    {
        InitializeComponent();

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveBounds(); };
        SourceInitialized += (_, _) => DesktopPin.Attach(this, keepBottom: () => !B.AlwaysOnTop);
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _saveTimer.Stop();
        };

        Place();
        ApplySettings();
        SyncToolbar();
    }

    // ── 외부에서 부르는 것 ───────────────────────────────────

    public void SetData(BriefingResult data, DateTime at)
    {
        _data = data;
        _updatedAt = at;
        FillProjects();
        RenderSoon();
    }

    /// <summary>끌어 옮긴 카드를 응답 전에 먼저 그리거나, 실패해 되돌린 뒤 다시 그린다.</summary>
    public void Redraw() => RenderSoon();

    /// <summary>창 밖에서 바뀐 데이터로 다시 그린다. 입력 칸에 입력 중이면 포커스가 떠날 때까지 미룬다.</summary>
    private void RenderSoon()
    {
        if (!_addBusy && _editorView is { IsKeyboardFocusWithin: true })
        {
            _renderPending = true;
            return;
        }
        Render();
    }

    /// <summary>트레이 메뉴처럼 창 밖에서 설정을 바꿨을 때 다시 적용한다.</summary>
    public void Reapply()
    {
        ApplySettings();
        SyncToolbar();
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
        var b = B;
        b.Left = Math.Round(Left);
        b.Top = Math.Round(Top);
        b.Width = Math.Round(ActualWidth > 0 ? ActualWidth : Width);
        b.Height = Math.Round(ActualHeight > 0 ? ActualHeight : Height);
        App.Current.Config.Save();
    }

    // ── 배치 ─────────────────────────────────────────────────

    /// <summary>저장된 자리가 없거나 화면 밖이면 주 모니터 작업 영역 가운데.</summary>
    private void Place()
    {
        var b = B;
        var wa = SystemParameters.WorkArea;
        var width = Math.Max(MinWidth, b.Width ?? Math.Min(1180, wa.Width - 48));
        var height = Math.Max(MinHeight, b.Height ?? Math.Min(660, wa.Height - 48));
        Width = width;
        Height = height;

        if (b.Left is { } l && b.Top is { } t && IsVisibleOnScreen(l, t, width, height))
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = wa.Left + (wa.Width - width) / 2;
            Top = wa.Top + (wa.Height - height) / 2;
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
        if (B.Locked || e.ClickCount > 1) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    // ── 테마·고정 ────────────────────────────────────────────

    private void ApplySettings()
    {
        var b = B;
        var p = WidgetPalette.For(b.Theme);
        p.ApplyTo(Resources);

        // 알파 0 픽셀은 클릭이 뒤로 통과하므로 투명도 100% 여도 1/255 는 남긴다.
        var alpha = (byte)Math.Max(1, Math.Round(255 * (100 - Math.Clamp(b.Transparency, 0, 100)) / 100.0));
        Frame.Background = new SolidColorBrush(Color.FromArgb(alpha, p.Background.R, p.Background.G, p.Background.B));
        // 카드는 배경보다 진하게 둬서 투명도를 올려도 읽힌다.
        var card = new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, alpha + 70), p.Surface.R, p.Surface.G, p.Surface.B));
        card.Freeze();
        Resources["W.Card"] = card;

        ResizeMode = b.Locked ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(b.Locked ? 0 : 6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Topmost = b.AlwaysOnTop;
        if (!b.AlwaysOnTop) DesktopPin.SendToBottom(this);

        Header.Cursor = b.Locked ? null : Cursors.SizeAll;
        LockIcon.Visibility = b.Locked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || B.Theme != "system") return;
        Dispatcher.BeginInvoke(() => { ApplySettings(); Render(); });
    }

    // ── 툴바 ─────────────────────────────────────────────────

    private void FillProjects()
    {
        if (_data is null) return;
        _ready = false;
        ProjectBox.Items.Clear();
        ProjectBox.Items.Add(new ComboBoxItem { Content = "모든 프로젝트 (열 = 상태 그룹)", Tag = "" });
        foreach (var p in _data.Projects.OrderBy(p => p.Identifier))
            ProjectBox.Items.Add(new ComboBoxItem { Content = $"{p.Identifier}  {p.Name}", Tag = p.Identifier });
        SyncToolbar();
    }

    /// <summary>설정값을 툴바에 맞춘다. 우클릭 메뉴에서 바꿔도 ComboBox·체크가 따라간다.</summary>
    private void SyncToolbar()
    {
        var b = B;
        _ready = false;
        ScopeBox.SelectedIndex = b.Scope == "mine" ? 0 : 1;
        ClosedCheck.IsChecked = b.ShowClosed;
        if (ProjectBox.Items.Count > 0)
            ProjectBox.SelectedItem = ProjectBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, b.Project, StringComparison.OrdinalIgnoreCase))
                ?? ProjectBox.Items[0];
        _ready = true;
    }

    private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ScopeBox.SelectedItem is not ComboBoxItem { Tag: string scope }) return;
        B.Scope = scope;
        Changed();
    }

    private void ProjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ProjectBox.SelectedItem is not ComboBoxItem { Tag: string project }) return;
        B.Project = project;
        Changed();
    }

    private void ClosedCheck_Click(object sender, RoutedEventArgs e)
    {
        B.ShowClosed = ClosedCheck.IsChecked == true;
        Changed();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    /// <summary>Plane 조회 중이면 새로고침 아이콘을 돌린다.</summary>
    public void SetBusy(bool busy) => Views.Spinner.Set(RefreshIcon, busy);

    private void Changed()
    {
        App.Current.Config.Save();
        ApplySettings();
        SyncToolbar();
        Render();
    }

    // ── 그리기 ───────────────────────────────────────────────

    private PlaneProject? SelectedProject =>
        B.Project.Length == 0 || _data is null ? null
        : _data.Projects.FirstOrDefault(p => string.Equals(p.Identifier, B.Project, StringComparison.OrdinalIgnoreCase));

    private void Render()
    {
        _renderPending = false;
        _editorView = null;
        CancelDrag();
        Columns.Children.Clear();
        _columns.Clear();
        if (_data is null) return;

        var project = SelectedProject;
        var defs = project is not null && _data.States.TryGetValue(project.Id, out var states)
            ? states.Select(s => new ColumnDef(s.Name, s.Color, s.Id, s.Group)).ToArray()
            : GroupColumns;

        var items = _data.AllItems
            .Where(i => B.Scope != "mine" || i.IsMine)
            .Where(i => project is null || i.ProjectId == project.Id)
            .ToList();
        var width = ColumnWidth(defs.Length);
        // 수정 중이던 항목이 새로고침·범위 변경으로 보드에서 빠지면 수정 칸도 닫는다.
        if (_editItemId is { } editId && !_addBusy && items.All(i => i.IssueId != editId))
        {
            _editItemId = null;
            ResetDraft();
        }

        var placed = 0;
        foreach (var def in defs)
        {
            var cards = items
                .Where(i => def.StateId is not null ? i.StateId == def.StateId : i.StateGroup == def.Group)
                .OrderBy(i => i.Due ?? DateOnly.MaxValue)
                .ThenBy(i => PriorityRank(i.Priority))
                .ThenBy(i => i.Key, StringComparer.Ordinal)
                .ToList();
            placed += cards.Count;
            var view = BuildColumn(def, cards, hideCards: def.Group is "completed" or "cancelled" && !B.ShowClosed);
            view.Width = width;
            _columns.Add((view, def));
            Columns.Children.Add(view);
        }

        var scope = (B.Scope == "mine" ? "내 담당" : "전체") + (project is null ? " · 모든 프로젝트" : $" · {project.Identifier}");
        var unknown = items.Count - placed;
        _status = (_updatedAt is { } at ? $"업데이트 {at:HH:mm} · " : "") + $"{scope} {items.Count}건"
                  + (unknown > 0 ? $" (상태를 알 수 없는 {unknown}건 제외)" : "")
                  + " · 카드를 누르면 수정, 다른 열로 끌면 상태 변경, 우클릭하면 Plane 에서 열기";
        StatusText.Text = _status;
    }

    /// <summary>열이 창 너비를 나눠 쓴다. 최소 너비보다 좁아지면 가로 스크롤.</summary>
    private double ColumnWidth(int count)
    {
        var avail = Scroller.ActualWidth > 0 ? Scroller.ActualWidth : Width - 24;
        return Math.Max(ColumnMinWidth, Math.Floor(avail / Math.Max(1, count)) - ColumnGap);
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ColumnWidth(_columns.Count);
        foreach (var (view, _) in _columns) view.Width = width;
    }

    private Border BuildColumn(ColumnDef def, List<BriefingItem> cards, bool hideCards)
    {
        var header = new DockPanel { Margin = new Thickness(4, 2, 4, 8), LastChildFill = false };
        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Frozen(def.Color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = def.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock
        {
            Text = hideCards ? $"{cards.Count} (숨김)" : $"{cards.Count}",
            FontSize = 12, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        header.Children.Add(dot);
        header.Children.Add(title);
        header.Children.Add(count);

        var list = new StackPanel();
        if (!hideCards)
            foreach (var item in cards) list.Children.Add(item.IssueId == _editItemId ? BuildAddEditor(def, item) : BuildCard(item));

        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        FrameworkElement footer = ColumnKey(def) == _addColumn ? BuildAddEditor(def) : BuildAddButton(def);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var column = new Border
        {
            Margin = new Thickness(0, 0, ColumnGap, 0),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Child = body,
        };
        column.SetResourceReference(Border.BackgroundProperty, "W.ItemFill");
        return column;
    }

    private Border BuildCard(BriefingItem item)
    {
        var key = new TextBlock { Text = item.Key, FontSize = 11, FontFamily = new FontFamily("Consolas, Segoe UI") };
        key.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        var name = new TextBlock { Text = item.Name, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 4) };

        var bottom = new DockPanel { LastChildFill = true };
        var due = new TextBlock { Text = item.Due is null ? "" : item.DueText, FontSize = 11 };
        due.SetResourceReference(TextBlock.ForegroundProperty, item.IsOverdue && item.IsOpen ? "W.Danger" : "W.Muted");
        DockPanel.SetDock(due, Dock.Right);
        bottom.Children.Add(due);
        var assignee = new TextBlock
        {
            Text = item.AssigneeText, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        assignee.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        bottom.Children.Add(assignee);

        var stack = new StackPanel();
        stack.Children.Add(key);
        stack.Children.Add(name);
        stack.Children.Add(bottom);

        var card = new Border
        {
            Child = stack,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            Tag = item,
            ToolTip = $"{item.Key}  {item.Name}\n{item.ProjectName}\n{item.StateName} · {PriorityLabel(item)} · {item.AssigneeText}"
                      + (item.StartDate is { } s ? $"\n시작 {s:MM'/'dd}" : "") + $"\n마감 {item.DueText}",
        };
        card.SetResourceReference(Border.BackgroundProperty, "W.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "W.Grid");
        // 우선순위가 있으면 왼쪽 테두리를 두껍게 해 그 색을 보인다.
        var bar = PriorityBrush(item.Priority);
        if (bar is not null)
        {
            card.BorderBrush = bar;
            card.BorderThickness = new Thickness(3, 1, 1, 1);
        }
        if (item.IsDone) card.Opacity = 0.5;

        card.MouseLeftButtonDown += Card_MouseLeftButtonDown;
        card.MouseMove += Card_MouseMove;
        card.MouseLeftButtonUp += Card_MouseLeftButtonUp;
        card.LostMouseCapture += (_, _) => CancelDrag();
        return card;
    }

    // ── 빠른 추가 ────────────────────────────────────────────

    private static string ColumnKey(ColumnDef def) => def.StateId ?? def.Group;

    private Button BuildAddButton(ColumnDef def)
    {
        var text = new TextBlock { Text = "＋ 새 작업 항목", FontSize = 12 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("Nav"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = $"'{def.Title}' 상태로 새 작업 항목 추가",
        };
        button.Click += (_, _) => OpenEditor(def);
        return button;
    }

    private const string BusyNote = "앞 항목을 저장하는 중입니다. 끝난 뒤 다시 해 주세요";

    private void OpenEditor(ColumnDef def)
    {
        if (_addBusy)
        {
            SetStatus(BusyNote);
            return;
        }
        if (_addColumn != ColumnKey(def) || _editItemId is not null) ResetDraft();
        _editItemId = null;
        _addColumn = ColumnKey(def);
        _addFocusPending = true;
        Render();
    }

    /// <summary>카드를 그 자리에서 수정 칸으로 바꾼다. 값은 지금 항목 값으로 채운다.</summary>
    private void OpenEdit(BriefingItem item)
    {
        if (_addBusy)
        {
            SetStatus(BusyNote);
            return;
        }
        _addColumn = null;
        _editItemId = item.IssueId;
        _addDraft = item.Name;
        _addPriority = item.Priority;
        _addStart = item.StartDate;
        _addDue = item.Due;
        _addAssignees = item.AssigneeIds.ToList();
        _addLabels.Clear();
        _addLabels.AddRange(item.LabelIds);
        _addPanel = null;
        _addFocusPending = true;
        // 라벨 칩에 이름을 보이려면 그 프로젝트 라벨 목록이 있어야 한다.
        if (item.LabelIds.Count > 0) _ = EnsureOptionsAsync();
        Render();
    }

    private BriefingItem? EditingItem =>
        _editItemId is { } id ? _data?.AllItems.FirstOrDefault(i => i.IssueId == id) : null;

    private void CloseEditor()
    {
        if (_addBusy) return;
        _addColumn = null;
        _editItemId = null;
        ResetDraft();
        Render();
    }

    /// <summary>제목과 세부 값을 비운다. 프로젝트 선택은 남긴다.</summary>
    private void ResetDraft()
    {
        _addDraft = "";
        _addPriority = "none";
        _addStart = null;
        _addDue = null;
        _addAssignees = null;
        _addLabels.Clear();
        _addPanel = null;
    }

    /// <summary>입력 칸이 만들 프로젝트. 프로젝트 하나 보기면 그 프로젝트, 모든 프로젝트 보기면 입력 칸에서 고른 것.</summary>
    private PlaneProject? EditorProject =>
        EditingItem is { } editing ? _data?.Projects.FirstOrDefault(p => p.Id == editing.ProjectId)
        : SelectedProject ?? _data?.Projects.FirstOrDefault(p => p.Id == _addProjectId);

    private List<string> EffectiveAssignees =>
        _addAssignees ?? (B.Scope == "mine" && _data is not null ? new List<string> { _data.MyUserId } : new List<string>());

    /// <summary>
    /// Plane 의 빠른 추가처럼 열 아래 입력 칸. 제목 아래 칩(우선순위·날짜·담당자·라벨)을 누르면 입력 칸 안에 선택 패널이 펼쳐진다.
    /// Enter 면 그 열의 상태로 만든다. Esc 는 패널이 펼쳐져 있으면 패널을, 아니면 입력 칸을 닫는다.
    /// 모든 프로젝트 보기(열 = 상태 그룹)에서는 어느 프로젝트에 만들지 고르는 ComboBox 가 붙는다.
    /// 팝업 대신 입력 칸 안에 펼치는 것은, 바탕화면 층 위젯이 활성화 없이 떠 있어 팝업의 마우스 capture·포커스가 불안정하기 때문이다.
    /// editing 이 있으면 카드 자리에 그리는 수정 칸이다. 프로젝트는 바꿀 수 없고 Enter 면 고친 값만 저장한다.
    /// </summary>
    private Border BuildAddEditor(ColumnDef def, BriefingItem? editing = null)
    {
        var stack = new StackPanel();
        _chipTexts.Clear();

        if (editing is not null)
        {
            var key = new TextBlock { Text = $"{editing.Key}  ·  {editing.StateName}", FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
            key.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
            stack.Children.Add(key);
        }
        else if (SelectedProject is null && _data is not null)
        {
            var projectBox = new ComboBox { Margin = new Thickness(0, 0, 0, 6), FontSize = 11.5, MinHeight = 24, IsEnabled = !_addBusy };
            foreach (var p in _data.Projects.OrderBy(p => p.Identifier))
                projectBox.Items.Add(new ComboBoxItem { Content = $"{p.Identifier}  {p.Name}", Tag = p });
            // 이번에 고른 프로젝트 → 설정의 기본 프로젝트 → 첫 프로젝트 순으로 고른다.
            var items = projectBox.Items.Cast<ComboBoxItem>().ToList();
            var defaultId = App.Current.DefaultProject?.Id;
            projectBox.SelectedItem = items.FirstOrDefault(i => ((PlaneProject)i.Tag).Id == _addProjectId)
                                      ?? items.FirstOrDefault(i => ((PlaneProject)i.Tag).Id == defaultId)
                                      ?? items.FirstOrDefault();
            if (projectBox.SelectedItem is ComboBoxItem { Tag: PlaneProject first }) _addProjectId = first.Id;
            projectBox.SelectionChanged += (_, _) =>
            {
                if (projectBox.SelectedItem is not ComboBoxItem { Tag: PlaneProject p } || p.Id == _addProjectId) return;
                // 라벨·멤버는 프로젝트마다 다르다.
                _addProjectId = p.Id;
                _addAssignees = null;
                _addLabels.Clear();
                Dispatcher.BeginInvoke(Render);
            };
            stack.Children.Add(projectBox);
        }

        // 제목 입력. 빈 칸이면 안내 글자를 겹쳐 보인다.
        var title = new TextBox
        {
            Text = _addDraft,
            FontSize = 12.5,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            IsEnabled = !_addBusy,
        };
        title.SetResourceReference(Control.ForegroundProperty, "W.Text");
        title.SetResourceReference(TextBoxBase.CaretBrushProperty, "W.Text");
        _addTitleBox = title;
        var hint = new TextBlock
        {
            Text = "작업 제목",
            FontSize = 12.5,
            Margin = new Thickness(2, 2, 0, 0),
            IsHitTestVisible = false,
            Visibility = _addDraft.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        var titleGrid = new Grid();
        titleGrid.Children.Add(title);
        titleGrid.Children.Add(hint);
        stack.Children.Add(titleGrid);
        title.TextChanged += (_, _) =>
        {
            _addDraft = title.Text;
            hint.Visibility = title.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        chips.Children.Add(Chip("priority", "", "우선순위"));
        chips.Children.Add(Chip("date", "", "날짜"));
        chips.Children.Add(Chip("assignee", "", "담당자"));
        chips.Children.Add(Chip("label", "", "라벨"));
        stack.Children.Add(chips);
        UpdateChips();

        if (_addPanel is not null && !_addBusy)
        {
            var panel = new Border
            {
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(5),
                Child = _addPanel switch
                {
                    "priority" => BuildPriorityPanel(),
                    "date" => BuildDatePanel(),
                    _ => BuildCheckPanel(_addPanel),
                },
            };
            panel.SetResourceReference(Border.BackgroundProperty, "W.ItemFill");
            stack.Children.Add(panel);
        }

        var keys = new TextBlock
        {
            Text = editing is not null
                ? (_addBusy ? "저장하는 중…" : "Enter 저장 · Esc 취소")
                : (_addBusy ? "등록하는 중…" : "Enter 등록 · Esc 닫기"),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        keys.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        stack.Children.Add(keys);

        var editor = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = editing is not null ? new Thickness(0, 0, 0, 6) : new Thickness(0, 4, 0, 0),
            Tag = editing is not null ? "editor" : null,
        };
        editor.SetResourceReference(Border.BackgroundProperty, "W.Card");
        // 수정 칸 안을 누를 때 열·창 끌기로 번지지 않게.
        editor.MouseLeftButtonDown += (_, e) => e.Handled = true;

        // 미뤄 둔 다시 그리기는 포커스가 입력 칸을 떠난 뒤(다른 카드·창으로 이동) 한다.
        _editorView = editor;
        editor.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue || !_renderPending) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_renderPending && _editorView?.IsKeyboardFocusWithin != true) Render();
            });
        };
        editor.SetResourceReference(Border.BorderBrushProperty, "W.TodayRing");

        // 칩·체크 목록에 포커스가 있어도 Enter·Esc 가 먹도록 입력 칸 전체에서 받는다.
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.OriginalSource is not ComboBox)
            {
                e.Handled = true;
                _ = editing is not null ? SaveEditAsync(editing) : SubmitAsync(def);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_addPanel is not null) TogglePanel(_addPanel);
                else CloseEditor();
            }
        };

        // 위젯은 활성화 없이 뜨므로 입력하려면 창을 활성화하고 포커스를 준다.
        // 백그라운드 새로고침으로 다시 그릴 때는 다른 앱의 포커스를 뺏지 않게 열기·등록 직후에만 한다.
        if (!_addBusy && _addFocusPending)
        {
            _addFocusPending = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                Activate();
                title.Focus();
                Keyboard.Focus(title);
                title.CaretIndex = title.Text.Length;
            });
        }
        return editor;
    }

    /// <summary>Plane 의 속성 버튼처럼 테두리 있는 작은 버튼. 누르면 그 패널을 펼치거나 접는다.</summary>
    private Button Chip(string key, string glyph, string placeholder)
    {
        var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
        var text = new TextBlock { Text = placeholder, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140, VerticalAlignment = VerticalAlignment.Center };
        text.Tag = placeholder;
        _chipTexts[key] = text;

        var body = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 7, 2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text } },
        };
        body.SetResourceReference(Border.BorderBrushProperty, _addPanel == key ? "W.TodayRing" : "W.Grid");

        var button = new Button
        {
            Content = body,
            Style = (Style)FindResource("Nav"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 4),
            IsEnabled = !_addBusy,
        };
        button.Click += (_, _) => TogglePanel(key);
        return button;
    }

    private void TogglePanel(string key)
    {
        _addPanel = _addPanel == key ? null : key;
        if (_addPanel is "assignee" or "label") _ = EnsureOptionsAsync();
        _addFocusPending = _addPanel is null; // 패널을 접으면 제목으로 돌아간다.
        Render();
    }

    /// <summary>칩 글자를 지금 값으로. 값이 있으면 글자색을 진하게 한다.</summary>
    private void UpdateChips()
    {
        Set("priority", _addPriority == "none" ? null : PriorityText(_addPriority));
        Set("date", DateRangeText(_addStart, _addDue));
        Set("assignee", NamesText(EffectiveAssignees, MemberName));
        Set("label", NamesText(_addLabels, LabelName));

        void Set(string key, string? value)
        {
            if (!_chipTexts.TryGetValue(key, out var t)) return;
            t.Text = value ?? (string)t.Tag;
            t.SetResourceReference(TextBlock.ForegroundProperty, value is null ? "W.Muted" : "W.Text");
        }
    }

    private static string? DateRangeText(DateOnly? start, DateOnly? due) => (start, due) switch
    {
        (null, null) => null,
        ({ } s, null) => $"{s:M'/'d} ~",
        (null, { } d) => $"~ {d:M'/'d}",
        ({ } s, { } d) when s == d => $"{d:M'/'d}",
        ({ } s, { } d) => $"{s:M'/'d} ~ {d:M'/'d}",
    };

    private static string? NamesText(List<string> ids, Func<string, string> name) => ids.Count switch
    {
        0 => null,
        1 => name(ids[0]),
        _ => $"{name(ids[0])} 외 {ids.Count - 1}",
    };

    private string MemberName(string id)
    {
        if (_data is not null && id == _data.MyUserId) return "나";
        if (EditorOptions?.Members.FirstOrDefault(m => m.Id == id) is { } m && m.DisplayName.Length > 0) return m.DisplayName;
        return _data?.MemberNames.GetValueOrDefault(id) is { Length: > 0 } n ? n : "?";
    }

    private string LabelName(string id) => EditorOptions?.Labels.FirstOrDefault(l => l.Id == id)?.Name ?? "?";

    private CreateOptions? EditorOptions => EditorProject is { } p ? _options.GetValueOrDefault(p.Id) : null;

    private static string PriorityText(string priority) => priority switch
    {
        "urgent" => "긴급", "high" => "높음", "medium" => "보통", "low" => "낮음", _ => "없음",
    };

    private UIElement BuildPriorityPanel()
    {
        var list = new StackPanel();
        foreach (var p in new[] { "urgent", "high", "medium", "low", "none" })
        {
            var dot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0),
                Background = PriorityBrush(p) ?? Frozen("#9CA3AF"), VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock { Text = PriorityText(p), FontSize = 12, FontWeight = p == _addPriority ? FontWeights.SemiBold : FontWeights.Normal };
            var row = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } },
                Style = (Style)FindResource("Nav"),
                HorizontalAlignment = HorizontalAlignment.Left, // Nav 템플릿은 내용을 가운데에 둔다.
                Padding = new Thickness(6, 3, 6, 3),
            };
            var value = p;
            row.Click += (_, _) =>
            {
                _addPriority = value;
                TogglePanel("priority");
            };
            list.Children.Add(row);
        }
        return list;
    }

    /// <summary>
    /// 달력 하나로 시작일~마감일. 하루만 고르면 마감일만, 드래그나 Shift+클릭으로 기간을 고르면 시작일과 마감일.
    /// WPF Calendar 는 날짜를 누른 뒤에도 마우스 capture 를 쥐고 있어 다음 클릭 한 번을 먹으므로 손을 떼면 풀어 준다.
    /// </summary>
    private UIElement BuildDatePanel()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var calendar = new Calendar
        {
            SelectionMode = CalendarSelectionMode.SingleRange,
            DisplayDate = (_addStart ?? _addDue ?? today).ToDateTime(TimeOnly.MinValue),
            IsTodayHighlighted = true,
        };
        // WPF Calendar 템플릿은 안쪽 테두리·요일·화살표 색이 밝은 배경 기준으로 고정돼 있다.
        // 어둡게 테마에서도 읽히도록 달력만 밝은 카드로 두고, 앱 전역 TextBlock 스타일이 읽는 Text 키도 어두운 글자로 덮는다.
        calendar.Background = Brushes.White;
        calendar.Resources["Text"] = Frozen("#1B1F24");
        calendar.SetResourceReference(Control.BorderBrushProperty, "W.Grid");
        if (_addDue is { } due)
            calendar.SelectedDates.AddRange((_addStart ?? due).ToDateTime(TimeOnly.MinValue), due.ToDateTime(TimeOnly.MinValue));
        else if (_addStart is { } start)
            calendar.SelectedDates.Add(start.ToDateTime(TimeOnly.MinValue));

        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDates.Count == 0)
            {
                _addStart = _addDue = null;
            }
            else
            {
                var first = DateOnly.FromDateTime(calendar.SelectedDates.Min());
                var last = DateOnly.FromDateTime(calendar.SelectedDates.Max());
                _addDue = last;
                _addStart = first < last ? first : null;
            }
            UpdateChips();
        };
        calendar.PreviewMouseUp += (_, _) =>
        {
            if (Mouse.Captured is CalendarItem) Mouse.Capture(null);
        };

        var hint = new TextBlock { Text = "하루 = 마감일 · 드래그/Shift+클릭 = 기간", FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 4) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");

        var buttons = new WrapPanel();
        buttons.Children.Add(SmallButton("오늘", () => SetDue(today)));
        buttons.Children.Add(SmallButton("내일", () => SetDue(today.AddDays(1))));
        buttons.Children.Add(SmallButton("이번 주 금", () => SetDue(today.AddDays(((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7))));
        buttons.Children.Add(SmallButton("지우기", () => { _addStart = _addDue = null; Render(); }));

        var stack = new StackPanel();
        // 열이 좁으면 달력을 줄인다(늘리지는 않는다).
        stack.Children.Add(new Viewbox { Child = calendar, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        stack.Children.Add(hint);
        stack.Children.Add(buttons);
        return stack;

        void SetDue(DateOnly d)
        {
            _addStart = null;
            _addDue = d;
            Render();
        }
    }

    private Button SmallButton(string text, Action action)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        var button = new Button { Content = label, Style = (Style)FindResource("Nav"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 2, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>담당자(여러 명)·라벨(여러 개) 체크 목록. 멤버·라벨은 패널을 처음 펼칠 때 프로젝트마다 한 번 받는다.</summary>
    private UIElement BuildCheckPanel(string kind)
    {
        var project = EditorProject;
        var options = EditorOptions;
        if (project is null) return Note("프로젝트를 골라 주세요");
        if (options is null)
            return Note(_optionsError is { } err && _optionsLoading is null ? $"못 불러왔습니다: {err}" : "불러오는 중…");

        var rows = kind == "assignee"
            ? options.Members.Select(m => (m.Id, Text: m.Id == _data?.MyUserId ? $"{m.DisplayName} (나)" : m.DisplayName))
                .OrderBy(r => r.Id != _data?.MyUserId).ToList()
            : options.Labels.Select(l => (l.Id, Text: l.Name)).ToList();
        if (rows.Count == 0) return Note(kind == "assignee" ? "프로젝트 멤버가 없습니다" : "이 프로젝트에는 라벨이 없습니다");

        // 담당자 기본값(범위가 내 담당이면 나)을 처음 바꿀 때 실제 목록으로 옮긴다.
        var selected = kind == "assignee" ? (_addAssignees ??= EffectiveAssignees) : _addLabels;
        var list = new StackPanel();
        foreach (var (id, text) in rows)
        {
            var check = new CheckBox
            {
                Content = text,
                FontSize = 12,
                Style = (Style)FindResource("W.Check"),
                IsChecked = selected.Contains(id),
                Margin = new Thickness(2, 2, 2, 3),
            };
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) { if (!selected.Contains(id)) selected.Add(id); }
                else selected.Remove(id);
                UpdateChips();
            };
            list.Children.Add(check);
        }
        return new ScrollViewer { Content = list, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "W.Muted");
        return t;
    }

    private async Task EnsureOptionsAsync()
    {
        if (EditorProject is not { } project || OptionsLoader is null) return;
        if (_options.ContainsKey(project.Id) || _optionsLoading == project.Id) return;
        _optionsLoading = project.Id;
        _optionsError = null;
        try
        {
            _options[project.Id] = await OptionsLoader(project);
        }
        catch (Exception ex)
        {
            AppLog.Error($"빠른 추가 멤버·라벨 조회 실패 ({project.Identifier})", ex);
            _optionsError = ex.Message;
        }
        finally
        {
            _optionsLoading = null;
        }
        if (_addPanel is "assignee" or "label") Render();
        else UpdateChips(); // 수정 칸의 라벨 칩 이름. 다시 그리지 않아 입력 중인 제목의 포커스를 지킨다.
    }

    private async Task SubmitAsync(ColumnDef def)
    {
        var name = _addDraft.Trim();
        if (_addBusy || name.Length == 0 || _data is null || CreateHandler is null) return;

        var project = EditorProject;
        if (project is null)
        {
            SetStatus("프로젝트를 골라 주세요");
            return;
        }
        var states = _data.States.GetValueOrDefault(project.Id) ?? new List<PlaneState>();
        var state = def.StateId is { } id ? states.FirstOrDefault(s => s.Id == id) : BriefingService.FirstInGroup(states, def.Group);
        if (state is null)
        {
            SetStatus($"{project.Identifier} 프로젝트에는 '{def.Title}' 상태가 없습니다");
            return;
        }

        var draft = new NewItemDraft(name, _addPriority, _addStart, _addDue, EffectiveAssignees.ToList(), _addLabels.ToList());
        _addBusy = true;
        Render();
        var ok = false;
        var before = _status;
        try { ok = await CreateHandler(project, state, draft); }
        finally
        {
            // 처리 쪽이 SetStatus 로 남긴 결과 문구는 다시 그린 뒤에도 보이게 한다.
            var note = _status != before ? _status : null;
            _addBusy = false;
            if (ok) ResetDraft();
            _addFocusPending = true;
            Render();
            if (note is not null) SetStatus(note);
        }
    }

    private async Task SaveEditAsync(BriefingItem item)
    {
        var name = _addDraft.Trim();
        if (_addBusy || UpdateHandler is null) return;
        if (name.Length == 0)
        {
            SetStatus("제목을 비울 수 없습니다");
            return;
        }

        var draft = new NewItemDraft(name, _addPriority, _addStart, _addDue, EffectiveAssignees.ToList(), _addLabels.ToList());
        _addBusy = true;
        Render();
        var ok = false;
        var before = _status;
        try { ok = await UpdateHandler(item, draft); }
        finally
        {
            var note = _status != before ? _status : null;
            _addBusy = false;
            if (ok)
            {
                _editItemId = null;
                ResetDraft();
            }
            else _addFocusPending = true;
            Render();
            if (note is not null) SetStatus(note);
        }
    }

    // ── 카드 끌기 ────────────────────────────────────────────

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: BriefingItem item } card) return;
        e.Handled = true; // 제목 줄 끌기(창 이동)로 번지지 않게.
        _dragItem = item;
        _dragOrigin = e.GetPosition(Root);
        _dragging = false;
        card.CaptureMouse();
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Root);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            GhostText.Text = $"{_dragItem.Key}  {_dragItem.Name}";
            Ghost.Visibility = Visibility.Visible;
        }
        Canvas.SetLeft(Ghost, p.X + 12);
        Canvas.SetTop(Ghost, p.Y + 8);

        var target = ColumnAt(e);
        if (target?.View != _dropTarget?.View)
        {
            if (_dropTarget is { } old) old.View.SetResourceReference(Border.BackgroundProperty, "W.ItemFill");
            _dropTarget = target;
            if (target is { } t && !IsCurrentColumn(_dragItem, t.Def)) t.View.SetResourceReference(Border.BackgroundProperty, "W.TodayFill");
        }
        StatusText.Text = target is { } col && !IsCurrentColumn(_dragItem, col.Def)
            ? $"{_dragItem.Key} → {col.Def.Title}  (놓으면 Plane 에 저장)"
            : $"{_dragItem.Key}  (다른 열에 놓으면 상태가 바뀐다)";
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var item = _dragItem;
        var dragging = _dragging;
        var target = dragging ? ColumnAt(e) : null;
        CancelDrag();
        if (item is null) return;

        if (!dragging)
        {
            OpenEdit(item); // Plane 에서 열기는 카드 우클릭 메뉴로 옮겼다.
            return;
        }
        if (target is not { } col || IsCurrentColumn(item, col.Def) || _data is null) return;

        var states = _data.States.GetValueOrDefault(item.ProjectId) ?? new List<PlaneState>();
        var state = col.Def.StateId is { } id
            ? states.FirstOrDefault(s => s.Id == id)
            : BriefingService.FirstInGroup(states, col.Def.Group);
        if (state is null)
        {
            SetStatus($"{item.ProjectIdentifier} 프로젝트에는 '{col.Def.Title}' 상태가 없습니다");
            return;
        }
        StateMoveRequested?.Invoke(item, state);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragItem is not null)
        {
            CancelDrag();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>마우스 아래 열. 세로 위치는 보지 않아 열 머리·빈 곳에 놓아도 된다.</summary>
    private (Border View, ColumnDef Def)? ColumnAt(MouseEventArgs e)
    {
        foreach (var c in _columns)
        {
            var x = e.GetPosition(c.View).X;
            if (x >= 0 && x <= c.View.ActualWidth) return c;
        }
        return null;
    }

    private static bool IsCurrentColumn(BriefingItem item, ColumnDef def) =>
        def.StateId is not null ? item.StateId == def.StateId : item.StateGroup == def.Group;

    /// <summary>끌기를 끝낸다. 렌더링으로 카드가 사라지거나 캡처를 잃어도 여기로 온다.</summary>
    private void CancelDrag()
    {
        var captured = Mouse.Captured as FrameworkElement;
        _dragItem = null;
        _dragging = false;
        Ghost.Visibility = Visibility.Collapsed;
        if (_dropTarget is { } t) t.View.SetResourceReference(Border.BackgroundProperty, "W.ItemFill");
        _dropTarget = null;
        StatusText.Text = _status;
        if (captured is { Tag: BriefingItem }) captured.ReleaseMouseCapture();
    }

    // ── 우클릭 메뉴 ──────────────────────────────────────────

    private void Frame_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem is not null) return;
        var b = B;
        // 메뉴는 창 밖 팝업이라 창 Resources 를 못 본다. 같은 테마 색을 메뉴에도 넣는다.
        var menu = new ContextMenu();
        WidgetPalette.For(b.Theme).ApplyTo(menu.Resources);
        // 카드(또는 수정 칸) 위에서 누르면 그 항목 메뉴를 맨 위에 붙인다.
        if (CardItemAt(e.OriginalSource as DependencyObject) is { } card)
        {
            menu.Items.Add(new MenuItem { Header = $"{card.Key}  {card.Name}", IsEnabled = false });
            menu.Items.Add(Item("Plane 에서 열기", () => OpenRequested?.Invoke(card)));
            if (card.IssueId != _editItemId) menu.Items.Add(Item("수정", () => OpenEdit(card)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("새로고침", () => RefreshRequested?.Invoke()));
        menu.Items.Add(Item("캘린더 위젯 보이기", () => CalendarRequested?.Invoke()));
        menu.Items.Add(new Separator());

        menu.Items.Add(Choice("표시 범위", b.Scope, v => b.Scope = v, ("내 담당", "mine"), ("전체 (담당자 무관)", "all")));
        var projects = new List<(string, string)> { ("모든 프로젝트 (열 = 상태 그룹)", "") };
        if (_data is not null)
            projects.AddRange(_data.Projects.OrderBy(p => p.Identifier).Select(p => ($"{p.Identifier}  {p.Name}", p.Identifier)));
        menu.Items.Add(Choice("프로젝트", b.Project, v => b.Project = v, projects.ToArray()));
        menu.Items.Add(Toggle("완료·취소 카드 표시", b.ShowClosed, v => b.ShowClosed = v));
        menu.Items.Add(new Separator());

        menu.Items.Add(Choice("테마", b.Theme, v => b.Theme = v,
            ("밝게", "light"), ("어둡게", "dark"), ("Windows 설정 따라감", "system")));
        menu.Items.Add(Choice("투명도", b.Transparency, v => b.Transparency = v,
            Enumerable.Range(0, 11).Select(i => (i == 0 ? "0% (불투명)" : i == 10 ? "100% (배경 없음)" : $"{i * 10}%", i * 10)).ToArray()));
        menu.Items.Add(new Separator());

        menu.Items.Add(Toggle("항상 위에 표시", b.AlwaysOnTop, v => b.AlwaysOnTop = v));
        menu.Items.Add(Toggle("위치·크기 고정", b.Locked, v => b.Locked = v));
        menu.Items.Add(Item("보드 숨기기", () => HideRequested?.Invoke()));

        menu.PlacementTarget = Frame;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>우클릭한 곳을 감싼 카드의 항목. 수정 칸 안이면 수정 중인 항목.</summary>
    private BriefingItem? CardItemAt(DependencyObject? source)
    {
        for (var d = source; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is Border { Tag: BriefingItem item }) return item;
            if (d is Border { Tag: "editor" }) return EditingItem;
        }
        return null;
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

    // ── 보조 ─────────────────────────────────────────────────

    private static Brush? PriorityBrush(string priority) => priority switch
    {
        "urgent" => Frozen("#DC2626"),
        "high" => Frozen("#EA580C"),
        "medium" => Frozen("#CA8A04"),
        "low" => Frozen("#2563EB"),
        _ => null,
    };

    private static string PriorityLabel(BriefingItem item) => item.PriorityLabel.Length > 0 ? $"우선순위 {item.PriorityLabel}" : "우선순위 없음";

    private static int PriorityRank(string priority) => priority switch
    {
        "urgent" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4,
    };

    private static Brush Frozen(string hex)
    {
        Brush b;
        try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { b = new SolidColorBrush(Colors.Gray); }
        b.Freeze();
        return b;
    }
}
