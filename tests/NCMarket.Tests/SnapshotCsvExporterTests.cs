using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

/// <summary>
/// The <c>export</c> command is where a snapshot leaves this program for a spreadsheet.
/// What matters is the shape of the file — which columns exist, in what order, and what
/// happens to a value that would break the grid — because whoever opens it downstream
/// builds formulas on column positions, not on names.
/// </summary>
public sealed class SnapshotCsvExporterTests
{
    private static readonly DateTime TakenAt = new(2026, 8, 24, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The 22 columns that describe the listing itself, before stats and skills.</summary>
    private const int FixedColumns = 22;

    /// <summary>Base and bonus for each of the 11 stat types this build knows.</summary>
    private const int KnownStatColumns = 11 * 2;

    /// <summary>Columns one skill slot occupies.</summary>
    private const int SkillColumns = 10;

    private static SnapshotInfo Snapshot() =>
        new(7, "heimdall", TakenAt, "10", 1, SnapshotStatus.Complete, null);

    private static StatModel Stat(int type, long value, bool additional = false) =>
        new() { Type = type, Value = value, Additional = additional };

    private static SkillModel Skill(int skillId, int chance = 35) =>
        new()
        {
            SkillId = skillId,
            Chance = chance,
            Power = 12345,
            StatPowerRatio = 3850,
            ReferencedStatType = 2,
            HitCount = 2,
            Cooldown = 10,
            SkillCategory = 1,
            ElementalType = 1,
        };

    /// <summary>Loads a name provider from a synthetic one-line localization file.</summary>
    private static Task<NameProvider> NamesAsync(TempDir dir, string csv) =>
        NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", dir.WriteFile("item_name.csv", csv),
            maxAge: TimeSpan.FromDays(7));

