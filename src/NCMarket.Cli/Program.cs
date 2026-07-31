using System.Globalization;
using System.Text;
using NCMarket.Core;
using NCMarket.Core.Models;

Console.OutputEncoding = Encoding.UTF8;
var culture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var verb = args[0].ToLowerInvariant();
var options = ParseOptions(args.Skip(1).ToArray());

try
{
    return verb switch
    {
        "fetch" => await FetchAsync(),
        "snapshot" => await SnapshotAsync(),
        "snapshots" => ListSnapshots(),
        "history" => History(),
        "stats" => Stats(),
        "deals" => await DealsAsync(),
        "export" => await ExportAsync(),
        "prune" => Prune(),
        _ => Unknown(),
    };
}
catch (Exception e)
{
    Console.Error.WriteLine($"Errore: {e.Message}");
    if (e.InnerException is not null)
    {
        Console.Error.WriteLine($"  Dettaglio: {e.InnerException.Message}");
    }

    return 1;
}

// ---------------------------------------------------------------- comandi

async Task<int> FetchAsync()
{
    if (!TryGetType(required: true, out var type))
    {
        return 2;
    }

    var planet = GetPlanet();
    var order = GetOrder();
    var limit = GetInt("limit", 20);
    var names = await LoadItemNamesAsync();
    var skillNames = await LoadSkillNamesAsync();

    using var client = new MarketClient(planet);
    var page = await client.GetProductsPageAsync(type!.Value, limit, GetInt("offset", 0), order);

    var totalInfo = page.TotalCount > 0
        ? $"{page.TotalCount} inserzioni totali"
        : $"prime {page.ItemProducts.Count} inserzioni";
    Console.WriteLine($"Mercato {planet.Name} — {type.Value} — {totalInfo}, ordinate per '{order}':");
    Console.WriteLine();

    if (options.ContainsKey("details"))
    {
        PrintProductDetails(page.ItemProducts, names, skillNames);
    }
    else
    {
        PrintProducts(page.ItemProducts, names, skillNames);
    }

    return 0;
}

async Task<int> SnapshotAsync()
{
    var planet = GetPlanet();
    var maxPerType = options.TryGetValue("max-per-type", out var maxRaw)
        ? int.Parse(maxRaw, culture)
        : (int?)null;

    EquipmentType[] types;
    if (options.TryGetValue("types", out var typesRaw))
    {
        var parsed = new List<EquipmentType>();
        foreach (var token in typesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!EquipmentTypes.TryParse(token, out var t))
            {
                Console.Error.WriteLine($"Tipo equipaggiamento non valido: '{token}'.");
                return 2;
            }

            parsed.Add(t);
        }

        types = parsed.Distinct().ToArray();
    }
    else
    {
        types = EquipmentTypes.All;
    }

    using var db = OpenDb();
    using var client = new MarketClient(planet);

    var takenAt = DateTime.UtcNow;
    var snapshotId = db.CreateSnapshot(planet.Name, types, takenAt);
    Console.WriteLine(
        $"Snapshot #{snapshotId} — pianeta {planet.Name}, {takenAt.ToString("u", culture)}");

    var grandTotal = 0;
    foreach (var type in types)
    {
        Console.Write($"  {type,-9}: recupero...");
        var products = await client.GetAllProductsAsync(
            type,
            maxItems: maxPerType,
            progress: (done, total) => Console.Write(
                $"\r  {type,-9}: {done}{(total > 0 ? "/" + total : "")} scaricate...      "));
        var saved = db.AddProducts(snapshotId, products);
        grandTotal += saved;
        Console.WriteLine($"\r  {type,-9}: salvate {saved} inserzioni      ");
    }

    db.FinalizeSnapshot(snapshotId);
    Console.WriteLine();
    Console.WriteLine($"Totale: {grandTotal} inserzioni storicizzate in {db.DbPath}");
    return 0;
}

int ListSnapshots()
{
    using var db = OpenDb();
    var planetFilter = options.TryGetValue("planet", out var p) ? p.ToLowerInvariant() : null;
    var snapshots = db.GetSnapshots(planetFilter);
    if (snapshots.Count == 0)
    {
        Console.WriteLine("Nessuno snapshot salvato. Esegui prima il comando 'snapshot'.");
        return 0;
    }

    PrintTable(
        new[] { "Id", "Pianeta", "Data (UTC)", "Tipi", "Prodotti" },
        new[] { true, false, false, false, true },
        snapshots.Select(s => new[]
        {
            s.Id.ToString(culture),
            s.Planet,
            s.TakenAtUtc.ToString("yyyy-MM-dd HH:mm:ss", culture),
            s.ItemSubTypes,
            s.ProductCount.ToString("N0", culture),
        }).ToList());
    return 0;
}

