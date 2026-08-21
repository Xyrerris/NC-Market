using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class DealMessageTests
{
    private static DealQuery Query() => new()
    {
        Planet = Planet.Heimdall,
        MinDiscountPercent = 25,
        MinSamples = 5,
    };

    /// <summary>
    /// A listing at <paramref name="price"/> against a bucket whose median is 100 NCG for
    /// 1.000 CP. With a combat point the comparison is on NCG/CP, without one it falls
    /// back to the plain price, which is the distinction the message has to carry.
    /// </summary>
    private static Deal Deal(decimal price = 50m, int combatPoint = 1000)
    {
        var product = TestData.Product(price: price, combatPoint: combatPoint);
        var priceDiscount = (1 - (double)price / 100) * 100;
        var usesCp = combatPoint > 0;
        var pricePerCp = usesCp ? (double)price / combatPoint : (double?)null;
        var baseline = new PriceBaseline(
            BaselineKey.Of(product), Samples: 12, MedianPrice: 100,
            CpSamples: usesCp ? 12 : 0, MedianPricePerCp: usesCp ? 0.1 : null);

        var discount = usesCp ? (1 - pricePerCp!.Value / 0.1) * 100 : priceDiscount;
        return new Deal(product, baseline, pricePerCp, discount, priceDiscount, usesCp);
    }

    [Fact]
    public void The_message_says_what_is_cheap_how_much_and_against_what()
    {
        var text = DealMessage.Format(new[] { Deal() }, Query(), NameProvider.Empty, 10);

        Assert.Contains("heimdall", text, StringComparison.Ordinal);
        Assert.Contains("50.00 NCG", text, StringComparison.Ordinal);
        Assert.Contains("50.0%", text, StringComparison.Ordinal);
        Assert.Contains("20 CP/NCG contro una mediana di 10", text, StringComparison.Ordinal);

        // Uno sconto senza denominatore è un numero: la stessa inserzione è un affare
        // contro i prezzi richiesti e niente contro quelli di vendita.
        Assert.Contains("inserzioni concluse", text, StringComparison.Ordinal);
        Assert.Contains("campioni ≥ 5", text, StringComparison.Ordinal);
        Assert.Contains("12 inserzioni", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listing_without_a_comparable_combat_point_says_so()
    {
        var text = DealMessage.Format(
            new[] { Deal(combatPoint: 0) }, Query(), NameProvider.Empty, 10);

        Assert.Contains("nessun CP confrontabile", text, StringComparison.Ordinal);
        Assert.Contains("Mediana 100.00 NCG", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CP/NCG", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Deals_beyond_the_maximum_are_counted_rather_than_listed()
    {
        var deals = new[] { Deal(price: 40m), Deal(price: 45m), Deal(price: 50m) };

        var text = DealMessage.Format(deals, Query(), NameProvider.Empty, max: 1);

        Assert.Contains("1) ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2) ", text, StringComparison.Ordinal);
        Assert.Contains("Altre 2", text, StringComparison.Ordinal);
        Assert.Contains("3 nuove occasioni", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_filters_in_force_are_part_of_the_message()
    {
        var query = Query() with
        {
            Type = EquipmentType.Ring,
            Grades = new HashSet<int> { 5, 6 },
            SinceUtc = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc),
            Population = BaselinePopulation.Listed,
        };

        var text = DealMessage.Format(new[] { Deal() }, query, NameProvider.Empty, 10);

        Assert.Contains("(Ring, rarità Legendary,Divinity)", text, StringComparison.Ordinal);
        Assert.Contains("storico dal 2026-08-14", text, StringComparison.Ordinal);
        Assert.Contains("prezzi richiesti", text, StringComparison.Ordinal);
    }
}
