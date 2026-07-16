namespace NCMarket.Core.Models;

/// <summary>
/// One page of results from <c>GET /Market/products/items/{itemSubType}</c>.
/// Mirrors <c>MarketService.Response.MarketProductResponse</c>.
/// </summary>
public sealed class MarketProductsPage
{
    public int TotalCount { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
    public List<ItemProduct> ItemProducts { get; set; } = new();
}

/// <summary>
/// A single equipment listing. Mirrors <c>MarketService.Response.ItemProductResponseModel</c>;
/// enum-typed fields (itemType, itemSubType, elementalType, stat/skill types) are serialized
/// as integers by the service and kept as such here.
/// </summary>
public sealed class ItemProduct
{
    public Guid ProductId { get; set; }
    public string SellerAgentAddress { get; set; } = "";
    public string SellerAvatarAddress { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public long RegisteredBlockIndex { get; set; }
    public bool Exist { get; set; }
    public bool Legacy { get; set; }
    public int ItemId { get; set; }
    public int IconId { get; set; }
    public int Grade { get; set; }
    public int ItemType { get; set; }
    public int ItemSubType { get; set; }
    public int ElementalType { get; set; }
    public Guid TradableId { get; set; }
    public int SetId { get; set; }
    public int CombatPoint { get; set; }
    public int Level { get; set; }
    public List<SkillModel> SkillModels { get; set; } = new();
    public List<StatModel> StatModels { get; set; } = new();
    public int OptionCountFromCombination { get; set; }
    public decimal UnitPrice { get; set; }
    public long Crystal { get; set; }
    public long CrystalPerPrice { get; set; }
    public bool ByCustomCraft { get; set; }
    public bool HasRandomOnlyIcon { get; set; }
}

/// <summary>Mirrors <c>MarketService.Response.SkillResponseModel</c>.</summary>
public sealed class SkillModel
{
    public int SkillId { get; set; }
    public int ElementalType { get; set; }
    public int SkillCategory { get; set; }
    public int HitCount { get; set; }
    public int Cooldown { get; set; }
    public long Power { get; set; }
    public int StatPowerRatio { get; set; }
    public int Chance { get; set; }
    public int ReferencedStatType { get; set; }
}

/// <summary>Mirrors <c>MarketService.Response.StatResponseModel</c>.</summary>
public sealed class StatModel
{
    public long Value { get; set; }
    public int Type { get; set; }
    public bool Additional { get; set; }
}

/// <summary>
/// Human-readable labels for lib9c enum values returned as integers by the market service.
/// </summary>
public static class GameEnums
{
    /// <summary>lib9c <c>Nekoyume.Model.Stat.StatType</c>.</summary>
    public static string StatTypeName(int value) => value switch
    {
        0 => "NONE",
        1 => "HP",
        2 => "ATK",
        3 => "DEF",
        4 => "CRI",
        5 => "HIT",
        6 => "SPD",
        7 => "DRV",
        8 => "DRR",
        9 => "CDMG",
        10 => "ArmorPen",
        11 => "Thorn",
        _ => $"Stat{value}",
    };

    /// <summary>lib9c <c>Nekoyume.Model.Elemental.ElementalType</c>.</summary>
    public static string ElementalTypeName(int value) => value switch
    {
        0 => "Normal",
        1 => "Fire",
        2 => "Water",
        3 => "Land",
        4 => "Wind",
        _ => $"Elem{value}",
    };

    /// <summary>lib9c <c>Nekoyume.Model.Skill.SkillCategory</c>.</summary>
    public static string SkillCategoryName(int value) => value switch
    {
        0 => "NormalAttack",
        1 => "BlowAttack",
        2 => "DoubleAttack",
        3 => "AreaAttack",
        4 => "BuffRemovalAttack",
        5 => "ShatterStrike",
        6 => "Heal",
        7 => "HPBuff",
        8 => "AttackBuff",
        9 => "DefenseBuff",
        10 => "CriticalBuff",
        11 => "HitBuff",
        12 => "SpeedBuff",
        13 => "DamageReductionBuff",
        14 => "CriticalDamageBuff",
        15 => "Buff",
        16 => "Debuff",
        17 => "TickDamage",
        18 => "Focus",
        19 => "Dispel",
        20 => "FullBuffRemovalAttack",
        _ => $"Skill{value}",
    };
}
