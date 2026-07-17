using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LubanDesktopPet;

/// <summary>
/// Resolves the usable work area of the monitor that currently contains a WPF window.
/// Native monitor coordinates are physical pixels, while the returned rectangle uses
/// the same device-independent coordinate space as <see cref="Window.Left"/> and
/// <see cref="Window.Top"/>.
/// </summary>
internal static class MonitorWorkArea
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    internal static Rect GetForWindow(Window window)
    {
        if (window is null)
        {
            return GetFallbackWorkArea();
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return GetFallbackWorkArea();
            }

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return GetFallbackWorkArea();
            }

            var monitorInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };

            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return GetFallbackWorkArea();
            }

            return TryConvertToWindowDips(window, monitorInfo.WorkArea, out var workArea)
                ? workArea
                : GetFallbackWorkArea();
        }
        catch
        {
            // A monitor/DPI lookup must never interrupt the desktop pet. This also
            // covers calls made while the HWND or its presentation source is changing.
            return GetFallbackWorkArea();
        }
    }

    private static bool TryConvertToWindowDips(Window window, NativeRect nativeRect, out Rect workArea)
    {
        workArea = Rect.Empty;

        if (nativeRect.Right <= nativeRect.Left || nativeRect.Bottom <= nativeRect.Top)
        {
            return false;
        }

        var windowLeft = window.Left;
        var windowTop = window.Top;
        if (!double.IsFinite(windowLeft) || !double.IsFinite(windowTop))
        {
            return false;
        }

        // PointFromScreen performs the physical-pixel to DIP conversion using this
        // window's current presentation source/DPI. Adding the window position turns
        // the two local points back into absolute WPF coordinates, including negative
        // coordinates used by monitors placed left or above the primary display.
        var localTopLeft = window.PointFromScreen(new Point(nativeRect.Left, nativeRect.Top));
        var localBottomRight = window.PointFromScreen(new Point(nativeRect.Right, nativeRect.Bottom));

        var left = windowLeft + localTopLeft.X;
        var top = windowTop + localTopLeft.Y;
        var right = windowLeft + localBottomRight.X;
        var bottom = windowTop + localBottomRight.Y;

        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right <= left || bottom <= top)
        {
            return false;
        }

        workArea = new Rect(left, top, right - left, bottom - top);
        return true;
    }

    private static Rect GetFallbackWorkArea()
    {
        try
        {
            var workArea = SystemParameters.WorkArea;
            if (!workArea.IsEmpty &&
                double.IsFinite(workArea.X) && double.IsFinite(workArea.Y) &&
                double.IsFinite(workArea.Width) && double.IsFinite(workArea.Height) &&
                workArea.Width > 0 && workArea.Height > 0)
            {
                return workArea;
            }
        }
        catch
        {
            // Fall through to a harmless last-resort rectangle.
        }

        return new Rect(0, 0, 1920, 1080);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