int History()
{
    if (!options.TryGetValue("item", out var itemRaw) ||
        !int.TryParse(itemRaw, NumberStyles.Integer, culture, out var itemId))
    {
        Console.Error.WriteLine("Specifica l'item: history --item <itemId> (es. --item 10152001)");
        return 2;
    }

    var planet = GetPlanet();
    using var db = OpenDb();
    var rows = db.GetItemHistory(itemId, planet.Name);

    var names = LoadItemNamesAsync().GetAwaiter().GetResult();
    Console.WriteLine($"Storico prezzi — {names.GetName(itemId)} (item {itemId}) su {planet.Name}:");
    Console.WriteLine();

    if (rows.Count == 0)
    {
        Console.WriteLine("Nessun dato: l'item non compare negli snapshot salvati.");
        return 0;
    }

    PrintTable(
        new[] { "Snapshot", "Data (UTC)", "Inserzioni", "Min NCG", "Media NCG", "Max NCG" },
        new[] { true, false, true, true, true, true },
        rows.Select(r => new[]
        {
            r.SnapshotId.ToString(culture),
            r.TakenAtUtc.ToString("yyyy-MM-dd HH:mm:ss", culture),
            r.Listings.ToString("N0", culture),
            r.MinPrice.ToString("N2", culture),
            r.AvgPrice.ToString("N2", culture),
            r.MaxPrice.ToString("N2", culture),
        }).ToList());
    return 0;
}

int Stats()
{
    var planet = GetPlanet();
    if (!TryGetType(required: false, out var type))
    {
        return 2;
    }

    var top = GetInt("top", 30);

    using var db = OpenDb();
    var snapshotId = db.GetLatestSnapshotId(planet.Name);
    if (snapshotId is null)
    {
        Console.WriteLine($"Nessuno snapshot per {planet.Name}. Esegui prima 'snapshot'.");
        return 0;
    }

    var rows = db.GetSnapshotStats(snapshotId.Value, type);
    var names = LoadItemNamesAsync().GetAwaiter().GetResult();

    var scope = type is null ? "tutti gli equipaggiamenti" : type.Value.ToString();
    Console.WriteLine(
        $"Statistiche snapshot #{snapshotId} ({planet.Name}, {scope}) — " +
        $"primi {Math.Min(top, rows.Count)} item per numero di inserzioni:");
    Console.WriteLine();

    PrintTable(
        new[] { "ItemId", "Nome", "Tipo", "Grado", "Ins.", "Min NCG", "Media NCG", "Max NCG", "CP max", "Lv max" },
        new[] { true, false, false, true, true, true, true, true, true, true },
        rows.Take(top).Select(r => new[]
        {
            r.ItemId.ToString(culture),
            Truncate(ProductFormat.ItemDisplayName(r.ItemId, r.Grade, r.ItemSubType, names), 28),
            ((EquipmentType)r.ItemSubType).ToString(),
            r.Grade.ToString(culture),
            r.Listings.ToString("N0", culture),
            r.MinPrice.ToString("N2", culture),
            r.AvgPrice.ToString("N2", culture),
            r.MaxPrice.ToString("N2", culture),
            r.MaxCombatPoint.ToString("N0", culture),
            r.MaxLevel.ToString(culture),
        }).ToList());
    return 0;
}

