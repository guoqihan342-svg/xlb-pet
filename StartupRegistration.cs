using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace LubanDesktopPet;

internal sealed class StartupRegistration
{
    internal const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "LubanDesktopPet";

    private readonly string _expectedCommand;
    private readonly Func<string?> _readValue;
    private readonly Action<string> _writeValue;
    private readonly Action _deleteValue;

    internal StartupRegistration(
        string expectedCommand,
        Func<string?> readValue,
        Action<string> writeValue,
        Action deleteValue)
    {
        _expectedCommand = expectedCommand;
        _readValue = readValue;
        _writeValue = writeValue;
        _deleteValue = deleteValue;
    }

    internal string ExpectedCommand => _expectedCommand;

    internal static bool TryCreateForCurrentProcess(
        out StartupRegistration? registration,
        out string? error)
    {
        registration = null;
        error = null;
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) ||
                !File.Exists(processPath))
            {
                using var process = Process.GetCurrentProcess();
                processPath = process.MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(processPath) ||
                !File.Exists(processPath))
            {
                error = "无法确定当前程序路径";
                return false;
            }

            string command;
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                var entryAssemblyName =
                    Assembly.GetEntryAssembly()?.GetName().Name;
                var entryPath = string.IsNullOrWhiteSpace(entryAssemblyName)
                    ? null
                    : Path.Combine(
                        AppContext.BaseDirectory,
                        entryAssemblyName + ".dll");
                if (entryPath is null ||
                    !File.Exists(entryPath))
                {
                    error = "无法确定 dotnet 启动所需的入口 DLL";
                    return false;
                }

                command = BuildLaunchCommand(processPath, entryPath);
            }
            else
            {
                command = BuildLaunchCommand(processPath);
            }

            registration = new StartupRegistration(
                command,
                ReadRegistryValue,
                WriteRegistryValue,
                DeleteRegistryValue);
            return true;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            error = exception.Message;
            return false;
        }
    }

    internal bool TryReadAndRepair(
        out bool enabled,
        out string? error)
    {
        enabled = false;
        error = null;
        string? previousValue = null;
        var valueRead = false;
        try
        {
            previousValue = _readValue();
            valueRead = true;
            enabled = !string.IsNullOrWhiteSpace(previousValue);
            if (!enabled ||
                string.Equals(
                    previousValue,
                    _expectedCommand,
                    StringComparison.Ordinal))
            {
                return true;
            }

            _writeValue(_expectedCommand);
            var repairedValue = _readValue();
            if (!string.Equals(
                    repairedValue,
                    _expectedCommand,
                    StringComparison.Ordinal))
            {
                throw new IOException("开机自启路径写入后校验失败");
            }

            return true;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            error = exception.Message;
            if (valueRead)
            {
                TryRestore(previousValue);
            }

            enabled = TryReadEnabledBestEffort();
            return false;
        }
    }

    internal bool TrySetEnabled(
        bool enabled,
        out bool actualEnabled,
        out string? error)
    {
        actualEnabled = false;
        error = null;
        string? previousValue;
        try
        {
            previousValue = _readValue();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            error = exception.Message;
            return false;
        }

        try
        {
            if (enabled)
            {
                _writeValue(_expectedCommand);
            }
            else
            {
                _deleteValue();
            }

            var verifiedValue = _readValue();
            actualEnabled = !string.IsNullOrWhiteSpace(verifiedValue);
            var verified = enabled
                ? string.Equals(
                    verifiedValue,
                    _expectedCommand,
                    StringComparison.Ordinal)
                : !actualEnabled;
            if (!verified)
            {
                throw new IOException("开机自启设置写入后校验失败");
            }

            return true;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            error = exception.Message;
            TryRestore(previousValue);
            actualEnabled = TryReadEnabledBestEffort();
            return false;
        }
    }

    internal static string BuildLaunchCommand(
        string executablePath,
        string? entryAssemblyPath = null)
    {
        var command = $"\"{Path.GetFullPath(executablePath)}\"";
        if (!string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            command += $" \"{Path.GetFullPath(entryAssemblyPath)}\"";
        }

        return command + " --autostart";
    }

    private void TryRestore(string? previousValue)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(previousValue))
            {
                _deleteValue();
            }
            else
            {
                _writeValue(previousValue);
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            AppLogger.Error("恢复开机自启注册表设置失败", exception);
        }
    }

    private bool TryReadEnabledBestEffort()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(_readValue());
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            AppLogger.Error("读取开机自启最终状态失败", exception);
            return false;
        }
    }

    private static string? ReadRegistryValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RunKeyPath,
            writable: false);
        return key?.GetValue(
            ValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static void WriteRegistryValue(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            RunKeyPath,
            writable: true) ??
            throw new UnauthorizedAccessException("无法打开当前用户开机启动注册表");
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    private static void DeleteRegistryValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            RunKeyPath,
            writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException
            or Win32Exception;
}
