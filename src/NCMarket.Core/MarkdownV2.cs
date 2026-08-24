using System.Text;

namespace NCMarket.Core;

/// <summary>
/// The escaping rules of MarkdownV2, the markup Telegram parses in a message.
/// <para>
/// It exists because the alternative is remembering. Most of an alert is Italian prose
/// written here, but the names it puts in bold come from the game's localization file
/// (<see cref="ProductFormat.ItemDisplayName"/>) and are whatever the game decided to
/// call an item: a single unescaped bracket in one of them does not misformat the
/// message, it makes Telegram refuse it — and a refused alert is an occasion nobody
/// hears about.
/// </para>
/// </summary>
public static class MarkdownV2
{
    /// <summary>
    /// The characters MarkdownV2 gives a meaning to outside an entity. Telegram's rule
    /// is blunt on purpose: each of them must be preceded by a backslash wherever it
    /// appears as text, whether or not it could be read as markup there.
    /// </summary>
    private const string Special = @"_*[]()~`>#+-=|{}.!\";

    /// <summary>Escapes <paramref name="text"/> for use as ordinary text.</summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (Special.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Wraps <paramref name="text"/> in a code span — the monospace box the alerts put
    /// every figure in. Inside one only the backtick that would close it and the
    /// backslash that escapes need escaping, which is why a price reads <c>6.00</c>
    /// here and would have to read <c>6\.00</c> as ordinary text.
    /// </summary>
    public static string Code(string text)
    {
        var sb = new StringBuilder(text.Length + 4).Append('`');
        foreach (var c in text)
        {
            if (c is '`' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('`').ToString();
    }
}