async Task<int> DealsAsync()
{
    if (!TryGetType(required: false, out var type))
    {
        return 2;
    }

    var planet = GetPlanet();
    var discount = GetInt("discount", 25);
    var minSamples = GetInt("min-samples", 5);
    var days = GetInt("days", 0);
    var sinceUtc = days > 0 ? DateTime.UtcNow.AddDays(-days) : (DateTime?)null;
    var top = GetInt("top", 30);
    var maxPerType = options.TryGetValue("max-per-type", out var maxRaw)
        ? int.Parse(maxRaw, culture)
        : (int?)null;

    HashSet<int>? grades = null;
    if (options.TryGetValue("grade", out var gradesRaw))
    {
        grades = new HashSet<int>();
        foreach (var token in gradesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Grades.TryParse(token, out var grade))
            {
                Console.Error.WriteLine(
                    $"Rarità non valida: '{token}'. Valori ammessi: 1-8 oppure " +
                    "normal, rare, epic, unique, legendary, divinity, mythic, transcendent.");
                return 2;
            }

            grades.Add((int)grade);
        }
    }

    using var db = OpenDb();
    var baselines = db.GetPriceBaselines(planet.Name, type, sinceUtc);
    if (baselines.Count == 0)
    {
        Console.WriteLine($"Nessuno storico prezzi per {planet.Name}. Esegui prima 'snapshot'.");
        return 0;
    }

    List<ItemProduct> current;
    if (options.ContainsKey("from-snapshot"))
    {
        var snapshotId = db.GetLatestSnapshotId(planet.Name);
        if (snapshotId is null)
        {
            Console.WriteLine($"Nessuno snapshot per {planet.Name}. Esegui prima 'snapshot'.");
            return 0;
        }

        current = db.GetSnapshotProducts(snapshotId.Value, type).ToList();
        Console.WriteLine($"Offerte correnti: snapshot #{snapshotId} ({planet.Name})");
    }
    else
    {
        var types = type is null ? EquipmentTypes.All : new[] { type.Value };
        using var client = new MarketClient(planet);
        current = new List<ItemProduct>();
        Console.WriteLine($"Offerte correnti: mercato live ({planet.Name})");
        foreach (var t in types)
        {
            Console.Write($"  {t,-9}: recupero...");
            var products = await client.GetAllProductsAsync(
                t,
                maxItems: maxPerType,
                progress: (done, total) => Console.Write(
                    $"\r  {t,-9}: {done}{(total > 0 ? "/" + total : "")} scaricate...      "));
            current.AddRange(products);
            Console.WriteLine($"\r  {t,-9}: {products.Count} inserzioni      ");
        }
    }

    if (grades is not null)
    {
        current = current.Where(p => grades.Contains(p.Grade)).ToList();
    }

    var deals = DealFinder.FindDeals(current, baselines, discount, minSamples);
    Console.WriteLine();
    if (deals.Count == 0)
    {
        Console.WriteLine("Nessuna occasione trovata con i criteri correnti.");
        return 0;
    }

    var names = await LoadItemNamesAsync();
    var window = days > 0 ? $", ultimi {days} giorni" : "";
    var gradeScope = grades is null
        ? ""
        : ", rarità " + string.Join(",", grades.OrderBy(g => g).Select(g => (Grade)g));
    Console.WriteLine(
        $"Occasioni su {planet.Name} — sconto ≥ {discount}% sulla mediana storica del " +
        $"rapporto prezzo/CP per item+livello (campioni ≥ {minSamples}{window}{gradeScope}) — " +
        $"prime {Math.Min(top, deals.Count)} di {deals.Count}:");
    Console.WriteLine();

    // Price/CP ratios are tiny (often < 1e-4): the inverse CP-per-NCG is shown
    // instead, matching the market service's own crystal_per_price convention.
    PrintTable(
        new[]
        {
            "#", "ItemId", "Nome", "Tipo", "Gr", "Lv", "CP", "Prezzo NCG",
            "CP/NCG", "Med CP/NCG", "Sconto%", "Sconto prezzo%", "Camp.",
        },
        new[] { true, true, false, false, true, true, true, true, true, true, true, true, true },
        deals.Take(top).Select((d, i) => new[]
        {
            (i + 1).ToString(culture),
            d.Product.ItemId.ToString(culture),
            Truncate(
                ProductFormat.ItemDisplayName(
                    d.Product.ItemId, d.Product.Grade, d.Product.ItemSubType, names), 28),
            ((EquipmentType)d.Product.ItemSubType).ToString(),
            d.Product.Grade.ToString(culture),
            d.Product.Level.ToString(culture),
            d.Product.CombatPoint.ToString("N0", culture),
            d.Product.Price.ToString("N2", culture),
            d.PricePerCp is double ppc ? (1 / ppc).ToString("N0", culture) : "-",
            d.UsedCpMetric
                ? (1 / d.Baseline.MedianPricePerCp!.Value).ToString("N0", culture)
                : "-",
            d.DiscountPercent.ToString("N1", culture),
            d.PriceDiscountPercent.ToString("N1", culture),
            d.Baseline.Samples.ToString("N0", culture),
        }).ToList());
    return 0;
}

