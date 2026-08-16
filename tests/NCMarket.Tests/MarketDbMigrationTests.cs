using System.Globalization;
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
