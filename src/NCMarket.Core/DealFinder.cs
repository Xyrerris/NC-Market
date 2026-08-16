using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// A current listing priced below its historical baseline. The primary metric is the
/// discount on price-per-CP (NCG per combat point) versus the historical median of the
/// listing's own <see cref="BaselineKey"/> bucket; when the CP metric is not usable
/// (listing or baseline without a positive combat point) the plain price discount is
/// used instead, as signalled by <see cref="UsedCpMetric"/>.
/// </summary>
public sealed record Deal(
    ItemProduct Product,
    PriceBaseline Baseline,
    double? PricePerCp,
    double DiscountPercent,
    double PriceDiscountPercent,
    bool UsedCpMetric);

/// <summary>Detects listings that are cheap versus their historical medians.</summary>
public static class DealFinder
{
    /// <summary>
    /// Filters <paramref name="listings"/> down to the deals: listings whose primary
    /// discount versus the historical baseline of their own bucket (see
    /// <see cref="BaselineKey"/>) is at least <paramref name="minDiscountPercent"/>.
    /// Listings without a comparable baseline, or whose baseline has fewer than
    /// <paramref name="minSamples"/> distinct historical listings, are skipped. Results
    /// are sorted by discount (descending), then by price (ascending).
    /// </summary>
    public static IReadOnlyList<Deal> FindDeals(
        IEnumerable<ItemProduct> listings,
        IReadOnlyDictionary<BaselineKey, PriceBaseline> baselines,
        double minDiscountPercent,
        int minSamples)
    {
        var deals = new List<Deal>();
        foreach (var product in listings)
        {
            if (!baselines.TryGetValue(BaselineKey.Of(product), out var baseline))
            {
                continue;
            }

            if (baseline.Samples < minSamples || product.Price <= 0 || baseline.MedianPrice <= 0)
            {
                continue;
            }

            var priceDiscount = (1 - (double)product.Price / baseline.MedianPrice) * 100;

            double? pricePerCp = product.CombatPoint > 0
                ? (double)product.Price / product.CombatPoint
                : null;
            var cpUsable = pricePerCp is not null
                && baseline.MedianPricePerCp is > 0
                && baseline.CpSamples >= minSamples;
            var discount = cpUsable
                ? (1 - pricePerCp!.Value / baseline.MedianPricePerCp!.Value) * 100
                : priceDiscount;

            if (discount >= minDiscountPercent)
            {
                deals.Add(new Deal(product, baseline, pricePerCp, discount, priceDiscount, cpUsable));
            }
        }

        return deals
            .OrderByDescending(d => d.DiscountPercent)
            .ThenBy(d => d.Product.Price)
            .ToList();
    }
}