async Task<int> ExportAsync()
{
    if (!TryGetType(required: false, out var type))
    {
        return 2;
    }

    var separator = options.GetValueOrDefault("sep", ",").ToLowerInvariant() switch
    {
        "," => ',',
        ";" => ';',
        "tab" => '\t',
        var s => throw new ArgumentException($"Separatore non valido: '{s}'. Usa ',', ';' o 'tab'."),
    };

    using var db = OpenDb();

    SnapshotInfo? snapshot;
    if (options.TryGetValue("snapshot", out var snapshotRaw))
    {
        if (!long.TryParse(snapshotRaw, NumberStyles.Integer, culture, out var snapshotId))
        {
            Console.Error.WriteLine($"Id snapshot non valido: '{snapshotRaw}'.");
            return 2;
        }

        snapshot = db.GetSnapshot(snapshotId);
        if (snapshot is null)
        {
            Console.Error.WriteLine($"Snapshot #{snapshotId} non trovato. Usa 'snapshots' per l'elenco.");
            return 2;
        }
    }
    else
    {
        var planet = GetPlanet();
        var latest = db.GetLatestSnapshotId(planet.Name);
        if (latest is null)
        {
            Console.WriteLine($"Nessuno snapshot per {planet.Name}. Esegui prima 'snapshot'.");
            return 0;
        }

        snapshot = db.GetSnapshot(latest.Value)!;
    }

    var products = db.GetSnapshotProducts(snapshot.Id, type);
    var itemNames = await LoadItemNamesAsync();
    var skillNames = await LoadSkillNamesAsync();

    var defaultName = $"ncmarket-{snapshot.Planet}-s{snapshot.Id}" +
                      (type is null ? "" : $"-{type.Value.ToString().ToLowerInvariant()}") + ".csv";
    var outPath = options.GetValueOrDefault("out", defaultName);

    // UTF-8 with BOM so Excel detects the encoding (accented item names).
    await using (var writer = new StreamWriter(outPath, append: false, new UTF8Encoding(true)))
    {
        SnapshotCsvExporter.Write(writer, snapshot, products, itemNames, skillNames, separator);
    }

    var scope = type is null ? "tutti gli equipaggiamenti" : type.Value.ToString();
    Console.WriteLine(
        $"Esportate {products.Count} inserzioni (snapshot #{snapshot.Id}, {snapshot.Planet}, {scope}) " +
        $"in {Path.GetFullPath(outPath)}");
    return 0;
}

int Prune()
{
    var days = GetInt("days", 365);
    if (days < 1)
    {
        Console.Error.WriteLine("Valore non valido per --days: deve essere almeno 1.");
        return 2;
    }

    var dryRun = options.ContainsKey("dry-run");
    var cutoffUtc = DateTime.UtcNow.AddDays(-days);

    using var db = OpenDb();
    var result = db.Prune(cutoffUtc, dryRun);

    Console.WriteLine(
        $"Retention su {db.DbPath}: rimozione delle inserzioni non più viste " +
        $"da {days} giorni (prima del {cutoffUtc.ToString("yyyy-MM-dd HH:mm", culture)} UTC)" +
        (dryRun ? " — prova, nessuna modifica" : "") + ":");
    Console.WriteLine();

    var invariant = dryRun ? "da rimuovere" : null;
    Console.WriteLine($"  Inserzioni {invariant ?? "rimosse"}: {result.ListingsRemoved.ToString("N0", culture)}");
    Console.WriteLine($"  Avvistamenti {invariant ?? "rimossi"}: {result.SightingsRemoved.ToString("N0", culture)}");
    Console.WriteLine($"  Snapshot {invariant ?? "rimossi"}: {result.SnapshotsRemoved.ToString("N0", culture)}");
    if (!dryRun)
    {
        Console.WriteLine(
            $"  Dimensione database: {FormatBytes(result.BytesBefore)} -> {FormatBytes(result.BytesAfter)}");
    }

    return 0;
}

int Unknown()
{
    Console.Error.WriteLine($"Comando sconosciuto: '{verb}'. Usa 'help' per l'elenco dei comandi.");
    return 2;
}

// ---------------------------------------------------------------- helper

MarketDb OpenDb()
{
    var db = new MarketDb(options.GetValueOrDefault("db"));
    if (db.MigrationBackupPath is not null)
    {
        Console.WriteLine(
            "Database migrato allo schema v2 (inserzioni deduplicate). " +
            $"Backup del vecchio formato: {db.MigrationBackupPath}");
        Console.WriteLine();
    }

    return db;
}

