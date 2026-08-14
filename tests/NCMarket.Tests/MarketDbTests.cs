using Microsoft.Data.Sqlite;
using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class MarketDbTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_snapshot_is_partial_until_it_is_finalized()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var id = db.CreateSnapshot("heimdall", EquipmentTypes.All, Now);
        db.AddProducts(id, new[] { TestData.Product() });

        Assert.False(db.GetSnapshot(id)!.IsComplete);

        db.FinalizeSnapshot(id);

        var finalized = db.GetSnapshot(id)!;
        Assert.True(finalized.IsComplete);
        Assert.Equal(1, finalized.ProductCount);
    }

    [Fact]
    public void GetLatestSnapshotId_ignores_an_interrupted_snapshot()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var complete = TestData.AddCompleteSnapshot(
            db, Now.AddHours(-6), new[] { TestData.Product() });

        // Una cattura interrotta a metà: la riga esiste e ha già dei sightings, ma
        // FinalizeSnapshot non è mai stato raggiunto.
        var interrupted = db.CreateSnapshot("heimdall", EquipmentTypes.All, Now);
        db.AddProducts(interrupted, new[] { TestData.Product(itemId: 10100001) });

        Assert.Equal(complete, db.GetLatestSnapshotId("heimdall"));
    }

    [Fact]
    public void GetLatestSnapshotId_is_null_when_every_snapshot_is_partial()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var id = db.CreateSnapshot("heimdall", EquipmentTypes.All, Now);
        db.AddProducts(id, new[] { TestData.Product() });

        Assert.Null(db.GetLatestSnapshotId("heimdall"));
    }

    [Fact]
    public void GetLatestSnapshotId_is_per_planet()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var heimdall = TestData.AddCompleteSnapshot(
            db, Now.AddHours(-6), new[] { TestData.Product() });
        TestData.AddCompleteSnapshot(db, Now, new[] { TestData.Product() }, planet: "odin");

        Assert.Equal(heimdall, db.GetLatestSnapshotId("heimdall"));
    }

    [Fact]
    public void A_listing_seen_again_is_stored_once_and_sighted_twice()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var productId = Guid.NewGuid();
        var first = TestData.AddCompleteSnapshot(
            db, Now.AddHours(-6), new[] { TestData.Product(productId: productId) });
        var second = TestData.AddCompleteSnapshot(
            db, Now, new[] { TestData.Product(productId: productId) });

        Assert.Single(db.GetSnapshotProducts(first));
        Assert.Single(db.GetSnapshotProducts(second));

        using var conn = temp.Connect();
        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM listings;"));
        Assert.Equal(2L, Scalar(conn, "SELECT COUNT(*) FROM sightings;"));

        // Una sola inserzione distinta: il baseline non deve contarla due volte.
        var baseline = db.GetPriceBaselines("heimdall")[(10100000, 0)];
        Assert.Equal(1, baseline.Samples);
    }

    [Fact]
    public void GetPriceBaselines_takes_the_median_of_each_item_and_level()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now, new[]
        {
            TestData.Product(price: 100m, combatPoint: 1000),
            TestData.Product(price: 300m, combatPoint: 1000),
            TestData.Product(price: 200m, combatPoint: 1000),
            TestData.Product(level: 3, price: 900m, combatPoint: 1000),
        });

        var baselines = db.GetPriceBaselines("heimdall");

        var level0 = baselines[(10100000, 0)];
        Assert.Equal(3, level0.Samples);
        Assert.Equal(200d, level0.MedianPrice);
        Assert.Equal(0.2d, level0.MedianPricePerCp);

        Assert.Equal(900d, baselines[(10100000, 3)].MedianPrice);
    }

    [Fact]
    public void GetPriceBaselines_leaves_the_price_per_cp_null_without_cp_samples()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now, new[]
        {
            TestData.Product(price: 100m, combatPoint: 0),
            TestData.Product(price: 200m, combatPoint: 0),
        });

        var baseline = db.GetPriceBaselines("heimdall")[(10100000, 0)];
        Assert.Equal(0, baseline.CpSamples);
        Assert.Null(baseline.MedianPricePerCp);
    }

    [Fact]
    public void GetPriceBaselines_drops_listings_last_seen_before_the_window()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(
            db, Now.AddDays(-40), new[] { TestData.Product(price: 1000m) });
        TestData.AddCompleteSnapshot(
            db, Now.AddDays(-1), new[] { TestData.Product(price: 100m) });

        var all = db.GetPriceBaselines("heimdall");
        Assert.Equal(2, all[(10100000, 0)].Samples);

        var recent = db.GetPriceBaselines("heimdall", sinceUtc: Now.AddDays(-7));
        Assert.Equal(1, recent[(10100000, 0)].Samples);
        Assert.Equal(100d, recent[(10100000, 0)].MedianPrice);
    }

    [Fact]
    public void Prune_dry_run_reports_the_rows_without_deleting_them()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now.AddDays(-400), new[] { TestData.Product() });

        var result = db.Prune(Now.AddDays(-365), dryRun: true);

        Assert.Equal(1, result.ListingsRemoved);
        Assert.Equal(1, result.SightingsRemoved);
        Assert.Equal(1, result.SnapshotsRemoved);
        Assert.Equal(result.BytesBefore, result.BytesAfter);
        Assert.Single(db.GetSnapshots());
    }

    [Fact]
    public void Prune_removes_only_what_is_older_than_the_cutoff()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now.AddDays(-400), new[] { TestData.Product() });
        var recent = TestData.AddCompleteSnapshot(
            db, Now.AddDays(-1), new[] { TestData.Product(itemId: 10100001) });

        var result = db.Prune(Now.AddDays(-365));

        Assert.Equal(1, result.ListingsRemoved);
        Assert.Equal(recent, Assert.Single(db.GetSnapshots()).Id);
    }

    [Fact]
    public void Prune_filters_listings_through_the_last_seen_index()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        TestData.AddCompleteSnapshot(db, Now, new[] { TestData.Product() });

        using var conn = temp.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "EXPLAIN QUERY PLAN DELETE FROM listings WHERE last_seen_at_utc < '2026-01-01';";

        var plan = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            plan.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        Assert.Contains(
            plan, line => line.Contains("ix_listings_last_seen", StringComparison.Ordinal));
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
