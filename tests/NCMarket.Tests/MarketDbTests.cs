using Microsoft.Data.Sqlite;
using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

public sealed class MarketDbTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The comparables bucket of <see cref="TestData.Product"/>'s defaults.</summary>
    private static BaselineKey Bucket(
        int itemId = 10100000, int level = 0, int optionCount = 1) =>
        new(itemId, level, optionCount);

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
        var baseline = db.GetPriceBaselines("heimdall").Baselines[Bucket()];
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

        var baselines = db.GetPriceBaselines("heimdall").Baselines;

        var level0 = baselines[Bucket()];
        Assert.Equal(3, level0.Samples);
        Assert.Equal(200d, level0.MedianPrice);
        Assert.Equal(0.2d, level0.MedianPricePerCp);

        Assert.Equal(900d, baselines[Bucket(level: 3)].MedianPrice);
    }

    [Fact]
    public void GetPriceBaselines_keeps_a_separate_bucket_per_option_count()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        // Stesso item e stesso livello: senza l'option_count nella chiave le due
        // popolazioni si mescolerebbero in un'unica mediana da 550 NCG.
        TestData.AddCompleteSnapshot(db, Now, new[]
        {
            TestData.Product(optionCount: 1, price: 100m, combatPoint: 1000),
            TestData.Product(optionCount: 1, price: 100m, combatPoint: 1000),
            TestData.Product(optionCount: 4, price: 1000m, combatPoint: 1000),
            TestData.Product(optionCount: 4, price: 1000m, combatPoint: 1000),
        });

        var baselines = db.GetPriceBaselines("heimdall").Baselines;

        Assert.Equal(2, baselines.Count);
        Assert.Equal(100d, baselines[Bucket(optionCount: 1)].MedianPrice);
        Assert.Equal(1000d, baselines[Bucket(optionCount: 4)].MedianPrice);
        Assert.Equal(2, baselines[Bucket(optionCount: 4)].Samples);
    }

    [Fact]
    public void GetPriceBaselines_gathers_a_bucket_scattered_through_the_listing()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        // Il listino arriva nell'ordine del market service, quindi i pezzi comparabili
        // fra loro sono sparsi: qui vengono alternati di proposito. Il bucketing lascia
        // il raggruppamento alla query, che le ordina per chiave; senza quell'ordine
        // ogni bucket si spezzerebbe in frammenti e nel risultato resterebbe solo
        // l'ultimo, con una mediana calcolata su un campione al posto di tre.
        TestData.AddCompleteSnapshot(db, Now, new[]
        {
            TestData.Product(price: 100m, combatPoint: 1000),
            TestData.Product(level: 5, price: 900m, combatPoint: 1000),
            TestData.Product(price: 200m, combatPoint: 1000),
            TestData.Product(level: 5, price: 700m, combatPoint: 1000),
            TestData.Product(price: 300m, combatPoint: 1000),
            TestData.Product(level: 5, price: 800m, combatPoint: 1000),
        });

        var baselines = db.GetPriceBaselines("heimdall").Baselines;

        Assert.Equal(2, baselines.Count);
        Assert.Equal(3, baselines[Bucket()].Samples);
        Assert.Equal(200d, baselines[Bucket()].MedianPrice);
        Assert.Equal(3, baselines[Bucket(level: 5)].Samples);
        Assert.Equal(800d, baselines[Bucket(level: 5)].MedianPrice);
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

        var baseline = db.GetPriceBaselines("heimdall").Baselines[Bucket()];
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

        var all = db.GetPriceBaselines("heimdall").Baselines;
        Assert.Equal(2, all[Bucket()].Samples);

        var recent = db.GetPriceBaselines("heimdall", sinceUtc: Now.AddDays(-7)).Baselines;
        Assert.Equal(1, recent[Bucket()].Samples);
        Assert.Equal(100d, recent[Bucket()].MedianPrice);
    }

    [Fact]
    public void A_listing_gone_from_a_later_snapshot_is_measured_as_a_sale()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        // Richieste di 100, 200 e 900 NCG: la mediana richiesta è 200.
        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[]
        {
            TestData.Product(price: 100m),
            TestData.Product(price: 200m),
            TestData.Product(price: 900m),
        });

        // Cattura completa successiva con il mercato vuoto: tutte e tre sono sparite.
        TestData.AddCompleteSnapshot(db, Now, Array.Empty<ItemProduct>());

        var sold = db.GetPriceBaselines("heimdall", population: BaselinePopulation.Sold);

        // La 900 chiedeva più del +20% sulla mediana: è un ritiro, non una vendita.
        Assert.Equal(new ListingOutcomes(3, 0, 2, 1), sold.Outcomes);
        Assert.Equal(2, sold.Baselines[Bucket()].Samples);
        Assert.Equal(150d, sold.Baselines[Bucket()].MedianPrice);

        // Sulla popolazione dei prezzi richiesti il riferimento resta più alto: è
        // esattamente la differenza che questa distinzione esiste per misurare.
        var listed = db.GetPriceBaselines("heimdall");
        Assert.Equal(sold.Outcomes, listed.Outcomes);
        Assert.Equal(3, listed.Baselines[Bucket()].Samples);
        Assert.Equal(200d, listed.Baselines[Bucket()].MedianPrice);
    }

    [Fact]
    public void A_listing_still_on_sale_is_not_a_sale()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var productId = Guid.NewGuid();
        TestData.AddCompleteSnapshot(
            db, Now.AddHours(-6), new[] { TestData.Product(productId: productId) });
        TestData.AddCompleteSnapshot(
            db, Now, new[] { TestData.Product(productId: productId) });

        var sold = db.GetPriceBaselines("heimdall", population: BaselinePopulation.Sold);

        Assert.Empty(sold.Baselines);
        Assert.Equal(new ListingOutcomes(1, 1, 0, 0), sold.Outcomes);
    }

    [Fact]
    public void The_sale_margin_decides_where_a_disappearance_stops_being_a_sale()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        // Mediana richiesta 200: la terza inserzione sta il 10% sopra.
        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[]
        {
            TestData.Product(price: 100m),
            TestData.Product(price: 200m),
            TestData.Product(price: 220m),
        });
        TestData.AddCompleteSnapshot(db, Now, Array.Empty<ItemProduct>());

        var strict = db.GetPriceBaselines(
            "heimdall", population: BaselinePopulation.Sold, saleMarginPercent: 0);
        Assert.Equal(new ListingOutcomes(3, 0, 2, 1), strict.Outcomes);

        var lenient = db.GetPriceBaselines(
            "heimdall", population: BaselinePopulation.Sold, saleMarginPercent: 20);
        Assert.Equal(new ListingOutcomes(3, 0, 3, 0), lenient.Outcomes);
    }

    [Fact]
    public void An_interrupted_snapshot_is_not_proof_that_a_listing_is_gone()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[] { TestData.Product() });

        // Una cattura interrotta non ha mai visto l'intero listino: l'assenza
        // dell'inserzione è un artefatto della cattura, non una sparizione.
        TestData.AddSnapshot(db, Now, Array.Empty<ItemProduct>());

        var sold = db.GetPriceBaselines("heimdall", population: BaselinePopulation.Sold);

        Assert.Empty(sold.Baselines);
        Assert.Equal(1, sold.Outcomes.Open);
    }

    [Fact]
    public void A_snapshot_of_another_type_is_not_proof_that_a_listing_is_gone()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[] { TestData.Product() });
        TestData.AddCompleteSnapshot(
            db, Now, Array.Empty<ItemProduct>(), types: new[] { EquipmentType.Weapon });

        var sold = db.GetPriceBaselines("heimdall", population: BaselinePopulation.Sold);

        Assert.Empty(sold.Baselines);
        Assert.Equal(1, sold.Outcomes.Open);
    }

    [Fact]
    public void A_truncated_snapshot_is_not_proof_that_a_listing_is_gone()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[] { TestData.Product() });

        // --max-per-type: la cattura si è fermata prima della fine del listino, quindi
        // non sappiamo se l'inserzione mancante fosse ancora in vendita.
        TestData.AddCompleteSnapshot(db, Now, Array.Empty<ItemProduct>(), maxPerType: 10);

        var sold = db.GetPriceBaselines("heimdall", population: BaselinePopulation.Sold);

        Assert.Empty(sold.Baselines);
        Assert.Equal(1, sold.Outcomes.Open);
    }

    [Fact]
    public void A_snapshot_of_another_planet_is_not_proof_that_a_listing_is_gone()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[] { TestData.Product() });
        TestData.AddCompleteSnapshot(db, Now, Array.Empty<ItemProduct>(), planet: "odin");

        var sold = db.GetPriceBaselines("heimdall", population: BaselinePopulation.Sold);

        Assert.Empty(sold.Baselines);
        Assert.Equal(1, sold.Outcomes.Open);
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
