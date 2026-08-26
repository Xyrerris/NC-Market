using System.Collections.Immutable;
using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The only part of the valuation that reads something a person typed. Everything after
/// it is arithmetic on a bucket, so a message misread here does not fail — it answers
/// about a different piece, and the answer looks exactly as sound as a right one.
/// <para>
/// The cases worth writing are therefore of two kinds: the shapes the same piece can be
/// written in, which must all arrive at one query, and the shapes that are not a piece at
/// all, which must be refused by name instead of guessed at.
/// </para>
/// </summary>
public sealed class ValuationRequestParserTests
{
    private const int Atk = 2;
    private const int Def = 3;
    private const int Hit = 5;

    /// <summary>
    /// The message of the plan, as it would be typed by someone reading it off the game:
    /// one field per line, in the order the game shows them.
    /// </summary>
    private const string Example = """
        Transcendent
        Sword Fire
        +7
        ATK 1.404.374
        DEF 3.359.312
        HIT 5.734.266
        Skill si
        CP 151.216.255
        """;

    private static ValuationQuery Parse(string message)
    {
        Assert.True(
            ValuationRequestParser.TryParse(message, Planet.Heimdall, out var query, out var error),
            error);
        Assert.Null(error);
        return query!;
    }

    private static string Error(string message)
    {
        Assert.False(
            ValuationRequestParser.TryParse(message, Planet.Heimdall, out var query, out var error));
        Assert.Null(query);
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error!;
    }

    private static ValuationKey Key(
        int[]? options = null, int level = 7, bool skill = true, bool custom = false) =>
        new(EquipmentType.Weapon, (int)Grade.Transcendent, ElementalType.Fire, level,
            (options ?? new[] { Atk, Def, Hit }).ToImmutableSortedSet(), skill, custom);

    [Fact]
    public void The_example_message_is_read_field_by_field()
    {
        var query = Parse(Example);

        Assert.Equal(Key(), query.Key);
        Assert.Equal(151_216_255, query.CombatPoint);
        Assert.Equal(Planet.Heimdall, query.Planet);

        // The thresholds are the ones the deal commands already defend; the parser reads
        // a piece, it does not get to decide how many comparables are enough.
        Assert.Equal(5, query.MinSamples);
        Assert.Equal(BaselinePopulation.Sold, query.Population);
    }

    /// <summary>
    /// Free line order was the requirement, and classifying tokens rather than lines is
    /// what satisfies it: no line means anything by its position.
    /// </summary>
    [Fact]
    public void The_order_of_the_lines_does_not_matter()
    {
        var reversed = string.Join("\n", Example.Split('\n').Reverse());

        Assert.Equal(Parse(Example), Parse(reversed));
    }

    /// <summary>
    /// The same classification also buys the shape nobody planned for: the whole piece on
    /// one line, which is how it gets written on a phone.
    /// </summary>
    [Fact]
    public void The_whole_piece_fits_on_one_line()
    {
        var query = Parse(
            "Transcendent Sword Fire +7 ATK 1.404.374 DEF 3.359.312 HIT 5.734.266 " +
            "skill si CP 151.216.255");

        Assert.Equal(Parse(Example), query);
    }

    /// <summary>
    /// A number is a number however its thousands are grouped: the dotted form is what
    /// the game shows, the comma form is what another locale shows, and the bare form is
    /// what someone retypes. None of the three is a decimal.
    /// </summary>
    [Theory]
    [InlineData("CP 151.216.255")]
    [InlineData("CP 151,216,255")]
    [InlineData("CP 151216255")]
    public void Thousands_separators_are_ignored_inside_a_number(string cp)
    {
        var query = Parse("Transcendent Sword Fire ATK 1.404.374 " + cp);

        Assert.Equal(151_216_255, query.CombatPoint);
    }

    /// <summary>
    /// The stat values are consumed so they do not fall through as bare numbers, and then
    /// dropped: they are not part of the bucket, whichever way they are written.
    /// </summary>
    [Theory]
    [InlineData("ATK 1.404.374")]
    [InlineData("ATK 1,404,374")]
    [InlineData("ATK 1404374")]
    [InlineData("ATK")]
    public void The_value_of_an_option_is_read_and_dropped(string option)
    {
        var query = Parse("Transcendent Sword Fire " + option);

        Assert.Equal(new[] { Atk }, query.Key.OptionStats);
    }

