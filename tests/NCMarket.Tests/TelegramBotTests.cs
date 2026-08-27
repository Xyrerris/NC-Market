using System.Net;
using System.Text.Json;
using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The bot, over a fake Telegram. Nothing here is about what a piece is worth — that is
/// <see cref="ValuationServiceTests"/> — and everything is about what happens when a
/// process stands up for days and anyone can write to it: who gets answered, how often,
/// what survives a restart, and which failures must not take the loop down with them.
/// </summary>
public sealed class TelegramBotTests
{
    /// <summary>Not a real credential, but shaped like one so a leak would be visible.</summary>
    private const string Token = "123456:AA-token-di-prova";

    private const long Allowed = 4242;

    private const long Stranger = -1009999999999;

    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private const int Atk = 2;
    private const int Def = 3;

    /// <summary>The example of the plan, as somebody would type it.</summary>
    private const string Piece = "Transcendent Sword Fire\nATK 1.404.374\nDEF 3.359.312\nskill si";

    private static TelegramBotOptions Options(int messagesPerMinute = 10) => new()
    {
        Token = Token,
        AllowedChats = new HashSet<long> { Allowed },
        MessagesPerMinute = messagesPerMinute,
        PollTimeout = TimeSpan.FromSeconds(1),
        RetryDelay = TimeSpan.Zero,
    };

    private static ValuationDefaults Defaults => new() { Planet = Planet.Heimdall };

    /// <summary>Five listings of the bucket <see cref="Piece"/> describes.</summary>
    private static void FillMarket(MarketDb db)
    {
        var listings = new[] { 10m, 20m, 40m, 60m, 300m }.Select(price => TestData.Product(
            itemId: 10181000,
            price: price,
            grade: 8,
            itemSubType: (int)EquipmentType.Weapon,
            elementalType: (int)ElementalType.Fire,
            optionStats: new[] { Atk, Def },
            hasSkill: true));

        TestData.AddCompleteSnapshot(
            db, Now, listings.ToArray(), types: new[] { EquipmentType.Weapon });
    }

    private static async Task<FakeTelegram> RunAsync(
        TempDatabase temp,
        FakeTelegram telegram,
        TelegramBotOptions? options = null,
        Func<DateTime>? clock = null)
    {
        using var http = new HttpClient(telegram, disposeHandler: false);
        var settings = options ?? Options();
        using var updates = new TelegramUpdateSource(Token, settings.PollTimeout, http);
        using var replies = new TelegramNotifier(new TelegramOptions { Token = Token }, http);

        var bot = new TelegramBot(
            settings, updates, replies, () => temp.Open(), Defaults, clock: clock);

        await bot.RunAsync(telegram.Stopping.Token);
        return telegram;
    }

