using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Catcretary.Widget;

/// <summary>
/// 위젯 테마 색. 배경 투명도는 <see cref="Background"/> 의 알파에만 적용하고 글자는 그대로 둔다.
/// <see cref="Surface"/> 는 메뉴·드롭다운처럼 투명도와 무관하게 불투명해야 하는 팝업 배경,
/// <see cref="Hover"/> 는 메뉴·목록에서 마우스가 올라간 줄.
/// </summary>
public sealed record WidgetPalette(
    Color Background, Color Text, Color Muted, Color Grid,
    Color Sunday, Color Saturday, Color TodayFill, Color TodayRing, Color ItemFill, Color Danger,
    Color Surface, Color Hover)
{
    public static readonly WidgetPalette Light = new(
        Background: Hex("#FFFFFF"), Text: Hex("#1B1F24"), Muted: Hex("#6B7280"), Grid: Hex("#1F000000"),
        Sunday: Hex("#DC2626"), Saturday: Hex("#2563EB"), TodayFill: Hex("#1F2563EB"), TodayRing: Hex("#2563EB"),
        ItemFill: Hex("#0F000000"), Danger: Hex("#DC2626"),
        Surface: Hex("#FFFFFF"), Hover: Hex("#162563EB"));

    public static readonly WidgetPalette Dark = new(
        Background: Hex("#1E2127"), Text: Hex("#E6E8EB"), Muted: Hex("#9AA3AE"), Grid: Hex("#26FFFFFF"),
        Sunday: Hex("#F87171"), Saturday: Hex("#60A5FA"), TodayFill: Hex("#333B82F6"), TodayRing: Hex("#60A5FA"),
        ItemFill: Hex("#17FFFFFF"), Danger: Hex("#F87171"),
        Surface: Hex("#262A31"), Hover: Hex("#2E60A5FA"));

    /// <summary>"light" / "dark" / "system". system 은 Windows 앱 모드(밝게/어둡게)를 따른다.</summary>
    public static WidgetPalette For(string theme) => theme switch
    {
        "dark" => Dark,
        "system" => SystemUsesLightTheme() ? Light : Dark,
        _ => Light,
    };

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    /// <summary>
    /// 위젯 창이나 우클릭 메뉴의 Resources 에 테마 색을 넣는다. App.xaml 의 공용 스타일(메뉴·ComboBox 등)이
    /// 같은 키를 DynamicResource 로 읽으므로 이 사전이 있는 요소 아래에서는 이 테마로 그려진다.
    /// 템플릿 안 글자는 앱 전역 TextBlock 스타일(Text 키)을 따르므로 Text·TextMuted 도 덮는다.
    /// </summary>
    public void ApplyTo(ResourceDictionary res)
    {
        Set(res, "W.Text", Text);
        Set(res, "W.Muted", Muted);
        Set(res, "W.Grid", Grid);
        Set(res, "W.Sunday", Sunday);
        Set(res, "W.Saturday", Saturday);
        Set(res, "W.TodayFill", TodayFill);
        Set(res, "W.TodayRing", TodayRing);
        Set(res, "W.ItemFill", ItemFill);
        Set(res, "W.Danger", Danger);
        Set(res, "W.Surface", Surface);
        Set(res, "W.Hover", Hover);
        Set(res, "Text", Text);
        Set(res, "TextMuted", Muted);
    }

    private static void Set(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
