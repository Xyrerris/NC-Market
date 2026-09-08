using System.Net;
using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class NameProviderTests
{
    private const int ItemId = 10100000;

    /// <summary>What the cache on disk holds.</summary>
    private const string CachedCsv = "ITEM_NAME_10100000,Ring\n";

    /// <summary>What the download answers with, deliberately different from the cache.</summary>
    private const string DownloadedCsv = "ITEM_NAME_10100000,Necklace\n";

    [Fact]
    public async Task A_stale_cache_is_replaced_by_what_the_download_returns()
    {
        using var dir = new TempDir();
        var cache = dir.WriteFile("item_name.csv", CachedCsv);
        var handler = new FakeHttpHandler().Answering(HttpStatusCode.OK, DownloadedCsv);
        using var http = new HttpClient(handler);

        var names = await NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", cache, maxAge: TimeSpan.Zero, http: http);

        Assert.Equal("Necklace", names.GetName(ItemId));
        Assert.Contains(
            "Necklace", await File.ReadAllTextAsync(cache), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unwritable_cache_still_serves_the_names_it_already_holds()
    {
        // Da root i bit di permesso non si applicano e lo scenario non è riproducibile.
        // Su Windows l'eccezione non serve: l'attributo di sola lettura vale anche per un
        // processo elevato.
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            return;
        }

        using var dir = new TempDir();
        var cache = dir.WriteFile("item_name.csv", CachedCsv);
        File.SetAttributes(cache, FileAttributes.ReadOnly);
        var handler = new FakeHttpHandler().Answering(HttpStatusCode.OK, DownloadedCsv);
        using var http = new HttpClient(handler);

        var names = await NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", cache, maxAge: TimeSpan.Zero, http: http);

        // La regressione: il permesso negato sulla riscrittura non deve uscire dal metodo.
        // Prima l'UnauthorizedAccessException sfuggiva al filtro del catch e uccideva il
        // comando, con la cache vecchia lì accanto, leggibile e più che sufficiente.
        Assert.Equal("Ring", names.GetName(ItemId));

        // E il tentativo di aggiornamento c'è stato per davvero: senza questa riga il test
        // passerebbe anche se il refresh non fosse mai partito.
        Assert.Single(handler.Urls);
    }

    /// <summary>
    /// Reads a cache written a moment ago, which counts as fresh: no download is attempted
    /// and what comes back is exactly what the parser made of the file.
    /// </summary>
    private static Task<NameProvider> ParseAsync(string cachePath) =>
        NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", cachePath, maxAge: TimeSpan.FromDays(7));

    /// <summary>
    /// The localization file is a CSV, not a line split on commas: item names contain
    /// commas and quotes, and the game protects them the RFC 4180 way. Splitting naively
    /// would file half the catalogue under half its name.
    /// </summary>
    [Fact]
    public async Task A_quoted_name_keeps_the_commas_and_quotes_inside_it()
    {
        using var dir = new TempDir();
        var cache = dir.WriteFile("item_name.csv", string.Join('\n',
            """ITEM_NAME_1,"Sword, Long",Spada,Sword""",
            """ITEM_NAME_2,"The ""Real"" Ring",Anello,Ring""",
            "ITEM_NAME_3,Plain Ring,Anello,Ring"));

        var names = await ParseAsync(cache);

        Assert.Equal("Sword, Long", names.GetName(1));
        Assert.Equal("""The "Real" Ring""", names.GetName(2));
        Assert.Equal("Plain Ring", names.GetName(3));
    }

    /// <summary>
    /// Everything the file holds that is not an item name — the header, the other name
    /// tables, and whatever a partially written cache ends in — is skipped one line at a
    /// time. A malformed line must cost its own name and nothing else: throwing here would
    /// take down a command over a row nobody was going to look up.
    /// </summary>
    [Fact]
    public async Task Lines_that_are_not_a_name_cost_only_themselves()
    {
        using var dir = new TempDir();
        var cache = dir.WriteFile("item_name.csv", string.Join('\n',
            "Key,English,Italian",        // intestazione
            "SKILL_NAME_1,Flame Blow",    // altro prefisso
            "ITEM_NAME_abc,Whatever",     // id non numerico
            "ITEM_NAME_4,",               // nome vuoto
            "ITEM_NAME_5,   ",            // nome di soli spazi
            "ITEM_NAME_6",                // riga troncata, senza il secondo campo
            "ITEM_NAME_7,Good Ring"));

        var names = await ParseAsync(cache);

        Assert.Equal(1, names.Count);
        Assert.Equal("Good Ring", names.GetName(7));

        // Gli id scartati non sono "assenti per sbaglio": restano numerici, che è il modo
        // in cui il resto del programma dichiara di non avere un nome.
        Assert.Equal("4", names.GetName(4));
        Assert.False(names.TryGetName(6, out _));
    }

    /// <summary>
    /// No cache to fall back on and a download that fails: the load still returns, and
    /// every id reads as its number. This is the offline first run, and it must not be a
    /// crash over the names, which are decoration on top of the prices.
    /// </summary>
    [Fact]
    public async Task Without_a_cache_and_without_the_network_every_id_stays_a_number()
    {
        using var dir = new TempDir();
        var missing = dir.WriteFile("item_name.csv", "");
        File.Delete(missing);
        var handler = new FakeHttpHandler().Answering(HttpStatusCode.InternalServerError, "");
        using var http = new HttpClient(handler);

        var names = await NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", missing, maxAge: TimeSpan.Zero, http: http);

        Assert.Equal(0, names.Count);
        Assert.Equal("10100000", names.GetName(ItemId));

        // Una risposta fallita non lascia dietro di sé una cache vuota, che il prossimo
        // avvio prenderebbe per fresca e non riproverebbe a scaricare per sette giorni.
        Assert.False(File.Exists(missing));
    }

    /// <summary>
    /// A cache younger than <c>maxAge</c> is used as it is. The default age is seven days
    /// and the CLI loads names on nearly every command: re-downloading each time would
    /// mean one request to GitHub per invocation, for a file that changes on release days.
    /// </summary>
    [Fact]
    public async Task A_fresh_cache_is_not_downloaded_again()
    {
        using var dir = new TempDir();
        var cache = dir.WriteFile("item_name.csv", CachedCsv);
        var handler = new FakeHttpHandler().Answering(HttpStatusCode.OK, DownloadedCsv);
        using var http = new HttpClient(handler);

        var names = await NameProvider.LoadAsync(
            NameProvider.ItemCsvUrl, "ITEM_NAME_", cache,
            maxAge: TimeSpan.FromDays(7), http: http);

        Assert.Equal("Ring", names.GetName(ItemId));
        Assert.Empty(handler.Urls);
    }
}

/// <summary>A private temp directory for cache files, removed with the fixture.</summary>
internal sealed class TempDir : IDisposable
{
    private readonly string _dir;

    public TempDir()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ncmarket-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Writes a file in the directory and returns its full path.</summary>
    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            // Un file in sola lettura non si cancella: il test che ne crea uno si
            // lascerebbe dietro la directory temporanea.
            foreach (var file in Directory.GetFiles(_dir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Un residuo nella temp non vale il fallimento di un test.
        }
    }
}
