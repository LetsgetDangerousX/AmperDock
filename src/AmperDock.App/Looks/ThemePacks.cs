using System.IO;
using System.Text.Json;
using AmperDock.Core;
using AmperDock.Shell;

namespace AmperDock.App.Looks;

/// <summary>A theme pack: a wallpaper and the dock theme, cursors, trail and folder icon that go with it.</summary>
public sealed record ThemePackInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Mood { get; init; } = "";
    public string Blurb { get; init; } = "";
    public string Preview { get; init; } = "";
    public string Wallpaper { get; init; } = "";
    public string DockTheme { get; init; } = "";
    public string CursorSet { get; init; } = "";
    public string Trail { get; init; } = "";
    public string FolderIcon { get; init; } = "";
}

/// <summary>Which parts of a pack to apply. Everything is on unless the person untics it.</summary>
[Flags]
public enum PackParts { None = 0, Wallpaper = 1, Dock = 2, Cursors = 4, Trail = 8, Folders = 16, All = 31 }

/// <summary>How a pack's wallpaper goes on the desktop.</summary>
public enum WallpaperMode
{
    Set,                  // just this picture (stops a running slideshow; Undo starts it again)
    AddToMySlideshow,     // copy it into the person's running slideshow folder
    AmperDockSlideshow,   // a slideshow of every pack's wallpaper, starting with this one
}

/// <summary>Timing for <see cref="WallpaperMode.AmperDockSlideshow"/>.</summary>
public sealed record SlideshowTiming(TimeSpan Interval, bool Shuffle)
{
    public static readonly SlideshowTiming Default = new(TimeSpan.FromMinutes(1), true);
}

/// <summary>The app's docks, as the look service needs them: read and replace each dock's look, and list folders on them.</summary>
public interface IDockLooks
{
    IReadOnlyList<(string DockId, DockAppearance Look)> Looks();
    void SetLook(string dockId, DockAppearance look);
    IEnumerable<string> FoldersOnDocks();
}

/// <summary>
/// What a pack replaced, kept until Undo. Applying a second pack keeps the first "before" for each part,
/// so Undo always goes back to how things were before any pack.
/// </summary>
public sealed record PackRecord
{
    public string PackId { get; init; } = "";
    public WallpaperSnapshot? WallpaperBefore { get; init; }
    public string? AddedToSlideshow { get; init; }          // a picture copied into the slideshow folder, removed on undo
    public bool CursorsChanged { get; init; }
    public string? CursorsBefore { get; init; }               // an AmperDock set id, or null for the person's own cursors
    public bool TrailChanged { get; init; }
    public string? TrailBefore { get; init; }
    public Dictionary<string, string>? DocksBefore { get; init; }        // dock id -> appearance JSON
    public Dictionary<string, string?>? FoldersBefore { get; init; }     // folder -> AmperDock icon id it had, or null
}

public sealed partial class LookService
{
    private const string KeyPack = "look.pack";

    /// <summary>The pack applied last (its "before" state is kept for Undo), or null.</summary>
    public PackRecord? AppliedPack
    {
        get
        {
            try { return _store.GetSetting(KeyPack) is { } json && json != "null" ? JsonSerializer.Deserialize<PackRecord>(json) : null; }
            catch (JsonException) { return null; }
        }
        private set => _store.SetSetting(KeyPack, value is null ? "null" : JsonSerializer.Serialize(value));
    }

