using System.Globalization;
using System.Text;
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
    string Status, int? MaxPerType)
{
    public bool IsComplete => Status == SnapshotStatus.Complete;

    /// <summary>
    /// True when the capture was cut short by an explicit per-type limit. Such a
    /// snapshot is a sample of the market, not the whole listing, so a listing missing
    /// from it is not proof that it left the market (see
    /// <see cref="MarketDb.GetPriceBaselines"/>).
    /// </summary>
    public bool IsTruncated => MaxPerType is not null;
}

public sealed record ItemHistoryRow(
    long SnapshotId, DateTime TakenAtUtc, int Listings,
    double MinPrice, double AvgPrice, double MaxPrice);

public sealed record ItemStatsRow(
    int ItemId, int ItemSubType, int Grade, int Listings,
    double MinPrice, double AvgPrice, double MaxPrice, int MaxCombatPoint, int MaxLevel);

/// <summary>
/// Identity of a bucket of comparable listings. Enhancement level is not the only
/// attribute that moves the price: a piece that rolled four random options is worth
/// considerably more than the same item at the same level with one, so the two are not
/// comparables and must not discount each other. Grade is deliberately absent — it is a
/// property of the item id, so it would partition nothing.
/// </summary>
public readonly record struct BaselineKey(int ItemId, int Level, int OptionCount)
{
    /// <summary>
    /// The bucket a listing belongs to. Single source of the key, so the buckets built
    /// by <see cref="MarketDb.GetPriceBaselines"/> and the lookups done by
    /// <see cref="DealFinder"/> cannot drift apart.
    /// </summary>
    public static BaselineKey Of(ItemProduct product) =>
        new(product.ItemId, product.Level, product.OptionCountFromCombination);

    public override string ToString() => $"item {ItemId}, +{Level}, {OptionCount} opzioni";
}

/// <summary>
/// Historical price baseline for a bucket of comparable listings (see
/// <see cref="BaselineKey"/>): median price and median price-per-CP over the distinct
/// listings observed across snapshots. <see cref="MedianPricePerCp"/> is null when no
/// sample in the bucket has a positive combat point (<see cref="CpSamples"/> is 0).
/// </summary>
public sealed record PriceBaseline(
    BaselineKey Key, int Samples, double MedianPrice,
    int CpSamples, double? MedianPricePerCp);

/// <summary>
/// Which listings a price baseline is computed from. The distinction is the difference
/// between measuring what sellers ask and measuring what the market pays.
/// </summary>
public enum BaselinePopulation
{
    /// <summary>
    /// Every distinct listing observed. These are asking prices: a piece nobody ever
    /// bought stays in the sample until <see cref="MarketDb.Prune"/> removes it and
    /// keeps the median up.
    /// </summary>
    Listed,

    /// <summary>
    /// Only the listings that left the market at a price compatible with a sale, as
    /// classified by <see cref="MarketDb.GetPriceBaselines"/>.
    /// </summary>
    Sold,
}

/// <summary>
/// How the listings in scope were classified. <see cref="Open"/> ones are still on sale
/// (or their disappearance is not yet observable); the two that left the market are told
/// apart by price. The three add up to <see cref="Total"/>.
/// </summary>
public sealed record ListingOutcomes(int Total, int Open, int LikelySold, int LikelyWithdrawn);

/// <summary>
/// What became of a single listing, by the same heuristic
/// <see cref="MarketDb.GetPriceBaselines"/> applies to a whole bucket:
/// <see cref="ListingOutcomes"/> is the count of these.
/// </summary>
public enum ListingOutcome
{
    /// <summary>Still on sale, or its disappearance is not observable yet.</summary>
    Open,

    /// <summary>Gone from the market at a price compatible with a sale.</summary>
    LikelySold,

    /// <summary>Gone from the market well above the going rate, so more likely pulled.</summary>
    LikelyWithdrawn,
}

