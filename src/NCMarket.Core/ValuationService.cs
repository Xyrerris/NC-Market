namespace NCMarket.Core;

/// <summary>
/// Answers "what is this piece worth?" from the stored history: it turns a
/// <see cref="ValuationKey"/> into a bucket of comparables and reduces it to a range,
/// widening the bucket a step at a time while there are too few of them.
/// <para>
/// Two rules decide everything this class does, and they pull in opposite directions.
/// The bucket must be narrow enough that its members really are the same piece — which
/// is why the ladder climbs one field at a time and reports where it stopped — and it
/// must hold enough listings to mean anything, which is why it climbs at all. When both
/// cannot be had, the answer is that the data is not enough: a range built on two
/// listings reads exactly like a range built on fifty.
/// </para>
/// <para>
/// Nothing here writes to a console, and nothing here knows about Telegram.
/// </para>
/// </summary>
public sealed class ValuationService
{
    private readonly MarketDb _db;

    public ValuationService(MarketDb db) => _db = db;

    /// <summary>
    /// Values the piece described by <paramref name="query"/>. Climbs
    /// <see cref="ValuationSteps.Ladder"/> outwards from
    /// <see cref="ValuationQuery.StartStep"/> — the exact bucket unless a wider one was
    /// asked for — and returns the first step that holds at least
    /// <see cref="ValuationQuery.MinSamples"/> comparables, or a
    /// <see cref="ValuationStatus.InsufficientData"/> result carrying what the widest step
    /// did find.
    /// <para>
    /// At every step the preferred population is tried first and the fallback to
    /// <see cref="BaselinePopulation.Listed"/> happens <em>before</em> widening, not after:
    /// asking prices for the right piece say more than concluded sales of a piece that is
    /// only roughly the same one — and with a database of two snapshots no listing has
    /// been observed to conclude yet, so the opposite order would answer every question
    /// from the widest bucket on the ladder.
    /// </para>
    /// </summary>
    public ValuationResult Evaluate(ValuationQuery query)
    {
        ValuationResult? widest = null;

        foreach (var step in ValuationSteps.Ladder)
        {
            // A ladder climbed from higher up is still the same ladder: the rungs below
            // the one asked for are skipped rather than removed, so a widening that was
            // chosen and one that was forced produce the very same result.
            if (step < query.StartStep)
            {
                continue;
            }

            var set = _db.GetComparables(
                query.Planet.Name, FilterFor(query.Key, step), query.SinceUtc,
                query.SaleMarginPercent);

            foreach (var population in PopulationsFor(query.Population))
            {
                var kept = Keep(set.Listings, population);
                var candidate = Measure(
                    query, step, population, population != query.Population, set.Outcomes, kept);

                if (kept.Count >= query.MinSamples)
                {
                    return candidate;
                }

                // The ladder only widens, so the last candidate computed is also the one
                // built on the most comparables: it is what the "not enough data" answer
                // reports, and it is the difference between "nothing at all" and "nearly
                // there".
                widest = candidate;
            }
        }

        return (widest ?? Measure(
                query, ValuationStep.TypeAndGrade, query.Population, false,
                new ListingOutcomes(0, 0, 0, 0), Array.Empty<ComparableListing>()))
            with
            {
                Status = ValuationStatus.InsufficientData,
                Prices = null,
            };
    }

