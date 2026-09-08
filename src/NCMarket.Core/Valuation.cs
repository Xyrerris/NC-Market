using System.Collections.Immutable;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// Identity of a bucket of comparable listings as seen from <em>outside</em> the market
/// service: everything in it is something the owner of a piece can read off the item, so
/// a valuation can be asked for without knowing any database id.
/// <para>
/// The differences from <see cref="BaselineKey"/> are all consequences of that:
/// </para>
/// <list type="bullet">
/// <item>no item id — whoever writes to the bot does not know it. At grades 7 and 8 the
/// (type, grade, element) triple determines it; below, it narrows it down to a handful of
/// variants and the answer has to say so;</item>
/// <item>no option count — <see cref="ItemProduct.OptionCountFromCombination"/> is not
/// reconstructible from what is visible: 31% of the weapons measured carry more options
/// than they show, because two rolls landing on the same stat come back merged into one
/// row. What is visible is the <em>set of stat types</em> the options rolled, and that is
/// what this key carries;</item>
/// <item>grade is present, while <see cref="BaselineKey"/> leaves it out deliberately:
/// there it is a property of the item id and would partition nothing, here there is no
/// item id, so the grade goes back to being information;</item>
/// <item>custom craft is present because it is invisible in the triple: a custom-crafted
/// piece has an item id of its own (the <c>2016…</c>/<c>2046…</c> range) but shares
/// sub type, grade and element with the ordinary ones. 55 triples in the database hold
/// both populations, and without this field those buckets would average two different
/// items together.</item>
/// </list>
/// <para>
/// The <em>values</em> of the options are deliberately absent. They do not predict the
/// price inside a bucket (rank correlations from -0.42 to +0.03 on the most populated
/// grade-6+ buckets), so they cannot narrow the range; they place the piece inside it,
/// which is what <see cref="ValuationResult.CpPercentile"/> reports.
/// </para>
/// </summary>
/// <param name="OptionStats">
/// lib9c <c>StatType</c> values of the additional (crafting option) stats, deduplicated
/// and sorted. Empty for a piece with no option.
/// </param>
public sealed record ValuationKey(
    EquipmentType Type,
    int Grade,
    ElementalType Element,
    int Level,
    ImmutableSortedSet<int> OptionStats,
    bool HasSkill,
    bool ByCustomCraft)
{
    /// <summary>Null is the same thing as no option, and is not worth a crash.</summary>
    public ImmutableSortedSet<int> OptionStats { get; init; } =
        OptionStats ?? ImmutableSortedSet<int>.Empty;

    /// <summary>
    /// The bucket a stored listing belongs to. Single source of the key, so what
    /// <see cref="MarketDb.GetComparables"/> matches and what a request is turned into
    /// cannot drift apart.
    /// </summary>
    public static ValuationKey Of(ItemProduct product) =>
        new((EquipmentType)product.ItemSubType,
            product.Grade,
            (ElementalType)product.ElementalType,
            product.Level,
            OptionStatsOf(product.StatModels),
            product.SkillModels.Count > 0,
            product.ByCustomCraft);

    /// <summary>
    /// The option stats of a listing: the types of its <em>additional</em> stats, which
    /// are the ones a crafting option rolled. The base stat of the item is not an option
    /// and never enters the key.
    /// </summary>
    public static ImmutableSortedSet<int> OptionStatsOf(IEnumerable<StatModel> stats) =>
        stats.Where(s => s.Additional).Select(s => s.Type).ToImmutableSortedSet();

    /// <summary>Option stat names, e.g. <c>"ATK/DEF/HIT"</c>, or "-" when there is none.</summary>
    public string OptionStatsText() =>
        OptionStats.Count == 0
            ? "-"
            : string.Join("/", OptionStats.Select(GameEnums.StatTypeName));

    public override string ToString() =>
        $"{Type} grado {Grade} · {Elementals.Name(Element)} · +{Level} · " +
        $"{OptionStatsText()} · {(HasSkill ? "con skill" : "senza skill")}" +
        (ByCustomCraft ? " · custom craft" : "");

    /// <summary>
    /// Structural, as the record contract promises: the compiler would compare
    /// <see cref="OptionStats"/> by reference, and two keys built from the same stats
    /// would then land in different buckets.
    /// </summary>
    public bool Equals(ValuationKey? other) =>
        other is not null
        && Type == other.Type
        && Grade == other.Grade
        && Element == other.Element
        && Level == other.Level
        && HasSkill == other.HasSkill
        && ByCustomCraft == other.ByCustomCraft
        && OptionStats.SetEquals(other.OptionStats);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.Add(Grade);
        hash.Add(Element);
        hash.Add(Level);
        hash.Add(HasSkill);
        hash.Add(ByCustomCraft);
        foreach (var stat in OptionStats)
        {
            hash.Add(stat);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// How far the bucket had to be widened before it held enough comparables. Every step is
/// strictly wider than the one before it, and the step reached is part of the answer: a
/// range measured on <see cref="TypeAndGrade"/> and one measured on <see cref="Exact"/>
/// are not the same claim, and look identical if nobody says which is which.
/// </summary>
public enum ValuationStep
{
    /// <summary>The key as asked.</summary>
    Exact = 0,

    /// <summary>Every element of the same type and grade.</summary>
    AnyElement = 1,

    /// <summary>Enhancement levels merged, on top of the element.</summary>
    AnyLevel = 2,

    /// <summary>The same <em>number</em> of options instead of the same set.</summary>
    SameOptionCount = 3,

    /// <summary>Type, grade and skill only.</summary>
    TypeAndGrade = 4,
}

public static class ValuationSteps
{
    /// <summary>The ladder, in the order it is climbed.</summary>
    public static readonly ValuationStep[] Ladder =
    {
        ValuationStep.Exact,
        ValuationStep.AnyElement,
        ValuationStep.AnyLevel,
        ValuationStep.SameOptionCount,
        ValuationStep.TypeAndGrade,
    };

    /// <summary>
    /// What the answer has to say about a step, in the words the bot uses. Empty for
    /// <see cref="ValuationStep.Exact"/>, which has nothing to declare.
    /// </summary>
    public static string Declare(ValuationStep step) => step switch
    {
        ValuationStep.Exact => "",
        ValuationStep.AnyElement => "stimato su tutti gli elementi",
        ValuationStep.AnyLevel => "livelli diversi accorpati",
        ValuationStep.SameOptionCount => "opzioni diverse dalle tue",
        ValuationStep.TypeAndGrade => "stima larga",
        _ => "",
    };
}

/// <summary>Whether a valuation could be measured, and if not why.</summary>
public enum ValuationStatus
{
    /// <summary>A range was measured, at the step <see cref="ValuationResult.Step"/> reports.</summary>
    Ok,

    /// <summary>
    /// The whole ladder was climbed without reaching <see cref="ValuationQuery.MinSamples"/>
    /// comparables. <see cref="ValuationResult.Comparables"/> says how many were found at
    /// the widest step, which is the difference between "no data at all" and "nearly
    /// enough". A range invented on two samples is worse than silence, because it looks
    /// exactly like a good one.
    /// </summary>
    InsufficientData,
}

/// <summary>
/// A piece to value, described the way its owner sees it. The defaults are the ones the
/// deal commands already defend: five comparables, concluded listings when there are any.
/// </summary>
public sealed record ValuationQuery
{
    /// <summary>Planet whose history the piece is compared against.</summary>
    public required Planet Planet { get; init; }

    /// <summary>The piece.</summary>
    public required ValuationKey Key { get; init; }

    /// <summary>
    /// Combat point of the piece, when known. It only places the piece among the
    /// comparables (<see cref="ValuationResult.CpPercentile"/>); it never moves the range.
    /// </summary>
    public int? CombatPoint { get; init; }

    /// <summary>Comparables a bucket needs before the ladder stops widening.</summary>
    public int MinSamples { get; init; } = 5;

    /// <summary>
    /// Lowest rung of <see cref="ValuationSteps.Ladder"/> to try. The default
    /// <see cref="ValuationStep.Exact"/> is the piece as it was described, which is what a
    /// message asks for; anything higher is a widening somebody <em>chose</em> — the
    /// "senza elemento" button of the bot — rather than one the ladder was driven to by a
    /// bucket too small to say anything.
    /// <para>
    /// It is a field of the query and not a second method because the answer has to come
    /// out the same either way: <see cref="ValuationResult.Step"/> declares where the
    /// range was measured, and whoever reads it cannot tell — and must not have to tell —
    /// whether that rung was reached or requested.
    /// </para>
    /// </summary>
    public ValuationStep StartStep { get; init; } = ValuationStep.Exact;

    /// <summary>
    /// Keep only listings still on sale after this instant; null uses the whole history.
    /// </summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>
    /// Population to measure. <see cref="BaselinePopulation.Sold"/> is a preference, not a
    /// requirement: a bucket with too few concluded listings falls back to
    /// <see cref="BaselinePopulation.Listed"/> at the same step rather than widening, and
    /// says so through <see cref="ValuationResult.PopulationFallback"/>.
    /// </summary>
    public BaselinePopulation Population { get; init; } = BaselinePopulation.Sold;

    /// <summary>Tolerance of the sale heuristic (see <see cref="MarketDb.GetPriceBaselines"/>).</summary>
    public double SaleMarginPercent { get; init; } = MarketDb.DefaultSaleMarginPercent;
}

/// <summary>
/// The five numbers of a bucket's price distribution. Quartiles are interpolated between
/// ranks, so <see cref="Median"/> is the same number the baselines of
/// <see cref="MarketDb"/> are built on.
/// </summary>
public sealed record PriceRange(double Min, double P25, double Median, double P75, double Max);

/// <summary>
/// What a valuation found. There is deliberately no "estimated price" field: inside a
/// bucket the price follows neither the combat point nor the option values (the example
/// bucket runs from 11 to 333 NCG around a median of 41), so a single number would be
/// invented precision. Not having the field is what stops a caller from inventing it.
/// </summary>
/// <param name="Key">
/// The piece as it was described. It stays the piece even when the bucket was widened:
/// a key cannot say "any element", so what was given up is <paramref name="Step"/>'s to
/// declare, and the two together name the bucket exactly.
/// </param>
/// <param name="Step">How far the ladder was climbed.</param>
/// <param name="Comparables">Listings the range is built from.</param>
/// <param name="Population">The population actually measured.</param>
/// <param name="PopulationFallback">
/// True when <paramref name="Population"/> is not the one that was asked for, because the
/// concluded listings were too few.
/// </param>
/// <param name="Outcomes">How the listings of the bucket were classified.</param>
/// <param name="Prices">
/// The distribution; null when the status is <see cref="ValuationStatus.InsufficientData"/>.
/// </param>
/// <param name="OldestSeenUtc">Last sighting of the oldest comparable; null when there is none.</param>
/// <param name="NewestSeenUtc">Last sighting of the most recent comparable; null when there is none.</param>
/// <param name="CpPercentile">
/// Share of the comparables the piece beats on combat point, in percent. Null when no
/// combat point was given, or when no comparable carries one.
/// </param>
/// <param name="Listings">
/// The comparables themselves, cheapest first, so the answer can be taken apart instead
/// of trusted.
/// </param>
public sealed record ValuationResult(
    ValuationStatus Status,
    ValuationKey Key,
    ValuationStep Step,
    int Comparables,
    BaselinePopulation Population,
    bool PopulationFallback,
    ListingOutcomes Outcomes,
    PriceRange? Prices,
    DateTime? OldestSeenUtc,
    DateTime? NewestSeenUtc,
    int? CpPercentile,
    IReadOnlyList<ComparableListing> Listings)
{
    /// <summary>What the answer has to declare about the widening, if anything.</summary>
    public string StepDeclaration => ValuationSteps.Declare(Step);
}
