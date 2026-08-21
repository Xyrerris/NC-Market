using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>Whether a deal search could compare anything, and if not why.</summary>
public enum DealStatus
{
    /// <summary>The comparison ran. It may still have found no listing cheap enough.</summary>
    Ok,

    /// <summary>
    /// Nothing is stored for the planet: there is no history to compare against.
    /// </summary>
    NoHistory,

    /// <summary>
    /// History exists, but no listing has concluded yet, so a
    /// <see cref="BaselinePopulation.Sold"/> baseline has nothing to measure. Observing a
    /// disappearance takes a second complete, untruncated snapshot of the same type.
    /// </summary>
    NoSales,

    /// <summary>
    /// The current listings were to be read back from the latest stored snapshot and the
    /// planet has no complete one.
    /// </summary>
    NoSnapshot,
}

/// <summary>
/// What to compare, and against what. Defaults are deliberately permissive — no discount
/// threshold, one sample, the whole history — so a caller that omits a field is not
/// silently filtered; the thresholds worth defending belong to whoever exposes them.
/// </summary>
public sealed record DealQuery
{
    /// <summary>Planet whose history and listings are compared.</summary>
    public required Planet Planet { get; init; }

    /// <summary>Equipment type to restrict to; null compares every type.</summary>
    public EquipmentType? Type { get; init; }

    /// <summary>Grades to keep among the current listings; null keeps every grade.</summary>
    public IReadOnlySet<int>? Grades { get; init; }

    /// <summary>Minimum discount versus the baseline for a listing to count as a deal.</summary>
    public double MinDiscountPercent { get; init; }

    /// <summary>Historical listings a bucket needs before it is used as a reference.</summary>
    public int MinSamples { get; init; } = 1;

    /// <summary>
    /// Keep only the listings still on sale after this instant; null uses the whole history.
    /// </summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>Population the medians are computed on.</summary>
    public BaselinePopulation Population { get; init; } = BaselinePopulation.Sold;

    /// <summary>Tolerance of the sale heuristic (see <see cref="MarketDb.GetPriceBaselines"/>).</summary>
    public double SaleMarginPercent { get; init; } = MarketDb.DefaultSaleMarginPercent;

    /// <summary>
    /// Compare the latest stored snapshot instead of the live market. Reading the
    /// database back answers in milliseconds and needs no network, at the price of
    /// judging listings that may already be gone.
    /// </summary>
    public bool FromSnapshot { get; init; }

    /// <summary>Cap on the listings downloaded per type when comparing the live market.</summary>
    public int? MaxPerType { get; init; }
}

/// <summary>
/// Outcome of a deal search: the deals, the baselines they were measured against — with
/// the population split that says how much those baselines can be trusted — and, when the
/// comparison was made against a stored snapshot, which one.
/// </summary>
public sealed record DealReport(
    DealStatus Status,
    BaselineSet Baselines,
    long? SnapshotId,
    IReadOnlyList<Deal> Deals);

/// <summary>
/// Answers "what is cheap right now": computes the historical baselines, obtains the
/// listings to judge — from the live market or from the latest stored snapshot — and hands
/// both to <see cref="DealFinder"/>. The three ways the question cannot be answered (no
/// history, no concluded listing yet, no complete snapshot) come back as a
/// <see cref="DealStatus"/> rather than as an empty result, because they call for
/// different answers and none of them means "nothing is cheap".
/// <para>
/// Orchestration only: nothing here writes to a console.
/// </para>
/// </summary>
public sealed class DealService
{
    private readonly MarketDb _db;
    private readonly IMarketListingSource? _market;

    /// <param name="db">History the baselines are computed from.</param>
    /// <param name="market">
    /// Source of the listings to judge. Needed only for queries that compare the live
    /// market: with <see cref="DealQuery.FromSnapshot"/> the listings come from
    /// <paramref name="db"/> and this may be null.
    /// </param>
    public DealService(MarketDb db, IMarketListingSource? market = null)
    {
        _db = db;
        _market = market;
    }

    /// <summary>
    /// Runs the comparison. <paramref name="progress"/> is only ever called while
    /// downloading the live market.
    /// </summary>
    public async Task<DealReport> FindAsync(
        DealQuery query,
        ICaptureProgress? progress = null,
        CancellationToken ct = default)
    {
        var baselines = _db.GetPriceBaselines(
            query.Planet.Name, query.Type, query.SinceUtc, query.Population,
            query.SaleMarginPercent);

        if (baselines.Outcomes.Total == 0)
        {
            return Nothing(DealStatus.NoHistory, baselines);
        }

        if (baselines.Baselines.Count == 0)
        {
            return Nothing(DealStatus.NoSales, baselines);
        }

        long? snapshotId = null;
        IReadOnlyList<ItemProduct> current;
        if (query.FromSnapshot)
        {
            snapshotId = _db.GetLatestSnapshotId(query.Planet.Name);
            if (snapshotId is null)
            {
                return Nothing(DealStatus.NoSnapshot, baselines);
            }

            current = _db.GetSnapshotProducts(snapshotId.Value, query.Type);
        }
        else
        {
            current = await DownloadAsync(query, progress, ct);
        }

        if (query.Grades is not null)
        {
            current = current.Where(p => query.Grades.Contains(p.Grade)).ToList();
        }

        var deals = DealFinder.FindDeals(
            current, baselines.Baselines, query.MinDiscountPercent, query.MinSamples);
        return new DealReport(DealStatus.Ok, baselines, snapshotId, deals);
    }

    private async Task<IReadOnlyList<ItemProduct>> DownloadAsync(
        DealQuery query, ICaptureProgress? progress, CancellationToken ct)
    {
        if (_market is null)
        {
            throw new InvalidOperationException(
                "Confrontare il mercato live richiede una sorgente di inserzioni: passa un " +
                "IMarketListingSource a DealService, oppure usa DealQuery.FromSnapshot.");
        }

        if (_market.Planet != query.Planet)
        {
            // Comparing one planet's listings against another's medians would produce a
            // full table of plausible-looking nonsense.
            throw new InvalidOperationException(
                $"La sorgente di inserzioni è del pianeta {_market.Planet.Name}, ma la " +
                $"ricerca riguarda {query.Planet.Name}.");
        }

        var types = query.Type is null ? EquipmentTypes.All : new[] { query.Type.Value };
        var current = new List<ItemProduct>();
        foreach (var type in types)
        {
            progress?.TypeStarted(type);
            var products = await _market.GetAllProductsAsync(
                type,
                query.MaxPerType,
                (fetched, announced) => progress?.TypeProgress(type, fetched, announced),
                ct);

            current.AddRange(products);
            progress?.TypeCompleted(type, products.Count);
        }

        return current;
    }

    private static DealReport Nothing(DealStatus status, BaselineSet baselines) =>
        new(status, baselines, null, Array.Empty<Deal>());
}
