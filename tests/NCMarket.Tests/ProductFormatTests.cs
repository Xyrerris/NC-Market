using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

/// <summary>
/// What a listing looks like once it reaches a person: the table cells, the detail view
/// and the CSV all render through here, so these are the strings the project is read in.
/// </summary>
public sealed class ProductFormatTests
{
    private const int KnownItemId = 10100000;
    private const int UnknownItemId = 10199999;

    /// <summary>A provider that knows one item, so "unknown id" is not "no names at all".</summary>
    private static async Task<NameProvider> NamesAsync(TempDir dir)
    {
        var cache = dir.WriteFile("item_name.csv", $"ITEM_NAME_{KnownItemId},Ring of Fire\n");
        return await NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", cache, maxAge: TimeSpan.FromDays(7));
    }

    private static StatModel Stat(int type, long value, bool additional = false) =>
        new() { Type = type, Value = value, Additional = additional };

    private static SkillModel Skill(
        int skillId = 100001, int chance = 35, long power = 0, int statPowerRatio = 0,
        int referencedStatType = 2, int hitCount = 1, int cooldown = 0,
        int skillCategory = 1, int elementalType = 1) =>
        new()
        {
            SkillId = skillId,
            Chance = chance,
            Power = power,
            StatPowerRatio = statPowerRatio,
            ReferencedStatType = referencedStatType,
            HitCount = hitCount,
            Cooldown = cooldown,
            SkillCategory = skillCategory,
            ElementalType = elementalType,
        };

    [Fact]
    public async Task A_known_id_is_shown_by_name()
    {
        using var dir = new TempDir();
        var names = await NamesAsync(dir);

        Assert.Equal(
            "Ring of Fire",
            ProductFormat.ItemDisplayName(KnownItemId, grade: 3, itemSubType: 10, names));
    }

    /// <summary>
    /// Grades 7 and 8 are the series the game names by itself, and they are also the ones
    /// most likely to be newer than the localization file on GitHub: an id the CSV does
    /// not carry still reads as an item rather than as a number.
    /// </summary>
    [Theory]
    [InlineData(7, (int)EquipmentType.Weapon, "Valkyrie Sword")]
    [InlineData(7, (int)EquipmentType.Armor, "Valkyrie Armor")]
    [InlineData(7, (int)EquipmentType.Belt, "Valkyrie Belt")]
    [InlineData(7, (int)EquipmentType.Necklace, "Valkyrie Necklace")]
    [InlineData(7, (int)EquipmentType.Ring, "Valkyrie Ring")]
    [InlineData(8, (int)EquipmentType.Weapon, "Transcendent Sword")]
    [InlineData(8, (int)EquipmentType.Ring, "Transcendent Ring")]
    public async Task An_unknown_id_of_the_named_series_falls_back_to_the_series_name(
        int grade, int itemSubType, string expected)
    {
        using var dir = new TempDir();
        var names = await NamesAsync(dir);

        Assert.Equal(
            expected, ProductFormat.ItemDisplayName(UnknownItemId, grade, itemSubType, names));
    }

    /// <summary>
    /// Outside those two series there is nothing to guess, and guessing wrong on a name
    /// is worse than showing the id: below grade 7, or on a sub type that is not
    /// equipment, the number stays.
    /// </summary>
    [Theory]
    [InlineData(6, (int)EquipmentType.Ring)]
    [InlineData(3, (int)EquipmentType.Weapon)]
    [InlineData(7, 3)]
    [InlineData(8, 0)]
    public async Task An_unknown_id_with_nothing_to_infer_from_stays_a_number(
        int grade, int itemSubType)
    {
        using var dir = new TempDir();
        var names = await NamesAsync(dir);

        Assert.Equal(
            UnknownItemId.ToString(),
            ProductFormat.ItemDisplayName(UnknownItemId, grade, itemSubType, names));
    }

