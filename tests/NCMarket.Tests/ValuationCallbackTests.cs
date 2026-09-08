using System.Collections.Immutable;
using System.Text;
using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The vocabulary of the buttons. Two things have to hold and neither is about formatting:
/// a query written into a button has to come back out as the same query — the button is
/// where the question lives once the answer has been sent — and anything else that arrives
/// has to be refused, because callback data comes back from a client and is therefore
/// input.
/// </summary>
public sealed class ValuationCallbackTests
{
    private const int Atk = 2;
    private const int Def = 3;

    private static ValuationQuery Query(
        Planet? planet = null,
        int? combatPoint = 151_216_255,
        ValuationStep start = ValuationStep.Exact,
        bool custom = false,
        int[]? options = null) =>
        new()
        {
            Planet = planet ?? Planet.Heimdall,
            Key = new ValuationKey(
                EquipmentType.Weapon,
                (int)Grade.Transcendent,
                ElementalType.Fire,
                7,
                (options ?? new[] { Atk, Def }).ToImmutableSortedSet(),
                HasSkill: true,
                ByCustomCraft: custom),
            CombatPoint = combatPoint,
            StartStep = start,
        };

    /// <summary>
    /// Every field of the piece, plus where to look for it and where to start looking.
    /// What is not carried has to be reconstructible from the running bot, and a key is
    /// not.
    /// </summary>
    [Fact]
    public void A_query_written_into_a_button_comes_back_the_same_query()
    {
        var query = Query();

        Assert.True(ValuationCallback.TryDecode(
            ValuationCallback.Encode(ValuationAction.Comparables, query),
            out var action,
            out var decoded));

        Assert.Equal(ValuationAction.Comparables, action);
        Assert.Equal(query.Planet, decoded!.Planet);
        Assert.Equal(query.Key, decoded.Key);
        Assert.Equal(query.CombatPoint, decoded.CombatPoint);
        Assert.Equal(query.StartStep, decoded.StartStep);
    }

    /// <summary>
    /// The three cases that are not the ordinary one: a piece nobody gave a combat point
    /// for, a bucket already widened on purpose, and the other planet.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void The_optional_fields_survive_the_trip(bool noCp, bool widened, bool odin)
    {
        var query = Query(
            planet: odin ? Planet.Odin : Planet.Heimdall,
            combatPoint: noCp ? null : 151_216_255,
            start: widened ? ValuationStep.AnyElement : ValuationStep.Exact,
            custom: true,
            options: Array.Empty<int>());

        Assert.True(ValuationCallback.TryDecode(
            ValuationCallback.Encode(ValuationAction.Widen, query), out _, out var decoded));

        Assert.Equal(query.Planet, decoded!.Planet);
        Assert.Equal(query.CombatPoint, decoded.CombatPoint);
        Assert.Equal(query.StartStep, decoded.StartStep);
        Assert.Empty(decoded.Key.OptionStats);
        Assert.True(decoded.Key.ByCustomCraft);
    }

    /// <summary>
    /// Telegram refuses the whole message when the data of one button is longer than 64
    /// bytes — the message, not the button. So the widest piece this project can describe
    /// has to fit, and the check has to happen on the line that composes the button rather
    /// than as a 400 answered to somebody waiting for a valuation.
    /// </summary>
    [Fact]
    public void The_widest_piece_fits_in_the_sixty_four_bytes_telegram_allows()
    {
        var widest = new ValuationQuery
        {
            Planet = Planet.Heimdall,
            Key = new ValuationKey(
                EquipmentType.Necklace,
                99,
                ElementalType.Wind,
                255,
                new[] { 11, 12, 13, 14 }.ToImmutableSortedSet(),
                HasSkill: true,
                ByCustomCraft: true),
            CombatPoint = int.MaxValue,
            StartStep = ValuationStep.TypeAndGrade,
        };

        var data = ValuationCallback.Encode(ValuationAction.OtherPlanet, widest);

        Assert.True(
            Encoding.UTF8.GetByteCount(data) <= InlineButton.MaxDataBytes,
            $"'{data}' è di {Encoding.UTF8.GetByteCount(data)} byte.");

        // E il bottone lo verifica da sé, perché una tastiera che non si può mandare deve
        // fallire dove è stata composta.
        Assert.Throws<ArgumentException>(
            () => new InlineButton("Troppo", new string('x', InlineButton.MaxDataBytes + 1)));
    }

