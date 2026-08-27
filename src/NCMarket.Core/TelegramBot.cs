using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NCMarket.Core;

/// <summary>
/// Configuration of a bot that listens. The token is the same credential the alerts use
/// (<see cref="TelegramOptions.TokenVariable"/>) and is read from the environment for the
/// same reason; what is new is the allowlist, and it is new because the direction is.
/// <para>
/// <b>The allowlist is mandatory.</b> A bot that only sends talks to the chat it was
/// configured with; a bot that listens answers whoever finds its username, and every
/// message is a query on the database behind it. There is no safe default here — an empty
/// list would be a bot nobody can use, an absent one a bot everybody can — so a missing
/// <see cref="AllowedChatsVariable"/> stops the command at startup, exactly as missing
/// credentials stop <c>deals --notify</c>. Discovering this line by finding the bot
/// already open to the internet is the worst possible way to read it.
/// </para>
/// </summary>
public sealed record TelegramBotOptions
{
    /// <summary>Environment variable holding the chat ids allowed to ask.</summary>
    public const string AllowedChatsVariable = "NCMARKET_TELEGRAM_ALLOWED_CHATS";

    public required string Token { get; init; }

    /// <summary>
    /// Chats whose messages are answered. Everything else is ignored in silence: telling
    /// a stranger "you are not authorized" confirms the bot exists and invites them to
    /// keep trying, while nothing at all is indistinguishable from a username that was
    /// never a bot.
    /// </summary>
    public required IReadOnlySet<long> AllowedChats { get; init; }

    /// <summary>
    /// Messages per chat per minute. An allowed chat is not a threat, but a phone in a
    /// pocket and a script written in good faith both send faster than a person, and
    /// every message is a bucket query.
    /// </summary>
    public int MessagesPerMinute { get; init; } = 10;

    /// <summary>
    /// How long Telegram holds a poll open with nothing to say. Long is the point: it is
    /// what makes one request a minute enough to answer in a second.
    /// </summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>Pause after a failure that may not happen again, before polling anew.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the configuration from the environment, naming what is missing instead of
    /// starting a bot that is half configured.
    /// </summary>
    public static bool TryFromEnvironment(out TelegramBotOptions? options, out string? error)
    {
        options = null;
        error = null;

        var token = Environment.GetEnvironmentVariable(TelegramOptions.TokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            error =
                $"Bot Telegram non configurato: manca {TelegramOptions.TokenVariable}. " +
                "Il token si ottiene da @BotFather.";
            return false;
        }

        var raw = Environment.GetEnvironmentVariable(AllowedChatsVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error =
                $"Bot Telegram non configurato: manca {AllowedChatsVariable}, l'elenco " +
                "delle chat che possono interrogarlo (id separati da virgola, negativi " +
                "per gruppi e canali). È obbligatorio: un bot in ascolto risponde a " +
                "chiunque ne trovi lo username, e ogni messaggio è una query sul " +
                "database. L'id della propria chat si legge scrivendo al bot e aprendo " +
                "https://api.telegram.org/bot<token>/getUpdates.";
            return false;
        }

        var chats = new HashSet<long>();
        foreach (var chatToken in raw.Split(
                     ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(
                    chatToken, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                    out var chat))
            {
                error =
                    $"Chat non valida in {AllowedChatsVariable}: '{chatToken}'. Gli id sono " +
                    "numeri interi, negativi per gruppi e canali.";
                return false;
            }

            chats.Add(chat);
        }

        // ',,' is not an empty allowlist, it is a variable that meant to say something:
        // a bot that answers nobody looks exactly like a bot that is down.
        if (chats.Count == 0)
        {
            error =
                $"{AllowedChatsVariable} non indica alcuna chat: un bot che non risponde " +
                "a nessuno non si distingue da un bot fermo.";
            return false;
        }

        options = new TelegramBotOptions { Token = token.Trim(), AllowedChats = chats };
        return true;
    }
}

/// <summary>
/// Somewhere an answer can be sent, to the chat that asked. It is not
/// <see cref="INotificationChannel"/>: an alert goes to the one chat the job was
/// configured with, an answer goes back to whoever wrote, and a bot that could only reply
/// to a configured destination would answer the wrong person.
/// </summary>
public interface IReplyChannel
{
    Task SendAsync(long chatId, string message, CancellationToken ct = default);
}

/// <summary>
/// What the bot values pieces against: the planet, and the two knobs the CLI exposes. The
/// planet is not read from the message (see <see cref="ValuationRequestParser"/>) — a
/// message describes a piece, not where to look for it.
/// </summary>
public sealed record ValuationDefaults
{
    public required Planet Planet { get; init; }

    /// <summary>Comparables a bucket needs before the widening ladder stops.</summary>
    public int MinSamples { get; init; } = 5;

    /// <summary>
    /// History window in days; null uses the whole history. It becomes an instant when a
    /// message arrives and not at startup: this process stays up for weeks, and a window
    /// computed once would keep pointing at the day the bot was deployed.
    /// </summary>
    public int? Days { get; init; }
}

