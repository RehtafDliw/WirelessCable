using System;
using System.Windows;
using Microsoft.Win32;

namespace WirelessCable.Desktop.Services;

/// <summary>主题偏好。字符串值用于 settings.json，改动要兼容旧文件。</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// 主题切换。
///
/// 资源字典分两层：索引 0 是 Base（字体与样式，全程不变），索引 1 是画刷（浅/深二选一）。
/// 样式里所有颜色都用 DynamicResource 引用画刷，所以替换索引 1 就能整窗即时换肤——
/// 不需要重建窗口，也不会丢当前页面状态。
/// </summary>
public static class ThemeService
{
    private const int PaletteIndex = 1;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>当前实际生效的是不是深色（System 时取决于系统设置）。</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > PaletteIndex)
        {
            merged[PaletteIndex] = dictionary;
        }
        else
        {
            merged.Add(dictionary);
        }
    }

    /// <summary>读 Windows 的"应用模式"设置。读不到就按浅色处理（默认主题）。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // AppsUseLightTheme: 0 = 深色，1 = 浅色
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ToSetting(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system",
    };

    public static AppTheme Parse(string? value) => value switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };
}