    /// <summary>
    /// Data nobody here wrote is refused rather than half-read: it comes back from a
    /// client, and a field decoded out of something crafted would become a query on the
    /// database.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("pippo")]
    [InlineData("v1|c|heimdall|8|6|1|7|2,3|1|0|151216255")]           // un campo in meno
    [InlineData("v0|c|heimdall|8|6|1|7|2,3|1|0|151216255|0")]         // versione precedente
    [InlineData("v1|z|heimdall|8|6|1|7|2,3|1|0|151216255|0")]         // azione sconosciuta
    [InlineData("v1|c|marte|8|6|1|7|2,3|1|0|151216255|0")]            // pianeta inesistente
    [InlineData("v1|c|heimdall|8|99|1|7|2,3|1|0|151216255|0")]        // tipo inesistente
    [InlineData("v1|c|heimdall|8|6|9|7|2,3|1|0|151216255|0")]         // elemento inesistente
    [InlineData("v1|c|heimdall|8|6|1|999|2,3|1|0|151216255|0")]       // livello fuori scala
    [InlineData("v1|c|heimdall|8|6|1|7|2,3,5,7,11|1|0|151216255|0")]  // cinque opzioni
    [InlineData("v1|c|heimdall|8|6|1|7|2,3|si|0|151216255|0")]        // skill non booleana
    [InlineData("v1|c|heimdall|8|6|1|7|2,3|1|0|151216255|9")]         // gradino inesistente
    public void Data_nobody_here_wrote_is_refused(string data)
    {
        Assert.False(ValuationCallback.TryDecode(data, out _, out var query));
        Assert.Null(query);
    }

    /// <summary>
    /// The guided flow speaks the other half of the vocabulary, and the two are told apart
    /// by the data itself: a press of the flow must never decode as a query, or a stale
    /// keyboard would end up answering a question nobody asked.
    /// </summary>
    [Fact]
    public void The_two_vocabularies_do_not_answer_for_each_other()
    {
        var press = ValuationCallback.Encode(DialogField.Element, (int)ElementalType.Wind);

        Assert.True(ValuationCallback.TryDecodeDialog(press, out var field, out var value));
        Assert.Equal(DialogField.Element, field);
        Assert.Equal((int)ElementalType.Wind, value);

        Assert.False(ValuationCallback.TryDecode(press, out _, out _));
        Assert.False(ValuationCallback.IsAnswer(press));

        var answer = ValuationCallback.Encode(ValuationAction.Widen, Query());
        Assert.True(ValuationCallback.IsAnswer(answer));
        Assert.False(ValuationCallback.TryDecodeDialog(answer, out _, out _));
    }

    /// <summary>
    /// A keyboard wraps instead of running off the side of a phone, and the wrapping is
    /// the only thing about it that is not the caller's business.
    /// </summary>
    [Fact]
    public void A_keyboard_wraps_its_buttons_over_rows()
    {
        var keyboard = InlineKeyboard.Wrap(
            Grades.All.Select(g => new InlineButton(
                g.ToString(), ValuationCallback.Encode(DialogField.Grade, (int)g))),
            perRow: 3);

        Assert.Equal(new[] { 3, 3, 2 }, keyboard.Rows.Select(r => r.Count));
        Assert.Equal(Grades.All.Length, keyboard.Buttons.Count());
        Assert.Contains("\"inline_keyboard\"", keyboard.ToJson(), StringComparison.Ordinal);
    }
}
