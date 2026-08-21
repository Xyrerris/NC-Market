using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

public sealed class DealAlertServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private static DealQuery Query() => new()
    {
        Planet = Planet.Heimdall,
        Type = EquipmentType.Ring,
        MinDiscountPercent = 25,
        MinSamples = 2,
        FromSnapshot = true,
    };

    /// <summary>
    /// The same history as <see cref="DealServiceTests"/>: two listings concluded at 100
    /// NCG for 1.000 CP — a sold median of 100 — and one at 900 that disappeared far
    /// above the going rate and counts as a withdrawal. <paramref name="onSale"/> is what
    /// the later snapshot still saw, and therefore what a search judges.
    /// </summary>
    private static void Seed(MarketDb db, params ItemProduct[] onSale)
    {
        TestData.AddCompleteSnapshot(db, Now.AddHours(-6), new[]
        {
            TestData.Product(price: 100m),
            TestData.Product(price: 100m),
            TestData.Product(price: 900m),
        });
        TestData.AddCompleteSnapshot(db, Now, onSale);
    }

    private static Task<DealReport> Search(MarketDb db) =>
        new DealService(db).FindAsync(Query());

    private static Task<AlertReport> Announce(
        MarketDb db, DealReport report, INotificationChannel channel, int max = 10) =>
        new DealAlertService(db, channel).AnnounceAsync(
            report, Query(), NameProvider.Empty, max);

    [Fact]
    public async Task An_offer_is_announced_once_and_then_never_again()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Seed(db, TestData.Product(price: 50m));

        var channel = new FakeChannel();
        var first = await Announce(db, await Search(db), channel);
        var second = await Announce(db, await Search(db), channel);

        // Il motivo per cui questa classe esiste: il job rigira ogni poche ore e ritrova
        // la stessa occasione finché qualcuno non la compra.
        Assert.True(first.Sent);
        Assert.Single(first.New);
        Assert.False(second.Sent);
        Assert.Empty(second.New);
        Assert.Equal(1, second.Found);
        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task A_send_that_failed_is_retried_by_the_next_run()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Seed(db, TestData.Product(price: 50m));

        var refused = await Search(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Announce(db, refused, new FakeChannel().Failing()));

        // Segnare come annunciata un'inserzione il cui messaggio non è partito la
        // renderebbe invisibile per sempre, e un silenzio non è osservabile.
        var channel = new FakeChannel();
        var retry = await Announce(db, await Search(db), channel);

        Assert.True(retry.Sent);
        Assert.Single(retry.New);
        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task Deals_past_the_ones_listed_are_announced_too()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Seed(db, TestData.Product(price: 50m), TestData.Product(price: 40m));

        var channel = new FakeChannel();
        var first = await Announce(db, await Search(db), channel, max: 1);
        var second = await Announce(db, await Search(db), channel, max: 1);

        // Il messaggio ne elenca una e dichiara l'altra: sono entrambe annunciate, o la
        // seconda tornerebbe a ogni esecuzione senza mai essere elencata.
        Assert.Equal(2, first.New.Count);
        Assert.Contains("Altre 1", Assert.Single(channel.Sent), StringComparison.Ordinal);
        Assert.False(second.Sent);
    }

    [Fact]
    public async Task A_search_that_could_not_compare_anything_says_nothing()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var channel = new FakeChannel();
        var report = await Search(db);
        var alert = await Announce(db, report, channel);

        // Storico assente, nessuna vendita, nessuno snapshot completo: sono cose da
        // risolvere sul server, non da mandare in chat.
        Assert.Equal(DealStatus.NoHistory, report.Status);
        Assert.False(alert.Sent);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task A_run_with_no_deal_at_all_sends_nothing()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Seed(db, TestData.Product(price: 100m));

        var channel = new FakeChannel();
        var report = await Search(db);
        var alert = await Announce(db, report, channel);

        Assert.Equal(DealStatus.Ok, report.Status);
        Assert.Equal(0, alert.Found);
        Assert.False(alert.Sent);
        Assert.Empty(channel.Sent);
    }
}
