using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

/// <summary>A database file in a private temp directory, removed with the fixture.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _dir;

    public TempDatabase()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ncmarket-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        DbPath = Path.Combine(_dir, "market.db");
    }

    public string DbPath { get; }

    public MarketDb Open() => new(DbPath);

    /// <summary>Raw connection, for assertions on the schema itself.</summary>
    public SqliteConnection Connect()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows long enough to break the delete.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover in the temp directory is not worth failing a test over.
        }
    }
}

internal static class TestData
{
    /// <summary>A listing with plausible defaults; override only what the test is about.</summary>
    /// <param name="optionStats">
    /// lib9c <c>StatType</c> values the crafting options rolled. Passing any of them also
    /// gives the piece its base stat, which is what a real listing carries and what a
    /// valuation key has to leave out.
    /// </param>
    public static ItemProduct Product(
        int itemId = 10100000,
        int level = 0,
        decimal price = 100m,
        int combatPoint = 1000,
        int grade = 3,
        int itemSubType = (int)EquipmentType.Ring,
        int optionCount = 1,
        Guid? productId = null,
        int elementalType = 0,
        int[]? optionStats = null,
        bool hasSkill = false,
        bool byCustomCraft = false) =>
        new()
        {
            ProductId = productId ?? Guid.NewGuid(),
            ItemId = itemId,
            IconId = itemId,
            Grade = grade,
            ItemSubType = itemSubType,
            Level = level,
            CombatPoint = combatPoint,
            ElementalType = elementalType,
            Price = price,
            Quantity = 1m,
            UnitPrice = price,
            OptionCountFromCombination = optionCount,
            ByCustomCraft = byCustomCraft,
            StatModels = BuildStats(optionStats),
            SkillModels = hasSkill
                ? new List<SkillModel> { new() { SkillId = 100000, Chance = 35 } }
                : new List<SkillModel>(),
            SellerAgentAddress = "0xagent",
            SellerAvatarAddress = "0xavatar",
            RegisteredBlockIndex = 1,
        };

    /// <summary>
    /// The stats of a piece: the base stat of the item, which no option rolled, followed
    /// by one additional stat per option.
    /// </summary>
    private static List<StatModel> BuildStats(int[]? optionStats)
    {
        if (optionStats is null)
        {
            return new List<StatModel>();
        }

        var stats = new List<StatModel> { new() { Type = 2, Value = 39548, Additional = false } };
        stats.AddRange(optionStats.Select(
            type => new StatModel { Type = type, Value = 1974, Additional = true }));
        return stats;
    }

    /// <summary>
    /// Creates a snapshot, fills it and marks it complete. Defaults to a full capture of
    /// the ring listing, the type <see cref="Product"/> produces.
    /// </summary>
    public static long AddCompleteSnapshot(
        MarketDb db, DateTime takenAtUtc, IEnumerable<ItemProduct> products,
        string planet = "heimdall",
        IEnumerable<EquipmentType>? types = null,
        int? maxPerType = null)
    {
        var id = AddSnapshot(db, takenAtUtc, products, planet, types, maxPerType);
        db.FinalizeSnapshot(id);
        return id;
    }

    /// <summary>Creates a snapshot and fills it, leaving it partial (never finalized).</summary>
    public static long AddSnapshot(
        MarketDb db, DateTime takenAtUtc, IEnumerable<ItemProduct> products,
        string planet = "heimdall",
        IEnumerable<EquipmentType>? types = null,
        int? maxPerType = null)
    {
        var id = db.CreateSnapshot(
            planet, types ?? new[] { EquipmentType.Ring }, takenAtUtc, maxPerType);
        db.AddProducts(id, products);
        return id;
    }
}

/// <summary>
/// A market that answers from a fixed listing instead of the network, and remembers what
/// it was asked for: what the orchestration services do is make requests, in an order and
/// with limits that matter, which is what this records.
/// </summary>
internal sealed class FakeMarket : IMarketListingSource
{
    private readonly Dictionary<EquipmentType, IReadOnlyList<ItemProduct>> _listings = new();
    private EquipmentType? _failOn;

