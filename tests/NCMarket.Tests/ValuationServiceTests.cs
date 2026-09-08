using System.Collections.Immutable;
using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

/// <summary>
/// The widening ladder. Two things have to hold at once for an answer to be worth
/// sending: the comparables have to be the same piece, and there have to be enough of
/// them. Every test here is a case where the two disagree, plus the cases where the
/// bucket must not widen — a range that quietly took in a different item is the failure
/// this whole feature is built to avoid.
/// </summary>
public sealed class ValuationServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private const int Atk = 2;
    private const int Def = 3;
    private const int Hit = 5;
    private const int Spd = 6;

    private static readonly EquipmentType[] Weapons = { EquipmentType.Weapon };

    private static ValuationKey Key(
        int[]? options = null,
        ElementalType element = ElementalType.Fire,
        int level = 0,
        bool skill = true,
        bool custom = false) =>
        new(EquipmentType.Weapon, 8, element, level,
            (options ?? new[] { Atk, Def }).ToImmutableSortedSet(), skill, custom);

    /// <summary>A listing of the bucket <see cref="Key"/> describes, priced as asked.</summary>
    private static ItemProduct Piece(
        decimal price,
        int[]? options = null,
        ElementalType element = ElementalType.Fire,
        int level = 0,
        bool skill = true,
        bool custom = false,
        int combatPoint = 1000,
        Guid? productId = null) =>
        TestData.Product(
            itemId: 10181000,
            level: level,
            price: price,
            combatPoint: combatPoint,
            grade: 8,
            itemSubType: (int)EquipmentType.Weapon,
            productId: productId,
            elementalType: (int)element,
            optionStats: options ?? new[] { Atk, Def },
            hasSkill: skill,
            byCustomCraft: custom);

    private static ValuationResult Evaluate(
        MarketDb db,
        ValuationKey key,
        int minSamples = 5,
        int? combatPoint = null,
        BaselinePopulation population = BaselinePopulation.Listed,
        ValuationStep start = ValuationStep.Exact) =>
        new ValuationService(db).Evaluate(new ValuationQuery
        {
            Planet = Planet.Heimdall,
            Key = key,
            MinSamples = minSamples,
            CombatPoint = combatPoint,
            Population = population,
            StartStep = start,
        });

    private static void Sell(MarketDb db, params ItemProduct[] products) =>
        TestData.AddCompleteSnapshot(db, Now, products, types: Weapons);

    [Fact]
    public void A_bucket_with_enough_comparables_answers_where_it_is()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db, Piece(10m), Piece(20m), Piece(30m), Piece(40m), Piece(100m));

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStatus.Ok, result.Status);
        Assert.Equal(ValuationStep.Exact, result.Step);
        Assert.Equal("", result.StepDeclaration);
        Assert.Equal(5, result.Comparables);
        Assert.Equal(new PriceRange(10, 20, 30, 40, 100), result.Prices);
        Assert.Equal(Now, result.OldestSeenUtc);
        Assert.Equal(Now, result.NewestSeenUtc);
    }

    /// <summary>
    /// The element is the first thing given up, and the answer says so: at grade 8 the
    /// five elements are five items, so a range that merged them is a different claim
    /// from one that did not.
    /// </summary>
    [Fact]
    public void The_element_is_the_first_thing_given_up()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m),
            Piece(20m),
            Piece(30m, element: ElementalType.Water),
            Piece(40m, element: ElementalType.Water),
            Piece(50m, element: ElementalType.Wind));

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStep.AnyElement, result.Step);
        Assert.Equal("stimato su tutti gli elementi", result.StepDeclaration);
        Assert.Equal(5, result.Comparables);
    }

    /// <summary>
    /// The ladder can be climbed from higher up, which is what the "senza elemento" button
    /// of the bot asks for. What the test is really about is that the result is
    /// indistinguishable from the one a bucket too small would have produced: the answer
    /// declares where the range was measured, never whether that rung was reached or
    /// requested.
    /// </summary>
    [Fact]
    public void A_ladder_started_higher_up_gives_up_the_element_without_being_forced_to()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m),
            Piece(20m),
            Piece(30m),
            Piece(40m),
            Piece(100m),
            Piece(50m, element: ElementalType.Water));

        // Il bucket esatto basterebbe: cinque comparabili sono i cinque richiesti.
        Assert.Equal(ValuationStep.Exact, Evaluate(db, Key()).Step);

        var widened = Evaluate(db, Key(), start: ValuationStep.AnyElement);

        Assert.Equal(ValuationStep.AnyElement, widened.Step);
        Assert.Equal("stimato su tutti gli elementi", widened.StepDeclaration);
        Assert.Equal(6, widened.Comparables);
    }

    [Fact]
    public void The_level_goes_after_the_element()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m),
            Piece(20m, element: ElementalType.Water),
            Piece(30m, level: 7),
            Piece(40m, level: 7),
            Piece(50m, level: 12, element: ElementalType.Wind));

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStep.AnyLevel, result.Step);
        Assert.Equal("livelli diversi accorpati", result.StepDeclaration);
        Assert.Equal(5, result.Comparables);
    }

    /// <summary>
    /// Only here do options on other stats enter the bucket, and only with the same
    /// number of rolls behind them.
    /// </summary>
    [Fact]
    public void The_options_stop_being_a_set_and_become_a_number()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m),
            Piece(20m, level: 7),
            Piece(30m, options: new[] { Atk, Hit }),
            Piece(40m, options: new[] { Def, Spd }),
            Piece(50m, options: new[] { Hit, Spd }, element: ElementalType.Wind),
            // Three options is a different piece, and stays out even here.
            Piece(900m, options: new[] { Atk, Def, Hit }));

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStep.SameOptionCount, result.Step);
        Assert.Equal("opzioni diverse dalle tue", result.StepDeclaration);
        Assert.Equal(5, result.Comparables);
        Assert.Equal(50d, result.Prices!.Max);
    }

    [Fact]
    public void The_widest_step_keeps_the_type_the_grade_and_the_skill()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m),
            Piece(20m, options: new[] { Atk, Hit }),
            Piece(30m, options: new[] { Atk, Def, Hit }),
            Piece(40m, options: new[] { Spd }),
            Piece(50m, options: Array.Empty<int>()),
            // Same rarity, no skill: the one distinction the widest bucket still keeps.
            Piece(900m, skill: false));

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStep.TypeAndGrade, result.Step);
        Assert.Equal("stima larga", result.StepDeclaration);
        Assert.Equal(5, result.Comparables);
        Assert.Equal(50d, result.Prices!.Max);
    }

    /// <summary>
    /// Past the last step there is no answer, and saying so is the answer. A range built
    /// on two listings reads exactly like one built on fifty.
    /// </summary>
    [Fact]
    public void A_ladder_that_runs_out_says_so_instead_of_inventing_a_range()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m),
            Piece(20m, options: new[] { Spd }, level: 7, element: ElementalType.Wind),
            Piece(900m, skill: false),
            Piece(900m, skill: false, element: ElementalType.Water));

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStatus.InsufficientData, result.Status);
        Assert.Null(result.Prices);
        Assert.Equal(ValuationStep.TypeAndGrade, result.Step);
        Assert.Equal(2, result.Comparables);
    }

    /// <summary>
    /// Concluded listings are what the question is really about, so they are tried first
    /// and used when there are enough of them.
    /// </summary>
    [Fact]
    public void Concluded_listings_are_measured_when_there_are_enough_of_them()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var standing = Guid.NewGuid();
        TestData.AddCompleteSnapshot(db, Now.AddDays(-1), new[]
        {
            Piece(90m), Piece(95m), Piece(100m), Piece(105m), Piece(110m),
            Piece(100m, productId: standing),
        }, types: Weapons);

        // A second complete capture of the same type: the five listings missing from it
        // are gone from the market, and none of them was asking above the going rate.
        TestData.AddCompleteSnapshot(
            db, Now, new[] { Piece(100m, productId: standing) }, types: Weapons);

        var result = Evaluate(db, Key(), population: BaselinePopulation.Sold);

        Assert.Equal(ValuationStatus.Ok, result.Status);
        Assert.Equal(BaselinePopulation.Sold, result.Population);
        Assert.False(result.PopulationFallback);
        Assert.Equal(5, result.Comparables);
        Assert.Equal(new ListingOutcomes(6, 1, 5, 0), result.Outcomes);
        Assert.Equal(110d, result.Prices!.Max);
    }

    /// <summary>
    /// With no disappearance observable yet — which is the state of the database today —
    /// the answer falls back to asking prices <em>at the same step</em>. Widening first
    /// would answer every question from the widest bucket on the ladder while an exact
    /// one sat there unused, and the fallback is declared either way.
    /// </summary>
    [Fact]
    public void Asking_prices_are_a_declared_fallback_and_not_a_reason_to_widen()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db, Piece(10m), Piece(20m), Piece(30m), Piece(40m), Piece(50m));

        var result = Evaluate(db, Key(), population: BaselinePopulation.Sold);

        Assert.Equal(ValuationStatus.Ok, result.Status);
        Assert.Equal(ValuationStep.Exact, result.Step);
        Assert.Equal(BaselinePopulation.Listed, result.Population);
        Assert.True(result.PopulationFallback);
        Assert.Equal(5, result.Comparables);
        Assert.Equal(new ListingOutcomes(5, 5, 0, 0), result.Outcomes);
    }

    /// <summary>
    /// The combat point never moves the range — it does not predict the price inside a
    /// bucket — it only says where the piece sits in the group.
    /// </summary>
    [Fact]
    public void The_combat_point_places_the_piece_among_its_comparables()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m, combatPoint: 100),
            Piece(20m, combatPoint: 200),
            Piece(30m, combatPoint: 300),
            Piece(40m, combatPoint: 400),
            Piece(50m, combatPoint: 500));

        var withCp = Evaluate(db, Key(), combatPoint: 350);
        var withoutCp = Evaluate(db, Key());

        Assert.Equal(60, withCp.CpPercentile);
        Assert.Null(withoutCp.CpPercentile);
        Assert.Equal(withoutCp.Prices, withCp.Prices);
    }

    [Fact]
    public void Comparables_without_a_combat_point_cannot_rank_anything()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m, combatPoint: 0), Piece(20m, combatPoint: 0), Piece(30m, combatPoint: 0),
            Piece(40m, combatPoint: 0), Piece(50m, combatPoint: 0));

        Assert.Null(Evaluate(db, Key(), combatPoint: 350).CpPercentile);
    }

    /// <summary>
    /// Three fields the exact bucket does not merge, each checked by putting a pile of
    /// expensive listings just outside it: if one leaked in, the maximum would say so.
    /// </summary>
    [Theory]
    [InlineData("options")]
    [InlineData("skill")]
    [InlineData("custom")]
    public void What_the_exact_bucket_excludes_never_reaches_the_range(string difference)
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        ItemProduct Other(decimal price) => difference switch
        {
            "options" => Piece(price, options: new[] { Atk, Hit }),
            "skill" => Piece(price, skill: false),
            _ => Piece(price, custom: true),
        };

        Sell(db,
            Piece(10m), Piece(20m), Piece(30m), Piece(40m), Piece(50m),
            Other(900m), Other(910m), Other(920m), Other(930m), Other(940m));

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStep.Exact, result.Step);
        Assert.Equal(5, result.Comparables);
        Assert.Equal(50d, result.Prices!.Max);
    }

    /// <summary>
    /// A custom-crafted piece shares sub type, grade and element with the ordinary ones,
    /// so without that field the two would be one bucket — and the ladder could never
    /// recover from it, because it only ever widens.
    /// </summary>
    [Fact]
    public void A_custom_craft_is_valued_against_custom_crafts()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db,
            Piece(10m), Piece(20m), Piece(30m), Piece(40m), Piece(50m),
            Piece(900m, custom: true), Piece(910m, custom: true), Piece(920m, custom: true),
            Piece(930m, custom: true), Piece(940m, custom: true));

        var result = Evaluate(db, Key(custom: true));

        Assert.Equal(ValuationStep.Exact, result.Step);
        Assert.Equal(900d, result.Prices!.Min);
    }

    /// <summary>
    /// A planet is not a wider bucket, it is another market: it is not on the ladder, and
    /// nothing from it reaches an answer.
    /// </summary>
    [Fact]
    public void Another_planet_is_not_a_step_of_the_ladder()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        TestData.AddCompleteSnapshot(db, Now, new[]
        {
            Piece(10m), Piece(20m), Piece(30m), Piece(40m), Piece(50m),
        }, planet: "odin", types: Weapons);

        var result = Evaluate(db, Key());

        Assert.Equal(ValuationStatus.InsufficientData, result.Status);
        Assert.Equal(0, result.Comparables);
    }

    /// <summary>The comparables come back cheapest first, so an answer can be taken apart.</summary>
    [Fact]
    public void The_comparables_come_back_with_the_answer()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Sell(db, Piece(30m), Piece(10m), Piece(50m), Piece(20m), Piece(40m));

        var result = Evaluate(db, Key());

        Assert.Equal(
            new[] { 10d, 20d, 30d, 40d, 50d },
            result.Listings.Select(l => l.Price));
        Assert.All(result.Listings, l => Assert.Equal(10181000, l.ItemId));
    }
}
