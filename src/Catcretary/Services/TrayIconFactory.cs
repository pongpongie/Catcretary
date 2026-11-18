using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Catcretary.Services;

/// <summary>
/// 트레이 아이콘. Assets/pet/app.ico (tools/make-icon.ps1 로 스프라이트에서 생성) 가 있으면 그것을,
/// 없으면 코드로 그린 도형을 쓴다. alert 이면 우하단에 빨간 점을 얹는다.
/// </summary>
public static class TrayIconFactory
{
    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "pet", "app.ico");

    public static Icon Create(bool alert)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            if (File.Exists(IconPath))
            {
                using var icon = new Icon(IconPath, 32, 32);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawIcon(icon, new Rectangle(0, 0, 32, 32));
            }
            else
            {
                DrawFallback(g);
            }

            if (alert)
            {
                using var dot = new SolidBrush(Color.FromArgb(220, 38, 38));
                using var ring = new Pen(Color.White, 2f);
                g.FillEllipse(dot, 19, 19, 12, 12);
                g.DrawEllipse(ring, 19, 19, 12, 12);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void DrawFallback(Graphics g)
    {
        using var brush = new SolidBrush(Color.FromArgb(37, 99, 235));
        using var path = RoundedRect(new Rectangle(2, 2, 28, 28), 8);
        g.FillPath(brush, path);
        using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { new PointF(9, 16.5f), new PointF(14, 21.5f), new PointF(23, 10.5f) });
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
