using System.Text.Json.Serialization;

namespace Catcretary.Models;

/// <summary>Plane REST v1 의 커서 페이지네이션 응답 봉투.</summary>
public sealed class Paged<T>
{
    [JsonPropertyName("results")] public List<T> Results { get; set; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
    [JsonPropertyName("next_page_results")] public bool NextPageResults { get; set; }
}

public sealed class PlaneProject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("archived_at")] public DateTime? ArchivedAt { get; set; }
    /// <summary>목록은 워크스페이스의 모든 공개 프로젝트를 주지만, 하위 조회는 멤버만 된다(아니면 403).</summary>
    [JsonPropertyName("is_member")] public bool IsMember { get; set; } = true;
}

/// <summary>프로젝트 생성 규칙. Plane 의 FORBIDDEN_IDENTIFIER_CHARS_PATTERN 은 이름에도 적용된다.</summary>
public static class PlaneRules
{
    public const string ForbiddenChars = "&+,:;$^}{*=?@#|'<>.()%!-";
    public const int IdentifierMaxLength = 12;

    public static bool HasForbiddenChar(string s) => s.IndexOfAny(ForbiddenChars.ToCharArray()) >= 0;
}

public sealed class PlaneState
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>backlog / unstarted / started / completed / cancelled</summary>
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#9CA3AF";
    /// <summary>프로젝트 안 표시 순서. 보드 열 순서에 쓴다.</summary>
    [JsonPropertyName("sequence")] public double Sequence { get; set; }

    public bool IsOpen => Group is "backlog" or "unstarted" or "started";
}

public sealed class PlaneMember
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
}

public sealed class PlaneIssue
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sequence_id")] public int SequenceId { get; set; }
    [JsonPropertyName("priority")] public string Priority { get; set; } = "none";
    [JsonPropertyName("target_date")] public string? TargetDate { get; set; }
    [JsonPropertyName("start_date")] public string? StartDate { get; set; }
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("assignees")] public List<string> Assignees { get; set; } = new();
    [JsonPropertyName("labels")] public List<string> Labels { get; set; } = new();
    [JsonPropertyName("is_draft")] public bool IsDraft { get; set; }
    [JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
    [JsonPropertyName("updated_by")] public string? UpdatedBy { get; set; }

    /// <summary>target_date 는 "YYYY-MM-DD" 문자열이다. 파싱 실패 시 null.</summary>
    public DateOnly? Due =>
        DateOnly.TryParse(TargetDate, out var d) ? d : null;

    public DateOnly? Start =>
        DateOnly.TryParse(StartDate, out var d) ? d : null;
}

public sealed class PlaneLabel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string? Color { get; set; }
}

/// <summary>
/// cycle. 날짜는 DateTime 으로 오는데 시간대 변환을 거치면 하루씩 밀릴 수 있어 앞 10자(날짜)만 쓴다.
/// 건수는 목록 조회의 annotate 값이다.
/// </summary>
public sealed class PlaneCycle
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("start_date")] public string? StartDate { get; set; }
    [JsonPropertyName("end_date")] public string? EndDate { get; set; }
    [JsonPropertyName("total_issues")] public int TotalIssues { get; set; }
    [JsonPropertyName("completed_issues")] public int CompletedIssues { get; set; }
    [JsonPropertyName("cancelled_issues")] public int CancelledIssues { get; set; }

    public DateOnly? Start => ParseDate(StartDate);
    public DateOnly? End => ParseDate(EndDate);
    public int Remaining => Math.Max(0, TotalIssues - CompletedIssues - CancelledIssues);

    private static DateOnly? ParseDate(string? s) =>
        s is { Length: >= 10 } && DateOnly.TryParse(s[..10], out var d) ? d : null;
}

public sealed class PlaneComment
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("actor")] public string? Actor { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("comment_stripped")] public string? Text { get; set; }
}

/// <summary>work-items/search 결과 한 줄. state 는 오지 않는다.</summary>
public sealed class PlaneSearchHit
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sequence_id")] public int SequenceId { get; set; }
    [JsonPropertyName("project__identifier")] public string ProjectIdentifier { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
}

public sealed class PlaneSearchResponse
{
    [JsonPropertyName("issues")] public List<PlaneSearchHit> Issues { get; set; } = new();
}
