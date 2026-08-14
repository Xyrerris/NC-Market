using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// Completeness of a snapshot. A snapshot is <see cref="Partial"/> from the moment it is
/// created until <see cref="MarketDb.FinalizeSnapshot"/> marks it complete, so a capture
/// interrupted halfway (a network failure on one of the equipment types) stays
/// recognisable instead of silently passing for a full listing.
/// </summary>
public static class SnapshotStatus
{
    public const string Partial = "partial";
    public const string Complete = "complete";
}

public sealed record SnapshotInfo(
    long Id, string Planet, DateTime TakenAtUtc, string ItemSubTypes, int ProductCount,
    string Status)
{
    public bool IsComplete => Status == SnapshotStatus.Complete;
}

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
/// Outcome of a retention pass: how many rows were (or, on a dry run, would be)
/// removed and the database file size before/after the pass. On a dry run
/// <see cref="BytesAfter"/> equals <see cref="BytesBefore"/>.
/// </summary>
public sealed record PruneResult(
    int ListingsRemoved, int SightingsRemoved, int SnapshotsRemoved,
    long BytesBefore, long BytesAfter);

/// <summary>
/// SQLite storage for market snapshots. Snapshots are logically immutable copies of
/// the listings observed at a given time, but they are stored deduplicated (schema v2):
/// each distinct listing (product_id, whose attributes never change on the market
/// service — a price change creates a new product) is written once in
/// <c>listings</c>, and per-snapshot membership is recorded in the two-integer
/// <c>sightings</c> table. Version 1 databases, which stored a full copy of every
/// listing per snapshot, are migrated automatically on open (a .v1.bak backup of the
/// original file is left next to it, see <see cref="MigrationBackupPath"/>); version 2
/// databases gain the <c>snapshots.status</c> column in place.
/// </summary>
public sealed class MarketDb : IDisposable
{
    private const long SchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _conn;

    public string DbPath { get; }

