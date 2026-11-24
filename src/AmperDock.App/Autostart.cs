using System.IO;
using Microsoft.Win32;

namespace AmperDock.App;

/// <summary>
/// "Start AmperDock when I sign in to Windows": an entry in the current user's Run key. Windows starts it
/// with <see cref="Flag"/>, so the dock comes up quietly without opening the AmperDock window.
/// </summary>
public static class Autostart
{
    public const string Flag = "--startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "AmperDock";

    /// <summary>
    /// The program to start. An installed copy has a launcher one folder up from the "current" folder that
    /// survives updates; use it when it's there.
    /// </summary>
    private static string Program()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AmperDock.exe");
        var dir = Path.GetDirectoryName(exe);
        if (dir is not null && string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(dir) is { } root && File.Exists(Path.Combine(root, "AmperDock.exe")))
            return Path.Combine(root, "AmperDock.exe");
        return exe;
    }

    public static bool IsOn
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string s && s.Length > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
    }

    /// <summary>Turn it on or off. Returns false if Windows wouldn't let us.</summary>
    public static bool Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(Name, $"\"{Program()}\" {Flag}");
            else key.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            App.Log(ex);
            return false;
        }
    }
}
