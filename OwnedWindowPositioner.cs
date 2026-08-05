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

            // Lock/unlock, remote-session switches and per-monitor DPI changes
            // can move or resize an HWND without changing the anchor's logical
            // monitor. Re-read the native child rectangle on every real
            // positioning pass; cached coordinates are only observations, not
            // authoritative window-manager state.
            if (!GetWindowRect(cache._childHandle, out var childRect))
            {
                cache._hasChildGeometry = false;
                cache._hasLastPosition = false;
                return false;
            }

            cache._childWidth = childRect.Right - childRect.Left;
            cache._childHeight = childRect.Bottom - childRect.Top;
            cache._hasChildGeometry =
                cache._childWidth > 0 && cache._childHeight > 0;
            if (!cache._hasChildGeometry)
            {
                cache._hasLastPosition = false;
                return false;
            }

            var anchorLeft = (int)Math.Round(anchorTopLeft.X);
            var anchorRight = (int)Math.Round(anchorBottomRight.X);
            var anchorBottom = (int)Math.Round(anchorBottomRight.Y);
            var desiredPosition = CalculateDesiredPosition(
                anchorLeft,
                anchorRight,
                anchorBottom,
                cache._childWidth,
                cache._childHeight,
                cache._workArea,
                preferredChildIsOnLeft,
                out childIsOnLeft);

            if (childRect.Left == desiredPosition.X &&
                childRect.Top == desiredPosition.Y)
            {
                childIsOnLeft = IsChildActuallyOnLeft(
                    childRect.Left,
                    cache._childWidth,
                    anchorLeft,
                    anchorRight);
                cache._lastLeft = childRect.Left;
                cache._lastTop = childRect.Top;
                cache._hasLastPosition = true;
                return true;
            }

            if (!SetWindowPos(
                cache._childHandle,
                IntPtr.Zero,
                desiredPosition.X,
                desiredPosition.Y,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
            {
                cache._hasLastPosition = false;
                return false;
            }

            if (!GetWindowRect(cache._childHandle, out var movedChildRect))
            {
                cache._hasChildGeometry = false;
                cache._hasLastPosition = false;
                return false;
            }

            // WM_DPICHANGED can synchronously alter the physical child size
            // during the first move. Recompute against the current work area
            // and correct the position once with that post-move rectangle.
            cache._childWidth = movedChildRect.Right - movedChildRect.Left;
            cache._childHeight = movedChildRect.Bottom - movedChildRect.Top;
            cache._hasChildGeometry =
                cache._childWidth > 0 && cache._childHeight > 0;
            if (!cache._hasChildGeometry)
            {
                cache._hasLastPosition = false;
                return false;
            }

            var correctedPosition = CalculateDesiredPosition(
                anchorLeft,
                anchorRight,
                anchorBottom,
                cache._childWidth,
                cache._childHeight,
                cache._workArea,
                preferredChildIsOnLeft,
                out childIsOnLeft);
            if ((movedChildRect.Left != correctedPosition.X ||
                 movedChildRect.Top != correctedPosition.Y) &&
                !SetWindowPos(
                    cache._childHandle,
                    IntPtr.Zero,
                    correctedPosition.X,
                    correctedPosition.Y,
                    0,
                    0,
                    SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
            {
                cache._hasLastPosition = false;
                return false;
            }

            childIsOnLeft = IsChildActuallyOnLeft(
                correctedPosition.X,
                cache._childWidth,
                anchorLeft,
                anchorRight);
            cache._lastLeft = correctedPosition.X;
            cache._lastTop = correctedPosition.Y;
            cache._hasLastPosition = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySetBounds(Window window, Rect logicalBounds)
    {
        try
        {
            if (!window.IsLoaded ||
                PresentationSource.FromVisual(window) is not HwndSource source ||
                source.Handle == IntPtr.Zero)
            {
                return false;
            }

            var transform = source.CompositionTarget.TransformToDevice;
            var scaleX = transform.M11;
            var scaleY = transform.M22;
            if (!double.IsFinite(scaleX) ||
                !double.IsFinite(scaleY) ||
                scaleX <= 0 ||
                scaleY <= 0 ||
                !IsFinitePositiveBounds(logicalBounds))
            {
                return false;
            }

            // Width and height are rounded independently from the origin.
            // Subtracting rounded right/bottom coordinates can lose one pixel
            // on a negative-coordinate, mixed-DPI monitor.
            var left = RoundPhysicalPixel(logicalBounds.Left, scaleX);
            var top = RoundPhysicalPixel(logicalBounds.Top, scaleY);
            var width = RoundPhysicalPixel(logicalBounds.Width, scaleX);
            var height = RoundPhysicalPixel(logicalBounds.Height, scaleY);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            if (!GetWindowRect(source.Handle, out var currentBounds))
            {
                return false;
            }

            var currentWidth = currentBounds.Right - currentBounds.Left;
            var currentHeight = currentBounds.Bottom - currentBounds.Top;
            var sizeIsUnchanged =
                currentWidth == width && currentHeight == height;
            if (currentBounds.Left == left &&
                currentBounds.Top == top &&
                sizeIsUnchanged)
            {
                return true;
            }

            if (sizeIsUnchanged)
            {
                // A position-only correction must not send WM_SIZE to a
                // transparent owner whose layered backbuffer is already at
                // the permanent maximum envelope.
                return SetWindowPos(
                    source.Handle,
                    IntPtr.Zero,
                    left,
                    top,
                    0,
                    0,
                    SwpNoSize |
                    SwpNoZOrder |
                    SwpNoActivate |
                    SwpNoOwnerZOrder);
            }

            // A shown WPF Window normally turns separate Width, Height, Left
            // and Top writes into four SetWindowPos calls. A transparent
            // layered window can expose those intermediate rectangles for one
            // DWM composition, which looks like a flash. One native move-size
            // is atomic; synchronous WM_MOVE/WM_SIZE messages also update the
            // WPF dependency properties, so no duplicate property writes are
            // needed after this succeeds.
            return SetWindowPos(
                source.Handle,
                IntPtr.Zero,
                left,
                top,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySetPosition(
        Window window,
        double logicalLeft,
        double logicalTop)
    {
        try
        {
            if (!window.IsLoaded ||
                PresentationSource.FromVisual(window) is not HwndSource source ||
                source.Handle == IntPtr.Zero)
            {
                return false;
            }

            var transform = source.CompositionTarget.TransformToDevice;
            var scaleX = transform.M11;
            var scaleY = transform.M22;
            if (!double.IsFinite(scaleX) ||
                !double.IsFinite(scaleY) ||
                scaleX <= 0 ||
                scaleY <= 0 ||
                !double.IsFinite(logicalLeft) ||
                !double.IsFinite(logicalTop))
            {
                return false;
            }

            var left = RoundPhysicalPixel(logicalLeft, scaleX);
            var top = RoundPhysicalPixel(logicalTop, scaleY);
            if (!GetWindowRect(source.Handle, out var currentBounds))
            {
                return false;
            }

            if (currentBounds.Left == left && currentBounds.Top == top)
            {
                return true;
            }

            return SetWindowPos(
                source.Handle,
                IntPtr.Zero,
                left,
                top,
                0,
                0,
                SwpNoSize |
                SwpNoZOrder |
                SwpNoActivate |
                SwpNoOwnerZOrder);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryGetPhysicalBounds(
        Window window,
        out NativeRect bounds)
    {
        bounds = default;
        try
        {
            if (!window.IsLoaded ||
                PresentationSource.FromVisual(window) is not HwndSource source ||
                source.Handle == IntPtr.Zero)
            {
                return false;
            }

            return GetWindowRect(source.Handle, out bounds) &&
                   bounds.Right > bounds.Left &&
                   bounds.Bottom > bounds.Top;
        }
        catch
        {
            bounds = default;
            return false;
        }
    }

    internal static bool TrySetPhysicalPosition(
        Window window,
        int physicalLeft,
        int physicalTop)
    {
        try
        {
            if (!window.IsLoaded ||
                PresentationSource.FromVisual(window) is not HwndSource source ||
                source.Handle == IntPtr.Zero)
            {
                return false;
            }

            if (GetWindowRect(source.Handle, out var currentBounds) &&
                currentBounds.Left == physicalLeft &&
                currentBounds.Top == physicalTop)
            {
                return true;
            }

            // Drag input and monitor work areas are already expressed in
            // physical screen pixels. Passing them through WPF's current DPI
            // transform would corrupt negative-coordinate and mixed-DPI
            // monitor positions, so this path deliberately stays native.
            return SetWindowPos(
                source.Handle,
                IntPtr.Zero,
                physicalLeft,
                physicalTop,
                0,
                0,
                SwpNoSize |
                SwpNoZOrder |
                SwpNoActivate |
                SwpNoOwnerZOrder);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFinitePositiveBounds(Rect bounds) =>
        double.IsFinite(bounds.Left) &&
        double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height) &&
        bounds.Width > 0 &&
        bounds.Height > 0;

    private static int RoundPhysicalPixel(double logicalValue, double dpiScale)
    {
        var physicalValue = logicalValue * dpiScale;
        if (!double.IsFinite(physicalValue) ||
            physicalValue < int.MinValue ||
            physicalValue > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalValue));
        }

        return checked((int)Math.Round(
            physicalValue,
            MidpointRounding.AwayFromZero));
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

    private static bool IsChildActuallyOnLeft(
        int childLeft,
        int childWidth,
        int anchorLeft,
        int anchorRight)
    {
        var childCenter = childLeft + childWidth / 2d;
        var anchorCenter = (anchorLeft + anchorRight) / 2d;
        return childCenter <= anchorCenter;
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
