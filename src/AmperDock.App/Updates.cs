using System.IO;
using System.Text.Json;
using AmperDock.App.Looks;
using AmperDock.Core;
using AmperDock.Shell;
using Velopack;
using Velopack.Sources;

namespace AmperDock.App;

/// <summary>
/// Installed copies of AmperDock (made with Velopack, see tools/make_installer.ps1) update themselves
/// from the project's GitHub releases: a quiet check a minute after start-up and then once a day; a new
/// version downloads in the background and goes in the next time AmperDock starts. Copies run from the
/// source folder aren't installed and never update.
/// </summary>
public static class Updates
{
    public const string Releases = "https://github.com/LetsgetDangerousX/AmperDock";

    private static UpdateManager? Manager()
    {
        try
        {
            var m = new UpdateManager(new GithubSource(Releases, null, false, null), null, null);
            return m.IsInstalled ? m : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>Look for a newer version and download it. Returns its version number, or null if there's nothing new.</summary>
    public static async Task<string?> CheckAndDownloadAsync()
    {
        if (Manager() is not { } m) return null;
        try
        {
            var info = await m.CheckForUpdatesAsync();
            if (info is null) return null;
            await m.DownloadUpdatesAsync(info);
            return info.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.Log(ex);   // offline, GitHub unreachable...: try again tomorrow
            return null;
        }
    }
}

/// <summary>
/// Run by the uninstaller (before AmperDock's files are removed): put back everything AmperDock changed
/// outside itself, so uninstalling leaves the PC as it was. Each step is independent: one failing
/// doesn't stop the rest.
/// </summary>
public static class UninstallCleanup
{
    private sealed class StoredDocks(SettingsStore store) : IDockLooks
    {
        public IReadOnlyList<(string DockId, DockAppearance Look)> Looks() => store.GetDocks().Select(d => (d.Id, store.GetAppearance(d.Id))).ToList();
        public void SetLook(string dockId, DockAppearance look) => store.SetAppearance(dockId, look);
        public IEnumerable<string> FoldersOnDocks() => [];
    }

    public static void Run()
    {
        if (!File.Exists(SettingsStore.DefaultPath())) return;
        using var store = new SettingsStore(SettingsStore.DefaultPath());
        var looks = new LookService(store);
        Try(() => { looks.UndoPack(new StoredDocks(store)); });                       // the wallpaper (or slideshow) a theme pack replaced
        Try(looks.RestoreCursors);                                                // the person's own cursors
        foreach (var folder in looks.Folders) Try(() => { looks.RestoreFolder(folder); });   // every folder icon AmperDock set
        Try(() => { looks.UndoFolderIconEverywhere(); });                             // the folder icon for every folder (asks permission)
        Try(() => { ShelfRetirement.Run(store); });                               // any icons an old desktop shelf hid
    }

    private static void Try(Action step)
    {
        try { step(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.Log(ex); }
    }
}

/// <summary>
/// Desktop shelves were removed (users found them confusing). Earlier builds hid desktop icons that were
/// put on a shelf; this shows every one of them again with the attributes it had, then deletes the shelf.
/// </summary>
public static class ShelfRetirement
{
    /// <summary>Returns how many icons were shown again.</summary>
    public static int Run(SettingsStore store)
    {
        var restored = 0;
        foreach (var dock in store.GetDocks())
        {
            var b = store.GetBehavior(dock.Id);
            if (!b.DesktopShelf && b.HiddenOnDesktop.Count == 0) continue;
            foreach (var path in b.HiddenOnDesktop)
            {
                try
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;
                    var original = b.OriginalAttributes.TryGetValue(path, out var a) ? (FileAttributes)a : File.GetAttributes(path) & ~FileAttributes.Hidden;
                    File.SetAttributes(path, original);
                    Notify(path);
                    restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
            }
            if (b.DesktopShelf && dock.Id != SettingsStore.MainDockId) store.DeleteDock(dock.Id);
            else store.SetBehavior(dock.Id, b with { DesktopShelf = false, HiddenOnDesktop = [], OriginalAttributes = new() });
        }
        return restored;
    }

    private static void Notify(string path)
    {
        var p = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(0x00000800 /* SHCNE_ATTRIBUTES */, 0x0005 /* SHCNF_PATHW */, p, 0); }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
