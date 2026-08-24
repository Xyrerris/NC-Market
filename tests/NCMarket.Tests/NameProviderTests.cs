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
