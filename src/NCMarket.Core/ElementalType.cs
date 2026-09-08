using System.Globalization;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// Elemental affinity of an item, matching lib9c's
/// <c>Nekoyume.Model.Elemental.ElementalType</c> values — the integers the market
/// service returns in <see cref="ItemProduct.ElementalType"/>.
/// </summary>
public enum ElementalType
{
    Normal = 0,
    Fire = 1,
    Water = 2,
    Land = 3,
    Wind = 4,
}

public static class Elementals
{
    public static readonly ElementalType[] All =
    {
        ElementalType.Normal,
        ElementalType.Fire,
        ElementalType.Water,
        ElementalType.Land,
        ElementalType.Wind,
    };

    /// <summary>
    /// Names to values, built from <see cref="GameEnums.ElementalTypeName"/> so that the
    /// display side stays the only place the names are written down: this parser adds the
    /// opposite direction, not a second list that can drift from the first.
    /// </summary>
    private static readonly Dictionary<string, ElementalType> ByName =
        All.ToDictionary(
            e => GameEnums.ElementalTypeName((int)e),
            e => e,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses an element from a name ("normal", "fire", "water", "land", "wind") or its
    /// numeric lib9c value (0-4).
    /// </summary>
    public static bool TryParse(string text, out ElementalType element)
    {
        var token = text.Trim();
        if (ByName.TryGetValue(token, out element))
        {
            return true;
        }

        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && Enum.IsDefined(typeof(ElementalType), value))
        {
            element = (ElementalType)value;
            return true;
        }

        element = default;
        return false;
    }

    /// <summary>Display name of an element, from the single source of the names.</summary>
    public static string Name(ElementalType element) =>
        GameEnums.ElementalTypeName((int)element);
}
