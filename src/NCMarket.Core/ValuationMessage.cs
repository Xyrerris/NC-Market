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
    /// The valuation itself: a range, what it was measured on, and what had to be given
    /// up to measure it. There is no single price anywhere in it, because
    /// <see cref="ValuationResult"/> does not carry one — inside a bucket the price
    /// follows neither the combat point nor the option values, so a number would be a
    /// precision nobody measured.
    /// <para>
    /// Every line after the range is there to keep the range from being read as more than
    /// it is: how many listings are behind it, whether those are sales or asking prices,
    /// how far the bucket had to be widened. With a database of two snapshots the honest
    /// answer is nearly always "this is what people ask", and a bot that said so only in
    /// its documentation would be a bot that never says so.
    /// </para>
    /// </summary>
    public static string Answer(ValuationQuery query, ValuationResult result)
    {
        if (result.Status != ValuationStatus.Ok || result.Prices is null)
        {
            return Insufficient(query, result);
        }

        var prices = result.Prices;
        var sb = new StringBuilder("💰 ")
            .Append(Ncg(prices.Min))
            .Append(" – ")
            .Append(Ncg(prices.Max))
            .Append(" · mediana ")
            .Append(Ncg(prices.Median))
            .Append('\n');

        sb.Append("📊 ")
          .Append(result.Comparables.ToString("N0", Culture))
          .Append(result.Comparables == 1 ? " comparabile · " : " comparabili · ")
          .Append(PopulationName(result.Population))
          .Append(" · ")
          .Append(query.Planet.Name)
          .Append(Window(result));

        if (result.Step != ValuationStep.Exact)
        {
            // The key in the result is the piece as it was described, not the bucket that
            // was measured: only the step knows what was given up, so only the step can
            // say it.
            sb.Append("\n🔎 Bucket allargato: ").Append(result.StepDeclaration);
        }

        if (result.CpPercentile is int percentile)
        {
            sb.Append("\n📈 Il CP del pezzo sta nel ")
              .Append(percentile.ToString(Culture))
              .Append("° percentile del gruppo");
        }

        if (result.Population == BaselinePopulation.Listed)
        {
            sb.Append("\n⚠️ ")
              .Append(result.PopulationFallback
                  ? "Vendite osservate troppo poche: è quanto si chiede, non quanto si paga"
                  : "Prezzi richiesti: è quanto si chiede, non quanto si paga");
        }

        return sb.ToString();
    }

    /// <summary>
    /// What is said when the whole ladder was not enough. It reports what the widest
    /// bucket did find, because "two listings" and "none at all" are different answers —
    /// and neither of them is a range: one invented on two samples reads exactly like one
    /// measured on fifty.
    /// </summary>
    private static string Insufficient(ValuationQuery query, ValuationResult result) =>
        "🤷 Non ho abbastanza dati per questo pezzo. Anche allargando fino alla stima " +
        "larga (stesso tipo, stessa rarità, stessa presenza di skill) ho trovato " +
        result.Comparables.ToString("N0", Culture) +
        (result.Comparables == 1 ? " inserzione" : " inserzioni") +
        " sulle " + query.MinSamples.ToString("N0", Culture) + " che servono.\n" +
        "La risposta arriva da sé man mano che lo storico si allunga: il meccanismo c'è, " +
        "i campioni no.";

    /// <summary>
    /// When the comparables were last seen. Those dates are most of what says whether a
    /// range can be trusted, and they are written the way the rest of the project writes
    /// them.
    /// </summary>
    private static string Window(ValuationResult result)
    {
        if (result.OldestSeenUtc is not DateTime oldest
            || result.NewestSeenUtc is not DateTime newest)
        {
            return "";
        }

        var from = oldest.ToString("yyyy-MM-dd", Culture);
        var to = newest.ToString("yyyy-MM-dd", Culture);
        return from == to ? $" · visti il {from}" : $" · visti dal {from} al {to}";
    }

    /// <summary>
    /// What the range was measured on, in the words that keep the difference visible: a
    /// sale and an asking price are not the same claim about a piece.
    /// </summary>
    private static string PopulationName(BaselinePopulation population) =>
        population == BaselinePopulation.Sold ? "vendite stimate" : "prezzi richiesti";

    private static string Ncg(double price) => price.ToString("N2", Culture) + " NCG";

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