    /// <summary>
    /// The bucket of a step. Each one drops a field of the key rather than replacing it,
    /// so every step contains the one before it — that is what makes the ladder a ladder,
    /// and what lets the step alone describe what was given up.
    /// </summary>
    internal static ComparableFilter FilterFor(ValuationKey key, ValuationStep step) => step switch
    {
        ValuationStep.Exact => new ComparableFilter
        {
            Type = key.Type,
            Grade = key.Grade,
            Element = key.Element,
            Level = key.Level,
            OptionStats = key.OptionStats,
            HasSkill = key.HasSkill,
            ByCustomCraft = key.ByCustomCraft,
        },
        ValuationStep.AnyElement => new ComparableFilter
        {
            Type = key.Type,
            Grade = key.Grade,
            Level = key.Level,
            OptionStats = key.OptionStats,
            HasSkill = key.HasSkill,
            ByCustomCraft = key.ByCustomCraft,
        },
        ValuationStep.AnyLevel => new ComparableFilter
        {
            Type = key.Type,
            Grade = key.Grade,
            OptionStats = key.OptionStats,
            HasSkill = key.HasSkill,
            ByCustomCraft = key.ByCustomCraft,
        },
        // The set of option stats gives way to their number: a piece with ATK and DEF
        // starts comparing against one with ATK and HIT, which is a different piece with
        // the same amount of rolls behind it.
        ValuationStep.SameOptionCount => new ComparableFilter
        {
            Type = key.Type,
            Grade = key.Grade,
            OptionStatCount = key.OptionStats.Count,
            HasSkill = key.HasSkill,
            ByCustomCraft = key.ByCustomCraft,
        },
        // Everything a piece of this type and rarity can be, minus the skill, which
        // splits the population too plainly to merge even here.
        _ => new ComparableFilter
        {
            Type = key.Type,
            Grade = key.Grade,
            HasSkill = key.HasSkill,
        },
    };

    /// <summary>
    /// The population asked for, then the one to fall back on. Asking for
    /// <see cref="BaselinePopulation.Listed"/> has nothing to fall back to: it is already
    /// every listing observed.
    /// </summary>
    private static BaselinePopulation[] PopulationsFor(BaselinePopulation wanted) =>
        wanted == BaselinePopulation.Sold
            ? new[] { BaselinePopulation.Sold, BaselinePopulation.Listed }
            : new[] { BaselinePopulation.Listed };

    private static IReadOnlyList<ComparableListing> Keep(
        IReadOnlyList<ComparableListing> listings, BaselinePopulation population) =>
        population == BaselinePopulation.Sold
            ? listings.Where(l => l.Outcome == ListingOutcome.LikelySold).ToList()
            : listings;

    /// <summary>Reduces a bucket to the answer, without judging whether it is big enough.</summary>
    private static ValuationResult Measure(
        ValuationQuery query,
        ValuationStep step,
        BaselinePopulation population,
        bool fallback,
        ListingOutcomes outcomes,
        IReadOnlyList<ComparableListing> listings)
    {
        var prices = listings.Select(l => l.Price).ToList();
        prices.Sort();

        return new ValuationResult(
            ValuationStatus.Ok,
            query.Key,
            step,
            listings.Count,
            population,
            fallback,
            outcomes,
            prices.Count == 0
                ? null
                : new PriceRange(
                    prices[0],
                    Quantile(prices, 0.25),
                    Quantile(prices, 0.5),
                    Quantile(prices, 0.75),
                    prices[^1]),
            listings.Count == 0 ? null : listings.Min(l => l.LastSeenAtUtc),
            listings.Count == 0 ? null : listings.Max(l => l.LastSeenAtUtc),
            Percentile(listings, query.CombatPoint),
            listings);
    }

    /// <summary>
    /// Quantile of a sorted list, interpolated between the two neighbouring ranks. At
    /// <c>q = 0.5</c> this is the median <see cref="MarketDb"/> computes for its
    /// baselines, which is the point: the two numbers can end up side by side in one
    /// answer.
    /// </summary>
    private static double Quantile(List<double> sorted, double q)
    {
        var position = q * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    /// <summary>
    /// Where the piece sits among its comparables on combat point: the share of them it
    /// beats, in percent. Comparables without a combat point are left out rather than
    /// counted as zero, which would flatter every piece that has one. Null when the CP
    /// was not given, or when no comparable carries one — the honest answer there being
    /// that the group cannot be ranked, not that the piece is at the bottom of it.
    /// </summary>
    private static int? Percentile(IReadOnlyList<ComparableListing> listings, int? combatPoint)
    {
        if (combatPoint is not > 0)
        {
            return null;
        }

        var ranked = listings.Where(l => l.CombatPoint > 0).ToList();
        if (ranked.Count == 0)
        {
            return null;
        }

        var below = ranked.Count(l => l.CombatPoint < combatPoint.Value);
        return (int)Math.Round(below * 100.0 / ranked.Count, MidpointRounding.AwayFromZero);
    }
}