/// <summary>
/// One listing comparable to the piece being valued, reduced to what an answer is made
/// of. <see cref="ItemId"/> and <see cref="ProductId"/> are carried so that a widened
/// bucket can be taken apart — a range from 11 to 333 NCG is unusable until the 333 can
/// be seen for the outlier it is.
/// </summary>
public sealed record ComparableListing(
    Guid ProductId,
    int ItemId,
    int Level,
    ElementalType Element,
    double Price,
    int CombatPoint,
    DateTime LastSeenAtUtc,
    ListingOutcome Outcome);

/// <summary>
/// The comparables of a bucket, with the classification split of the whole bucket. The
/// split covers every listing matched, not only the population a caller keeps: it is what
/// says how much a <see cref="BaselinePopulation.Sold"/> answer can be trusted, and a
/// caller that filtered first would have nothing left to judge it by.
/// </summary>
public sealed record ComparableSet(
    ListingOutcomes Outcomes,
    IReadOnlyList<ComparableListing> Listings);

/// <summary>
/// Which listings count as comparable. Every field left null is a filter not applied,
/// which is exactly how the widening ladder of <see cref="ValuationService"/> climbs: it
/// drops fields rather than replacing the query.
/// </summary>
public sealed record ComparableFilter
{
    /// <summary>Equipment sub type. Always filtered: nothing compares across it.</summary>
    public required EquipmentType Type { get; init; }

    /// <summary>lib9c grade (rarity). Always filtered, for the same reason.</summary>
    public required int Grade { get; init; }

    /// <summary>Element; null merges all five.</summary>
    public ElementalType? Element { get; init; }

    /// <summary>Enhancement level; null merges every level.</summary>
    public int? Level { get; init; }

    /// <summary>
    /// Exact set of option stat types the listing must carry (lib9c <c>StatType</c>
    /// values); null does not constrain the options. Mutually exclusive with
    /// <see cref="OptionStatCount"/>, which is the looser form of the same filter.
    /// </summary>
    public IReadOnlySet<int>? OptionStats { get; init; }

    /// <summary>
    /// Number of distinct option stat types the listing must carry, whichever they are.
    /// </summary>
    public int? OptionStatCount { get; init; }

    /// <summary>Whether the listing must carry a skill; null does not constrain it.</summary>
    public bool? HasSkill { get; init; }

    /// <summary>Whether the listing must be custom craft; null merges both populations.</summary>
    public bool? ByCustomCraft { get; init; }
}

/// <summary>
/// The baselines of a query together with the population they were measured on, so a
/// caller can state what it is comparing against instead of implying it.
/// </summary>
public sealed record BaselineSet(
    BaselinePopulation Population,
    ListingOutcomes Outcomes,
    IReadOnlyDictionary<BaselineKey, PriceBaseline> Baselines);

/// <summary>
/// Outcome of a retention pass: how many rows were (or, on a dry run, would be)
/// removed and the database file size before/after the pass. On a dry run
/// <see cref="BytesAfter"/> equals <see cref="BytesBefore"/>.
/// </summary>
public sealed record PruneResult(
    int ListingsRemoved, int SightingsRemoved, int SnapshotsRemoved,
    int NotificationsRemoved, long BytesBefore, long BytesAfter);

/// <summary>
/// SQLite storage for market snapshots. Snapshots are logically immutable copies of
/// the listings observed at a given time, but they are stored deduplicated (schema v2):
/// each distinct listing (product_id, whose attributes never change on the market
/// service — a price change creates a new product) is written once in
/// <c>listings</c>, and per-snapshot membership is recorded in the two-integer
/// <c>sightings</c> table. Version 1 databases, which stored a full copy of every
/// listing per snapshot, are migrated automatically on open (a .v1.bak backup of the
/// original file is left next to it, see <see cref="MigrationBackupPath"/>); version 2
/// and 3 databases gain the <c>snapshots.status</c> and <c>snapshots.max_per_type</c>
/// columns in place.
/// </summary>
public sealed class MarketDb : IDisposable
{
    /// <summary>
    /// Default tolerance of the sale heuristic, in percent: a listing that left the
    /// market asking no more than this much above the median ask of its bucket is
    /// counted as sold rather than withdrawn.
    /// </summary>
    public const int DefaultSaleMarginPercent = 20;

    private const long SchemaVersion = 4;

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

