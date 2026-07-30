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
    private const int SystemMetricPrimaryScreenWidth = 0;
    private const int SystemMetricPrimaryScreenHeight = 1;
    private const int NativeEdgeTolerancePixels = 1;

    internal enum ScreenEdge
    {
        Left,
        Right,
        Bottom
    }

    internal readonly record struct PhysicalWorkArea(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int MonitorLeft,
        int MonitorTop,
        int MonitorRight,
        int MonitorBottom)
    {
        internal PhysicalWorkArea(
            int left,
            int top,
            int right,
            int bottom)
            : this(
                left,
                top,
                right,
                bottom,
                left,
                top,
                right,
                bottom)
        {
        }

        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal int MonitorWidth => MonitorRight - MonitorLeft;
        internal int MonitorHeight => MonitorBottom - MonitorTop;
        internal bool IsValid =>
            Width > 0 &&
            Height > 0 &&
            MonitorWidth > 0 &&
            MonitorHeight > 0;
    }

    internal static IReadOnlyList<PhysicalWorkArea> GetAllPhysicalWorkAreas()
    {
        var workAreas = new List<PhysicalWorkArea>();
        try
        {
            _ = EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                (
                    IntPtr monitor,
                    IntPtr monitorHdc,
                    ref NativeRect monitorRectangle,
                    IntPtr callbackData) =>
                {
                    var monitorInfo = new MonitorInfo
                    {
                        Size = (uint)Marshal.SizeOf<MonitorInfo>()
                    };
                    if (!GetMonitorInfo(monitor, ref monitorInfo))
                    {
                        return true;
                    }

                    var candidate = new PhysicalWorkArea(
                        monitorInfo.WorkArea.Left,
                        monitorInfo.WorkArea.Top,
                        monitorInfo.WorkArea.Right,
                        monitorInfo.WorkArea.Bottom,
                        monitorInfo.MonitorArea.Left,
                        monitorInfo.MonitorArea.Top,
                        monitorInfo.MonitorArea.Right,
                        monitorInfo.MonitorArea.Bottom);
                    if (candidate.IsValid && !workAreas.Contains(candidate))
                    {
                        workAreas.Add(candidate);
                    }

                    return true;
                },
                IntPtr.Zero);
        }
        catch
        {
            workAreas.Clear();
        }

        if (workAreas.Count == 0)
        {
            var monitor = MonitorFromPoint(
                new NativePoint { X = 0, Y = 0 },
                MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };
            if (monitor != IntPtr.Zero &&
                GetMonitorInfo(monitor, ref monitorInfo))
            {
                workAreas.Add(new PhysicalWorkArea(
                    monitorInfo.WorkArea.Left,
                    monitorInfo.WorkArea.Top,
                    monitorInfo.WorkArea.Right,
                    monitorInfo.WorkArea.Bottom,
                    monitorInfo.MonitorArea.Left,
                    monitorInfo.MonitorArea.Top,
                    monitorInfo.MonitorArea.Right,
                    monitorInfo.MonitorArea.Bottom));
            }
            else
            {
                var width = GetSystemMetrics(SystemMetricPrimaryScreenWidth);
                var height = GetSystemMetrics(SystemMetricPrimaryScreenHeight);
                workAreas.Add(new PhysicalWorkArea(
                    0,
                    0,
                    width > 0 ? width : 1920,
                    height > 0 ? height : 1080));
            }
        }

        workAreas.Sort(static (left, right) =>
        {
            var comparison = left.Left.CompareTo(right.Left);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Top.CompareTo(right.Top);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Right.CompareTo(right.Right);
            return comparison != 0
                ? comparison
                : left.Bottom.CompareTo(right.Bottom);
        });
        return workAreas;
    }

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

    internal static Rect GetForVisual(
        Window coordinateWindow,
        FrameworkElement monitorAnchor)
    {
        if (coordinateWindow is null || monitorAnchor is null)
        {
            return GetFallbackWorkArea();
        }

        try
        {
            var monitor = TryGetMonitorForVisual(monitorAnchor);
            if (monitor == IntPtr.Zero)
            {
                return GetForWindow(coordinateWindow);
            }

            var monitorInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return GetForWindow(coordinateWindow);
            }

            return TryConvertToWindowDips(
                    coordinateWindow,
                    monitorInfo.WorkArea,
                    out var workArea)
                ? workArea
                : GetForWindow(coordinateWindow);
        }
        catch
        {
            return GetForWindow(coordinateWindow);
        }
    }

    internal static bool IsExternalWorkAreaEdgeAt(
        Window window,
        ScreenEdge edge,
        double orthogonalScreenDip) =>
        IsExternalWorkAreaEdgeAt(
            window,
            monitorAnchor: null,
            edge,
            orthogonalScreenDip);

    internal static bool IsExternalWorkAreaEdgeAt(
        Window window,
        FrameworkElement? monitorAnchor,
        ScreenEdge edge,
        double orthogonalScreenDip)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return true;
            }

            var currentMonitor = monitorAnchor is not null
                ? TryGetMonitorForVisual(monitorAnchor)
                : IntPtr.Zero;
            if (currentMonitor == IntPtr.Zero)
            {
                currentMonitor = MonitorFromWindow(
                    handle,
                    MonitorDefaultToNearest);
            }

            var currentInfo = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>()
            };
            if (currentMonitor == IntPtr.Zero ||
                !GetMonitorInfo(currentMonitor, ref currentInfo))
            {
                return true;
            }

            var workEdgeMatchesMonitorEdge = edge switch
            {
                ScreenEdge.Left =>
                    Math.Abs(currentInfo.WorkArea.Left -
                             currentInfo.MonitorArea.Left) <=
                    NativeEdgeTolerancePixels,
                ScreenEdge.Right =>
                    Math.Abs(currentInfo.WorkArea.Right -
                             currentInfo.MonitorArea.Right) <=
                    NativeEdgeTolerancePixels,
                ScreenEdge.Bottom =>
                    Math.Abs(currentInfo.WorkArea.Bottom -
                             currentInfo.MonitorArea.Bottom) <=
                    NativeEdgeTolerancePixels,
                _ => true
            };
            if (!workEdgeMatchesMonitorEdge)
            {
                // A taskbar or reserved desktop band is itself a real work-area
                // boundary even when the physical monitor continues behind it.
                return true;
            }

            var localPoint = edge == ScreenEdge.Bottom
                ? new Point(orthogonalScreenDip - window.Left, 0)
                : new Point(0, orthogonalScreenDip - window.Top);
            var physicalPoint = window.PointToScreen(localPoint);
            var orthogonalPhysical = edge == ScreenEdge.Bottom
                ? (int)Math.Round(physicalPoint.X)
                : (int)Math.Round(physicalPoint.Y);

            var hasAdjacentMonitor = false;
            _ = EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                (
                    IntPtr monitor,
                    IntPtr monitorHdc,
                    ref NativeRect monitorRectangle,
                    IntPtr callbackData) =>
                {
                    if (monitor == currentMonitor)
                    {
                        return true;
                    }

                    var otherInfo = new MonitorInfo
                    {
                        Size = (uint)Marshal.SizeOf<MonitorInfo>()
                    };
                    if (!GetMonitorInfo(monitor, ref otherInfo))
                    {
                        return true;
                    }

                    var adjacent = edge switch
                    {
                        ScreenEdge.Left =>
                            Math.Abs(otherInfo.MonitorArea.Right -
                                     currentInfo.MonitorArea.Left) <=
                            NativeEdgeTolerancePixels &&
                            IsWithinHalfOpenRange(
                                orthogonalPhysical,
                                Math.Max(
                                    currentInfo.MonitorArea.Top,
                                    otherInfo.MonitorArea.Top),
                                Math.Min(
                                    currentInfo.MonitorArea.Bottom,
                                    otherInfo.MonitorArea.Bottom)),
                        ScreenEdge.Right =>
                            Math.Abs(otherInfo.MonitorArea.Left -
                                     currentInfo.MonitorArea.Right) <=
                            NativeEdgeTolerancePixels &&
                            IsWithinHalfOpenRange(
                                orthogonalPhysical,
                                Math.Max(
                                    currentInfo.MonitorArea.Top,
                                    otherInfo.MonitorArea.Top),
                                Math.Min(
                                    currentInfo.MonitorArea.Bottom,
                                    otherInfo.MonitorArea.Bottom)),
                        ScreenEdge.Bottom =>
                            Math.Abs(otherInfo.MonitorArea.Top -
                                     currentInfo.MonitorArea.Bottom) <=
                            NativeEdgeTolerancePixels &&
                            IsWithinHalfOpenRange(
                                orthogonalPhysical,
                                Math.Max(
                                    currentInfo.MonitorArea.Left,
                                    otherInfo.MonitorArea.Left),
                                Math.Min(
                                    currentInfo.MonitorArea.Right,
                                    otherInfo.MonitorArea.Right)),
                        _ => false
                    };
                    if (!adjacent)
                    {
                        return true;
                    }

                    hasAdjacentMonitor = true;
                    return false;
                },
                IntPtr.Zero);
            return !hasAdjacentMonitor;
        }
        catch
        {
            // Failing open preserves edge animation on unusual display drivers;
            // the exact contact-gap check still prevents premature snapping.
            return true;
        }
    }

    private static IntPtr TryGetMonitorForVisual(FrameworkElement visual)
    {
        if (!visual.IsLoaded ||
            visual.ActualWidth <= 0 ||
            visual.ActualHeight <= 0)
        {
            return IntPtr.Zero;
        }

        var center = visual.PointToScreen(
            new Point(
                visual.ActualWidth / 2,
                visual.ActualHeight / 2));
        if (!double.IsFinite(center.X) ||
            !double.IsFinite(center.Y) ||
            center.X < int.MinValue ||
            center.X > int.MaxValue ||
            center.Y < int.MinValue ||
            center.Y > int.MaxValue)
        {
            return IntPtr.Zero;
        }

        var nativeCenter = new NativePoint
        {
            X = checked((int)Math.Round(
                center.X,
                MidpointRounding.AwayFromZero)),
            Y = checked((int)Math.Round(
                center.Y,
                MidpointRounding.AwayFromZero))
        };
        return MonitorFromPoint(nativeCenter, MonitorDefaultToNearest);
    }

    private static bool IsWithinHalfOpenRange(int value, int start, int end) =>
        end > start && value >= start && value < end;

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr clipRectangle,
        MonitorEnumProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate bool MonitorEnumProcedure(
        IntPtr monitor,
        IntPtr hdc,
        ref NativeRect monitorRectangle,
        IntPtr data);
}
