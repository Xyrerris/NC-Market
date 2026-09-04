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

    /// <summary>
    /// Il flusso guidato. Quattro campi su sei sono enumerabili, e un bottone è la
    /// differenza fra un campo che non si può sbagliare e uno che sì: restano da scrivere
    /// soltanto le opzioni, che è esattamente ciò che il piano chiedeva a F5.
    /// </summary>
    [Fact]
    public async Task The_guided_flow_asks_the_four_enumerable_fields_and_then_the_options()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            FillMarket(db);
        }

        var telegram = new FakeTelegram()
            .Delivers((1, Allowed, "/valuta"))
            .Presses((2, Allowed, Press(DialogField.Grade, (int)Grade.Transcendent)))
            .Presses((3, Allowed, Press(DialogField.Type, (int)EquipmentType.Weapon)))
            .Presses((4, Allowed, Press(DialogField.Element, (int)ElementalType.Fire)))
            .Presses((5, Allowed, Press(DialogField.Skill, 1)))
            .Delivers((6, Allowed, "ATK 1.404.374 DEF 3.359.312"));

        await RunAsync(temp, telegram);

        Assert.Equal(6, telegram.Replies.Count);
        Assert.Contains("rarità", telegram.Replies[0].Text, StringComparison.Ordinal);
        Assert.Equal(Grades.All.Length, Labels(telegram.Replies[0].Markup).Count);
        Assert.Contains("tipo", telegram.Replies[1].Text, StringComparison.Ordinal);
        Assert.Contains("elemento", telegram.Replies[2].Text, StringComparison.Ordinal);
        Assert.Contains("skill", telegram.Replies[3].Text, StringComparison.Ordinal);
        Assert.Contains("opzioni", telegram.Replies[4].Text, StringComparison.Ordinal);

        // La stima arriva senza che sia stato scritto altro che i numeri delle opzioni, e
        // il pezzo è quello dei bottoni: il flusso guidato scrive il messaggio che una
        // persona avrebbe scritto e lo dà allo stesso parser.
        var answer = telegram.Replies[5].Text;
        Assert.Contains("Transcendent Weapon", answer, StringComparison.Ordinal);
        Assert.Contains("con skill", answer, StringComparison.Ordinal);
        Assert.Contains("NCG", answer, StringComparison.Ordinal);

        // Ogni pressione va confermata, o il telefono continua a girare la rotella sul
        // bottone come se il bot non avesse sentito.
        Assert.Equal(new[] { "cb2", "cb3", "cb4", "cb5" }, telegram.Acknowledged);
    }

    /// <summary>
    /// L'ultimo passo è l'unico che si scrive, quindi è l'unico la cui tastiera è una via
    /// d'uscita dallo scrivere: un pezzo senza opzioni è un pezzo vero, e chiederlo a
    /// parole vorrebbe dire far scrivere "niente".
    /// </summary>
    [Fact]
    public async Task The_last_step_of_the_guided_flow_can_be_answered_with_a_button_too()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram()
            .Delivers((1, Allowed, "/valuta"))
            .Presses((2, Allowed, Press(DialogField.Grade, (int)Grade.Mythic)))
            .Presses((3, Allowed, Press(DialogField.Type, (int)EquipmentType.Ring)))
            .Presses((4, Allowed, Press(DialogField.Element, (int)ElementalType.Water)))
            .Presses((5, Allowed, Press(DialogField.Skill, 0)))
            .Presses((6, Allowed, Press(DialogField.NoOptions)));

        await RunAsync(temp, telegram);

        var answer = telegram.Replies[5].Text;
        Assert.Contains("Mythic Ring", answer, StringComparison.Ordinal);
        Assert.Contains("senza opzioni", answer, StringComparison.Ordinal);
        Assert.Contains("senza skill", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// I bottoni sotto una risposta portano con sé la domanda intera, quindi funzionano
    /// anche dopo un redeploy: un messaggio resta sul telefono per settimane, e un bottone
    /// che rispondesse "non me lo ricordo più" sarebbe un bottone rotto.
    /// </summary>
    [Fact]
    public async Task The_buttons_under_an_answer_outlive_the_process_that_sent_them()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            FillMarket(db);
        }

        var first = await RunAsync(temp, new FakeTelegram().Delivers((1, Allowed, Piece)));

        var markup = Assert.Single(first.Replies).Markup;
        Assert.Equal(
            new[] { "🔍 Vedi i comparabili", "🌐 Senza elemento", "🪐 Su odin" },
            Labels(markup));

        // Secondo processo, stesso database, bottone del primo.
        var second = await RunAsync(
            temp,
            new FakeTelegram().Presses((2, Allowed, Button(markup, "Vedi i comparabili"))));

        var listing = Assert.Single(second.Replies).Text;
        Assert.Contains("comparabili, dal più economico", listing, StringComparison.Ordinal);
        Assert.Contains("NCG", listing, StringComparison.Ordinal);
        Assert.Equal(new[] { "cb2" }, second.Acknowledged);
    }

    /// <summary>
    /// "Senza elemento" è il primo gradino della scala preso apposta invece che per
    /// necessità, e la risposta lo dichiara come dichiara ogni altro allargamento.
    /// </summary>
    [Fact]
    public async Task The_widen_button_asks_the_same_piece_on_every_element()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            FillMarket(db);
        }

        var first = await RunAsync(temp, new FakeTelegram().Delivers((1, Allowed, Piece)));
        Assert.DoesNotContain(
            "Bucket allargato", Assert.Single(first.Replies).Text, StringComparison.Ordinal);

        var second = await RunAsync(
            temp,
            new FakeTelegram().Presses(
                (2, Allowed, Button(first.Replies[0].Markup, "Senza elemento"))));

        var answer = Assert.Single(second.Replies).Text;
        Assert.Contains(
            "Bucket allargato: stimato su tutti gli elementi", answer, StringComparison.Ordinal);

        // Allargato una volta, non si riallarga: il bottone prometterebbe una risposta
        // diversa e restituirebbe la stessa.
        Assert.DoesNotContain(
            Labels(second.Replies[0].Markup),
            label => label.Contains("Senza elemento", StringComparison.Ordinal));
    }

    /// <summary>
    /// Il pianeta è l'unica cosa della domanda che non stava nel messaggio, quindi è
    /// l'unico bottone che c'è sempre — anche sotto una risposta che non ha trovato dati,
    /// dove invece i comparabili non ci sono da elencare.
    /// </summary>
    [Fact]
    public async Task The_other_planet_is_one_press_away_and_offers_the_way_back()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            FillMarket(db);
        }

        var first = await RunAsync(temp, new FakeTelegram().Delivers((1, Allowed, Piece)));

        var second = await RunAsync(
            temp,
            new FakeTelegram().Presses((2, Allowed, Button(first.Replies[0].Markup, "Su odin"))));

        var reply = Assert.Single(second.Replies);
        Assert.Contains("Non ho abbastanza dati", reply.Text, StringComparison.Ordinal);
        Assert.Equal(new[] { "🪐 Su heimdall" }, Labels(reply.Markup));
    }

    /// <summary>
    /// Una tastiera sopravvive al processo che l'ha mandata, quindi può arrivare un
    /// bottone di una versione che non esiste più. Nominarlo costa un messaggio; ignorarlo
    /// costerebbe un bottone indistinguibile da un bot fermo.
    /// </summary>
    [Fact]
    public async Task A_button_nobody_here_wrote_is_named_instead_of_ignored()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram().Presses((1, Allowed, "v0|qualcosa|di|vecchio"));

        await RunAsync(temp, telegram);

        Assert.Contains(
            "Questo bottone non lo conosco", Assert.Single(telegram.Replies).Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// La conversazione sta in memoria e un riavvio la perde: è accettabile, costa un
    /// /valuta ripetuto, e va detto invece che lasciato scoprire.
    /// </summary>
    [Fact]
    public async Task A_guided_flow_lost_to_a_restart_says_so()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram().Presses(
            (1, Allowed, Press(DialogField.Element, (int)ElementalType.Fire)));

        await RunAsync(temp, telegram);

        Assert.Contains(
            "Non ho più questa domanda in corso", Assert.Single(telegram.Replies).Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Chi ha aperto il flusso guidato e poi ha scritto il pezzo per intero per abitudine
    /// riceve la valutazione del pezzo che ha scritto. L'alternativa è rispondere "due
    /// rarità" a un messaggio perfettamente buono, che si legge come un bot rotto e non
    /// come un bot che stava aspettando.
    /// </summary>
    [Fact]
    public async Task A_whole_piece_typed_during_the_guided_flow_wins_over_the_flow()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram()
            .Delivers((1, Allowed, "/valuta"))
            .Presses((2, Allowed, Press(DialogField.Grade, (int)Grade.Transcendent)))
            .Presses((3, Allowed, Press(DialogField.Type, (int)EquipmentType.Weapon)))
            .Presses((4, Allowed, Press(DialogField.Element, (int)ElementalType.Fire)))
            .Presses((5, Allowed, Press(DialogField.Skill, 1)))
            .Delivers((6, Allowed, "Mythic Ring Water skill no ATK 1"));

        await RunAsync(temp, telegram);

        var answer = telegram.Replies[5].Text;
        Assert.Contains("Mythic Ring", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Due rarità", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un comando è un ricominciare da capo per definizione: qualunque domanda a metà
    /// fosse aperta, non è di quella che parla il messaggio.
    /// </summary>
    [Fact]
    public async Task A_command_closes_the_guided_flow()
    {
        using var temp = new TempDatabase();
        var telegram = new FakeTelegram()
            .Delivers((1, Allowed, "/valuta"))
            .Presses((2, Allowed, Press(DialogField.Grade, (int)Grade.Transcendent)))
            .Delivers((3, Allowed, "/annulla"))
            .Delivers((4, Allowed, "Mythic Ring Water ATK 1"));

        await RunAsync(temp, telegram);

        Assert.Contains(
            "lasciamo perdere", telegram.Replies[2].Text, StringComparison.Ordinal);
        Assert.Contains("Mythic Ring", telegram.Replies[3].Text, StringComparison.Ordinal);
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

    /// <summary>The data a button of the guided flow carries, as the bot writes it.</summary>
    private static string Press(DialogField field, int value = 0) =>
        ValuationCallback.Encode(field, value);

    /// <summary>The labels of a keyboard, in reading order.</summary>
    private static IReadOnlyList<string> Labels(string? markup) =>
        Keyboard(markup).Select(b => b.Text).ToList();

    /// <summary>
    /// The data behind the button whose label contains <paramref name="label"/>, which is
    /// what a phone sends back when it is pressed.
    /// </summary>
    private static string Button(string? markup, string label)
    {
        foreach (var (text, data) in Keyboard(markup))
        {
            if (text.Contains(label, StringComparison.Ordinal))
            {
                return data;
            }
        }

        Assert.Fail($"Nessun bottone '{label}' fra {string.Join(", ", Labels(markup))}.");
        return "";
    }

    /// <summary>The buttons of a <c>reply_markup</c>, flattened out of their rows.</summary>
    private static List<(string Text, string Data)> Keyboard(string? markup)
    {
        Assert.NotNull(markup);

        using var json = JsonDocument.Parse(markup!);
        return json.RootElement.GetProperty("inline_keyboard")
            .EnumerateArray()
            .SelectMany(row => row.EnumerateArray())
            .Select(b => (
                b.GetProperty("text").GetString()!,
                b.GetProperty("callback_data").GetString()!))
            .ToList();
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
/// Telegram, reduced to the three calls the bot makes. It answers <c>getUpdates</c> from a
/// script and records what <c>sendMessage</c> and <c>answerCallbackQuery</c> were asked to
/// deliver; when the script runs out it cancels <see cref="Stopping"/>, which is what ends
/// a loop whose only ordinary way out is cancellation.
/// </summary>
internal sealed class FakeTelegram : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

    /// <summary>Full URLs of the polls, in order — the offset travels in there.</summary>
    public List<string> Polls { get; } = new();

    /// <summary>What was sent back, to whom, and with which buttons under it.</summary>
    public List<(long ChatId, string Text, string? Markup)> Replies { get; } = new();

    /// <summary>Ids of the presses the bot said it had heard.</summary>
    public List<string> Acknowledged { get; } = new();

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

    /// <summary>
    /// Queues one poll answering with these button presses. The chat is the one the
    /// pressed message belongs to, which is also where the answer goes: the bot writes to
    /// a chat, not to whoever pressed.
    /// </summary>
    public FakeTelegram Presses(params (long Id, long Chat, string Data)[] presses)
    {
        var result = string.Join(",", presses.Select(p =>
            "{\"update_id\":" + p.Id +
            ",\"callback_query\":{\"id\":\"cb" + p.Id +
            "\",\"data\":" + JsonSerializer.Serialize(p.Data) +
            ",\"message\":{\"chat\":{\"id\":" + p.Chat + "}}}}"));

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
            var markup = Field(form, "reply_markup");
            Replies.Add((
                long.Parse(Field(form, "chat_id")),
                Field(form, "text"),
                markup.Length == 0 ? null : markup));
            return Ok("""{"ok":true}""");
        }

        if (url.Contains("/answerCallbackQuery", StringComparison.Ordinal))
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Acknowledged.Add(Field(form, "callback_query_id"));
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
