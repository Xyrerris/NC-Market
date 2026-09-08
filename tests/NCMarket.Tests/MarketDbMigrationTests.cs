using System.Globalization;
using Microsoft.Data.Sqlite;
using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// Opening a database is what migrates it, so these tests build the old schema by hand
/// and check what the constructor made of it.
/// </summary>
public sealed class MarketDbMigrationTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_v2_database_gains_the_status_column_on_open()
    {
        using var temp = new TempDatabase();
        WriteV2Database(temp);

        using (var db = temp.Open())
        {
            var snapshots = db.GetSnapshots().ToDictionary(s => s.Id);

            // Fino alla v2 product_count era scritto solo da FinalizeSnapshot: un conteggio
            // valorizzato è quindi la firma di una cattura arrivata in fondo.
            Assert.True(snapshots[1].IsComplete);
            Assert.False(snapshots[2].IsComplete);
        }

        using var conn = temp.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Assert.Equal(4L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void A_v2_database_gains_the_capture_limit_column_on_open()
    {
        using var temp = new TempDatabase();
        WriteV2Database(temp);

        using var db = temp.Open();

        // Gli snapshot preesistenti valgono come catture integrali: è ciò che fa il
        // comando 'snapshot' quando --max-per-type non viene passato, e assumere il
        // contrario azzererebbe lo storico utile alla rilevazione delle vendite.
        Assert.All(db.GetSnapshots(), s => Assert.False(s.IsTruncated));

        // La colonna serve alla rilevazione delle vendite: senza, la query fallirebbe.
        // L'unica inserzione è stata vista nell'ultimo snapshot completo, quindi risulta
        // ancora in vendita.
        var sold = db.GetPriceBaselines("heimdall", population: BaselinePopulation.Sold);
        Assert.Empty(sold.Baselines);
        Assert.Equal(1, sold.Outcomes.Open);
    }

    [Fact]
    public void A_v2_database_keeps_its_data_and_gains_the_missing_index()
    {
        using var temp = new TempDatabase();
        WriteV2Database(temp);

        using var db = temp.Open();

        Assert.Equal(2, db.GetSnapshots().Count);
        Assert.Single(db.GetSnapshotProducts(1));
        Assert.Equal(1, db.GetLatestSnapshotId("heimdall"));

        using var conn = temp.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", "ix_listings_last_seen");
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// The one destructive migration: v1 stored a full copy of every listing in every
    /// snapshot, v2 stores each listing once and records membership in <c>sightings</c>.
    /// A listing seen twice has to survive as one row seen twice — collapsing it to one
    /// sighting would erase the evidence the sale heuristic reads.
    /// </summary>
    [Fact]
    public void A_v1_database_is_deduplicated_without_losing_a_sighting()
    {
        using var temp = new TempDatabase();
        WriteV1Database(temp);

        using var db = temp.Open();

        Assert.Equal(2, db.GetSnapshots().Count);

        // Lo snapshot 1 vedeva solo la prima inserzione, il 2 entrambe: è la differenza
        // che in v1 costava una copia completa e in v2 costa due interi.
        Assert.Single(db.GetSnapshotProducts(1));
        Assert.Equal(2, db.GetSnapshotProducts(2).Count);
        Assert.Equal(2, db.GetLatestSnapshotId("heimdall"));

        // E la copia sopravvissuta è la stessa inserzione, non una delle due buttata via.
        var kept = db.GetSnapshotProducts(1).Single();
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), kept.ProductId);
        Assert.Equal(100m, kept.Price);
        Assert.Equal(1000, kept.CombatPoint);
    }

    /// <summary>
    /// v1 had nowhere to record when a listing was first and last seen: the range is
    /// reconstructed from the snapshot ids it appeared in, and the timestamp from the
    /// snapshot it was last seen in. Both are what the <c>--days</c> window and the sale
    /// detection read, so taking them from the wrong row would reshape history silently.
    /// </summary>
    [Fact]
    public void A_migrated_listing_learns_when_it_was_first_and_last_seen()
    {
        using var temp = new TempDatabase();
        WriteV1Database(temp);

        using (temp.Open())
        {
            // Aprire il database è ciò che lo migra.
        }

        using var conn = temp.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT first_seen_snapshot_id, last_seen_snapshot_id, last_seen_at_utc
            FROM listings WHERE product_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", "11111111-1111-1111-1111-111111111111");
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(Iso(Now), reader.GetString(2));
    }

    /// <summary>
    /// A destructive migration with no way back is data loss waiting for a bug, so the
    /// original file is copied next to the database before anything is dropped — and the
    /// path is published, because a backup nobody is told about is not a backup. A v1
    /// database also comes out at the current version, not at 2: the column migrations
    /// run right after.
    /// </summary>
    [Fact]
    public void A_v1_database_leaves_a_backup_behind_and_comes_out_current()
    {
        using var temp = new TempDatabase();
        WriteV1Database(temp);

        string? backup;
        using (var db = temp.Open())
        {
            backup = db.MigrationBackupPath;

            // Le colonne aggiunte dopo la v2 ci sono anche per chi arriva dalla v1: senza,
            // la prima query sulla rilevazione delle vendite fallirebbe.
            Assert.All(db.GetSnapshots(), s => Assert.False(s.IsTruncated));
            Assert.True(db.GetSnapshots().Single(s => s.Id == 1).IsComplete);
        }

        Assert.Equal(temp.DbPath + ".v1.bak", backup);
        Assert.True(File.Exists(backup));

        using var conn = temp.Connect();

        // La tabella v1 è sparita — restare vuota accanto alla nuova sarebbe peggio che
        // non migrare — e le tre presenze sono tutte lì.
        Assert.Equal(0L, Scalar(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'products';"));
        Assert.Equal(3L, Scalar(conn, "SELECT COUNT(*) FROM sightings;"));
        Assert.Equal(4L, Scalar(conn, "PRAGMA user_version;"));
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Reproduces the original schema: one full copy of every listing per snapshot, and no
    /// version stamp at all — v1 predates <c>user_version</c>, which is why the open path
    /// recognises it by the presence of the <c>products</c> table instead.
    /// </summary>
    private static void WriteV1Database(TempDatabase temp)
    {
        using var conn = temp.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE snapshots(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                planet TEXT NOT NULL,
                taken_at_utc TEXT NOT NULL,
                item_sub_types TEXT NOT NULL,
                product_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE products(
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

            CREATE INDEX ix_products_item ON products(item_id, snapshot_id);
            CREATE INDEX ix_products_subtype ON products(item_sub_type, snapshot_id);

            INSERT INTO snapshots(id, planet, taken_at_utc, item_sub_types, product_count)
            VALUES
                (1, 'heimdall', '{Iso(Now.AddHours(-6))}', '10', 1),
                (2, 'heimdall', '{Iso(Now)}', '10', 2);

            INSERT INTO products(
                snapshot_id, product_id, item_sub_type, item_id, icon_id, grade, level,
                combat_point, elemental_type, price, quantity, unit_price, crystal,
                crystal_per_price, option_count, by_custom_craft, seller_agent,
                seller_avatar, registered_block_index, legacy, stats_json, skills_json)
            VALUES
                (1, '11111111-1111-1111-1111-111111111111', 10, 10100000, 10100000, 3, 0,
                 1000, 0, 100.0, 1.0, 100.0, 0, 0, 1, 0, '0xagent', '0xavatar', 1, 0,
                 '[]', '[]'),
                (2, '11111111-1111-1111-1111-111111111111', 10, 10100000, 10100000, 3, 0,
                 1000, 0, 100.0, 1.0, 100.0, 0, 0, 1, 0, '0xagent', '0xavatar', 1, 0,
                 '[]', '[]'),
                (2, '22222222-2222-2222-2222-222222222222', 10, 10100000, 10100000, 3, 0,
                 2000, 0, 200.0, 1.0, 200.0, 0, 0, 2, 0, '0xagent', '0xavatar', 2, 0,
                 '[]', '[]');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reproduces the schema as it was before the status column existed.</summary>
    private static void WriteV2Database(TempDatabase temp)
    {
        using var conn = temp.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE snapshots(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                planet TEXT NOT NULL,
                taken_at_utc TEXT NOT NULL,
                item_sub_types TEXT NOT NULL,
                product_count INTEGER NOT NULL DEFAULT 0
            );

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

            CREATE INDEX ix_listings_planet_subtype ON listings(planet, item_sub_type);
            CREATE INDEX ix_listings_item ON listings(item_id);
            CREATE INDEX ix_sightings_listing ON sightings(listing_id);

            INSERT INTO snapshots(id, planet, taken_at_utc, item_sub_types, product_count)
            VALUES
                (1, 'heimdall', '{Iso(Now.AddHours(-6))}', '10', 1),
                (2, 'heimdall', '{Iso(Now)}', '10', 0);

            INSERT INTO listings(
                id, product_id, planet, item_sub_type, item_id, icon_id, grade, level,
                combat_point, elemental_type, price, quantity, unit_price, crystal,
                crystal_per_price, option_count, by_custom_craft, seller_agent,
                seller_avatar, registered_block_index, legacy, stats_json, skills_json,
                first_seen_snapshot_id, last_seen_snapshot_id, last_seen_at_utc)
            VALUES(
                1, '11111111-1111-1111-1111-111111111111', 'heimdall', 10, 10100000,
                10100000, 3, 0, 1000, 0, 100.0, 1.0, 100.0, 0, 0, 1, 0, '0xagent',
                '0xavatar', 1, 0, '[]', '[]', 1, 1, '{Iso(Now.AddHours(-6))}');

            INSERT INTO sightings(snapshot_id, listing_id) VALUES(1, 1);

            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
