using System.IO;
using System.Windows;
using System.Windows.Threading;
using AmperDock.App.Dock;
using AmperDock.App.Gallery;
using AmperDock.App.Looks;
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
    private readonly List<DockHost> _docks = [];
    private readonly Dictionary<string, AppearanceWindow> _appearanceWindows = new();
    private LookService? _looks;
    private LookPacksWindow? _looksWindow;
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
        _looks = new LookService(_store);
        if (e.Args.SkipWhile(x => x != "--selftest-looks").Skip(1).FirstOrDefault() is { } report)
        {
            File.WriteAllText(report, LookSelfTest.Run(_looks));
            Shutdown();
            return;
        }
        foreach (var d in _store.GetDocks().Where(d => d.Enabled)) StartDock(d.Id);
        _looks.Changed += () => { foreach (var d in _docks) d.RefreshIcons(); };   // folder items pick up new folder icons
        _looks.Start();
        if (e.Args.Contains("--looks")) OpenLooks();
        if (e.Args.Contains("--appearance") && _docks.Count > 0) OpenAppearance(_docks[0]);
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

    private void OpenLooks()
    {
        if (_looksWindow is { IsLoaded: true }) { _looksWindow.Activate(); return; }
        _looksWindow = new LookPacksWindow(_looks!);
        _looksWindow.Show();
    }

    private void StartDock(string id)
    {
        try { _docks.Add(new DockHost(_store!, _theme!, id, new DockCommands(OpenAppearance, OpenLooks, NewDock, RemoveDock))); }
        catch (Exception ex) { Log(ex); }
    }

    private void OpenAppearance(DockHost dock)
    {
        if (_appearanceWindows.TryGetValue(dock.ContainerId, out var open)) { open.Activate(); return; }
        var win = new AppearanceWindow(dock, _store!);
        if (!dock.IsMain) win.Title = $"Dock appearance: {_store!.GetDocks().FirstOrDefault(d => d.Id == dock.ContainerId)?.Name}";
        _appearanceWindows[dock.ContainerId] = win;
        win.Closed += (_, _) => _appearanceWindows.Remove(dock.ContainerId);
        win.Show();
    }

    /// <summary>
    /// "New dock": an empty dock on the same screen, on an edge no other dock uses there (left, right,
    /// top, then bottom), or floating in the middle when every edge is taken. It starts with the same look.
    /// </summary>
    private void NewDock(DockHost from)
    {
        var here = from.Placement.MonitorKey;
        var used = _docks.Where(d => d.Placement.MonitorKey == here).Select(d => d.Placement.Edge).ToHashSet();
        var edge = new[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom }.Cast<DockEdge?>().FirstOrDefault(e => !used.Contains(e!.Value));
        var work = from.WorkArea;
        var placement = edge is { } free
            ? new DockPlacement(free, here, 0.5f)
            : new DockPlacement(DockEdge.Float, here, 0.5f, (int)(work.X + work.W / 2), (int)(work.Y + work.H / 2));
        var n = _store!.GetDocks().Count + 1;
        var id = _store.CreateDock($"Dock {n}", placement, from.Appearance);
        StartDock(id);
    }

    private void RemoveDock(DockHost dock)
    {
        if (dock.IsMain) return;
        var name = _store!.GetDocks().FirstOrDefault(d => d.Id == dock.ContainerId)?.Name ?? "this dock";
        var what = dock.ItemCount switch { 0 => "", 1 => " and the 1 item on it", var c => $" and the {c} items on it" };
        var answer = MessageBox.Show($"Remove {name}{what}?{Environment.NewLine}{Environment.NewLine}Only the dock goes. Your apps, files and folders aren't touched.",
            "Remove dock", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        if (_appearanceWindows.TryGetValue(dock.ContainerId, out var win)) win.Close();
        _docks.Remove(dock);
        dock.Dispose();
        _store.DeleteDock(dock.ContainerId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _looks?.Dispose();
        foreach (var d in _docks) d.Dispose();
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
