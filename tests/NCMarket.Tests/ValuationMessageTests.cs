using System.Collections.Immutable;
using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The echo of the interpretation. It costs one line and it is what turns a misreading
/// into something visible: without it, a message read wrong produces a valuation of
/// another piece that is indistinguishable from a right one.
/// <para>
/// So the test of the echo is the whole line, character by character, and not a handful
/// of substrings: what has to hold is that every field of the key is in it, including the
/// ones nobody wrote.
/// </para>
/// </summary>
public sealed class ValuationMessageTests
{
    private const int Atk = 2;
    private const int Def = 3;
    private const int Hit = 5;

    private static ValuationQuery Query(
        int[]? options = null,
        int level = 7,
        bool skill = true,
        bool custom = false,
        int? combatPoint = 151_216_255,
        int grade = (int)Grade.Transcendent) =>
        new()
        {
            Planet = Planet.Heimdall,
            Key = new ValuationKey(
                EquipmentType.Weapon, grade, ElementalType.Fire, level,
                (options ?? new[] { Atk, Def, Hit }).ToImmutableSortedSet(), skill, custom),
            CombatPoint = combatPoint,
        };

    [Fact]
    public void The_echo_is_the_whole_piece_in_one_line()
    {
        Assert.Equal(
            "Ho letto: Transcendent Weapon · Fire · +7 · opzioni ATK, DEF, HIT · con skill · " +
            "CP 151,216,255",
            ValuationMessage.Echo(Query()));
    }

    /// <summary>
    /// What was read off the message and what was assumed look the same in the echo, on
    /// purpose: "+0" is the reading most worth contradicting, because it is the one
    /// nobody wrote.
    /// </summary>
    [Fact]
    public void The_fields_nobody_wrote_are_in_the_echo_too()
    {
        Assert.Equal(
            "Ho letto: Transcendent Weapon · Fire · +0 · opzione ATK · senza skill",
            ValuationMessage.Echo(
                Query(new[] { Atk }, level: 0, skill: false, combatPoint: null)));
    }

    /// <summary>
    /// A message whose option lines never arrived is a legitimate query for a piece with
    /// no option, and the two are told apart by the reader, not by the parser — which is
    /// only possible if the echo says which one it read.
    /// </summary>
    [Fact]
    public void A_piece_without_options_says_so()
    {
        Assert.Contains(
            "senza opzioni",
            ValuationMessage.Echo(Query(Array.Empty<int>())),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Custom craft shows only when it is on: its default is no, and a line carrying
    /// every negative answer stops being read.
    /// </summary>
    [Fact]
    public void Custom_craft_shows_when_it_is_on_and_stays_quiet_when_it_is_not()
    {
        Assert.EndsWith(
            "· con skill · custom craft · CP 151,216,255",
            ValuationMessage.Echo(Query(custom: true)),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "custom craft", ValuationMessage.Echo(Query()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The type is named as the rest of the project names it, not by the alias that was
    /// typed: reflecting someone's own word back at them confirms nothing, and "Sword"
    /// would suggest an item was identified — which is exactly what a valuation with no
    /// item id cannot do.
    /// </summary>
    [Fact]
    public void The_type_is_the_one_the_project_prints_and_not_the_alias_that_was_typed()
    {
        Assert.True(ValuationRequestParser.TryParse(
            "Transcendent Sword Fire ATK 1", Planet.Heimdall, out var query, out _));

        var echo = ValuationMessage.Echo(query!);

        Assert.Contains("Transcendent Weapon", echo, StringComparison.Ordinal);
        Assert.DoesNotContain("Sword", echo, StringComparison.Ordinal);
    }

    /// <summary>
    /// The combat point is written the way every other figure of the project is written,
    /// whichever of the three accepted spellings it came in as. The parser speaks the
    /// game's dialect; the answer speaks the project's.
    /// </summary>
    [Theory]
    [InlineData("151.216.255")]
    [InlineData("151,216,255")]
    [InlineData("151216255")]
    public void The_combat_point_comes_back_in_the_spelling_the_project_uses(string cp)
    {
        Assert.True(ValuationRequestParser.TryParse(
            $"Transcendent Sword Fire ATK 1 CP {cp}", Planet.Heimdall, out var query, out _));

        Assert.EndsWith(
            "· CP 151,216,255", ValuationMessage.Echo(query!), StringComparison.Ordinal);
    }

    /// <summary>
    /// A grade lib9c grows tomorrow has no name here yet, and the echo says the number
    /// rather than nothing: the point of the line is that the reader can check it.
    /// </summary>
    [Fact]
    public void A_rarity_without_a_name_is_shown_by_its_number()
    {
        Assert.StartsWith(
            "Ho letto: grado 9 Weapon", ValuationMessage.Echo(Query(grade: 9)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The answer of the example bucket: a range, its median, and every line that keeps
    /// the range from being read as more than it is.
    /// </summary>
    [Fact]
    public void The_answer_is_a_range_with_what_it_was_measured_on()
    {
        var answer = ValuationMessage.Answer(Query(), Result());

        Assert.Equal(
            """
            💰 11.00 NCG – 333.00 NCG · mediana 41.00 NCG
            📊 7 comparabili · prezzi richiesti · heimdall · visti dal 2026-08-14 al 2026-08-24
            📈 Il CP del pezzo sta nel 60° percentile del gruppo
            ⚠️ Prezzi richiesti: è quanto si chiede, non quanto si paga
            """,
            answer);
    }

    /// <summary>
    /// A widened bucket and an exact one look identical unless the answer says which is
    /// which — and the key cannot say it, because a key has no way to mean "any element".
    /// </summary>
    [Fact]
    public void A_widened_bucket_declares_what_was_given_up()
    {
        var answer = ValuationMessage.Answer(
            Query(), Result() with { Step = ValuationStep.AnyElement });

        Assert.Contains(
            "🔎 Bucket allargato: stimato su tutti gli elementi", answer,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a combat point the piece cannot be placed among its comparables, and the
    /// line is left out rather than guessed at.
    /// </summary>
    [Fact]
    public void Without_a_combat_point_there_is_no_percentile_line()
    {
        var answer = ValuationMessage.Answer(
            Query(combatPoint: null), Result() with { CpPercentile = null });

        Assert.DoesNotContain("percentile", answer, StringComparison.Ordinal);
        Assert.Contains("mediana", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// No range at all, and the count of what the widest bucket did find: "two listings"
    /// and "nothing whatsoever" are different answers, and neither of them is a price.
    /// </summary>
    [Fact]
    public void Not_enough_data_says_how_far_it_got_and_gives_no_range()
    {
        var answer = ValuationMessage.Answer(
            Query(),
            Result() with
            {
                Status = ValuationStatus.InsufficientData,
                Step = ValuationStep.TypeAndGrade,
                Comparables = 2,
                Prices = null,
            });

        Assert.Contains("2 inserzioni sulle 5 che servono", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("NCG", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bucket of the plan: seven listings between 11 and 333 NCG around a median of
    /// 41, measured on asking prices because nothing has been observed to conclude yet.
    /// </summary>
    private static ValuationResult Result() =>
        new(ValuationStatus.Ok,
            Query().Key,
            ValuationStep.Exact,
            Comparables: 7,
            BaselinePopulation.Listed,
            PopulationFallback: false,
            new ListingOutcomes(7, 7, 0, 0),
            new PriceRange(11, 24, 41, 120, 333),
            new DateTime(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc),
            CpPercentile: 60,
            Array.Empty<ComparableListing>());
}
