using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ClaudeUsageTray;

internal static class IconFactory
{
    public static Icon BuildRobotIcon() => BuildRobotIcon(64);

    /// <summary>
    /// Draws fresh at exactly the requested pixel size instead of scaling
    /// down a bigger bitmap — small sizes (16/18/20px, used for the taskbar
    /// button and title bar) blur into an indistinct dark blob when you
    /// downscale a 32px+ source, losing the visor/eyes/antenna entirely.
    /// </summary>
    public static Icon BuildRobotIcon(int size)
    {
        using var bmp = BuildRobotBitmap(size);
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    /// <summary>
    /// Writes a proper multi-resolution .ico (used for the exe/taskbar/shortcut
    /// icon) built from the exact same drawing as the tray icon, so every
    /// place Windows shows this app's icon looks identical.
    /// </summary>
    public static void SaveMultiResolutionIco(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (var size in sizes)
        {
            using var bmp = BuildRobotBitmap(size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }

        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        bw.Write((short)0); // reserved
        bw.Write((short)1); // type: icon
        bw.Write((short)sizes.Length);

        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var data = pngs[i];
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0); // color count
            bw.Write((byte)0); // reserved
            bw.Write((short)1); // planes
            bw.Write((short)32); // bits per pixel
            bw.Write(data.Length);
            bw.Write(offset);
            offset += data.Length;
        }

        foreach (var data in pngs) bw.Write(data);
    }

    private static Bitmap BuildRobotBitmap(int size)
    {
        var scale = size / 64f;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);

        const int baseSize = 64;

        // Antenna, drawn first so the head overlaps its base cleanly.
        using (var antennaPen = new Pen(Color.FromArgb(255, 122, 108, 214), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(antennaPen, baseSize / 2f, 12, baseSize / 2f, 2);
        }
        using (var glow = new SolidBrush(Color.FromArgb(70, 96, 230, 220)))
        {
            g.FillEllipse(glow, baseSize / 2f - 7, -5, 14, 14);
        }
        using (var tip = new SolidBrush(Color.FromArgb(255, 96, 230, 220)))
        {
            g.FillEllipse(tip, baseSize / 2f - 3.5f, -1.5f, 7, 7);
        }

        // Head: rounded square, violet-to-indigo diagonal gradient.
        var headRect = new Rectangle(7, 12, baseSize - 14, baseSize - 20);
        using (var headPath = RoundedRect(headRect, 16))
        using (var headBrush = new LinearGradientBrush(headRect, Color.FromArgb(255, 149, 129, 240), Color.FromArgb(255, 76, 63, 168), LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(headBrush, headPath);
        }

        // Small ear nubs for a friendlier silhouette.
        using (var earBrush = new SolidBrush(Color.FromArgb(255, 108, 92, 200)))
        {
            g.FillEllipse(earBrush, headRect.Left - 3, headRect.Top + 12, 7, 16);
            g.FillEllipse(earBrush, headRect.Right - 4, headRect.Top + 12, 7, 16);
        }

        // Visor: dark band across the face, holds the glowing eyes.
        var visorRect = new Rectangle(headRect.Left + 6, headRect.Top + 14, headRect.Width - 12, 20);
        using (var visorPath = RoundedRect(visorRect, 10))
        using (var visorBrush = new LinearGradientBrush(visorRect, Color.FromArgb(255, 34, 30, 64), Color.FromArgb(255, 20, 18, 42), LinearGradientMode.Vertical))
        {
            g.FillPath(visorBrush, visorPath);
        }

        // Eyes: soft glow behind a bright core, teal to match the antenna tip.
        DrawEye(g, visorRect.Left + 9, visorRect.Top + visorRect.Height / 2f);
        DrawEye(g, visorRect.Right - 9, visorRect.Top + visorRect.Height / 2f);

        return bmp;
    }

    private static void DrawEye(Graphics g, float cx, float cy)
    {
        using var glow = new SolidBrush(Color.FromArgb(90, 96, 230, 220));
        g.FillEllipse(glow, cx - 6, cy - 6, 12, 12);
        using var core = new SolidBrush(Color.FromArgb(255, 120, 240, 225));
        g.FillEllipse(core, cx - 3, cy - 3, 6, 6);
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
