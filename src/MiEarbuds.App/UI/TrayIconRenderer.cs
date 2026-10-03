using System.Drawing;
using System.Drawing.Drawing2D;

namespace MiEarbuds.App.UI;

/// <summary>生成托盘电池数字图标。</summary>
public static class TrayIconRenderer
{
    public static Bitmap Render(int? percent)
    {
        var bmp = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        using var path = RoundedRect(new Rectangle(3, 3, 58, 58), 16);
        using (var bg = new SolidBrush(Color.FromArgb(255, 28, 28, 33)))
            g.FillPath(bg, path);
        using (var pen = new Pen(Color.FromArgb(255, 58, 58, 66), 2f))
            g.DrawPath(pen, path);

        string text = percent is >= 0 and <= 100 ? percent.Value.ToString() : "?";
        float size = percent is >= 0 and <= 100 && percent >= 100 ? 24f : 28f;
        using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var color = percent switch
        {
            >= 50 => Color.FromArgb(255, 0x6F, 0xBF, 0x73),
            >= 20 => Color.FromArgb(255, 0xD9, 0xA1, 0x3B),
            >= 0 => Color.FromArgb(255, 0xD9, 0x6A, 0x5B),
            _ => Color.FromArgb(255, 0x8F, 0x8F, 0x98),
        };
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new RectangleF(0, -2, 64, 64), fmt);
        return bmp;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
