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
    // Velopack's install/update/uninstall hooks must run before anything else. On uninstall, first put
    // back everything AmperDock changed (cursors, folder icons, wallpaper, tidied desktop icons).
    static App() => Velopack.VelopackApp.Build()
        .OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run())
        .SetAutoApplyOnStartup(true)
        .Run();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmperDock", "logs", "amperdock.log");

    private Mutex? _single;
    private SettingsStore? _store;
    private ThemeManager? _theme;
    private readonly List<DockHost> _docks = [];
    private readonly Dictionary<string, AppearanceWindow> _appearanceWindows = new();
    private LookService? _looks;
    private DesktopWidgetManager? _widgets;
    private LookPacksWindow? _looksWindow;
    private GalleryWindow? _gallery;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };

        // the short-lived elevated helper: do the one job and leave (no dock, no windows)
        if (e.Args.Length >= 2 && e.Args[0] == "--admin-task")
        {
            Shutdown(AdminTasks.Perform(e.Args[1]));
            return;
        }

        // one dock per user session: a second copy would fight over the same screen edge.
        // A copy with its own settings file (--db, for trying things out) counts separately.
        var db = e.Args.SkipWhile(a => a != "--db").Skip(1).FirstOrDefault();
        var name = db is null ? @"Local\AmperDock.SingleInstance"
            : @"Local\AmperDock.SingleInstance." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(db).ToLowerInvariant())))[..16];
        _single = new Mutex(true, name, out var first);
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
        if (e.Args.SkipWhile(x => x != "--selftest-drop").Skip(1).FirstOrDefault() is { } dropReport)
        {
            // what a drop of each item would add: Start menu apps (a Store app and a desktop app) and a plain file
            var names = new[]
            {
                @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
                @"shell:AppsFolder\{6D809377-6AF0-444B-8957-A3773F02200E}\Notepad++\notepad++.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"),
            };
            var lines = names.Select(n => $"{n} -> {string.Join(" | ", DropTarget.TargetsOf(n))}").ToList();
            File.WriteAllLines(dropReport, lines);
            Shutdown();
            return;
        }
        if (e.Args.SkipWhile(x => x != "--selftest-wallpaper").Skip(1).FirstOrDefault() is { } wallReport)
        {
            // read-only: what Undo would put back (the wallpaper per monitor, or the slideshow and its timing)
            File.WriteAllText(wallReport, System.Text.Json.JsonSerializer.Serialize(Wallpapers.Snapshot(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
                + Environment.NewLine + "slideshow folder: " + Wallpapers.SlideshowFolder());
            Shutdown();
            return;
        }
        if (e.Args.SkipWhile(x => x != "--selftest-tasks").Skip(1).FirstOrDefault() is { } taskReport)
        {
            // what the dock sees: open windows with their program and app id, items' keys, and the matches
            var sb = new System.Text.StringBuilder();
            var windows = Shell.Tasks.Snapshot();
            foreach (var w in windows) sb.AppendLine($"window {w.Hwnd:X} pid={w.Pid} exe={w.ExePath} appid={w.AppId}");
            var items = _store.GetDocks().SelectMany(d => _store.GetItems(d.Id)).ToList();
            var keys = items.Select(i => (i, k: Shell.AppIds.KeysFor(i.Target))).ToList();
            foreach (var (i, k) in keys) sb.AppendLine($"item {i.Label} target={i.Target} appid={k.AppId} exe={k.Exe}");
            var (byItem, left) = Core.TaskMatch.Assign(keys.Select(x => new Core.TaskMatch.ItemKey(x.i.Id, x.k.AppId, x.k.Exe)).ToList(),
                windows, w => w.AppId, w => w.ExePath);
            foreach (var (i, _) in keys) sb.AppendLine($"running {i.Label}: {(byItem.TryGetValue(i.Id, out var ws) ? ws.Count : 0)}");
            sb.AppendLine($"unmatched windows: {left.Count}");
            var apps = Shell.AppIds.InstalledApps();
            sb.AppendLine($"installed apps: {apps.Count}");
            foreach (var a in apps.Take(400)) sb.AppendLine($"app {a.Name} | {a.AppId} | {a.Target}");
            File.WriteAllText(taskReport, sb.ToString());
            Shutdown();
            return;
        }
        _widgets = new DesktopWidgetManager(_store);
        MoveWidgetsOffDocks();
        RetireShelves();
        foreach (var d in _store.GetDocks().Where(d => d.Enabled)) StartDock(d.Id);
        _widgets.Start();
        StartUpdateChecks();
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
        _looksWindow = new LookPacksWindow(_looks!, new AppDockLooks(this));
        _looksWindow.Show();
    }

    /// <summary>The docks, as the look service sees them (theme packs change their looks, and give their folders icons).</summary>
    private sealed class AppDockLooks(App app) : IDockLooks
    {
        public IReadOnlyList<(string DockId, DockAppearance Look)> Looks() => app._docks.Select(d => (d.ContainerId, d.Appearance)).ToList();

        public void SetLook(string dockId, DockAppearance look)
        {
            if (app._docks.FirstOrDefault(d => d.ContainerId == dockId) is { } dock) dock.SaveAppearance(look);
            else app._store!.SetAppearance(dockId, look);   // a dock that's switched off keeps it for next time
        }

        public IEnumerable<string> FoldersOnDocks() => app._store!.GetDocks().SelectMany(d => app._store.GetItems(d.Id))
            .Where(i => i.Type == ItemTypes.Folder && i.Target is not null).Select(i => i.Target!);
    }

    /// <summary>An installed copy looks for a new version a minute after start-up, then once a day.</summary>
    private void StartUpdateChecks()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(24);
            if (await Updates.CheckAndDownloadAsync() is { } version)
                _docks.FirstOrDefault(d => d.IsMain)?.Notify($"AmperDock {version} is ready. It goes in next time AmperDock starts.");
        };
        timer.Start();
    }

    /// <summary>
    /// Widgets live on the desktop, not in docks. An earlier build put them in docks: take those out,
    /// and give the desktop a clock if one of them was a clock.
    /// </summary>
    private void MoveWidgetsOffDocks()
    {
        var onDocks = _store!.GetDocks().SelectMany(d => _store.GetItems(d.Id)).Where(i => i.Type == ItemTypes.Widget).ToList();
        foreach (var item in onDocks) _store.RemoveItem(item.Id);
        if (onDocks.Any(i => i.Target == "widget:clock") && !_widgets!.Has("clock")) _widgets.Add("clock");
    }

    private void StartDock(string id)
    {
        try { _docks.Add(new DockHost(_store!, _theme!, id, new DockCommands(OpenAppearance, OpenLooks, NewDock, RemoveDock, (kind, _) => _widgets!.Add(kind)))); }
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

    /// <summary>
    /// Desktop shelves are gone. Anything one tidied away is shown on the desktop again, exactly as it
    /// was (its original attributes), and the shelf itself is removed.
    /// </summary>
    private void RetireShelves()
    {
        var restored = ShelfRetirement.Run(_store!);
        if (restored > 0) Dispatcher.BeginInvoke(() => _docks.FirstOrDefault(d => d.IsMain)?.Notify(
            restored == 1 ? "The desktop shelf is gone, and its icon is back on your desktop." : $"The desktop shelf is gone, and its {restored} icons are back on your desktop."),
            DispatcherPriority.ApplicationIdle);
    }

    private void RemoveDock(DockHost dock)
    {
        if (dock.IsMain) return;
        var name = _store!.GetDocks().FirstOrDefault(d => d.Id == dock.ContainerId)?.Name ?? "this dock";
        var what = dock.ItemCount switch { 0 => "", 1 => " and the 1 item on it", var c => $" and the {c} items on it" };
        var detail = "Only the dock goes. Your apps, files and folders aren't touched.";
        var answer = MessageBox.Show($"Remove {name}{what}?{Environment.NewLine}{Environment.NewLine}{detail}",
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
        _widgets?.CloseAll();
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
