using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AmperDock.Shell;

/// <summary>
/// The few things that need administrator rights: hiding or showing icons on the desktop every user
/// shares (the Public desktop), and the folder icon for every folder on the PC. The dock never runs
/// elevated; it starts a short-lived copy of itself with "--admin-task" (Windows asks the user first),
/// which does one job from a small, checked list and exits.
/// </summary>
public static class AdminTasks
{
    public sealed record FileAttr(string Path, int Attributes);

    /// <summary>One job for the elevated helper. Kind: "attributes" (Files) or "folder-icon" (Icon set, or null to undo).</summary>
    public sealed record Job(string Kind, List<FileAttr>? Files = null, string? Icon = null, string? PreviousClosed = null, string? PreviousOpen = null);

    public const string ShellIconsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    public static string SharedDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>Where a system-wide folder icon is kept, so every account on the PC can read it.</summary>
    public static string SharedIconFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AmperDock", "icons");

    /// <summary>The folder icons Windows uses for every folder right now ("3" closed, "4" open), or null for the standard ones.</summary>
    public static (string? closed, string? open) CurrentFolderIcon()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ShellIconsKey);
        return (key?.GetValue("3") as string, key?.GetValue("4") as string);
    }

    /// <summary>
    /// Run <paramref name="job"/> elevated. Windows shows its permission prompt; returns false if the user
    /// said no or the job failed. Blocks until the helper exits, so call it off the UI thread.
    /// </summary>
    public static bool RunElevated(Job job)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find AmperDock's own program file.");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job)));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"--admin-task {payload}") { UseShellExecute = true, Verb = "runas" });
            if (p is null) return false;
            p.WaitForExit(60_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;   // the user chose No
        }
    }

    /// <summary>
    /// The elevated side: check the job and do it. Only files on the shared desktop may have their
    /// attributes changed, and the folder icon must be an .ico. Returns the process exit code.
    /// </summary>
    public static int Perform(string payload)
    {
        Job? job;
        try { job = JsonSerializer.Deserialize<Job>(Encoding.UTF8.GetString(Convert.FromBase64String(payload))); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return 2; }
        if (job is null) return 2;
        try
        {
            switch (job.Kind)
            {
                case "attributes":
                    var shared = Path.GetFullPath(SharedDesktop).TrimEnd('\\') + "\\";
                    foreach (var f in job.Files ?? [])
                    {
                        var full = Path.GetFullPath(f.Path);
                        if (!full.StartsWith(shared, StringComparison.OrdinalIgnoreCase)) return 3;   // only the shared desktop
                        if (File.Exists(full) || Directory.Exists(full)) File.SetAttributes(full, (FileAttributes)f.Attributes);
                        Notify(full);
                    }
                    return 0;

                case "folder-icon":
                    using (var key = Registry.LocalMachine.CreateSubKey(ShellIconsKey, writable: true))
                    {
                        if (job.Icon is { } icon)
                        {
                            if (!icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || !File.Exists(icon)) return 4;
                            Directory.CreateDirectory(SharedIconFolder);
                            var dest = Path.Combine(SharedIconFolder, "folder.ico");
                            File.Copy(icon, dest, overwrite: true);
                            key.SetValue("3", dest + ",0", RegistryValueKind.String);
                            key.SetValue("4", dest + ",0", RegistryValueKind.String);
                        }
                        else
                        {
                            // undo: what was there before, or nothing (the standard folder)
                            if (job.PreviousClosed is { } c) key.SetValue("3", c); else key.DeleteValue("3", throwOnMissingValue: false);
                            if (job.PreviousOpen is { } o) key.SetValue("4", o); else key.DeleteValue("4", throwOnMissingValue: false);
                        }
                    }
                    RefreshIcons();
                    return 0;

                default:
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 5;
        }
    }

    /// <summary>Tell Explorer the icons changed, and rebuild its icon cache so the new folder icon shows everywhere.</summary>
    public static void RefreshIcons()
    {
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
        try
        {
            var ie4 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ie4uinit.exe");
            if (File.Exists(ie4)) Process.Start(new ProcessStartInfo(ie4, "-show") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(10_000);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void Notify(string path)
    {
        var p = Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(0x00000800 /* SHCNE_ATTRIBUTES */, 0x0005 /* SHCNF_PATHW */, p, 0); }
        finally { Marshal.FreeHGlobal(p); }
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
