using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmperDock.Core;
using AmperDock.Shell;

namespace AmperDock.App.Settings;

/// <summary>
/// Edit one dock item: rename it, point it somewhere else, set how a program starts, and give it a
/// custom icon (a picture, an .ico, or an icon inside an app or DLL). Nothing changes until Save.
/// </summary>
public partial class ItemWindow : Window
{
    private readonly SettingsStore _store;
    private readonly DockItem _original;
    private readonly Action<DockItem> _saved;
    private readonly DispatcherTimer _previewDelay;
    private string? _iconPath;

    public string ItemId => _original.Id;

    public ItemWindow(SettingsStore store, DockItem item, Action<DockItem> saved)
    {
        _store = store;
        _original = item;
        _saved = saved;
        _iconPath = item.IconPath;
        InitializeComponent();
        _previewDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => { _previewDelay!.Stop(); Refresh(); }, Dispatcher);

        Title = $"Edit {item.Label}";
        NameBox.Text = item.Label;
        TargetBox.Text = item.Target ?? "";
        ArgsBox.Text = item.Arguments ?? "";
        StartInBox.Text = item.StartIn ?? "";
        AdminBox.IsChecked = item.RunAsAdmin;
        MultipleBox.IsChecked = item.AllowMultiple;
        Refresh();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private string Target => TargetBox.Text.Trim().Trim('"');

    private string CurrentType =>
        Target.Length == 0 ? _original.Type
        : string.Equals(Target, _original.Target, StringComparison.OrdinalIgnoreCase) ? _original.Type
        : Shortcuts.TypeFor(Target);

