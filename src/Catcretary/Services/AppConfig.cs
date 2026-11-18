using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Catcretary.Services;

/// <summary>
/// 사용자 설정. API 키가 들어가므로 저장소가 아니라 %APPDATA%\Catcretary\config.json 에 둔다.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "https://plane.example.com";
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("workspaceSlug")] public string WorkspaceSlug { get; set; } = "";
    /// <summary>"HH:mm". 이 시각이 지나면 그날 첫 브리핑을 띄운다.</summary>
    [JsonPropertyName("briefingTime")] public string BriefingTime { get; set; } = "09:00";
    [JsonPropertyName("weekdaysOnly")] public bool WeekdaysOnly { get; set; } = true;
    /// <summary>브리핑 시각에 알림만 띄울지, 창까지 열지.</summary>
    [JsonPropertyName("openWindowOnBriefing")] public bool OpenWindowOnBriefing { get; set; } = true;
    [JsonPropertyName("refreshIntervalMinutes")] public int RefreshIntervalMinutes { get; set; } = 30;
    /// <summary>브리핑에서 제외할 프로젝트 identifier 목록 (예: SANDBOX).</summary>
    [JsonPropertyName("excludedProjects")] public List<string> ExcludedProjects { get; set; } = new();
    /// <summary>프로젝트 identifier → 말로 부를 때 쓰는 별칭들. 예: "SHOP2": ["SHOP", "쇼핑몰"].</summary>
    [JsonPropertyName("projectAliases")] public Dictionary<string, List<string>> ProjectAliases { get; set; } = new();
    /// <summary>캐릭터 창 위치(DIP). null 이면 주 모니터 우하단.</summary>
    [JsonPropertyName("petLeft")] public double? PetLeft { get; set; }
    [JsonPropertyName("petTop")] public double? PetTop { get; set; }
    /// <summary>스프라이트 확대 배율. 32px 셀 × 4 = 128px.</summary>
    [JsonPropertyName("petScale")] public int PetScale { get; set; } = 4;

    /// <summary>
    /// 말풍선 등록에서 프로젝트를 말하지 않았을 때 넣을 프로젝트 identifier (예: ME). 비우면 되묻는다.
    /// 보드의 "모든 프로젝트" 보기에서 빠른 추가를 열 때도 처음 고른 프로젝트가 된다.
    /// </summary>
    [JsonPropertyName("defaultProject")] public string DefaultProject { get; set; } = "";

    /// <summary>바탕화면 캘린더 위젯. 위젯 우클릭 메뉴에서 바꾸면 바로 저장된다.</summary>
    [JsonPropertyName("widget")] public WidgetConfig Widget { get; set; } = new();

    /// <summary>상태별 보드 창. 툴바에서 바꾸면 바로 저장된다.</summary>
    [JsonPropertyName("board")] public BoardConfig Board { get; set; } = new();

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Catcretary");

    public static string Path_ => System.IO.Path.Combine(Directory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 한글 별칭이 유니코드 이스케이프로 저장되지 않게.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(WorkspaceSlug);

    /// <summary>앱 이름이 PlaneBriefing 이던 시절의 설정 폴더. 새 폴더가 비어 있으면 가져온다.</summary>
    private static string LegacyDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlaneBriefing");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(Path_) && System.IO.Directory.Exists(LegacyDirectory))
            {
                System.IO.Directory.CreateDirectory(Directory);
                foreach (var f in System.IO.Directory.GetFiles(LegacyDirectory, "*.json"))
                    File.Copy(f, System.IO.Path.Combine(Directory, System.IO.Path.GetFileName(f)), overwrite: false);
            }

            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_)) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // 설정 파일이 깨졌으면 기본값으로 시작한다. 설정 창에서 다시 입력받는다.
            AppLog.Error("설정 파일 읽기 실패, 기본값으로 시작", ex);
        }
        return new AppConfig();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path_, JsonSerializer.Serialize(this, Options));
    }
}

