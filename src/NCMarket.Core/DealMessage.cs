using System.Globalization;
using System.Text;

namespace NCMarket.Core;

/// <summary>
/// The text of a deal alert. It is read on a phone, in a chat app, by someone who has to
/// decide in a few seconds whether to open the game — so each listing gets four lines
/// that answer "what is it", "how much", "why is that cheap", and nothing else, with the
/// figures set apart from the words around them.
/// <para>
/// That setting apart is <see cref="MarkdownV2"/>, which Telegram parses: every value
/// coming from outside this class — an item name, a planet, a number — goes through
/// <see cref="MarkdownV2.Escape"/> or <see cref="MarkdownV2.Code"/>, and the fixed
/// Italian text is written already escaped. Nothing is interpolated raw: an unescaped
/// character does not produce an ugly message, it produces no message at all.
/// </para>
/// <para>
/// No entity ever spans a newline. That is what makes it safe for
/// <see cref="TelegramNotifier.Split"/> to cut a long alert between its lines: each part
/// is parseable on its own, where a bold run left open at a cut would have the part
/// refused.
/// </para>
/// <para>
/// Numbers are formatted invariantly, like everything else the project prints, so an
/// alert read next to a CSV or a console table shows the same figure written the same
/// way.
/// </para>
/// </summary>
public static class DealMessage
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Renders <paramref name="deals"/> — already filtered down to the ones worth
    /// announcing — as the body of one alert, listing at most <paramref name="max"/> of
    /// them and saying how many were left out.
    /// </summary>
    public static string Format(
        IReadOnlyList<Deal> deals, DealQuery query, NameProvider names, int max)
    {
        var sb = new StringBuilder();
        var count = deals.Count;

        // A count formatted "N0" is digits and thousands separators, neither of which
        // MarkdownV2 gives a meaning to; the planet is a name from elsewhere, so it is
        // not written on trust.
        sb.Append("🏷️ *NC\\-Market* — ")
          .Append(count.ToString("N0", Culture))
          .Append(count == 1 ? " nuova occasione su " : " nuove occasioni su ")
          .Append(MarkdownV2.Code(query.Planet.Name))
          .Append('\n');

        var scope = Scope(query);
        if (scope.Length > 0)
        {
            sb.Append(scope).Append('\n');
        }

        sb.Append(Criteria(query)).Append('\n');

        foreach (var (deal, index) in deals.Take(max).Select((d, i) => (d, i + 1)))
        {
            sb.Append('\n');
            Append(sb, deal, index, names);
        }

        if (count > max)
        {
            sb.Append("\n➕ Altre ")
              .Append(MarkdownV2.Code((count - max).ToString("N0", Culture)))
              .Append(" non elencate: l'elenco completo è quello del comando ")
              .Append(MarkdownV2.Code("deals"))
              .Append("\\.\n");
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Deal deal, int index, NameProvider names)
    {
        var p = deal.Product;

        // Index and name in one bold run, numbered rather than in keycap emoji: those
        // stop at ten, '--top' defaults to thirty, and a list that changes shape halfway
        // is harder to read than one that never does.
        sb.Append('*').Append(index.ToString(Culture)).Append("\\. ")
          .Append(MarkdownV2.Escape(
              ProductFormat.ItemDisplayName(p.ItemId, p.Grade, p.ItemSubType, names)))
          .Append(" \\+").Append(p.Level.ToString(Culture))
          .Append("*\n");

        sb.Append(MarkdownV2.Escape(((EquipmentType)p.ItemSubType).ToString()))
          .Append(" · grado ").Append(p.Grade.ToString(Culture))
          .Append(" · ").Append(p.OptionCountFromCombination.ToString(Culture))
          .Append(p.OptionCountFromCombination == 1 ? " opzione" : " opzioni")
          .Append(" · CP ").Append(MarkdownV2.Code(p.CombatPoint.ToString("N0", Culture)))
          .Append('\n');

        sb.Append("💰 ")
          .Append(MarkdownV2.Code(p.Price.ToString("N2", Culture) + " NCG"))
          .Append(" — sconto ")
          .Append(MarkdownV2.Code(deal.DiscountPercent.ToString("N1", Culture) + "%"));

        if (deal.UsedCpMetric)
        {
            // Price/CP ratios are tiny: the inverse is shown, as in the console table.
            sb.Append(" su NCG/CP \\(")
              .Append(MarkdownV2.Code(
                  deal.PriceDiscountPercent.ToString("N1", Culture) + "%"))
              .Append(" sul prezzo\\)\n")
              .Append("📊 ")
              .Append(MarkdownV2.Code((1 / deal.PricePerCp!.Value).ToString("N0", Culture)))
              .Append(" CP/NCG vs mediana ")
              .Append(MarkdownV2.Code(
                  (1 / deal.Baseline.MedianPricePerCp!.Value).ToString("N0", Culture)));
        }
        else
        {
            // No comparable combat point on the listing or in its bucket: the discount is
            // the plain price one, and saying so is the difference between a metric and a
            // number that looks like one.
            sb.Append(" sul prezzo \\(nessun CP confrontabile\\)\n")
              .Append("📊 mediana ")
              .Append(MarkdownV2.Code(
                  deal.Baseline.MedianPrice.ToString("N2", Culture) + " NCG"));
        }

        sb.Append(" su ")
          .Append(MarkdownV2.Code(deal.Baseline.Samples.ToString("N0", Culture)))
          .Append(deal.Baseline.Samples == 1 ? " inserzione\n" : " inserzioni\n");
    }

    /// <summary>
    /// The filters that were in force, so the alert says what it looked at. A line of its
    /// own, and none at all when nothing was filtered: an unfiltered search has nothing
    /// to declare, and an empty pair of parentheses in the heading would say it anyway.
    /// </summary>
    private static string Scope(DealQuery query)
    {
        var parts = new List<string>(3);
        if (query.Type is EquipmentType type)
        {
            parts.Add(MarkdownV2.Escape(type.ToString()));
        }

        if (query.Grades is not null)
        {
            parts.Add("rarità " + MarkdownV2.Escape(
                string.Join(",", query.Grades.OrderBy(g => g).Select(g => (Grade)g))));
        }

        if (query.SinceUtc is DateTime since)
        {
            parts.Add("storico dal " +
                MarkdownV2.Escape(since.ToString("yyyy-MM-dd", Culture)));
        }

        return parts.Count == 0 ? "" : "🔎 " + string.Join(" · ", parts);
    }

    /// <summary>
    /// What "cheap" meant for this alert. Without it a discount is a number without a
    /// denominator: the same listing is a 40% bargain against asking prices and nothing
    /// at all against concluded ones.
    /// </summary>
    private static string Criteria(DealQuery query)
    {
        var population = query.Population == BaselinePopulation.Sold
            ? "delle inserzioni concluse"
            : "dei prezzi richiesti";

        return "_Sconto ≥ " +
               MarkdownV2.Escape(query.MinDiscountPercent.ToString("0.##", Culture)) +
               "% sulla mediana " + population +
               " per item \\+ livello \\+ opzioni \\(campioni ≥ " +
               query.MinSamples.ToString(Culture) + "\\)_";
    }
}
