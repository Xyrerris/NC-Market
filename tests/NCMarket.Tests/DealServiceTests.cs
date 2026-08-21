using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

public sealed class DealServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Two listings concluded at 100 NCG for 1.000 CP — a sold median of 100 NCG, that is
    /// 0,10 NCG/CP — plus one at 900 that disappeared far above the going rate and counts
    /// as a withdrawal. The disappearance is observable because a later complete snapshot
    /// of the same type did not see them; <paramref name="stillOnSale"/> is what that
    /// second snapshot did see, and is the listing a search then judges.
    /// </summary>
    private static void SeedConcludedListings(MarketDb db, params ItemProduct[] stillOnSale)
    {
        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[]
        {
            TestData.Product(price: 100m),
            TestData.Product(price: 100m),
            TestData.Product(price: 900m),
        });
        TestData.AddCompleteSnapshot(db, Now, stillOnSale);
    }

    private static DealQuery Query(bool fromSnapshot = false) => new()
    {
        Planet = Planet.Heimdall,
        Type = EquipmentType.Ring,
        MinDiscountPercent = 25,
        MinSamples = 2,
        FromSnapshot = fromSnapshot,
    };

    [Fact]
    public async Task Without_any_history_the_search_says_so_instead_of_answering()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var report = await new DealService(db).FindAsync(Query(fromSnapshot: true));

        Assert.Equal(DealStatus.NoHistory, report.Status);
        Assert.Empty(report.Deals);
    }

    [Fact]
    public async Task History_without_a_concluded_listing_is_not_a_baseline()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        // Un solo snapshot: nessuna sparizione è ancora osservabile, quindi la
        // popolazione 'sold' è vuota anche se lo storico non lo è.
        TestData.AddCompleteSnapshot(db, Now, new[] { TestData.Product(price: 100m) });

        var report = await new DealService(db).FindAsync(Query(fromSnapshot: true));

        Assert.Equal(DealStatus.NoSales, report.Status);
        Assert.Equal(1, report.Baselines.Outcomes.Total);
        Assert.Empty(report.Deals);
    }

    [Fact]
    public async Task Comparing_the_latest_snapshot_requires_a_complete_one()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        // Cattura interrotta: c'è storico da cui calcolare i prezzi richiesti, ma nessuno
        // snapshot completo da confrontare.
        TestData.AddSnapshot(db, Now, new[] { TestData.Product(price: 100m) });

        var query = Query(fromSnapshot: true) with { Population = BaselinePopulation.Listed };
        var report = await new DealService(db).FindAsync(query);

        Assert.Equal(DealStatus.NoSnapshot, report.Status);
        Assert.Null(report.SnapshotId);
    }

    [Fact]
    public async Task A_listing_below_the_concluded_median_is_a_deal()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var bargain = TestData.Product(price: 50m);
        SeedConcludedListings(db, bargain);

        var report = await new DealService(db).FindAsync(Query(fromSnapshot: true));

        Assert.Equal(DealStatus.Ok, report.Status);
        Assert.Equal(db.GetLatestSnapshotId("heimdall"), report.SnapshotId);
        Assert.Equal(new ListingOutcomes(4, 1, 2, 1), report.Baselines.Outcomes);

        var deal = Assert.Single(report.Deals);
        Assert.Equal(bargain.ProductId, deal.Product.ProductId);
        Assert.True(deal.UsedCpMetric);

        // 0,05 NCG/CP contro una mediana conclusa di 0,10.
        Assert.Equal(50d, deal.DiscountPercent, 1);
    }

    [Fact]
    public async Task The_live_market_is_asked_only_for_the_requested_type()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        SeedConcludedListings(db);
        var bargain = TestData.Product(price: 50m);
        var market = new FakeMarket().With(EquipmentType.Ring, bargain);
        var progress = new RecordingProgress();

        var report = await new DealService(db, market).FindAsync(
            Query() with { MaxPerType = 200 }, progress);

        Assert.Equal(new[] { EquipmentType.Ring }, market.Requested);
        Assert.Equal(200, market.LastMaxItems);
        Assert.Null(report.SnapshotId);
        Assert.Equal(bargain.ProductId, Assert.Single(report.Deals).Product.ProductId);

        Assert.Equal(new[] { EquipmentType.Ring }, progress.Started);
        Assert.Equal(new[] { (EquipmentType.Ring, 1) }, progress.Completed);
    }

    [Fact]
    public async Task Without_a_type_the_live_market_is_asked_for_all_of_them()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        SeedConcludedListings(db);
        var market = new FakeMarket();

        var report = await new DealService(db, market).FindAsync(Query() with { Type = null });

        Assert.Equal(EquipmentTypes.All, market.Requested);
        Assert.Equal(DealStatus.Ok, report.Status);
        Assert.Empty(report.Deals);
    }

    [Fact]
    public async Task The_grade_filter_applies_to_the_current_listings()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        SeedConcludedListings(db);
        var epic = TestData.Product(price: 50m, grade: 3);
        var mythic = TestData.Product(price: 50m, grade: 7);
        var market = new FakeMarket().With(EquipmentType.Ring, epic, mythic);

        var report = await new DealService(db, market).FindAsync(
            Query() with { Grades = new HashSet<int> { 7 } });

        Assert.Equal(mythic.ProductId, Assert.Single(report.Deals).Product.ProductId);
    }

    [Fact]
    public async Task A_live_search_without_a_market_is_refused()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        SeedConcludedListings(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DealService(db).FindAsync(Query()));
    }

    [Fact]
    public async Task A_market_of_another_planet_is_refused()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        SeedConcludedListings(db);

        // Confrontare le inserzioni di un pianeta con le mediane di un altro produrrebbe
        // una tabella di occasioni verosimili e prive di senso.
        var market = new FakeMarket(Planet.Odin).With(EquipmentType.Ring, TestData.Product());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DealService(db, market).FindAsync(Query()));
    }
}
