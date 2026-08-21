using System.Globalization;
using System.Text;

namespace NCMarket.Core;

/// <summary>
/// The text of a deal alert. Plain text with no markup: it is read on a phone, in a chat
/// app, by someone who has to decide in a few seconds whether to open the game — so each
/// listing gets three lines that answer "what is it", "how much", "why is that cheap",
/// and nothing else.
/// <para>
/// It carries no assumption about where it is delivered, which is what lets the same text
/// go to Telegram today and somewhere else tomorrow. Numbers are formatted invariantly,
/// like everything else the project prints, so an alert read next to a CSV or a console
/// table shows the same figure written the same way.
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
        sb.Append("NC-Market — ")
          .Append(count.ToString("N0", Culture))
          .Append(count == 1 ? " nuova occasione su " : " nuove occasioni su ")
          .Append(query.Planet.Name)
          .Append(Scope(query))
          .Append('\n');

        sb.Append(Criteria(query)).Append('\n');

        foreach (var (deal, index) in deals.Take(max).Select((d, i) => (d, i + 1)))
        {
            sb.Append('\n');
            Append(sb, deal, index, names);
        }

        if (count > max)
        {
            sb.Append("\nAltre ")
              .Append((count - max).ToString("N0", Culture))
              .Append(" non elencate: l'elenco completo è quello del comando deals.\n");
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Deal deal, int index, NameProvider names)
    {
        var p = deal.Product;
        sb.Append(index.ToString(Culture)).Append(") ")
          .Append(ProductFormat.ItemDisplayName(p.ItemId, p.Grade, p.ItemSubType, names))
          .Append(" +").Append(p.Level.ToString(Culture))
          .Append(" — ").Append((EquipmentType)p.ItemSubType)
          .Append(" grado ").Append(p.Grade.ToString(Culture))
          .Append(", ").Append(p.OptionCountFromCombination.ToString(Culture))
          .Append(p.OptionCountFromCombination == 1 ? " opzione" : " opzioni")
          .Append(", CP ").Append(p.CombatPoint.ToString("N0", Culture))
          .Append('\n');

        sb.Append("   ").Append(p.Price.ToString("N2", Culture)).Append(" NCG — sconto ")
          .Append(deal.DiscountPercent.ToString("N1", Culture)).Append('%');

        if (deal.UsedCpMetric)
        {
            // Price/CP ratios are tiny: the inverse is shown, as in the console table.
            sb.Append(" su NCG/CP (")
              .Append(deal.PriceDiscountPercent.ToString("N1", Culture))
              .Append("% sul prezzo)\n")
              .Append("   ")
              .Append((1 / deal.PricePerCp!.Value).ToString("N0", Culture))
              .Append(" CP/NCG contro una mediana di ")
              .Append((1 / deal.Baseline.MedianPricePerCp!.Value).ToString("N0", Culture));
        }
        else
        {
            // No comparable combat point on the listing or in its bucket: the discount is
            // the plain price one, and saying so is the difference between a metric and a
            // number that looks like one.
            sb.Append(" sul prezzo (nessun CP confrontabile)\n")
              .Append("   Mediana ")
              .Append(deal.Baseline.MedianPrice.ToString("N2", Culture))
              .Append(" NCG");
        }

        sb.Append(" su ").Append(deal.Baseline.Samples.ToString("N0", Culture))
          .Append(deal.Baseline.Samples == 1 ? " inserzione\n" : " inserzioni\n");
    }

    /// <summary>The filters that were in force, so the alert says what it looked at.</summary>
    private static string Scope(DealQuery query)
    {
        var parts = new List<string>(2);
        if (query.Type is EquipmentType type)
        {
            parts.Add(type.ToString());
        }

        if (query.Grades is not null)
        {
            parts.Add("rarità " +
                string.Join(",", query.Grades.OrderBy(g => g).Select(g => (Grade)g)));
        }

        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
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
        var window = query.SinceUtc is DateTime since
            ? ", storico dal " + since.ToString("yyyy-MM-dd", Culture)
            : "";

        return $"Sconto ≥ {query.MinDiscountPercent.ToString("0.##", Culture)}% sulla mediana " +
               $"{population} per item+livello+opzioni " +
               $"(campioni ≥ {query.MinSamples.ToString(Culture)}{window}).\n";
    }
}