    /// <summary>
    /// The aliases are <see cref="NCMarket.Core.Models.GameEnums.StatTypeName"/> read
    /// backwards, so the names the answer prints are names a request may use; the
    /// synonyms are what someone types when not reading them off the game.
    /// </summary>
    [Theory]
    [InlineData("ATK", Atk)]
    [InlineData("atk", Atk)]
    [InlineData("ATTACK", Atk)]
    [InlineData("Attacco", Atk)]
    [InlineData("DEF", Def)]
    [InlineData("HIT", Hit)]
    [InlineData("SPEED", 6)]
    [InlineData("SPD", 6)]
    [InlineData("CRIT", 4)]
    [InlineData("CRI", 4)]
    [InlineData("ArmorPen", 10)]
    public void A_stat_is_named_by_its_name_or_by_a_synonym(string alias, int expected)
    {
        var query = Parse($"Transcendent Sword Fire {alias} 100");

        Assert.Equal(new[] { expected }, query.Key.OptionStats);
    }

    [Theory]
    [InlineData("skill si", true)]
    [InlineData("skill sì", true)]
    [InlineData("skill yes", true)]
    [InlineData("skill no", false)]
    [InlineData("Skill NO", false)]
    [InlineData("con skill", true)]
    [InlineData("senza skill", false)]
    [InlineData("skill", true)]
    public void The_skill_is_answered_yes_or_no_in_the_words_people_use(
        string skill, bool expected)
    {
        var query = Parse("Transcendent Sword Fire ATK 1 " + skill);

        Assert.Equal(expected, query.Key.HasSkill);
    }

    /// <summary>
    /// The fields that default rather than being asked for. Every default is the common
    /// case — 92% of the listings are +0, and custom craft stops at grade 6 — and every
    /// one of them is shown by the echo, which is what keeps a default from being a guess.
    /// </summary>
    [Fact]
    public void What_is_not_written_takes_the_default_of_the_common_case()
    {
        var query = Parse("Transcendent Sword Fire ATK 1.404.374");

        Assert.Equal(0, query.Key.Level);
        Assert.False(query.Key.HasSkill);
        Assert.False(query.Key.ByCustomCraft);
        Assert.Null(query.CombatPoint);
    }

    /// <summary>
    /// Custom craft is invisible in the triple — same sub type, same grade, same element,
    /// a different item id — so it has to be asked, and whoever crafted the piece knows.
    /// </summary>
    [Theory]
    [InlineData("custom craft si", true)]
    [InlineData("custom craft", true)]
    [InlineData("customcraft si", true)]
    [InlineData("custom si", true)]
    [InlineData("con custom craft", true)]
    [InlineData("custom craft no", false)]
    [InlineData("senza custom craft", false)]
    public void Custom_craft_is_asked_and_not_deduced(string custom, bool expected)
    {
        var query = Parse($"Divinity Sword Fire ATK 1 {custom} DEF 2");

        Assert.Equal(expected, query.Key.ByCustomCraft);
        Assert.Equal(new[] { Atk, Def }, query.Key.OptionStats);
    }

    /// <summary>
    /// Two rolls landing on the same stat come back merged into one row, so one row is
    /// what the piece shows and one option is what the bucket holds.
    /// </summary>
    [Fact]
    public void The_same_stat_written_twice_is_one_option()
    {
        var query = Parse("Transcendent Sword Fire ATK 1.404.374 ATK 200.000");

        Assert.Equal(new[] { Atk }, query.Key.OptionStats);
    }

    /// <summary>
    /// A piece with no option is a bucket like any other. It is also the shape of a
    /// message whose option lines never arrived, which is what the echo is there to make
    /// visible — refusing it here would refuse the legitimate piece too.
    /// </summary>
    [Fact]
    public void A_piece_without_options_is_a_piece()
    {
        var query = Parse("Transcendent Sword Fire");

        Assert.Empty(query.Key.OptionStats);
    }

