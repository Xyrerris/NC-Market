using System.Text;

namespace NCMarket.Core;

/// <summary>
/// Resolves numeric game ids to English names using the official client localization files
/// (<c>item_name.csv</c>, <c>skill_name.csv</c> from the planetarium/NineChronicles
/// repository), cached locally.
/// </summary>
public sealed class NameProvider
{
    private const string LocalizationBaseUrl =
        "https://raw.githubusercontent.com/planetarium/NineChronicles/main/" +
        "nekoyume/Assets/StreamingAssets/Localization/";

    public const string ItemCsvUrl = LocalizationBaseUrl + "item_name.csv";
    public const string SkillCsvUrl = LocalizationBaseUrl + "skill_name.csv";

    private readonly Dictionary<int, string> _names;

    private NameProvider(Dictionary<int, string> names) => _names = names;

    public int Count => _names.Count;

    public string GetName(int id) =>
        _names.TryGetValue(id, out var name) ? name : id.ToString();

    public bool TryGetName(int id, out string name) => _names.TryGetValue(id, out name!);

    /// <summary>Returns a provider that resolves every id to its numeric string.</summary>
    public static NameProvider Empty { get; } = new(new Dictionary<int, string>());

    /// <summary>Loads item names (<c>ITEM_NAME_*</c> keys from <c>item_name.csv</c>).</summary>
    public static Task<NameProvider> LoadItemNamesAsync(
        string? cachePath = null,
        TimeSpan? maxAge = null,
        HttpClient? http = null,
        CancellationToken ct = default) =>
        LoadAsync(ItemCsvUrl, "ITEM_NAME_", cachePath ?? AppPaths.ItemNameCachePath,
            maxAge, http, ct);

    /// <summary>Loads skill names (<c>SKILL_NAME_*</c> keys from <c>skill_name.csv</c>).</summary>
    public static Task<NameProvider> LoadSkillNamesAsync(
        string? cachePath = null,
        TimeSpan? maxAge = null,
        HttpClient? http = null,
        CancellationToken ct = default) =>
        LoadAsync(SkillCsvUrl, "SKILL_NAME_", cachePath ?? AppPaths.SkillNameCachePath,
            maxAge, http, ct);

    /// <summary>
    /// Loads a name map from the local cache, refreshing it from GitHub when missing or
    /// older than <paramref name="maxAge"/> (default 7 days). Falls back to a stale cache,
    /// then to an empty provider, when the download fails.
    /// </summary>
    public static async Task<NameProvider> LoadAsync(
        string csvUrl,
        string keyPrefix,
        string cachePath,
        TimeSpan? maxAge = null,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        maxAge ??= TimeSpan.FromDays(7);

        var cacheIsFresh = File.Exists(cachePath) &&
                           DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < maxAge;

        if (!cacheIsFresh)
        {
            var ownsHttp = http is null;
            http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            try
            {
                var csv = await http.GetStringAsync(csvUrl, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, csv, Encoding.UTF8, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                // Offline or GitHub unreachable: fall back to a stale cache if present.
            }
            finally
            {
                if (ownsHttp)
                {
                    http.Dispose();
                }
            }
        }

        if (!File.Exists(cachePath))
        {
            return Empty;
        }

        var names = new Dictionary<int, string>();
        foreach (var line in await File.ReadAllLinesAsync(cachePath, Encoding.UTF8, ct))
        {
            if (!line.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = SplitCsvLine(line);
            if (fields.Count < 2 ||
                !int.TryParse(fields[0].AsSpan(keyPrefix.Length), out var id) ||
                string.IsNullOrWhiteSpace(fields[1]))
            {
                continue;
            }

            names[id] = fields[1];
        }

        return new NameProvider(names);
    }

    /// <summary>Splits one CSV line honoring double-quoted fields (RFC 4180 style).</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
