using AmperDock.Core;

namespace AmperDock.Core.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"amperdock-test-{Guid.NewGuid()}.db");
    private SettingsStore NewStore() => new(_path);

    [Fact]
    public void Migrates_fresh_db_and_seeds_main_dock()
    {
        using var s = NewStore();
        Assert.Equal(1, s.UserVersion);
        Assert.Empty(s.GetItems(SettingsStore.MainDockId));
    }

    [Fact]
    public void Insert_move_remove_keep_order_and_survive_reopen()
    {
        using (var s = NewStore())
        {
            s.InsertItems(SettingsStore.MainDockId, 0, [
                new(ItemTypes.Program, "A", @"C:\a.exe"),
                new(ItemTypes.Program, "B", @"C:\b.exe"),
                new(ItemTypes.Program, "C", @"C:\c.exe")]);
            s.InsertItems(SettingsStore.MainDockId, 1, [new(ItemTypes.Folder, "D", @"C:\d")]);   // A D B C
            var items = s.GetItems(SettingsStore.MainDockId);
            Assert.Equal("ADBC", string.Concat(items.Select(i => i.Label)));

            s.MoveItem(items[0].Id, 3);                                                           // D B C A
            Assert.Equal("DBCA", Labels(s));
            s.MoveItem(items[0].Id, 0);                                                           // A D B C
            s.RemoveItem(items[1].Id);                                                            // A B C
            Assert.Equal("ABC", Labels(s));
        }
        using var reopened = NewStore();
        Assert.Equal("ABC", Labels(reopened));
    }

    [Fact]
    public void Many_inserts_in_the_same_gap_renumber_instead_of_colliding()
    {
        using var s = NewStore();
        s.InsertItems(SettingsStore.MainDockId, 0, [new(ItemTypes.Program, "first", "a"), new(ItemTypes.Program, "last", "z")]);
        for (var i = 0; i < 80; i++) s.InsertItems(SettingsStore.MainDockId, 1, [new(ItemTypes.Program, $"n{i}", "x")]);
        var items = s.GetItems(SettingsStore.MainDockId);
        Assert.Equal(82, items.Count);
        Assert.Equal("first", items[0].Label);
        Assert.Equal("n79", items[1].Label);
        Assert.Equal("last", items[^1].Label);
        Assert.Equal(items.Count, items.Select(i => i.SortOrder).Distinct().Count());
    }

    [Fact]
    public void Appearance_round_trips_and_clamps()
    {
        using var s = NewStore();
        Assert.Equal(DockAppearance.Default, s.GetAppearance(SettingsStore.MainDockId));   // '{}' in a fresh db
        s.SetAppearance(SettingsStore.MainDockId, new DockAppearance { Roundness = 0, Color = "#e86fa6", Opacity = 0 });
        var a = s.GetAppearance(SettingsStore.MainDockId);
        Assert.Equal((0f, "#E86FA6", 0), (a.Roundness, a.Color, a.Opacity));

        Assert.Equal(100, DockAppearance.FromJson("""{"opacity": 250}""").Opacity);
        Assert.Null(DockAppearance.FromJson("""{"color": "purple"}""").Color);
        Assert.Equal(DockAppearance.Default, DockAppearance.FromJson("not json"));
    }

    [Fact]
    public void Gradient_and_outline_settings_round_trip_and_clamp()
    {
        using var s = NewStore();
        s.SetAppearance(SettingsStore.MainDockId, new DockAppearance
        {
            Style = DockStyles.Gradient, Color = "#B497FF", Color2 = "#f4a6c8", GradientAngle = 450,
            OutlineColor = "#2a1f3d", OutlineWidth = 99,
        });
        var a = s.GetAppearance(SettingsStore.MainDockId);
        Assert.Equal((DockStyles.Gradient, "#F4A6C8", 90f, "#2A1F3D", 6f), (a.Style, a.Color2, a.GradientAngle, a.OutlineColor, a.OutlineWidth));
        Assert.Equal(DockStyles.Glass, DockAppearance.FromJson("""{"style": "sparkly"}""").Style);
    }

    private static string Labels(SettingsStore s) => string.Concat(s.GetItems(SettingsStore.MainDockId).Select(i => i.Label));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
    }
}

