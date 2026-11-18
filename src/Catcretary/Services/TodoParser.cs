using System.Globalization;
using System.Text.RegularExpressions;
using Catcretary.Models;

namespace Catcretary.Services;

public enum TodoIntent { CreateTodo, ListToday, ChangeState, Comment, OpenItem, Search, CreateProject }

/// <summary>사용자가 말한 문장에서 뽑아낸 명령 초안. 쓰기 명령은 확정 전까지 Plane 에 쓰지 않는다.</summary>
public sealed class TodoDraft
{
    public TodoIntent Intent { get; init; }

    // ── 등록 ──
    public PlaneProject? Project { get; set; }
    /// <summary>문장에 프로젝트가 없어 설정의 기본 프로젝트를 넣었다. 확인 말풍선에서 다른 프로젝트를 고를 수 있게 한다.</summary>
    public bool ProjectFromDefault { get; set; }
    public string Title { get; set; } = "";
    public DateOnly? StartDate { get; set; }
    public DateOnly? TargetDate { get; set; }
    public string Priority { get; set; } = "none";
    /// <summary>"#버그" 의 "버그". 등록할 때 프로젝트 라벨 이름과 대조한다.</summary>
    public List<string> Labels { get; } = new();
    /// <summary>진행 중인 cycle 에 넣기.</summary>
    public bool AddToCycle { get; set; }
    /// <summary>마감이 시작보다 앞서면 등록 버튼을 막는다. 프로젝트 버튼을 눌러 되묻기 문구가 지워져도 유지된다.</summary>
    public bool DatesValid => StartDate is not { } s || TargetDate is not { } t || t >= s;

    // ── 항목 명령 (SHOP2-3 완료 / 시작 / 취소 / 메모: … / 열어) ──
    public PlaneProject? KeyProject { get; init; }
    public int SequenceId { get; init; }
    /// <summary>completed / started / cancelled.</summary>
    public string? TargetGroup { get; init; }
    public string? CommentText { get; init; }
    public string IssueKey => KeyProject is null ? "" : $"{KeyProject.Identifier}-{SequenceId}";

    // ── 검색 ──
    public string? Query { get; init; }

    // ── 프로젝트 생성 ──
    public string NewProjectName { get; set; } = "";
    public string NewProjectIdentifier { get; set; } = "";
    public PlaneProject? Template { get; set; }
    public List<string> Aliases { get; } = new();

    /// <summary>비어 있지 않으면 확정 전에 되물어야 한다.</summary>
    public string? Clarification { get; set; }

    public string PriorityLabel => Priority switch
    {
        "urgent" => "긴급", "high" => "높음", "medium" => "보통", "low" => "낮음", _ => "없음",
    };

    public string TargetGroupLabel => TargetGroup switch
    {
        "completed" => "완료", "started" => "진행 중", "cancelled" => "취소", _ => "",
    };
}

/// <summary>
/// 규칙 기반 파서. LLM 없이 명령 형식·프로젝트 별칭·날짜 표현·우선순위 단어를 뽑고 나머지를 제목으로 쓴다.
/// 애매하면 추측하지 않고 Clarification 에 질문을 남긴다.
/// 판정 순서: 항목 명령(KEY-N) → 프로젝트 생성 → 검색 → 브리핑 → 할 일 등록.
/// </summary>
public sealed class TodoParser
{
    private readonly List<PlaneProject> _projects;
    private readonly List<PlaneProject> _allProjects;
    private readonly Dictionary<string, List<string>> _aliases;
    private readonly DateOnly _today;
    private readonly PlaneProject? _defaultProject;

    private static readonly CultureInfo Ko = new("ko-KR");

