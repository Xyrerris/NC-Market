using System.Net;
using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The one place this program talks to somebody else's server. Everything downstream —
/// snapshots, baselines, alerts — is built on what comes back from here, so what these
/// tests pin down is the request that goes out, the answer that is trusted, and the two
/// failure modes the client is allowed to have: ask again, or stop.
/// </summary>
public sealed class MarketClientTests
{
    private const string RingsUrl =
        "https://b.9capi.com/marketProviderHeimdall/Market/products/items/10";

    /// <summary>One page carrying the listings with the given ids and nothing else.</summary>
    private static string Page(params string[] productIds) =>
        $$"""{"totalCount":0,"limit":2,"offset":0,"itemProducts":[{{Items(productIds)}}]}""";

    private static string Items(string[] productIds) =>
        string.Join(",", productIds.Select(id => $$"""{"productId":"{{id}}"}"""));

    private static string Id(int n) => $"{n:D8}-1111-1111-1111-111111111111";

    private static FakeHttpHandler Answering(params string[] pages)
    {
        var handler = new FakeHttpHandler();
        foreach (var page in pages)
        {
            handler.Answering(HttpStatusCode.OK, page);
        }

        return handler;
    }

    /// <summary>
    /// The request names the equipment type in the path and the window in the query, and
    /// the sort order is escaped: it reaches the URL from a command-line option, which is
    /// the one value here that a user chooses.
    /// </summary>
    [Fact]
    public async Task The_request_says_which_type_which_window_and_in_what_order()
    {
        var handler = Answering(Page());
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        await client.GetProductsPageAsync(EquipmentType.Ring, limit: 100, offset: 200);

        Assert.Equal($"{RingsUrl}?limit=100&offset=200&order=unit_price", handler.Urls[0]);

        // Il market service è un endpoint pubblico di qualcun altro: il traffico si deve
        // poter distinguere da una raffica anonima, e chi lo gestisce deve poter risalire
        // al progetto.
        Assert.Equal(
            "NC-Market/1.0 (+https://github.com/Xyrerris/NC-Market)",
            handler.Headers[0].UserAgent.ToString());
    }

    /// <summary>
    /// The filters go on the wire in the one shape the service actually binds: the
    /// parameter repeated once per value. This is pinned rather than trusted because the
    /// two shapes one would otherwise write fail in opposite ways — <c>itemIds=1,2</c> is
    /// refused with a 422, while <c>itemIds[]=1</c> is answered 200 and <em>ignored</em>,
    /// which is the whole unfiltered market wearing the shape of a filtered answer
    /// (measured against b.9capi.com on 2026-08-25).
    /// </summary>
    [Fact]
    public async Task A_filter_repeats_its_parameter_once_per_value()
    {
        var handler = Answering(Page());
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        await client.GetProductsPageAsync(
            EquipmentType.Ring, limit: 5, offset: 0, order: "cp_desc",
            filter: new ListingFilter
            {
                ItemIds = new[] { 10181000, 10182000 },
                IconIds = new[] { 10181000 },
                Custom = false,
            });

        Assert.Equal(
            $"{RingsUrl}?limit=5&offset=0&order=cp_desc" +
            "&itemIds=10181000&itemIds=10182000&iconIds=10181000&isCustom=false",
            handler.Urls[0]);
    }

