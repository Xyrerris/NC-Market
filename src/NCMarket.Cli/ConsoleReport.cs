using System.Globalization;
using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Cli;

/// <summary>
/// Everything the CLI puts on screen. Kept apart from <c>Program</c> so that reading a
/// command tells you what it does — which options it accepts, which service it drives —
/// without wading through column widths, and so that the day a dashboard or a scheduled
/// job reuses the services in <c>NCMarket.Core</c>, none of this comes along.
/// </summary>
internal static class ConsoleReport
{
    /// <summary>
    /// Numbers and dates are formatted invariantly: the output is read by people and by
    /// scripts, and a thousands separator that changed with the machine's locale would
    /// break the second group.
    /// </summary>
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Describes the equipment filter of a command in its header line.</summary>
    public static string Scope(EquipmentType? type) =>
        type is null ? "tutti gli equipaggiamenti" : type.Value.ToString();

    // ------------------------------------------------------------------ fetch

    public static void Listings(
        Planet planet,
        EquipmentType type,
        string order,
        MarketProductsPage page,
        NameProvider names,
        NameProvider skillNames,
        bool details)
    {
        var totalInfo = page.TotalCount > 0
            ? $"{page.TotalCount} inserzioni totali"
            : $"prime {page.ItemProducts.Count} inserzioni";
        Console.WriteLine(
            $"Mercato {planet.Name} — {type} — {totalInfo}, ordinate per '{order}':");
        Console.WriteLine();

        if (page.ItemProducts.Count == 0)
        {
            Console.WriteLine("Nessuna inserzione trovata.");
            return;
        }

        if (details)
        {
            ProductDetails(page.ItemProducts, names, skillNames);
        }
        else
        {
            Products(page.ItemProducts, names, skillNames);
        }
    }

    private static void Products(
        IReadOnlyList<ItemProduct> products, NameProvider names, NameProvider skillNames) =>
        PrintTable(
            new[] { "#", "ItemId", "Nome", "Grado", "Lv", "CP", "Opz", "Elem", "Prezzo NCG", "Statistiche", "Skill", "Venditore" },
            new[] { true, true, false, true, true, true, true, false, true, false, false, false },
            products.Select((p, i) => new[]
            {
                (i + 1).ToString(Culture),
                p.ItemId.ToString(Culture),
                Truncate(ProductFormat.ItemDisplayName(p.ItemId, p.Grade, p.ItemSubType, names), 28),
                p.Grade.ToString(Culture),
                p.Level.ToString(Culture),
                p.CombatPoint.ToString("N0", Culture),
                p.OptionCountFromCombination.ToString(Culture),
                GameEnums.ElementalTypeName(p.ElementalType),
                p.Price.ToString("N2", Culture),
                Truncate(ProductFormat.StatsSummary(p.StatModels), 40),
                Truncate(ProductFormat.SkillsSummary(p.SkillModels, skillNames), 36),
                p.SellerAvatarAddress.Length >= 8 ? "0x" + p.SellerAvatarAddress[..8] : p.SellerAvatarAddress,
            }).ToList());

    private static void ProductDetails(
        IReadOnlyList<ItemProduct> products, NameProvider names, NameProvider skillNames)
    {
        for (var i = 0; i < products.Count; i++)
        {
            var p = products[i];
            Console.WriteLine(
                $"[{i + 1}] {ProductFormat.ItemDisplayName(p.ItemId, p.Grade, p.ItemSubType, names)} (item {p.ItemId}) — " +
                $"grado {p.Grade}, +{p.Level}, CP {p.CombatPoint.ToString("N0", Culture)}, " +
                $"{GameEnums.ElementalTypeName(p.ElementalType)}");
            Console.WriteLine(
                $"    Prezzo: {p.Price.ToString("N2", Culture)} NCG — " +
                $"opzioni {p.OptionCountFromCombination}, cristalli {p.Crystal.ToString("N0", Culture)}" +
                (p.ByCustomCraft ? ", custom craft" : ""));
            Console.WriteLine($"    Statistiche: {ProductFormat.StatsSummary(p.StatModels)}");
            foreach (var skill in p.SkillModels)
            {
                Console.WriteLine($"    Skill: {ProductFormat.SkillDetail(skill, skillNames)}");
            }

            Console.WriteLine($"    Venditore: 0x{p.SellerAvatarAddress} — prodotto {p.ProductId}");
            Console.WriteLine();
        }
    }

