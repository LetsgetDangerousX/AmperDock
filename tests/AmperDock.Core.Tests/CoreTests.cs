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

    private static string Labels(SettingsStore s) => string.Concat(s.GetItems(SettingsStore.MainDockId).Select(i => i.Label));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
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
