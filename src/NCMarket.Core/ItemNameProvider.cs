using System.Text;

namespace NCMarket.Core;

/// <summary>
/// Resolves item ids to English item names using the official client localization file
/// (<c>item_name.csv</c> from the planetarium/NineChronicles repository), cached locally.
/// </summary>
public sealed class ItemNameProvider
{
    public const string CsvUrl =
        "https://raw.githubusercontent.com/planetarium/NineChronicles/main/" +
        "nekoyume/Assets/StreamingAssets/Localization/item_name.csv";

    private const string KeyPrefix = "ITEM_NAME_";

    private readonly Dictionary<int, string> _names;

    private ItemNameProvider(Dictionary<int, string> names) => _names = names;

    public int Count => _names.Count;

    public string GetName(int itemId) =>
        _names.TryGetValue(itemId, out var name) ? name : itemId.ToString();

    public bool TryGetName(int itemId, out string name) => _names.TryGetValue(itemId, out name!);

    /// <summary>Returns a provider that resolves every id to its numeric string.</summary>
    public static ItemNameProvider Empty { get; } = new(new Dictionary<int, string>());

    /// <summary>
    /// Loads the name map from the local cache, refreshing it from GitHub when missing or
    /// older than <paramref name="maxAge"/> (default 7 days). Falls back to a stale cache,
    /// then to an empty provider, when the download fails.
    /// </summary>
    public static async Task<ItemNameProvider> LoadAsync(
        string? cachePath = null,
        TimeSpan? maxAge = null,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        cachePath ??= AppPaths.ItemNameCachePath;
        maxAge ??= TimeSpan.FromDays(7);

        var cacheIsFresh = File.Exists(cachePath) &&
                           DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < maxAge;

        if (!cacheIsFresh)
        {
            var ownsHttp = http is null;
            http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            try
            {
                var csv = await http.GetStringAsync(CsvUrl, ct);
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
            if (!line.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = SplitCsvLine(line);
            if (fields.Count < 2 ||
                !int.TryParse(fields[0].AsSpan(KeyPrefix.Length), out var itemId) ||
                string.IsNullOrWhiteSpace(fields[1]))
            {
                continue;
            }

            names[itemId] = fields[1];
        }

        return new ItemNameProvider(names);
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
