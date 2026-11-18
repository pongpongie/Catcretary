using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Catcretary.Models;

namespace Catcretary.Services;

/// <summary>
/// Plane self-hosted REST v1 클라이언트 (서버 1.4.2 기준, 작업 항목은 work-items 경로).
/// 이 빌드에는 workspace 전체 PQL 조회가 없어서, 프로젝트별로 조회한 뒤 앱에서 필터한다.
/// API 키는 분당 60회로 제한되므로 states 는 프로세스 안에서 캐시하고, 보내는 호출 수를 프로세스 전체에서 센다.
/// </summary>
public sealed class PlaneClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _workspace;

    /// <summary>states 는 거의 바뀌지 않는다. 수동 새로고침 때 ClearCache 로 비운다.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, List<PlaneState> States)> StateCache = new();
    private static readonly TimeSpan StateCacheTtl = TimeSpan.FromHours(6);

    /// <summary>브리핑에 필요한 필드만 받는다. 생략하면 description_html 까지 전부 온다.</summary>
    private const string IssueFields =
        "id,name,sequence_id,priority,target_date,start_date,project,state,assignees,labels,is_draft,updated_at,updated_by";

    public PlaneClient(AppConfig config)
    {
        _workspace = config.WorkspaceSlug;
        _http = new HttpClient { BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
    }

    private string Root => $"api/v1/workspaces/{_workspace}";

    public static void ClearCache() => StateCache.Clear();

    // ── 조회 ─────────────────────────────────────────────────

    /// <summary>API 키 주인. 내 담당을 가릴 id 를 이메일 대조 없이 얻는다.</summary>
    public async Task<PlaneMember> GetMeAsync(CancellationToken ct = default) =>
        await GetAsync<PlaneMember>("api/v1/users/me/", ct) ?? throw new InvalidOperationException("users/me 응답이 비어 있습니다");

    public async Task<List<PlaneProject>> GetProjectsAsync(CancellationToken ct = default) =>
        await GetAllPagesAsync<PlaneProject>($"{Root}/projects/", ct);

    public async Task<List<PlaneState>> GetStatesAsync(string projectId, CancellationToken ct = default)
    {
        if (StateCache.TryGetValue(projectId, out var hit) && DateTime.UtcNow - hit.At < StateCacheTtl)
            return hit.States;
        var states = await GetAllPagesAsync<PlaneState>($"{Root}/projects/{projectId}/states/", ct);
        StateCache[projectId] = (DateTime.UtcNow, states);
        return states;
    }

    public async Task<List<PlaneIssue>> GetIssuesAsync(string projectId, CancellationToken ct = default) =>
        await GetAllPagesAsync<PlaneIssue>($"{Root}/projects/{projectId}/work-items/", ct, $"fields={IssueFields}");

    /// <summary>"SHOP2-3" 같은 식별자로 한 항목. 없으면 404 PlaneApiException.</summary>
    public async Task<PlaneIssue> GetIssueByKeyAsync(string projectIdentifier, int sequenceId, CancellationToken ct = default) =>
        await GetAsync<PlaneIssue>($"{Root}/work-items/{projectIdentifier}-{sequenceId}/?fields={IssueFields}", ct)
        ?? throw new InvalidOperationException("항목 응답이 비어 있습니다");

    /// <summary>워크스페이스 멤버. 이 엔드포인트만 배열을 그대로 돌려준다.</summary>
    public async Task<List<PlaneMember>> GetMembersAsync(CancellationToken ct = default)
    {
        var members = await GetAsync<List<PlaneMember>>($"{Root}/members/", ct);
        return members ?? new List<PlaneMember>();
    }

    /// <summary>프로젝트 멤버. role 없이 사용자 정보 배열만 온다.</summary>
    public async Task<List<PlaneMember>> GetProjectMembersAsync(string projectId, CancellationToken ct = default) =>
        await GetAsync<List<PlaneMember>>($"{Root}/projects/{projectId}/project-members/", ct) ?? new List<PlaneMember>();

    public async Task<List<PlaneLabel>> GetLabelsAsync(string projectId, CancellationToken ct = default) =>
        await GetAllPagesAsync<PlaneLabel>($"{Root}/projects/{projectId}/labels/", ct);

    /// <summary>오늘이 기간 안에 든 cycle. 페이지 봉투 없이 배열로 온다.</summary>
    public async Task<List<PlaneCycle>> GetCurrentCyclesAsync(string projectId, CancellationToken ct = default) =>
        await GetAsync<List<PlaneCycle>>($"{Root}/projects/{projectId}/cycles/?cycle_view=current", ct) ?? new List<PlaneCycle>();

    /// <summary>최근 댓글 몇 개(최신순).</summary>
    public async Task<List<PlaneComment>> GetRecentCommentsAsync(string projectId, string issueId, int count, CancellationToken ct = default)
    {
        var page = await GetAsync<Paged<PlaneComment>>(
            $"{Root}/projects/{projectId}/work-items/{issueId}/comments/?per_page={count}&fields=id,actor,created_at,comment_stripped", ct);
        return page?.Results ?? new List<PlaneComment>();
    }

    /// <summary>이름·번호·프로젝트 identifier 부분 일치. 내가 멤버인 프로젝트만 나온다.</summary>
    public async Task<List<PlaneSearchHit>> SearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var res = await GetAsync<PlaneSearchResponse>(
            $"{Root}/work-items/search/?search={Uri.EscapeDataString(query)}&workspace_search=true&limit={limit}", ct);
        return res?.Issues ?? new List<PlaneSearchHit>();
    }

    // ── 쓰기 ─────────────────────────────────────────────────

    public async Task UpdateStateAsync(string projectId, string issueId, string stateId, CancellationToken ct = default)
    {
        using var res = await SendJsonAsync(HttpMethod.Patch, $"{Root}/projects/{projectId}/work-items/{issueId}/", new { state = stateId }, ct);
    }

    /// <summary>캘린더에서 끌어 옮긴 날짜. 값이 있는 필드만 보낸다(null 을 보내면 지워지므로).</summary>
    public async Task UpdateDatesAsync(string projectId, string issueId, DateOnly? startDate, DateOnly? targetDate, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>();
        if (startDate is { } s) body["start_date"] = s.ToString("yyyy-MM-dd");
        if (targetDate is { } t) body["target_date"] = t.ToString("yyyy-MM-dd");
        if (body.Count == 0) return;
        using var res = await SendJsonAsync(HttpMethod.Patch, $"{Root}/projects/{projectId}/work-items/{issueId}/", body, ct);
    }

    /// <summary>
    /// 보드에서 고친 필드만 보낸다. 값이 null 이면 그 필드를 지운다(날짜). 응답은 고친 뒤의 항목.
    /// </summary>
    public async Task<PlaneIssue?> UpdateIssueAsync(string projectId, string issueId, IReadOnlyDictionary<string, object?> changes, CancellationToken ct = default)
    {
        using var res = await SendJsonAsync(HttpMethod.Patch, $"{Root}/projects/{projectId}/work-items/{issueId}/", changes, ct);
        return await res.Content.ReadFromJsonAsync<PlaneIssue>(cancellationToken: ct);
    }

    public async Task<PlaneIssue?> CreateIssueAsync(
        string projectId, string name, IReadOnlyList<string> assigneeIds, string? startDate, string? targetDate, string priority,
        IReadOnlyList<string> labelIds, string? stateId = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["priority"] = priority,
        };
        // 없으면 서버가 프로젝트 기본 state 로 만든다. 보드의 열에서 추가할 때만 지정한다.
        if (!string.IsNullOrWhiteSpace(stateId)) payload["state"] = stateId;
        if (assigneeIds.Count > 0) payload["assignees"] = assigneeIds;
        if (!string.IsNullOrWhiteSpace(startDate)) payload["start_date"] = startDate;
        if (!string.IsNullOrWhiteSpace(targetDate)) payload["target_date"] = targetDate;
        if (labelIds.Count > 0) payload["labels"] = labelIds;

        using var res = await SendJsonAsync(HttpMethod.Post, $"{Root}/projects/{projectId}/work-items/", payload, ct);
        return await res.Content.ReadFromJsonAsync<PlaneIssue>(cancellationToken: ct);
    }

    /// <summary>평문을 줄 단위 문단으로 바꿔 comment_html 로 보낸다.</summary>
    public async Task AddCommentAsync(string projectId, string issueId, string text, CancellationToken ct = default)
    {
        var html = string.Concat(text.Split('\n').Select(line => $"<p>{WebUtility.HtmlEncode(line.TrimEnd('\r'))}</p>"));
        using var res = await SendJsonAsync(HttpMethod.Post, $"{Root}/projects/{projectId}/work-items/{issueId}/comments/",
            new { comment_html = html }, ct);
    }

    public async Task AddToCycleAsync(string projectId, string cycleId, string issueId, CancellationToken ct = default)
    {
        using var res = await SendJsonAsync(HttpMethod.Post, $"{Root}/projects/{projectId}/cycles/{cycleId}/cycle-issues/",
            new { issues = new[] { issueId } }, ct);
    }

    /// <summary>만든 사람은 서버가 프로젝트 Admin 으로 넣고, 기본 state 5개도 서버가 만든다.</summary>
    public async Task<PlaneProject> CreateProjectAsync(string name, string identifier, CancellationToken ct = default)
    {
        using var res = await SendJsonAsync(HttpMethod.Post, $"{Root}/projects/", new { name, identifier }, ct);
        return await res.Content.ReadFromJsonAsync<PlaneProject>(cancellationToken: ct)
               ?? throw new InvalidOperationException("프로젝트 생성 응답이 비어 있습니다");
    }

    public async Task CreateStateAsync(string projectId, string name, string color, string group, CancellationToken ct = default)
    {
        using var res = await SendJsonAsync(HttpMethod.Post, $"{Root}/projects/{projectId}/states/", new { name, color, group }, ct);
        StateCache.TryRemove(projectId, out _);
    }

    public async Task CreateLabelAsync(string projectId, string name, string? color, CancellationToken ct = default)
    {
        using var res = await SendJsonAsync(HttpMethod.Post, $"{Root}/projects/{projectId}/labels/",
            color is null ? new { name } : new { name, color } as object, ct);
    }

    /// <summary>role: 20 Admin, 15 Member, 5 Guest.</summary>
    public async Task AddProjectMemberAsync(string projectId, string memberId, int role, CancellationToken ct = default)
    {
        using var res = await SendJsonAsync(HttpMethod.Post, $"{Root}/projects/{projectId}/project-members/",
            new { member = memberId, role }, ct);
    }

    // ── 공통 ─────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, path, null, ct);
        return await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct) =>
        SendAsync(method, path, JsonSerializer.Serialize(body), ct);

    /// <summary>호출 제한(429)에 걸리면 Retry-After 만큼(최대 60초) 기다렸다가 한 번 더 보낸다.</summary>
    private const int MaxRetryAfterSeconds = 60;

    // ── 호출 수 관리 ─────────────────────────────────────────
    // API 키 한도(분당 60회)를 프로세스 안의 모든 PlaneClient 가 나눠 쓴다.
    // 조회는 ReadLimit 까지만 쓰고 나머지는 쓰기 몫으로 남겨, 새로고침이 한도를 다 써도 등록·수정은 바로 나간다.
    private static readonly Queue<DateTime> Sent = new();
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private const int ReadLimit = 48;
    private const int WriteLimit = 58;

    /// <summary>지금 1분 창에서 조회에 더 쓸 수 있는 횟수. 댓글 확인처럼 미뤄도 되는 조회가 멈출 때 본다.</summary>
    public static int ReadsAvailable
    {
        get
        {
            lock (Sent)
            {
                Prune(DateTime.UtcNow);
                return Math.Max(0, ReadLimit - Sent.Count);
            }
        }
    }

    private static void Prune(DateTime now)
    {
        while (Sent.Count > 0 && now - Sent.Peek() >= Window) Sent.Dequeue();
    }

    /// <summary>한도 안이면 바로 기록하고 돌아간다. 넘으면 가장 오래된 호출이 창을 벗어날 때까지 기다린다.</summary>
    private static async Task AcquireAsync(bool write, CancellationToken ct)
    {
        var limit = write ? WriteLimit : ReadLimit;
        while (true)
        {
            TimeSpan wait;
            lock (Sent)
            {
                var now = DateTime.UtcNow;
                Prune(now);
                if (Sent.Count < limit)
                {
                    Sent.Enqueue(now);
                    return;
                }
                // limit 번째로 오래된 호출이 빠져야 자리가 난다. 쓰기는 조회 몫 위에 더 쌓일 수 있어 Peek 만으로는 부족하다.
                wait = Sent.ElementAt(Sent.Count - limit) + Window - now;
            }
            AppLog.Info($"호출 수 관리, {wait.TotalSeconds:0}초 대기 ({(write ? "쓰기" : "조회")})");
            await Task.Delay(wait + TimeSpan.FromMilliseconds(200), ct);
        }
    }

    /// <summary>실패하면 어느 요청이 어떤 코드로 막혔는지 담은 PlaneApiException 을 던진다.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await AcquireAsync(method != HttpMethod.Get, ct);
            // 요청 메시지는 한 번만 보낼 수 있어서 재시도마다 새로 만든다.
            using var req = new HttpRequestMessage(method, path)
            {
                Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
            };
            var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return res;

            using (res)
            {
                if (attempt == 0 && res.StatusCode == HttpStatusCode.TooManyRequests
                    && res.Headers.RetryAfter?.Delta is { } wait && wait.TotalSeconds <= MaxRetryAfterSeconds)
                {
                    AppLog.Info($"호출 제한, {wait.TotalSeconds:0}초 뒤 다시 시도: {method.Method} {path.Split('?')[0]}");
                    await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
                    continue;
                }
                var body = await res.Content.ReadAsStringAsync(ct);
                throw new PlaneApiException(method.Method, path, res.StatusCode, body);
            }
        }
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string path, CancellationToken ct, string? query = null)
    {
        var all = new List<T>();
        string? cursor = null;
        do
        {
            var url = $"{path}?per_page=100" + (query is null ? "" : "&" + query)
                      + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await GetAsync<Paged<T>>(url, ct);
            if (page is null) break;
            all.AddRange(page.Results);
            cursor = page.NextPageResults ? page.NextCursor : null;
        } while (cursor is not null && !ct.IsCancellationRequested);
        return all;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Plane 응답이 2xx 가 아닐 때. 메시지는 화면에 그대로 띄우므로 짧게, 본문은 로그용으로 따로 둔다.</summary>
public sealed class PlaneApiException : HttpRequestException
{
    private const int MaxBody = 500;

    public string Method { get; }
    public string Path { get; }
    public string Body { get; }

    public PlaneApiException(string method, string path, System.Net.HttpStatusCode status, string body)
        : base($"{method} {StripQuery(path)} → {(int)status} {status}", null, status)
    {
        Method = method;
        Path = path;
        Body = body.Length <= MaxBody ? body : body[..MaxBody] + "…";
    }

    /// <summary>화면 메시지가 fields 목록으로 길어지지 않게 쿼리는 뺀다. 로그의 Path 에는 남는다.</summary>
    private static string StripQuery(string path) => path.Split('?')[0];
}