    /// <param name="projects">등록 대상 프로젝트.</param>
    /// <param name="allProjects">워크스페이스 전체. 항목 식별자·템플릿·identifier 중복 검사에 쓴다.</param>
    /// <param name="defaultProject">문장에 프로젝트가 없을 때 넣을 프로젝트. null 이면 되묻는다.</param>
    public TodoParser(List<PlaneProject> projects, List<PlaneProject> allProjects, Dictionary<string, List<string>> aliases, DateOnly today,
        PlaneProject? defaultProject = null)
    {
        _projects = projects;
        _allProjects = allProjects;
        _aliases = aliases;
        _today = today;
        _defaultProject = defaultProject;
    }

    public TodoDraft Parse(string input)
    {
        var text = input.Trim();

        if (TryParseItemCommand(text) is { } command) return command;
        if (TryParseCreateProject(text) is { } project) return project;
        if (TryParseSearch(text) is { } search) return search;

        if (IsListToday(text))
            return new TodoDraft { Intent = TodoIntent.ListToday };

        var draft = new TodoDraft { Intent = TodoIntent.CreateTodo };

        text = ExtractLabels(text, draft);
        text = ExtractProject(text, draft);
        text = ExtractCycle(text, draft);
        text = ExtractPriority(text, draft);
        text = ExtractDates(text, draft);
        draft.Title = CleanTitle(text);
        if (draft.Project is null && _defaultProject is not null)
        {
            draft.Project = _defaultProject;
            draft.ProjectFromDefault = true;
        }

        if (draft.Title.Length == 0)
            draft.Clarification = "뭘 등록할 거냥? 제목을 못 찾았다냥.";
        else if (draft.Project is null)
            draft.Clarification = "어느 프로젝트에 넣을까냥?";
        else if (draft.StartDate is { } s && draft.TargetDate is { } t && t < s)
            draft.Clarification = "마감이 시작보다 빠르다냥. 다시 말해 줄래냥?";

        return draft;
    }

    // ── 항목 명령 ────────────────────────────────────────────

