using System.Globalization;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// Flat CSV export of one snapshot: one row per listing, with stats spread over fixed
/// per-stat base/bonus columns and skills over one column group per skill slot, ready
/// for spreadsheet analysis.
/// </summary>
public static class SnapshotCsvExporter
{
    /// <summary>Stat columns, in lib9c <c>StatType</c> order (see <see cref="GameEnums.StatTypeName"/>).</summary>
    private static readonly int[] KnownStatTypes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly string[] SkillColumns =
    {
        "id", "name", "category", "elemental", "chance_pct",
        "power", "stat_ratio_pct", "ref_stat", "hits", "cooldown",
    };

    /// <summary>Writes header + one row per listing; returns the number of data rows.</summary>
    public static int Write(
        TextWriter writer,
        SnapshotInfo snapshot,
        IReadOnlyList<ItemProduct> products,
        NameProvider itemNames,
        NameProvider skillNames,
        char separator = ',')
    {
        // Column layout adapts to the data: stat types unknown to this build get extra
        // columns instead of being dropped, and there are as many skill groups as the
        // most skill-rich listing needs.
        var statTypes = KnownStatTypes
            .Concat(products
                .SelectMany(p => p.StatModels.Select(s => s.Type))
                .Where(t => !KnownStatTypes.Contains(t))
                .Distinct()
                .OrderBy(t => t))
            .ToArray();
        var maxSkills = products.Count == 0 ? 0 : products.Max(p => p.SkillModels.Count);

        var header = new List<string>
        {
            "snapshot_id", "planet", "taken_at_utc", "product_id",
            "item_id", "item_name", "item_type", "grade", "level", "combat_point",
            "elemental", "price_ncg", "quantity", "unit_price_ncg",
            "crystal", "crystal_per_price", "option_count", "by_custom_craft",
            "legacy", "registered_block_index", "seller_agent", "seller_avatar",
        };
        foreach (var statType in statTypes)
        {
            var name = GameEnums.StatTypeName(statType).ToLowerInvariant();
            header.Add($"{name}_base");
            header.Add($"{name}_bonus");
        }

        for (var slot = 1; slot <= maxSkills; slot++)
        {
            header.AddRange(SkillColumns.Select(c => $"skill{slot}_{c}"));
        }

        WriteRow(writer, header, separator);

        var takenAt = snapshot.TakenAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var p in products)
        {
            var row = new List<string>
            {
                Inv(snapshot.Id),
                snapshot.Planet,
                takenAt,
                p.ProductId.ToString("D"),
                Inv(p.ItemId),
                ProductFormat.ItemDisplayName(p.ItemId, p.Grade, p.ItemSubType, itemNames),
                ((EquipmentType)p.ItemSubType).ToString(),
                Inv(p.Grade),
                Inv(p.Level),
                Inv(p.CombatPoint),
                GameEnums.ElementalTypeName(p.ElementalType),
                p.Price.ToString(CultureInfo.InvariantCulture),
                p.Quantity.ToString(CultureInfo.InvariantCulture),
                p.UnitPrice.ToString(CultureInfo.InvariantCulture),
                Inv(p.Crystal),
                Inv(p.CrystalPerPrice),
                Inv(p.OptionCountFromCombination),
                p.ByCustomCraft ? "1" : "0",
                p.Legacy ? "1" : "0",
                Inv(p.RegisteredBlockIndex),
                p.SellerAgentAddress,
                p.SellerAvatarAddress,
            };

            var statValues = new Dictionary<int, (long Base, long Bonus)>();
            foreach (var s in p.StatModels)
            {
                var current = statValues.GetValueOrDefault(s.Type);
                statValues[s.Type] = s.Additional
                    ? (current.Base, current.Bonus + s.Value)
                    : (current.Base + s.Value, current.Bonus);
            }

            foreach (var statType in statTypes)
            {
                var (baseValue, bonus) = statValues.GetValueOrDefault(statType);
                row.Add(Inv(baseValue));
                row.Add(Inv(bonus));
            }

            for (var slot = 0; slot < maxSkills; slot++)
            {
                if (slot < p.SkillModels.Count)
                {
                    var s = p.SkillModels[slot];
                    row.Add(Inv(s.SkillId));
                    row.Add(skillNames.GetName(s.SkillId));
                    row.Add(GameEnums.SkillCategoryName(s.SkillCategory));
                    row.Add(GameEnums.ElementalTypeName(s.ElementalType));
                    row.Add(Inv(s.Chance));
                    row.Add(Inv(s.Power));
                    // StatPowerRatio is in basis points: 3500 -> 35% of the referenced stat.
                    row.Add((s.StatPowerRatio / 100.0).ToString("0.##", CultureInfo.InvariantCulture));
                    row.Add(GameEnums.StatTypeName(s.ReferencedStatType));
                    row.Add(Inv(s.HitCount));
                    row.Add(Inv(s.Cooldown));
                }
                else
                {
                    row.AddRange(Enumerable.Repeat("", SkillColumns.Length));
                }
            }

            WriteRow(writer, row, separator);
        }

        return products.Count;
    }

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void WriteRow(TextWriter writer, IReadOnlyList<string> fields, char separator)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(separator);
            }

            writer.Write(Escape(fields[i], separator));
        }

        // CRLF, not Environment.NewLine: RFC 4180 ends a record with CRLF, and a bare LF
        // would be indistinguishable from a newline quoted inside a field when the export
        // runs on Linux. The file has to read the same wherever it was written.
        writer.Write("\r\n");
    }

    /// <summary>RFC 4180 quoting: quote fields containing the separator, quotes or newlines.</summary>
    private static string Escape(string field, char separator)
    {
        var needsQuotes = field.Contains(separator) || field.Contains('"') ||
                          field.Contains('\n') || field.Contains('\r');
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
