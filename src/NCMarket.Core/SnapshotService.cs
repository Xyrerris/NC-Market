namespace NCMarket.Core;

/// <summary>Progress of a capture that is being stored (see <see cref="SnapshotService"/>).</summary>
public interface ISnapshotProgress : ICaptureProgress
{
    /// <summary>
    /// The snapshot row exists from here on, empty and <see cref="SnapshotStatus.Partial"/>.
    /// The id is announced before the first download rather than only returned at the end,
    /// because from this moment a failure leaves that snapshot behind.
    /// </summary>
    void SnapshotCreated(long snapshotId, DateTime takenAtUtc);

    /// <summary>
    /// The capture failed. The snapshot keeps the types downloaded so far and stays
    /// partial, so nothing downstream picks it as the latest snapshot.
    /// </summary>
    void SnapshotInterrupted(long snapshotId);
}

/// <summary>What to capture. Defaults describe the whole listing of every equipment type.</summary>
public sealed record SnapshotRequest
{
    /// <summary>Equipment types to download.</summary>
    public IReadOnlyList<EquipmentType> Types { get; init; } = EquipmentTypes.All;

    /// <summary>
    /// Stop after this many listings per type. Such a capture is a sample rather than the
    /// listing, so it is recorded on the snapshot: <see cref="MarketDb.GetPriceBaselines"/>
    /// must not read the listings it never downloaded as listings that left the market.
    /// </summary>
    public int? MaxPerType { get; init; }
}

/// <summary>What one equipment type contributed to a snapshot.</summary>
public sealed record TypeCapture(EquipmentType Type, int Listings);

/// <summary>A completed capture: the snapshot it produced and its composition.</summary>
public sealed record SnapshotReport(
    long SnapshotId,
    string Planet,
    DateTime TakenAtUtc,
    int? MaxPerType,
    IReadOnlyList<TypeCapture> Types,
    int Listings);

/// <summary>
/// Stores the market listing: creates the snapshot, downloads one equipment type at a
/// time, saves each as it arrives and marks the snapshot complete only once every type is
/// in. Saving per type is what makes an interrupted capture worth keeping — the types
/// already downloaded survive — and marking it complete last is what keeps it out of
/// <see cref="MarketDb.GetLatestSnapshotId"/> until it really is a full listing.
/// <para>
/// Orchestration only: nothing here writes to a console, so a scheduled job or a web
/// request drives it exactly as the CLI does. Serialising captures against each other is
/// the host's job, not this class's — acquire a <see cref="DbLock"/> before opening the
/// database when more than one process can run.
/// </para>
/// </summary>
public sealed class SnapshotService
{
    private readonly MarketDb _db;
    private readonly IMarketListingSource _market;

    public SnapshotService(MarketDb db, IMarketListingSource market)
    {
        _db = db;
        _market = market;
    }

    /// <summary>
    /// Runs a capture and returns what it stored. On failure the snapshot row survives
    /// with the types downloaded so far and stays <see cref="SnapshotStatus.Partial"/>;
    /// the exception propagates unchanged, after
    /// <see cref="ISnapshotProgress.SnapshotInterrupted"/> has named the snapshot.
    /// </summary>
    /// <exception cref="ArgumentException">The request asks for no equipment type.</exception>
    public async Task<SnapshotReport> CaptureAsync(
        SnapshotRequest request,
        ISnapshotProgress? progress = null,
        CancellationToken ct = default)
    {
        var types = request.Types.Distinct().ToArray();
        if (types.Length == 0)
        {
            // A snapshot covering nothing would still be finalized as complete and would
            // then become the latest snapshot of its planet, hiding the real listing
            // behind an empty one.
            throw new ArgumentException(
                "Uno snapshot deve coprire almeno un tipo di equipaggiamento.", nameof(request));
        }

        var planet = _market.Planet.Name;
        var takenAtUtc = DateTime.UtcNow;
        var snapshotId = _db.CreateSnapshot(planet, types, takenAtUtc, request.MaxPerType);
        progress?.SnapshotCreated(snapshotId, takenAtUtc);

        var captured = new List<TypeCapture>(types.Length);
        var total = 0;
        try
        {
            foreach (var type in types)
            {
                progress?.TypeStarted(type);
                var products = await _market.GetAllProductsAsync(
                    type,
                    request.MaxPerType,
                    (fetched, announced) => progress?.TypeProgress(type, fetched, announced),
                    ct);

                var saved = _db.AddProducts(snapshotId, products);
                captured.Add(new TypeCapture(type, saved));
                total += saved;
                progress?.TypeCompleted(type, saved);
            }
        }
        catch
        {
            progress?.SnapshotInterrupted(snapshotId);
            throw;
        }

        _db.FinalizeSnapshot(snapshotId);
        return new SnapshotReport(
            snapshotId, planet, takenAtUtc, request.MaxPerType, captured, total);
    }
}
