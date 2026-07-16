using System.Globalization;
using System.Text;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// Compact, human-readable renderings of listing stats and skills.
/// </summary>
public static class ProductFormat
{
    /// <summary>
    /// One-line stat summary, e.g. <c>"ATK 39548 (+1974) · SPD +820"</c>. Base and
    /// additional (crafting option) values of the same stat are merged into one entry:
    /// the base value first, the additional bonus in parentheses.
    /// </summary>
    public static string StatsSummary(IReadOnlyCollection<StatModel> stats)
    {
        if (stats.Count == 0)
        {
            return "-";
        }

        var parts = new List<string>();
        foreach (var group in stats.GroupBy(s => s.Type).OrderBy(g => g.Key))
        {
            var baseValue = group.Where(s => !s.Additional).Sum(s => s.Value);
            var bonus = group.Where(s => s.Additional).Sum(s => s.Value);
            var name = GameEnums.StatTypeName(group.Key);
            parts.Add((baseValue, bonus) switch
            {
                (not 0, not 0) => $"{name} {N0(baseValue)} (+{N0(bonus)})",
                (not 0, 0) => $"{name} {N0(baseValue)}",
                (0, not 0) => $"{name} +{N0(bonus)}",
                _ => $"{name} 0",
            });
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// One-line skill summary for table cells, e.g. <c>"Flame Blow Attack 35%"</c>.
    /// </summary>
    public static string SkillsSummary(IReadOnlyCollection<SkillModel> skills, NameProvider skillNames)
    {
        if (skills.Count == 0)
        {
            return "-";
        }

        return string.Join(" · ", skills.Select(s => $"{skillNames.GetName(s.SkillId)} {s.Chance}%"));
    }

    /// <summary>
    /// Full one-line description of a skill for detail views, e.g.
    /// <c>"Flame Blow Attack [BlowAttack, Fire] — probabilità 35%, potenza 12345,
    /// +38.5% ATK, 2 colpi, cooldown 10"</c>.
    /// </summary>
    public static string SkillDetail(SkillModel skill, NameProvider skillNames)
    {
        var sb = new StringBuilder();
        sb.Append(skillNames.GetName(skill.SkillId));
        sb.Append(" [").Append(GameEnums.SkillCategoryName(skill.SkillCategory));
        sb.Append(", ").Append(GameEnums.ElementalTypeName(skill.ElementalType)).Append(']');
        sb.Append(" — probabilità ").Append(skill.Chance).Append('%');

        if (skill.Power != 0)
        {
            sb.Append(", potenza ").Append(N0(skill.Power));
        }

        // StatPowerRatio is in basis points: 10000 = +100% of the referenced stat.
        if (skill.StatPowerRatio != 0)
        {
            var percent = (skill.StatPowerRatio / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
            sb.Append(", +").Append(percent).Append("% ")
              .Append(GameEnums.StatTypeName(skill.ReferencedStatType));
        }

        if (skill.HitCount > 1)
        {
            sb.Append(", ").Append(skill.HitCount).Append(" colpi");
        }

        if (skill.Cooldown > 0)
        {
            sb.Append(", cooldown ").Append(skill.Cooldown);
        }

        return sb.ToString();
    }

    private static string N0(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