string FormatBytes(long bytes) => bytes switch
{
    >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("N2", culture) + " GB",
    >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("N1", culture) + " MB",
    >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("N0", culture) + " KB",
    _ => bytes.ToString("N0", culture) + " B",
};

Dictionary<string, string> ParseOptions(string[] rest)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = rest[i][2..];
        if (i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            result[key] = rest[++i];
        }
        else
        {
            result[key] = "true"; // flag senza valore, es. --no-names
        }
    }

    return result;
}

Planet GetPlanet()
{
    var name = options.GetValueOrDefault("planet", Planet.Default.Name);
    if (!Planet.TryGet(name, out var planet))
    {
        throw new ArgumentException(
            $"Pianeta non valido: '{name}'. Valori ammessi: " +
            string.Join(", ", Planet.All.Select(p => p.Name)));
    }

    return planet;
}

string GetOrder()
{
    var order = options.GetValueOrDefault("order", "unit_price");
    if (!MarketClient.ValidOrders.Contains(order))
    {
        throw new ArgumentException(
            $"Ordinamento non valido: '{order}'. Valori ammessi: " +
            string.Join(", ", MarketClient.ValidOrders));
    }

    return order;
}

int GetInt(string key, int fallback) =>
    options.TryGetValue(key, out var raw) ? int.Parse(raw, culture) : fallback;

bool TryGetType(bool required, out EquipmentType? type)
{
    type = null;
    if (options.TryGetValue("type", out var raw))
    {
        if (!EquipmentTypes.TryParse(raw, out var parsed))
        {
            Console.Error.WriteLine(
                $"Tipo equipaggiamento non valido: '{raw}'. " +
                "Valori ammessi: weapon (sword), armor, belt, necklace, ring.");
            return false;
        }

        type = parsed;
        return true;
    }

    if (required)
    {
        Console.Error.WriteLine("Specifica il tipo: --type weapon|armor|belt|necklace|ring");
        return false;
    }

    return true;
}

async Task<NameProvider> LoadItemNamesAsync() =>
    options.ContainsKey("no-names") ? NameProvider.Empty : await NameProvider.LoadItemNamesAsync();

async Task<NameProvider> LoadSkillNamesAsync() =>
    options.ContainsKey("no-names") ? NameProvider.Empty : await NameProvider.LoadSkillNamesAsync();

void PrintProducts(IReadOnlyList<ItemProduct> products, NameProvider names, NameProvider skillNames)
{
    if (products.Count == 0)
    {
        Console.WriteLine("Nessuna inserzione trovata.");
        return;
    }

    PrintTable(
        new[] { "#", "ItemId", "Nome", "Grado", "Lv", "CP", "Opz", "Elem", "Prezzo NCG", "Statistiche", "Skill", "Venditore" },
        new[] { true, true, false, true, true, true, true, false, true, false, false, false },
        products.Select((p, i) => new[]
        {
            (i + 1).ToString(culture),
            p.ItemId.ToString(culture),
            Truncate(ProductFormat.ItemDisplayName(p.ItemId, p.Grade, p.ItemSubType, names), 28),
            p.Grade.ToString(culture),
            p.Level.ToString(culture),
            p.CombatPoint.ToString("N0", culture),
            p.OptionCountFromCombination.ToString(culture),
            GameEnums.ElementalTypeName(p.ElementalType),
            p.Price.ToString("N2", culture),
            Truncate(ProductFormat.StatsSummary(p.StatModels), 40),
            Truncate(ProductFormat.SkillsSummary(p.SkillModels, skillNames), 36),
            p.SellerAvatarAddress.Length >= 8 ? "0x" + p.SellerAvatarAddress[..8] : p.SellerAvatarAddress,
        }).ToList());
}

