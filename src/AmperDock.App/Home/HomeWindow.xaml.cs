using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmperDock.App.Dock;
using AmperDock.App.Looks;
using AmperDock.App.Settings;
using AmperDock.Core;
using AmperDock.Shell;

namespace AmperDock.App.Home;

/// <summary>What the AmperDock window works with: the app's docks, settings, looks and widgets.</summary>
public sealed record HomeContext(
    SettingsStore Store,
    Func<IReadOnlyList<DockHost>> Docks,
    LookService Looks,
    IDockLooks DockLooks,
    DesktopWidgetManager Widgets,
    Func<DockHost, DockHost?> NewDock,
    Func<DockHost, bool> RemoveDock);

/// <summary>
/// The AmperDock window: opens when the shortcut is double-clicked (or from the dock's menu), with a
/// vertical tab for every part of AmperDock. Closing it leaves the dock running.
/// </summary>
public partial class HomeWindow : Window
{
    public enum Tab { Dock, Position, Appearance, Looks, Widgets, Holiday, About }

    private readonly HomeContext _ctx;
    private readonly Dictionary<string, AppearancePage> _appearance = new();
    private LookPacksPage? _looksPage;
    private DockHost? _dock;          // the dock the Dock, Position and Appearance tabs are about
    private Tab _tab = Tab.Dock;
    private bool _syncing;            // filling controls from settings: don't treat it as the user changing them
    private readonly Action _onDockChanged, _onWidgetsChanged;

    public HomeWindow(HomeContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        var version = Version();
        VersionChip.Text = version;
        AboutVersion.Text = $"AmperDock {version}";
        AutostartBox.IsChecked = Autostart.IsOn;
        foreach (var (ms, label) in DockHost.RevealChoices) RevealPicker.Items.Add(new ComboBoxItem { Content = label, Tag = ms });
        BuildWidgetKinds();

        _onDockChanged = () => Dispatcher.BeginInvoke(SyncDockTabs);
        _onWidgetsChanged = () => Dispatcher.BeginInvoke(() => { if (_tab == Tab.Widgets) BuildWidgetList(); });
        ctx.Widgets.Changed += _onWidgetsChanged;
        Closed += (_, _) =>
        {
            ctx.Widgets.Changed -= _onWidgetsChanged;
            if (_dock is not null) _dock.Changed -= _onDockChanged;
            foreach (var page in _appearance.Values) page.Discard();   // an unsaved look goes back to the saved one
        };
        SelectDock(ctx.Docks().FirstOrDefault(d => d.IsMain) ?? ctx.Docks().FirstOrDefault());
    }

