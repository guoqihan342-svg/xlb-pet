using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LubanDesktopPet;

/// <summary>
/// Keeps an owned tool window beside a WPF visual in physical screen pixels.
/// Using native coordinates avoids stale-DPI offsets when the owner crosses monitors.
/// </summary>
internal static class OwnedWindowPositioner
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    internal static bool TryPosition(
        FrameworkElement anchor,
        Window child,
        out bool childIsOnLeft)
    {
        childIsOnLeft = true;
        try
        {
            if (!anchor.IsLoaded || !child.IsVisible)
            {
                return false;
            }

            var childHandle = new WindowInteropHelper(child).Handle;
            if (childHandle == IntPtr.Zero || !GetWindowRect(childHandle, out var childRect))
            {
                return false;
            }

            var anchorTopLeft = anchor.PointToScreen(new Point(0, 0));
            var anchorBottomRight = anchor.PointToScreen(
                new Point(anchor.ActualWidth, anchor.ActualHeight));
            var anchorCenter = new NativePoint
            {
                X = (int)Math.Round((anchorTopLeft.X + anchorBottomRight.X) / 2),
                Y = (int)Math.Round((anchorTopLeft.Y + anchorBottomRight.Y) / 2)
            };
            var monitor = MonitorFromPoint(anchorCenter, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            {
                return false;
            }

            var childWidth = childRect.Right - childRect.Left;
            var childHeight = childRect.Bottom - childRect.Top;
            var anchorLeft = (int)Math.Round(anchorTopLeft.X);
            var anchorRight = (int)Math.Round(anchorBottomRight.X);
            var anchorBottom = (int)Math.Round(anchorBottomRight.Y);
            childIsOnLeft = anchorLeft - childWidth >= monitorInfo.WorkArea.Left;

            var desiredLeft = childIsOnLeft
                ? anchorLeft - childWidth
                : anchorRight;
            var desiredTop = anchorBottom - childHeight;
            var maximumLeft = Math.Max(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Right - childWidth);
            var maximumTop = Math.Max(
                monitorInfo.WorkArea.Top,
                monitorInfo.WorkArea.Bottom - childHeight);
            desiredLeft = Math.Clamp(desiredLeft, monitorInfo.WorkArea.Left, maximumLeft);
            desiredTop = Math.Clamp(desiredTop, monitorInfo.WorkArea.Top, maximumTop);

            return SetWindowPos(
                childHandle,
                IntPtr.Zero,
                desiredLeft,
                desiredTop,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
