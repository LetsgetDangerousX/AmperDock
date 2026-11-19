using System.IO;
using System.Text;
using AmperDock.Shell;
using Microsoft.Win32;

namespace AmperDock.App.Looks;

/// <summary>
/// Developer check (--selftest-looks report.txt): apply then restore a cursor set and a folder icon on a
/// temporary folder, and confirm everything is back exactly as it was. Leaves the system unchanged.
/// </summary>
internal static class LookSelfTest
{
    public static string Run(LookService looks)
    {
        var log = new StringBuilder();
        void Check(string what, bool ok) => log.AppendLine($"{(ok ? "PASS" : "FAIL")} {what}");

        // ---- cursors
        var before = CursorScheme.Snapshot();
        var set = looks.Catalog.CursorSets.First();
        looks.ApplyCursors(set);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors"))
        {
            var arrow = k?.GetValue("Arrow", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            Check($"cursor set '{set.Id}' applied (Arrow = {arrow})", arrow is not null && arrow.StartsWith(LookService.LooksDir) && File.Exists(arrow));
        }
        Check("AmperDock reports the set as current", looks.CursorSetId == set.Id);
        looks.RestoreCursors();
        var after = CursorScheme.Snapshot();
        Check("original cursors restored exactly", before == after);
        Check("no set reported after restore", looks.CursorSetId is null);

        // ---- folder icon, on a throwaway folder
        var dir = Path.Combine(Path.GetTempPath(), "AmperDock-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var icon = looks.Catalog.FolderIcons.First();
            var err = looks.ApplyFolderIcon(icon, dir);
            Check($"folder icon applied without error ({err ?? "ok"})", err is null);
            var (file, _) = FolderIcons.Read(dir);
            Check($"folder now uses {file}", file is not null && file.EndsWith($"{icon.Id}.ico", StringComparison.OrdinalIgnoreCase));
            Check("folder listed as customised", looks.Folders.Any(f => f.Path == dir));
            err = looks.RestoreFolder(looks.Folders.First(f => f.Path == dir));
            Check($"folder restored without error ({err ?? "ok"})", err is null);
            Check("folder has no custom icon again", FolderIcons.Read(dir).file is null);
            Check("folder no longer listed", looks.Folders.All(f => f.Path != dir));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(dir)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
            Directory.Delete(dir);
        }

        Check($"catalogue loaded: {looks.Catalog.CursorSets.Count} cursor sets, {looks.Catalog.Trails.Count} trails, {looks.Catalog.FolderIcons.Count} folder icons",
            looks.Catalog.CursorSets.Count == 4 && looks.Catalog.Trails.Count == 11 && looks.Catalog.FolderIcons.Count == 9);
        return log.ToString();
    }
}