void PrintProductDetails(IReadOnlyList<ItemProduct> products, NameProvider names, NameProvider skillNames)
{
    if (products.Count == 0)
    {
        Console.WriteLine("Nessuna inserzione trovata.");
        return;
    }

    for (var i = 0; i < products.Count; i++)
    {
        var p = products[i];
        Console.WriteLine(
            $"[{i + 1}] {ProductFormat.ItemDisplayName(p.ItemId, p.Grade, p.ItemSubType, names)} (item {p.ItemId}) — " +
            $"grado {p.Grade}, +{p.Level}, CP {p.CombatPoint.ToString("N0", culture)}, " +
            $"{GameEnums.ElementalTypeName(p.ElementalType)}");
        Console.WriteLine(
            $"    Prezzo: {p.Price.ToString("N2", culture)} NCG — " +
            $"opzioni {p.OptionCountFromCombination}, cristalli {p.Crystal.ToString("N0", culture)}" +
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

void PrintTable(string[] headers, bool[] rightAlign, List<string[]> rows)
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

string Truncate(string text, int max) =>
    text.Length <= max ? text : text[..(max - 1)] + "…";

void PrintHelp()
{
    Console.WriteLine("""
        NC-Market — prezzi degli equipaggiamenti dal mercato di Nine Chronicles

        Uso: ncmarket <comando> [opzioni]

        Comandi:
          fetch      Interroga il mercato live (non salva nulla)
                       --type weapon|armor|belt|necklace|ring   (obbligatorio; 'sword' = weapon)
                       --order <ordine>      default: unit_price
                                             (price, price_desc, cp, cp_desc, grade, grade_desc,
                                              level, level_desc, unit_price, unit_price_desc,
                                              opt_count, opt_count_desc, crystal, crystal_desc,
                                              crystal_per_price, crystal_per_price_desc)
                       --limit <n>           default: 20
                       --offset <n>          default: 0
                       --details             scheda completa per inserzione: statistiche
                                             (ATK, HP, DEF, ...) base e bonus, skill con
                                             probabilità/potenza, cristalli, venditore

          snapshot   Scarica e storicizza il listino nel database SQLite
                       --types w,a,...       default: tutti e cinque i tipi
                       --max-per-type <n>    limite prodotti per tipo (default: tutti)

          snapshots  Elenca gli snapshot salvati

          history    Storico prezzi di un item attraverso gli snapshot
                       --item <itemId>       (obbligatorio, es. 10152001)

          stats      Statistiche per item sull'ultimo snapshot
                       --type <tipo>         filtro opzionale
                       --top <n>             default: 30

          deals      Occasioni: inserzioni correnti sotto la mediana storica del
                     database (metrica primaria: NCG per punto CP, per item+livello)
                       --type <tipo>         filtro opzionale (default: tutti i tipi)
                       --grade <g[,g...]>    filtro rarità: 1-8 o normal, rare, epic,
                                             unique, legendary, divinity, mythic,
                                             transcendent (default: tutte)
                       --discount <pct>      sconto minimo percentuale, default: 25
                       --min-samples <n>     inserzioni storiche minime per confronto, default: 5
                       --days <n>            finestra storica in giorni (default: tutto lo storico)
                       --from-snapshot       confronta l'ultimo snapshot invece del mercato live
                       --max-per-type <n>    limite prodotti per tipo (solo live)
                       --top <n>             default: 30

          export     Esporta uno snapshot in CSV flat: una riga per inserzione,
                     statistiche in colonne <stat>_base/<stat>_bonus e skill in
                     colonne skill1_*/skill2_*
                       --snapshot <id>       default: ultimo snapshot del pianeta
                       --type <tipo>         filtro opzionale
                       --out <file>          default: ncmarket-<pianeta>-s<id>[-tipo].csv
                       --sep ,|;|tab         separatore CSV (default: ','; per Excel
                                             in italiano usare ';')

          prune      Retention: elimina le inserzioni non più viste da N giorni
                     (con i relativi avvistamenti e gli snapshot rimasti vuoti),
                     poi compatta il database con VACUUM
                       --days <n>            giorni di storico da conservare, default: 365
                       --dry-run             mostra cosa verrebbe rimosso senza modificare nulla

        Opzioni comuni:
          --planet odin|heimdall   default: heimdall
          --db <percorso>          database SQLite (default: %LOCALAPPDATA%\NCMarket\ncmarket.db)
          --no-names               non risolvere i nomi di item e skill

        Esempi:
          ncmarket fetch --type weapon --order price --limit 10
          ncmarket fetch --type ring --order cp_desc --limit 5 --details
          ncmarket snapshot --planet odin
          ncmarket history --item 10152001
          ncmarket stats --type ring --top 20
          ncmarket deals --discount 30
          ncmarket deals --grade legendary,mythic
          ncmarket deals --type ring --from-snapshot --min-samples 3
          ncmarket export --type weapon --sep ;
          ncmarket export --snapshot 2 --out listino.csv
          ncmarket prune --dry-run
          ncmarket prune --days 180
        """);
}
