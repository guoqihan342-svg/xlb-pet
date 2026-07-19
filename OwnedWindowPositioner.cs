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
    private static readonly uint MonitorInfoSize = (uint)Marshal.SizeOf<MonitorInfo>();

    internal sealed class PositionCache
    {
        internal readonly WindowInteropHelper _childInteropHelper;
        internal IntPtr _childHandle;
        internal NativeRect _monitorArea;
        internal NativeRect _workArea;
        internal int _childWidth;
        internal int _childHeight;
        internal int _lastLeft;
        internal int _lastTop;
        internal bool _hasMonitorGeometry;
        internal bool _hasChildGeometry;
        internal bool _hasLastPosition;

        internal PositionCache(Window child)
        {
            _childInteropHelper = new WindowInteropHelper(child);
        }

        internal void InvalidateGeometry()
        {
            _hasMonitorGeometry = false;
            _hasChildGeometry = false;
            _hasLastPosition = false;
        }
    }

    internal static bool TryPosition(
        FrameworkElement anchor,
        Window child,
        PositionCache cache,
        out bool childIsOnLeft,
        bool? preferredChildIsOnLeft = null)
    {
        childIsOnLeft = true;
        try
        {
            if (!anchor.IsLoaded || !child.IsVisible)
            {
                return false;
            }

            cache._childHandle = cache._childHandle != IntPtr.Zero
                ? cache._childHandle
                : cache._childInteropHelper.Handle;
            if (cache._childHandle == IntPtr.Zero)
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
            var anchorRemainsOnCachedMonitor = cache._hasMonitorGeometry &&
                                               anchorCenter.X >= cache._monitorArea.Left &&
                                               anchorCenter.X < cache._monitorArea.Right &&
                                               anchorCenter.Y >= cache._monitorArea.Top &&
                                               anchorCenter.Y < cache._monitorArea.Bottom;
            var monitorChanged = !anchorRemainsOnCachedMonitor;
            if (!anchorRemainsOnCachedMonitor)
            {
                var monitor = MonitorFromPoint(anchorCenter, MonitorDefaultToNearest);
                var monitorInfo = new MonitorInfo
                {
                    Size = MonitorInfoSize
                };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
                {
                    return false;
                }

                cache._monitorArea = monitorInfo.MonitorArea;
                cache._workArea = monitorInfo.WorkArea;
                cache._hasMonitorGeometry = true;
                cache._hasChildGeometry = false;
            }

            if (!cache._hasChildGeometry)
            {
                if (!GetWindowRect(cache._childHandle, out var childRect))
                {
                    return false;
                }

                cache._childWidth = childRect.Right - childRect.Left;
                cache._childHeight = childRect.Bottom - childRect.Top;
                cache._lastLeft = childRect.Left;
                cache._lastTop = childRect.Top;
                cache._hasChildGeometry = cache._childWidth > 0 && cache._childHeight > 0;
                cache._hasLastPosition = cache._hasChildGeometry;
                if (!cache._hasChildGeometry)
                {
                    return false;
                }
            }

            var childWidth = cache._childWidth;
            var childHeight = cache._childHeight;
            var anchorLeft = (int)Math.Round(anchorTopLeft.X);
            var anchorRight = (int)Math.Round(anchorBottomRight.X);
            var anchorBottom = (int)Math.Round(anchorBottomRight.Y);
            var desiredPosition = CalculateDesiredPosition(
                anchorLeft,
                anchorRight,
                anchorBottom,
                childWidth,
                childHeight,
                cache._workArea,
                preferredChildIsOnLeft,
                out childIsOnLeft);

            // Slider composition frames can request the same child position
            // repeatedly. Avoid a redundant native transition (and its window
            // manager/layout work) when the physical-pixel target is unchanged.
            if (cache._hasLastPosition &&
                cache._lastLeft == desiredPosition.X &&
                cache._lastTop == desiredPosition.Y)
            {
                return true;
            }

            var positioned = SetWindowPos(
                cache._childHandle,
                IntPtr.Zero,
                desiredPosition.X,
                desiredPosition.Y,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            if (!positioned)
            {
                cache._hasLastPosition = false;
                return false;
            }

            if (monitorChanged)
            {
                // PerMonitorV2 can resize the child synchronously when it
                // crosses onto a monitor with a different DPI. Refresh the
                // native rectangle and correct the position once now, instead
                // of carrying the old screen's pixel size into later frames.
                if (!GetWindowRect(cache._childHandle, out var movedChildRect))
                {
                    cache._hasChildGeometry = false;
                    cache._hasLastPosition = false;
                    return true;
                }

                cache._childWidth = movedChildRect.Right - movedChildRect.Left;
                cache._childHeight = movedChildRect.Bottom - movedChildRect.Top;
                cache._hasChildGeometry =
                    cache._childWidth > 0 && cache._childHeight > 0;
                if (!cache._hasChildGeometry)
                {
                    cache._hasLastPosition = false;
                    return true;
                }

                desiredPosition = CalculateDesiredPosition(
                    anchorLeft,
                    anchorRight,
                    anchorBottom,
                    cache._childWidth,
                    cache._childHeight,
                    cache._workArea,
                    preferredChildIsOnLeft,
                    out childIsOnLeft);
                if (movedChildRect.Left != desiredPosition.X ||
                    movedChildRect.Top != desiredPosition.Y)
                {
                    positioned = SetWindowPos(
                        cache._childHandle,
                        IntPtr.Zero,
                        desiredPosition.X,
                        desiredPosition.Y,
                        0,
                        0,
                        SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                    if (!positioned)
                    {
                        cache._hasLastPosition = false;
                        return false;
                    }
                }
            }

            cache._lastLeft = desiredPosition.X;
            cache._lastTop = desiredPosition.Y;
            cache._hasLastPosition = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static NativePoint CalculateDesiredPosition(
        int anchorLeft,
        int anchorRight,
        int anchorBottom,
        int childWidth,
        int childHeight,
        NativeRect workArea,
        bool? preferredChildIsOnLeft,
        out bool childIsOnLeft)
    {
        childIsOnLeft = preferredChildIsOnLeft ??
                        anchorLeft - childWidth >= workArea.Left;
        var desiredLeft = childIsOnLeft
            ? anchorLeft - childWidth
            : anchorRight;
        var desiredTop = anchorBottom - childHeight;
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - childWidth);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - childHeight);
        return new NativePoint
        {
            X = Math.Clamp(desiredLeft, workArea.Left, maximumLeft),
            Y = Math.Clamp(desiredTop, workArea.Top, maximumTop)
        };
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
    internal struct NativeRect
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
