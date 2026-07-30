namespace NCMarket.Core;

/// <summary>
/// Item grades (rarity), matching lib9c's <c>Nekoyume.Model.EnumType.Grade</c> values.
/// </summary>
public enum Grade
{
    Normal = 1,
    Rare = 2,
    Epic = 3,
    Unique = 4,
    Legendary = 5,
    Divinity = 6,
    Mythic = 7,
    Transcendent = 8,
}

public static class Grades
{
    public static readonly Grade[] All =
    {
        Grade.Normal,
        Grade.Rare,
        Grade.Epic,
        Grade.Unique,
        Grade.Legendary,
        Grade.Divinity,
        Grade.Mythic,
        Grade.Transcendent,
    };

    /// <summary>
    /// Parses a grade from a name ("normal", "rare", ..., "divine" as an alias of
    /// "divinity") or its numeric value (1-8).
    /// </summary>
    public static bool TryParse(string text, out Grade grade)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "normal":
            case "1":
                grade = Grade.Normal;
                return true;
            case "rare":
            case "2":
                grade = Grade.Rare;
                return true;
            case "epic":
            case "3":
                grade = Grade.Epic;
                return true;
            case "unique":
            case "4":
                grade = Grade.Unique;
                return true;
            case "legendary":
            case "5":
                grade = Grade.Legendary;
                return true;
            case "divinity":
            case "divine":
            case "6":
                grade = Grade.Divinity;
                return true;
            case "mythic":
            case "7":
                grade = Grade.Mythic;
                return true;
            case "transcendent":
            case "8":
                grade = Grade.Transcendent;
                return true;
            default:
                grade = default;
                return false;
        }
    }
}
