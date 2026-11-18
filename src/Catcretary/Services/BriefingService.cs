using System.Net;
using Catcretary.Models;

namespace Catcretary.Services;

/// <summary>Plane 에서 긁어온 원본과, 그것으로 만든 브리핑.</summary>
/// <param name="Projects">브리핑·등록 대상(멤버, 미보관, 제외 목록 밖).</param>
/// <param name="AllProjects">워크스페이스의 모든 프로젝트. identifier 중복 검사와 템플릿 찾기에 쓴다.</param>
/// <param name="MineIds">내 담당 미완료 항목 id. 다음 브리핑의 "새로 배정" 기준이 된다.</param>
/// <param name="NoticeKeys">이번에 찾은 댓글·변경 각각의 키. 앞 조회와 비교해 새로 생긴 것만 알린다.</param>
/// <param name="AllItems">대상 프로젝트의 모든 항목(담당자·상태 무관, 초안 제외). 캘린더 "전체" 범위와 보드가 쓴다.
/// 내 담당 브리핑 항목은 이 목록과 같은 객체라, 한쪽에서 바꾼 날짜·상태가 다른 쪽에도 보인다.</param>
/// <param name="States">프로젝트 id → state 목록(sequence 순).</param>
/// <param name="MemberNames">워크스페이스 멤버 id → 표시 이름. 새로고침 전에 바로 넣는 새 항목의 담당자 이름에 쓴다.</param>
public sealed record BriefingResult(
    Briefing Briefing,
    List<PlaneProject> Projects,
    List<PlaneProject> AllProjects,
    string MyUserId,
    HashSet<string> MineIds,
    HashSet<string> NoticeKeys,
    List<BriefingItem> AllItems,
    Dictionary<string, List<PlaneState>> States,
    Dictionary<string, string> MemberNames);

/// <param name="Baseline">지난 브리핑 때의 내 담당 id. null 이면 "새로 배정" 섹션을 만들지 않는다.</param>
/// <param name="NoticeSinceUtc">이 시각 이후 남이 남긴 댓글·변경을 알린다. null 이면 찾지 않는다.</param>
public sealed record BriefingOptions(IReadOnlySet<string>? Baseline, DateTime? NoticeSinceUtc);

/// <summary>
/// 프로젝트별 조회 → 내 담당·미완료 필터 → 분류.
/// 한 항목은 한 섹션에만 들어간다(분류 우선순위: 새로 배정 &gt; 지남 &gt; 오늘 &gt; 이번 주 &gt; 진행 중 &gt; 우선순위 &gt; 그 외).
/// 내 담당이 아닌 항목도 모두 AllItems 로 모은다(추가 호출 없음, 이미 받은 목록).
/// 호출 수: me·members·projects 3 + 프로젝트마다 issues·cycles 2 (states 는 캐시) + 댓글 확인 최대 MaxCommentChecks.
/// </summary>
public sealed class BriefingService
{
    /// <summary>API 키 제한(분당 60회) 안에 들도록 댓글 확인은 마감이 가까운 항목부터 이만큼만.</summary>
    private const int MaxCommentChecks = 20;
    private const int CommentsPerIssue = 5;
    /// <summary>조회 몫이 이만큼 남지 않으면 댓글 확인을 멈춘다. 보드의 프로젝트 다시 받기 같은 조회가 쓸 자리.</summary>
    private const int CommentReserve = 8;

    private readonly AppConfig _config;

    public BriefingService(AppConfig config) => _config = config;

