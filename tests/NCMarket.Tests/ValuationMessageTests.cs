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
}
