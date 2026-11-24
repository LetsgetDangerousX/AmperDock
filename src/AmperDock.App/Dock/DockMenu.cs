using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AmperDock.Render;
using static AmperDock.App.Dock.Win32;

namespace AmperDock.App.Dock;

/// <summary>
/// The dock's right-click menu, painted in the dock's own colours (its fill, outline and a highlight from its
/// running-app marker) instead of Windows' grey. Entries are added the way Win32 menus are (flags, id, text,
/// "&amp;" before the access key), so the dock's menu code reads the same; picking one calls back with its id.
/// </summary>
internal sealed class DockMenu
{
    private sealed record Entry(uint Flags, int Id, string? Text, DockMenu? Sub);

    private readonly List<Entry> _items = [];

    public void Add(uint flags, int id, string? text) => _items.Add(new Entry(flags, id, text, null));
    public void Add(uint flags, DockMenu sub, string text) => _items.Add(new Entry(flags | MF_POPUP, 0, text, sub));

    /// <summary>The colours to paint with, worked out from the dock's look.</summary>
    public sealed record Palette(Color Back, Color? BackEnd, Color Border, Color Text, Color Muted, Color Hover, Color Mark);

    public static Palette PaletteFor(DockStyle style, DockStyle theme)
    {
        static Color C(Argb a) => Color.FromRgb((byte)(a.Value >> 16), (byte)(a.Value >> 8), (byte)a.Value);
        static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        // an outline-only or see-through dock still needs a solid menu: fall back to the theme's own fill
        var back = style.Background.Alpha > 8 ? C(style.Background) : C(theme.Background);
        Color? end = style.BackgroundEnd is { Alpha: > 8 } e ? C(e) : null;
        var dark = Luma(end is { } en ? Mix(back, en, 0.5) : back) < 0.5;
        var text = dark ? Color.FromRgb(0xF4, 0xEF, 0xFC) : Color.FromRgb(0x2A, 0x1F, 0x3D);
        var border = style.Outline.Alpha > 8 ? C(style.Outline) : Mix(back, text, 0.18);
        var mark = C(style.Indicator);
        // the highlight: the marker colour, softened into the fill so text stays readable on it
        var hover = Mix(back, mark, dark ? 0.35 : 0.25);
        return new Palette(back, end, border, text, Mix(back, text, 0.55), hover, Luma(mark) is var lm && Math.Abs(lm - Luma(back)) > 0.25 ? mark : text);
    }

    /// <summary>Open the menu at the pointer. <paramref name="pick"/> gets the chosen id; <paramref name="closed"/> runs when it goes away.</summary>
    public void Show(Palette p, Action<int> pick, Action closed)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, HasDropShadow = true };
        Style(menu, p);
        Fill(menu.Items, p, pick, menu);
        var done = false;
        menu.Closed += (_, _) => { if (!done) { done = true; closed(); } };
        menu.IsOpen = true;
    }

    private void Fill(ItemCollection items, Palette p, Action<int> pick, ContextMenu root)
    {
        foreach (var e in _items)
        {
            if ((e.Flags & MF_SEPARATOR) != 0)
            {
                items.Add(new Separator { Background = new SolidColorBrush(p.Border), Margin = new Thickness(10, 4, 10, 4), Height = 1 });
                continue;
            }
            var mi = new MenuItem
            {
                Header = (e.Text ?? "").Replace("_", "__").Replace("&", "_"),
                IsEnabled = (e.Flags & MF_GRAYED) == 0,
                IsChecked = (e.Flags & MF_CHECKED) != 0,
                Foreground = new SolidColorBrush(p.Text),
            };
            Paint(mi, p);
            if (e.Sub is { } sub) sub.Fill(mi.Items, p, pick, root);
            else
            {
                var id = e.Id;
                mi.Click += (_, _) => { root.IsOpen = false; pick(id); };
            }
            items.Add(mi);
        }
    }

    // ---- looks: a rounded card in the dock's fill with its outline; rows highlight in the marker's colour

    private static Brush BackBrush(Palette p) => p.BackEnd is { } end
        ? new LinearGradientBrush(p.Back, end, 0)
        : new SolidColorBrush(p.Back);

    private static void Style(ContextMenu menu, Palette p)
    {
        menu.Template = Card(p, isRoot: true);
        menu.Foreground = new SolidColorBrush(p.Text);
        menu.SetResourceReference(Control.FontFamilyProperty, "Font.Sans");
        menu.FontSize = 14;
    }

    private static ControlTemplate Card(Palette p, bool isRoot)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, BackBrush(p));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(p.Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.PaddingProperty, new Thickness(6));
        border.SetValue(FrameworkElement.MinWidthProperty, 230.0);
        var items = new FrameworkElementFactory(typeof(StackPanel));
        items.SetValue(Panel.IsItemsHostProperty, true);
        border.AppendChild(items);
        return new ControlTemplate(isRoot ? typeof(ContextMenu) : typeof(MenuItem)) { VisualTree = border };
    }

    /// <summary>A row: a check mark column, the text, and an arrow for submenus that open in the same card style.</summary>
    private static void Paint(MenuItem mi, Palette p)
    {
        const string Xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuItem">
              <Grid>
                <Border x:Name="Row" CornerRadius="9" Padding="8,6,10,6" Background="Transparent">
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="22"/>
                      <ColumnDefinition Width="*"/>
                      <ColumnDefinition Width="18"/>
                    </Grid.ColumnDefinitions>
                    <Path x:Name="Check" Data="M1,5 L4.5,8.5 L11,1.5" StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                          StrokeLineJoin="Round" VerticalAlignment="Center" Visibility="Hidden" Tag="mark"/>
                    <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                    <Path x:Name="Arrow" Grid.Column="2" Data="M0,0 L4,4 L0,8" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                          HorizontalAlignment="Right" VerticalAlignment="Center" Visibility="Collapsed"
                          Stroke="{Binding Foreground, RelativeSource={RelativeSource TemplatedParent}}"/>
                  </Grid>
                </Border>
                <Popup x:Name="Sub" Placement="Right" HorizontalOffset="4" VerticalOffset="-7" AllowsTransparency="True" Focusable="False"
                       IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}" PopupAnimation="Fade">
                  <Border x:Name="SubCard" BorderThickness="1.5" CornerRadius="14" Padding="6" MinWidth="200">
                    <ItemsPresenter/>
                  </Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="Role" Value="SubmenuHeader"><Setter TargetName="Arrow" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(Xaml);
        mi.Template = template;
        mi.Loaded += (_, _) =>
        {
            if (template.FindName("Check", mi) is System.Windows.Shapes.Path check) check.Stroke = new SolidColorBrush(p.Mark);
            if (template.FindName("SubCard", mi) is Border sub)
            {
                // the submenu's items, in a card like the main menu's
                sub.Background = BackBrush(p);
                sub.BorderBrush = new SolidColorBrush(p.Border);
            }
        };
        var hover = new SolidColorBrush(p.Hover);
        void Light(bool on) { if (template.FindName("Row", mi) is Border row) row.Background = on ? hover : Brushes.Transparent; }
        mi.MouseEnter += (_, _) => Light(true);
        mi.MouseLeave += (_, _) => Light(mi.IsSubmenuOpen);
        mi.SubmenuClosed += (_, _) => Light(mi.IsMouseOver);
        mi.GotKeyboardFocus += (_, _) => Light(true);
        mi.LostKeyboardFocus += (_, _) => Light(mi.IsMouseOver || mi.IsSubmenuOpen);
    }
}