    /// <summary>The main folders a pack's folder icon goes on (the ones Windows shows under This PC).</summary>
    public static IEnumerable<string> MainFolders()
    {
        var list = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Downloads(),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        return list.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));
    }

    private static string? Downloads()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");   // FOLDERID_Downloads
        return SHGetKnownFolderPath(ref id, 0, 0, out var p) == 0 ? ReadAndFree(p) : null;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);

    private static string? ReadAndFree(nint p)
    {
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(p); }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// Apply the ticked parts of a pack. <paramref name="mode"/> says how the wallpaper goes on (see
    /// <see cref="WallpaperMode"/>); <paramref name="timing"/> is used for an AmperDock slideshow.
    /// Returns problems, if any, in plain words.
    /// </summary>
    public List<string> ApplyPack(ThemePackInfo pack, PackParts parts, WallpaperMode mode, IDockLooks docks, SlideshowTiming? timing = null)
    {
        var problems = new List<string>();
        var rec = AppliedPack ?? new PackRecord();
        rec = rec with { PackId = pack.Id };

        if (parts.HasFlag(PackParts.Wallpaper))
        {
            try
            {
                var dir = Path.Combine(LooksDir, "wallpapers");
                Directory.CreateDirectory(dir);
                var source = LookCatalog.FullPath(pack.Wallpaper);
                if (mode == WallpaperMode.AmperDockSlideshow)
                {
                    var first = FillSlideshowFolder(pack);
                    rec = rec with { WallpaperBefore = rec.WallpaperBefore ?? Wallpapers.Snapshot() };
                    var t = timing ?? SlideshowTiming.Default;
                    Wallpapers.StartSlideshow(SlideshowDir, t.Interval, t.Shuffle, first);
                }
                else if (mode == WallpaperMode.AddToMySlideshow && Wallpapers.SlideshowFolder() is { } folder)
                {
                    if (rec.AddedToSlideshow is { } old && File.Exists(old)) File.Delete(old);   // one pack picture at a time
                    var dest = Path.Combine(folder, $"AmperDock {pack.Name}.png");
                    File.Copy(source, dest, overwrite: true);
                    rec = rec with { AddedToSlideshow = dest };
                }
                else
                {
                    var copy = Path.Combine(dir, $"{pack.Id}.png");
                    File.Copy(source, copy, overwrite: true);
                    rec = rec with { WallpaperBefore = rec.WallpaperBefore ?? Wallpapers.Snapshot() };
                    Wallpapers.Set(copy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                App.Log(ex);
                problems.Add($"The wallpaper couldn't be changed ({ex.Message}).");
            }
        }

        if (parts.HasFlag(PackParts.Dock) && DockThemeCatalog(pack.DockTheme) is { } theme)
        {
            var before = rec.DocksBefore ?? docks.Looks().ToDictionary(d => d.DockId, d => d.Look.ToJson());
            foreach (var (id, look) in docks.Looks())
            {
                before.TryAdd(id, look.ToJson());
                // the theme's look, keeping each dock's own size and wallpaper choices
                docks.SetLook(id, theme with { IndicatorSize = look.IndicatorSize, Reflection = look.Reflection,
                    MatchWallpaper = look.MatchWallpaper, IconTint = look.IconTint });
            }
            rec = rec with { DocksBefore = before };
        }

        if (parts.HasFlag(PackParts.Cursors) && Catalog.CursorSets.FirstOrDefault(c => c.Id == pack.CursorSet) is { } cursors)
        {
            if (!rec.CursorsChanged) rec = rec with { CursorsChanged = true, CursorsBefore = CursorSetId };
            try { ApplyCursors(cursors); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log(ex);
                problems.Add($"The cursors couldn't be changed ({ex.Message}).");
            }
        }

        if (parts.HasFlag(PackParts.Trail) && Catalog.Trails.Any(t => t.Id == pack.Trail))
        {
            if (!rec.TrailChanged) rec = rec with { TrailChanged = true, TrailBefore = TrailId };
            SetTrail(pack.Trail);
        }

        if (parts.HasFlag(PackParts.Folders) && Catalog.FolderIcons.FirstOrDefault(f => f.Id == pack.FolderIcon) is { } icon)
        {
            var current = Folders.ToDictionary(f => f.Path, f => (string?)f.IconId, StringComparer.OrdinalIgnoreCase);
            var before = rec.FoldersBefore ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var targets = MainFolders().Concat(current.Keys).Concat(docks.FoldersOnDocks())
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var failed = 0;
            foreach (var folder in targets)
            {
                before.TryAdd(folder, current.GetValueOrDefault(folder));
                if (ApplyFolderIcon(icon, folder) is not null) failed++;
            }
            if (failed > 0) problems.Add(failed == 1 ? "One folder couldn't take the new icon." : $"{failed} folders couldn't take the new icon.");
            rec = rec with { FoldersBefore = before };
        }

        AppliedPack = rec;
        Changed?.Invoke();
        return problems;
    }

    /// <summary>Put back everything packs changed, exactly as it was before the first one.</summary>
    public List<string> UndoPack(IDockLooks docks)
    {
        var problems = new List<string>();
        if (AppliedPack is not { } rec) return problems;

        if (rec.AddedToSlideshow is { } added)
            try { if (File.Exists(added)) File.Delete(added); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"Couldn't remove {Path.GetFileName(added)} from your slideshow folder."); }
        if (rec.WallpaperBefore is { } wall)
            try { Wallpapers.Restore(wall); }
            catch (System.Runtime.InteropServices.COMException ex) { App.Log(ex); problems.Add("Your old wallpaper couldn't be put back."); }

        if (rec.DocksBefore is { } docksBefore)
            foreach (var (id, json) in docksBefore)
                docks.SetLook(id, DockAppearance.FromJson(json));

        if (rec.CursorsChanged)
        {
            if (rec.CursorsBefore is { } set && Catalog.CursorSets.FirstOrDefault(c => c.Id == set) is { } info) ApplyCursors(info);
            else RestoreCursors();
        }
        if (rec.TrailChanged) SetTrail(rec.TrailBefore);

        if (rec.FoldersBefore is { } foldersBefore)
        {
            var looks = Folders.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var (folder, iconBefore) in foldersBefore)
            {
                if (iconBefore is not null && Catalog.FolderIcons.FirstOrDefault(i => i.Id == iconBefore) is { } icon) ApplyFolderIcon(icon, folder);
                else if (looks.TryGetValue(folder, out var look) && RestoreFolder(look) is { } err) problems.Add(err);
            }
        }

        AppliedPack = null;
        Changed?.Invoke();
        return problems;
    }

    /// <summary>Where the AmperDock slideshow's pictures live (AmperDock's own folder, never the person's).</summary>
    public static string SlideshowDir => Path.Combine(LooksDir, "wallpapers", "slideshow");

    /// <summary>
    /// Copy every pack's wallpaper (and any extra slideshow pictures in the catalog) into <see cref="SlideshowDir"/>,
    /// the chosen pack's first by name. Pictures from older versions are cleared out. Returns the chosen pack's copy.
    /// </summary>
    private string FillSlideshowFolder(ThemePackInfo chosen)
    {
        Directory.CreateDirectory(SlideshowDir);
        foreach (var old in Directory.EnumerateFiles(SlideshowDir, "*.png")) File.Delete(old);
        var pictures = Catalog.ThemePacks.Where(p => p.Id != chosen.Id).Select(p => (p.Id, p.Wallpaper))
            .Concat(Catalog.SlideshowWallpapers.Select(w => (w.Id, w.Wallpaper)))
            .Prepend((chosen.Id, chosen.Wallpaper))
            .Where(p => p.Wallpaper.Length > 0)
            .DistinctBy(p => p.Wallpaper)
            .ToList();
        string? first = null;
        for (var i = 0; i < pictures.Count; i++)
        {
            var source = LookCatalog.FullPath(pictures[i].Wallpaper);
            if (!File.Exists(source)) continue;
            var dest = Path.Combine(SlideshowDir, $"{i:00} {pictures[i].Id}.png");
            File.Copy(source, dest, overwrite: true);
            first ??= dest;
        }
        return first ?? throw new IOException("No slideshow pictures were found.");
    }

    private static DockAppearance? DockThemeCatalog(string id) => global::AmperDock.App.Dock.DockThemeCatalog.Shared.Find(id)?.Appearance;
}
