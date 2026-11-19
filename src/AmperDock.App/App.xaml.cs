using System.IO;
using System.Windows;
using System.Windows.Threading;
using AmperDock.App.Dock;
using AmperDock.App.Gallery;
using AmperDock.App.Settings;
using AmperDock.App.Theme;
using AmperDock.Core;
using AmperDock.Shell;

namespace AmperDock.App;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmperDock", "logs", "amperdock.log");

    private Mutex? _single;
    private SettingsStore? _store;
    private ThemeManager? _theme;
    private DockHost? _dock;
    private GalleryWindow? _gallery;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };

        // one dock per user session: a second copy would fight over the same screen edge
        _single = new Mutex(true, @"Local\AmperDock.SingleInstance", out var first);
        if (!first) { Shutdown(); return; }

        _theme = new ThemeManager();
        _theme.Apply(_theme.IsDark);

        var dbPath = e.Args.SkipWhile(a => a != "--db").Skip(1).FirstOrDefault() ?? SettingsStore.DefaultPath();
        _store = new SettingsStore(dbPath);
        SeedOnFirstRun(_store);

        if (e.Args.Contains("--gallery")) OpenGallery();
        _dock = new DockHost(_store, _theme, SettingsStore.MainDockId, OpenAppearance);
        if (e.Args.Contains("--appearance")) OpenAppearance(_dock);
    }

    /// <summary>A brand-new dock gets a couple of apps every Windows install has, so it isn't empty and confusing.</summary>
    private static void SeedOnFirstRun(SettingsStore store)
    {
        if (store.GetSetting("first_run_seeded") is not null) return;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var seeds = new[]
        {
            Path.Combine(win, "explorer.exe"),
            Path.Combine(win, "System32", "notepad.exe"),
            Path.Combine(win, "System32", "Taskmgr.exe"),
        }.Where(File.Exists).Select(Shortcuts.FromDroppedPath).ToList();
        if (store.GetItems(SettingsStore.MainDockId).Count == 0 && seeds.Count > 0)
            store.InsertItems(SettingsStore.MainDockId, 0, seeds);
        store.SetSetting("first_run_seeded", "true");
    }

    private void OpenGallery()
    {
        if (_gallery is { IsLoaded: true }) { _gallery.Activate(); return; }
        _gallery = new GalleryWindow(_theme!);
        _gallery.Show();
    }

    private AppearanceWindow? _appearance;

    private void OpenAppearance(DockHost dock)
    {
        if (_appearance is { IsLoaded: true }) { _appearance.Activate(); return; }
        _appearance = new AppearanceWindow(dock, _store!);
        _appearance.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dock?.Dispose();
        _store?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    private static readonly bool s_trace = Environment.GetEnvironmentVariable("AMPERDOCK_TRACE") == "1";

    /// <summary>Opt-in diagnostics (set AMPERDOCK_TRACE=1). Off by default: no logging in normal use.</summary>
    public static void Trace(string line)
    {
        if (!s_trace) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(Path.ChangeExtension(LogPath, ".trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch (IOException) { }
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {ex}\n\n");
        }
        catch (IOException) { }
    }
}
