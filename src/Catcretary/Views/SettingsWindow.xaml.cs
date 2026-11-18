using System.Globalization;
using System.Windows;
using Catcretary.Services;

namespace Catcretary.Views;

public partial class SettingsWindow : Window
{
    public AppConfig Result { get; private set; }

    public SettingsWindow(AppConfig current)
    {
        InitializeComponent();

        Result = current;

        BaseUrlBox.Text = current.BaseUrl;
        WorkspaceBox.Text = current.WorkspaceSlug;
        ApiKeyBox.Password = current.ApiKey;
        TimeBox.Text = current.BriefingTime;
        WeekdaysCheck.IsChecked = current.WeekdaysOnly;
        OpenWindowCheck.IsChecked = current.OpenWindowOnBriefing;
        ExcludedBox.Text = string.Join(", ", current.ExcludedProjects);
        AutoStartCheck.IsChecked = AutoStart.IsEnabled;

        // 목록은 마지막 조회에서 받은 등록 대상 프로젝트. 아직 조회 전이면 직접 입력한다.
        DefaultProjectBox.Items.Add("");
        foreach (var p in App.Current.Latest?.Projects.OrderBy(p => p.Identifier) ?? Enumerable.Empty<Models.PlaneProject>())
            DefaultProjectBox.Items.Add(p.Identifier);
        DefaultProjectBox.Text = current.DefaultProject;
    }

    private AppConfig Collect() => new()
    {
        BaseUrl = BaseUrlBox.Text.Trim(),
        WorkspaceSlug = WorkspaceBox.Text.Trim(),
        ApiKey = ApiKeyBox.Password.Trim(),
        BriefingTime = TimeBox.Text.Trim(),
        WeekdaysOnly = WeekdaysCheck.IsChecked == true,
        OpenWindowOnBriefing = OpenWindowCheck.IsChecked == true,
        RefreshIntervalMinutes = Result.RefreshIntervalMinutes,
        DefaultProject = DefaultProjectBox.Text.Trim().ToUpperInvariant(),
        ExcludedProjects = ExcludedBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
    };

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var config = Collect();
        if (!config.IsComplete)
        {
            TestResult.Text = "서버 주소·slug·API 키가 모두 필요합니다.";
            return;
        }

        TestButton.IsEnabled = false;
        TestResult.Text = "확인 중";
        try
        {
            using var client = new PlaneClient(config);
            var me = await client.GetMeAsync();
            var projects = await client.GetProjectsAsync();
            TestResult.Text = $"연결됨. {me.DisplayName} ({me.Email}), 프로젝트 {projects.Count}개";
        }
        catch (Exception ex)
        {
            AppLog.Error("연결 테스트 실패", ex);
            TestResult.Text = $"실패: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = Collect();

        if (!config.IsComplete)
        {
            TestResult.Text = "서버 주소·slug·API 키가 모두 필요합니다.";
            return;
        }
        if (!TimeSpan.TryParseExact(config.BriefingTime, "h\\:mm", CultureInfo.InvariantCulture, out _))
        {
            TestResult.Text = "알림 시각은 HH:mm 형식이어야 합니다.";
            return;
        }

        AutoStart.Set(AutoStartCheck.IsChecked == true);
        Result = config;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