public sealed class WidgetConfig
{
    [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
    /// <summary>창 위치·크기(DIP). null 이면 주 모니터 우상단 기본 크기.</summary>
    [JsonPropertyName("left")] public double? Left { get; set; }
    [JsonPropertyName("top")] public double? Top { get; set; }
    [JsonPropertyName("width")] public double? Width { get; set; }
    [JsonPropertyName("height")] public double? Height { get; set; }
    /// <summary>위치·크기 고정. 켜면 끌기·크기 조절이 막힌다.</summary>
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    /// <summary>false 면 바탕화면 층(모든 창 아래), true 면 다른 창 위에 항상 표시.</summary>
    [JsonPropertyName("alwaysOnTop")] public bool AlwaysOnTop { get; set; }
    /// <summary>"light" / "dark" / "system".</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "light";
    /// <summary>배경 투명도 0~100(%). 100 이면 배경 없이 글자만 남는다.</summary>
    [JsonPropertyName("transparency")] public int Transparency { get; set; } = 15;
    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 12;
    [JsonPropertyName("weekStartsMonday")] public bool WeekStartsMonday { get; set; }
    [JsonPropertyName("showLunar")] public bool ShowLunar { get; set; } = true;
    [JsonPropertyName("showHolidays")] public bool ShowHolidays { get; set; } = true;
    /// <summary>켜면 시작일~마감일 사이 모든 날에 표시한다. 끄면 마감일(없으면 시작일) 하루에만.</summary>
    [JsonPropertyName("showRange")] public bool ShowRange { get; set; }
    /// <summary>켜면 월 대신 5주를 보여주고 이번 주를 가운데(3번째) 줄에 둔다.</summary>
    [JsonPropertyName("focusWeek")] public bool FocusWeek { get; set; }
    /// <summary>진행 중인 cycle 기간을 칸 아래 띠로 표시.</summary>
    [JsonPropertyName("showCycles")] public bool ShowCycles { get; set; } = true;
    /// <summary>"mine" 내 담당만, "all" 담당자 무관 전체.</summary>
    [JsonPropertyName("scope")] public string Scope { get; set; } = "mine";
    /// <summary>보여줄 프로젝트 identifier. 비어 있으면 모든 프로젝트.</summary>
    [JsonPropertyName("projects")] public List<string> Projects { get; set; } = new();
    /// <summary>계산으로 알 수 없는 휴일. "yyyy-MM-dd" → 이름. 예: "2026-06-03": "지방선거".</summary>
    [JsonPropertyName("extraHolidays")] public Dictionary<string, string> ExtraHolidays { get; set; } = new();
}

/// <summary>상태별 보드 위젯. 고정·테마·투명도·항상 위 의미는 <see cref="WidgetConfig"/> 와 같다.</summary>
public sealed class BoardConfig
{
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("alwaysOnTop")] public bool AlwaysOnTop { get; set; }
    /// <summary>"light" / "dark" / "system".</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "light";
    /// <summary>배경 투명도 0~100(%).</summary>
    [JsonPropertyName("transparency")] public int Transparency { get; set; } = 15;
    [JsonPropertyName("left")] public double? Left { get; set; }
    [JsonPropertyName("top")] public double? Top { get; set; }
    [JsonPropertyName("width")] public double? Width { get; set; }
    [JsonPropertyName("height")] public double? Height { get; set; }
    /// <summary>"mine" / "all".</summary>
    [JsonPropertyName("scope")] public string Scope { get; set; } = "all";
    /// <summary>프로젝트 identifier. 비어 있으면 모든 프로젝트(열 = state 그룹), 하나면 열 = 그 프로젝트의 state.</summary>
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    /// <summary>완료·취소 열의 카드 표시. 꺼도 열은 남아 있어 끌어다 놓을 수 있다.</summary>
    [JsonPropertyName("showClosed")] public bool ShowClosed { get; set; }
}

/// <summary>
/// 마지막으로 브리핑을 띄운 "날짜@시각" 키. 하루 한 번만 띄우되,
/// 설정에서 시각을 바꾸면 키가 달라져 그날 다시 뜬다.
/// </summary>
public sealed class AppState
{
    [JsonPropertyName("lastBriefingKey")] public string? LastBriefingKey { get; set; }

    /// <summary>
    /// "새로 배정"·"새 소식" 기준. 브리핑 시각마다 한 칸씩 민다:
    /// baseline ← announced, announced ← 이번 내 담당 id, prevAnnounceAt ← lastAnnounceAt, lastAnnounceAt ← 지금.
    /// 그래서 하루 종일 "어제 브리핑 이후" 를 기준으로 본다.
    /// </summary>
    [JsonPropertyName("baselineIds")] public List<string>? BaselineIds { get; set; }
    [JsonPropertyName("announcedIds")] public List<string>? AnnouncedIds { get; set; }
    [JsonPropertyName("lastAnnounceAt")] public DateTime? LastAnnounceAt { get; set; }
    [JsonPropertyName("prevAnnounceAt")] public DateTime? PrevAnnounceAt { get; set; }

    /// <summary>브리핑 시각에 기준을 민다. 조회가 끝난 뒤 RecordAnnounced 로 이번 id 를 남긴다.</summary>
    public void RotateForAnnounce(DateTime nowUtc)
    {
        BaselineIds = AnnouncedIds;
        PrevAnnounceAt = LastAnnounceAt;
        LastAnnounceAt = nowUtc;
    }

    /// <summary>댓글·변경을 찾을 시작 시각. 브리핑 기록이 없으면 하루 전.</summary>
    public DateTime NoticeSinceUtc(DateTime nowUtc) => PrevAnnounceAt ?? nowUtc.AddDays(-1);

    private static string Path_ => System.IO.Path.Combine(AppConfig.Directory, "state.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(Path_)) ?? new AppState();
        }
        catch (Exception ex)
        {
            AppLog.Error("state.json 읽기 실패, 기본값으로 시작", ex);
        }
        return new AppState();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(AppConfig.Directory);
        File.WriteAllText(Path_, JsonSerializer.Serialize(this));
    }
}
