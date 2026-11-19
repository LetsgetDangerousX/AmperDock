using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmperDock.Shell;

namespace AmperDock.App.Settings;

/// <summary>
/// "Add an app": the Start menu's full app list (Store apps included), searchable, multi-select.
/// The list and the icons load on a background thread so the window opens at once.
/// </summary>
public partial class AppPickerWindow : Window
{
    public sealed class Row(InstalledApp app, string? note) : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        public InstalledApp App { get; } = app;
        public string Name => App.Name;
        public string? Note { get; } = note;
        public ImageSource? Icon { get => _icon; set { _icon = value; Changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Name;   // what screen readers announce for the row
        private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly HashSet<string> _onDock;
    private readonly Action<IReadOnlyList<InstalledApp>> _add;
    private readonly Action _browse;
    private ICollectionView? _view;
    private volatile bool _closed;

    public AppPickerWindow(IEnumerable<string> targetsOnDock, Action<IReadOnlyList<InstalledApp>> add, Action browse)
    {
        _onDock = targetsOnDock.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _add = add;
        _browse = browse;
        InitializeComponent();
        Loaded += (_, _) => { SearchBox.Focus(); Load(); };
        Closed += (_, _) => _closed = true;
    }

    private void Load()
    {
        // the shell's app list is a COM object that wants a single-threaded apartment
        var thread = new Thread(() =>
        {
            List<InstalledApp> apps;
            try { apps = AppIds.InstalledApps(); }
            catch (Exception ex) { App.Log(ex); apps = []; }
            var rows = apps.Select(a => new Row(a, _onDock.Contains(a.Target) ? "On the dock" : null)).ToList();
            Dispatcher.BeginInvoke(() => Show(rows));
            foreach (var row in rows)
            {
                if (_closed) return;
                var px = ShellIcons.Get(row.App.Target.StartsWith(AppIds.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? row.App.Target : AppIds.AppsFolderPrefix + row.App.AppId, 64);
                if (px is null) continue;
                var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32, null, px.Bgra, px.Width * 4);
                bmp.Freeze();
                Dispatcher.BeginInvoke(() => row.Icon = bmp);
            }
        }) { IsBackground = true, Name = "app list" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Show(List<Row> rows)
    {
        if (_closed) return;
        AppList.ItemsSource = rows;
        _view = CollectionViewSource.GetDefaultView(rows);
        _view.Filter = o => Matches((Row)o, SearchBox.Text);
        UpdateStatus();
    }

    private static bool Matches(Row r, string query)
    {
        var q = query.Trim();
        return q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateStatus()
    {
        if (_view is null) return;
        var any = !_view.IsEmpty;
        Status.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Status.Text = AppList.ItemsSource is ICollection<Row> { Count: 0 }
            ? "Windows didn't list any apps. Use \"Browse for a file instead\"."
            : $"No apps called \"{SearchBox.Text.Trim()}\".";
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateStatus();
        if (AppList.SelectedItems.Count == 0 && !_view.IsEmpty) AppList.SelectedIndex = 0;   // Enter adds the top match
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e) => AddButton.IsEnabled = AppList.SelectedItems.Count > 0;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(AppList, d) is ListBoxItem) OnAdd(sender, e);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var picked = AppList.SelectedItems.Cast<Row>().Select(r => r.App).ToList();
        if (picked.Count == 0) return;
        Close();
        _add(picked);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        Close();
        _browse();
    }
}
