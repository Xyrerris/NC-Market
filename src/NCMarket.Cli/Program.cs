using System.Globalization;
using System.Text;
using NCMarket.Cli;
using NCMarket.Core;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    HelpText.Print();
    return 0;
}

var verb = args[0].ToLowerInvariant();
if (!CommandLine.TryParse(verb, args.Skip(1).ToArray(), out var parsed, out var parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine("Usa 'help' per l'elenco dei comandi e delle relative opzioni.");
    return 2;
}

var options = parsed!;

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
        "notify-test" => await NotifyTestAsync(),
        _ => throw new InvalidOperationException(
            $"Comando '{verb}' dichiarato in CommandLine ma non implementato."),
    };
}
catch (ArgumentException e)
{
    // Valore di opzione rifiutato (pianeta, ordinamento, separatore, intervallo
    // numerico): stesso esito di un'opzione sconosciuta.
    Console.Error.WriteLine($"Errore: {e.Message}");
    return 2;
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
//
// Ogni comando fa tre cose e nient'altro: legge le proprie opzioni, chiama il servizio
// di NCMarket.Core che sa fare il lavoro, passa il risultato a ConsoleReport. Ciò che
// è riusabile — la cattura del listino e la ricerca delle occasioni — sta in Core, così
// una dashboard o un job schedulato lo guidano allo stesso modo senza portarsi dietro
// la console.

async Task<int> FetchAsync()
{
    if (!TryGetType(required: true, out var type) || !TryGetFilter(out var filter))
    {
        return 2;
    }

    var planet = GetPlanet();
    var order = GetOrder();
    var limit = options.GetInt("limit", 20, min: 1);
    var offset = options.GetInt("offset", 0, min: 0);
    var names = await LoadItemNamesAsync();
    var skillNames = await LoadSkillNamesAsync();

    using var client = new MarketClient(planet);
    var page = await client.GetProductsPageAsync(type!.Value, limit, offset, order, filter);

    ConsoleReport.Listings(
        planet, type.Value, order, filter, page, names, skillNames,
        options.ContainsKey("details"));
    return 0;
}

async Task<int> SnapshotAsync()
{
    if (!TryGetTypes(out var types))
    {
        return 2;
    }

    var planet = GetPlanet();
    var maxPerType = GetMaxPerType();

    // Il lock si prende prima di aprire il database: serializzare i processi è compito
    // di chi lancia i comandi, non del servizio che esegue la cattura.
    using var dbLock = LockDb();
    using var db = OpenDb();
    using var client = new MarketClient(planet);

    var service = new SnapshotService(db, client);
    var report = await service.CaptureAsync(
        new SnapshotRequest { Types = types, MaxPerType = maxPerType },
        new ConsoleSnapshotProgress(planet, maxPerType));

    ConsoleReport.SnapshotStored(report, db.DbPath);
    return 0;
}

int ListSnapshots()
{
    using var db = OpenDb();
    // Senza --planet si elencano tutti i pianeti; se c'è, va validato come altrove.
    var planetFilter = options.ContainsKey("planet") ? GetPlanet().Name : null;
    var snapshots = db.GetSnapshots(planetFilter);
    if (snapshots.Count == 0)
    {
        Console.WriteLine("Nessuno snapshot salvato. Esegui prima il comando 'snapshot'.");
        return 0;
    }

    ConsoleReport.Snapshots(snapshots);
    return 0;
}

int History()
{
    if (!options.ContainsKey("item"))
    {
        Console.Error.WriteLine("Specifica l'item: history --item <itemId> (es. --item 10152001)");
        return 2;
    }

    var itemId = options.GetInt("item", 0, min: 1);
    var planet = GetPlanet();

    using var db = OpenDb();
    var rows = db.GetItemHistory(itemId, planet.Name);

    ConsoleReport.History(itemId, planet, rows, LoadItemNamesAsync().GetAwaiter().GetResult());
    return 0;
}

int Stats()
{
    if (!TryGetType(required: false, out var type))
    {
        return 2;
    }

    var planet = GetPlanet();
    var top = options.GetInt("top", 30, min: 1);

    using var db = OpenDb();
    var snapshotId = db.GetLatestSnapshotId(planet.Name);
    if (snapshotId is null)
    {
        Console.WriteLine(
            $"Nessuno snapshot completo per {planet.Name}. Esegui prima 'snapshot'.");
        return 0;
    }

    var rows = db.GetSnapshotStats(snapshotId.Value, type);
    ConsoleReport.Stats(
        snapshotId.Value, planet, type, rows, top,
        LoadItemNamesAsync().GetAwaiter().GetResult());
    return 0;
}

async Task<int> DealsAsync()
{
    if (!TryGetType(required: false, out var type) || !TryGetGrades(out var grades))
    {
        return 2;
    }

    var planet = GetPlanet();
    var days = options.GetInt("days", 0, min: 0);
    var top = options.GetInt("top", 30, min: 1);
    var fromSnapshot = options.ContainsKey("from-snapshot");

    var population = options.GetValueOrDefault("baseline", "sold").ToLowerInvariant() switch
    {
        "sold" => BaselinePopulation.Sold,
        "listed" => BaselinePopulation.Listed,
        var value => throw new ArgumentException(
            $"Popolazione di confronto non valida: '{value}'. Valori ammessi: sold " +
            "(inserzioni concluse) e listed (tutte le inserzioni osservate)."),
    };

    // Un'opzione che non ha effetto è un errore, non un default silenzioso.
    if (options.ContainsKey("sale-margin") && population != BaselinePopulation.Sold)
    {
        Console.Error.WriteLine(
            "L'opzione '--sale-margin' regola l'euristica di vendita e si applica " +
            "soltanto a '--baseline sold'.");
        return 2;
    }

    // Il canale di notifica si valida qui, prima della ricerca: scaricare il mercato
    // live sono minuti, e scoprire solo alla fine che manca il token significa averli
    // spesi per niente.
    var notify = options.ContainsKey("notify");
    TelegramOptions? telegram = null;
    if (notify && !TelegramOptions.TryFromEnvironment(out telegram, out var notifyError))
    {
        Console.Error.WriteLine(notifyError);
        return 2;
    }

    var query = new DealQuery
    {
        Planet = planet,
        Type = type,
        Grades = grades,
        MinDiscountPercent = options.GetInt("discount", 25, min: 0, max: 100),
        MinSamples = options.GetInt("min-samples", 5, min: 1),
        SinceUtc = days > 0 ? DateTime.UtcNow.AddDays(-days) : null,
        Population = population,
        SaleMarginPercent = options.GetInt(
            "sale-margin", MarketDb.DefaultSaleMarginPercent, min: 0, max: 500),
        FromSnapshot = fromSnapshot,
        MaxPerType = GetMaxPerType(),
    };

    using var db = OpenDb();
    // Il client serve solo per il mercato live: con --from-snapshot le inserzioni
    // correnti si rileggono dal database e non si apre nulla verso la rete.
    using var client = fromSnapshot ? null : new MarketClient(planet);

    var report = await new DealService(db, client).FindAsync(
        query, new ConsoleCaptureProgress($"Offerte correnti: mercato live ({planet.Name})"));

    if (report.Status != DealStatus.Ok)
    {
        ConsoleReport.DealsUnavailable(report, planet);
        return 0;
    }

    if (report.SnapshotId is long snapshotId)
    {
        Console.WriteLine($"Offerte correnti: snapshot #{snapshotId} ({planet.Name})");
    }

    Console.WriteLine();
    if (report.Deals.Count == 0)
    {
        Console.WriteLine("Nessuna occasione trovata con i criteri correnti.");
        return 0;
    }

    var names = await LoadItemNamesAsync();
    ConsoleReport.Deals(report, query, days, top, names);

    if (notify)
    {
        using var notifier = new TelegramNotifier(telegram!);
        var alert = await new DealAlertService(db, notifier)
            .AnnounceAsync(report, query, names, top);

        ConsoleReport.Alert(alert, notifier.Name);
    }

    return 0;
}

/// Verifica la configurazione del canale di notifica prima che serva davvero: senza
/// questo comando l'unico modo di sapere se token e chat sono giusti è aspettare la
/// prima occasione, e un silenzio non distingue "nessuna occasione" da "non arriva".
async Task<int> NotifyTestAsync()
{
    if (!TelegramOptions.TryFromEnvironment(out var telegram, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    using var notifier = new TelegramNotifier(telegram!);

    // Il canale dichiara MarkdownV2 per ogni messaggio, quindi anche questo va sfuggito:
    // un punto non sfuggito non arriverebbe storto, non arriverebbe.
    await notifier.SendAsync(MarkdownV2.Escape(
        "NC-Market — messaggio di prova.\n" +
        "Se lo stai leggendo, le notifiche delle occasioni arriveranno in questa chat."));

    Console.WriteLine($"Messaggio di prova inviato su {notifier.Name}.");
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
    if (options.ContainsKey("snapshot"))
    {
        var snapshotId = options.GetLong("snapshot", 0, min: 1);
        snapshot = db.GetSnapshot(snapshotId);
        if (snapshot is null)
        {
            Console.Error.WriteLine($"Snapshot #{snapshotId} non trovato. Usa 'snapshots' per l'elenco.");
            return 2;
        }

        // Esportare uno snapshot parziale su richiesta esplicita è legittimo, purché
        // sia chiaro che il listino non è completo.
        if (!snapshot.IsComplete)
        {
            Console.WriteLine(
                $"Attenzione: lo snapshot #{snapshot.Id} è parziale (cattura interrotta): " +
                "il CSV non contiene l'intero listino.");
        }
    }
    else
    {
        var planet = GetPlanet();
        var latest = db.GetLatestSnapshotId(planet.Name);
        if (latest is null)
        {
            Console.WriteLine(
                $"Nessuno snapshot completo per {planet.Name}. Esegui prima 'snapshot'.");
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

    Console.WriteLine(
        $"Esportate {products.Count} inserzioni (snapshot #{snapshot.Id}, {snapshot.Planet}, " +
        $"{ConsoleReport.Scope(type)}) in {Path.GetFullPath(outPath)}");
    return 0;
}

int Prune()
{
    var days = options.GetInt("days", 365, min: 1);
    var dryRun = options.ContainsKey("dry-run");
    var cutoffUtc = DateTime.UtcNow.AddDays(-days);

    // Il VACUUM finale richiede accesso esclusivo: senza lock un prune schedulato che
    // incrocia uno snapshot fallirebbe sul busy_timeout.
    using var dbLock = LockDb();
    using var db = OpenDb();
    var result = db.Prune(cutoffUtc, dryRun);

    ConsoleReport.Prune(db.DbPath, days, cutoffUtc, dryRun, result);
    return 0;
}

// ----------------------------------------------------------------- opzioni

string DbPath() => options.GetValueOrDefault("db") ?? AppPaths.DefaultDbPath;

/// Serializza i comandi che scrivono o compattano il database (snapshot, prune):
/// sul server gli Scheduled Task prima o poi si sovrappongono.
DbLock LockDb() =>
    DbLock.Acquire(
        DbPath(),
        TimeSpan.FromMinutes(30),
        () => Console.WriteLine(
            "Database in uso da un altro comando NC-Market: attendo che termini..."));

int? GetMaxPerType() =>
    options.ContainsKey("max-per-type") ? options.GetInt("max-per-type", 0, min: 1) : null;

MarketDb OpenDb()
{
    var db = new MarketDb(DbPath());
    if (db.MigrationBackupPath is not null)
    {
        Console.WriteLine(
            "Database migrato dallo schema v1 (inserzioni deduplicate, stato degli " +
            $"snapshot). Backup del vecchio formato: {db.MigrationBackupPath}");
        Console.WriteLine();
    }

    return db;
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

bool TryGetType(bool required, out EquipmentType? type)
{
    type = null;
    if (options.TryGetValue("type", out var raw))
    {
        if (!EquipmentTypes.TryParse(raw, out var parsedType))
        {
            Console.Error.WriteLine(
                $"Tipo equipaggiamento non valido: '{raw}'. " +
                "Valori ammessi: weapon (sword), armor, belt, necklace, ring.");
            return false;
        }

        type = parsedType;
        return true;
    }

    if (required)
    {
        Console.Error.WriteLine("Specifica il tipo: --type weapon|armor|belt|necklace|ring");
        return false;
    }

    return true;
}

/// I tipi che 'snapshot' deve catturare: senza --types sono tutti e cinque.
bool TryGetTypes(out EquipmentType[] types)
{
    types = EquipmentTypes.All;
    if (!options.TryGetValue("types", out var raw))
    {
        return true;
    }

    var wanted = new List<EquipmentType>();
    foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        if (!EquipmentTypes.TryParse(token, out var parsedType))
        {
            Console.Error.WriteLine($"Tipo equipaggiamento non valido: '{token}'.");
            return false;
        }

        wanted.Add(parsedType);
    }

    // Uno snapshot che non copre alcun tipo verrebbe comunque marcato completo e
    // diventerebbe l'ultimo snapshot del pianeta, nascondendo il listino vero.
    if (wanted.Count == 0)
    {
        Console.Error.WriteLine(
            "L'opzione '--types' non indica alcun tipo: " +
            "usa --types weapon,armor,belt,necklace,ring oppure ometti l'opzione.");
        return false;
    }

    types = wanted.Distinct().ToArray();
    return true;
}

/// I filtri che il market service applica oltre al tipo nella rotta.
bool TryGetFilter(out ListingFilter filter)
{
    filter = ListingFilter.None;

    if (!TryGetIds("item-ids", out var itemIds) || !TryGetIds("icon-ids", out var iconIds))
    {
        return false;
    }

    bool? custom = null;
    if (options.TryGetValue("custom", out var raw))
    {
        if (!bool.TryParse(raw, out var wanted))
        {
            Console.Error.WriteLine(
                $"Valore non valido per '--custom': '{raw}'. Valori ammessi: true, false.");
            return false;
        }

        custom = wanted;
    }

    filter = new ListingFilter { ItemIds = itemIds, IconIds = iconIds, Custom = custom };

    // Prima di scaricare i nomi e di interrogare il servizio: la combinazione che il
    // market service non sa rispondere si riconosce senza chiedergliela.
    filter.Validate();
    return true;
}

/// Elenco di id separati da virgola, come '--types' e '--grade' per i loro valori.
bool TryGetIds(string key, out int[] ids)
{
    ids = Array.Empty<int>();
    if (!options.TryGetValue(key, out var raw))
    {
        return true;
    }

    var wanted = new List<int>();
    var tokens = raw.Split(
        ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var token in tokens)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            Console.Error.WriteLine(
                $"Id non valido per '--{key}': '{token}'. Gli id sono numeri interi " +
                "positivi separati da virgola (es. --item-ids 10181000,10182000).");
            return false;
        }

        wanted.Add(id);
    }

    // Un filtro vuoto non restringe nulla: '--item-ids ,,' passerebbe per un filtro
    // applicato e restituirebbe l'intero sottotipo, che è il modo in cui questa API
    // sbaglia già da sé (vedi ListingFilter).
    if (wanted.Count == 0)
    {
        Console.Error.WriteLine(
            $"L'opzione '--{key}' non indica alcun id: indicane almeno uno oppure ometti " +
            "l'opzione.");
        return false;
    }

    ids = wanted.Distinct().ToArray();
    return true;
}

bool TryGetGrades(out HashSet<int>? grades)
{
    grades = null;
    if (!options.TryGetValue("grade", out var raw))
    {
        return true;
    }

    grades = new HashSet<int>();
    foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        if (!Grades.TryParse(token, out var grade))
        {
            Console.Error.WriteLine(
                $"Rarità non valida: '{token}'. Valori ammessi: 1-8 oppure " +
                "normal, rare, epic, unique, legendary, divinity, mythic, transcendent.");
            return false;
        }

        grades.Add((int)grade);
    }

    return true;
}

async Task<NameProvider> LoadItemNamesAsync() =>
    options.ContainsKey("no-names") ? NameProvider.Empty : await NameProvider.LoadItemNamesAsync();

async Task<NameProvider> LoadSkillNamesAsync() =>
    options.ContainsKey("no-names") ? NameProvider.Empty : await NameProvider.LoadSkillNamesAsync();