public class PlacementTests
{
    private static readonly RectF Work = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(960, 1030, DockEdge.Bottom)]
    [InlineData(960, 10, DockEdge.Top)]
    [InlineData(5, 500, DockEdge.Left)]
    [InlineData(1915, 500, DockEdge.Right)]
    [InlineData(960, 500, DockEdge.Float)]
    public void Snaps_to_the_nearest_edge_or_floats(int x, int y, DockEdge expected)
        => Assert.Equal(expected, DockPlacement.Snap(x, y, Work, 96, null).Edge);

    [Fact]
    public void Snap_alignment_follows_the_drop_point()
    {
        Assert.Equal(0.25f, DockPlacement.Snap(480, 1035, Work, 96, null).Alignment, 3);
        Assert.Equal(0.5f, DockPlacement.Snap(3, 520, Work, 96, null).Alignment, 3);
        var f = DockPlacement.Snap(700, 400, Work, 96, "\\\\.\\DISPLAY2");
        Assert.Equal((700, 400, "\\\\.\\DISPLAY2"), (f.FloatX, f.FloatY, f.MonitorKey));
    }

    [Theory]
    [InlineData(DockEdge.Bottom)]
    [InlineData(DockEdge.Top)]
    [InlineData(DockEdge.Left)]
    [InlineData(DockEdge.Right)]
    public void Frame_mapping_round_trips_and_keeps_the_edge_side(DockEdge edge)
    {
        var f = new DockFrame(edge, 1000, 150);
        var (wx, wy) = f.ToWindow(300, 140);
        Assert.Equal((300f, 140f), f.ToDock(wx, wy));
        // the band (near y = Thickness in dock space) must end up against the matching window edge
        var band = f.ToWindow(new RectF(100, 100, 50, 50));
        var (w, h) = f.WindowSize;
        switch (edge)
        {
            case DockEdge.Bottom: Assert.Equal(h, band.Bottom, 3); break;
            case DockEdge.Top: Assert.Equal(0, band.Y, 3); break;
            case DockEdge.Left: Assert.Equal(0, band.X, 3); break;
            case DockEdge.Right: Assert.Equal(w, band.Right, 3); break;
        }
    }

    [Fact]
    public void Placement_persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amperdock-test-{Guid.NewGuid()}.db");
        try
        {
            using (var s = new SettingsStore(path))
            {
                Assert.Equal(DockEdge.Bottom, s.GetPlacement(SettingsStore.MainDockId).Edge);
                s.SetPlacement(SettingsStore.MainDockId, new DockPlacement(DockEdge.Left, "\\\\.\\DISPLAY2", 0.3f));
            }
            using var again = new SettingsStore(path);
            var p = again.GetPlacement(SettingsStore.MainDockId);
            Assert.Equal((DockEdge.Left, "\\\\.\\DISPLAY2"), (p.Edge, p.MonitorKey));
            Assert.Equal(0.3f, p.Alignment, 3);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
        }
    }
}

public class LayoutTests
{
    private static readonly DockMetrics M = new();

    [Fact]
    public void No_pointer_means_rest_size()
    {
        var c = DockLayout.RestCenters(5, M, 500);
        Assert.All(DockLayout.TargetSizes(c, M, null), s => Assert.Equal(M.IconSize, s));
    }

    [Fact]
    public void Icon_under_pointer_is_fully_magnified_and_neighbours_fall_off()
    {
        var c = DockLayout.RestCenters(5, M, 500);
        var s = DockLayout.TargetSizes(c, M, c[2]);
        Assert.Equal(M.MagnifiedSize, s[2], 3);
        Assert.True(s[1] < s[2] && s[0] < s[1]);
        Assert.Equal(s[1], s[3], 3);
    }

