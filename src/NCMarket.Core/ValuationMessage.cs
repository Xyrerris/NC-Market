using System.Globalization;
using System.Text;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// The words a valuation is said in. For now that is one line — the echo of how a
/// message was read — which belongs here and not to the presentation of the answer: it
/// is the cheapest defence a free-text parser has, and the answer is not worth showing
/// until it is clear which piece it is about.
/// <para>
/// A misread message does not produce a visible failure. It produces a valuation of a
/// different piece, correct in every respect except the one that matters, and nothing in
/// its shape says so. One line of echo turns that into something the reader catches at a
/// glance — the same principle as <c>deals --dicount 30</c> being an error instead of a
/// filter that silently does not apply.
/// </para>
/// <para>
/// Two things the echo says differently from how they may have been written, and both on
/// purpose:
/// </para>
/// <list type="bullet">
/// <item>the type is named by its <see cref="EquipmentType"/> — <c>Weapon</c>, the name
/// the rest of the project prints — and not by the alias that was typed. Echoing
/// <c>Sword</c> back would reflect the writer's own word at them and confirm nothing;
/// worse, it would suggest the item was identified, which is exactly what a valuation
/// without an item id cannot do;</item>
/// <item>numbers are formatted invariantly, like every other figure the project prints,
/// so a combat point read here and the same one read in a deal alert or a CSV are written
/// the same way. The parser accepts the game's own <c>151.216.255</c> on the way in; what
/// comes back out is this project's spelling of it.</item>
/// </list>
/// <para>
/// The text is plain, not <see cref="MarkdownV2"/>. Nothing written by the sender
/// survives into it — every part comes from a parsed enum or a number — so a caller
/// sending it to Telegram can hand the whole line to <see cref="MarkdownV2.Escape"/> in
/// one call, which is safe precisely because there is no entity in it to break.
/// </para>
/// </summary>
public static class ValuationMessage
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// How <paramref name="query"/> was understood, in one line. Every field of the key
    /// appears, including the ones that were defaulted rather than written: a level
    /// nobody typed still shows as <c>+0</c>, because "I assumed +0" is the reading most
    /// worth contradicting.
    /// </summary>
    public static string Echo(ValuationQuery query)
    {
        var key = query.Key;
        var sb = new StringBuilder("Ho letto: ")
            .Append(GradeName(key.Grade))
            .Append(' ')
            .Append(key.Type)
            .Append(" · ")
            .Append(Elementals.Name(key.Element))
            .Append(" · +")
            .Append(key.Level.ToString(Culture))
            .Append(" · ")
            .Append(Options(key))
            .Append(key.HasSkill ? " · con skill" : " · senza skill");

        // Custom craft shows only when it is on. It is a rarity, its default is no, and a
        // line that carries every negative answer stops being read.
        if (key.ByCustomCraft)
        {
            sb.Append(" · custom craft");
        }

        if (query.CombatPoint is int cp)
        {
            sb.Append(" · CP ").Append(cp.ToString("N0", Culture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The options, named and counted. A piece with none is a piece the parser was told
    /// nothing about, and saying so is what lets the reader notice that the four lines
    /// they typed did not arrive.
    /// </summary>
    private static string Options(ValuationKey key) => key.OptionStats.Count switch
    {
        0 => "senza opzioni",
        1 => "opzione " + GameEnums.StatTypeName(key.OptionStats[0]),
        _ => "opzioni " + string.Join(", ", key.OptionStats.Select(GameEnums.StatTypeName)),
    };

    /// <summary>
    /// The rarity by name, falling back to the number for a grade lib9c grew after this
    /// enum was written.
    /// </summary>
    private static string GradeName(int grade) =>
        Enum.IsDefined(typeof(Grade), grade)
            ? ((Grade)grade).ToString()
            : "grado " + grade.ToString(Culture);
}
