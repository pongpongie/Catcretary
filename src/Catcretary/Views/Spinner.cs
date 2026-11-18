using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Catcretary.Views;

/// <summary>새로고침 버튼 아이콘을 조회하는 동안 돌린다. 위젯·보드가 같이 쓴다.</summary>
internal static class Spinner
{
    private static readonly Duration Turn = new(TimeSpan.FromSeconds(1));

    public static void Set(UIElement icon, bool busy)
    {
        if (icon.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform();
            icon.RenderTransform = rotate;
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (busy)
        {
            if (rotate.HasAnimatedProperties) return; // 이미 도는 중이면 각도를 처음으로 되돌리지 않는다.
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, Turn) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = 0;
        }
    }
}
