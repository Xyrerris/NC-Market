namespace NCMarket.Core;

/// <summary>
/// Default locations for NC-Market data files (database, caches).
/// </summary>
public static class AppPaths
{
    public static string DataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NCMarket");

    public static string DefaultDbPath => Path.Combine(DataDir, "ncmarket.db");

    public static string ItemNameCachePath => Path.Combine(DataDir, "item_name.csv");

    public static string SkillNameCachePath => Path.Combine(DataDir, "skill_name.csv");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