    /// <summary>
    /// "SHOP2-3 완료", "shop2-3 시작", "SHOP2-3에 메모: 내용", "SHOP2-3 열어".
    /// 앞부분이 실제 프로젝트 identifier 일 때만 명령으로 본다. 아니면 일반 등록 문장이다.
    /// </summary>
    private TodoDraft? TryParseItemCommand(string text)
    {
        var m = Regex.Match(text, @"^([A-Za-z][A-Za-z0-9]{0,11})\s*-\s*(\d+)(?!\d)(.*)$", RegexOptions.Singleline);
        if (!m.Success) return null;

        var project = _allProjects.FirstOrDefault(p =>
            string.Equals(p.Identifier, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        if (project is null) return null;
        var seq = int.Parse(m.Groups[2].Value);

        var rest = Regex.Replace(m.Groups[3].Value, @"^\s*(에다가|에다|에는|에|을|를|은|는|이|가|의)?\s*[,，]?\s*", "");

        var comment = Regex.Match(rest,
            @"^(메모|댓글|코멘트|comment|노트)\s*(남겨|달아|추가|적어)?\s*(줘|해\s*줘)?\s*[:：]?\s*(?<body>.+)$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (comment.Success)
            return new TodoDraft
            {
                Intent = TodoIntent.Comment, KeyProject = project, SequenceId = seq,
                CommentText = comment.Groups["body"].Value.Trim(),
            };

        // 짧은 꼬리말만 상태 명령으로 본다. 긴 문장은 등록 문장일 가능성이 높다.
        if (rest.Length <= 16)
        {
            var group =
                Regex.IsMatch(rest, @"(취소|cancel|드롭)", RegexOptions.IgnoreCase) ? "cancelled"
                : Regex.IsMatch(rest, @"(완료|끝|done|닫아|close)", RegexOptions.IgnoreCase) ? "completed"
                : Regex.IsMatch(rest, @"(시작|진행|착수|start)", RegexOptions.IgnoreCase) ? "started"
                : null;
            if (group is not null)
                return new TodoDraft { Intent = TodoIntent.ChangeState, KeyProject = project, SequenceId = seq, TargetGroup = group };

            if (rest.Trim().Length == 0 || Regex.IsMatch(rest, @"(열어|열기|보여|open)", RegexOptions.IgnoreCase))
                return new TodoDraft { Intent = TodoIntent.OpenItem, KeyProject = project, SequenceId = seq };
        }
        return null;
    }

    // ── 검색 ─────────────────────────────────────────────────

    private static TodoDraft? TryParseSearch(string text)
    {
        var m = Regex.Match(text, @"^(검색|찾기|search|find)\s*[:：]?\s*(?<q>.+)$", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(text, @"^(?<q>.+?)\s*(을|를)?\s*(검색\s*해\s*줘|검색해|검색|찾아\s*줘|찾아\s*봐|찾아)\s*[.!?]?$");
        if (!m.Success) return null;

        var q = m.Groups["q"].Value.Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();
        return q.Length == 0 ? null : new TodoDraft { Intent = TodoIntent.Search, Query = q };
    }

    // ── 프로젝트 생성 ────────────────────────────────────────

    /// <summary>
    /// "새 프로젝트 BLOG3 만들어줘, 식별자 BLOG3, 템플릿 SHOP2, 별칭 블로그 BLOG-III".
    /// 쉼표로 나눈 조각마다 식별자 / 템플릿 / 별칭 / 이름 중 하나로 본다.
    /// </summary>
    private TodoDraft? TryParseCreateProject(string text)
    {
        if (!Regex.IsMatch(text, @"(새|신규)\s*프로젝트|프로젝트\s*(를|을)?\s*(하나\s*)?(만들|생성)")) return null;

        var draft = new TodoDraft { Intent = TodoIntent.CreateProject };
        var name = "";
        string? missingTemplate = null;

        foreach (var segment in text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match m;
            if ((m = Regex.Match(segment, @"(식별자|identifier|키)\s*(은|는|:|：)?\s*(?<v>[A-Za-z0-9]+)", RegexOptions.IgnoreCase)).Success)
                draft.NewProjectIdentifier = m.Groups["v"].Value.ToUpperInvariant();
            else if ((m = Regex.Match(segment, @"(템플릿|template)\s*(은|는|:|：)?\s*(?<v>.+)$", RegexOptions.IgnoreCase)).Success
                     || (m = Regex.Match(segment, @"^(?<v>.+?)\s*(을|를)?\s*(템플릿으로|처럼|복사)")).Success)
            {
                draft.Template = FindProject(m.Groups["v"].Value, _allProjects);
                if (draft.Template is null) missingTemplate = m.Groups["v"].Value.Trim();
            }
            else if ((m = Regex.Match(segment, @"별칭\s*(은|는|:|：)?\s*(?<v>.+)$")).Success)
                draft.Aliases.AddRange(Regex.Split(m.Groups["v"].Value, @"[\s/]+").Where(a => a.Length > 0));
            else if ((m = Regex.Match(segment, @"이름\s*(은|는|:|：)?\s*(?<v>.+)$")).Success)
                name = m.Groups["v"].Value;
            else if (segment.Contains("프로젝트") && name.Length == 0)
                name = Regex.Replace(segment,
                    @"(새|신규)\s*(?=프로젝트)|프로젝트\s*(를|을|로)?\s*|하나\s*|(만들어\s*줘|만들어|만들기|만들자|생성\s*해\s*줘|생성해|생성)\s*[.!]*", " ");
        }

        draft.NewProjectName = Regex.Replace(name, @"\s{2,}", " ").Trim().Trim('"', '\'', '“', '”', '‘', '’', '[', ']').Trim();
        if (draft.NewProjectIdentifier.Length == 0)
            draft.NewProjectIdentifier = new string(draft.NewProjectName.ToUpperInvariant()
                .Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').Take(PlaneRules.IdentifierMaxLength).ToArray());

        draft.Clarification = ValidateNewProject(draft)
            ?? (missingTemplate is null ? null : $"템플릿 프로젝트 '{missingTemplate}' 를 못 찾았다냥");
        return draft;
    }

    private string? ValidateNewProject(TodoDraft d)
    {
        if (d.NewProjectName.Length == 0) return "프로젝트 이름을 못 찾았다냥. 예: 새 프로젝트 BLOG3 만들어줘";
        if (PlaneRules.HasForbiddenChar(d.NewProjectName)) return "이름에 Plane 이 막는 기호가 있다냥 (- . ( ) 등)";
        if (d.NewProjectIdentifier.Length == 0) return "식별자를 정해 달라냥. 예: …, 식별자 BLOG3 (영문·숫자 12자 이내)";
        if (!Regex.IsMatch(d.NewProjectIdentifier, $@"^[A-Z0-9]{{1,{PlaneRules.IdentifierMaxLength}}}$"))
            return "식별자는 영문 대문자·숫자 12자 이내여야 한다냥";
        if (_allProjects.Any(p => string.Equals(p.Identifier, d.NewProjectIdentifier, StringComparison.OrdinalIgnoreCase)))
            return $"식별자 {d.NewProjectIdentifier} 는 이미 있다냥";
        if (_allProjects.Any(p => string.Equals(p.Name.Trim(), d.NewProjectName, StringComparison.OrdinalIgnoreCase)))
            return $"이름이 같은 프로젝트가 이미 있다냥";
        return null;
    }

    // ── 브리핑 ───────────────────────────────────────────────

    private static bool IsListToday(string text)
    {
        if (text.Length > 24) return false;
        return Regex.IsMatch(text, @"(오늘|지금)\s*(뭐|무엇|할\s*일|일정|브리핑)|할\s*일\s*(뭐|목록|보여|알려)|브리핑|todo\s*(list|보여)", RegexOptions.IgnoreCase);
    }

    // ── 등록 문장 ────────────────────────────────────────────

    /// <summary>"#버그 #UI" → 라벨 이름. 다른 추출보다 먼저 지워 프로젝트·우선순위 단어와 섞이지 않게 한다.</summary>
    private static string ExtractLabels(string text, TodoDraft draft)
    {
        foreach (Match m in Regex.Matches(text, @"#([^\s#,，]+)"))
            if (!draft.Labels.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                draft.Labels.Add(m.Groups[1].Value);
        return Regex.Replace(text, @"#[^\s#,，]+", " ").Trim();
    }

    /// <summary>"이번 사이클", "스프린트에 넣어". 제목의 "사이클 타임" 같은 말과 섞이지 않게 앞뒤 말이 있어야 한다.</summary>
    private static string ExtractCycle(string text, TodoDraft draft)
    {
        var m = Regex.Match(text,
            @"[,，]?\s*(?:(?:이번|현재)\s*(?:사이클|cycle|스프린트)(?:에|로)?|(?:사이클|cycle|스프린트)(?:에|로))\s*(?:넣어|추가|포함)?\s*(?:줘)?\s*[,，]?",
            RegexOptions.IgnoreCase);
        if (!m.Success) return text;
        draft.AddToCycle = true;
        return Cut(text, m);
    }

    /// <summary>identifier, 이름, 별칭 중 가장 긴 것이 문장에 있으면 그 프로젝트. 붙은 조사도 같이 지운다.</summary>
    private string ExtractProject(string text, TodoDraft draft)
    {
        var candidates = Candidates(_projects);
        var best = BestCandidate(text, candidates);
        if (best is null) return text;

        draft.Project = best.Value.Project;

        // 같은 프로젝트를 가리키는 토큰이 여러 개 있어도("SHOP 쇼핑몰 ...") 전부 지운다.
        foreach (var (_, token) in candidates.Where(c => c.Project == best.Value.Project).OrderByDescending(c => c.Token.Length))
        {
            var tok = Normalize(token);
            if (tok.Length < 2) continue;
            // 원문에서 토큰(공백·하이픈·괄호 무시)과 뒤따르는 조사를 제거한다.
            var pattern = string.Concat(tok.Select(ch => Regex.Escape(ch.ToString()) + @"[\s\-\[\]_]*"));
            text = Regex.Replace(text, pattern + @"(프로젝트)?(에서|에다|에는|에|의|은|는|이|가|로|으로)?\s*[,，]?", " ", RegexOptions.IgnoreCase);
        }
        return text.Trim();
    }

    private List<(PlaneProject Project, string Token)> Candidates(IEnumerable<PlaneProject> projects)
    {
        var candidates = new List<(PlaneProject Project, string Token)>();
        foreach (var p in projects)
        {
            candidates.Add((p, p.Identifier));
            candidates.Add((p, p.Name));
            if (_aliases.TryGetValue(p.Identifier, out var aliases))
                candidates.AddRange(aliases.Select(a => (p, a)));
        }
        return candidates;
    }

    private static (PlaneProject Project, string Token)? BestCandidate(string text, List<(PlaneProject Project, string Token)> candidates)
    {
        var norm = Normalize(text);
        foreach (var c in candidates.OrderByDescending(c => c.Token.Length))
        {
            var tok = Normalize(c.Token);
            if (tok.Length < 2) continue;
            if (norm.Contains(tok, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    private PlaneProject? FindProject(string text, IEnumerable<PlaneProject> projects) =>
        BestCandidate(text, Candidates(projects))?.Project;

    private static string Normalize(string s) => Regex.Replace(s, @"[\s\-\[\]_()]", "");

    private static string ExtractPriority(string text, TodoDraft draft)
    {
        var rules = new (string Pattern, string Priority)[]
        {
            (@"(긴급|급함|급해|급하게|급히|ASAP|당장)", "urgent"),
            (@"(중요|우선|높음|먼저)", "high"),
            (@"(보통|중간)", "medium"),
            (@"(낮음|여유|천천히|나중에|언젠가)", "low"),
        };
        foreach (var (pattern, priority) in rules)
        {
            var m = Regex.Match(text, @"[,，]?\s*" + pattern + @"(함|해|하게|히|하다|임|이야|요)?\s*[,，.!]?", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            draft.Priority = priority;
            return Cut(text, m);
        }
        return text;
    }

    /// <summary>
    /// 시작일("월요일부터", "10/7~")을 먼저 떼고 나서 마감일을 찾는다.
    /// "월요일부터 금요일까지" 처럼 요일만 말해 마감이 시작보다 앞으로 계산되면 한 주 뒤로 민다.
    /// </summary>
    private string ExtractDates(string text, TodoDraft draft)
    {
        text = ExtractDate(text, @"\s*(에서부터|부터|~|∼)", d => draft.StartDate = d);
        text = ExtractDate(text, @"\s*(까지|에|로|안으로)?", d => draft.TargetDate = d);

        if (draft.StartDate is { } s && draft.TargetDate is { } t)
            while (t < s && s.DayNumber - t.DayNumber < 7) draft.TargetDate = t = t.AddDays(7);
        return text;
    }

    private string ExtractDate(string text, string suffix, Action<DateOnly> set)
    {
        var rules = new (string Pattern, Func<Match, DateOnly?> Resolve)[]
        {
            (@"(\d{4})[-./](\d{1,2})[-./](\d{1,2})", m => Make(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))),
            (@"(\d{1,2})월\s*(\d{1,2})일", m => NextOccurrence(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))),
            (@"(\d{1,2})[/.](\d{1,2})(?!\d)", m => NextOccurrence(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))),
            (@"(\d+)\s*일\s*(후|뒤|안에|이내)", m => _today.AddDays(int.Parse(m.Groups[1].Value))),
            (@"(\d+)\s*주\s*(후|뒤|안에|이내)", m => _today.AddDays(7 * int.Parse(m.Groups[1].Value))),
            (@"다다음\s*주\s*([월화수목금토일])요일", m => WeekdayInWeek(m.Groups[1].Value, 2)),
            (@"다음\s*주\s*([월화수목금토일])요일", m => WeekdayInWeek(m.Groups[1].Value, 1)),
            (@"이번\s*주\s*([월화수목금토일])요일", m => WeekdayInWeek(m.Groups[1].Value, 0)),
            (@"(?<![가-힣])([월화수목금토일])요일", m => NextWeekday(m.Groups[1].Value)),
            (@"다음\s*주(?!\s*[월화수목금토일])", _ => WeekdayInWeek("금", 1)),
            (@"이번\s*주(?!\s*[월화수목금토일])", _ => WeekdayInWeek("금", 0)),
            (@"(이번\s*달|월)\s*말", _ => new DateOnly(_today.Year, _today.Month, DateTime.DaysInMonth(_today.Year, _today.Month))),
            (@"글피", _ => _today.AddDays(3)),
            (@"모레", _ => _today.AddDays(2)),
            (@"내일", _ => _today.AddDays(1)),
            (@"오늘", _ => _today),
        };

        foreach (var (pattern, resolve) in rules)
        {
            var m = Regex.Match(text, @"[,，]?\s*" + pattern + suffix + @"\s*[,，]?");
            if (!m.Success) continue;
            var date = resolve(m);
            if (date is null) continue;
            set(date.Value);
            return Cut(text, m);
        }
        return text;
    }

    /// <summary>찾은 표현을 공백으로 바꾼다. 그냥 지우면 앞뒤 단어가 붙어 다음 규칙의 단어 경계 검사가 틀어진다.</summary>
    private static string Cut(string text, Match m) => (text[..m.Index] + " " + text[(m.Index + m.Length)..]).Trim();

    private static DateOnly? Make(int y, int mo, int d)
    {
        try { return new DateOnly(y, mo, d); } catch { return null; }
    }

    private DateOnly? NextOccurrence(int month, int day)
    {
        var d = Make(_today.Year, month, day);
        if (d is null) return null;
        return d.Value < _today ? Make(_today.Year + 1, month, day) : d;
    }

    private static DayOfWeek ToDow(string ko) => ko switch
    {
        "월" => DayOfWeek.Monday, "화" => DayOfWeek.Tuesday, "수" => DayOfWeek.Wednesday,
        "목" => DayOfWeek.Thursday, "금" => DayOfWeek.Friday, "토" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday,
    };

    /// <summary>주는 월요일 시작. weekOffset 0 = 이번 주.</summary>
    private DateOnly WeekdayInWeek(string ko, int weekOffset)
    {
        var dow = ToDow(ko);
        var monday = _today.AddDays(-(((int)_today.DayOfWeek + 6) % 7));
        var target = monday.AddDays(((int)dow + 6) % 7 + 7 * weekOffset);
        return target;
    }

    /// <summary>"금요일" 처럼 주 지정이 없으면 오늘 이후 가장 가까운 그 요일(오늘 포함).</summary>
    private DateOnly NextWeekday(string ko)
    {
        var dow = ToDow(ko);
        var diff = ((int)dow - (int)_today.DayOfWeek + 7) % 7;
        return _today.AddDays(diff);
    }

    private static string CleanTitle(string text)
    {
        var t = text;
        t = Regex.Replace(t, @"^\s*(할\s*일|투두|todo)\s*[:：]?\s*", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*(등록|추가|넣어|올려|적어|기록)?\s*(해\s*줘|해줘|해주세요|해|하자|할\s*것|하기|해야\s*함|해야\s*해|해야\s*됨|해야\s*돼)?\s*[.!。]*\s*$", "");
        t = Regex.Replace(t, @"^[\s,，:：\-]+|[\s,，:：\-]+$", "");
        t = Regex.Replace(t, @"\s{2,}", " ");
        return t.Trim();
    }

    /// <summary>ko-KR 의 날짜 구분자는 '-' 라서 '/' 를 리터럴로 고정한다.</summary>
    public static string FormatDate(DateOnly d) => d.ToString("M'/'d (ddd)", Ko);
}
