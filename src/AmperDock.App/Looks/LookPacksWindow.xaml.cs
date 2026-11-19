using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AmperDock.App.Looks;

/// <summary>Look packs: pick cursors, a cursor trail and folder icons. Every choice applies immediately and can be undone.</summary>
public partial class LookPacksWindow : Window
{
    private readonly LookService _looks;
    private FolderIconInfo? _selectedIcon;

    public LookPacksWindow(LookService looks)
    {
        _looks = looks;
        InitializeComponent();
        _selectedIcon = looks.Catalog.FolderIcons.FirstOrDefault();
        Rebuild();
        looks.Changed += OnChanged;
        Closed += (_, _) => looks.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Rebuild);

    private void Rebuild()
    {
        BuildCursors();
        BuildTrails();
        BuildFolders();
    }

    // ---------------------------------------------------------------- cursors

    private void BuildCursors()
    {
        CursorCards.Children.Clear();
        var current = _looks.CursorSetId;
        CursorCards.Children.Add(Card("Your original cursors", "Windows", null, null, current is null, () => _looks.RestoreCursors()));
        foreach (var set in _looks.Catalog.CursorSets)
            CursorCards.Children.Add(Card(set.Name, null, LookCatalog.Image(set.PreviewArrow), LookCatalog.Image(set.PreviewHand),
                current == set.Id, () => Try(() => _looks.ApplyCursors(set))));
    }

    // ---------------------------------------------------------------- trails

    private void BuildTrails()
    {
        TrailCards.Children.Clear();
        if (!LookService.AnimationsAllowed)
            TrailNote.Text = "Windows animation effects are turned off (Settings → Accessibility → Visual effects), so trails stay paused.";
        var current = _looks.TrailId;
        TrailCards.Children.Add(Card("Off", "No trail", null, null, current is null, () => _looks.SetTrail(null)));
        foreach (var t in _looks.Catalog.Trails)
            TrailCards.Children.Add(Card(t.Name, t.Tag, LookCatalog.Image(t.Preview), null, current == t.Id, () => _looks.SetTrail(t.Id)));
    }

    // ---------------------------------------------------------------- folders

    private void BuildFolders()
    {
        FolderCards.Children.Clear();
        foreach (var icon in _looks.Catalog.FolderIcons)
            FolderCards.Children.Add(Card(icon.Name, null, LookCatalog.Image(icon.Preview), null, _selectedIcon?.Id == icon.Id,
                () => { _selectedIcon = icon; BuildFolders(); }, compact: true));
        ApplyFolderButton.Content = _selectedIcon is null ? "Choose folders…" : $"Give folders the {_selectedIcon.Name} icon…";

        FolderList.Children.Clear();
        var done = _looks.Folders;
        if (done.Count == 0)
        {
            var none = new TextBlock { Text = "None yet." };
            none.SetResourceReference(StyleProperty, "Text.Muted");
            FolderList.Children.Add(none);
        }
        foreach (var f in done)
        {
            var icon = _looks.Catalog.FolderIcons.FirstOrDefault(i => i.Id == f.IconId);
            var img = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0), Source = icon is null ? null : LookCatalog.Image(icon.Preview) };
            var path = new TextBlock { Text = f.Path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            path.SetResourceReference(StyleProperty, "Text.Body");
            var restore = new Button { Content = "Restore", Margin = new Thickness(12, 0, 0, 0) };
            restore.SetResourceReference(StyleProperty, "Btn.Secondary");
            AutomationProperties.SetName(restore, $"Restore the original icon for {f.Path}");
            restore.Click += (_, _) => Report(_looks.RestoreFolder(f));
            DockPanel.SetDock(img, System.Windows.Controls.Dock.Left);
            DockPanel.SetDock(restore, System.Windows.Controls.Dock.Right);
            var row = new Border
            {
                Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Child = new DockPanel { Children = { img, restore, path } },
            };
            row.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            row.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
            FolderList.Children.Add(row);
        }
    }

    private void OnApplyFolder(object sender, RoutedEventArgs e)
    {
        if (_selectedIcon is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Give folders the {_selectedIcon.Name} icon", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        var errors = dlg.FolderNames.Select(f => _looks.ApplyFolderIcon(_selectedIcon, f)).Where(err => err is not null).ToList();
        Report(errors.Count == 0
            ? null
            : errors.Count == 1 ? errors[0] : $"{errors.Count} folders couldn't be changed. {errors[0]}",
            okText: dlg.FolderNames.Length == 1 ? "Done. Explorer may take a moment to show it." : $"Done: {dlg.FolderNames.Length - errors.Count} folders.");
    }

    private void Report(string? error, string? okText = null)
    {
        FolderStatus.Text = error ?? okText ?? "";
        FolderStatus.SetResourceReference(TextBlock.ForegroundProperty, error is null ? "Brush.TextMuted" : "Brush.Danger");
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, $"That didn't work: {ex.Message}", "Look packs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- cards

    /// <summary>A selectable card: one or two preview images (or a caption), a name, and a selected state.</summary>
    private static Border Card(string name, string? sub, ImageSource? img1, ImageSource? img2, bool selected, Action onPick, bool compact = false)
    {
        var size = compact ? 48 : 40;
        var pics = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 52 };
        if (img1 is not null) pics.Children.Add(new Image { Source = img1, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center });
        if (img2 is not null) pics.Children.Add(new Image { Source = img2, Width = size, Height = size, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (img1 is null)
        {
            var glyph = new TextBlock { Text = sub == "No trail" ? "○" : "↖", FontSize = 26, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            pics.Children.Add(glyph);
        }
        var title = new TextBlock { Text = name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        title.SetResourceReference(StyleProperty, "Text.BodyStrong");
        var panel = new StackPanel { Children = { pics, title } };
        if (sub is not null)
        {
            var s = new TextBlock { Text = sub, TextAlignment = TextAlignment.Center };
            s.SetResourceReference(StyleProperty, "Text.Caption");
            panel.Children.Add(s);
        }
        var card = new Border
        {
            Width = compact ? 104 : 128, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8, 10, 8, 10),
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(selected ? 2 : 1), Focusable = true, Cursor = Cursors.Hand, Child = panel,
        };
        card.SetResourceReference(Border.BorderBrushProperty, selected ? "Brush.FocusRing" : "Brush.Border");
        card.SetResourceReference(Border.BackgroundProperty, selected ? "Brush.AccentSoft" : "Brush.SurfaceRaised");
        AutomationProperties.SetName(card, $"{name}{(sub is null ? "" : $", {sub}")}{(selected ? ", selected" : "")}");
        card.MouseLeftButtonUp += (_, _) => onPick();
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { onPick(); e.Handled = true; } };
        card.GotKeyboardFocus += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Brush.FocusRing");
        card.LostKeyboardFocus += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, selected ? "Brush.FocusRing" : "Brush.Border");
        return card;
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