    // --------------------------------------------------------------- snapshot

    public static void SnapshotStored(SnapshotReport report, string dbPath)
    {
        Console.WriteLine();
        Console.WriteLine($"Totale: {report.Listings} inserzioni storicizzate in {dbPath}");
    }

    public static void Snapshots(IReadOnlyList<SnapshotInfo> snapshots)
    {
        PrintTable(
            new[] { "Id", "Pianeta", "Data (UTC)", "Tipi", "Prodotti", "Stato" },
            new[] { true, false, false, false, true, false },
            snapshots.Select(s => new[]
            {
                s.Id.ToString(Culture),
                s.Planet,
                s.TakenAtUtc.ToString("yyyy-MM-dd HH:mm:ss", Culture),
                s.ItemSubTypes,
                s.ProductCount.ToString("N0", Culture),
                s.IsComplete
                    ? "completo" + (s.IsTruncated ? $" (limite {s.MaxPerType})" : "")
                    : "PARZIALE",
            }).ToList());

        if (snapshots.Any(s => !s.IsComplete))
        {
            Console.WriteLine();
            Console.WriteLine(
                "Gli snapshot parziali sono catture interrotte a metà: restano consultabili " +
                "per id, ma stats, deals ed export non li scelgono come ultimo snapshot.");
        }

        if (snapshots.Any(s => s.IsTruncated))
        {
            Console.WriteLine();
            Console.WriteLine(
                "Gli snapshot con un limite (--max-per-type) non coprono l'intero listino: " +
                "la rilevazione delle vendite non li usa come prova che un'inserzione sia " +
                "sparita dal mercato.");
        }
    }

    // ---------------------------------------------------------------- history

    public static void History(
        int itemId, Planet planet, IReadOnlyList<ItemHistoryRow> rows, NameProvider names)
    {
        Console.WriteLine(
            $"Storico prezzi — {names.GetName(itemId)} (item {itemId}) su {planet.Name}:");
        Console.WriteLine();

        if (rows.Count == 0)
        {
            Console.WriteLine("Nessun dato: l'item non compare negli snapshot salvati.");
            return;
        }

        PrintTable(
            new[] { "Snapshot", "Data (UTC)", "Inserzioni", "Min NCG", "Media NCG", "Max NCG" },
            new[] { true, false, true, true, true, true },
            rows.Select(r => new[]
            {
                r.SnapshotId.ToString(Culture),
                r.TakenAtUtc.ToString("yyyy-MM-dd HH:mm:ss", Culture),
                r.Listings.ToString("N0", Culture),
                r.MinPrice.ToString("N2", Culture),
                r.AvgPrice.ToString("N2", Culture),
                r.MaxPrice.ToString("N2", Culture),
            }).ToList());
    }

    // ------------------------------------------------------------------ stats

    public static void Stats(
        long snapshotId,
        Planet planet,
        EquipmentType? type,
        IReadOnlyList<ItemStatsRow> rows,
        int top,
        NameProvider names)
    {
        Console.WriteLine(
            $"Statistiche snapshot #{snapshotId} ({planet.Name}, {Scope(type)}) — " +
            $"primi {Math.Min(top, rows.Count)} item per numero di inserzioni:");
        Console.WriteLine();

        PrintTable(
            new[] { "ItemId", "Nome", "Tipo", "Grado", "Ins.", "Min NCG", "Media NCG", "Max NCG", "CP max", "Lv max" },
            new[] { true, false, false, true, true, true, true, true, true, true },
            rows.Take(top).Select(r => new[]
            {
                r.ItemId.ToString(Culture),
                Truncate(ProductFormat.ItemDisplayName(r.ItemId, r.Grade, r.ItemSubType, names), 28),
                ((EquipmentType)r.ItemSubType).ToString(),
                r.Grade.ToString(Culture),
                r.Listings.ToString("N0", Culture),
                r.MinPrice.ToString("N2", Culture),
                r.AvgPrice.ToString("N2", Culture),
                r.MaxPrice.ToString("N2", Culture),
                r.MaxCombatPoint.ToString("N0", Culture),
                r.MaxLevel.ToString(Culture),
            }).ToList());
    }

