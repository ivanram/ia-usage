using System.Drawing.Drawing2D;

namespace ClaudeUsageTray;

internal static class IconFactory
{
    public static Icon BuildRobotIcon()
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var headBrush = new LinearGradientBrush(
                new Rectangle(0, 10, size, size - 10),
                Color.FromArgb(255, 108, 122, 137),
                Color.FromArgb(255, 69, 82, 97),
                LinearGradientMode.Vertical);
            using var outlinePen = new Pen(Color.FromArgb(255, 45, 54, 64), 2f);

            // Antenna
            using (var antennaPen = new Pen(Color.FromArgb(255, 69, 82, 97), 3f))
            {
                g.DrawLine(antennaPen, size / 2f, 10, size / 2f, 2);
            }
            using (var antennaTip = new SolidBrush(Color.FromArgb(255, 79, 209, 197)))
            {
                g.FillEllipse(antennaTip, size / 2f - 4, -2, 8, 8);
            }

            // Head
            using (var headPath = RoundedRect(new Rectangle(6, 12, size - 12, size - 20), 14))
            {
                g.FillPath(headBrush, headPath);
                g.DrawPath(outlinePen, headPath);
            }

            // Eyes
            using var eyeBrush = new SolidBrush(Color.FromArgb(255, 79, 209, 197));
            using var eyeHighlight = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            g.FillEllipse(eyeBrush, 16, 26, 14, 16);
            g.FillEllipse(eyeBrush, size - 30, 26, 14, 16);
            g.FillEllipse(eyeHighlight, 19, 29, 4, 4);
            g.FillEllipse(eyeHighlight, size - 27, 29, 4, 4);

            // Mouth grille
            using var mouthPen = new Pen(Color.FromArgb(255, 45, 54, 64), 2.5f);
            for (var i = 0; i < 3; i++)
            {
                var x = 22 + i * 10;
                g.DrawLine(mouthPen, x, size - 18, x, size - 12);
            }
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
