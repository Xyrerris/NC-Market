using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NCMarket.Core.Models;

namespace NCMarket.Core;

public sealed record SnapshotInfo(
    long Id, string Planet, DateTime TakenAtUtc, string ItemSubTypes, int ProductCount);

public sealed record ItemHistoryRow(
    long SnapshotId, DateTime TakenAtUtc, int Listings,
    double MinPrice, double AvgPrice, double MaxPrice);

public sealed record ItemStatsRow(
    int ItemId, int ItemSubType, int Grade, int Listings,
    double MinPrice, double AvgPrice, double MaxPrice, int MaxCombatPoint, int MaxLevel);

/// <summary>
/// Historical price baseline for a bucket of comparable listings (same item_id and
/// level): median price and median price-per-CP over the distinct listings observed
/// across snapshots. <see cref="MedianPricePerCp"/> is null when no sample in the
/// bucket has a positive combat point (<see cref="CpSamples"/> is 0).
/// </summary>
public sealed record PriceBaseline(
    int ItemId, int Level, int Samples, double MedianPrice,
    int CpSamples, double? MedianPricePerCp);

/// <summary>
/// SQLite storage for market snapshots. Each snapshot is an immutable copy of the
/// listings observed at a given time; analyses compare snapshots over time.
/// </summary>
public sealed class MarketDb : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _conn;

    public string DbPath { get; }

    public MarketDb(string? dbPath = null)
    {
        DbPath = dbPath ?? AppPaths.DefaultDbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(DbPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();
        Execute("PRAGMA foreign_keys = ON;");
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS snapshots(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                planet TEXT NOT NULL,
                taken_at_utc TEXT NOT NULL,
                item_sub_types TEXT NOT NULL,
                product_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS products(
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                product_id TEXT NOT NULL,
                item_sub_type INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                icon_id INTEGER NOT NULL,
                grade INTEGER NOT NULL,
                level INTEGER NOT NULL,
                combat_point INTEGER NOT NULL,
                elemental_type INTEGER NOT NULL,
                price REAL NOT NULL,
                quantity REAL NOT NULL,
                unit_price REAL NOT NULL,
                crystal INTEGER NOT NULL,
                crystal_per_price INTEGER NOT NULL,
                option_count INTEGER NOT NULL,
                by_custom_craft INTEGER NOT NULL,
                seller_agent TEXT,
                seller_avatar TEXT,
                registered_block_index INTEGER NOT NULL,
                legacy INTEGER NOT NULL,
                stats_json TEXT,
                skills_json TEXT,
                PRIMARY KEY(snapshot_id, product_id)
            );

            CREATE INDEX IF NOT EXISTS ix_products_item ON products(item_id, snapshot_id);
            CREATE INDEX IF NOT EXISTS ix_products_subtype ON products(item_sub_type, snapshot_id);
            """);
    }

    /// <summary>Creates a new snapshot row and returns its id.</summary>
    public long CreateSnapshot(string planet, IEnumerable<EquipmentType> types, DateTime takenAtUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots(planet, taken_at_utc, item_sub_types, product_count)
            VALUES ($planet, $takenAt, $types, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$planet", planet);
        cmd.Parameters.AddWithValue("$takenAt", takenAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$types", string.Join(",", types.Select(t => (int)t)));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Bulk-inserts listings into a snapshot inside a single transaction.</summary>
    public int AddProducts(long snapshotId, IEnumerable<ItemProduct> products)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO products(
                snapshot_id, product_id, item_sub_type, item_id, icon_id, grade, level,
                combat_point, elemental_type, price, quantity, unit_price, crystal,
                crystal_per_price, option_count, by_custom_craft, seller_agent,
                seller_avatar, registered_block_index, legacy, stats_json, skills_json)
            VALUES(
                $snapshotId, $productId, $itemSubType, $itemId, $iconId, $grade, $level,
                $cp, $elemental, $price, $quantity, $unitPrice, $crystal,
                $crystalPerPrice, $optionCount, $byCustomCraft, $sellerAgent,
                $sellerAvatar, $blockIndex, $legacy, $statsJson, $skillsJson);
            """;

        var p = cmd.Parameters;
        p.Add("$snapshotId", SqliteType.Integer);
        p.Add("$productId", SqliteType.Text);
        p.Add("$itemSubType", SqliteType.Integer);
        p.Add("$itemId", SqliteType.Integer);
        p.Add("$iconId", SqliteType.Integer);
        p.Add("$grade", SqliteType.Integer);
        p.Add("$level", SqliteType.Integer);
        p.Add("$cp", SqliteType.Integer);
        p.Add("$elemental", SqliteType.Integer);
        p.Add("$price", SqliteType.Real);
        p.Add("$quantity", SqliteType.Real);
        p.Add("$unitPrice", SqliteType.Real);
        p.Add("$crystal", SqliteType.Integer);
        p.Add("$crystalPerPrice", SqliteType.Integer);
        p.Add("$optionCount", SqliteType.Integer);
        p.Add("$byCustomCraft", SqliteType.Integer);
        p.Add("$sellerAgent", SqliteType.Text);
        p.Add("$sellerAvatar", SqliteType.Text);
        p.Add("$blockIndex", SqliteType.Integer);
        p.Add("$legacy", SqliteType.Integer);
        p.Add("$statsJson", SqliteType.Text);
        p.Add("$skillsJson", SqliteType.Text);

        var count = 0;
        foreach (var product in products)
        {
            p["$snapshotId"].Value = snapshotId;
            p["$productId"].Value = product.ProductId.ToString("D");
            p["$itemSubType"].Value = product.ItemSubType;
            p["$itemId"].Value = product.ItemId;
            p["$iconId"].Value = product.IconId;
            p["$grade"].Value = product.Grade;
            p["$level"].Value = product.Level;
            p["$cp"].Value = product.CombatPoint;
            p["$elemental"].Value = product.ElementalType;
            p["$price"].Value = (double)product.Price;
            p["$quantity"].Value = (double)product.Quantity;
            p["$unitPrice"].Value = (double)product.UnitPrice;
            p["$crystal"].Value = product.Crystal;
            p["$crystalPerPrice"].Value = product.CrystalPerPrice;
            p["$optionCount"].Value = product.OptionCountFromCombination;
            p["$byCustomCraft"].Value = product.ByCustomCraft ? 1 : 0;
            p["$sellerAgent"].Value = product.SellerAgentAddress;
            p["$sellerAvatar"].Value = product.SellerAvatarAddress;
            p["$blockIndex"].Value = product.RegisteredBlockIndex;
            p["$legacy"].Value = product.Legacy ? 1 : 0;
            p["$statsJson"].Value = JsonSerializer.Serialize(product.StatModels, JsonOptions);
            p["$skillsJson"].Value = JsonSerializer.Serialize(product.SkillModels, JsonOptions);
            count += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return count;
    }

    /// <summary>Updates the cached product count of a snapshot.</summary>
    public void FinalizeSnapshot(long snapshotId)
    {
        Execute($"""
            UPDATE snapshots
            SET product_count = (SELECT COUNT(*) FROM products WHERE snapshot_id = {snapshotId})
            WHERE id = {snapshotId};
            """);
    }

    public IReadOnlyList<SnapshotInfo> GetSnapshots(string? planet = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, planet, taken_at_utc, item_sub_types, product_count
            FROM snapshots
            WHERE ($planet IS NULL OR planet = $planet)
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$planet", (object?)planet ?? DBNull.Value);

        var result = new List<SnapshotInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SnapshotInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseUtc(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4)));
        }

        return result;
    }

    public SnapshotInfo? GetSnapshot(long id) => GetSnapshots().FirstOrDefault(s => s.Id == id);

    public long? GetLatestSnapshotId(string planet)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(id) FROM snapshots WHERE planet = $planet;";
        cmd.Parameters.AddWithValue("$planet", planet);
        var value = cmd.ExecuteScalar();
        return value is long id ? id : null;
    }

    /// <summary>Min/avg/max price of an item across snapshots (its market price history).</summary>
    public IReadOnlyList<ItemHistoryRow> GetItemHistory(int itemId, string? planet = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.taken_at_utc, COUNT(*) AS listings,
                   MIN(p.price), AVG(p.price), MAX(p.price)
            FROM products p
            JOIN snapshots s ON s.id = p.snapshot_id
            WHERE p.item_id = $itemId
              AND ($planet IS NULL OR s.planet = $planet)
            GROUP BY s.id
            ORDER BY s.id;
            """;
        cmd.Parameters.AddWithValue("$itemId", itemId);
        cmd.Parameters.AddWithValue("$planet", (object?)planet ?? DBNull.Value);

        var result = new List<ItemHistoryRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ItemHistoryRow(
                reader.GetInt64(0),
                ParseUtc(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }

        return result;
    }

    /// <summary>Per-item aggregates over one snapshot (defaults intended: the latest).</summary>
    public IReadOnlyList<ItemStatsRow> GetSnapshotStats(long snapshotId, EquipmentType? type = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT item_id, item_sub_type, MAX(grade), COUNT(*) AS listings,
                   MIN(price), AVG(price), MAX(price), MAX(combat_point), MAX(level)
            FROM products
            WHERE snapshot_id = $snapshotId
              AND ($subType IS NULL OR item_sub_type = $subType)
            GROUP BY item_id
            ORDER BY listings DESC, item_id;
            """;
        cmd.Parameters.AddWithValue("$snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("$subType", type is null ? DBNull.Value : (int)type.Value);

        var result = new List<ItemStatsRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ItemStatsRow(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return result;
    }

    /// <summary>
    /// Reads back the full listings of a snapshot (stats and skills included),
    /// optionally filtered by equipment type.
    /// </summary>
    public IReadOnlyList<ItemProduct> GetSnapshotProducts(long snapshotId, EquipmentType? type = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT product_id, item_sub_type, item_id, icon_id, grade, level, combat_point,
                   elemental_type, price, quantity, unit_price, crystal, crystal_per_price,
                   option_count, by_custom_craft, seller_agent, seller_avatar,
                   registered_block_index, legacy, stats_json, skills_json
            FROM products
            WHERE snapshot_id = $snapshotId
              AND ($subType IS NULL OR item_sub_type = $subType)
            ORDER BY item_sub_type, item_id, unit_price;
            """;
        cmd.Parameters.AddWithValue("$snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("$subType", type is null ? DBNull.Value : (int)type.Value);

        var result = new List<ItemProduct>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ItemProduct
            {
                ProductId = Guid.Parse(reader.GetString(0)),
                ItemSubType = reader.GetInt32(1),
                ItemId = reader.GetInt32(2),
                IconId = reader.GetInt32(3),
                Grade = reader.GetInt32(4),
                Level = reader.GetInt32(5),
                CombatPoint = reader.GetInt32(6),
                ElementalType = reader.GetInt32(7),
                Price = (decimal)reader.GetDouble(8),
                Quantity = (decimal)reader.GetDouble(9),
                UnitPrice = (decimal)reader.GetDouble(10),
                Crystal = reader.GetInt64(11),
                CrystalPerPrice = reader.GetInt64(12),
                OptionCountFromCombination = reader.GetInt32(13),
                ByCustomCraft = reader.GetInt32(14) != 0,
                SellerAgentAddress = reader.IsDBNull(15) ? "" : reader.GetString(15),
                SellerAvatarAddress = reader.IsDBNull(16) ? "" : reader.GetString(16),
                RegisteredBlockIndex = reader.GetInt64(17),
                Legacy = reader.GetInt32(18) != 0,
                StatModels = DeserializeOrEmpty<StatModel>(reader, 19),
                SkillModels = DeserializeOrEmpty<SkillModel>(reader, 20),
            });
        }

        return result;
    }

    /// <summary>
    /// Per-(item_id, level) historical price baselines over the snapshots of a planet.
    /// Each listing (product_id) is counted once even when it persists across several
    /// snapshots, so stale listings do not dominate the medians. Rows with a
    /// non-positive combat point contribute to the price median only. Listings from
    /// the latest snapshot are part of the baseline as well; with a reasonable
    /// minimum sample size their effect on the median is negligible.
    /// </summary>
    public IReadOnlyDictionary<(int ItemId, int Level), PriceBaseline> GetPriceBaselines(
        string planet, EquipmentType? type = null, DateTime? sinceUtc = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT p.product_id, p.item_id, p.level, p.price, p.combat_point
            FROM products p
            JOIN snapshots s ON s.id = p.snapshot_id
            WHERE s.planet = $planet
              AND ($subType IS NULL OR p.item_sub_type = $subType)
              AND ($since IS NULL OR s.taken_at_utc >= $since);
            """;
        cmd.Parameters.AddWithValue("$planet", planet);
        cmd.Parameters.AddWithValue("$subType", type is null ? DBNull.Value : (int)type.Value);
        cmd.Parameters.AddWithValue(
            "$since",
            sinceUtc is null
                ? DBNull.Value
                : sinceUtc.Value.ToString("O", CultureInfo.InvariantCulture));

        var buckets = new Dictionary<(int ItemId, int Level), (List<double> Prices, List<double> PricesPerCp)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetInt32(1), reader.GetInt32(2));
            var price = reader.GetDouble(3);
            var combatPoint = reader.GetInt32(4);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = (new List<double>(), new List<double>());
                buckets[key] = bucket;
            }

            bucket.Prices.Add(price);
            if (combatPoint > 0)
            {
                bucket.PricesPerCp.Add(price / combatPoint);
            }
        }

        var result = new Dictionary<(int ItemId, int Level), PriceBaseline>(buckets.Count);
        foreach (var ((itemId, level), (prices, pricesPerCp)) in buckets)
        {
            result[(itemId, level)] = new PriceBaseline(
                itemId,
                level,
                prices.Count,
                Median(prices),
                pricesPerCp.Count,
                pricesPerCp.Count > 0 ? Median(pricesPerCp) : null);
        }

        return result;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
    }

    private static List<T> DeserializeOrEmpty<T>(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return new List<T>();
        }

        return JsonSerializer.Deserialize<List<T>>(reader.GetString(ordinal), JsonOptions)
               ?? new List<T>();
    }

    private static DateTime ParseUtc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
