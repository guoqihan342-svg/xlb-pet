using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LubanDesktopPet;

internal static class WindowChromeAppearance
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int DwmWindowAttributeBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;

    internal static void ExcludeFromAltTab(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // ShowInTaskbar=False is the WPF-level contract. Reinforce it with the
        // native tool-window style after the HWND exists because shell
        // enumeration can otherwise expose an ownerless transparent Window in
        // Alt+Tab during early startup.
        window.ShowInTaskbar = false;
        window.SourceInitialized -= Window_SourceInitialized;
        window.SourceInitialized += Window_SourceInitialized;
        TryApplyAltTabStyles(window);
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.SourceInitialized -= Window_SourceInitialized;
        TryApplyAltTabStyles(window);
    }

    private static void TryApplyAltTabStyles(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        Marshal.SetLastPInvokeError(0);
        var currentStylePointer = GetWindowLongPtr(handle, GwlExStyle);
        if (currentStylePointer == IntPtr.Zero &&
            Marshal.GetLastPInvokeError() != 0)
        {
            return;
        }

        var currentStyle = currentStylePointer.ToInt64();
        var requiredStyle =
            (currentStyle | WsExToolWindow) & ~WsExAppWindow;
        if (requiredStyle == currentStyle)
        {
            return;
        }

        Marshal.SetLastPInvokeError(0);
        var previousStyle = SetWindowLongPtr(
            handle,
            GwlExStyle,
            new IntPtr(requiredStyle));
        if (previousStyle == IntPtr.Zero &&
            Marshal.GetLastPInvokeError() != 0)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize |
            SwpNoMove |
            SwpNoZOrder |
            SwpNoActivate |
            SwpFrameChanged);
    }

    internal static void TryHideSystemBorder(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var borderColor = DwmColorNone;
        try
        {
            _ = DwmSetWindowAttribute(
                handle,
                DwmWindowAttributeBorderColor,
                ref borderColor,
                sizeof(uint));
        }
        catch (DllNotFoundException)
        {
            // Older Windows builds do not expose this cosmetic DWM option.
        }
        catch (EntryPointNotFoundException)
        {
            // Keep the editor usable if DWM is unavailable.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));

    private static IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr newValue) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, newValue)
            : new IntPtr(
                SetWindowLong32(
                    windowHandle,
                    index,
                    newValue.ToInt32()));

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongW",
        SetLastError = true)]
    private static extern int GetWindowLong32(
        IntPtr windowHandle,
        int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongPtrW",
        SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(
        IntPtr windowHandle,
        int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowLongW",
        SetLastError = true)]
    private static extern int SetWindowLong32(
        IntPtr windowHandle,
        int index,
        int newValue);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowLongPtrW",
        SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(
        IntPtr windowHandle,
        int index,
        IntPtr newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