    public async Task<BriefingResult> BuildAsync(BriefingOptions options, CancellationToken ct = default)
    {
        using var client = new PlaneClient(_config);

        var me = await client.GetMeAsync(ct);
        var members = await client.GetMembersAsync(ct);
        var names = members.ToDictionary(m => m.Id, m => m.DisplayName);

        var allProjects = await client.GetProjectsAsync(ct);
        var projects = allProjects
            .Where(p => p.ArchivedAt is null)
            .Where(p => p.IsMember)
            .Where(p => !_config.ExcludedProjects.Contains(p.Identifier, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekEnd = today.AddDays(7);

        var summaries = new List<ProjectSummary>();
        var cycles = new List<CycleInfo>();
        var mine = new List<(BriefingItem Item, PlaneIssue Issue)>();
        var allItems = new List<BriefingItem>();
        var statesByProject = new Dictionary<string, List<PlaneState>>();

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();

            var states = (await client.GetStatesAsync(project.Id, ct)).OrderBy(s => s.Sequence).ToList();
            statesByProject[project.Id] = states;
            var byState = states.ToDictionary(s => s.Id);
            var doneStateId = DoneState(states)?.Id;

            var issues = (await client.GetIssuesAsync(project.Id, ct)).Where(i => !i.IsDraft).ToList();
            var groupOf = (PlaneIssue i) => byState.TryGetValue(i.State, out var s) ? s.Group : "";

            var open = issues.Where(i => byState.TryGetValue(i.State, out var s) && s.IsOpen).ToList();
            var mineHere = open.Where(i => i.Assignees.Contains(me.Id)).ToList();

            foreach (var issue in issues)
            {
                var item = ToItem(issue, project, byState.GetValueOrDefault(issue.State), doneStateId, names, me.Id);
                allItems.Add(item);
                if (item.IsMine && item.IsOpen && byState.ContainsKey(issue.State)) mine.Add((item, issue));
            }

            var cycleText = "";
            try
            {
                foreach (var c in await client.GetCurrentCyclesAsync(project.Id, ct))
                {
                    if (c.Start is not { } s || c.End is not { } e) continue;
                    var info = new CycleInfo(project.Identifier, c.Name, s, e, c.Remaining, c.TotalIssues);
                    cycles.Add(info);
                    if (cycleText.Length == 0) cycleText = info.Summary;
                }
            }
            catch (PlaneApiException ex)
            {
                // cycle 을 끈 프로젝트 등. 브리핑 전체를 실패시키지 않는다.
                AppLog.Error($"{project.Identifier} cycle 조회 실패", ex);
            }

            summaries.Add(new ProjectSummary
            {
                Identifier = project.Identifier,
                Name = project.Name,
                MineOpen = mineHere.Count,
                TotalOpen = open.Count,
                MineOverdue = mineHere.Count(i => i.Due is not null && i.Due.Value < today),
                MineDueThisWeek = mineHere.Count(i => i.Due is not null && i.Due.Value >= today && i.Due.Value <= weekEnd),
                TotalActive = issues.Count(i => groupOf(i) != "cancelled"),
                Completed = issues.Count(i => groupOf(i) == "completed"),
                CycleText = cycleText,
            });
        }

        mine = mine.OrderBy(m => m.Item.Due ?? DateOnly.MaxValue).ThenBy(m => PriorityRank(m.Item.Priority)).ToList();

        var noticeKeys = new HashSet<string>();
        var notices = options.NoticeSinceUtc is { } since
            ? await CollectNoticesAsync(client, mine, me.Id, names, since, noticeKeys, ct)
            : new Dictionary<string, string>();

        var sections = CreateSections();
        var newCount = 0;
        foreach (var (item, _) in mine)
        {
            if (notices.TryGetValue(item.IssueId, out var notice)) item.NoticeText = notice;
            var isNew = options.Baseline is not null && !options.Baseline.Contains(item.IssueId);
            if (isNew) newCount++;
            var kind = isNew ? SectionKind.New : Classify(item, today, weekEnd);
            sections.First(s => s.Kind == kind).Items.Add(item);
        }

        var items = sections.SelectMany(s => s.Items).ToList();
        var briefing = new Briefing
        {
            Sections = sections.Where(s => s.Items.Count > 0).ToList(),
            Projects = summaries.OrderByDescending(p => p.MineOverdue).ThenByDescending(p => p.MineOpen).ToList(),
            MineOpenTotal = items.Count,
            MineOverdueTotal = items.Count(i => i.IsOverdue),
            MineDueTodayTotal = items.Count(i => i.Due == today),
            NewTotal = newCount,
            NoticeTotal = notices.Count,
            Cycles = cycles,
        };

        return new BriefingResult(briefing, projects, allProjects, me.Id,
            mine.Select(m => m.Item.IssueId).ToHashSet(), noticeKeys, allItems, statesByProject, names);
    }

    /// <summary>
    /// 남이 바꾼 항목은 목록의 updated_at/updated_by 로 공짜로 안다. 댓글은 항목 updated_at 을 건드리지 않아서
    /// 항목마다 최근 댓글을 따로 본다. 실패해도 브리핑은 계속하고, 429(호출 제한)면 남은 확인을 멈춘다.
    /// </summary>
    private static async Task<Dictionary<string, string>> CollectNoticesAsync(
        PlaneClient client, List<(BriefingItem Item, PlaneIssue Issue)> mine, string myId,
        Dictionary<string, string> names, DateTime sinceUtc, HashSet<string> keys, CancellationToken ct)
    {
        var parts = new Dictionary<string, List<string>>();
        void Add(string issueId, string text) =>
            (parts.TryGetValue(issueId, out var l) ? l : parts[issueId] = new List<string>()).Add(text);
        string Name(string? id) => id is not null && names.TryGetValue(id, out var n) && n.Length > 0 ? n : "누군가";

        foreach (var (item, issue) in mine.Take(MaxCommentChecks))
        {
            // 댓글 확인은 다음 새로고침으로 미뤄도 된다. 조회 몫을 다 써서 기다리느니 여기서 멈춘다.
            if (PlaneClient.ReadsAvailable < CommentReserve)
            {
                AppLog.Info("호출 수가 한도에 가까워 남은 댓글 확인은 다음 새로고침에서 한다");
                break;
            }
            try
            {
                var comments = (await client.GetRecentCommentsAsync(item.ProjectId, item.IssueId, CommentsPerIssue, ct))
                    .Where(c => c.Actor != myId && c.CreatedAt.ToUniversalTime() > sinceUtc)
                    .ToList();
                foreach (var c in comments) keys.Add($"c:{c.Id}");
                if (comments.Count > 0)
                {
                    var latest = comments[0];
                    var preview = (latest.Text ?? "").Trim().ReplaceLineEndings(" ");
                    if (preview.Length > 40) preview = preview[..40] + "…";
                    Add(item.IssueId, comments.Count == 1
                        ? $"새 댓글 · {Name(latest.Actor)}: {preview}"
                        : $"새 댓글 {comments.Count}개 · {Name(latest.Actor)}: {preview}");
                }
            }
            catch (PlaneApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                AppLog.Error("댓글 확인 중 호출 제한, 남은 항목은 다음 새로고침에서 확인", ex);
                break;
            }
            catch (PlaneApiException ex)
            {
                AppLog.Error($"{item.Key} 댓글 확인 실패", ex);
            }
        }

        foreach (var (item, issue) in mine)
        {
            if (issue.UpdatedBy is null || issue.UpdatedBy == myId) continue;
            if (issue.UpdatedAt is not { } at || at.ToUniversalTime() <= sinceUtc) continue;
            keys.Add($"u:{issue.Id}:{at.ToUniversalTime().Ticks}");
            Add(item.IssueId, $"{Name(issue.UpdatedBy)} 님이 수정");
        }

        return parts.ToDictionary(p => p.Key, p => string.Join(" · ", p.Value));
    }

    /// <summary>
    /// 목록 한 줄. 이미 완료·취소된 항목(검색 결과)은 완료 버튼을 막는다.
    /// state 를 모르면(목록에 없는 state id) 빈 이름과 회색으로 둔다.
    /// </summary>
    public static BriefingItem ToItem(PlaneIssue issue, PlaneProject project, PlaneState? state, string? doneStateId,
        IReadOnlyDictionary<string, string>? names = null, string? myId = null) => new()
    {
        IssueId = issue.Id,
        ProjectId = project.Id,
        ProjectIdentifier = project.Identifier,
        ProjectName = project.Name,
        SequenceId = issue.SequenceId,
        Name = issue.Name,
        Priority = issue.Priority,
        Due = issue.Due,
        StartDate = issue.Start,
        StateId = issue.State,
        StateName = state?.Name ?? "",
        StateGroup = state?.Group ?? "",
        StateColor = state?.Color ?? "#9CA3AF",
        DoneStateId = state is null || state.IsOpen ? doneStateId : null,
        AssigneeIds = issue.Assignees,
        LabelIds = issue.Labels,
        AssigneeText = issue.Assignees.Count == 0 ? "담당 없음"
            : string.Join(", ", issue.Assignees.Select(a => names is not null && names.TryGetValue(a, out var n) && n.Length > 0 ? n : "?")),
        IsMine = myId is not null && issue.Assignees.Contains(myId),
    };

    /// <summary>completed 그룹 state 중 첫 번째. 완료 처리에 쓴다.</summary>
    public static PlaneState? DoneState(List<PlaneState> states) => FirstInGroup(states, "completed");

    /// <summary>그룹의 첫 state. "시작" 은 started, "취소" 는 cancelled 그룹으로 보낸다.</summary>
    public static PlaneState? FirstInGroup(List<PlaneState> states, string group) => states.FirstOrDefault(s => s.Group == group);

    private static SectionKind Classify(BriefingItem item, DateOnly today, DateOnly weekEnd)
    {
        if (item.Due is { } due)
        {
            if (due < today) return SectionKind.Overdue;
            if (due == today) return SectionKind.Today;
            if (due <= weekEnd) return SectionKind.ThisWeek;
        }
        if (item.StateGroup == "started") return SectionKind.InProgress;
        if (item.Priority is "urgent" or "high") return SectionKind.HighPriority;
        return SectionKind.Other;
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "urgent" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4,
    };

    private static List<BriefingSection> CreateSections() => new()
    {
        new BriefingSection { Kind = SectionKind.New,          Title = "새로 배정",   Accent = "#059669" },
        new BriefingSection { Kind = SectionKind.Overdue,      Title = "마감 지남",   Accent = "#DC2626" },
        new BriefingSection { Kind = SectionKind.Today,        Title = "오늘 마감",   Accent = "#EA580C" },
        new BriefingSection { Kind = SectionKind.ThisWeek,     Title = "이번 주 마감", Accent = "#CA8A04" },
        new BriefingSection { Kind = SectionKind.InProgress,   Title = "진행 중",     Accent = "#2563EB" },
        new BriefingSection { Kind = SectionKind.HighPriority, Title = "우선순위 높음", Accent = "#7C3AED" },
        new BriefingSection { Kind = SectionKind.Other,        Title = "그 외 미완료", Accent = "#6B7280" },
    };
}
