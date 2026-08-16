using System.Net;
using System.Text.Json;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// HTTP client for the official Nine Chronicles market service
/// (https://github.com/planetarium/market-service).
/// </summary>
public sealed class MarketClient : IDisposable
{
    /// <summary>Sort orders accepted by the market service.</summary>
    public static readonly string[] ValidOrders =
    {
        "price", "price_desc",
        "unit_price", "unit_price_desc",
        "cp", "cp_desc",
        "grade", "grade_desc",
        "level", "level_desc",
        "opt_count", "opt_count_desc",
        "crystal", "crystal_desc",
        "crystal_per_price", "crystal_per_price_desc",
    };

    /// <summary>Courtesy pause between consecutive pages of the same collection.</summary>
    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;

    public Planet Planet { get; }

    public MarketClient(Planet planet, HttpClient? http = null)
    {
        Planet = planet;
        _baseUrl = planet.MarketBaseUrl.TrimEnd('/');
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // The market service is a public endpoint: identify the caller so its operators
        // can tell this traffic apart (and reach the project) instead of seeing an
        // anonymous burst of requests.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "NC-Market/1.0 (+https://github.com/Xyrerris/NC-Market)");
        }
    }

    /// <summary>
    /// Fetches a single page of listings for the given equipment type. Only transient
    /// failures (5xx, 408, 429, transport errors, timeouts) are retried; a definitive
    /// answer such as 400 or 404 fails immediately, reporting its status code.
    /// </summary>
    public async Task<MarketProductsPage> GetProductsPageAsync(
        EquipmentType type,
        int limit,
        int offset,
        string order = "unit_price",
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/Market/products/items/{(int)type}" +
                  $"?limit={limit}&offset={offset}&order={Uri.EscapeDataString(order)}";

        var lastError = default(Exception);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            }

            try
            {
                using var response = await _http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode))
                    {
                        lastError = new HttpRequestException(
                            $"Il market service ha risposto {(int)response.StatusCode} per {url}");
                        continue;
                    }

                    // Not something a retry can fix: a wrong item type, a malformed
                    // order, a moved endpoint. Fail now instead of after 6 seconds.
                    throw new InvalidOperationException(
                        $"Il market service ha risposto {(int)response.StatusCode} " +
                        $"({response.ReasonPhrase}) per {url}.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var page = await JsonSerializer.DeserializeAsync<MarketProductsPage>(
                    stream, JsonOptions, ct);
                return page ?? new MarketProductsPage();
            }
            catch (HttpRequestException e)
            {
                lastError = e;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                lastError = e; // HttpClient timeout
            }
        }

        throw new InvalidOperationException(
            $"Impossibile interrogare il market service ({url}) dopo 3 tentativi.", lastError);
    }

    /// <summary>
    /// Fetches all listings for the given equipment type, paginating automatically.
    /// The <c>totalCount</c> field is not populated by current deployments (always 0),
    /// so pagination stops on the first short or empty page instead. Results are
    /// de-duplicated by product id; the progress callback receives (fetched, totalCount)
    /// where totalCount may be 0 (unknown). The default order is <c>cp_desc</c> because
    /// the service does not use a stable secondary sort key: orders with many tied values
    /// (e.g. <c>unit_price</c>) shuffle ties between requests, causing pages to overlap
    /// and listings to be skipped, while combat point values are almost always distinct.
    /// </summary>
    public async Task<IReadOnlyList<ItemProduct>> GetAllProductsAsync(
        EquipmentType type,
        string order = "cp_desc",
        int pageSize = 1000,
        int? maxItems = null,
        Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        var seen = new HashSet<Guid>();
        var results = new List<ItemProduct>();
        var offset = 0;
        var staleBatches = 0;

        while (maxItems is null || results.Count < maxItems.Value)
        {
            var page = await GetProductsPageAsync(type, pageSize, offset, order, ct);
            if (page.ItemProducts.Count == 0)
            {
                break;
            }

            var added = 0;
            foreach (var product in page.ItemProducts)
            {
                if (seen.Add(product.ProductId))
                {
                    results.Add(product);
                    added++;
                }
            }

            // A page made only of already-seen listings means no forward progress:
            // bail out after a few of them rather than looping forever.
            staleBatches = added == 0 ? staleBatches + 1 : 0;
            if (staleBatches >= 3)
            {
                break;
            }

            offset += page.ItemProducts.Count;
            progress?.Invoke(results.Count, page.TotalCount);

            if (page.ItemProducts.Count < pageSize)
            {
                break;
            }

            await Task.Delay(PageDelay, ct);
        }

        if (maxItems is int max && results.Count > max)
        {
            results.RemoveRange(max, results.Count - max);
        }

        return results;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.RequestTimeout ||
        status == HttpStatusCode.TooManyRequests;

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