    /// <summary>
    /// Excluding custom craft is a filter, not the absence of one: <c>false</c> has to
    /// reach the query string, or asking for the ordinary pieces would quietly return
    /// both populations.
    /// </summary>
    [Fact]
    public async Task Excluding_custom_craft_reaches_the_query_string()
    {
        var handler = Answering(Page());
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        await client.GetProductsPageAsync(
            EquipmentType.Ring, limit: 5, offset: 0,
            filter: new ListingFilter { Custom = false });

        Assert.EndsWith("&isCustom=false", handler.Urls[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The combination the service mishandles never reaches it. Asked for ids together
    /// with <c>isCustom=true</c> it drops the ids and answers with every custom-crafted
    /// piece of the sub type — measured on b.9capi.com, where <c>itemIds=10181000</c>
    /// alone returns item 10181000 and the same request plus <c>isCustom=true</c>
    /// returns 20160003 and 20160004. That is an unfiltered answer wearing the shape of
    /// a filtered one, so the request is refused instead of sent.
    /// </summary>
    [Fact]
    public async Task Ids_asked_together_with_custom_craft_are_refused_not_sent()
    {
        var handler = Answering(Page());
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetProductsPageAsync(
                EquipmentType.Ring, limit: 5, offset: 0,
                filter: new ListingFilter
                {
                    ItemIds = new[] { 10181000 },
                    Custom = true,
                }));

        Assert.Empty(handler.Urls);
        Assert.Contains("isCustom", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction combines, and has to keep combining: excluding custom craft
    /// from a question about one item is the ordinary way to ask it.
    /// </summary>
    [Fact]
    public async Task Ids_asked_without_custom_craft_are_a_valid_pair()
    {
        var handler = Answering(Page());
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        await client.GetProductsPageAsync(
            EquipmentType.Ring, limit: 5, offset: 0,
            filter: new ListingFilter { ItemIds = new[] { 10181000 }, Custom = false });

        Assert.EndsWith(
            "&itemIds=10181000&isCustom=false", handler.Urls[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// And it travels with every page of a walk, not only the first. A filter dropped
    /// after page one would fill the answer with listings that do not match it, in an
    /// order that makes the mixture look deliberate.
    /// </summary>
    [Fact]
    public async Task A_filter_travels_with_every_page_of_the_walk()
    {
        var handler = Answering(Page(Id(1), Id(2)), Page(Id(3)));
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        await client.GetAllProductsAsync(
            EquipmentType.Ring, pageSize: 2,
            filter: new ListingFilter { ItemIds = new[] { 10181000 } });

        Assert.Equal(2, handler.Urls.Count);
        Assert.All(
            handler.Urls,
            url => Assert.Contains("&itemIds=10181000", url, StringComparison.Ordinal));
    }

    /// <summary>
    /// A caller that already identified itself keeps its own name: the client adds a user
    /// agent, it does not impose one.
    /// </summary>
    [Fact]
    public async Task A_caller_that_already_has_a_name_keeps_it()
    {
        var handler = Answering(Page());
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Qualcun-Altro/2.0");
        using var client = new MarketClient(Planet.Heimdall, http);

        await client.GetProductsPageAsync(EquipmentType.Ring, limit: 1, offset: 0);

        Assert.Equal("Qualcun-Altro/2.0", handler.Headers[0].UserAgent.ToString());
    }

    /// <summary>
    /// The wire format, read as the service sends it: names in camel case, enum-typed
    /// fields as integers, prices as numbers. This is the mapping every later number
    /// rests on, and the only test here that would notice the service renaming a field.
    /// </summary>
    [Fact]
    public async Task A_listing_is_read_field_by_field_as_the_service_sends_it()
    {
        const string body = """
            {
              "totalCount": 0,
              "limit": 100,
              "offset": 0,
              "itemProducts": [
                {
                  "productId": "11111111-1111-1111-1111-111111111111",
                  "sellerAgentAddress": "0xagent",
                  "sellerAvatarAddress": "0xavatar",
                  "price": 1234.5,
                  "quantity": 1,
                  "registeredBlockIndex": 42,
                  "exist": true,
                  "legacy": false,
                  "itemId": 10100000,
                  "iconId": 10100000,
                  "grade": 5,
                  "itemType": 0,
                  "itemSubType": 10,
                  "elementalType": 1,
                  "combatPoint": 4200,
                  "level": 3,
                  "optionCountFromCombination": 4,
                  "unitPrice": 1234.5,
                  "crystal": 90,
                  "crystalPerPrice": 7,
                  "byCustomCraft": true,
                  "statModels": [ { "value": 39548, "type": 2, "additional": false } ],
                  "skillModels": [
                    {
                      "skillId": 100001, "elementalType": 1, "skillCategory": 1,
                      "hitCount": 2, "cooldown": 10, "power": 12345,
                      "statPowerRatio": 3850, "chance": 35, "referencedStatType": 2
                    }
                  ]
                }
              ]
            }
            """;

        var handler = Answering(body);
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var page = await client.GetProductsPageAsync(EquipmentType.Ring, limit: 100, offset: 0);
        var product = Assert.Single(page.ItemProducts);

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), product.ProductId);
        Assert.Equal(1234.5m, product.Price);
        Assert.Equal(10100000, product.ItemId);
        Assert.Equal(5, product.Grade);
        Assert.Equal((int)EquipmentType.Ring, product.ItemSubType);
        Assert.Equal(3, product.Level);
        Assert.Equal(4200, product.CombatPoint);
        Assert.Equal(4, product.OptionCountFromCombination);
        Assert.Equal(90, product.Crystal);
        Assert.True(product.ByCustomCraft);
        Assert.Equal(39548, Assert.Single(product.StatModels).Value);
        Assert.Equal(3850, Assert.Single(product.SkillModels).StatPowerRatio);
    }

    /// <summary>
    /// A body of <c>null</c> is a valid JSON document and an empty answer. It becomes an
    /// empty page rather than a null reference two layers down.
    /// </summary>
    [Fact]
    public async Task An_answer_that_says_nothing_is_an_empty_page()
    {
        var handler = Answering("null");
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var page = await client.GetProductsPageAsync(EquipmentType.Ring, limit: 1, offset: 0);

        Assert.Empty(page.ItemProducts);
    }

    /// <summary>
    /// A 4xx is the service saying the request itself is wrong — a type it does not have,
    /// an order it does not know, an endpoint that moved. Retrying would cost six seconds
    /// to arrive at the same answer, so it fails now, naming the status.
    /// </summary>
    [Fact]
    public async Task An_answer_a_retry_cannot_fix_fails_at_once()
    {
        var handler = new FakeHttpHandler().Answering(HttpStatusCode.NotFound, "");
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetProductsPageAsync(EquipmentType.Ring, limit: 1, offset: 0));

        Assert.Single(handler.Urls);
        Assert.Contains("404", error.Message, StringComparison.Ordinal);
        Assert.Contains(RingsUrl, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 503 is the service saying "not now". Snapshots run unattended on a schedule, so
    /// giving up on the first one would leave a partial capture behind every time the
    /// service hiccupped for a second.
    /// </summary>
    [Fact]
    public async Task An_answer_that_may_change_is_asked_again()
    {
        var handler = new FakeHttpHandler()
            .Answering(HttpStatusCode.ServiceUnavailable, "")
            .Answering(HttpStatusCode.OK, Page(Id(1)));
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var page = await client.GetProductsPageAsync(EquipmentType.Ring, limit: 1, offset: 0);

        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(Guid.Parse(Id(1)), Assert.Single(page.ItemProducts).ProductId);
    }

    /// <summary>
    /// Three transient failures are a service that is down, not one that hiccupped. The
    /// client stops there and keeps the last error as the cause: a capture that fails
    /// loudly is a snapshot marked partial, which is what P0.1 exists for.
    /// </summary>
    [Fact]
    public async Task Three_failures_in_a_row_are_a_service_that_is_down()
    {
        var handler = new FakeHttpHandler()
            .Answering(HttpStatusCode.InternalServerError, "")
            .Answering(HttpStatusCode.BadGateway, "")
            .Answering(HttpStatusCode.ServiceUnavailable, "");
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetProductsPageAsync(EquipmentType.Ring, limit: 1, offset: 0));

        Assert.Equal(3, handler.Urls.Count);
        Assert.Contains("3 tentativi", error.Message, StringComparison.Ordinal);
        Assert.Contains("503", error.InnerException!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Paging walks forward by what the last page actually held and stops on the first
    /// short one, because <c>totalCount</c> is always 0 on current deployments: there is
    /// no total to compare against, only a page that did not fill up.
    /// </summary>
    [Fact]
    public async Task Pages_are_walked_until_one_comes_back_short()
    {
        var handler = Answering(Page(Id(1), Id(2)), Page(Id(3)));
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var products = await client.GetAllProductsAsync(EquipmentType.Ring, pageSize: 2);

        Assert.Equal(3, products.Count);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("limit=2&offset=0", handler.Urls[0], StringComparison.Ordinal);
        Assert.Contains("limit=2&offset=2", handler.Urls[1], StringComparison.Ordinal);

        // L'ordine di default è cp_desc: il servizio non ha una chiave di ordinamento
        // secondaria stabile, e su un ordinamento con molti pari merito le pagine si
        // sovrappongono. I combat point sono quasi sempre distinti.
        Assert.All(
            handler.Urls,
            url => Assert.Contains("order=cp_desc", url, StringComparison.Ordinal));
    }

    /// <summary>
    /// The de-duplication that the unstable ordering makes necessary: the same listing
    /// returned on two pages is one listing. Without it a snapshot would count a product
    /// twice, and every median computed from it would be off.
    /// </summary>
    [Fact]
    public async Task A_listing_seen_on_two_pages_is_kept_once()
    {
        var handler = Answering(Page(Id(1), Id(2)), Page(Id(2), Id(3)), Page(Id(4)));
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var products = await client.GetAllProductsAsync(EquipmentType.Ring, pageSize: 2);

        Assert.Equal(
            new[] { Id(1), Id(2), Id(3), Id(4) },
            products.Select(p => p.ProductId.ToString()));
    }

    /// <summary>
    /// And the guard behind it: a page that adds nothing is no forward progress, and a
    /// service shuffling ties can produce those forever. Three of them end the walk —
    /// three, not one, because a single overlapping page is ordinary.
    /// </summary>
    [Fact]
    public async Task Pages_that_add_nothing_end_the_walk_instead_of_looping()
    {
        var repeated = Page(Id(1));
        var handler = Answering(repeated, repeated, repeated, repeated, repeated, repeated);
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var products = await client.GetAllProductsAsync(EquipmentType.Ring, pageSize: 1);

        Assert.Single(products);
        Assert.Equal(4, handler.Urls.Count);
    }

    /// <summary>
    /// <c>--max-per-type</c> is a limit on what is kept, not a hint: a page that overshoots
    /// it is trimmed, so a truncated capture holds exactly the number it declares.
    /// </summary>
    [Fact]
    public async Task A_capture_limit_is_a_limit_on_what_is_kept()
    {
        var handler = Answering(Page(Id(1), Id(2)), Page(Id(3), Id(4)));
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var products = await client.GetAllProductsAsync(
            EquipmentType.Ring, pageSize: 2, maxItems: 3);

        Assert.Equal(3, products.Count);
        Assert.Equal(2, handler.Urls.Count);
    }

    /// <summary>
    /// Progress is reported once per page with the running total, which is what the
    /// console line counts up. The second number is the service's <c>totalCount</c>, zero
    /// in practice — an unknown total, not a total of zero.
    /// </summary>
    [Fact]
    public async Task Progress_counts_the_listings_kept_so_far()
    {
        var handler = Answering(Page(Id(1), Id(2)), Page(Id(3)));
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);
        var reported = new List<(int Fetched, int Total)>();

        await client.GetAllProductsAsync(
            EquipmentType.Ring, pageSize: 2,
            progress: (fetched, total) => reported.Add((fetched, total)));

        Assert.Equal(new[] { (2, 0), (3, 0) }, reported);
    }

    /// <summary>
    /// A type with nothing on sale is an empty answer, not an error: the market really can
    /// be empty for a sub type, and one request is enough to establish it.
    /// </summary>
    [Fact]
    public async Task A_type_with_nothing_on_sale_costs_one_request()
    {
        var handler = Answering(Page());
        using var http = new HttpClient(handler);
        using var client = new MarketClient(Planet.Heimdall, http);

        var products = await client.GetAllProductsAsync(EquipmentType.Ring, pageSize: 100);

        Assert.Empty(products);
        Assert.Single(handler.Urls);
    }

    /// <summary>
    /// A client given an <see cref="HttpClient"/> does not own it: disposing the client
    /// has to leave the caller's usable, or the second planet of a two-planet run would
    /// fail on a disposed handler.
    /// </summary>
    [Fact]
    public async Task Disposing_the_client_does_not_dispose_a_borrowed_http_client()
    {
        var handler = Answering(Page(Id(1)), Page(Id(2)));
        using var http = new HttpClient(handler);

        using (var client = new MarketClient(Planet.Heimdall, http))
        {
            await client.GetProductsPageAsync(EquipmentType.Ring, limit: 1, offset: 0);
        }

        using var second = new MarketClient(Planet.Heimdall, http);
        var page = await second.GetProductsPageAsync(EquipmentType.Ring, limit: 1, offset: 0);

        Assert.Equal(Guid.Parse(Id(2)), Assert.Single(page.ItemProducts).ProductId);
    }
}