    public FakeMarket(Planet? planet = null) => Planet = planet ?? Planet.Heimdall;

    public Planet Planet { get; }

    /// <summary>Types asked for, in the order they were asked for.</summary>
    public List<EquipmentType> Requested { get; } = new();

    /// <summary>The per-type limit of the last request.</summary>
    public int? LastMaxItems { get; private set; }

    /// <summary>Puts a type on sale, so a market is set up in a single expression.</summary>
    public FakeMarket With(EquipmentType type, params ItemProduct[] products)
    {
        _listings[type] = products;
        return this;
    }

    /// <summary>Makes one type fail to download, the way a network error would.</summary>
    public FakeMarket FailingOn(EquipmentType type)
    {
        _failOn = type;
        return this;
    }

    public Task<IReadOnlyList<ItemProduct>> GetAllProductsAsync(
        EquipmentType type,
        int? maxItems = null,
        Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        Requested.Add(type);
        LastMaxItems = maxItems;
        if (type == _failOn)
        {
            throw new InvalidOperationException(
                $"Il market service ha risposto 503 per {type}.");
        }

        var products = _listings.GetValueOrDefault(type, Array.Empty<ItemProduct>());
        progress?.Invoke(products.Count, products.Count);
        return Task.FromResult(products);
    }
}

/// <summary>
/// A destination that keeps the messages instead of delivering them: what an alert run
/// does is decide whether to speak and what to say, which is what this records.
/// </summary>
internal sealed class FakeChannel : INotificationChannel
{
    private bool _fails;

    public string Name => "Fake";

    /// <summary>The messages delivered, in order.</summary>
    public List<string> Sent { get; } = new();

    /// <summary>Makes every send fail, the way a wrong token or a dead network would.</summary>
    public FakeChannel Failing()
    {
        _fails = true;
        return this;
    }

    public Task SendAsync(string message, CancellationToken ct = default)
    {
        if (_fails)
        {
            throw new InvalidOperationException(
                "Telegram ha risposto 401 (Unauthorized): Unauthorized.");
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An HTTP endpoint that answers from a script instead of from the network, and keeps
/// what it was sent.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

    /// <summary>Full URLs requested, in order — token included, which is the point.</summary>
    public List<string> Urls { get; } = new();

    /// <summary>Request bodies, as sent on the wire.</summary>
    public List<string> Bodies { get; } = new();

    /// <summary>Request headers, in the same order — how a caller identifies itself.</summary>
    public List<HttpRequestHeaders> Headers { get; } = new();

    /// <summary>Queues one answer. Requests past the last one get a plain success.</summary>
    public FakeHttpHandler Answering(HttpStatusCode status, string body = """{"ok":true}""")
    {
        _answers.Enqueue((status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Urls.Add(request.RequestUri!.ToString());
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        Headers.Add(request.Headers);

        var (status, body) = _answers.Count > 0
            ? _answers.Dequeue()
            : (HttpStatusCode.OK, """{"ok":true}""");

        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

/// <summary>Collects the progress callbacks instead of printing them.</summary>
internal sealed class RecordingProgress : ISnapshotProgress
{
    public long? Created { get; private set; }

    public long? Interrupted { get; private set; }

    public List<EquipmentType> Started { get; } = new();

    public List<(EquipmentType Type, int Listings)> Completed { get; } = new();

    public void SnapshotCreated(long snapshotId, DateTime takenAtUtc) => Created = snapshotId;

    public void SnapshotInterrupted(long snapshotId) => Interrupted = snapshotId;

    public void TypeStarted(EquipmentType type) => Started.Add(type);

    public void TypeProgress(EquipmentType type, int fetched, int total)
    {
    }

    public void TypeCompleted(EquipmentType type, int listings) =>
        Completed.Add((type, listings));
}