/// <summary>
/// The chatbot. It polls Telegram for messages, answers the ones coming from a chat on
/// the allowlist, and keeps doing so for as long as the process lives.
/// <para>
/// Nothing here decides anything about a valuation: a message becomes a query in
/// <see cref="ValuationRequestParser"/>, the query becomes a range in
/// <see cref="ValuationService"/>, and the range becomes words in
/// <see cref="ValuationMessage"/>. What is left is everything that only matters because
/// the process stands up for days and anyone can write to it — the allowlist, the rate
/// limit, the offset that survives a restart, and the rule that one bad message must not
/// stop the loop.
/// </para>
/// <para>
/// <b>The database is opened per message and closed again.</b> A connection held for days
/// would outlive the <c>VACUUM</c> that ends <c>prune</c>, which needs the file to
/// itself: the weekly retention job would then fail at a moment having nothing to do with
/// its cause. Opening around each message costs milliseconds on something a person
/// triggers, and it is also what lets the bot run through a <c>prune</c> — the one query
/// that lands inside the VACUUM waits on the busy timeout and, if it still cannot get in,
/// is answered with "try again in a moment" instead of taking the bot down.
/// </para>
/// </summary>
public sealed class TelegramBot
{
    /// <summary>Window the rate limit counts over.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What the bot says when it is asked what it does, and when it is asked to value
    /// nothing in particular. F5 turns the second case into the guided flow; until then
    /// the example is the whole documentation of the format.
    /// </summary>
    private const string Help =
        "Scrivimi il pezzo come lo leggi sull'oggetto e ti dico quanto vale, per esempio:\n" +
        "\n" +
        "Transcendent Sword Fire +7\n" +
        "ATK 1.404.374\n" +
        "DEF 3.359.312\n" +
        "skill si\n" +
        "CP 151.216.255\n" +
        "\n" +
        "L'ordine delle righe non conta, e va bene anche tutto su una riga. Rarità, tipo " +
        "ed elemento servono sempre; livello (+0 se non lo scrivi), skill, custom craft e " +
        "CP sono facoltativi.\n" +
        "\n" +
        "Rispondo con come ho letto il messaggio — così un errore di lettura si vede " +
        "subito — e con l'intervallo di prezzo delle inserzioni comparabili.";

    private readonly TelegramBotOptions _options;
    private readonly ITelegramUpdateSource _updates;
    private readonly IReplyChannel _replies;
    private readonly Func<MarketDb> _openDb;
    private readonly ValuationDefaults _defaults;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<long, ChatRate> _rates = new();