    /// <summary>Update everything that depends on the location and the icon.</summary>
    private void Refresh()
    {
        var type = CurrentType;
        KindText.Text = type switch
        {
            ItemTypes.Program => "App",
            ItemTypes.Folder => "Folder",
            ItemTypes.Url => "Link",
            _ => "File",
        };
        ProgramPanel.Visibility = type == ItemTypes.Program ? Visibility.Visible : Visibility.Collapsed;
        ShowButton.IsEnabled = File.Exists(Target) || Directory.Exists(Target);

        var custom = IconRef.Parse(_iconPath);
        IconSource.Text = custom is null
            ? "Using the icon Windows shows for this item."
            : custom.Value.IsIconResource && !custom.Value.File.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? $"Icon {custom.Value.Index + 1} from {Path.GetFileName(custom.Value.File)}"
                : $"Using {Path.GetFileName(custom.Value.File)}";
        ResetIconButton.IsEnabled = custom is not null;

        var px = ShellIcons.ForItem(_iconPath, Target.Length > 0 ? Target : null, 128);
        IconPreview.Source = px is null ? null : BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32, null, px.Bgra, px.Width * 4);
    }

    private void OnTargetChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Error.Text = "";
        _previewDelay.Stop();
        _previewDelay.Start();   // typing a path: wait for a pause before asking Windows for its icon
    }

    // ---------------------------------------------------------------- location

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose what this item opens",
            DereferenceLinks = false,
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.appref-ms;*.bat;*.cmd|All files|*.*",
            FilterIndex = CurrentType == ItemTypes.Program ? 1 : 2,
        };
        if (StartFolder() is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) SetTarget(dlg.FileName);
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder this item opens" };
        if (StartFolder() is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) SetTarget(dlg.FolderName);
    }

    private string? StartFolder()
    {
        if (Directory.Exists(Target)) return Target;
        var parent = Target.Length > 0 ? Path.GetDirectoryName(Target) : null;
        return parent is not null && Directory.Exists(parent) ? parent : null;
    }

    /// <summary>A newly chosen location. The name follows it if you hadn't renamed the item yourself.</summary>
    private void SetTarget(string path)
    {
        var oldTarget = Target;
        var nameWasDefault = oldTarget.Length == 0 || NameBox.Text.Trim() == Shortcuts.LabelFor(oldTarget) || NameBox.Text.Trim() == _original.Label && _original.Label == Shortcuts.LabelFor(_original.Target ?? "");
        TargetBox.Text = path;
        if (nameWasDefault) NameBox.Text = Shortcuts.LabelFor(path);
        // a start folder that was just "the old app's folder" moves with the app
        var oldDir = oldTarget.Length > 0 ? Path.GetDirectoryName(oldTarget) : null;
        if (StartInBox.Text.Trim().Length == 0 || string.Equals(StartInBox.Text.Trim(), oldDir, StringComparison.OrdinalIgnoreCase))
            StartInBox.Text = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) ?? "" : "";
        _previewDelay.Stop();
        Refresh();
    }

    private void OnBrowseStartIn(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder the app starts in" };
        if (Directory.Exists(StartInBox.Text.Trim())) dlg.InitialDirectory = StartInBox.Text.Trim();
        if (dlg.ShowDialog(this) == true) StartInBox.Text = dlg.FolderName;
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Target) || Directory.Exists(Target))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Target}\"") { UseShellExecute = true })?.Dispose();
    }

    // ---------------------------------------------------------------- icon

    private void OnIconImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an icon picture",
            Filter = "Pictures and icons|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        var custom = IconRef.Parse(_iconPath);
        var dir = custom is null ? LookPacksIcons() : Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(custom.Value.File));
        if (dir is not null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        var picked = new IconRef(dlg.FileName, 0);
        if (ShellIcons.Custom(picked, 64) is null) { Error.Text = "Windows couldn't read that picture."; return; }
        _iconPath = picked.ToString();
        Error.Text = "";
        Refresh();
    }

    /// <summary>Our own folder icons are a nice place to start browsing for a picture.</summary>
    private static string? LookPacksIcons()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "LookPacks", "folders");
        return Directory.Exists(dir) ? dir : null;
    }

    private void OnIconPick(object sender, RoutedEventArgs e)
    {
        // start in the current icon's file, else the app itself, else Windows' big icon library
        var custom = IconRef.Parse(_iconPath);
        string file; int index = 0;
        if (custom is { IsIconResource: true } c) { file = c.File; index = c.Index; }
        else if (Shortcuts.ExeFor(_original with { Target = Target }) is { } exe) file = exe;
        else file = @"%SystemRoot%\System32\imageres.dll";

        var owner = new WindowInteropHelper(this).Handle;
        if (ShellIcons.PickIcon(owner, file, index) is not { } picked) return;
        if (ShellIcons.Custom(picked, 64) is null) { Error.Text = "That icon couldn't be loaded."; return; }
        _iconPath = picked.ToString();
        Error.Text = "";
        Refresh();
    }

    private void OnIconReset(object sender, RoutedEventArgs e)
    {
        _iconPath = null;
        Refresh();
    }

    // ---------------------------------------------------------------- save

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var target = Target;
        if (target.Length == 0) { Fail("Choose what this item opens.", TargetBox); return; }
        var type = CurrentType;
        var isLink = target.Contains("://", StringComparison.Ordinal) || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        if (!isLink && !File.Exists(target) && !Directory.Exists(target))
        {
            Fail("Windows can't find that location. Check the path, or use the Choose buttons.", TargetBox);
            return;
        }
        var startIn = StartInBox.Text.Trim().Trim('"');
        if (type == ItemTypes.Program && startIn.Length > 0 && !Directory.Exists(startIn))
        {
            Fail("The start-in folder doesn't exist.", StartInBox);
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = Shortcuts.LabelFor(target);

        var program = type == ItemTypes.Program;
        try { _iconPath = KeepIconCopy(_iconPath); }
        catch (IOException ex) { App.Log(ex); }   // keep pointing at the original; it still works while it's there
        catch (UnauthorizedAccessException ex) { App.Log(ex); }
        var updated = _original with
        {
            Label = name,
            Type = type,
            Target = target,
            Arguments = program ? ArgsBox.Text.Trim() : null,
            StartIn = program ? startIn : null,
            IconPath = _iconPath,
            RunAsAdmin = program && AdminBox.IsChecked == true,
            AllowMultiple = program && MultipleBox.IsChecked == true,
        };
        try
        {
            _store.UpdateItem(updated);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Fail($"Couldn't save: {ex.Message}", null);
            return;
        }
        _saved(updated);
        Close();
    }

    public static string IconsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmperDock", "icons");

    /// <summary>
    /// Pictures and .ico files are copied into AmperDock's own folder, so the icon survives the original
    /// being moved, deleted, or (for our bundled art) the app updating to a new folder. Icons inside
    /// programs and DLLs stay where they are; those files are meant to stay put.
    /// </summary>
    private static string? KeepIconCopy(string? iconPath)
    {
        if (IconRef.Parse(iconPath) is not { } r) return iconPath;
        var file = Environment.ExpandEnvironmentVariables(r.File);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (r.IsIconResource && ext != ".ico") return iconPath;
        if (!File.Exists(file) || file.StartsWith(IconsDir, StringComparison.OrdinalIgnoreCase)) return iconPath;
        Directory.CreateDirectory(IconsDir);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(file)))[..10].ToLowerInvariant();
        var copy = Path.Combine(IconsDir, $"{Path.GetFileNameWithoutExtension(file)}-{hash}{ext}");
        if (!File.Exists(copy)) File.Copy(file, copy);
        return new IconRef(copy, r.Index).ToString();
    }

    private void Fail(string message, Control? focus)
    {
        Error.Text = message;
        focus?.Focus();
    }
}
