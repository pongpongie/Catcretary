using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Catcretary.Models;

public enum SectionKind { New, Overdue, Today, ThisWeek, InProgress, HighPriority, Other }

public sealed class BriefingItem : INotifyPropertyChanged
{
    public required string IssueId { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectIdentifier { get; init; }
    public required string ProjectName { get; init; }
    public required int SequenceId { get; init; }
    public required string Name { get; init; }
    public required string Priority { get; init; }
    // 날짜·상태는 캘린더·보드에서 끌어 옮길 때 응답을 기다리지 않고 먼저 바꿔 그린다(실패하면 되돌린다).
    public DateOnly? Due { get; set; }
    public DateOnly? StartDate { get; set; }
    public string StateId { get; set; } = "";
    public required string StateName { get; set; }
    public required string StateGroup { get; set; }
    public required string StateColor { get; set; }
    /// <summary>이 프로젝트의 completed 그룹 state id. 없으면 완료 처리 버튼을 막는다.</summary>
    public string? DoneStateId { get; init; }
    public IReadOnlyList<string> AssigneeIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LabelIds { get; init; } = Array.Empty<string>();
    /// <summary>담당자 표시 이름. 없으면 "담당 없음".</summary>
    public string AssigneeText { get; set; } = "담당 없음";
    public bool IsMine { get; init; }

    /// <summary>미완료(backlog/unstarted/started). state 를 모르는 항목(검색 결과 일부)도 미완료로 본다.</summary>
    public bool IsOpen => StateGroup is "backlog" or "unstarted" or "started" or "";
    /// <summary>지난 브리핑 이후 남이 남긴 댓글·변경. 비어 있으면 표시하지 않는다.</summary>
    public string NoticeText { get; set; } = "";

    public string Key => $"{ProjectIdentifier}-{SequenceId}";

    public string DueText
    {
        get
        {
            if (Due is null) return "마감 없음";
            var today = DateOnly.FromDateTime(DateTime.Today);
            var days = Due.Value.DayNumber - today.DayNumber;
            return days switch
            {
                < 0 => $"{Due:MM'/'dd} ({-days}일 지남)",
                0 => $"{Due:MM'/'dd} (오늘)",
                1 => $"{Due:MM'/'dd} (내일)",
                _ => $"{Due:MM'/'dd} ({days}일 남음)",
            };
        }
    }

    public bool IsOverdue => Due is not null && Due.Value < DateOnly.FromDateTime(DateTime.Today);
    public bool CanComplete => DoneStateId is not null;

    public string PriorityLabel => Priority switch
    {
        "urgent" => "긴급",
        "high" => "높음",
        "medium" => "보통",
        "low" => "낮음",
        _ => "",
    };

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    private bool _isDone;
    /// <summary>이번 세션에서 완료 처리한 항목. 목록에서 지우지 않고 취소선으로 남긴다.</summary>
    public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class BriefingSection
{
    public required SectionKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Accent { get; init; }
    public ObservableCollection<BriefingItem> Items { get; } = new();
    public string CountText => $"{Items.Count}건";
}

public sealed class ProjectSummary
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public int MineOpen { get; init; }
    public int TotalOpen { get; init; }
    public int MineOverdue { get; init; }
    public int MineDueThisWeek { get; init; }

    /// <summary>진행률 분모. 초안·취소를 뺀 전체 항목 수.</summary>
    public int TotalActive { get; init; }
    public int Completed { get; init; }
    /// <summary>진행 중인 cycle 요약. 없으면 빈 문자열.</summary>
    public string CycleText { get; init; } = "";

    public int ProgressPercent => TotalActive == 0 ? 0 : (int)Math.Round(100.0 * Completed / TotalActive);

    /// <summary>브리핑 머리에 쓰는 한 덩어리.</summary>
    public string Line =>
        $"{Identifier} 담당 {MineOpen}/전체 {TotalOpen}" + (MineOverdue > 0 ? $" (지연 {MineOverdue})" : "")
        + $" · 진행률 {ProgressPercent}%" + (CycleText.Length > 0 ? $" · {CycleText}" : "");

    public string MineText => $"{MineOpen}";
    public string TotalText => $"전체 {TotalOpen}";
    public string RiskText => MineOverdue > 0 ? $"지연 {MineOverdue}" : "지연 없음";
    public bool HasRisk => MineOverdue > 0;
}

public sealed class Briefing
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public List<BriefingSection> Sections { get; init; } = new();
    public List<ProjectSummary> Projects { get; init; } = new();
    public int MineOpenTotal { get; init; }
    public int MineOverdueTotal { get; init; }
    public int MineDueTodayTotal { get; init; }
    public int NewTotal { get; init; }
    public int NoticeTotal { get; init; }
    public List<CycleInfo> Cycles { get; init; } = new();

    /// <summary>"새로 배정 2건 · 새 소식 1건". 둘 다 없으면 빈 문자열.</summary>
    private string Extra => string.Join(" · ", new[]
    {
        NewTotal > 0 ? $"새로 배정 {NewTotal}건" : null,
        NoticeTotal > 0 ? $"새 소식 {NoticeTotal}건" : null,
    }.Where(x => x is not null));

    /// <summary>말풍선용. 고양이 말투.</summary>
    public string HeadlineCat =>
        MineOverdueTotal > 0
            ? $"마감 지난 게 {MineOverdueTotal}건, 오늘 마감 {MineDueTodayTotal}건이다냥"
            : MineDueTodayTotal > 0
                ? $"오늘 마감 {MineDueTodayTotal}건, 지연은 없다냥"
                : MineOpenTotal > 0
                    ? $"미완료 {MineOpenTotal}건, 오늘 마감은 없다냥"
                    : "할 일이 없다냥";

    public string HeadlineCatWithExtra => Extra.Length == 0 ? HeadlineCat : $"{HeadlineCat} ({Extra})";

    public string Headline =>
        MineOverdueTotal > 0
            ? $"마감 지난 항목 {MineOverdueTotal}건, 오늘 마감 {MineDueTodayTotal}건"
            : MineDueTodayTotal > 0
                ? $"오늘 마감 {MineDueTodayTotal}건, 지연 없음"
                : MineOpenTotal > 0
                    ? $"내 담당 미완료 {MineOpenTotal}건, 오늘 마감 없음"
                    : "내 담당 미완료 없음";

    public string HeadlineWithExtra => Extra.Length == 0 ? Headline : $"{Headline} · {Extra}";
}

/// <summary>오늘이 기간 안에 든 cycle. 위젯 띠와 브리핑 요약에 쓴다.</summary>
public sealed record CycleInfo(string ProjectIdentifier, string Name, DateOnly Start, DateOnly End, int Remaining, int Total)
{
    public string Range => $"{Start:M'/'d}~{End:M'/'d}";
    public string Summary => $"{Name} 남은 {Remaining}/{Total}건 ({Range})";
}

/// <summary>보드 빠른 추가의 입력값. 날짜·담당자·라벨은 비워 두면 보내지 않는다.</summary>
public sealed record NewItemDraft(
    string Name, string Priority, DateOnly? Start, DateOnly? Due,
    IReadOnlyList<string> AssigneeIds, IReadOnlyList<string> LabelIds);

/// <summary>빠른 추가에서 고를 수 있는 프로젝트 멤버와 라벨.</summary>
public sealed record CreateOptions(List<PlaneMember> Members, List<PlaneLabel> Labels);