    /// <summary>Runs the export and returns the rows written and the lines of the file.</summary>
    private static (int Rows, string[] Lines) Export(
        IReadOnlyList<ItemProduct> products,
        NameProvider? itemNames = null,
        char separator = ',')
    {
        using var writer = new StringWriter();
        var rows = SnapshotCsvExporter.Write(
            writer, Snapshot(), products, itemNames ?? NameProvider.Empty, NameProvider.Empty,
            separator);

        return (rows, writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// An empty snapshot still produces its header. A file with no columns cannot be told
    /// apart from a failed export once it is on disk, and the count returned is what the
    /// command reports to the user.
    /// </summary>
    [Fact]
    public void An_export_without_listings_is_a_header_and_nothing_else()
    {
        var (rows, lines) = Export(Array.Empty<ItemProduct>());

        Assert.Equal(0, rows);
        var header = Assert.Single(lines);
        Assert.Equal(FixedColumns + KnownStatColumns, header.Split(',').Length);
        Assert.StartsWith(
            "snapshot_id,planet,taken_at_utc,product_id,item_id,item_name,", header);
    }

    /// <summary>
    /// One listing, end to end: the snapshot columns repeat on every row, the listing
    /// columns are the listing, and the numbers are written invariant — a comma as the
    /// decimal separator would move a price into the next column.
    /// </summary>
    [Fact]
    public void A_listing_becomes_one_row_of_the_values_it_carries()
    {
        var product = TestData.Product(
            itemId: 10100000, level: 3, price: 1234.5m, combatPoint: 4200, grade: 5,
            itemSubType: (int)EquipmentType.Ring, optionCount: 4,
            productId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        product.Crystal = 90;
        product.CrystalPerPrice = 7;

        var (rows, lines) = Export(new[] { product });

        Assert.Equal(1, rows);
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "7,heimdall,2026-08-24 09:30:00,11111111-1111-1111-1111-111111111111," +
            "10100000,10100000,Ring,5,3,4200,Normal,1234.5,1,1234.5,90,7,4,0,0,1," +
            "0xagent,0xavatar," +
            "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            lines[1]);
    }

    /// <summary>
    /// Stats arrive as one entry per (type, base or additional) pair and leave as two
    /// columns per type. Repeated entries of the same kind are summed, which is what makes
    /// the column a total instead of the last value seen.
    /// </summary>
    [Fact]
    public void Base_and_bonus_of_a_stat_are_two_columns_and_repeats_are_summed()
    {
        var product = TestData.Product();
        product.StatModels.AddRange(new[]
        {
            Stat(2, 39548),                  // ATK base
            Stat(2, 974, additional: true),  // ATK bonus
            Stat(2, 26, additional: true),   // secondo bonus ATK
            Stat(6, 820, additional: true),  // SPD, solo bonus
        });

        var (_, lines) = Export(new[] { product });
        var header = lines[0].Split(',');
        var row = lines[1].Split(',');

        Assert.Equal("39548", row[Array.IndexOf(header, "atk_base")]);
        Assert.Equal("1000", row[Array.IndexOf(header, "atk_bonus")]);
        Assert.Equal("0", row[Array.IndexOf(header, "spd_base")]);
        Assert.Equal("820", row[Array.IndexOf(header, "spd_bonus")]);
    }

    /// <summary>
    /// A stat type this build has no name for still gets its columns, appended after the
    /// known ones. Dropping it would silently lose data from an export taken after the
    /// game added a stat, and the export is the copy people keep.
    /// </summary>
    [Fact]
    public void A_stat_type_this_build_does_not_know_gains_its_own_columns()
    {
        var product = TestData.Product();
        product.StatModels.Add(Stat(99, 5));

        var (_, lines) = Export(new[] { product });
        var header = lines[0].Split(',');
        var row = lines[1].Split(',');

        Assert.Equal(FixedColumns + KnownStatColumns + 2, header.Length);
        Assert.Equal("stat99_base", header[^2]);
        Assert.Equal("stat99_bonus", header[^1]);
        Assert.Equal("5", row[^2]);
        Assert.Equal("0", row[^1]);
    }

    /// <summary>
    /// The file has as many skill slots as the richest listing needs, and a listing with
    /// fewer skills fills the remaining slots with empty cells rather than ending early:
    /// a short row would shift every column after it, for that row alone.
    /// </summary>
    [Fact]
    public void Skill_slots_are_as_many_as_the_richest_listing_and_short_rows_are_padded()
    {
        var rich = TestData.Product(productId: Guid.NewGuid());
        rich.SkillModels.AddRange(new[] { Skill(100001), Skill(200002, chance: 10) });
        var plain = TestData.Product(productId: Guid.NewGuid());

        var (_, lines) = Export(new[] { rich, plain });
        var header = lines[0].Split(',');
        var slot = FixedColumns + KnownStatColumns;

        Assert.Equal(slot + 2 * SkillColumns, header.Length);
        Assert.Equal("skill1_id", header[slot]);
        Assert.Equal("skill2_cooldown", header[^1]);

        // id, nome (nessuna localizzazione caricata, quindi l'id), categoria, elementale,
        // probabilità, potenza, rapporto in percento — 3850 punti base sono +38.5% —
        // stat di riferimento, colpi, cooldown.
        var richRow = lines[1].Split(',');
        Assert.Equal(
            new[]
            {
                "100001", "100001", "BlowAttack", "Fire", "35", "12345", "38.5", "ATK",
                "2", "10",
            },
            richRow[slot..(slot + SkillColumns)]);

        var plainRow = lines[2].Split(',');
        Assert.Equal(header.Length, plainRow.Length);
        Assert.All(plainRow[slot..], cell => Assert.Equal("", cell));
    }

    /// <summary>
    /// The quoting is not cosmetic: an item name is whatever the game called it, and one
    /// comma in a name would move every column after it by one, for that row alone. The
    /// rule is RFC 4180 — quote the field, double the quotes inside it.
    /// </summary>
    [Fact]
    public async Task A_value_that_would_break_the_grid_is_quoted()
    {
        using var dir = new TempDir();
        var names = await NamesAsync(dir, "ITEM_NAME_10100000,\"Sword, \"\"Long\"\"\"\n");

        var product = TestData.Product();
        product.SellerAgentAddress = "prima\nseconda";

        var (_, lines) = Export(new[] { product }, names);

        Assert.Equal("Sword, \"Long\"", names.GetName(10100000));
        Assert.Contains("\"Sword, \"\"Long\"\"\"", lines[1], StringComparison.Ordinal);

        // Un a capo dentro un campo non chiude la riga: il file resta di due righe.
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"prima\nseconda\"", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// With a different separator it is that character that has to be quoted, and the
    /// comma that no longer does: the rule is about the file being written, not about
    /// commas.
    /// </summary>
    [Fact]
    public async Task The_separator_decides_what_needs_quoting()
    {
        using var dir = new TempDir();
        var names = await NamesAsync(dir, "ITEM_NAME_10100000,Ring; the Second\n");

        var (_, semicolon) = Export(new[] { TestData.Product() }, names, separator: ';');
        Assert.Equal(FixedColumns + KnownStatColumns, semicolon[0].Split(';').Length);
        Assert.Contains("\"Ring; the Second\"", semicolon[1], StringComparison.Ordinal);

        var (_, comma) = Export(new[] { TestData.Product() }, names);
        Assert.Contains(",Ring; the Second,", comma[1], StringComparison.Ordinal);
    }
}