    // ------------------------------------------------------------------ deals

    /// <summary>
    /// Why the comparison could not be made. Each case calls for a different answer: two
    /// of them are "capture more data", the third is "the heuristic has nothing to work
    /// with yet" and points at the fallback that does.
    /// </summary>
    public static void DealsUnavailable(DealReport report, Planet planet)
    {
        switch (report.Status)
        {
            case DealStatus.NoHistory:
                Console.WriteLine(
                    $"Nessuno storico prezzi per {planet.Name}. Esegui prima 'snapshot'.");
                break;

            case DealStatus.NoSales:
                Console.WriteLine(
                    "Nessuna vendita rilevata fra le " +
                    $"{report.Baselines.Outcomes.Total.ToString("N0", Culture)} " +
                    $"inserzioni storicizzate per {planet.Name}: perché la sparizione di " +
                    "un'inserzione sia osservabile serve un secondo snapshot completo e integrale " +
                    "(senza --max-per-type) dello stesso tipo. Usa '--baseline listed' per " +
                    "confrontare intanto con i prezzi richiesti.");
                break;

            case DealStatus.NoSnapshot:
                Console.WriteLine(
                    $"Nessuno snapshot completo per {planet.Name}. Esegui prima 'snapshot'.");
                break;
        }
    }

    public static void Deals(
        DealReport report, DealQuery query, int days, int top, NameProvider names)
    {
        var deals = report.Deals;
        var window = days > 0 ? $", ultimi {days} giorni" : "";
        var gradeScope = query.Grades is null
            ? ""
            : ", rarità " + string.Join(",", query.Grades.OrderBy(g => g).Select(g => (Grade)g));

        Console.WriteLine(PopulationSummary(report.Baselines, query.SaleMarginPercent));
        Console.WriteLine(
            $"Occasioni su {query.Planet.Name} — sconto ≥ {Percent(query.MinDiscountPercent)}% " +
            "sulla mediana storica del rapporto prezzo/CP per item+livello+opzioni " +
            $"(campioni ≥ {query.MinSamples}{window}{gradeScope}) — " +
            $"prime {Math.Min(top, deals.Count)} di {deals.Count}:");
        Console.WriteLine();

        // Price/CP ratios are tiny (often < 1e-4): the inverse CP-per-NCG is shown
        // instead, matching the market service's own crystal_per_price convention.
        PrintTable(
            new[]
            {
                "#", "ItemId", "Nome", "Tipo", "Gr", "Lv", "Opz", "CP", "Prezzo NCG",
                "CP/NCG", "Med CP/NCG", "Sconto%", "Sconto prezzo%", "Camp.",
            },
            new[]
            {
                true, true, false, false, true, true, true, true, true, true, true, true, true, true,
            },
            deals.Take(top).Select((d, i) => new[]
            {
                (i + 1).ToString(Culture),
                d.Product.ItemId.ToString(Culture),
                Truncate(
                    ProductFormat.ItemDisplayName(
                        d.Product.ItemId, d.Product.Grade, d.Product.ItemSubType, names), 28),
                ((EquipmentType)d.Product.ItemSubType).ToString(),
                d.Product.Grade.ToString(Culture),
                d.Product.Level.ToString(Culture),
                d.Product.OptionCountFromCombination.ToString(Culture),
                d.Product.CombatPoint.ToString("N0", Culture),
                d.Product.Price.ToString("N2", Culture),
                d.PricePerCp is double ppc ? (1 / ppc).ToString("N0", Culture) : "-",
                d.UsedCpMetric
                    ? (1 / d.Baseline.MedianPricePerCp!.Value).ToString("N0", Culture)
                    : "-",
                d.DiscountPercent.ToString("N1", Culture),
                d.PriceDiscountPercent.ToString("N1", Culture),
                d.Baseline.Samples.ToString("N0", Culture),
            }).ToList());
    }

