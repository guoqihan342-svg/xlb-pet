using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LubanDesktopPet;

internal static class WindowChromeAppearance
{
    private const int DwmWindowAttributeBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;

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
}
