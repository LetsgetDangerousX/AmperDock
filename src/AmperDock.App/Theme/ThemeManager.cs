using System.IO;
using System.Text.Json;
using System.Windows;
using AmperDock.Render;
using Microsoft.Win32;

namespace AmperDock.App.Theme;

/// <summary>
/// Light/dark switching for the WPF dialogs (swaps the generated token dictionary) and the dock
/// style for the renderer (read from tokens.json). Follows Windows' app mode.
/// </summary>
public sealed class ThemeManager
{
    private readonly JsonElement _tokens;
    private ResourceDictionary? _current;

    public bool IsDark { get; private set; }
    public event Action? Changed;

    public ThemeManager()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tokens.json");
        _tokens = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        IsDark = WindowsPrefersDark();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var dark = WindowsPrefersDark();
            if (dark == IsDark) return;
            Apply(dark);
        };
    }

    public static bool WindowsPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    /// <summary>Swap the colour dictionary in Application.Resources and tell listeners.</summary>
    public void Apply(bool dark)
    {
        IsDark = dark;
        var app = Application.Current;
        var dict = new ResourceDictionary { Source = new Uri($"/Theme/Tokens.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (_current is not null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Insert(0, dict);
        _current = dict;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;   // Fluent controls follow
        // Fluent reads the accent from these keys; point them at our accent instead of the Windows one
        var accent = (System.Windows.Media.Color)dict["Color.Accent"];
        foreach (var k in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
                                  "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            app.Resources[k] = accent;
        Changed?.Invoke();
    }

    private string Color(string role) => _tokens.GetProperty("color").GetProperty(IsDark ? "dark" : "light").GetProperty(role).GetString()!;

    private string DockValue(string name)
    {
        var e = _tokens.GetProperty("dock").GetProperty(name);
        return e.ValueKind == JsonValueKind.Object ? e.GetProperty(IsDark ? "dark" : "light").GetString()! : e.GetString()!;
    }

    public DockStyle DockStyle() => new(
        Background: Argb.FromRgba(DockValue("background")),
        Outline: Argb.FromRgba(DockValue("outline")),
        Highlight: Argb.FromRgba(DockValue("highlight_top")),
        Indicator: Argb.FromRgba(Color("accent")),
        IndicatorFocused: Argb.FromRgba(Color("blush")),
        LabelBg: Argb.FromRgba(Color("tooltip-bg")),
        LabelText: Argb.FromRgba(Color("tooltip-text")),
        Shadow: Argb.FromRgba("#2A1F3D"));

    public Core.DockMetrics DockMetrics()
    {
        var d = _tokens.GetProperty("dock");
        float F(string n) => d.GetProperty(n).GetSingle();
        var pad = d.GetProperty("padding");
        var ind = d.GetProperty("indicator");
        return new Core.DockMetrics(F("icon_size"), F("magnified_size"), F("magnify_range_icons"), F("icon_spacing"),
            pad.GetProperty("x").GetSingle(), pad.GetProperty("y").GetSingle(),
            ind.GetProperty("size").GetSingle(), ind.GetProperty("gap").GetSingle(), F("radius"));
    }
}