    /// <summary>Bring the window up on a tab (and, for the dock tabs, a particular dock).</summary>
    public void ShowTab(Tab tab, DockHost? dock = null)
    {
        if (dock is not null) SelectDock(dock);
        var radio = RadioFor(tab);
        if (radio.IsChecked == true) ShowPage(tab);
        else radio.IsChecked = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>A dock was added or removed somewhere else (its own menu).</summary>
    public void DocksChanged()
    {
        var docks = _ctx.Docks();
        foreach (var gone in _appearance.Keys.Where(id => docks.All(d => d.ContainerId != id)).ToList()) _appearance.Remove(gone);
        SelectDock(_dock is not null && docks.Contains(_dock) ? _dock : docks.FirstOrDefault(d => d.IsMain) ?? docks.FirstOrDefault());
    }

    private static string Version()
    {
        var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    // ---------------------------------------------------------------- tabs

    private RadioButton RadioFor(Tab tab) => tab switch
    {
        Tab.Position => TabPosition,
        Tab.Appearance => TabAppearance,
        Tab.Looks => TabLooks,
        Tab.Widgets => TabWidgets,
        Tab.Holiday => TabHoliday,
        Tab.About => TabAbout,
        _ => TabDock,
    };

    private void OnTab(object sender, RoutedEventArgs e)
    {
        var tab = sender == TabPosition ? Tab.Position : sender == TabAppearance ? Tab.Appearance : sender == TabLooks ? Tab.Looks
            : sender == TabWidgets ? Tab.Widgets : sender == TabHoliday ? Tab.Holiday : sender == TabAbout ? Tab.About : Tab.Dock;
        ShowPage(tab);
    }

    private void ShowPage(Tab tab)
    {
        _tab = tab;
        PageDock.Visibility = Vis(tab == Tab.Dock);
        PagePosition.Visibility = Vis(tab == Tab.Position);
        PageAppearance.Visibility = Vis(tab == Tab.Appearance);
        PageLooks.Visibility = Vis(tab == Tab.Looks);
        PageWidgets.Visibility = Vis(tab == Tab.Widgets);
        PageHoliday.Visibility = Vis(tab == Tab.Holiday);
        PageAbout.Visibility = Vis(tab == Tab.About);
        switch (tab)
        {
            case Tab.Appearance: ShowAppearance(); break;
            case Tab.Looks:
                PageLooks.Content ??= _looksPage = new LookPacksPage(_ctx.Looks, _ctx.DockLooks);
                break;
            case Tab.Widgets: BuildWidgetList(); break;
            case Tab.About: AutostartBox.IsChecked = Autostart.IsOn; break;
            default: SyncDockTabs(); break;
        }
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- which dock

    private void SelectDock(DockHost? dock)
    {
        if (_dock != dock)
        {
            if (_dock is not null) _dock.Changed -= _onDockChanged;
            _dock = dock;
            if (_dock is not null) _dock.Changed += _onDockChanged;
        }
        FillDockPickers();
        SyncDockTabs();
        if (_tab == Tab.Appearance) ShowAppearance();
    }

    private string DockName(DockHost d) =>
        _ctx.Store.GetDocks().FirstOrDefault(i => i.Id == d.ContainerId)?.Name is { } n && !d.IsMain ? n : "Main dock";

    private void FillDockPickers()
    {
        _syncing = true;
        try
        {
            var docks = _ctx.Docks();
            foreach (var picker in new[] { DockPicker, PositionDockPicker })
            {
                picker.Items.Clear();
                foreach (var d in docks) picker.Items.Add(new ComboBoxItem { Content = DockName(d), Tag = d });
                picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag == _dock);
            }
            // one dock: nothing to choose between on the Position tab
            PositionDockPicker.Visibility = Vis(docks.Count > 1);
            RemoveDockButton.Visibility = Vis(_dock is { IsMain: false });
        }
        finally { _syncing = false; }
    }

    private void OnDockPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (sender as ComboBox)?.SelectedItem is not ComboBoxItem { Tag: DockHost d }) return;
        SelectDock(d);
    }

    private void OnNewDock(object sender, RoutedEventArgs e)
    {
        if (_dock is null) return;
        if (_ctx.NewDock(_dock) is { } made) SelectDock(made);
    }

    private void OnRemoveDock(object sender, RoutedEventArgs e)
    {
        if (_dock is null or { IsMain: true }) return;
        var gone = _dock;
        if (!_ctx.RemoveDock(gone)) return;
        gone.Changed -= _onDockChanged;
        _dock = null;
        DocksChanged();
    }

    // ---------------------------------------------------------------- Dock and Position tabs

