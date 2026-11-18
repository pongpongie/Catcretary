using System.Globalization;

namespace Catcretary.Widget;

/// <summary>
/// 음력 표기와 한국 공휴일. <see cref="KoreanLunisolarCalendar"/> 기준이라 지원 범위(918~2050년) 밖은 음력·음력 공휴일이 없다.
/// 선거일·임시공휴일은 계산할 수 없으므로 설정의 extraHolidays 로 넣는다.
/// </summary>
public static class KoreanCalendar
{
    private static readonly KoreanLunisolarCalendar Lunar = new();
    private static readonly Dictionary<int, Dictionary<DateOnly, string>> Cache = new();

    public readonly record struct LunarDate(int Month, int Day, bool IsLeap)
    {
        /// <summary>칸 구석에 붙이는 짧은 표기. 예: "8.7", "윤6.1".</summary>
        public string Short => $"{(IsLeap ? "윤" : "")}{Month}.{Day}";
        public string Long => $"음력 {(IsLeap ? "윤" : "")}{Month}월 {Day}일";
    }

    public static LunarDate? ToLunar(DateOnly date)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        if (dt < Lunar.MinSupportedDateTime || dt > Lunar.MaxSupportedDateTime) return null;

        var year = Lunar.GetYear(dt);
        var month = Lunar.GetMonth(dt);
        var day = Lunar.GetDayOfMonth(dt);
        // GetMonth 는 윤달을 포함해 1~13 으로 센다. GetLeapMonth 가 5 면 5번째 달이 윤4월이다.
        var leap = Lunar.GetLeapMonth(year);
        var isLeap = leap > 0 && month == leap;
        if (leap > 0 && month >= leap) month--;
        return new LunarDate(month, day, isLeap);
    }

    /// <summary>음력(윤달 아닌 달) → 양력. 지원 범위 밖이면 null.</summary>
    private static DateOnly? FromLunar(int year, int month, int day)
    {
        try
        {
            var leap = Lunar.GetLeapMonth(year);
            var index = leap > 0 && month >= leap ? month + 1 : month;
            return DateOnly.FromDateTime(Lunar.ToDateTime(year, index, day, 0, 0, 0, 0));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string? HolidayName(DateOnly date, IReadOnlyDictionary<string, string>? extra = null)
    {
        if (extra is not null && extra.TryGetValue(date.ToString("yyyy-MM-dd"), out var custom)) return custom;
        return For(date.Year).TryGetValue(date, out var name) ? name : null;
    }

    private static Dictionary<DateOnly, string> For(int year)
    {
        if (Cache.TryGetValue(year, out var cached)) return cached;

        var map = new Dictionary<DateOnly, string>();
        void Add(DateOnly d, string name) => map[d] = map.TryGetValue(d, out var old) ? $"{old}·{name}" : name;

        // (날짜, 이름, 토·일이면 대체공휴일인가)
        var singles = new List<(DateOnly Date, string Name, bool SubstituteOnWeekend)>
        {
            (new(year, 1, 1), "신정", false),
            (new(year, 3, 1), "삼일절", year >= 2022),
            (new(year, 5, 5), "어린이날", year >= 2014),
            (new(year, 6, 6), "현충일", false),
            (new(year, 8, 15), "광복절", year >= 2021),
            (new(year, 10, 3), "개천절", year >= 2021),
            (new(year, 10, 9), "한글날", year >= 2021),
            (new(year, 12, 25), "성탄절", year >= 2023),
        };
        if (FromLunar(year, 4, 8) is { } buddha) singles.Add((buddha, "부처님오신날", year >= 2023));

        foreach (var s in singles) Add(s.Date, s.Name);

        // 대체공휴일 기준: (이 날 다음부터 찾는다)
        var anchors = new List<DateOnly>();

        // 어린이날은 다른 공휴일과 겹쳐도 대체된다(2014~). 나머지는 토·일만.
        foreach (var s in singles.Where(s => s.SubstituteOnWeekend))
        {
            var overlapped = s.Name == "어린이날" && map[s.Date].Contains('·');
            if (IsWeekend(s.Date) || overlapped) anchors.Add(s.Date);
        }

        // 설날·추석 연휴: 사흘 중 일요일이거나 다른 공휴일과 겹치는 날 수만큼, 연휴 다음 첫 평일 비공휴일로 대체(2014~).
        AddLunarBlock(1, 1, "설날");
        AddLunarBlock(8, 15, "추석");

        void AddLunarBlock(int month, int day, string name)
        {
            if (FromLunar(year, month, day) is not { } mid) return;
            var days = new[] { mid.AddDays(-1), mid, mid.AddDays(1) };
            var hits = days.Count(d => d.DayOfWeek == DayOfWeek.Sunday || map.ContainsKey(d));
            Add(days[0], $"{name} 연휴");
            Add(days[1], name);
            Add(days[2], $"{name} 연휴");
            if (year >= 2014)
                for (var i = 0; i < hits; i++) anchors.Add(days[2]);
        }

        foreach (var anchor in anchors.OrderBy(d => d))
        {
            var d = anchor.AddDays(1);
            while (IsWeekend(d) || map.ContainsKey(d)) d = d.AddDays(1);
            Add(d, "대체공휴일");
        }

        Cache[year] = map;
        return map;
    }

    private static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
