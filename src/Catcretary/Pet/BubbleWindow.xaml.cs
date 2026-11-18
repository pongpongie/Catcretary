using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Catcretary.Models;
using Catcretary.Services;

namespace Catcretary.Pet;

public enum BubbleMode { Hidden, Message, Input, Confirm, Action, Briefing }

/// <summary>
/// 캐릭터 옆에 뜨는 말풍선. 메시지 / 입력 / 등록 확인 / 범용 확인 / 항목 목록 모드를 한 창에서 바꿔 쓴다.
/// 카드 바깥은 투명이라 클릭이 통과한다.
/// </summary>
public partial class BubbleWindow : Window
{
    private readonly PetWindow _pet;
    private readonly DispatcherTimer _autoHide = new();
    private BubbleMode _mode = BubbleMode.Hidden;
    private TodoDraft? _draft;
    private Action? _onMessageClick;
    private Action? _onAction;

    public event Action<string>? Submitted;
    public event Action<TodoDraft>? Confirmed;
    public event Action<BriefingItem>? CompleteRequested;
    public event Action<BriefingItem>? OpenRequested;
    /// <summary>말풍선이 접혔다. 고양이를 기본 상태로 돌릴 때 쓴다.</summary>
    public event Action? Hidden;

    private static readonly string[] Prompts =
    {
        "뭐 할 거냥?", "말해 보라냥", "듣고 있다냥", "무슨 일이냥?", "적어 줄 테니 말하라냥",
    };
    private readonly Random _rng = new();

    public BubbleMode Mode => _mode;