    /// <summary>Fill the Dock and Position tabs from the selected dock.</summary>
    private void SyncDockTabs()
    {
        if (_dock is not { } dock) return;
        _syncing = true;
        try
        {
            BuildItemList(dock);
            var b = dock.Behavior;
            OpenAppsBox.IsChecked = dock.ShowsOpenAppsSetting;
            PreviewsBox.IsChecked = b.Previews;

            var p = dock.Placement;
            foreach (RadioButton rb in EdgeChoices.Children) rb.IsChecked = (string)rb.Tag == p.Edge.ToString();
            ScreenPicker.Items.Clear();
            foreach (var (key, name) in DockHost.Screens()) ScreenPicker.Items.Add(new ComboBoxItem { Content = name, Tag = key });
            ScreenPicker.SelectedItem = ScreenPicker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.MonitorKey)
                                        ?? ScreenPicker.Items.Cast<ComboBoxItem>().FirstOrDefault();
            ScreenPicker.IsEnabled = ScreenPicker.Items.Count > 1;

            var floating = p.Edge == DockEdge.Float;
            HideOff.IsChecked = !b.AutoHide || floating;
            HideOn.IsChecked = b.AutoHide && !floating;
            HideOn.IsEnabled = !floating;
            FloatHideNote.Visibility = Vis(floating);
            RevealRow.IsEnabled = HideOn.IsChecked == true;
            RevealPicker.SelectedItem = RevealPicker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == b.RevealDelayMs)
                                        ?? RevealPicker.Items.Cast<ComboBoxItem>().ElementAtOrDefault(2);
            ReserveBox.IsChecked = b.ReserveSpace && !b.AutoHide && !floating;
            ReserveBox.IsEnabled = !b.AutoHide && !floating;
        }
        finally { _syncing = false; }
    }

    private void BuildItemList(DockHost dock)
    {
        ItemList.Children.Clear();
        var items = dock.PinnedItems;
        if (items.Count == 0)
        {
            var none = new TextBlock { Text = "Nothing on this dock yet. Add an app below, or drag one onto the dock.", Margin = new Thickness(16, 14, 16, 14), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(StyleProperty, "Text.Muted");
            ItemList.Children.Add(none);
            return;
        }
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), Source = IconFor(item) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var name = new TextBlock { Text = item.Label, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(StyleProperty, "Text.BodyStrong");
            var kind = new TextBlock { Text = KindOf(item), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = item.Target };
            kind.SetResourceReference(StyleProperty, "Text.Caption");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, kind } };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(SmallButton("↑", $"Move {item.Label} left", i > 0, () => dock.MovePinned(item, -1)));
            buttons.Children.Add(SmallButton("↓", $"Move {item.Label} right", i < items.Count - 1, () => dock.MovePinned(item, +1)));
            buttons.Children.Add(SmallButton("Edit", $"Edit {item.Label}", true, () => dock.EditPinned(item)));
            buttons.Children.Add(SmallButton("Remove", $"Remove {item.Label} from the dock", true, () => dock.RemovePinned(item)));

            DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
            DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Right);
            var row = new Border
            {
                Padding = new Thickness(14, 8, 10, 8),
                BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                Child = new DockPanel { Children = { icon, buttons, text } },
            };
            row.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            ItemList.Children.Add(row);
        }
    }

    private static string KindOf(DockItem item) => item.Target == DockHost.HomeTarget ? "Opens this window" : item.Type switch
    {
        ItemTypes.Folder => "Folder",
        ItemTypes.Document => "File",
        ItemTypes.Url => "Website",
        ItemTypes.Uwp => "App",
        ItemTypes.Command => "Command",
        _ => "App",
    } + (item.Target is { Length: > 0 } t && !t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? $" · {t}" : "");

    private static ImageSource? IconFor(DockItem item)
    {
        try
        {
            var px = item.Target == DockHost.HomeTarget && item.IconPath is null ? DockHost.LogoPixels(64) : ShellIcons.ForItem(item.IconPath, item.Target, 32);
            if (px is null) return null;
            var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32, null, px.Bgra, px.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private Button SmallButton(string text, string name, bool enabled, Action click)
    {
        var b = new Button { Content = text, IsEnabled = enabled, Margin = new Thickness(6, 0, 0, 0), MinWidth = 36, Padding = new Thickness(12, 0, 12, 0) };
        b.SetResourceReference(StyleProperty, text.Length == 1 ? "Btn.Ghost" : "Btn.Secondary");
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    private void OnAddApp(object sender, RoutedEventArgs e) => _dock?.AddApp();
    private void OnAddFile(object sender, RoutedEventArgs e) => _dock?.AddFileOrFolder(folder: false);
    private void OnAddFolder(object sender, RoutedEventArgs e) => _dock?.AddFileOrFolder(folder: true);

    private void OnBehaviourBox(object sender, RoutedEventArgs e)
    {
        if (_syncing || _dock is not { } dock) return;
        var b = dock.Behavior;
        dock.ChangeBehavior(sender == OpenAppsBox ? b with { ShowOpenApps = OpenAppsBox.IsChecked == true }
            : sender == PreviewsBox ? b with { Previews = PreviewsBox.IsChecked == true }
            : b with { ReserveSpace = ReserveBox.IsChecked == true });
    }

    private void OnEdge(object sender, RoutedEventArgs e)
    {
        if (_syncing || _dock is not { } dock || sender is not RadioButton { Tag: string tag }) return;
        dock.MoveTo(Enum.Parse<DockEdge>(tag), dock.Placement.MonitorKey);
        SyncDockTabs();
    }

    private void OnScreenPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _dock is not { } dock || ScreenPicker.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        dock.MoveTo(dock.Placement.Edge, key);
        SyncDockTabs();
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        if (_syncing || _dock is not { } dock) return;
        var delay = RevealPicker.SelectedItem is ComboBoxItem { Tag: int ms } ? ms : dock.Behavior.RevealDelayMs;
        dock.ChangeBehavior(dock.Behavior with { AutoHide = HideOn.IsChecked == true, RevealDelayMs = delay });
        SyncDockTabs();
    }

    private void OnReveal(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _dock is not { } dock || RevealPicker.SelectedItem is not ComboBoxItem { Tag: int ms }) return;
        dock.ChangeBehavior(dock.Behavior with { RevealDelayMs = ms });
    }

    // ---------------------------------------------------------------- Appearance tab

    private void ShowAppearance()
    {
        if (_dock is not { } dock) { PageAppearance.Content = null; return; }
        if (!_appearance.TryGetValue(dock.ContainerId, out var page))
            _appearance[dock.ContainerId] = page = new AppearancePage(dock, _ctx.Store);
        else page.Refresh();
        if (PageAppearance.Content is AppearancePage old && old != page) old.HeaderExtraContent = null;
        // the dock picker sits beside the page title (only when there's more than one dock)
        if (_ctx.Docks().Count > 1)
        {
            var picker = new ComboBox { MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(picker, "Which dock");
            foreach (var d in _ctx.Docks()) picker.Items.Add(new ComboBoxItem { Content = DockName(d), Tag = d });
            picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag == dock);
            picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is ComboBoxItem { Tag: DockHost d }) SelectDock(d); };
            page.HeaderExtraContent = picker;
        }
        else page.HeaderExtraContent = null;
        PageAppearance.Content = page;
    }

    // ---------------------------------------------------------------- Widgets tab

    private void BuildWidgetKinds()
    {
        var notes = new Dictionary<string, string>
        {
            ["note"] = "A sticky note in your own colours",
            ["clock"] = "The time and date",
            ["countdown"] = "Days until something",
            ["weather"] = "Today's weather, for your town",
            ["mochi"] = "A cat who wanders, plays, eats and naps",
            [MochiThingWidget.Post] = "Mochi scratches it, and perches on top",
            [MochiThingWidget.Bowl] = "Double-click to fill it; Mochi eats",
            [MochiThingWidget.House] = "Mochi peeks out and sleeps inside",
            [PalWidget.Bella] = "The puppy from Racing for Pi",
            [PalWidget.Bunny] = "The bunny from Racing for Pi",
            [PalWidget.Dino] = "The dino from Racing for Pi",
        };
        foreach (var (kind, name) in DesktopWidgetKinds.All)
        {
            var title = new TextBlock { Text = name };
            title.SetResourceReference(StyleProperty, "Text.Subtitle");
            var note = new TextBlock { Text = notes.GetValueOrDefault(kind, ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
            note.SetResourceReference(StyleProperty, "Text.Caption");
            var add = new TextBlock { Text = "+ Add" };
            add.SetResourceReference(StyleProperty, "Text.BodyStrong");
            add.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            var card = new Border
            {
                Width = 176, Height = 118, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 12, 14, 12),
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Focusable = true, Cursor = System.Windows.Input.Cursors.Hand,
                Child = new DockPanel { LastChildFill = false, Children = { title, note, add } },
            };
            DockPanel.SetDock(title, System.Windows.Controls.Dock.Top);
            DockPanel.SetDock(note, System.Windows.Controls.Dock.Top);
            DockPanel.SetDock(add, System.Windows.Controls.Dock.Bottom);
            card.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Brush.FocusRing");
            card.MouseLeave += (_, _) => { if (!card.IsKeyboardFocused) card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border"); };
            card.GotKeyboardFocus += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Brush.FocusRing");
            card.LostKeyboardFocus += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            AutomationProperties.SetName(card, $"Add a {name} widget to the desktop");
            card.MouseLeftButtonUp += (_, _) => _ctx.Widgets.Add(kind);
            card.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { _ctx.Widgets.Add(kind); e.Handled = true; } };
            WidgetKinds.Children.Add(card);
        }
    }

    private void BuildWidgetList()
    {
        WidgetList.Children.Clear();
        var list = _ctx.Widgets.List();
        if (list.Count == 0)
        {
            var none = new TextBlock { Text = "No widgets yet. Pick one above.", Margin = new Thickness(16, 14, 16, 14) };
            none.SetResourceReference(StyleProperty, "Text.Muted");
            WidgetList.Children.Add(none);
            return;
        }
        for (var i = 0; i < list.Count; i++)
        {
            var w = list[i];
            var name = DesktopWidgetKinds.All.FirstOrDefault(k => k.Kind == w.Kind).Name ?? w.Kind;
            var title = new TextBlock { Text = name };
            title.SetResourceReference(StyleProperty, "Text.BodyStrong");
            var detail = new TextBlock { Text = WidgetDetail(w), TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(StyleProperty, "Text.Caption");
            var remove = SmallButton("Remove", $"Remove the {name} widget", true, () => _ctx.Widgets.Remove(w.Id));
            DockPanel.SetDock(remove, System.Windows.Controls.Dock.Right);
            var row = new Border
            {
                Padding = new Thickness(16, 10, 10, 10), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                Child = new DockPanel { Children = { remove, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } } } },
            };
            row.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            WidgetList.Children.Add(row);
        }
    }

    private static string WidgetDetail(DesktopWidget w) => w.Kind switch
    {
        "note" => string.IsNullOrWhiteSpace(w.Text) ? "Empty note" : w.Text.ReplaceLineEndings(" ").Trim(),
        "countdown" => w.Target is { } t ? $"{(string.IsNullOrWhiteSpace(w.Text) ? "Countdown" : w.Text)} · {Math.Max(0, (t.Date - DateTime.Today).Days)} days" : w.Text,
        "weather" => w.Place ?? "Choose a place: right-click the widget",
        "clock" => w.Clock24 ? "24-hour time" : "12-hour time",
        _ => "On your desktop",
    };

    // ---------------------------------------------------------------- About tab

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateTitle.Text = "Checking…";
        try
        {
            var version = await Updates.CheckAndDownloadAsync();
            (UpdateTitle.Text, UpdateText.Text) = version is not null
                ? ($"AmperDock {version} is ready", "It goes in the next time AmperDock starts.")
                : Updates.IsInstalled
                    ? ("You're up to date", "AmperDock checks for updates by itself and installs them the next time it starts.")
                    : ("Updates", "This copy wasn't installed with the AmperDock installer, so it doesn't update itself.");
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        if (!Autostart.Set(AutostartBox.IsChecked == true)) AutostartBox.IsChecked = Autostart.IsOn;
    }

    private void OnLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        e.Handled = true;
    }

    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------------------------------------------------------------- header buttons

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
