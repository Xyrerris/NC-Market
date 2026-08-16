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
    public static ItemProduct Product(
        int itemId = 10100000,
        int level = 0,
        decimal price = 100m,
        int combatPoint = 1000,
        int grade = 3,
        int itemSubType = (int)EquipmentType.Ring,
        int optionCount = 1,
        Guid? productId = null) =>
        new()
        {
            ProductId = productId ?? Guid.NewGuid(),
            ItemId = itemId,
            IconId = itemId,
            Grade = grade,
            ItemSubType = itemSubType,
            Level = level,
            CombatPoint = combatPoint,
            Price = price,
            Quantity = 1m,
            UnitPrice = price,
            OptionCountFromCombination = optionCount,
            SellerAgentAddress = "0xagent",
            SellerAvatarAddress = "0xavatar",
            RegisteredBlockIndex = 1,
        };

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
