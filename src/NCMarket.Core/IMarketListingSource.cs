using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// The listings currently on sale on one planet. <see cref="MarketClient"/> is the
/// implementation that talks to the market service; the abstraction exists so the
/// orchestration services (<see cref="SnapshotService"/>, <see cref="DealService"/>) can
/// be driven — and tested — without the network.
/// </summary>
public interface IMarketListingSource
{
    /// <summary>Planet these listings come from.</summary>
    Planet Planet { get; }

    /// <summary>
    /// Every listing on sale for an equipment type, or the first
    /// <paramref name="maxItems"/> of them. <paramref name="progress"/> receives
    /// (listings fetched so far, total announced by the service — 0 when unknown).
    /// </summary>
    Task<IReadOnlyList<ItemProduct>> GetAllProductsAsync(
        EquipmentType type,
        int? maxItems = null,
        Action<int, int>? progress = null,
        CancellationToken ct = default);
}
