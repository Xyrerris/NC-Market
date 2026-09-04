using System.Collections.Immutable;
using System.Globalization;
using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The echo of the interpretation, the answer, and the listings under it. The echo costs
/// one line and is what turns a misreading into something visible: without it, a message
/// read wrong produces a valuation of another piece that is indistinguishable from a right
/// one.
/// <para>
/// So the test of the echo is the whole line, character by character, and not a handful of
/// substrings: what has to hold is that every field of the key is in it, including the
/// ones nobody wrote — and, since the buttons arrived, that the MarkdownV2 around them is
/// markup Telegram accepts rather than markup that gets the whole message refused.
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
            "🏷️ Ho letto: *Transcendent Weapon* · Fire · \\+7 · opzioni ATK, DEF, HIT · " +
            "con skill · CP `151,216,255`",
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
            "🏷️ Ho letto: *Transcendent Weapon* · Fire · \\+0 · opzione ATK · senza skill",
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
            "· con skill · custom craft · CP `151,216,255`",
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
            "· CP `151,216,255`", ValuationMessage.Echo(query!), StringComparison.Ordinal);
    }

    /// <summary>
    /// A grade lib9c grows tomorrow has no name here yet, and the echo says the number
    /// rather than nothing: the point of the line is that the reader can check it.
    /// </summary>
    [Fact]
    public void A_rarity_without_a_name_is_shown_by_its_number()
    {
        Assert.StartsWith(
            "🏷️ Ho letto: *grado 9 Weapon*", ValuationMessage.Echo(Query(grade: 9)),
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
            💰 `11.00 NCG` – `333.00 NCG` · mediana `41.00 NCG`
            📊 `7` comparabili · prezzi richiesti · `heimdall` · visti dal `2026-08-14` al `2026-08-24`
            📈 Il CP del pezzo sta nel `60`° percentile del gruppo
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

        Assert.Contains(
            "`2` inserzioni sulle `5` che servono", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("NCG", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The listings are what makes a range checkable instead of believable: cheapest
    /// first, each carrying the fields a widened bucket may have merged, under the echo —
    /// because the button that asks for them can be pressed on an answer from days ago.
    /// </summary>
    [Fact]
    public void The_comparables_are_listed_cheapest_first_under_the_echo()
    {
        var listing = ValuationMessage.Comparables(Query(), Result() with
        {
            Comparables = 3,
            Listings = new[]
            {
                Listing(333, level: 7, ElementalType.Fire, cp: 0, "2026-08-24",
                    ListingOutcome.LikelyWithdrawn),
                Listing(11, level: 7, ElementalType.Fire, cp: 120_000_000, "2026-08-14",
                    ListingOutcome.Open),
                Listing(41, level: 0, ElementalType.Water, cp: 90_000_000, "2026-08-20",
                    ListingOutcome.LikelySold),
            },
        });

        Assert.Equal(
            "🏷️ Ho letto: *Transcendent Weapon* · Fire · \\+7 · opzioni ATK, DEF, HIT · " +
            "con skill · CP `151,216,255`\n" +
            "\n" +
            "🔍 `3` comparabili, dal più economico:\n" +
            "1\\. `11.00 NCG` · \\+7 Fire · CP `120,000,000` · `2026-08-14` · in vendita\n" +
            "2\\. `41.00 NCG` · \\+0 Water · CP `90,000,000` · `2026-08-20` · probabile vendita\n" +
            "3\\. `333.00 NCG` · \\+7 Fire · `2026-08-24` · probabile ritiro",
            listing);
    }

    /// <summary>
    /// Past a screenful the list stops being what makes a range checkable, so it says how
    /// many it left out instead of scrolling on — and where they are, because "five more"
    /// reads as "five unknown ones" unless the order is stated.
    /// </summary>
    [Fact]
    public void A_long_bucket_is_cut_and_says_how_many_it_left_out()
    {
        var many = Enumerable.Range(1, 25).Select(i => Listing(
            i, level: 0, ElementalType.Fire, cp: 0, "2026-08-24", ListingOutcome.Open));

        var listing = ValuationMessage.Comparables(
            Query(), Result() with { Comparables = 25, Listings = many.ToArray() });

        Assert.Contains(
            "🔍 `25` comparabili, i 20 più economici:", listing, StringComparison.Ordinal);
        Assert.Contains(
            "➕ Altri `5` non elencati, tutti più cari di questi\\.", listing,
            StringComparison.Ordinal);
        Assert.Contains("20\\. `20.00 NCG`", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("21\\. ", listing, StringComparison.Ordinal);
    }

    /// <summary>
    /// The button is not offered on an empty bucket, but a button already sent sits on a
    /// phone for as long as the message does: pressing it after the history was pruned has
    /// to produce a sentence, not a list of nothing.
    /// </summary>
    [Fact]
    public void An_empty_bucket_says_so_instead_of_listing_nothing()
    {
        var listing = ValuationMessage.Comparables(
            Query(),
            Result() with
            {
                Status = ValuationStatus.InsufficientData,
                Comparables = 0,
                Prices = null,
                Listings = Array.Empty<ComparableListing>(),
            });

        Assert.Contains("Non ho comparabili da elencare", listing, StringComparison.Ordinal);
    }

    /// <summary>
    /// The invariant the whole class is written to keep, and the one
    /// <see cref="TelegramNotifier.Split"/> depends on: an entity spanning a newline would
    /// be cut in half by any message long enough to be split, and Telegram refuses a part
    /// it cannot parse — which costs the answer, not merely its formatting.
    /// </summary>
    [Fact]
    public void No_entity_crosses_a_newline()
    {
        var messages = new[]
        {
            ValuationMessage.Echo(Query()),
            ValuationMessage.Answer(Query(), Result()),
            ValuationMessage.Answer(
                Query(),
                Result() with { Status = ValuationStatus.InsufficientData, Prices = null }),
            ValuationMessage.Comparables(Query(), Result() with
            {
                Listings = new[]
                {
                    Listing(11, 7, ElementalType.Fire, 1, "2026-08-14", ListingOutcome.Open),
                },
            }),
        };

        foreach (var line in messages.SelectMany(m => m.Split('\n')))
        {
            Assert.Equal(0, Unescaped(line, '`') % 2);
            Assert.Equal(0, Unescaped(line, '*') % 2);
        }
    }

    /// <summary>
    /// How many times a character appears as markup rather than as text. A backslash
    /// escapes whatever follows it, and that is the whole difference between an entity and
    /// a backtick somebody wrote.
    /// </summary>
    private static int Unescaped(string line, char c)
    {
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
            }
            else if (line[i] == c)
            {
                count++;
            }
        }

        return count;
    }

    private static ComparableListing Listing(
        double price,
        int level,
        ElementalType element,
        int cp,
        string seen,
        ListingOutcome outcome) =>
        new(Guid.NewGuid(),
            10181000,
            level,
            element,
            price,
            cp,
            DateTime.SpecifyKind(
                DateTime.ParseExact(seen, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    .AddHours(9),
                DateTimeKind.Utc),
            outcome);

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