    /// <summary>
    /// The regression the <c>Count == 0</c> check exists for: with <c>--no-names</c>, or
    /// offline without a cache, every id is unknown. Inferring the series there would turn
    /// a whole listing of grade 7 items into "Valkyrie Sword" repeated, hiding that names
    /// are simply not available.
    /// </summary>
    [Fact]
    public void Without_any_names_loaded_nothing_is_inferred()
    {
        Assert.Equal(
            UnknownItemId.ToString(),
            ProductFormat.ItemDisplayName(
                UnknownItemId, grade: 7, itemSubType: (int)EquipmentType.Weapon,
                NameProvider.Empty));
    }

    [Fact]
    public void A_listing_without_stats_says_so_with_a_dash()
    {
        Assert.Equal("-", ProductFormat.StatsSummary(Array.Empty<StatModel>()));
        Assert.Equal(
            "-", ProductFormat.SkillsSummary(Array.Empty<SkillModel>(), NameProvider.Empty));
    }

    /// <summary>
    /// Base and crafting bonus of the same stat are one entry, not two: the market service
    /// sends them as separate rows and reading them side by side is the whole point of the
    /// summary. Sorting is by stat type so the same item reads the same way twice.
    /// </summary>
    [Fact]
    public void Base_and_bonus_of_one_stat_are_merged_into_one_entry()
    {
        var summary = ProductFormat.StatsSummary(new[]
        {
            Stat(6, 820, additional: true),   // SPD, solo bonus
            Stat(2, 39548),                   // ATK base
            Stat(2, 974, additional: true),   // ATK bonus
            Stat(2, 1000, additional: true),  // secondo bonus ATK, sommato al precedente
            Stat(1, 12000),                   // HP, solo base
        });

        Assert.Equal("HP 12,000 · ATK 39,548 (+1,974) · SPD +820", summary);
    }

    /// <summary>
    /// A stat that is present and worth nothing is not the same as a stat that is absent:
    /// the first is a fact about the item, the second about the listing.
    /// </summary>
    [Fact]
    public void A_stat_worth_zero_is_still_printed()
    {
        Assert.Equal("CRI 0", ProductFormat.StatsSummary(new[] { Stat(4, 0) }));
    }

    [Fact]
    public async Task Skills_are_listed_by_name_and_chance()
    {
        using var dir = new TempDir();
        var cache = dir.WriteFile("skill_name.csv", "SKILL_NAME_100001,Flame Blow\n");
        var names = await NameProvider.LoadAsync(
            NameProvider.SkillCsvUrl, "SKILL_NAME_", cache, maxAge: TimeSpan.FromDays(7));

        var summary = ProductFormat.SkillsSummary(
            new[] { Skill(), Skill(skillId: 999999, chance: 10) }, names);

        // Uno skill che la localizzazione non conosce resta il suo id: senza il numero
        // non ci sarebbe modo di distinguerlo da uno che si chiama davvero cosi.
        Assert.Equal("Flame Blow 35% · 999999 10%", summary);
    }

    /// <summary>
    /// The detail line, with every optional segment present. The ratio is in basis points
    /// — 3850 is +38.5% of the referenced stat — which is the one number in here that
    /// cannot be read off the wire without converting it.
    /// </summary>
    [Fact]
    public void The_detail_line_carries_every_segment_a_skill_has()
    {
        var detail = ProductFormat.SkillDetail(
            Skill(power: 12345, statPowerRatio: 3850, hitCount: 2, cooldown: 10),
            NameProvider.Empty);

        Assert.Equal(
            "100001 [BlowAttack, Fire] — probabilità 35%, potenza 12,345, " +
            "+38.5% ATK, 2 colpi, cooldown 10",
            detail);
    }

    /// <summary>
    /// And with none of them: a skill that deals no fixed damage, scales off nothing,
    /// hits once and has no cooldown prints none of those, instead of four zeroes that
    /// would have to be read to be discarded.
    /// </summary>
    [Fact]
    public void The_segments_a_skill_does_not_have_are_left_out()
    {
        var detail = ProductFormat.SkillDetail(
            Skill(power: 0, statPowerRatio: 0, hitCount: 1, cooldown: 0, skillCategory: 6,
                elementalType: 0),
            NameProvider.Empty);

        Assert.Equal("100001 [Heal, Normal] — probabilità 35%", detail);
    }
}
