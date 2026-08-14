using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class DealFinderTests
{
    private static Dictionary<BaselineKey, PriceBaseline> Baselines(
        params PriceBaseline[] baselines) =>
        baselines.ToDictionary(b => b.Key);

    /// <summary>A baseline for the bucket of <see cref="TestData.Product"/>'s defaults.</summary>
    private static PriceBaseline Baseline(
        int samples, double medianPrice, int cpSamples, double? medianPricePerCp,
        int itemId = 10100000, int level = 0, int optionCount = 1) =>
        new(new BaselineKey(itemId, level, optionCount),
            samples, medianPrice, cpSamples, medianPricePerCp);

    [Fact]
    public void A_listing_without_a_comparable_baseline_is_skipped()
    {
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(itemId: 10100000, level: 0, price: 1m) },
            Baselines(Baseline(10, 1000, 10, 1, level: 3)),
            minDiscountPercent: 10,
            minSamples: 5);

        Assert.Empty(deals);
    }

    [Fact]
    public void A_listing_is_not_compared_with_a_different_option_count()
    {
        // Stesso item e stesso livello, ma il baseline è quello dei pezzi con 4 opzioni:
        // non sono comparabili, quindi l'inserzione resta senza riferimento e viene saltata.
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(optionCount: 1, price: 1m, combatPoint: 100) },
            Baselines(Baseline(10, 1000, 10, 1, optionCount: 4)),
            minDiscountPercent: 10,
            minSamples: 5);

        Assert.Empty(deals);
    }

    [Fact]
    public void Each_option_count_is_measured_against_its_own_baseline()
    {
        var oneOption = TestData.Product(optionCount: 1, price: 60m, combatPoint: 100);
        var fourOptions = TestData.Product(optionCount: 4, price: 60m, combatPoint: 100);

        // Il mercato paga 4 NCG per CP i pezzi con 4 opzioni e 1 NCG per CP quelli con una:
        // la stessa richiesta è un'occasione molto più grossa nel primo bucket.
        var deals = DealFinder.FindDeals(
            new[] { oneOption, fourOptions },
            Baselines(
                Baseline(10, 100, 10, 1, optionCount: 1),
                Baseline(10, 400, 10, 4, optionCount: 4)),
            minDiscountPercent: 10,
            minSamples: 5);

        Assert.Equal(
            new[] { fourOptions.ProductId, oneOption.ProductId },
            deals.Select(d => d.Product.ProductId));
        Assert.Equal(85d, deals[0].DiscountPercent, precision: 6);
        Assert.Equal(40d, deals[1].DiscountPercent, precision: 6);
    }

    [Fact]
    public void A_baseline_with_too_few_samples_is_skipped()
    {
        var deals = DealFinder.FindDeals(
            new[] { TestData.Product(price: 1m) },
            Baselines(Baseline(4, 1000, 4, 1)),
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
            Baselines(Baseline(10, 500, 10, 1)),
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
            Baselines(Baseline(10, 100, 0, null)),
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
            Baselines(Baseline(10, 100, 10, 1)),
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
            Baselines(Baseline(10, 100, 10, 1)),
            minDiscountPercent: 10,
            minSamples: 5);

        // 80% (20 NCG), poi 80% (40 NCG, stesso rapporto ma più caro), poi 50%.
        Assert.Equal(
            new[] { cheap.ProductId, tiedButPricier.ProductId, mid.ProductId },
            deals.Select(d => d.Product.ProductId));
    }
}
