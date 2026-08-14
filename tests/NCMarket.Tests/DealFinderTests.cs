using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class DealFinderTests
{
    private static Dictionary<(int ItemId, int Level), PriceBaseline> Baselines(
        params PriceBaseline[] baselines) =>
        baselines.ToDictionary(b => (b.ItemId, b.Level));

    [Fact]
    public void A_listing_without_a_comparable_baseline_is_skipped()
    {
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(itemId: 10100000, level: 0, price: 1m) },
            Baselines(new PriceBaseline(10100000, 3, 10, 1000, 10, 1)),
            minDiscountPercent: 10,
            minSamples: 5);

        Assert.Empty(deals);
    }

    [Fact]
    public void A_baseline_with_too_few_samples_is_skipped()
    {
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(price: 1m) },
            Baselines(new PriceBaseline(10100000, 0, 4, 1000, 4, 1)),
            minDiscountPercent: 10,
            minSamples: 5);

        Assert.Empty(deals);
    }

    [Fact]
    public void The_discount_is_measured_on_price_per_cp()
    {
        // Mediana 1 NCG per CP; l'inserzione ne chiede 0,6: 40% di sconto sulla metrica CP,
        // mentre sul solo prezzo lo sconto sarebbe l'88%.
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(price: 60m, combatPoint: 100) },
            Baselines(new PriceBaseline(10100000, 0, 10, 500, 10, 1)),
            minDiscountPercent: 30,
            minSamples: 5);

        var deal = Assert.Single(deals);
        Assert.True(deal.UsedCpMetric);
        Assert.Equal(40d, deal.DiscountPercent, precision: 6);
        Assert.Equal(88d, deal.PriceDiscountPercent, precision: 6);
    }

    [Fact]
    public void Without_a_usable_cp_the_plain_price_discount_is_used()
    {
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(price: 60m, combatPoint: 0) },
            Baselines(new PriceBaseline(10100000, 0, 10, 100, 0, null)),
            minDiscountPercent: 30,
            minSamples: 5);

        var deal = Assert.Single(deals);
        Assert.False(deal.UsedCpMetric);
        Assert.Equal(40d, deal.DiscountPercent, precision: 6);
    }

    [Fact]
    public void A_discount_below_the_threshold_is_not_a_deal()
    {
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(price: 95m, combatPoint: 100) },
            Baselines(new PriceBaseline(10100000, 0, 10, 100, 10, 1)),
            minDiscountPercent: 10,
            minSamples: 5);

        Assert.Empty(deals);
    }

    [Fact]
    public void Deals_come_out_by_descending_discount_then_ascending_price()
    {
        var cheap = TestData.Product(price: 20m, combatPoint: 100);
        var mid = TestData.Product(price: 50m, combatPoint: 100);
        var tiedButPricier = TestData.Product(price: 40m, combatPoint: 200);

        var deals = DealFinder.FindDeals(
            new[] { mid, tiedButPricier, cheap },
            Baselines(new PriceBaseline(10100000, 0, 10, 100, 10, 1)),
            minDiscountPercent: 10,
            minSamples: 5);

        // 80% (20 NCG), poi 80% (40 NCG, stesso rapporto ma più caro), poi 50%.
        Assert.Equal(
            new[] { cheap.ProductId, tiedButPricier.ProductId, mid.ProductId },
            deals.Select(d => d.Product.ProductId));
    }
}
