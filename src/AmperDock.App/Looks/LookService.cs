using System.IO;
using System.Text.Json;
using AmperDock.Core;
using AmperDock.Shell;

namespace AmperDock.App.Looks;

/// <summary>A folder whose icon AmperDock changed, and what it had before (so Restore puts that back).</summary>
public sealed record FolderLook(string Path, string IconId, string? PreviousFile, int PreviousIndex);

/// <summary>
/// Applies look packs: system cursors, the cursor trail and folder icons. Everything it changes it can
/// undo: the original cursors are saved before the first change, and each folder remembers its old icon.
/// Files Windows points at (cursors, icons) are copied to %LOCALAPPDATA%\AmperDock\looks so they keep
/// working when the app updates to a new folder.
/// </summary>
public sealed class LookService : IDisposable
{
    private const string KeyCursorSet = "look.cursor_set", KeyCursorBackup = "look.cursor_backup",
        KeyTrail = "look.trail", KeyFolders = "look.folders";

    private readonly SettingsStore _store;
    private TrailOverlay? _overlay;

    public LookCatalog Catalog { get; } = LookCatalog.Load();
    public event Action? Changed;

    public static string LooksDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmperDock", "looks");

    public LookService(SettingsStore store) => _store = store;

    /// <summary>Called at startup: bring the saved trail back.</summary>
    public void Start()
    {
        if (TrailId is { } id) StartTrail(id);
    }

    // ---------------------------------------------------------------- cursors

    /// <summary>The applied cursor set, or null for "your own cursors". Cleared if someone changed cursors outside AmperDock.</summary>
    public string? CursorSetId
    {
        get
        {
            var id = Unquote(_store.GetSetting(KeyCursorSet));
            return id is not null && CursorScheme.IsOurs(LooksDir) ? id : null;
        }
    }

    public void ApplyCursors(CursorSetInfo set)
    {
        // save the user's own cursors once, before the first change, so Restore is exact
        if (Unquote(_store.GetSetting(KeyCursorBackup)) is null || !CursorScheme.IsOurs(LooksDir))
            _store.SetSetting(KeyCursorBackup, JsonSerializer.Serialize(CursorScheme.Snapshot()));
        var dir = Path.Combine(LooksDir, "cursors", set.Id);
        Directory.CreateDirectory(dir);
        var arrow = Path.Combine(dir, "arrow.cur");
        var hand = Path.Combine(dir, "hand.cur");
        File.Copy(LookCatalog.FullPath(set.Arrow), arrow, overwrite: true);
        File.Copy(LookCatalog.FullPath(set.Hand), hand, overwrite: true);
        CursorScheme.Apply(arrow, hand);
        _store.SetSetting(KeyCursorSet, JsonSerializer.Serialize(set.Id));
        Changed?.Invoke();
    }

    public void RestoreCursors()
    {
        if (Unquote(_store.GetSetting(KeyCursorBackup)) is { } snapshot)
        {
            try { CursorScheme.Restore(snapshot); }
            catch (System.Text.Json.JsonException) { }   // a backup from an older build; nothing safe to restore from
        }
        _store.SetSetting(KeyCursorBackup, "null");   // the next set you pick snapshots your cursors fresh
        _store.SetSetting(KeyCursorSet, "null");
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- trail

    public string? TrailId => Unquote(_store.GetSetting(KeyTrail));

    /// <summary>False when Windows "Animation effects" is off; trails stay paused then.</summary>
    public static bool AnimationsAllowed => SystemState.AnimationsEnabled();

    public void SetTrail(string? id)
    {
        _store.SetSetting(KeyTrail, id is null ? "null" : JsonSerializer.Serialize(id));
        StopTrail();
        if (id is not null) StartTrail(id);
        Changed?.Invoke();
    }

    private void StartTrail(string id)
    {
        var trail = Catalog.Trails.FirstOrDefault(t => t.Id == id);
        App.Trace($"StartTrail {id}: found={trail is not null} animations={AnimationsAllowed} catalogue={Catalog.Trails.Count}");
        if (trail is null || !AnimationsAllowed) return;
        try { _overlay = new TrailOverlay(trail); }
        catch (Exception ex) { App.Log(ex); _overlay = null; }
    }

    private void StopTrail()
    {
        _overlay?.Dispose();
        _overlay = null;
    }

    // ---------------------------------------------------------------- folder icons

    public IReadOnlyList<FolderLook> Folders
    {
        get
        {
            try { return JsonSerializer.Deserialize<List<FolderLook>>(_store.GetSetting(KeyFolders) ?? "[]") ?? []; }
            catch (JsonException) { return []; }
        }
    }

    /// <summary>Give a folder one of our icons. Returns an error message, or null.</summary>
    public string? ApplyFolderIcon(FolderIconInfo icon, string folder)
    {
        if (!Directory.Exists(folder)) return "That folder doesn't exist any more.";
        var dir = Path.Combine(LooksDir, "folders");
        Directory.CreateDirectory(dir);
        var ico = Path.Combine(dir, $"{icon.Id}.ico");
        File.Copy(LookCatalog.FullPath(icon.Icon), ico, overwrite: true);

        var list = Folders.ToList();
        var existing = list.FirstOrDefault(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));
        // remember the icon it had before AmperDock first touched it, not one of ours
        var (prevFile, prevIndex) = existing is null ? FolderIcons.Read(folder) : (existing.PreviousFile, existing.PreviousIndex);
        var error = FolderIcons.Write(folder, ico);
        if (error is not null) return error;

        list.RemoveAll(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));
        list.Add(new FolderLook(folder, icon.Id, prevFile, prevIndex));
        _store.SetSetting(KeyFolders, JsonSerializer.Serialize(list));
        Changed?.Invoke();
        return null;
    }

    public string? RestoreFolder(FolderLook look)
    {
        string? error = null;
        if (Directory.Exists(look.Path)) error = FolderIcons.Write(look.Path, look.PreviousFile, look.PreviousIndex);
        if (error is not null) return error;
        var list = Folders.Where(f => !string.Equals(f.Path, look.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        _store.SetSetting(KeyFolders, JsonSerializer.Serialize(list));
        Changed?.Invoke();
        return null;
    }

    private static string? Unquote(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<string?>(json); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => StopTrail();
}
