namespace NCMarket.Core;

/// <summary>
/// Equipment item sub types, matching lib9c's <c>Nekoyume.Model.Item.ItemSubType</c> values.
/// </summary>
public enum EquipmentType
{
    Weapon = 6,
    Armor = 7,
    Belt = 8,
    Necklace = 9,
    Ring = 10,
}

public static class EquipmentTypes
{
    public static readonly EquipmentType[] All =
    {
        EquipmentType.Weapon,
        EquipmentType.Armor,
        EquipmentType.Belt,
        EquipmentType.Necklace,
        EquipmentType.Ring,
    };

    /// <summary>
    /// Parses an equipment type from a name ("weapon", "sword", "armor", ...) or its numeric value.
    /// </summary>
    public static bool TryParse(string text, out EquipmentType type)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "weapon":
            case "sword":
            case "6":
                type = EquipmentType.Weapon;
                return true;
            case "armor":
            case "7":
                type = EquipmentType.Armor;
                return true;
            case "belt":
            case "8":
                type = EquipmentType.Belt;
                return true;
            case "necklace":
            case "9":
                type = EquipmentType.Necklace;
                return true;
            case "ring":
            case "10":
                type = EquipmentType.Ring;
                return true;
            default:
                type = default;
                return false;
        }
    }
}
