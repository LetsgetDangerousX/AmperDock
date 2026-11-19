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