    public TelegramBot(
        TelegramBotOptions options,
        ITelegramUpdateSource updates,
        IReplyChannel replies,
        Func<MarketDb> openDb,
        ValuationDefaults defaults,
        Action<string>? log = null,
        Func<DateTime>? clock = null)
    {
        _options = options;
        _updates = updates;
        _replies = replies;
        _openDb = openDb;
        _defaults = defaults;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Polls and answers until <paramref name="ct"/> is cancelled, which is the only
    /// ordinary way out. A conflict with another poller and a refusal no retry fixes are
    /// thrown to the caller; everything else — a network blip, a crooked message, a busy
    /// database — is handled here, because a bot that stops on the first of those is a bot
    /// that is down every morning.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var offset = LoadOffset();
        Log($"In ascolto su {_defaults.Planet.Name}: {_options.AllowedChats.Count} chat " +
            $"autorizzate, offset {offset.ToString(CultureInfo.InvariantCulture)}.");

        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<TelegramUpdate> updates;
            try
            {
                updates = await _updates.GetUpdatesAsync(offset, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException e)
            {
                Log($"Lettura dei messaggi fallita, riprovo: {e.Message}");
                if (!await DelayAsync(_options.RetryDelay, ct))
                {
                    break;
                }

                continue;
            }

            foreach (var update in updates)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await HandleAsync(update, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    // One message must not cost the process. The offset advances below
                    // even in this case: a message that makes the bot throw would
                    // otherwise be read again at every poll, forever, and nothing behind
                    // it would ever be answered.
                    Log($"Messaggio non gestito ({update.UpdateId.ToString(CultureInfo.InvariantCulture)}): {e.Message}");
                }

                offset = update.UpdateId + 1;
                SaveOffset(offset);
            }
        }
    }

    /// <summary>
    /// Answers one update, or deliberately does not. The two silences — an update that
    /// carries no text, and a chat that is not on the allowlist — are the only paths that
    /// produce nothing at all.
    /// </summary>
    private async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        if (update.ChatId == 0 || string.IsNullOrWhiteSpace(update.Text))
        {
            return;
        }

        if (!_options.AllowedChats.Contains(update.ChatId))
        {
            Log($"Messaggio ignorato: la chat " +
                $"{update.ChatId.ToString(CultureInfo.InvariantCulture)} non è in " +
                $"{TelegramBotOptions.AllowedChatsVariable}.");
            return;
        }

        if (!Allow(update.ChatId, out var warn))
        {
            if (warn)
            {
                await _replies.SendAsync(
                    update.ChatId,
                    MarkdownV2.Escape(
                        "Troppi messaggi: ne leggo al massimo " +
                        _options.MessagesPerMinute.ToString(CultureInfo.InvariantCulture) +
                        " al minuto. Riprova fra poco."),
                    ct);
            }

            return;
        }

        // Everything the answer is made of comes from a parsed enum, a number or the
        // fixed Italian text of this project — nothing of the sender's survives into it —
        // so the whole reply can be escaped in one call. There is no entity in it to
        // break, which is what makes that safe (see ValuationMessage).
        await _replies.SendAsync(update.ChatId, MarkdownV2.Escape(Answer(update.Text!)), ct);
    }

    /// <summary>
    /// The text to answer with. A command is dispatched on its first token; anything else
    /// is a piece to value, because that is what people write to this bot.
    /// </summary>
    private string Answer(string message)
    {
        var text = message.Trim();
        if (!text.StartsWith('/'))
        {
            return Valuation(text);
        }

        var end = text.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        var command = (end < 0 ? text : text[..end]).ToLowerInvariant();
        var rest = end < 0 ? "" : text[(end + 1)..];

        // In a group Telegram delivers '/valuta@NomeDelBot': the mention says who is
        // being addressed, it is not part of the command.
        var at = command.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            command = command[..at];
        }

        return command switch
        {
            "/start" or "/help" or "/aiuto" => Help,
            "/valuta" => string.IsNullOrWhiteSpace(rest) ? Help : Valuation(rest),
            _ => $"Non conosco il comando '{command}'. Ho /valuta e /aiuto — e per una " +
                 "valutazione il comando non serve: scrivimi il pezzo e basta.",
        };
    }

    /// <summary>
    /// A piece, read and valued. A message the parser cannot read is answered with what
    /// it could not read and why: this is a conversation, and an exception raised inside
    /// a polling loop would reach the sender as a silence.
    /// </summary>
    private string Valuation(string text)
    {
        if (!ValuationRequestParser.TryParse(
                text, _defaults.Planet, out var parsed, out var error))
        {
            return error!;
        }

        var query = parsed! with
        {
            MinSamples = _defaults.MinSamples,
            SinceUtc = _defaults.Days is int days ? _clock().AddDays(-days) : null,
        };

        try
        {
            using var db = _openDb();
            var result = new ValuationService(db).Evaluate(query);
            return ValuationMessage.Echo(query) + "\n\n" + ValuationMessage.Answer(query, result);
        }
        catch (SqliteException e)
        {
            // Almost always the VACUUM of a scheduled prune. It lasts seconds, and saying
            // so is a better answer than a valuation that never arrives.
            Log($"Database non disponibile: {e.Message}");
            return "Il database è occupato da un altro comando (di solito la manutenzione " +
                   "periodica): riprova fra qualche secondo.";
        }
    }

    /// <summary>
    /// Whether this chat has any allowance left, and whether it has just run out. The
    /// warning goes out once per window: a chat over the limit is told the first time, so
    /// that the silence has an explanation, and then left alone — the answer to too many
    /// messages cannot be more messages.
    /// </summary>
    private bool Allow(long chatId, out bool warn)
    {
        warn = false;
        var now = _clock();
        if (!_rates.TryGetValue(chatId, out var rate))
        {
            rate = new ChatRate();
            _rates[chatId] = rate;
        }

        while (rate.Times.Count > 0 && now - rate.Times.Peek() >= RateWindow)
        {
            rate.Times.Dequeue();
            rate.Warned = false;
        }

        if (rate.Times.Count >= _options.MessagesPerMinute)
        {
            warn = !rate.Warned;
            rate.Warned = true;
            Log("Limite di frequenza raggiunto dalla chat " +
                chatId.ToString(CultureInfo.InvariantCulture) + ".");
            return false;
        }

        rate.Times.Enqueue(now);
        return true;
    }

    /// <summary>What one chat has sent lately, and whether it has been told to slow down.</summary>
    private sealed class ChatRate
    {
        public Queue<DateTime> Times { get; } = new();

        public bool Warned { get; set; }
    }

    /// <summary>
    /// Where to resume from. Without this a restart either rereads the queue — answering
    /// every message a second time — or skips it, and which of the two happens would be
    /// decided by Telegram's own retention rather than by anything here.
    /// </summary>
    private long LoadOffset()
    {
        using var db = _openDb();
        return db.GetTelegramOffset() ?? 0;
    }

    private void SaveOffset(long offset)
    {
        using var db = _openDb();
        db.SetTelegramOffset(offset);
    }

    /// <summary>Waits, and says whether the wait ended on its own or on cancellation.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Log(string message) => _log?.Invoke(message);
}
