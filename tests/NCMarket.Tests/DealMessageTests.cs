using System.Net;
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

        Assert.Contains("`heimdall`", text, StringComparison.Ordinal);
        Assert.Contains("`50.00 NCG`", text, StringComparison.Ordinal);
        Assert.Contains("`50.0%`", text, StringComparison.Ordinal);
        Assert.Contains("`20` CP/NCG vs mediana `10`", text, StringComparison.Ordinal);

        // Uno sconto senza denominatore è un numero: la stessa inserzione è un affare
        // contro i prezzi richiesti e niente contro quelli di vendita.
        Assert.Contains("inserzioni concluse", text, StringComparison.Ordinal);
        Assert.Contains("campioni ≥ 5", text, StringComparison.Ordinal);
        Assert.Contains("su `12` inserzioni", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il layout per intero. Le parti di una segnalazione si leggono insieme — indice e
    /// nome in grassetto, ogni cifra nel suo riquadro — e cambiarne una è cambiare il
    /// messaggio che qualcuno scorre in chat.
    /// </summary>
    [Fact]
    public void The_layout_is_the_message()
    {
        var text = DealMessage.Format(new[] { Deal() }, Query(), NameProvider.Empty, 10);

        Assert.Equal(
            "🏷️ *NC\\-Market* — 1 nuova occasione su `heimdall`\n" +
            "_Sconto ≥ 25% sulla mediana delle inserzioni concluse per item \\+ livello " +
            "\\+ opzioni \\(campioni ≥ 5\\)_\n" +
            "\n" +
            "*1\\. 10100000 \\+0*\n" +
            "Ring · grado 3 · 1 opzione · CP `1,000`\n" +
            "💰 `50.00 NCG` — sconto `50.0%` su NCG/CP \\(`50.0%` sul prezzo\\)\n" +
            "📊 `20` CP/NCG vs mediana `10` su `12` inserzioni\n",
            text);
    }

    [Fact]
    public void A_listing_without_a_comparable_combat_point_says_so()
    {
        var text = DealMessage.Format(
            new[] { Deal(combatPoint: 0) }, Query(), NameProvider.Empty, 10);

        Assert.Contains("nessun CP confrontabile", text, StringComparison.Ordinal);
        Assert.Contains("📊 mediana `100.00 NCG`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CP/NCG", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Deals_beyond_the_maximum_are_counted_rather_than_listed()
    {
        var deals = new[] { Deal(price: 40m), Deal(price: 45m), Deal(price: 50m) };

        var text = DealMessage.Format(deals, Query(), NameProvider.Empty, max: 1);

        Assert.Contains("*1\\. ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("*2\\. ", text, StringComparison.Ordinal);
        Assert.Contains("➕ Altre `2`", text, StringComparison.Ordinal);
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

        Assert.Contains(
            "🔎 Ring · rarità Legendary,Divinity · storico dal 2026\\-08\\-14",
            text, StringComparison.Ordinal);
        Assert.Contains("prezzi richiesti", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il nome di un item è quello che il gioco ha deciso di dargli, e finisce dentro un
    /// grassetto: non sfuggito, una parentesi non rende brutta la segnalazione, la fa
    /// rifiutare per intero.
    /// </summary>
    [Fact]
    public async Task An_item_name_with_markup_characters_is_escaped()
    {
        using var dir = new TempDir();
        var cache = dir.WriteFile(
            "item_name.csv", "ITEM_NAME_10100000,Ring [Fire] +1.0 (v2)\n");
        var handler = new FakeHttpHandler().Answering(HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        var names = await NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", cache, maxAge: TimeSpan.Zero, http: http);

        var text = DealMessage.Format(new[] { Deal() }, Query(), names, 10);

        Assert.Contains(
            @"*1\. Ring \[Fire\] \+1\.0 \(v2\) \+0*", text, StringComparison.Ordinal);
    }
}
