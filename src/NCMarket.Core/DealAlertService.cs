namespace NCMarket.Core;

/// <summary>
/// What one alert run did: how many deals the search found, which of them had never been
/// announced, and whether a message actually left. <see cref="Sent"/> is false whenever
/// <see cref="New"/> is empty — a job with nothing new to say says nothing.
/// </summary>
public sealed record AlertReport(int Found, IReadOnlyList<Deal> New, bool Sent);

/// <summary>
/// Announces the deals worth announcing, once each.
/// <para>
/// The reason this class exists is that a scheduled search is not a person looking: it
/// runs every few hours and finds the same bargain every time until somebody buys it. A
/// notification repeated eight times a day is a notification nobody reads, so an alert
/// carries only the listings never announced before — and the market's own identity of a
/// listing, <c>product_id</c>, is exactly the right key for that: it never changes while
/// an offer stands, and a re-listing at a different price is a different product, which
/// is a different offer and deserves to be announced again.
/// </para>
/// <para>
/// Orchestration only: nothing here writes to a console, and nothing here knows where the
/// message goes.
/// </para>
/// </summary>
public sealed class DealAlertService
{
    private readonly MarketDb _db;
    private readonly INotificationChannel _channel;

    /// <param name="db">Where the listings already announced are remembered.</param>
    /// <param name="channel">Where the message goes.</param>
    public DealAlertService(MarketDb db, INotificationChannel channel)
    {
        _db = db;
        _channel = channel;
    }

    /// <summary>
    /// Sends the deals of <paramref name="report"/> that have never been announced, at
    /// most <paramref name="max"/> of them spelled out in the message, and records them.
    /// A report that could not compare anything, or found nothing, sends nothing: the
    /// three <see cref="DealStatus"/> failures are for whoever ran the search to act on,
    /// not for a chat notification.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The channel refused the message. Nothing is recorded in that case, so the next run
    /// tries the same deals again.
    /// </exception>
    public async Task<AlertReport> AnnounceAsync(
        DealReport report,
        DealQuery query,
        NameProvider names,
        int max,
        CancellationToken ct = default)
    {
        if (report.Status != DealStatus.Ok || report.Deals.Count == 0)
        {
            return new AlertReport(report.Deals.Count, Array.Empty<Deal>(), false);
        }

        var announced = _db.GetAnnouncedProducts(report.Deals.Select(d => d.Product.ProductId));
        var fresh = report.Deals
            .Where(d => !announced.Contains(d.Product.ProductId))
            .ToList();

        if (fresh.Count == 0)
        {
            return new AlertReport(report.Deals.Count, fresh, false);
        }

        await _channel.SendAsync(DealMessage.Format(fresh, query, names, max), ct);

        // Recorded only now, and never before the message left: a listing marked as
        // announced by a send that then failed would never be announced again, and a
        // silence is not something the next run can notice. The opposite failure — a
        // message that went out and was not recorded — costs one duplicate alert, which
        // is the cheaper of the two.
        _db.RecordAnnounced(fresh.Select(d => d.Product.ProductId), DateTime.UtcNow);
        return new AlertReport(report.Deals.Count, fresh, true);
    }
}