    /// <summary>Path of the pre-migration backup, when opening this database migrated it.</summary>
    public string? MigrationBackupPath { get; private set; }

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
        // The snapshot job and ad-hoc queries can overlap on the server.
        Execute("PRAGMA busy_timeout = 5000;");
        EnsureSchema();
        Execute("PRAGMA journal_mode = WAL;");
    }

    private void EnsureSchema()
    {
        var version = (long)ExecuteScalar("PRAGMA user_version;")!;
        if (version == 0 && TableExists("products"))
        {
            MigrateV1ToV2();
            version = 2;
        }

        CreateSchema();

        // A database that already carried a version predates the status column: it lives
        // in the snapshots table, which no migration recreates, so it is added in place.
        if (version is > 0 and < 3)
        {
            MigrateV2ToV3();
        }

        if (version < SchemaVersion)
        {
            Execute($"PRAGMA user_version = {SchemaVersion};");
        }
    }

    private void CreateSchema()
    {
        Execute($"""
            CREATE TABLE IF NOT EXISTS snapshots(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                planet TEXT NOT NULL,
                taken_at_utc TEXT NOT NULL,
                item_sub_types TEXT NOT NULL,
                product_count INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT '{SnapshotStatus.Partial}'
            );

            CREATE TABLE IF NOT EXISTS listings(
                id INTEGER PRIMARY KEY,
                product_id TEXT NOT NULL UNIQUE,
                planet TEXT NOT NULL,
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
                -- Plain integers, not FKs: prune may drop old snapshot rows while the
                -- listing survives.
                first_seen_snapshot_id INTEGER NOT NULL,
                last_seen_snapshot_id INTEGER NOT NULL,
                last_seen_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sightings(
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                listing_id INTEGER NOT NULL REFERENCES listings(id) ON DELETE CASCADE,
                PRIMARY KEY(snapshot_id, listing_id)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_listings_planet_subtype ON listings(planet, item_sub_type);
            CREATE INDEX IF NOT EXISTS ix_listings_item ON listings(item_id);
            CREATE INDEX IF NOT EXISTS ix_sightings_listing ON sightings(listing_id);
            -- Filter column of both Prune and the --days window of GetPriceBaselines.
            CREATE INDEX IF NOT EXISTS ix_listings_last_seen ON listings(last_seen_at_utc);
            """);
    }

    /// <summary>
    /// Adds the completeness marker to a v2 database. Snapshots are considered complete
    /// when they carry a product count, because <see cref="FinalizeSnapshot"/> was the
    /// only writer of that column: a zero count is exactly the interrupted capture the
    /// column exists to flag.
    /// </summary>
    private void MigrateV2ToV3()
    {
        Execute($"""
            ALTER TABLE snapshots
                ADD COLUMN status TEXT NOT NULL DEFAULT '{SnapshotStatus.Partial}';

            UPDATE snapshots
            SET status = '{SnapshotStatus.Complete}'
            WHERE product_count > 0;
            """);
    }

    /// <summary>
    /// One-time in-place migration from schema v1 (a full copy of every listing per
    /// snapshot) to v2. Attribute columns are immutable per product_id, so any row of
    /// the group can provide them; first/last seen come from the snapshot id range.
    /// Indexes are left to <see cref="CreateSchema"/>, which runs right after.
    /// </summary>
    private void MigrateV1ToV2()
    {
        // The copy is the only safety net of a destructive migration, and a v1 database
        // left in WAL mode keeps its most recent writes outside the .db file: fold them
        // in before taking the backup.
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");

        var backupPath = DbPath + ".v1.bak";
        File.Copy(DbPath, backupPath, overwrite: true);

        Execute("BEGIN IMMEDIATE;");
        try
        {
            Execute("""
                CREATE TABLE listings(
                    id INTEGER PRIMARY KEY,
                    product_id TEXT NOT NULL UNIQUE,
                    planet TEXT NOT NULL,
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
                    first_seen_snapshot_id INTEGER NOT NULL,
                    last_seen_snapshot_id INTEGER NOT NULL,
                    last_seen_at_utc TEXT NOT NULL
                );

                CREATE TABLE sightings(
                    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                    listing_id INTEGER NOT NULL REFERENCES listings(id) ON DELETE CASCADE,
                    PRIMARY KEY(snapshot_id, listing_id)
                ) WITHOUT ROWID;

                INSERT INTO listings(
                    product_id, planet, item_sub_type, item_id, icon_id, grade, level,
                    combat_point, elemental_type, price, quantity, unit_price, crystal,
                    crystal_per_price, option_count, by_custom_craft, seller_agent,
                    seller_avatar, registered_block_index, legacy, stats_json, skills_json,
                    first_seen_snapshot_id, last_seen_snapshot_id, last_seen_at_utc)
                SELECT
                    p.product_id, s.planet, p.item_sub_type, p.item_id, p.icon_id, p.grade,
                    p.level, p.combat_point, p.elemental_type, p.price, p.quantity,
                    p.unit_price, p.crystal, p.crystal_per_price, p.option_count,
                    p.by_custom_craft, p.seller_agent, p.seller_avatar,
                    p.registered_block_index, p.legacy, p.stats_json, p.skills_json,
                    MIN(p.snapshot_id), MAX(p.snapshot_id), ''
                FROM products p
                JOIN snapshots s ON s.id = p.snapshot_id
                GROUP BY p.product_id;

                UPDATE listings SET last_seen_at_utc =
                    (SELECT taken_at_utc FROM snapshots
                     WHERE snapshots.id = listings.last_seen_snapshot_id);

                INSERT INTO sightings(snapshot_id, listing_id)
                SELECT p.snapshot_id, l.id
                FROM products p
                JOIN listings l ON l.product_id = p.product_id;

                DROP TABLE products;
                """);
            Execute("PRAGMA user_version = 2;");
            Execute("COMMIT;");
        }
        catch
        {
            Execute("ROLLBACK;");
            throw;
        }

        Execute("VACUUM;");
        MigrationBackupPath = backupPath;
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

    /// <summary>
    /// Records the listings observed by a snapshot inside a single transaction.
    /// Listings already known from previous snapshots only get their last-seen marker
    /// refreshed plus a two-integer sighting row; new listings are stored in full.
    /// Returns the number of distinct listings recorded for the snapshot.
    /// </summary>
    public int AddProducts(long snapshotId, IEnumerable<ItemProduct> products)
    {
        var (planet, takenAtUtc) = GetSnapshotKey(snapshotId);

        using var tx = _conn.BeginTransaction();

        using var touch = _conn.CreateCommand();
        touch.Transaction = tx;
        touch.CommandText = """
            UPDATE listings
            SET last_seen_snapshot_id = $snapshotId, last_seen_at_utc = $takenAt
            WHERE product_id = $productId
            RETURNING id;
            """;
        touch.Parameters.AddWithValue("$snapshotId", snapshotId);
        touch.Parameters.AddWithValue("$takenAt", takenAtUtc);
        var touchId = touch.Parameters.Add("$productId", SqliteType.Text);

        using var insert = _conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO listings(
                product_id, planet, item_sub_type, item_id, icon_id, grade, level,
                combat_point, elemental_type, price, quantity, unit_price, crystal,
                crystal_per_price, option_count, by_custom_craft, seller_agent,
                seller_avatar, registered_block_index, legacy, stats_json, skills_json,
                first_seen_snapshot_id, last_seen_snapshot_id, last_seen_at_utc)
            VALUES(
                $productId, $planet, $itemSubType, $itemId, $iconId, $grade, $level,
                $cp, $elemental, $price, $quantity, $unitPrice, $crystal,
                $crystalPerPrice, $optionCount, $byCustomCraft, $sellerAgent,
                $sellerAvatar, $blockIndex, $legacy, $statsJson, $skillsJson,
                $snapshotId, $snapshotId, $takenAt)
            RETURNING id;
            """;

        var p = insert.Parameters;
        p.AddWithValue("$snapshotId", snapshotId);
        p.AddWithValue("$takenAt", takenAtUtc);
        p.AddWithValue("$planet", planet);
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

        using var sight = _conn.CreateCommand();
        sight.Transaction = tx;
        sight.CommandText = """
            INSERT OR IGNORE INTO sightings(snapshot_id, listing_id)
            VALUES($snapshotId, $listingId);
            """;
        sight.Parameters.AddWithValue("$snapshotId", snapshotId);
        var sightId = sight.Parameters.Add("$listingId", SqliteType.Integer);

        var count = 0;
        foreach (var product in products)
        {
            var productId = product.ProductId.ToString("D");
            touchId.Value = productId;
            var listingId = touch.ExecuteScalar();
            if (listingId is null)
            {
                p["$productId"].Value = productId;
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
                listingId = insert.ExecuteScalar()!;
            }

            sightId.Value = listingId;
            count += sight.ExecuteNonQuery();
        }

        tx.Commit();
        return count;
    }

    /// <summary>
    /// Caches the product count of a snapshot and marks it complete: until this runs the
    /// snapshot stays <see cref="SnapshotStatus.Partial"/> and is ignored by
    /// <see cref="GetLatestSnapshotId"/>.
    /// </summary>
    public void FinalizeSnapshot(long snapshotId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE snapshots
            SET product_count = (SELECT COUNT(*) FROM sightings WHERE snapshot_id = $id),
                status = '{SnapshotStatus.Complete}'
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", snapshotId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SnapshotInfo> GetSnapshots(string? planet = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, planet, taken_at_utc, item_sub_types, product_count, status
            FROM snapshots
            WHERE ($planet IS NULL OR planet = $planet)
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$planet", (object?)planet ?? DBNull.Value);
        return ReadSnapshots(cmd);
    }

    public SnapshotInfo? GetSnapshot(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, planet, taken_at_utc, item_sub_types, product_count, status
            FROM snapshots
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadSnapshots(cmd).FirstOrDefault();
    }

    /// <summary>
    /// Id of the most recent <em>complete</em> snapshot of a planet. Interrupted captures
    /// are skipped on purpose: they are missing entire equipment types, and reporting on
    /// them would silently understate the market.
    /// </summary>
    public long? GetLatestSnapshotId(string planet)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT MAX(id) FROM snapshots
            WHERE planet = $planet AND status = '{SnapshotStatus.Complete}';
            """;
        cmd.Parameters.AddWithValue("$planet", planet);
        var value = cmd.ExecuteScalar();
        return value is long id ? id : null;
    }

    private static List<SnapshotInfo> ReadSnapshots(SqliteCommand cmd)
    {
        var result = new List<SnapshotInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SnapshotInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseUtc(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5)));
        }

        return result;
    }

    /// <summary>Min/avg/max price of an item across snapshots (its market price history).</summary>
    public IReadOnlyList<ItemHistoryRow> GetItemHistory(int itemId, string? planet = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.taken_at_utc, COUNT(*) AS listings,
                   MIN(l.price), AVG(l.price), MAX(l.price)
            FROM sightings g
            JOIN snapshots s ON s.id = g.snapshot_id
            JOIN listings l ON l.id = g.listing_id
            WHERE l.item_id = $itemId
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
            SELECT l.item_id, l.item_sub_type, MAX(l.grade), COUNT(*) AS listings,
                   MIN(l.price), AVG(l.price), MAX(l.price), MAX(l.combat_point), MAX(l.level)
            FROM sightings g
            JOIN listings l ON l.id = g.listing_id
            WHERE g.snapshot_id = $snapshotId
              AND ($subType IS NULL OR l.item_sub_type = $subType)
            GROUP BY l.item_id
            ORDER BY listings DESC, l.item_id;
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
            SELECT l.product_id, l.item_sub_type, l.item_id, l.icon_id, l.grade, l.level,
                   l.combat_point, l.elemental_type, l.price, l.quantity, l.unit_price,
                   l.crystal, l.crystal_per_price, l.option_count, l.by_custom_craft,
                   l.seller_agent, l.seller_avatar, l.registered_block_index, l.legacy,
                   l.stats_json, l.skills_json
            FROM sightings g
            JOIN listings l ON l.id = g.listing_id
            WHERE g.snapshot_id = $snapshotId
              AND ($subType IS NULL OR l.item_sub_type = $subType)
            ORDER BY l.item_sub_type, l.item_id, l.unit_price;
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
    /// Per-(item_id, level) historical price baselines over the listings of a planet.
    /// Listings are stored deduplicated, so each one contributes a single sample no
    /// matter how many snapshots observed it and stale listings do not dominate the
    /// medians. The <paramref name="sinceUtc"/> window keeps a listing when it was
    /// still on sale within the window (its last sighting is inside it). Rows with a
    /// non-positive combat point contribute to the price median only. Listings from
    /// the latest snapshot are part of the baseline as well; with a reasonable
    /// minimum sample size their effect on the median is negligible.
    /// </summary>
    public IReadOnlyDictionary<(int ItemId, int Level), PriceBaseline> GetPriceBaselines(
        string planet, EquipmentType? type = null, DateTime? sinceUtc = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT item_id, level, price, combat_point
            FROM listings
            WHERE planet = $planet
              AND ($subType IS NULL OR item_sub_type = $subType)
              AND ($since IS NULL OR last_seen_at_utc >= $since);
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
            var key = (reader.GetInt32(0), reader.GetInt32(1));
            var price = reader.GetDouble(2);
            var combatPoint = reader.GetInt32(3);
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

    /// <summary>
    /// Retention pass: removes listings whose last sighting is older than
    /// <paramref name="cutoffUtc"/> (their sightings cascade away) and snapshots taken
    /// before the cutoff that are left with no sightings, then compacts the file with
    /// VACUUM. With <paramref name="dryRun"/> nothing is modified and the result
    /// reports what would be removed. The product_count of surviving snapshots is not
    /// rewritten: it documents the size of the listing at capture time.
    /// </summary>
    public PruneResult Prune(DateTime cutoffUtc, bool dryRun = false)
    {
        var cutoff = cutoffUtc.ToString("O", CultureInfo.InvariantCulture);

        // Fold the WAL into the main file so before/after sizes are comparable.
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        var bytesBefore = new FileInfo(DbPath).Length;

        var sightings = ScalarCount("""
            SELECT COUNT(*)
            FROM sightings g
            JOIN listings l ON l.id = g.listing_id
            WHERE l.last_seen_at_utc < $cutoff;
            """, cutoff);
        var listings = ScalarCount(
            "SELECT COUNT(*) FROM listings WHERE last_seen_at_utc < $cutoff;", cutoff);
        var snapshots = ScalarCount("""
            SELECT COUNT(*)
            FROM snapshots s
            WHERE s.taken_at_utc < $cutoff
              AND NOT EXISTS (
                  SELECT 1
                  FROM sightings g
                  JOIN listings l ON l.id = g.listing_id
                  WHERE g.snapshot_id = s.id AND l.last_seen_at_utc >= $cutoff);
            """, cutoff);

        if (dryRun)
        {
            return new PruneResult(listings, sightings, snapshots, bytesBefore, bytesBefore);
        }

        Execute("BEGIN IMMEDIATE;");
        try
        {
            ExecuteWithCutoff("DELETE FROM listings WHERE last_seen_at_utc < $cutoff;", cutoff);
            ExecuteWithCutoff("""
                DELETE FROM snapshots
                WHERE taken_at_utc < $cutoff
                  AND NOT EXISTS (
                      SELECT 1 FROM sightings WHERE sightings.snapshot_id = snapshots.id);
                """, cutoff);
            Execute("COMMIT;");
        }
        catch
        {
            Execute("ROLLBACK;");
            throw;
        }

        Execute("VACUUM;");
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        var bytesAfter = new FileInfo(DbPath).Length;
        return new PruneResult(listings, sightings, snapshots, bytesBefore, bytesAfter);
    }

    private (string Planet, string TakenAtUtc) GetSnapshotKey(long snapshotId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT planet, taken_at_utc FROM snapshots WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", snapshotId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Snapshot #{snapshotId} not found.");
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private int ScalarCount(string sql, string cutoff)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private void ExecuteWithCutoff(string sql, string cutoff)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }

    private bool TableExists(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return (long)cmd.ExecuteScalar()! > 0;
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

    private object? ExecuteScalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