    [Fact]
    public void Arrange_is_centred_and_bottom_aligned()
    {
        var sizes = new float[] { 48, 80, 48 };
        var (icons, bg) = DockLayout.Arrange(sizes, M, 500, 200);
        Assert.Equal(500, (icons[0].X + icons[^1].Right) / 2, 3);
        Assert.Equal(icons[0].Bottom, icons[1].Bottom, 3);
        Assert.Equal(200, bg.Bottom, 3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10000, 4)]
    public void Insert_index_at_the_ends(float x, int expected)
        => Assert.Equal(expected, DockLayout.InsertIndex(DockLayout.RestCenters(4, M, 500), x));

    [Fact]
    public void Sort_order_midpoints()
    {
        Assert.Equal(1, SortOrder.ForIndex([], 0));
        Assert.Equal(1.5, SortOrder.ForIndex([1, 2], 1));
        Assert.Equal(0, SortOrder.ForIndex([1, 2], 0));
        Assert.Equal(3, SortOrder.ForIndex([1, 2], 2));
    }
}

public sealed class ItemAndDockTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"amperdock-test-{Guid.NewGuid():N}.db");
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(_path); } catch (IOException) { } }

    [Fact]
    public void UpdateItem_saves_every_editable_field_and_keeps_order()
    {
        using var s = new SettingsStore(_path);
        s.InsertItems(SettingsStore.MainDockId, 0, [new NewItem(ItemTypes.Program, "A", @"C:\a.exe"), new NewItem(ItemTypes.Program, "B", @"C:\b.exe")]);
        var a = s.GetItems(SettingsStore.MainDockId)[0];
        s.UpdateItem(a with { Label = "Renamed", Type = ItemTypes.Folder, Target = @"C:\Users", Arguments = "  ", StartIn = null,
            IconPath = @"%SystemRoot%\System32\imageres.dll,3", RunAsAdmin = true, AllowMultiple = true });
        var items = s.GetItems(SettingsStore.MainDockId);
        Assert.Equal("Renamed", items[0].Label);
        Assert.Equal(ItemTypes.Folder, items[0].Type);
        Assert.Equal(@"C:\Users", items[0].Target);
        Assert.Null(items[0].Arguments);   // blank is stored as null
        Assert.Equal(@"%SystemRoot%\System32\imageres.dll,3", items[0].IconPath);
        Assert.True(items[0].RunAsAdmin && items[0].AllowMultiple);
        Assert.Equal("B", items[1].Label);
    }

    [Fact]
    public void Docks_can_be_created_renamed_and_deleted_with_their_items()
    {
        using var s = new SettingsStore(_path);
        Assert.Single(s.GetDocks());
        var id = s.CreateDock("Games", new DockPlacement(DockEdge.Left, null, 0.3f));
        s.InsertItems(id, 0, [new NewItem(ItemTypes.Program, "G", @"C:\g.exe")]);
        Assert.Equal(DockEdge.Left, s.GetPlacement(id).Edge);
        Assert.Equal(0.3f, s.GetPlacement(id).Alignment, 3);
        s.RenameDock(id, " Play ");
        Assert.Equal("Play", s.GetDocks().Single(d => d.Id == id).Name);
        Assert.Empty(s.GetItems(SettingsStore.MainDockId));
        s.DeleteDock(id);
        Assert.Single(s.GetDocks());
        Assert.Empty(s.GetItems(id));
        Assert.Throws<InvalidOperationException>(() => s.DeleteDock(SettingsStore.MainDockId));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\shell32.dll,4", @"C:\Windows\System32\shell32.dll", 4, true)]
    [InlineData(@"%SystemRoot%\System32\imageres.dll,-102", @"%SystemRoot%\System32\imageres.dll", -102, true)]
    [InlineData(@"""C:\Program Files\App\app.exe"",0", @"C:\Program Files\App\app.exe", 0, true)]
    [InlineData(@"C:\Pics\cat, the best.png", @"C:\Pics\cat, the best.png", 0, false)]
    [InlineData(@"C:\Pics\kitty.png", @"C:\Pics\kitty.png", 0, false)]
    [InlineData(@"C:\Icons\paw.ico", @"C:\Icons\paw.ico", 0, true)]
    public void IconRef_parses_windows_icon_locations(string value, string file, int index, bool resource)
    {
        var r = IconRef.Parse(value)!.Value;
        Assert.Equal(file, r.File);
        Assert.Equal(index, r.Index);
        Assert.Equal(resource, r.IsIconResource);
        Assert.Equal(r, IconRef.Parse(r.ToString()));   // round-trips through the database
    }

    [Fact]
    public void IconRef_blank_is_none() => Assert.Null(IconRef.Parse("  "));
}

public class TaskMatchTests
{
    private sealed record W(string Name, string? AppId, string? Exe);

    private static Dictionary<string, string[]> Run(TaskMatch.ItemKey[] items, W[] windows, out string[] left)
    {
        var (byItem, rest) = TaskMatch.Assign(items, windows, w => w.AppId, w => w.Exe);
        left = rest.Select(w => w.Name).ToArray();
        return byItem.ToDictionary(kv => kv.Key, kv => kv.Value.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void Store_app_matches_by_app_id()
    {
        var r = Run([new("calc", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null)],
            [new("calc window", "microsoft.windowscalculator_8wekyb3d8bbwe!app", @"C:\Program Files\WindowsApps\x\CalculatorApp.exe")], out var left);
        Assert.Equal(["calc window"], r["calc"]);
        Assert.Empty(left);
    }

    [Fact]
    public void Pinned_web_app_takes_its_windows_and_the_browser_keeps_the_rest()
    {
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var r = Run([new("chrome", null, chrome), new("music", "Chrome._crx_music", chrome)],
            [new("browser", "Chrome", chrome), new("music app", "Chrome._crx_music", chrome), new("mail app", "Chrome._crx_mail", chrome)], out _);
        Assert.Equal(["browser", "mail app"], r["chrome"]);   // an unpinned web app falls back to its browser
        Assert.Equal(["music app"], r["music"]);
    }

    [Fact]
    public void Inbox_notepad_matches_the_store_notepad_window_but_desktop_namesakes_do_not()
    {
        var r = Run([new("notepad", null, @"C:\Windows\System32\notepad.exe")],
            [new("store notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11\Notepad\Notepad.exe"),
             new("other notepad", null, @"D:\tools\notepad.exe")], out var left);
        Assert.Equal(["store notepad"], r["notepad"]);
        Assert.Equal(["other notepad"], left);
    }

    [Fact]
    public void Unknown_windows_are_leftovers()
    {
        var r = Run([new("notepad", null, @"C:\Windows\notepad.exe")],
            [new("n", null, @"c:\windows\NOTEPAD.EXE"), new("x", null, @"C:\x.exe"), new("elevated", null, null)], out var left);
        Assert.Equal(["n"], r["notepad"]);
        Assert.Equal(["x", "elevated"], left);
    }
}

public class ThemeFieldTests
{
    [Fact]
    public void Theme_fields_round_trip_and_bad_values_fall_back()
    {
        var a = new DockAppearance { Theme = "Autumn", Shape = DockShapes.Leaf, Indicator = "leaf", IndicatorColor = "#ff8800", Decorations = false };
        var back = DockAppearance.FromJson(a.ToJson());
        Assert.Equal("autumn", back.Theme);
        Assert.Equal(DockShapes.Leaf, back.Shape);
        Assert.Equal("leaf", back.Indicator);
        Assert.Equal("#FF8800", back.IndicatorColor);
        Assert.False(back.Decorations);

        var bad = DockAppearance.FromJson("""{"shape":"blob","indicator":"ufo","indicator_color":"red"}""");
        Assert.Equal(DockShapes.Rounded, bad.Shape);
        Assert.Equal(DockIndicators.Paw, bad.Indicator);
        Assert.Null(bad.IndicatorColor);
        Assert.True(bad.Decorations);   // older saved looks without the field keep decorations on
    }

    [Theory]
    [InlineData("""{"indicator_size":"large"}""", "large")]
    [InlineData("""{"indicator_size":"huge"}""", "medium")]
    [InlineData("""{}""", "medium")]
    public void Indicator_size_round_trips_and_falls_back(string json, string expected)
    {
        var a = DockAppearance.FromJson(json);
        Assert.Equal(expected, a.IndicatorSize);
        Assert.Equal(expected, DockAppearance.FromJson(a.ToJson()).IndicatorSize);
    }
}