    /// <summary>
    /// What the alert did. A run that found deals but announced none is not the same as
    /// one that found nothing, and on a scheduled job this line is the only place the
    /// difference shows: silence alone would read as a broken notifier.
    /// </summary>
    public static void Alert(AlertReport alert, string channel)
    {
        Console.WriteLine();
        Console.WriteLine(alert.Sent
            ? $"Notifica {channel} inviata: {alert.New.Count.ToString("N0", Culture)} " +
              $"occasioni nuove su {alert.Found.ToString("N0", Culture)}."
            : $"Nessuna notifica {channel}: le {alert.Found.ToString("N0", Culture)} " +
              "occasioni trovate erano già state segnalate.");
    }

    /// <summary>
    /// States which population the medians were measured on: the difference between
    /// measuring what sellers ask and what the market pays.
    /// </summary>
    private static string PopulationSummary(BaselineSet set, double saleMargin)
    {
        var o = set.Outcomes;
        string N(int value) => value.ToString("N0", Culture);

        return set.Population == BaselinePopulation.Sold
            ? $"Riferimento: {N(o.LikelySold)} inserzioni concluse a un prezzo compatibile " +
              $"con una vendita, su {N(o.Total)} osservate ({N(o.Open)} ancora in vendita, " +
              $"{N(o.LikelyWithdrawn)} sparite oltre il +{Percent(saleMargin)}% sulla mediana del " +
              "proprio bucket e quindi considerate ritiri)."
            : $"Riferimento: tutte le {N(o.Total)} inserzioni osservate — sono prezzi " +
              $"richiesti, non di vendita ({N(o.LikelySold)} risultano concluse a un prezzo " +
              "compatibile con una vendita: '--baseline sold' usa soltanto quelle).";
    }

    // ------------------------------------------------------------------ prune

    public static void Prune(
        string dbPath, int days, DateTime cutoffUtc, bool dryRun, PruneResult result)
    {
        Console.WriteLine(
            $"Retention su {dbPath}: rimozione delle inserzioni non più viste " +
            $"da {days} giorni (prima del {cutoffUtc.ToString("yyyy-MM-dd HH:mm", Culture)} UTC)" +
            (dryRun ? " — prova, nessuna modifica" : "") + ":");
        Console.WriteLine();

        var invariant = dryRun ? "da rimuovere" : null;
        Console.WriteLine($"  Inserzioni {invariant ?? "rimosse"}: {result.ListingsRemoved.ToString("N0", Culture)}");
        Console.WriteLine($"  Avvistamenti {invariant ?? "rimossi"}: {result.SightingsRemoved.ToString("N0", Culture)}");
        Console.WriteLine($"  Snapshot {invariant ?? "rimossi"}: {result.SnapshotsRemoved.ToString("N0", Culture)}");
        Console.WriteLine($"  Segnalazioni {invariant ?? "rimosse"}: {result.NotificationsRemoved.ToString("N0", Culture)}");
        if (!dryRun)
        {
            Console.WriteLine(
                $"  Dimensione database: {FormatBytes(result.BytesBefore)} -> {FormatBytes(result.BytesAfter)}");
        }
    }

    // ----------------------------------------------------------------- comuni

    private static void PrintTable(string[] headers, bool[] rightAlign, List<string[]> rows)
    {
        var widths = headers.Select(h => h.Length).ToArray();
        foreach (var row in rows)
        {
            for (var c = 0; c < widths.Length; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        static string Cell(string text, int width, bool right) =>
            right ? text.PadLeft(width) : text.PadRight(width);

        Console.WriteLine(string.Join("  ", headers.Select((h, c) => Cell(h, widths[c], rightAlign[c]))));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            Console.WriteLine(string.Join("  ", row.Select((v, c) => Cell(v, widths[c], rightAlign[c]))));
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Percentages are whole numbers in practice: keep decimals only if there are any.</summary>
    private static string Percent(double value) => value.ToString("0.##", Culture);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("N2", Culture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("N1", Culture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("N0", Culture) + " KB",
        _ => bytes.ToString("N0", Culture) + " B",
    };
}