    public BubbleWindow(PetWindow pet)
    {
        InitializeComponent();
        _pet = pet;
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); HideBubble(); };
        _pet.LocationChanged += (_, _) => { if (_mode != BubbleMode.Hidden) Reposition(); };
        SizeChanged += (_, _) => { if (_mode != BubbleMode.Hidden) Reposition(); };
    }

    // ── 모드 전환 ─────────────────────────────────────────────

    public void ShowMessage(string text, TimeSpan? autoHide = null, Action? onClick = null)
    {
        SetMode(BubbleMode.Message);
        MessageText.Text = text;
        _onMessageClick = onClick;
        Card.Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand;
        Present(activate: false);
        StartAutoHide(autoHide ?? TimeSpan.FromSeconds(6));
    }

    public void ShowInput(string? prefill = null)
    {
        SetMode(BubbleMode.Input);
        InputPrompt.Text = Prompts[_rng.Next(Prompts.Length)];
        InputBox.Text = prefill ?? "";
        Present(activate: true);
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    public void ShowConfirm(TodoDraft draft, List<PlaneProject> projects)
    {
        _draft = draft;
        SetMode(BubbleMode.Confirm);

        ConfirmQuestion.Text = draft.Clarification ?? "이렇게 등록할까냥?";
        ConfirmProject.Text = draft.Project is null ? "—" : draft.Project.Name + (draft.ProjectFromDefault ? " (기본)" : "");
        ConfirmTitle.Text = draft.Title.Length == 0 ? "—" : draft.Title;
        ConfirmDue.Text = draft.TargetDate is { } d ? TodoParser.FormatDate(d) : "없음";
        ConfirmPriority.Text = draft.PriorityLabel;
        SetOptionalRow(ConfirmStartLabel, ConfirmStart, draft.StartDate is { } s ? TodoParser.FormatDate(s) : null);
        SetOptionalRow(ConfirmLabelsLabel, ConfirmLabels, draft.Labels.Count > 0 ? string.Join(" ", draft.Labels.Select(l => "#" + l)) : null);
        SetOptionalRow(ConfirmCycleLabel, ConfirmCycle, draft.AddToCycle ? ConfirmCycle.Text : null);

        ProjectChoices.Children.Clear();
        // 기본 프로젝트로 채운 경우에도 다른 프로젝트를 고를 수 있게 버튼을 보인다.
        if (draft.Project is null || draft.ProjectFromDefault)
        {
            foreach (var p in projects.Where(p => p.Id != draft.Project?.Id))
            {
                var b = new Button { Content = p.Name, Style = (Style)FindResource("Chip"), Tag = p };
                b.Click += (_, _) =>
                {
                    _draft!.Project = (PlaneProject)b.Tag;
                    _draft.ProjectFromDefault = false;
                    _draft.Clarification = _draft.Title.Length == 0 ? "무엇을 등록할까요?" : null;
                    ShowConfirm(_draft, projects);
                };
                ProjectChoices.Children.Add(b);
            }
        }

        ConfirmButton.IsEnabled = draft.Project is not null && draft.Title.Length > 0 && draft.DatesValid;
        Present(activate: true);
        if (ConfirmButton.IsEnabled) ConfirmButton.Focus();
    }

    /// <summary>
    /// 쓰기 전에 묻는 범용 확인. rows 는 (이름, 값) 줄. 값이 null 인 줄은 건너뛴다.
    /// canConfirm 이 false 면 버튼을 막고 질문만 보여준다(되묻기).
    /// </summary>
    public void ShowAction(string question, IEnumerable<(string Label, string? Value)> rows, string buttonText, bool canConfirm, Action onConfirm)
    {
        SetMode(BubbleMode.Action);
        ActionQuestion.Text = question;
        ActionRows.Children.Clear();
        ActionRows.RowDefinitions.Clear();
        foreach (var (label, value) in rows)
        {
            if (value is null) continue;
            var r = ActionRows.RowDefinitions.Count;
            ActionRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 12, 3), Foreground = (Brush)FindResource("TextMuted") };
            var val = new TextBlock { Text = value, FontSize = 12, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
            Grid.SetRow(name, r);
            Grid.SetRow(val, r);
            Grid.SetColumn(val, 1);
            ActionRows.Children.Add(name);
            ActionRows.Children.Add(val);
        }
        ActionButton.Content = buttonText;
        ActionButton.IsEnabled = canConfirm;
        _onAction = canConfirm ? onConfirm : null;
        Present(activate: true);
        if (canConfirm) ActionButton.Focus();
    }

    public void ShowBriefing(BriefingResult result, TimeSpan? autoHide = null)
    {
        var b = result.Briefing;
        ShowList(b.HeadlineWithExtra, string.Join("  ·  ", b.Projects.Select(p => p.Line)), b.Sections, "할 일이 없다냥.");
        if (autoHide is { } t) StartAutoHide(t);
    }

    /// <summary>섹션별 항목 목록. 브리핑과 검색 결과가 같이 쓴다.</summary>
    public void ShowList(string headline, string subtitle, IReadOnlyList<BriefingSection> sections, string emptyText)
    {
        SetMode(BubbleMode.Briefing);
        BriefingHeadline.Text = headline;
        BriefingProjects.Text = subtitle;
        BriefingProjects.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionList.ItemsSource = sections;
        BriefingEmpty.Text = emptyText;
        BriefingEmpty.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Present(activate: true);
    }

    private static void SetOptionalRow(TextBlock label, TextBlock value, string? text)
    {
        var vis = text is null ? Visibility.Collapsed : Visibility.Visible;
        label.Visibility = vis;
        value.Visibility = vis;
        if (text is not null) value.Text = text;
    }

    public void HideBubble()
    {
        _autoHide.Stop();
        var wasVisible = _mode != BubbleMode.Hidden;
        _mode = BubbleMode.Hidden;
        Hide();
        if (wasVisible) Hidden?.Invoke();
    }

    private void SetMode(BubbleMode mode)
    {
        _autoHide.Stop();
        _mode = mode;
        MessageText.Visibility = mode == BubbleMode.Message ? Visibility.Visible : Visibility.Collapsed;
        InputPanel.Visibility = mode == BubbleMode.Input ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = mode == BubbleMode.Confirm ? Visibility.Visible : Visibility.Collapsed;
        ActionPanel.Visibility = mode == BubbleMode.Action ? Visibility.Visible : Visibility.Collapsed;
        BriefingPanel.Visibility = mode == BubbleMode.Briefing ? Visibility.Visible : Visibility.Collapsed;
        Card.Cursor = Cursors.Arrow;
        _onMessageClick = null;
        _onAction = null;
    }

    private void Present(bool activate)
    {
        ShowActivated = activate;
        if (!IsVisible) Show();
        UpdateLayout();
        Reposition();
        if (activate) Activate();
    }

    private void StartAutoHide(TimeSpan after)
    {
        _autoHide.Stop();
        _autoHide.Interval = after;
        _autoHide.Start();
    }

    // ── 배치 ─────────────────────────────────────────────────

    /// <summary>캐릭터 위쪽, 오른쪽 끝을 맞춰 놓는다. 위가 모자라면 왼쪽으로, 모니터 밖이면 안으로 민다.</summary>
    private void Reposition()
    {
        var pet = _pet.Bounds;
        var area = ScreenHelper.WorkAreaFor(_pet);

        var w = ActualWidth;
        var h = ActualHeight;

        var left = pet.Right - w + 14;
        var top = pet.Top - h + 6;

        if (top < area.Top)
        {
            // 위 공간이 없으면 캐릭터 왼쪽에 붙인다.
            left = pet.Left - w + 14;
            top = Math.Max(area.Top, pet.Bottom - h);
        }

        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - w));
        top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - h));

        Left = left;
        Top = top;
    }

    // ── 입력 처리 ────────────────────────────────────────────

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideBubble(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        Submitted?.Invoke(text);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null) return;
        Confirmed?.Invoke(_draft);
    }

    private void Action_Click(object sender, RoutedEventArgs e) => _onAction?.Invoke();

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideBubble();

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BriefingItem item }) CompleteRequested?.Invoke(item);
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BriefingItem item }) return;
        e.Handled = true;
        OpenRequested?.Invoke(item);
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == BubbleMode.Message) _onMessageClick?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _mode is BubbleMode.Confirm or BubbleMode.Action or BubbleMode.Briefing)
        {
            HideBubble();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>다른 곳을 클릭하면 접힌다. 메시지 모드는 타이머로만 닫힌다.</summary>
    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_mode is BubbleMode.Input or BubbleMode.Confirm or BubbleMode.Action or BubbleMode.Briefing) HideBubble();
    }
}

/// <summary>DIP 좌표계 기준의 작업 영역. 창이 어느 모니터에 있든 그 모니터의 영역을 돌려준다.</summary>
public static class ScreenHelper
{
    public static Rect WorkAreaFor(Window window)
    {
        try
        {
            var source = PresentationSource.FromVisual(window);
            var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

            var center = toDevice.Transform(new Point(window.Left + window.ActualWidth / 2, window.Top + window.ActualHeight / 2));
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
            var wa = screen.WorkingArea;

            var tl = fromDevice.Transform(new Point(wa.Left, wa.Top));
            var br = fromDevice.Transform(new Point(wa.Right, wa.Bottom));
            return new Rect(tl, br);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }
}
