using System.Runtime.InteropServices;
using System.Windows.Media;

namespace ClaudeUsageTray;

internal static class DwmHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void EnableRoundedCorners(IntPtr hwnd)
    {
        var pref = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { /* Best effort: only matters on Windows 11. */ }
    }

    /// <summary>
    /// DWM rounds the corners of the window's real HWND rect, not whatever
    /// WPF happens to be painting inside it — fine when those two coincide
    /// (PopupWindow's Blur mode, whose margin is 0), but wrong for a window
    /// that pads itself out for a soft-shadow margin (PopupWindow's Standard
    /// mode): DWM would round the corners of that much bigger, mostly-
    /// invisible padded rect instead of the actual visible card, which
    /// showed up as a faint rectangular halo/box around the real content.
    /// The preference also persists on the HWND across resizes, so a window
    /// reused between the two modes (as PopupWindow is) needs this called
    /// explicitly on the Standard side too — leaving corners un-rounded
    /// doesn't happen by default once Blur mode has rounded it once.
    /// </summary>
    public static void DisableRoundedCorners(IntPtr hwnd)
    {
        var pref = DWMWCP_DONOTROUND;
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

    private enum AccentState
    {
        Disabled = 0,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Frosted-glass background behind the window, via the same
    /// undocumented-but-industry-standard composition API every WPF
    /// "acrylic" implementation relies on — the public DWM blur API
    /// (DwmEnableBlurBehindWindow) was effectively neutered starting
    /// Windows 8 and no longer produces a visible blur. Windows exposes no
    /// way to vary the actual Gaussian blur radius, so "blur strength" is
    /// simulated via <paramref name="tintAlpha"/> instead: more tint covers
    /// more of the blur (reads as less blurred), less tint lets more of the
    /// blurred desktop show through (reads as more blurred) — see
    /// PopupWindow.ApplyThemeColors for the percent-to-alpha mapping.
    /// </summary>
    public static void SetAcrylicBlur(IntPtr hwnd, Color tint, byte tintAlpha)
    {
        try
        {
            SetAccent(hwnd, new AccentPolicy
            {
                AccentState = AccentState.EnableAcrylicBlurBehind,
                GradientColor = (tintAlpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R,
            });
        }
        catch { /* Best effort: undocumented API, may vanish on a future Windows build. */ }
    }

    public static void DisableBlur(IntPtr hwnd)
    {
        try { SetAccent(hwnd, new AccentPolicy { AccentState = AccentState.Disabled }); }
        catch { /* Best effort. */ }
    }

    private static void SetAccent(IntPtr hwnd, AccentPolicy accent)
    {
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.AccentPolicy,
                SizeOfData = size,
                Data = ptr,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
