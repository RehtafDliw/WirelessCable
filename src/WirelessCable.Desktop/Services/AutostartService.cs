using System;
using Microsoft.Win32;

namespace WirelessCable.Desktop.Services;

/// <summary>
/// 开机自启。只写当前用户的 Run 项（不需要管理员权限），
/// 不用计划任务、不碰 HKLM——卸载时用户删掉程序即可，不会残留系统级配置。
/// </summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WirelessCable";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var executable = executablePath ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{executable}\"");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
