using System.Text;
using System.Text.Json;

namespace NCMarket.Core;

/// <summary>
/// One button under a message. What comes back when it is pressed is <see cref="Data"/>
/// and never <see cref="Text"/>: the label is written for whoever reads it and can be
/// changed at any time, the data is the vocabulary the bot answers in.
/// <para>
/// Telegram caps <c>callback_data</c> at <see cref="MaxDataBytes"/> bytes and refuses the
/// whole message when it is longer — not the button, the message. So the limit is checked
/// where the button is built: a keyboard that cannot be sent has to fail on the line that
/// composed it, and not as a 400 on a valuation somebody was waiting for.
/// </para>
/// </summary>
public sealed record InlineButton(string Text, string Data)
{
    /// <summary>What Telegram accepts as <c>callback_data</c>, in bytes of UTF-8.</summary>
    public const int MaxDataBytes = 64;

    public string Data { get; init; } = Checked(Data);

    private static string Checked(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            throw new ArgumentException(
                "Un bottone senza dati non si distingue da un altro una volta premuto.",
                nameof(data));
        }

        var bytes = Encoding.UTF8.GetByteCount(data);
        return bytes <= MaxDataBytes
            ? data
            : throw new ArgumentException(
                $"callback_data di {bytes} byte ('{data}'): Telegram ne accetta " +
                $"{MaxDataBytes}, e rifiuta l'intero messaggio quando sono di più.",
                nameof(data));
    }
}

/// <summary>
/// The keyboard shown under a message: rows of <see cref="InlineButton"/>, which is how
/// the four enumerable fields of a valuation stop being something to spell correctly.
/// <para>
/// It is a value, not a Telegram call: what makes it travel is
/// <see cref="IReplyChannel.SendAsync"/>, and what makes it come back is a
/// <see cref="TelegramUpdate.CallbackData"/>. A test can therefore read the buttons it
/// would have shown without a network in the middle.
/// </para>
/// </summary>
public sealed record InlineKeyboard(IReadOnlyList<IReadOnlyList<InlineButton>> Rows)
{
    /// <summary>Every button, in reading order.</summary>
    public IEnumerable<InlineButton> Buttons => Rows.SelectMany(row => row);

    /// <summary>One button per row, for labels a phone would otherwise cut in half.</summary>
    public static InlineKeyboard Column(params InlineButton[] buttons) =>
        new(buttons.Select(b => (IReadOnlyList<InlineButton>)new[] { b }).ToList());

    /// <summary>
    /// The buttons wrapped over rows of at most <paramref name="perRow"/>. Eight rarities
    /// on one row would each be three characters wide on a phone; the wrapping is what
    /// keeps a keyboard readable rather than merely present.
    /// </summary>
    public static InlineKeyboard Wrap(IEnumerable<InlineButton> buttons, int perRow) =>
        new(buttons
            .Select((button, index) => (button, index))
            .GroupBy(pair => pair.index / perRow)
            .Select(group => (IReadOnlyList<InlineButton>)group.Select(p => p.button).ToList())
            .ToList());

    /// <summary>
    /// The <c>reply_markup</c> Telegram expects. Serialized rather than concatenated: a
    /// label is written in Italian and would otherwise need its own escaping rules on top
    /// of the ones the message text already has.
    /// </summary>
    public string ToJson() =>
        JsonSerializer.Serialize(new
        {
            inline_keyboard = Rows.Select(row =>
                row.Select(b => new { text = b.Text, callback_data = b.Data })),
        });
}