    [Fact]
    public async Task A_piece_is_answered_with_the_echo_and_the_range()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            FillMarket(db);
        }

        var telegram = new FakeTelegram().Delivers((100, Allowed, Piece));

        await RunAsync(temp, telegram);

        var reply = Assert.Single(telegram.Replies).Text;

        // L'eco per prima: è ciò che rende visibile una lettura sbagliata prima che
        // diventi una stima sbagliata dall'aria giusta.
        Assert.Contains("Ho letto", reply, StringComparison.Ordinal);
        Assert.Contains("Transcendent Weapon", reply, StringComparison.Ordinal);
        Assert.Contains("NCG", reply, StringComparison.Ordinal);
        Assert.Contains("comparabili", reply, StringComparison.Ordinal);

        // Due snapshot a tre minuti di distanza non mostrano nessuna sparizione: la
        // popolazione ripiega su ciò che si osserva, e il messaggio lo dice.
        Assert.Contains("prezzi richiesti", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rispondere "non sei autorizzato" a uno sconosciuto conferma che il bot esiste e
    /// invita a insistere. L'offset avanza lo stesso: un messaggio non risposto è un
    /// messaggio letto, e riproporlo a ogni poll bloccherebbe tutto ciò che sta dietro.
    /// </summary>
    [Fact]
    public async Task A_chat_outside_the_allowlist_is_ignored_without_an_answer()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram().Delivers((100, Stranger, Piece));

        await RunAsync(temp, telegram);

        Assert.Empty(telegram.Replies);

        using var db = temp.Open();
        Assert.Equal(101, db.GetTelegramOffset());
    }

    [Fact]
    public async Task The_offset_is_written_down_and_read_back_after_a_restart()
    {
        using var temp = new TempDatabase();

        await RunAsync(temp, new FakeTelegram().Delivers((100, Allowed, "/aiuto")));

        using (var db = temp.Open())
        {
            Assert.Equal(101, db.GetTelegramOffset());
        }

        // Un secondo processo sullo stesso database: riparte da dove il primo si è
        // fermato, invece di rileggere la coda o di saltarla.
        var restarted = await RunAsync(temp, new FakeTelegram());

        Assert.Contains(
            "offset=101", Assert.Single(restarted.Polls), StringComparison.Ordinal);
    }

    /// <summary>
    /// Due processi sullo stesso token si prendono un 409 a vicenda. Ritentare
    /// significherebbe che uno dei due vince a caso e che i messaggi vengono risposti due
    /// volte, o mai, senza che niente lo spieghi.
    /// </summary>
    [Fact]
    public async Task A_second_poller_is_reported_and_not_retried()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram().Answers(
            HttpStatusCode.Conflict,
            """
            {"ok":false,"error_code":409,
             "description":"Conflict: terminated by other getUpdates request"}
            """);

        var error = await Assert.ThrowsAsync<TelegramConflictException>(
            () => RunAsync(temp, telegram));

        Assert.Contains("un'altra istanza", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("409", error.Message, StringComparison.Ordinal);

        // Il token sta nel percorso dell'URL, e un messaggio d'errore finisce in un log.
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
        Assert.Single(telegram.Polls);
    }

    [Fact]
    public async Task Over_the_rate_limit_a_chat_is_told_once_and_then_left_alone()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram().Delivers(
            (1, Allowed, "/aiuto"),
            (2, Allowed, "/aiuto"),
            (3, Allowed, "/aiuto"),
            (4, Allowed, "/aiuto"),
            (5, Allowed, "/aiuto"));

        // L'orologio fermo è la finestra: tutti e cinque i messaggi cadono nello stesso
        // minuto.
        await RunAsync(temp, telegram, Options(messagesPerMinute: 3), clock: () => Now);

        Assert.Equal(4, telegram.Replies.Count);
        Assert.All(
            telegram.Replies.Take(3),
            reply => Assert.Contains("Scrivimi il pezzo", reply.Text, StringComparison.Ordinal));

        // Il quarto riceve la spiegazione del silenzio, il quinto il silenzio: la
        // risposta a troppi messaggi non può essere altri messaggi.
        Assert.Contains("Troppi messaggi", telegram.Replies[3].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_are_dispatched_and_an_unknown_one_is_named()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            FillMarket(db);
        }

        var telegram = new FakeTelegram().Delivers(
            (1, Allowed, "/start"),
            (2, Allowed, "/valuta@NCMarketBot " + Piece),
            (3, Allowed, "/prezzo"));

        await RunAsync(temp, telegram);

        Assert.Equal(3, telegram.Replies.Count);
        Assert.Contains("Scrivimi il pezzo", telegram.Replies[0].Text, StringComparison.Ordinal);

        // La menzione dice a chi è rivolto il comando in un gruppo, non fa parte del
        // comando.
        Assert.Contains("Ho letto", telegram.Replies[1].Text, StringComparison.Ordinal);
        Assert.Contains("/prezzo", telegram.Replies[2].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un messaggio storto è una risposta, non un'eccezione: dentro un ciclo di polling
    /// un'eccezione arriverebbe a chi ha scritto come un silenzio, e il messaggio dopo non
    /// verrebbe letto affatto.
    /// </summary>
    [Fact]
    public async Task A_message_the_parser_cannot_read_becomes_an_answer_and_the_loop_goes_on()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            FillMarket(db);
        }

        var telegram = new FakeTelegram().Delivers(
            (1, Allowed, "pippo"),
            (2, Allowed, Piece));

        await RunAsync(temp, telegram);

        Assert.Equal(2, telegram.Replies.Count);
        Assert.Contains(
            "Non ho riconosciuto", telegram.Replies[0].Text, StringComparison.Ordinal);
        Assert.Contains("Ho letto", telegram.Replies[1].Text, StringComparison.Ordinal);

        using var db2 = temp.Open();
        Assert.Equal(3, db2.GetTelegramOffset());
    }

    /// <summary>
    /// Una foto, un ingresso in un gruppo: non c'è niente da rispondere, ma l'id va
    /// consumato lo stesso o il poll successivo lo richiede all'infinito.
    /// </summary>
    [Fact]
    public async Task An_update_without_text_advances_the_offset_without_an_answer()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram().Answers(
            HttpStatusCode.OK,
            """{"ok":true,"result":[{"update_id":7,"message":{"chat":{"id":4242}}}]}""");

        await RunAsync(temp, telegram);

        Assert.Empty(telegram.Replies);
        using var db = temp.Open();
        Assert.Equal(8, db.GetTelegramOffset());
    }

    /// <summary>
    /// Un errore di rete non è una ragione per fermare un bot: il poll riparte, e i
    /// messaggi che stavano dietro all'errore arrivano lo stesso.
    /// </summary>
    [Fact]
    public async Task A_transient_failure_is_retried_instead_of_stopping_the_bot()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram()
            .Answers(HttpStatusCode.BadGateway, "<html>proxy</html>")
            .Delivers((9, Allowed, "/aiuto"));

        await RunAsync(temp, telegram);

        // Tre poll: quello fallito, quello che consegna il messaggio rimasto dietro, e
        // quello che trova la coda vuota e chiude la prova.
        Assert.Equal(3, telegram.Polls.Count);
        Assert.Single(telegram.Replies);
    }

    [Fact]
    public async Task The_answer_goes_to_the_chat_that_asked()
    {
        using var temp = new TempDatabase();
        var options = Options() with
        {
            AllowedChats = new HashSet<long> { Allowed, Stranger },
        };

        var telegram = new FakeTelegram().Delivers((1, Stranger, "/aiuto"));

        await RunAsync(temp, telegram, options);

        Assert.Equal(Stranger, Assert.Single(telegram.Replies).ChatId);
    }

    [Fact]
    public void A_bot_without_an_allowlist_does_not_start()
    {
        WithEnvironment(Token, null, () =>
        {
            Assert.False(TelegramBotOptions.TryFromEnvironment(out var options, out var error));

            Assert.Null(options);
            Assert.Contains(
                TelegramBotOptions.AllowedChatsVariable, error!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_allowlist_is_a_comma_separated_list_of_chat_ids()
    {
        WithEnvironment(Token, " 4242 , -1001234567890 ", () =>
        {
            Assert.True(TelegramBotOptions.TryFromEnvironment(out var options, out var error));

            Assert.Null(error);
            Assert.Equal(
                new HashSet<long> { 4242, -1001234567890 }, options!.AllowedChats.ToHashSet());
        });
    }

    [Fact]
    public void A_chat_id_that_is_not_a_number_is_named()
    {
        WithEnvironment(Token, "4242,tutti", () =>
        {
            Assert.False(TelegramBotOptions.TryFromEnvironment(out _, out var error));
            Assert.Contains("tutti", error!, StringComparison.Ordinal);
        });
    }

    private static void WithEnvironment(string? token, string? chats, Action body)
    {
        var oldToken = Environment.GetEnvironmentVariable(TelegramOptions.TokenVariable);
        var oldChats = Environment.GetEnvironmentVariable(
            TelegramBotOptions.AllowedChatsVariable);
        try
        {
            Environment.SetEnvironmentVariable(TelegramOptions.TokenVariable, token);
            Environment.SetEnvironmentVariable(TelegramBotOptions.AllowedChatsVariable, chats);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelegramOptions.TokenVariable, oldToken);
            Environment.SetEnvironmentVariable(
                TelegramBotOptions.AllowedChatsVariable, oldChats);
        }
    }
}

/// <summary>
/// Telegram, reduced to the two calls the bot makes. It answers <c>getUpdates</c> from a
/// script and records what <c>sendMessage</c> was asked to deliver; when the script runs
/// out it cancels <see cref="Stopping"/>, which is what ends a loop whose only ordinary
/// way out is cancellation.
/// </summary>
internal sealed class FakeTelegram : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

    /// <summary>Full URLs of the polls, in order — the offset travels in there.</summary>
    public List<string> Polls { get; } = new();

    /// <summary>What was sent back, and to whom.</summary>
    public List<(long ChatId, string Text)> Replies { get; } = new();

    /// <summary>Cancelled once the script is exhausted, to stop the bot.</summary>
    public CancellationTokenSource Stopping { get; } = new();

    /// <summary>Queues one poll answering with these updates.</summary>
    public FakeTelegram Delivers(params (long Id, long Chat, string Text)[] updates)
    {
        var result = string.Join(",", updates.Select(u =>
            "{\"update_id\":" + u.Id +
            ",\"message\":{\"chat\":{\"id\":" + u.Chat +
            "},\"text\":" + JsonSerializer.Serialize(u.Text) + "}}"));

        return Answers(HttpStatusCode.OK, "{\"ok\":true,\"result\":[" + result + "]}");
    }

    /// <summary>Queues one poll answering exactly this.</summary>
    public FakeTelegram Answers(HttpStatusCode status, string body)
    {
        _answers.Enqueue((status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("/sendMessage", StringComparison.Ordinal))
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Replies.Add((long.Parse(Field(form, "chat_id")), Field(form, "text")));
            return Ok("""{"ok":true}""");
        }

        Polls.Add(url);
        if (_answers.Count > 0)
        {
            var (status, body) = _answers.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }

        // Nothing left to say: the bot has done everything the test asked of it.
        Stopping.Cancel();
        return Ok("""{"ok":true,"result":[]}""");
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>One field of a form-urlencoded body, decoded.</summary>
    private static string Field(string form, string name)
    {
        foreach (var pair in form.Split('&'))
        {
            var split = pair.IndexOf('=');
            if (split > 0 && pair[..split] == name)
            {
                return Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' '));
            }
        }

        return "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