        // Columns of the snapshots table, which no migration recreates: a database that
        // already carried a version gets them added in place, one step at a time.
        if (version is > 0 and < 3)
        {
            MigrateV2ToV3();
        }

        if (version is > 0 and < 4)
        {
            MigrateV3ToV4();
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
                status TEXT NOT NULL DEFAULT '{SnapshotStatus.Partial}',
                -- NULL when the capture covered the whole listing of its types; the
                -- --max-per-type limit otherwise. A truncated capture is not evidence
                -- of what was on sale, so sale detection ignores it.
                max_per_type INTEGER
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

            -- Listings already announced by 'deals --notify'. Keyed by product_id, the
            -- market's own identity of an offer, and not by a foreign key to listings:
            -- an alert on the live market names offers no snapshot has stored yet. The
            -- table is created here rather than by a migration because it is new, not
            -- changed, so any existing database acquires it on open (as in P0.5); it
            -- stays small — a deal is rare — and needs no index of its own, the only
            -- query on it being the retention sweep.
            CREATE TABLE IF NOT EXISTS notified_deals(
                product_id TEXT PRIMARY KEY,
                notified_at_utc TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_listings_planet_subtype ON listings(planet, item_sub_type);
            CREATE INDEX IF NOT EXISTS ix_listings_item ON listings(item_id);
            CREATE INDEX IF NOT EXISTS ix_sightings_listing ON sightings(listing_id);
            -- Filter column of Prune, which sweeps the whole table by age with no
            -- planet to narrow it down first.
            CREATE INDEX IF NOT EXISTS ix_listings_last_seen ON listings(last_seen_at_utc);

            -- Covers the baseline query of GetPriceBaselines: planet and the --days
            -- window are its filters, the remaining columns are the ones it selects,
            -- so SQLite answers from the index and never reaches the table. Measured
            -- on two million listings: --days 7 drops from 1,203 to 45 ms and
            -- --days 90 from 1,633 to 573 ms. The whole-history default gets cheaper
            -- too (3,549 to 2,629 ms), because an index row carries nine columns
            -- while a table row drags stats_json and skills_json along with it. The
            -- price is about half the size of the database again, and six seconds on
            -- the first open of an existing two-million-listing one, while the index
            -- is built (P2.7).
            CREATE INDEX IF NOT EXISTS ix_listings_baseline ON listings(
                planet, last_seen_at_utc, item_id, level, option_count,
                price, combat_point, item_sub_type, last_seen_snapshot_id);

            -- Filter columns of GetComparables, the query behind a valuation. It cannot
            -- cover that query and is not meant to: the option stats live inside
            -- stats_json, which no index can reach, so the rows are visited anyway. What
            -- it does is cut the visit down to a bucket — five small columns, not the
            -- second doubling of the database that ix_listings_baseline cost.
            CREATE INDEX IF NOT EXISTS ix_listings_valuation ON listings(
                planet, item_sub_type, grade, elemental_type, level);
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
    /// Adds the capture-limit marker to a v3 database. Existing rows keep NULL — a full
    /// capture — because that is what <c>snapshot</c> does unless <c>--max-per-type</c>
    /// is passed, and the opposite assumption would throw away the whole history for
    /// sale detection over a limit that may never have been used.
    /// </summary>
    private void MigrateV3ToV4() =>
        Execute("ALTER TABLE snapshots ADD COLUMN max_per_type INTEGER;");

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

    /// <summary>
    /// Creates a new snapshot row and returns its id. <paramref name="maxPerType"/>
    /// records a capture deliberately cut short at that many listings per type, so that
    /// <see cref="GetPriceBaselines"/> does not read the listings it never downloaded as
    /// listings that left the market.
    /// </summary>
    public long CreateSnapshot(
        string planet,
        IEnumerable<EquipmentType> types,
        DateTime takenAtUtc,
        int? maxPerType = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots(
                planet, taken_at_utc, item_sub_types, product_count, max_per_type)
            VALUES ($planet, $takenAt, $types, 0, $maxPerType);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$planet", planet);
        cmd.Parameters.AddWithValue("$takenAt", takenAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$types", string.Join(",", types.Select(t => (int)t)));
        cmd.Parameters.AddWithValue("$maxPerType", (object?)maxPerType ?? DBNull.Value);
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
            SELECT id, planet, taken_at_utc, item_sub_types, product_count, status,
                   max_per_type
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
            SELECT id, planet, taken_at_utc, item_sub_types, product_count, status,
                   max_per_type
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
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6)));
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
    /// Per-<see cref="BaselineKey"/> historical price baselines over the listings of a
    /// planet. Listings are stored deduplicated, so each one contributes a single sample
    /// no matter how many snapshots observed it and stale listings do not dominate the
    /// medians. The <paramref name="sinceUtc"/> window keeps a listing when it was still
    /// on sale within the window (its last sighting is inside it). Rows with a
    /// non-positive combat point contribute to the price median only.
    /// <para>
    /// <paramref name="population"/> decides <em>what</em> is being measured.
    /// <see cref="BaselinePopulation.Listed"/> takes every observed listing: those are
    /// asking prices, and a piece nobody bought keeps its price in the sample until the
    /// retention pass removes it. <see cref="BaselinePopulation.Sold"/> takes only the
    /// listings that plausibly concluded, in two steps:
    /// </para>
    /// <list type="number">
    /// <item>a listing has <em>left the market</em> when a later snapshot that could
    /// have seen it did not: same planet, covering its equipment type, complete (not an
    /// interrupted capture) and untruncated (no <c>--max-per-type</c>). Anything else is
    /// still on sale as far as this database knows;</item>
    /// <item>of those, one that asked no more than <paramref name="saleMarginPercent"/>
    /// above the median ask of its own bucket is counted as a sale; a listing that
    /// disappeared well above the going rate is far more likely to have been withdrawn
    /// or to have expired, and is dropped.</item>
    /// </list>
    /// <para>
    /// The heuristic is deliberately asymmetric — sales concentrate at the cheap end of
    /// the book — so a sold baseline sits below the corresponding listed one by
    /// construction; <see cref="BaselineSet.Outcomes"/> reports the split so the
    /// tolerance can be judged instead of trusted. Replacing it with the on-chain
    /// <c>BuyProduct</c> transactions is the next step up in accuracy.
    /// </para>
    /// </summary>
    public BaselineSet GetPriceBaselines(
        string planet,
        EquipmentType? type = null,
        DateTime? sinceUtc = null,
        BaselinePopulation population = BaselinePopulation.Listed,
        double saleMarginPercent = DefaultSaleMarginPercent)
    {
        var frontier = GetCoverageFrontier(planet);
        var tolerated = 1 + saleMarginPercent / 100;
        var wantSold = population == BaselinePopulation.Sold;

        var baselines = new Dictionary<BaselineKey, PriceBaseline>();
        var concluded = new List<BaselineRow>();
        var scratch = new List<double>();
        int total = 0, open = 0, likelySold = 0, likelyWithdrawn = 0;

        foreach (var bucket in ReadBuckets(planet, type, sinceUtc))
        {
            // The ask median classifies the listings of this bucket, so it is computed
            // whichever population was asked for.
            var listed = Summarise(bucket[0].Key, bucket, scratch);
            total += bucket.Count;

            concluded.Clear();
            foreach (var row in bucket)
            {
                if (!frontier.TryGetValue(row.SubType, out var lastProof)
                    || lastProof <= row.LastSeenSnapshotId)
                {
                    open++;
                    continue;
                }

                // No usable median means nothing to compare the asking price against:
                // keep the listing rather than invent a reason to discard it.
                if (listed.MedianPrice > 0 && row.Price > listed.MedianPrice * tolerated)
                {
                    likelyWithdrawn++;
                    continue;
                }

                concluded.Add(row);
            }

            likelySold += concluded.Count;

            if (!wantSold)
            {
                baselines[listed.Key] = listed;
            }
            else if (concluded.Count > 0)
            {
                baselines[listed.Key] = Summarise(listed.Key, concluded, scratch);
            }
        }

        return new BaselineSet(
            population,
            new ListingOutcomes(total, open, likelySold, likelyWithdrawn),
            baselines);
    }

    /// <summary>One listing, reduced to what a baseline is made of.</summary>
    private readonly record struct BaselineRow(
        BaselineKey Key, int SubType, long LastSeenSnapshotId, double Price, int CombatPoint);

    /// <summary>
    /// The baseline query carrying only the filters that were actually asked for.
    /// Writing the optional ones as <c>$p IS NULL OR column = $p</c> keeps the SQL
    /// constant and costs the index: a disjunction is not sargable, so the planner
    /// resolves it by scanning whatever the window. That idiom is what kept
    /// <c>ix_listings_last_seen</c> out of this query for as long as it existed (P2.7).
    /// <para>
    /// The selected columns are, in order, the trailing columns of
    /// <c>ix_listings_baseline</c>. That is what makes the index cover the query;
    /// selecting one more silently uncovers it and the cost comes back without
    /// anything breaking, which is why a test asserts on the query plan.
    /// </para>
    /// </summary>
    internal static string BaselineQuery(EquipmentType? type, DateTime? sinceUtc)
    {
        var sql = new StringBuilder("""
            SELECT item_id, level, option_count, price, combat_point,
                   item_sub_type, last_seen_snapshot_id
            FROM listings
            WHERE planet = $planet
            """);

        if (type is not null)
        {
            sql.Append("\n  AND item_sub_type = $subType");
        }

        if (sinceUtc is not null)
        {
            sql.Append("\n  AND last_seen_at_utc >= $since");
        }

        return sql.Append("\nORDER BY item_id, level, option_count;").ToString();
    }

    /// <summary>
    /// The listings of a planet, one <see cref="BaselineKey"/> bucket at a time. Ordering
    /// the query by the bucket key leaves the grouping to SQLite, so what is held in
    /// memory is a bucket — a handful of comparable pieces — instead of the whole
    /// history, which a year of captures makes millions of rows. The price is the sort,
    /// which SQLite spills to disk when it needs to; that is the trade, because the
    /// managed heap has nowhere to spill.
    /// <para>
    /// The list is reused from one bucket to the next: read a bucket before asking for
    /// the following one, and do not hold on to it.
    /// </para>
    /// </summary>
    private IEnumerable<List<BaselineRow>> ReadBuckets(
        string planet, EquipmentType? type, DateTime? sinceUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = BaselineQuery(type, sinceUtc);
        cmd.Parameters.AddWithValue("$planet", planet);
        if (type is not null)
        {
            cmd.Parameters.AddWithValue("$subType", (int)type.Value);
        }

        if (sinceUtc is not null)
        {
            cmd.Parameters.AddWithValue(
                "$since", sinceUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        }

        var bucket = new List<BaselineRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new BaselineRow(
                new BaselineKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetDouble(3),
                reader.GetInt32(4));

            if (bucket.Count > 0 && !row.Key.Equals(bucket[0].Key))
            {
                yield return bucket;
                bucket.Clear();
            }

            bucket.Add(row);
        }

        if (bucket.Count > 0)
        {
            yield return bucket;
        }
    }

    /// <summary>
    /// The baseline of a single bucket: median price over every listing given, median
    /// price-per-CP over those that carry a combat point. <paramref name="scratch"/> is
    /// the buffer the values are sorted in, passed in so that a whole history costs one
    /// of them instead of two per bucket.
    /// </summary>
    private static PriceBaseline Summarise(
        BaselineKey key, List<BaselineRow> rows, List<double> scratch)
    {
        scratch.Clear();
        foreach (var row in rows)
        {
            scratch.Add(row.Price);
        }

        var medianPrice = Median(scratch);

        scratch.Clear();
        foreach (var row in rows)
        {
            if (row.CombatPoint > 0)
            {
                scratch.Add(row.Price / row.CombatPoint);
            }
        }

        return new PriceBaseline(
            key, rows.Count, medianPrice,
            scratch.Count, scratch.Count > 0 ? Median(scratch) : null);
    }

    /// <summary>
    /// The listings comparable to a piece described from the outside (see
    /// <see cref="ValuationKey"/>), each classified the way
    /// <see cref="GetPriceBaselines"/> classifies the members of a bucket: gone from the
    /// market below the going rate is a likely sale, gone well above it a likely
    /// withdrawal, everything else still open. The two paths share
    /// <see cref="GetCoverageFrontier"/> and the ask-median comparison rather than each
    /// keeping a copy, because two copies of one heuristic drift.
    /// <para>
    /// Which listings match is decided in two places, and it has to be: the sub type, the
    /// grade, the element and the level are columns, so SQL narrows the table down to a
    /// bucket through <c>ix_listings_valuation</c>; the option stats and the skill live
    /// inside <c>stats_json</c> and <c>skills_json</c>, which no index reaches, so they
    /// are matched in memory over the rows that survived. A bucket is tens of listings,
    /// which is what makes the second half affordable.
    /// </para>
    /// <para>
    /// The result is ordered by price, cheapest first.
    /// </para>
    /// </summary>
    /// <param name="sinceUtc">
    /// Keep a listing only when it was still on sale within the window, i.e. its last
    /// sighting falls inside it. Null uses the whole history.
    /// </param>
    public ComparableSet GetComparables(
        string planet,
        ComparableFilter filter,
        DateTime? sinceUtc = null,
        double saleMarginPercent = DefaultSaleMarginPercent)
    {
        if (filter.OptionStats is not null && filter.OptionStatCount is not null)
        {
            // The looser filter would swallow the stricter one without a trace, and the
            // caller would get an answer to a question it did not ask.
            throw new ArgumentException(
                "Un filtro sui comparabili porta l'insieme delle stat-opzione oppure il " +
                "loro numero, non entrambi.",
                nameof(filter));
        }

        var matched = ReadComparables(planet, filter, sinceUtc);
        if (matched.Count == 0)
        {
            return new ComparableSet(
                new ListingOutcomes(0, 0, 0, 0), Array.Empty<ComparableListing>());
        }

        var frontier = GetCoverageFrontier(planet);
        var tolerated = 1 + saleMarginPercent / 100;

        var scratch = new List<double>(matched.Count);
        foreach (var row in matched)
        {
            scratch.Add(row.Listing.Price);
        }

        var medianAsk = Median(scratch);

        int open = 0, likelySold = 0, likelyWithdrawn = 0;
        var listings = new List<ComparableListing>(matched.Count);
        foreach (var row in matched)
        {
            ListingOutcome outcome;
            if (!frontier.TryGetValue(row.SubType, out var lastProof)
                || lastProof <= row.LastSeenSnapshotId)
            {
                outcome = ListingOutcome.Open;
                open++;
            }
            else if (medianAsk > 0 && row.Listing.Price > medianAsk * tolerated)
            {
                outcome = ListingOutcome.LikelyWithdrawn;
                likelyWithdrawn++;
            }
            else
            {
                outcome = ListingOutcome.LikelySold;
                likelySold++;
            }

            listings.Add(row.Listing with { Outcome = outcome });
        }

        listings.Sort((a, b) => a.Price.CompareTo(b.Price));

        return new ComparableSet(
            new ListingOutcomes(matched.Count, open, likelySold, likelyWithdrawn),
            listings);
    }

    /// <summary>One comparable, plus the two fields only the classification needs.</summary>
    private readonly record struct ComparableRow(
        ComparableListing Listing, int SubType, long LastSeenSnapshotId);

    /// <summary>
    /// The comparables query carrying only the filters that were actually asked for, for
    /// the reason spelled out on <see cref="BaselineQuery"/>: <c>$p IS NULL OR col = $p</c>
    /// is not sargable and would cost the index, and the widening ladder removes one
    /// predicate at a time, so the constant-SQL form would be the wrong shape here twice
    /// over.
    /// </summary>
    internal static string ComparableQuery(ComparableFilter filter, DateTime? sinceUtc)
    {
        var sql = new StringBuilder("""
            SELECT product_id, item_id, level, elemental_type, price, combat_point,
                   last_seen_at_utc, item_sub_type, last_seen_snapshot_id,
                   stats_json, skills_json
            FROM listings
            WHERE planet = $planet
              AND item_sub_type = $subType
              AND grade = $grade
            """);

        if (filter.Element is not null)
        {
            sql.Append("\n  AND elemental_type = $elemental");
        }

        if (filter.Level is not null)
        {
            sql.Append("\n  AND level = $level");
        }

        if (filter.ByCustomCraft is not null)
        {
            sql.Append("\n  AND by_custom_craft = $custom");
        }

        if (sinceUtc is not null)
        {
            // The unary plus keeps this term out of index selection, and it has to. Left
            // as a plain column reference, SQLite answers the whole query from
            // ix_listings_baseline on (planet, last_seen_at_utc) — a range over every
            // listing of the planet in the window, none of which it can rule out from the
            // index, because neither the grade nor the element is in it. Hidden this way,
            // the five equalities win, ix_listings_valuation cuts the visit down to the
            // bucket, and the window is checked on the handful of rows left. A no-op on
            // the value itself: same text, same comparison, same rows.
            sql.Append("\n  AND +last_seen_at_utc >= $since");
        }

        return sql.Append(';').ToString();
    }

    /// <summary>
    /// The rows matching <paramref name="filter"/>, unclassified. The JSON columns are
    /// deserialized only when a filter needs them: the skills of a bucket are read when
    /// the skill is asked about and never otherwise.
    /// </summary>
    private List<ComparableRow> ReadComparables(
        string planet, ComparableFilter filter, DateTime? sinceUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = ComparableQuery(filter, sinceUtc);
        cmd.Parameters.AddWithValue("$planet", planet);
        cmd.Parameters.AddWithValue("$subType", (int)filter.Type);
        cmd.Parameters.AddWithValue("$grade", filter.Grade);

        if (filter.Element is not null)
        {
            cmd.Parameters.AddWithValue("$elemental", (int)filter.Element.Value);
        }

        if (filter.Level is not null)
        {
            cmd.Parameters.AddWithValue("$level", filter.Level.Value);
        }

        if (filter.ByCustomCraft is not null)
        {
            cmd.Parameters.AddWithValue("$custom", filter.ByCustomCraft.Value ? 1 : 0);
        }

        if (sinceUtc is not null)
        {
            cmd.Parameters.AddWithValue(
                "$since", sinceUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        }

        var matchesOptions = filter.OptionStats is not null || filter.OptionStatCount is not null;

        var rows = new List<ComparableRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (matchesOptions)
            {
                var options = ValuationKey.OptionStatsOf(DeserializeOrEmpty<StatModel>(reader, 9));
                if (filter.OptionStats is not null && !options.SetEquals(filter.OptionStats))
                {
                    continue;
                }

                if (filter.OptionStatCount is not null && options.Count != filter.OptionStatCount)
                {
                    continue;
                }
            }

            if (filter.HasSkill is not null
                && DeserializeOrEmpty<SkillModel>(reader, 10).Count > 0 != filter.HasSkill.Value)
            {
                continue;
            }

            rows.Add(new ComparableRow(
                new ComparableListing(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    (ElementalType)reader.GetInt32(3),
                    reader.GetDouble(4),
                    reader.GetInt32(5),
                    ParseUtc(reader.GetString(6)),
                    ListingOutcome.Open),
                reader.GetInt32(7),
                reader.GetInt64(8)));
        }

        return rows;
    }

    /// <summary>
    /// Per equipment type, the id of the most recent snapshot of a planet that is proof
    /// of what was on sale: complete, untruncated, and covering that type. A listing
    /// last seen before its type's frontier is gone from the market; one last seen at or
    /// after it may simply still be listed. Types never captured that way are absent
    /// from the map, so nothing about them is inferred.
    /// <para>
    /// Shared by <see cref="GetPriceBaselines"/> and <see cref="GetComparables"/>: what
    /// counts as proof that a listing left the market is one decision, not two.
    /// </para>
    /// </summary>
    private Dictionary<int, long> GetCoverageFrontier(string planet)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, item_sub_types
            FROM snapshots
            WHERE planet = $planet
              AND status = '{SnapshotStatus.Complete}'
              AND max_per_type IS NULL
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$planet", planet);

        var frontier = new Dictionary<int, long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var types = reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in types)
            {
                if (int.TryParse(
                        token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var subType))
                {
                    // Rows arrive in id order, so the last write per type wins.
                    frontier[subType] = id;
                }
            }
        }

        return frontier;
    }

    /// <summary>
    /// Which of <paramref name="productIds"/> have already been the subject of an alert
    /// (see <see cref="DealAlertService"/>). Asking about the listings at hand, rather
    /// than reading the whole table, keeps the cost proportional to the deals found and
    /// not to how long the job has been running.
    /// </summary>
    public IReadOnlySet<Guid> GetAnnouncedProducts(IEnumerable<Guid> productIds)
    {
        var wanted = productIds.Distinct().ToList();
        var announced = new HashSet<Guid>();

        // SQLite caps the parameters of a single statement: a list of deals is short, but
        // that is the caller's business rather than a limit to inherit.
        const int batchSize = 500;
        for (var start = 0; start < wanted.Count; start += batchSize)
        {
            var batch = wanted.GetRange(start, Math.Min(batchSize, wanted.Count - start));
            using var cmd = _conn.CreateCommand();
            var placeholders = new string[batch.Count];
            for (var i = 0; i < batch.Count; i++)
            {
                placeholders[i] = "$p" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(placeholders[i], batch[i].ToString("D"));
            }

            cmd.CommandText =
                "SELECT product_id FROM notified_deals WHERE product_id IN (" +
                string.Join(", ", placeholders) + ");";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                announced.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        return announced;
    }

    /// <summary>
    /// Records listings as announced, and returns how many were not recorded already.
    /// Re-recording one keeps its first date: the row answers "has this been announced",
    /// and the answer does not change with the second alert that would have repeated it.
    /// </summary>
    public int RecordAnnounced(IEnumerable<Guid> productIds, DateTime atUtc)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO notified_deals(product_id, notified_at_utc)
            VALUES($productId, $at);
            """;
        cmd.Parameters.AddWithValue("$at", atUtc.ToString("O", CultureInfo.InvariantCulture));
        var productId = cmd.Parameters.Add("$productId", SqliteType.Text);

        var recorded = 0;
        foreach (var id in productIds)
        {
            productId.Value = id.ToString("D");
            recorded += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return recorded;
    }

    /// <summary>
    /// Retention pass: removes listings whose last sighting is older than
    /// <paramref name="cutoffUtc"/> (their sightings cascade away) and snapshots taken
    /// before the cutoff that are left with no sightings, then compacts the file with
    /// VACUUM. With <paramref name="dryRun"/> nothing is modified and the result
    /// reports what would be removed. The product_count of surviving snapshots is not
    /// rewritten: it documents the size of the listing at capture time.
    /// <para>
    /// An alert is forgotten only when its listing is: old enough, and no longer in the
    /// history that survives the pass. Forgetting it while the offer still stands would
    /// announce it a second time, which is the one thing
    /// <see cref="DealAlertService"/> exists to prevent.
    /// </para>
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

        // Same predicate as the DELETE below, on purpose: written as "no listing of this
        // product survives the cutoff" rather than "no listing at all", it gives the same
        // answer before and after the listings are removed, so the dry run counts what
        // the real pass removes instead of an optimistic subset of it.
        var notifications = ScalarCount(NotifiedDealsCutoff("SELECT COUNT(*)"), cutoff);

        if (dryRun)
        {
            return new PruneResult(
                listings, sightings, snapshots, notifications, bytesBefore, bytesBefore);
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
            ExecuteWithCutoff(NotifiedDealsCutoff("DELETE"), cutoff);
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
        return new PruneResult(
            listings, sightings, snapshots, notifications, bytesBefore, bytesAfter);
    }

    /// <summary>
    /// The retention rule for announced deals, as a <c>SELECT COUNT(*)</c> or a
    /// <c>DELETE</c>. One text for both so the dry run cannot promise something different
    /// from what the pass does.
    /// </summary>
    private static string NotifiedDealsCutoff(string head) => $"""
        {head} FROM notified_deals
        WHERE notified_at_utc < $cutoff
          AND NOT EXISTS (
              SELECT 1 FROM listings
              WHERE listings.product_id = notified_deals.product_id
                AND listings.last_seen_at_utc >= $cutoff);
        """;

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