    /// <summary>
    /// 'Normal' is a rarity and an element at once. The field still free is the one
    /// meant, which reads both of them without a rule anybody has to remember.
    /// </summary>
    [Fact]
    public void The_word_that_is_two_fields_fills_the_one_still_free()
    {
        var query = Parse("Normal Sword Normal ATK 1");

        Assert.Equal((int)Grade.Normal, query.Key.Grade);
        Assert.Equal(ElementalType.Normal, query.Key.Element);
    }

    [Fact]
    public void The_planet_of_the_query_is_the_one_it_was_asked_on()
    {
        Assert.True(ValuationRequestParser.TryParse(Example, Planet.Odin, out var query, out _));

        Assert.Equal(Planet.Odin, query!.Planet);
    }

    /// <summary>
    /// The element is mandatory, and its absence is refused rather than widened over: at
    /// grade 8 the triple is the item, so "any element" mixes five items whose prices are
    /// on different scales. That widening exists — it is step 1 of the ladder — and it is
    /// chosen with a button, not reached by forgetting a word.
    /// </summary>
    [Fact]
    public void A_missing_element_is_refused_and_named()
    {
        var error = Error("Transcendent Sword +7 ATK 1.404.374");

        Assert.Contains("elemento", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fire", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_rarity_or_type_is_refused_and_named()
    {
        Assert.Contains("rarità", Error("Sword Fire ATK 1"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo", Error("Transcendent Fire ATK 1"), StringComparison.OrdinalIgnoreCase);

        // Both missing, both named: one round trip per message, not one per field.
        var error = Error("Fire ATK 1");
        Assert.Contains("rarità", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="Grades.TryParse"/> accepts "8", so a lone 8 would quietly become
    /// Transcendent — a rarity nobody wrote, in a query that answers as confidently as
    /// any other. The token is named and the question asked instead.
    /// </summary>
    [Theory]
    [InlineData("8")]
    [InlineData("1.404.374")]
    public void A_bare_number_is_an_error_and_not_a_guess(string bare)
    {
        var error = Error($"Transcendent Sword Fire ATK 1.404.374 {bare}");

        Assert.Contains(bare, error, StringComparison.Ordinal);
        Assert.Contains($"+{bare}", error, StringComparison.Ordinal);
        Assert.Contains($"CP {bare}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stat_nobody_knows_is_refused_by_name()
    {
        var error = Error("Transcendent Sword Fire MAGIC 1.404.374");

        Assert.Contains("MAGIC", error, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_four_options_is_a_message_that_was_misread()
    {
        var error = Error("Transcendent Sword Fire ATK 1 DEF 2 HIT 3 CRI 4 SPD 5");

        Assert.Contains("SPD", error, StringComparison.Ordinal);
        Assert.Contains("quattro", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_field_answered_twice_is_refused_rather_than_overwritten()
    {
        Assert.Contains(
            "Mythic", Error("Transcendent Mythic Sword Fire ATK 1"), StringComparison.Ordinal);
        Assert.Contains(
            "+3", Error("Transcendent Sword Fire +7 +3 ATK 1"), StringComparison.Ordinal);
        Assert.Contains(
            "skill", Error("Transcendent Sword Fire skill si skill no ATK 1"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CP", Error("Transcendent Sword Fire CP 100 CP 200 ATK 1"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyword_left_without_its_value_says_which_value_it_wanted()
    {
        Assert.Contains("CP", Error("Transcendent Sword Fire CP"), StringComparison.Ordinal);
        Assert.Contains("CP", Error("Transcendent Sword Fire CP tanto"), StringComparison.Ordinal);
        Assert.Contains("+", Error("Transcendent Sword Fire +molto"), StringComparison.Ordinal);

        // A yes/no that binds to nothing is the same kind of dangling token.
        Assert.Contains("si", Error("Transcendent Sword Fire si ATK 1"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void An_empty_message_is_answered_with_an_example(string message)
    {
        Assert.Contains("Transcendent", Error(message), StringComparison.Ordinal);
    }

    /// <summary>
    /// The separators of the bot's own answer are separators on the way back in, so the
    /// echo can be corrected and returned instead of retyped.
    /// </summary>
    [Fact]
    public void The_punctuation_of_an_echo_is_read_back_as_separators()
    {
        var query = Parse("Transcendent Sword · Fire · +7 · ATK, DEF, HIT · skill si");

        Assert.Equal(Key(), query.Key);
    }
}
