using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AmperDock.Core;

/// <summary>
/// The only class that touches amperdock.db. Every multi-row change runs in one transaction.
/// Callers are the UI thread for now; it becomes a single background writer once writes get heavier.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const string MainDockId = "00000000-0000-4000-8000-000000000010";
    public const int SchemaVersion = 1;

    /// <summary>When the gap between two neighbours is smaller than this, the container is renumbered.</summary>
    private const double MinGap = 1e-6;

    private readonly SqliteConnection _db;

    public SettingsStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        _db.Open();
        Migrate();
    }

    /// <summary>Default database location: %APPDATA%\AmperDock\amperdock.db.</summary>
    public static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmperDock");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "amperdock.db");
    }

    public int UserVersion => Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);

    private void Migrate()
    {
        var version = UserVersion;
        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"amperdock.db is schema v{version}, newer than this build (v{SchemaVersion}). Update AmperDock.");
        if (version >= 1) return;

        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("0001_init.sql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = reader.ReadToEnd();   // the script sets PRAGMA user_version = 1 at the end
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- settings (key -> JSON value)

    public string? GetSetting(string key) => Scalar("SELECT value_json FROM settings WHERE key = $k", null, ("$k", key)) as string;

    public void SetSetting(string key, string valueJson) => Exec("""
        INSERT INTO settings (key, value_json) VALUES ($k, $v)
        ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now')
        """, null, ("$k", key), ("$v", valueJson));

    // ---------------------------------------------------------------- container appearance

    public DockAppearance GetAppearance(string containerId) =>
        DockAppearance.FromJson(Scalar("SELECT appearance_json FROM containers WHERE id = $id", null, ("$id", containerId)) as string);

    public void SetAppearance(string containerId, DockAppearance appearance) =>
        Exec("UPDATE containers SET appearance_json = $j WHERE id = $id", null,
            ("$j", appearance.Normalised().ToJson()), ("$id", containerId));

    // ---------------------------------------------------------------- placement

    public DockPlacement GetPlacement(string containerId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT edge, monitor_key, alignment, float_x, float_y FROM containers WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", containerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new DockPlacement();
        return new DockPlacement(
            DockPlacement.FromDb(r.IsDBNull(0) ? null : r.GetString(0)),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? 0.5f : (float)r.GetDouble(2),
            r.IsDBNull(3) ? 0 : r.GetInt32(3),
            r.IsDBNull(4) ? 0 : r.GetInt32(4));
    }

    public void SetPlacement(string containerId, DockPlacement p) => Exec("""
        UPDATE containers SET edge = $e, monitor_key = $m, alignment = $a, float_x = $fx, float_y = $fy WHERE id = $id
        """, null, ("$e", DockPlacement.ToDb(p.Edge)), ("$m", (object?)p.MonitorKey ?? DBNull.Value),
        ("$a", (double)Math.Clamp(p.Alignment, 0, 1)), ("$fx", p.FloatX), ("$fy", p.FloatY), ("$id", containerId));

    // ---------------------------------------------------------------- items

    public IReadOnlyList<DockItem> GetItems(string containerId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, container_id, sort_order, type, label, target, arguments, start_in, icon_path, run_as_admin, allow_multiple
            FROM items WHERE container_id = $c AND shelf_tab_id IS NULL
            ORDER BY sort_order, id
            """;
        cmd.Parameters.AddWithValue("$c", containerId);
        using var r = cmd.ExecuteReader();
        var list = new List<DockItem>();
        while (r.Read())
        {
            list.Add(new DockItem(
                r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt64(9) != 0, r.GetInt64(10) != 0));
        }
        return list;
    }

    /// <summary>Insert items at a position (0 = first). Returns the new items in order.</summary>
    public IReadOnlyList<DockItem> InsertItems(string containerId, int index, IEnumerable<NewItem> newItems)
    {
        var created = new List<string>();
        using (var tx = _db.BeginTransaction())
        {
            foreach (var n in newItems)
            {
                var order = OrderForIndex(containerId, index, tx);
                var id = Guid.NewGuid().ToString();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO items (id, container_id, sort_order, type, label, target, arguments, start_in)
                    VALUES ($id, $c, $o, $t, $l, $tg, $a, $s)
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$c", containerId);
                cmd.Parameters.AddWithValue("$o", order);
                cmd.Parameters.AddWithValue("$t", n.Type);
                cmd.Parameters.AddWithValue("$l", n.Label);
                cmd.Parameters.AddWithValue("$tg", (object?)n.Target ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$a", (object?)n.Arguments ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", (object?)n.StartIn ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                created.Add(id);
                index++;
            }
            tx.Commit();
        }
        var all = GetItems(containerId);
        return created.Select(id => all.First(i => i.Id == id)).ToList();
    }

    /// <summary>Move an item to a new index in the same container (index counted without the item itself).</summary>
    public void MoveItem(string itemId, int newIndex)
    {
        using var tx = _db.BeginTransaction();
        var containerId = (string)Scalar("SELECT container_id FROM items WHERE id = $id", tx, ("$id", itemId))!;
        var order = OrderForIndex(containerId, newIndex, tx, excludeId: itemId);
        Exec("UPDATE items SET sort_order = $o WHERE id = $id", tx, ("$o", order), ("$id", itemId));
        tx.Commit();
    }

    /// <summary>Remove an item. Nested containers opened from it cascade away with it.</summary>
    public void RemoveItem(string itemId) => Exec("DELETE FROM items WHERE id = $id", null, ("$id", itemId));

    /// <summary>
    /// The sort_order that puts a row at <paramref name="index"/>: the midpoint of its neighbours,
    /// or neighbour +/- 1 at the ends. Renumbers the container first if the gap is too small.
    /// </summary>
    private double OrderForIndex(string containerId, int index, SqliteTransaction tx, string? excludeId = null)
    {
        var orders = Orders(containerId, tx, excludeId);
        if (!SortOrder.HasRoom(orders, index, MinGap))
        {
            Renumber(containerId, tx);
            orders = Orders(containerId, tx, excludeId);
        }
        return SortOrder.ForIndex(orders, index);
    }

    private List<double> Orders(string containerId, SqliteTransaction tx, string? excludeId)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sort_order FROM items WHERE container_id = $c AND shelf_tab_id IS NULL AND id IS NOT $x ORDER BY sort_order, id";
        cmd.Parameters.AddWithValue("$c", containerId);
        cmd.Parameters.AddWithValue("$x", (object?)excludeId ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<double>();
        while (r.Read()) list.Add(r.GetDouble(0));
        return list;
    }

    private void Renumber(string containerId, SqliteTransaction tx)
    {
        Exec("""
            UPDATE items SET sort_order = (
              SELECT rn FROM (SELECT id AS rid, ROW_NUMBER() OVER (ORDER BY sort_order, id) AS rn
                              FROM items WHERE container_id = $c AND shelf_tab_id IS NULL) WHERE rid = items.id)
            WHERE container_id = $c AND shelf_tab_id IS NULL
            """, tx, ("$c", containerId));
    }

    // ---------------------------------------------------------------- helpers

    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string, object)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        return cmd.ExecuteScalar();
    }

    private void Exec(string sql, SqliteTransaction? tx, params (string, object)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>Pure helpers for REAL sort_order values, kept separate so they can be unit-tested.</summary>
public static class SortOrder
{
    public static double ForIndex(IReadOnlyList<double> orders, int index)
    {
        index = Math.Clamp(index, 0, orders.Count);
        if (orders.Count == 0) return 1;
        if (index == 0) return orders[0] - 1;
        if (index == orders.Count) return orders[^1] + 1;
        return (orders[index - 1] + orders[index]) / 2;
    }

    public static bool HasRoom(IReadOnlyList<double> orders, int index, double minGap)
    {
        index = Math.Clamp(index, 0, orders.Count);
        if (index == 0 || index == orders.Count) return true;
        return orders[index] - orders[index - 1] > minGap;
    }
}
