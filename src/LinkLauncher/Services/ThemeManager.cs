using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LinkLauncher.Services;

public static class ThemeManager
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly IReadOnlyDictionary<string, SolidColorBrush> LightBrushes = CreateBrushes(
        ("Accent", "#0E806B"),
        ("PrimaryInk", "#FFFFFF"),
        ("Ink", "#182B34"),
        ("Muted", "#71818B"),
        ("WindowBackground", "#F6F8FA"),
        ("Surface", "#FFFFFF"),
        ("Border", "#DCE4E8"),
        ("SearchHint", "#95A2AB"),
        ("SearchIcon", "#64828A"),
        ("SoftSurface", "#F3F6F8"),
        ("SelectedSurface", "#E8F5F0"),
        ("SelectedBorder", "#69B59F"),
        ("HoverSurface", "#F0F8F5"),
        ("HoverBorder", "#A3CFC2"),
        ("FilterActiveSurface", "#DFF0E9"),
        ("FilterActiveBorder", "#B2D5C7"),
        ("FilterActiveInk", "#236D58"),
        ("FilterInk", "#798992"),
        ("DetailInk", "#78958E"),
        ("KindSurface", "#F3F6F8"),
        ("KindInk", "#788C97"),
        ("EmptySurface", "#E5F1ED"),
        ("EmptyInk", "#5A9B87"),
        ("NoticeSurface", "#E8F4EF"),
        ("NoticeErrorSurface", "#FFF0E6"),
        ("SaveInk", "#668F7F"),
        ("FooterInk", "#8A99A3"),
        ("ErrorInk", "#B04431"));

    private static readonly IReadOnlyDictionary<string, SolidColorBrush> DarkBrushes = CreateBrushes(
        ("Accent", "#61C6AA"),
        ("PrimaryInk", "#14252A"),
        ("Ink", "#E8EFF3"),
        ("Muted", "#A7B7C1"),
        ("WindowBackground", "#14212A"),
        ("Surface", "#1D2D37"),
        ("Border", "#354852"),
        ("SearchHint", "#839BA9"),
        ("SearchIcon", "#9EB9C5"),
        ("SoftSurface", "#263944"),
        ("SelectedSurface", "#1D4139"),
        ("SelectedBorder", "#64B69F"),
        ("HoverSurface", "#243D3B"),
        ("HoverBorder", "#527C70"),
        ("FilterActiveSurface", "#284D42"),
        ("FilterActiveBorder", "#568F7B"),
        ("FilterActiveInk", "#A3E4CC"),
        ("FilterInk", "#B0BEC7"),
        ("DetailInk", "#9EBAB1"),
        ("KindSurface", "#2B3F4A"),
        ("KindInk", "#B4C6CF"),
        ("EmptySurface", "#244239"),
        ("EmptyInk", "#80CBB1"),
        ("NoticeSurface", "#244136"),
        ("NoticeErrorSurface", "#4C3529"),
        ("SaveInk", "#98C6B2"),
        ("FooterInk", "#93ACBA"),
        ("ErrorInk", "#F2AB98"));

    private static bool _systemEventsSubscribed;
    private static bool _paletteApplied;

    public static string CurrentTheme { get; private set; } = "Light";

    public static bool IsDark => string.Equals(CurrentTheme, "Dark", StringComparison.Ordinal);

    public static event Action? ThemeChanged;

    public static void Apply(string theme)
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        string requestedTheme = theme?.Trim() ?? string.Empty;
        Action apply = () => ApplyOnDispatcher(requestedTheme);
        if (application.Dispatcher.CheckAccess())
        {
            apply();
            return;
        }

        try
        {
            application.Dispatcher.BeginInvoke(DispatcherPriority.Normal, apply);
        }
        catch (InvalidOperationException)
        {
            // アプリ終了後に予約された更新は破棄します。
        }
    }

    public static Brush GetBrush(string key)
    {
        Application application = Application.Current
            ?? throw new InvalidOperationException("WPF Applicationが初期化されていません。");
        return (Brush)application.FindResource(key);
    }

    public static void Dispose()
    {
        if (!_systemEventsSubscribed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _systemEventsSubscribed = false;
    }

    private static void ApplyOnDispatcher(string requestedTheme)
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (string.Equals(requestedTheme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            ApplyResolvedTheme(application, isDark: true);
        }
        else if (string.Equals(requestedTheme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            ApplyResolvedTheme(application, isDark: false);
        }
        else
        {
            EnsureSystemEventsSubscription();
            ApplyResolvedTheme(application, ReadWindowsDarkMode());
        }
    }

    private static void EnsureSystemEventsSubscription()
    {
        if (_systemEventsSubscribed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _systemEventsSubscribed = true;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            application.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RefreshWindowsTheme));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher終了中の通知は破棄します。
        }
    }

    private static void RefreshWindowsTheme()
    {
        if (!_systemEventsSubscribed)
        {
            return;
        }

        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        ApplyResolvedTheme(application, ReadWindowsDarkMode());
    }

    private static void ApplyResolvedTheme(Application application, bool isDark)
    {
        string resolvedTheme = isDark ? "Dark" : "Light";
        bool changed = !string.Equals(CurrentTheme, resolvedTheme, StringComparison.Ordinal);
        if (_paletteApplied && !changed)
        {
            return;
        }

        IReadOnlyDictionary<string, SolidColorBrush> palette = isDark ? DarkBrushes : LightBrushes;
        ResourceDictionary resources = application.Resources;
        foreach (KeyValuePair<string, SolidColorBrush> entry in palette)
        {
            resources[entry.Key] = entry.Value;
        }

        ApplySystemColorResources(resources, palette);

        CurrentTheme = resolvedTheme;
        _paletteApplied = true;
        if (changed)
        {
            RaiseThemeChanged();
        }
    }

    private static void ApplySystemColorResources(
        ResourceDictionary resources,
        IReadOnlyDictionary<string, SolidColorBrush> palette)
    {
        SolidColorBrush ink = palette["Ink"];
        SolidColorBrush surface = palette["Surface"];
        SolidColorBrush accent = palette["Accent"];
        SolidColorBrush primaryInk = palette["PrimaryInk"];

        resources[SystemColors.WindowBrushKey] = palette["WindowBackground"];
        resources[SystemColors.WindowTextBrushKey] = ink;
        resources[SystemColors.ControlBrushKey] = surface;
        resources[SystemColors.ControlTextBrushKey] = ink;
        resources[SystemColors.MenuBrushKey] = surface;
        resources[SystemColors.MenuTextBrushKey] = ink;
        resources[SystemColors.HighlightBrushKey] = accent;
        resources[SystemColors.HighlightTextBrushKey] = primaryInk;
        resources[SystemColors.InfoBrushKey] = surface;
        resources[SystemColors.InfoTextBrushKey] = ink;
    }

    private static bool ReadWindowsDarkMode()
    {
        try
        {
            using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, writable: false);
            object? value = personalize?.GetValue(AppsUseLightThemeValue, defaultValue: 1) ?? 1;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or FormatException
            or InvalidCastException
            or OverflowException)
        {
            return false;
        }
    }

    private static void RaiseThemeChanged()
    {
        Action? handlers = ThemeChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch
            {
                // テーマ変更を受け取る画面側の例外でOS設定通知を中断しません。
            }
        }
    }

    private static IReadOnlyDictionary<string, SolidColorBrush> CreateBrushes(
        params (string Key, string Color)[] entries)
    {
        Dictionary<string, SolidColorBrush> brushes = new(entries.Length, StringComparer.Ordinal);
        foreach ((string key, string color) in entries)
        {
            SolidColorBrush brush = new(ParseColor(color));
            brush.Freeze();
            brushes.Add(key, brush);
        }

        return brushes;
    }

    private static Color ParseColor(string value)
    {
        int start = 1;
        byte alpha = 255;
        if (value.Length == 9)
        {
            alpha = ParseByte(value, start);
            start += 2;
        }

        return Color.FromArgb(
            alpha,
            ParseByte(value, start),
            ParseByte(value, start + 2),
            ParseByte(value, start + 4));
    }

    private static byte ParseByte(string value, int start)
    {
        return byte.Parse(value.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
