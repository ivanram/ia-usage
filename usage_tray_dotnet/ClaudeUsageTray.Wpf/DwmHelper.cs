using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

internal static class DwmHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void EnableRoundedCorners(IntPtr hwnd)
    {
        var pref = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { /* Best effort: only matters on Windows 11. */ }
    }

    public static void SetTitleBarDarkMode(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)); }
        catch { /* Best effort. */ }
    }

    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// WindowStyle="None" windows don't reliably pick up WPF's Icon property
    /// for their taskbar button/Alt-Tab thumbnail — handing them a flat
    /// BitmapSource (even one built from a real multi-resolution .ico) left
    /// the taskbar showing a generated letter-avatar instead. Sending the
    /// real native HICON straight to the window via WM_SETICON — the same
    /// mechanism Explorer itself relies on — is what actually sticks.
    /// </summary>
    public static void SetWindowIcon(IntPtr hwnd, IntPtr smallIcon, IntPtr bigIcon)
    {
        if (smallIcon != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, smallIcon);
        if (bigIcon != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, bigIcon);
    }
}
