using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace LubanDesktopPet;

internal sealed class TrayIconService : IDisposable
{
    private const string IconResourceName =
        "LubanDesktopPet.Assets.luban-tray.ico";
    private const uint TrayIconId = 1;
    private const int TrayCallbackMessage = 0x8000 + 0x51;
    private const int WmContextMenu = 0x007B;
    private const int WmRightButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetFocus = 0x00000003;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const uint IconResourceVersion = 0x00030000;
    private const int PreferredIconSize = 32;

    private static readonly SolidColorBrush MenuBackgroundBrush =
        CreateFrozenBrush(255, 253, 248);
    private static readonly SolidColorBrush MenuBorderBrush =
        CreateFrozenBrush(239, 191, 137);
    private static readonly SolidColorBrush MenuForegroundBrush =
        CreateFrozenBrush(92, 67, 72);
    private static readonly SolidColorBrush MenuSelectionBrush =
        CreateFrozenBrush(255, 238, 211);
    private static readonly SolidColorBrush MenuSelectionBorderBrush =
        CreateFrozenBrush(241, 172, 93);

    private readonly Window _owner;
    private readonly ContextMenu _menu;
    private readonly MenuItem _exitItem;
    private readonly HwndSourceHook _messageHook;
    private readonly uint _taskbarCreatedMessage;
    private HwndSource? _hwndSource;
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconService(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _messageHook = WindowMessageHook;
        _taskbarCreatedMessage =
            RegisterWindowMessageW("TaskbarCreated");
        _menu = CreateTrayMenu(out _exitItem);
        _exitItem.Click += ExitItem_Click;
        _menu.Closed += Menu_Closed;
        _owner.SourceInitialized += Owner_SourceInitialized;

        try
        {
            _iconHandle = CreateTrayIcon();
            AttachToOwnerWindow();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.SourceInitialized -= Owner_SourceInitialized;
        _exitItem.Click -= ExitItem_Click;
        _menu.Closed -= Menu_Closed;
        _menu.IsOpen = false;

        RemoveTrayIcon();
        if (_hwndSource is { } hwndSource)
        {
            hwndSource.RemoveHook(_messageHook);
            _hwndSource = null;
        }

        _windowHandle = IntPtr.Zero;
        if (_iconHandle != IntPtr.Zero)
        {
            _ = DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private static ContextMenu CreateTrayMenu(
        out MenuItem exitItem)
    {
        var menu = new ContextMenu
        {
            Background = MenuBackgroundBrush,
            BorderBrush = MenuBorderBrush,
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Microsoft YaHei"),
            FontSize = 12,
            Foreground = MenuForegroundBrush,
            HasDropShadow = true,
            Padding = new Thickness(5),
            Placement = PlacementMode.RelativePoint,
            StaysOpen = false,
            Template = CreateContextMenuTemplate()
        };

        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(
            new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(
            new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        itemStyle.Setters.Add(
            new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        itemStyle.Setters.Add(
            new Setter(Control.ForegroundProperty, MenuForegroundBrush));
        itemStyle.Setters.Add(
            new Setter(Control.FontFamilyProperty,
                new FontFamily("Microsoft YaHei")));
        itemStyle.Setters.Add(
            new Setter(Control.FontSizeProperty, 12d));
        itemStyle.Setters.Add(
            new Setter(Control.PaddingProperty,
                new Thickness(12, 7, 12, 7)));
        itemStyle.Setters.Add(
            new Setter(FrameworkElement.MinWidthProperty, 116d));
        itemStyle.Setters.Add(
            new Setter(Control.TemplateProperty,
                CreateMenuItemTemplate()));
        itemStyle.Seal();

        exitItem = new MenuItem
        {
            Header = "退出小鲁班",
            Style = itemStyle
        };
        menu.Items.Add(exitItem);
        return menu;
    }

    private static ControlTemplate CreateContextMenuTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(
            Border.BackgroundProperty,
            CreateTemplatedParentBinding(
                Control.BackgroundProperty.Name));
        border.SetBinding(
            Border.BorderBrushProperty,
            CreateTemplatedParentBinding(
                Control.BorderBrushProperty.Name));
        border.SetBinding(
            Border.BorderThicknessProperty,
            CreateTemplatedParentBinding(
                Control.BorderThicknessProperty.Name));
        border.SetBinding(
            Border.PaddingProperty,
            CreateTemplatedParentBinding(
                Control.PaddingProperty.Name));
        border.SetValue(
            Border.CornerRadiusProperty,
            new CornerRadius(9));
        border.SetValue(
            UIElement.SnapsToDevicePixelsProperty,
            true);

        var presenter =
            new FrameworkElementFactory(typeof(ItemsPresenter));
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(ContextMenu))
        {
            VisualTree = border
        };
    }

    private static ControlTemplate CreateMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "SelectionBorder";
        border.SetBinding(
            Border.BackgroundProperty,
            CreateTemplatedParentBinding(
                Control.BackgroundProperty.Name));
        border.SetBinding(
            Border.BorderBrushProperty,
            CreateTemplatedParentBinding(
                Control.BorderBrushProperty.Name));
        border.SetBinding(
            Border.BorderThicknessProperty,
            CreateTemplatedParentBinding(
                Control.BorderThicknessProperty.Name));
        border.SetBinding(
            Border.PaddingProperty,
            CreateTemplatedParentBinding(
                Control.PaddingProperty.Name));
        border.SetValue(
            Border.CornerRadiusProperty,
            new CornerRadius(7));
        border.SetValue(
            UIElement.SnapsToDevicePixelsProperty,
            true);

        var header =
            new FrameworkElementFactory(typeof(ContentPresenter));
        header.SetBinding(
            ContentPresenter.ContentProperty,
            CreateTemplatedParentBinding(
                HeaderedItemsControl.HeaderProperty.Name));
        header.SetBinding(
            ContentPresenter.ContentTemplateProperty,
            CreateTemplatedParentBinding(
                HeaderedItemsControl.HeaderTemplateProperty.Name));
        header.SetValue(
            ContentPresenter.RecognizesAccessKeyProperty,
            true);
        header.SetValue(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        border.AppendChild(header);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = border
        };
        var highlighted = new Trigger
        {
            Property = MenuItem.IsHighlightedProperty,
            Value = true
        };
        highlighted.Setters.Add(
            new Setter(
                Control.BackgroundProperty,
                MenuSelectionBrush));
        highlighted.Setters.Add(
            new Setter(
                Control.BorderBrushProperty,
                MenuSelectionBorderBrush));
        template.Triggers.Add(highlighted);

        var disabled = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabled.Setters.Add(
            new Setter(UIElement.OpacityProperty, 0.55d));
        template.Triggers.Add(disabled);
        return template;
    }

    private static Binding CreateTemplatedParentBinding(
        string propertyPath)
    {
        return new Binding(propertyPath)
        {
            Mode = BindingMode.OneWay,
            RelativeSource =
                new RelativeSource(
                    RelativeSourceMode.TemplatedParent)
        };
    }

    private static SolidColorBrush CreateFrozenBrush(
        byte red,
        byte green,
        byte blue)
    {
        var brush =
            new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void Owner_SourceInitialized(
        object? sender,
        EventArgs e)
    {
        AttachToOwnerWindow();
    }

    private void AttachToOwnerWindow()
    {
        if (_disposed || _hwndSource is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(_owner).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var hwndSource = HwndSource.FromHwnd(handle);
        if (hwndSource is null)
        {
            throw new InvalidOperationException(
                "Unable to attach the tray icon to the main window.");
        }

        _windowHandle = handle;
        _hwndSource = hwndSource;
        hwndSource.AddHook(_messageHook);
        AddTrayIcon();
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_disposed)
        {
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 &&
            (uint)message == _taskbarCreatedMessage)
        {
            // Explorer owns notification-area state. Its restart clears the
            // icon even though our window and HICON remain valid.
            _iconAdded = false;
            AddTrayIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (message != TrayCallbackMessage)
        {
            return IntPtr.Zero;
        }

        var notification =
            unchecked((ushort)lParam.ToInt64());
        if (notification is WmContextMenu or WmRightButtonUp)
        {
            ShowTrayMenu();
        }

        handled = true;
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        if (_disposed)
        {
            return;
        }

        if (!TryGetTrayMenuScreenPoint(out var screenPoint))
        {
            // Both native anchor queries can transiently fail while Explorer
            // or the input desktop is switching. An owner-relative fallback
            // is still preferable to WPF's parentless (0,0) popup.
            screenPoint = _owner.PointToScreen(
                new Point(_owner.ActualWidth / 2, _owner.ActualHeight / 2));
        }

        var placementPoint = ConvertTrayMenuPlacementPoint(
            _owner,
            screenPoint);

        // Resetting IsOpen also moves an already-open menu to the latest
        // pointer location after a second tray right-click.
        _menu.IsOpen = false;
        _menu.PlacementTarget = _owner;
        _menu.PlacementRectangle = new Rect(
            placementPoint,
            new Size(0, 0));
        _menu.Placement = PlacementMode.RelativePoint;
        _menu.HorizontalOffset = 0;
        _menu.VerticalOffset = 0;
        _menu.IsOpen = true;
    }

    private bool TryGetTrayMenuScreenPoint(out Point screenPoint)
    {
        if (GetCursorPos(out var cursorPoint))
        {
            screenPoint = new Point(cursorPoint.X, cursorPoint.Y);
            return true;
        }

        if (_windowHandle != IntPtr.Zero)
        {
            var identifier = new NotifyIconIdentifier
            {
                cbSize = checked(
                    (uint)Marshal.SizeOf<NotifyIconIdentifier>()),
                hWnd = _windowHandle,
                uID = TrayIconId
            };
            if (Shell_NotifyIconGetRect(
                    ref identifier,
                    out var iconRectangle) == 0 &&
                iconRectangle.Right > iconRectangle.Left &&
                iconRectangle.Bottom > iconRectangle.Top)
            {
                screenPoint = new Point(
                    iconRectangle.Left +
                    (iconRectangle.Right - iconRectangle.Left) / 2d,
                    iconRectangle.Top +
                    (iconRectangle.Bottom - iconRectangle.Top) / 2d);
                return true;
            }
        }

        screenPoint = default;
        return false;
    }

    private static Point ConvertTrayMenuPlacementPoint(
        Window owner,
        Point screenPoint) =>
        owner.PointFromScreen(screenPoint);

    private void ExitItem_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _menu.IsOpen = false;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Menu_Closed(object? sender, RoutedEventArgs e)
    {
        if (_disposed ||
            !_iconAdded ||
            _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData();
        _ = Shell_NotifyIconW(NimSetFocus, ref data);
    }

    private void AddTrayIcon()
    {
        if (_disposed ||
            _windowHandle == IntPtr.Zero ||
            _iconHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData();
        _iconAdded = Shell_NotifyIconW(NimAdd, ref data);
        if (!_iconAdded)
        {
            return;
        }

        data.uVersion = NotifyIconVersion4;
        _ = Shell_NotifyIconW(NimSetVersion, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData();
        _ = Shell_NotifyIconW(NimDelete, ref data);
        _iconAdded = false;
    }

    private NotifyIconData CreateNotifyIconData()
    {
        return new NotifyIconData
        {
            cbSize = checked((uint)Marshal.SizeOf<NotifyIconData>()),
            hWnd = _windowHandle,
            uID = TrayIconId,
            uFlags =
                NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = "小鲁班桌宠",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static IntPtr CreateTrayIcon()
    {
        var processPath = Environment.ProcessPath;
        var entryAssemblyName =
            Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                entryAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            _ = ExtractIconExW(
                processPath,
                0,
                out var largeIcon,
                out var smallIcon,
                1);
            if (largeIcon != IntPtr.Zero)
            {
                if (smallIcon != IntPtr.Zero)
                {
                    _ = DestroyIcon(smallIcon);
                }

                return largeIcon;
            }

            if (smallIcon != IntPtr.Zero)
            {
                return smallIcon;
            }
        }

        return CreateEmbeddedIcon();
    }

    private static IntPtr CreateEmbeddedIcon()
    {
        using var iconStream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(
                IconResourceName)
            ?? throw new InvalidOperationException(
                "Missing embedded Luban tray icon.");
        var iconBytes =
            GC.AllocateUninitializedArray<byte>(
                checked((int)iconStream.Length));
        iconStream.ReadExactly(iconBytes);
        var entries = ReadIconEntries(iconBytes);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                "The embedded Luban tray icon is invalid.");
        }

        var pinnedBytes =
            GCHandle.Alloc(iconBytes, GCHandleType.Pinned);
        try
        {
            var baseAddress = pinnedBytes.AddrOfPinnedObject();
            foreach (var entry in entries
                         .OrderBy(item =>
                             Math.Abs(
                                 item.Width - PreferredIconSize))
                         .ThenByDescending(item => item.BitDepth))
            {
                var icon = CreateIconFromResourceEx(
                    IntPtr.Add(baseAddress, entry.Offset),
                    checked((uint)entry.Length),
                    true,
                    IconResourceVersion,
                    entry.Width,
                    entry.Height,
                    0);
                if (icon != IntPtr.Zero)
                {
                    return icon;
                }
            }
        }
        finally
        {
            pinnedBytes.Free();
        }

        throw new InvalidOperationException(
            "Windows could not create the embedded Luban tray icon.");
    }

    private static List<IconEntry> ReadIconEntries(
        byte[] iconBytes)
    {
        var entries = new List<IconEntry>();
        if (iconBytes.Length < 6 ||
            BitConverter.ToUInt16(iconBytes, 0) != 0 ||
            BitConverter.ToUInt16(iconBytes, 2) != 1)
        {
            return entries;
        }

        var entryCount = BitConverter.ToUInt16(iconBytes, 4);
        for (var index = 0; index < entryCount; index++)
        {
            var directoryOffset = 6 + (index * 16);
            if (directoryOffset > iconBytes.Length - 16)
            {
                break;
            }

            var width = iconBytes[directoryOffset];
            var height = iconBytes[directoryOffset + 1];
            var bitDepth =
                BitConverter.ToUInt16(
                    iconBytes,
                    directoryOffset + 6);
            var byteLength =
                BitConverter.ToUInt32(
                    iconBytes,
                    directoryOffset + 8);
            var imageOffset =
                BitConverter.ToUInt32(
                    iconBytes,
                    directoryOffset + 12);
            if (byteLength == 0 ||
                imageOffset > int.MaxValue ||
                byteLength > int.MaxValue ||
                (ulong)imageOffset + byteLength >
                (ulong)iconBytes.Length)
            {
                continue;
            }

            entries.Add(
                new IconEntry(
                    width == 0 ? 256 : width,
                    height == 0 ? 256 : height,
                    bitDepth,
                    checked((int)imageOffset),
                    checked((int)byteLength)));
        }

        return entries;
    }

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(
        uint message,
        ref NotifyIconData data);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(
        ref NotifyIconIdentifier identifier,
        out NativeRect iconLocation);

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint ExtractIconExW(
        string fileName,
        int iconIndex,
        out IntPtr largeIcon,
        out IntPtr smallIcon,
        uint iconCount);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint RegisterWindowMessageW(
        string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(
        IntPtr resourceBits,
        uint resourceSize,
        [MarshalAs(UnmanagedType.Bool)] bool isIcon,
        uint version,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

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
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    private readonly record struct IconEntry(
        int Width,
        int Height,
        ushort BitDepth,
        int Offset,
        int Length);
}
