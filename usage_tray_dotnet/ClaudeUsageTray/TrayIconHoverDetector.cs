using System.Reflection;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Fires <see cref="HoverThresholdReached"/> when the cursor stays over the
/// given NotifyIcon for at least <see cref="ThresholdMs"/>. WinForms exposes
/// no native tray-icon hover event, so this polls the icon's actual screen
/// rectangle (via Shell_NotifyIconGetRect) against the cursor position.
/// </summary>
public sealed class TrayIconHoverDetector
{
    private const int ThresholdMs = 2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    private static readonly FieldInfo? WindowField = typeof(NotifyIcon).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? IdField = typeof(NotifyIcon).GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 200 };
    private int _hoverMs;
    private bool _triggeredThisHover;

    public event Action? HoverThresholdReached;

    public TrayIconHoverDetector(NotifyIcon icon)
    {
        _icon = icon;
        _poll.Tick += (s, e) => Poll();
        _poll.Start();
    }

    private void Poll()
    {
        if (!TryGetIconRect(out var rect)) return;

        var p = Cursor.Position;
        const int margin = 2;
        var inside = p.X >= rect.Left - margin && p.X <= rect.Right + margin
                     && p.Y >= rect.Top - margin && p.Y <= rect.Bottom + margin;

        if (inside)
        {
            _hoverMs += _poll.Interval;
            if (_hoverMs >= ThresholdMs && !_triggeredThisHover)
            {
                _triggeredThisHover = true;
                HoverThresholdReached?.Invoke();
            }
        }
        else
        {
            _hoverMs = 0;
            _triggeredThisHover = false;
        }
    }

    private bool TryGetIconRect(out RECT rect)
    {
        rect = default;
        try
        {
            if (WindowField?.GetValue(_icon) is not NativeWindow window || window.Handle == IntPtr.Zero) return false;
            if (IdField?.GetValue(_icon) is not uint id) return false;

            var identifier = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = window.Handle,
                uID = id,
            };
            return Shell_NotifyIconGetRect(ref identifier, out rect) == 0;
        }
        catch
        {
            return false;
        }
    }
}
