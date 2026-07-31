using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Cursor position + monitor work area via raw Win32 calls, so the app
/// doesn't need a System.Windows.Forms reference (which collides on
/// Button/TextBox/ProgressBar/Application/Brush with WPF's own types the
/// moment both UseWPF and UseWindowsForms are enabled in the same project).
/// </summary>
internal static class NativeScreenHelper
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static POINT GetCursorPosition()
    {
        GetCursorPos(out var p);
        return p;
    }

    /// <summary>Work area (excludes taskbar) of the monitor under the given point, in physical pixels.</summary>
    public static RECT GetWorkAreaForPoint(POINT p)
    {
        var monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>
    /// Real on-screen rect (physical pixels) of a tray icon registered with the
    /// given GUID identifier. Used to detect genuine hover over the icon —
    /// TaskbarIcon.IsMouseOver doesn't track it reliably since the icon isn't
    /// an actual rendered WPF visual, just a native shell icon.
    /// </summary>
    public static bool TryGetTrayIconRect(Guid iconGuid, out RECT rect)
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = IntPtr.Zero,
            uID = 0,
            guidItem = iconGuid,
        };
        var hr = Shell_NotifyIconGetRect(ref id, out rect);
        return hr == 0;
    }

    public static bool Contains(RECT r, POINT p) =>
        p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}
