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
    /// How long a guided flow left halfway keeps waiting. It expires because it is the one
    /// thing in this bot that changes what a plain message means: a conversation forgotten
    /// a week ago would otherwise fold today's message into last week's half-described
    /// piece.
    /// </summary>
    public TimeSpan DialogExpiry { get; init; } = TimeSpan.FromMinutes(30);

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
    /// <summary>
    /// Sends <paramref name="message"/> to <paramref name="chatId"/>, with the buttons of
    /// <paramref name="keyboard"/> under it when there is something worth offering next.
    /// </summary>
    Task SendAsync(
        long chatId,
        string message,
        InlineKeyboard? keyboard = null,
        CancellationToken ct = default);

    /// <summary>
    /// Says that a press was heard. It is separate from the answer because it is not one:
    /// Telegram spins a small clock on the pressed button until this arrives, and the
    /// answer may take a database query longer.
    /// </summary>
    Task AcknowledgeAsync(string callbackId, CancellationToken ct = default);
}

/// <summary>
/// What the bot values pieces against: the planet, and the two knobs the CLI exposes. The
/// planet is not read from a message (see <see cref="ValuationRequestParser"/>) — a
/// message describes a piece, not where to look for it — but it <em>is</em> read from a
/// button, because "su odin" is the same piece asked about somewhere else.
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
/// The chatbot. It polls Telegram for messages and button presses, answers the ones
/// coming from a chat on the allowlist, and keeps doing so for as long as the process
/// lives.
/// <para>
/// Nothing here decides anything about a valuation: a message becomes a query in
/// <see cref="ValuationRequestParser"/>, the query becomes a range in
/// <see cref="ValuationService"/>, and the range becomes words in
/// <see cref="ValuationMessage"/>. What is left is everything that only matters because
/// the process stands up for days and anyone can write to it — the allowlist, the rate
/// limit, the offset that survives a restart, the rule that one bad message must not stop
/// the loop — plus the one thing that only matters because a conversation has two ends:
/// the guided flow.
/// </para>
/// <para>
/// <b>The guided flow is the only state kept in memory, and a restart loses it.</b> That
/// is affordable because it costs a repeated <c>/valuta</c>, and it is the honest shape of
/// the thing: a half-asked question belongs to a conversation happening now. What must
/// <em>not</em> be lost is a button under an answer already sent — a message sits on a
/// phone for weeks — so those carry the whole query with them (see
/// <see cref="ValuationCallback"/>) and work whoever restarted what.
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
    /// What the bot says when it is asked what it does. The example is the whole
    /// documentation of the free-text format — the fast path, for whoever has got the hang
    /// of it — and the line before last is the way in for everybody else.
    /// </summary>
    private static readonly string Help = string.Join('\n', new[]
    {
        MarkdownV2.Escape(
            "Scrivimi il pezzo come lo leggi sull'oggetto e ti dico quanto vale, per esempio:"),
        "",
        MarkdownV2.Code("Transcendent Sword Fire +7"),
        MarkdownV2.Code("ATK 1.404.374"),
        MarkdownV2.Code("DEF 3.359.312"),
        MarkdownV2.Code("skill si"),
        MarkdownV2.Code("CP 151.216.255"),
        "",
        MarkdownV2.Escape(
            "L'ordine delle righe non conta, e va bene anche tutto su una riga. Rarità, " +
            "tipo ed elemento servono sempre; livello (+0 se non lo scrivi), skill, " +
            "custom craft e CP sono facoltativi."),
        "",
        MarkdownV2.Escape("Se preferisci non scrivere niente, ") +
        MarkdownV2.Code("/valuta") +
        MarkdownV2.Escape(" te li chiede uno per uno, coi bottoni."),
        "",
        MarkdownV2.Escape(
            "Rispondo con come ho letto il messaggio — così un errore di lettura si vede " +
            "subito — e con l'intervallo di prezzo delle inserzioni comparabili."),
    });

    /// <summary>The eight rarities, four to a row so that each stays readable on a phone.</summary>
    private static readonly InlineKeyboard GradeButtons = InlineKeyboard.Wrap(
        Grades.All.Select(g => new InlineButton(
            g.ToString(), ValuationCallback.Encode(DialogField.Grade, (int)g))),
        perRow: 4);

    private static readonly InlineKeyboard TypeButtons = InlineKeyboard.Wrap(
        EquipmentTypes.All.Select(t => new InlineButton(
            t.ToString(), ValuationCallback.Encode(DialogField.Type, (int)t))),
        perRow: 3);

    private static readonly InlineKeyboard ElementButtons = InlineKeyboard.Wrap(
        Elementals.All.Select(e => new InlineButton(
            Elementals.Name(e), ValuationCallback.Encode(DialogField.Element, (int)e))),
        perRow: 3);

    private static readonly InlineKeyboard SkillButtons = InlineKeyboard.Wrap(
        new[]
        {
            new InlineButton("Con skill", ValuationCallback.Encode(DialogField.Skill, 1)),
            new InlineButton("Senza skill", ValuationCallback.Encode(DialogField.Skill, 0)),
        },
        perRow: 2);

    /// <summary>
    /// The last step is the only one that is typed, so it is the only one whose keyboard
    /// is a way out of typing: a piece with no option at all is a real piece, and asking
    /// for it in words would mean asking somebody to write "niente".
    /// </summary>
    private static readonly InlineKeyboard OptionButtons = InlineKeyboard.Wrap(
        new[]
        {
            new InlineButton("Nessuna opzione", ValuationCallback.Encode(DialogField.NoOptions)),
            new InlineButton("Annulla", ValuationCallback.Encode(DialogField.Cancel)),
        },
        perRow: 2);

    private readonly TelegramBotOptions _options;
    private readonly ITelegramUpdateSource _updates;
    private readonly IReplyChannel _replies;
    private readonly Func<MarketDb> _openDb;
    private readonly ValuationDefaults _defaults;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<long, ChatRate> _rates = new();
    private readonly Dictionary<long, Dialog> _dialogs = new();

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
    /// Answers one update, or deliberately does not. The two silences — an update that is
    /// neither text nor a press, and a chat that is not on the allowlist — are the only
    /// paths that produce nothing at all.
    /// </summary>
    private async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        if (update.ChatId == 0
            || (string.IsNullOrWhiteSpace(update.Text)
                && string.IsNullOrWhiteSpace(update.CallbackData)))
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

        // Before the database, and before the rate limit too: an acknowledgement is not an
        // answer, it is what tells the phone the button was heard. Even a press refused for
        // frequency deserves to stop spinning, and a failed acknowledgement must not cost
        // the answer that is about to be sent.
        if (update.CallbackId is string callbackId)
        {
            try
            {
                await _replies.AcknowledgeAsync(callbackId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log($"Conferma del bottone non riuscita: {e.Message}");
            }
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
                    ct: ct);
            }

            return;
        }

        var reply = update.CallbackData is string data
            ? Pressed(update.ChatId, data)
            : Answer(update.ChatId, update.Text!);

        await _replies.SendAsync(update.ChatId, reply.Text, reply.Keyboard, ct);
    }

    /// <summary>
    /// The answer to a written message. A command is dispatched on its first token;
    /// anything else is a piece to value, because that is what people write to this bot.
    /// </summary>
    private Reply Answer(long chatId, string message)
    {
        var text = message.Trim();
        if (!text.StartsWith('/'))
        {
            return Typed(chatId, text);
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

        // A command is a fresh start by definition: whatever half-asked question was open,
        // it is not what this message is about.
        var had = _dialogs.Remove(chatId);

        return command switch
        {
            "/start" or "/help" or "/aiuto" => new Reply(Help),
            "/valuta" => string.IsNullOrWhiteSpace(rest) ? Start(chatId) : Valuation(rest),
            "/annulla" => Plain(had
                ? "Va bene, lasciamo perdere. Quando vuoi ricominci con /valuta, oppure " +
                  "mi scrivi il pezzo e basta."
                : "Non c'era niente da annullare."),
            _ => Plain(
                $"Non conosco il comando '{command}'. Ho /valuta, /annulla e /aiuto — e " +
                "per una valutazione il comando non serve: scrivimi il pezzo e basta."),
        };
    }

    /// <summary>
    /// A message written while a guided flow is open. The flow only gets this far when it
    /// is waiting for the options, which are the one thing buttons cannot ask for.
    /// <para>
    /// What is typed is joined to what was pressed and the join is parsed as one message:
    /// one parser and one echo, so a piece described half by buttons and half by hand is
    /// answered exactly like one described entirely by hand.
    /// </para>
    /// <para>
    /// When the join does not parse but the message alone does, the message wins and the
    /// flow is dropped. That is somebody who opened the guided flow and then wrote the
    /// piece out in full from habit: the alternative is answering "due rarità" to a
    /// perfectly good message, which reads as a bot that has broken rather than one that
    /// was waiting.
    /// </para>
    /// </summary>
    private Reply Typed(long chatId, string text)
    {
        if (!TryDialog(chatId, out var dialog))
        {
            return Valuation(text);
        }

        // Either way the conversation ends here: with the answer it was waiting for, or
        // because a message arrived instead of the press it had asked for, and leaving the
        // keyboard alive would leave two half-questions open at once.
        _dialogs.Remove(chatId);
        if (!dialog.IsComplete)
        {
            return Valuation(text);
        }

        var joined = (dialog.Prefix() + " " + text).Trim();
        return ValuationRequestParser.TryParse(joined, _defaults.Planet, out _, out _)
               || !ValuationRequestParser.TryParse(text, _defaults.Planet, out _, out _)
            ? Valuation(joined)
            : Valuation(text);
    }

    /// <summary>
    /// The answer to a press. The two vocabularies are told apart by the data itself (see
    /// <see cref="ValuationCallback"/>), and data belonging to neither is named rather
    /// than ignored: a button that does nothing at all cannot be told from a bot that has
    /// stopped.
    /// </summary>
    private Reply Pressed(long chatId, string data)
    {
        if (ValuationCallback.TryDecodeDialog(data, out var field, out var value))
        {
            return Answered(chatId, field, value);
        }

        if (ValuationCallback.TryDecode(data, out var action, out var query))
        {
            // Widening and changing planet are already in the query the button carries:
            // what is left for the action to say is whether the answer is the range or the
            // listings under it.
            return action == ValuationAction.Comparables ? Listing(query!) : Valuation(query!);
        }

        Log($"Bottone non riconosciuto: '{data}'.");
        return Plain(
            "Questo bottone non lo conosco: sarà di una versione precedente del bot. " +
            "Riscrivimi il pezzo, o ricomincia con /valuta.");
    }

    /// <summary>One field of the guided flow, answered by a press.</summary>
    private Reply Answered(long chatId, DialogField field, int value)
    {
        if (field == DialogField.Cancel)
        {
            _dialogs.Remove(chatId);
            return Plain("Va bene, lasciamo perdere. Quando vuoi ricominci con /valuta.");
        }

        if (!TryDialog(chatId, out var dialog))
        {
            // In memory and only in memory: a restart, or half an hour of silence, and the
            // conversation is gone. Saying so costs one message, and is the whole price of
            // not keeping conversations in the database.
            return Plain(
                "Non ho più questa domanda in corso: mi sono riavviato, o è passato " +
                "troppo tempo. Ricomincia con /valuta.");
        }

        switch (field)
        {
            case DialogField.Grade when Enum.IsDefined(typeof(Grade), value):
                dialog.Grade = (Grade)value;
                break;
            case DialogField.Type when Enum.IsDefined(typeof(EquipmentType), value):
                dialog.Type = (EquipmentType)value;
                break;
            case DialogField.Element when Enum.IsDefined(typeof(ElementalType), value):
                dialog.Element = (ElementalType)value;
                break;
            case DialogField.Skill:
                dialog.HasSkill = value == 1;
                break;
            case DialogField.NoOptions when dialog.IsComplete:
                _dialogs.Remove(chatId);
                return Valuation(dialog.Prefix());
            default:
                Log("Bottone fuori posto: " + field +
                    " = " + value.ToString(CultureInfo.InvariantCulture) + ".");
                return Plain(
                    "Questo bottone non risponde alla domanda che ti ho fatto. " +
                    "Ricomincia con /valuta.");
        }

        dialog.TouchedUtc = _clock();
        return Ask(dialog);
    }

    /// <summary>Opens a guided flow for this chat, replacing whatever it had open.</summary>
    private Reply Start(long chatId)
    {
        var dialog = new Dialog { TouchedUtc = _clock() };
        _dialogs[chatId] = dialog;
        return Ask(dialog);
    }

    /// <summary>
    /// The first field still unanswered, with the buttons that answer it. Four of the six
    /// fields of a piece are enumerable — eight rarities, five types, five elements, skill
    /// or no skill — and a button is the difference between a field that cannot be got
    /// wrong and one that can.
    /// </summary>
    private static Reply Ask(Dialog dialog)
    {
        if (dialog.Grade is null)
        {
            return new Reply(MarkdownV2.Escape("Che rarità è il pezzo?"), GradeButtons);
        }

        if (dialog.Type is null)
        {
            return new Reply(MarkdownV2.Escape("Che tipo di pezzo è?"), TypeButtons);
        }

        if (dialog.Element is null)
        {
            return new Reply(MarkdownV2.Escape("Che elemento ha?"), ElementButtons);
        }

        if (dialog.HasSkill is null)
        {
            return new Reply(MarkdownV2.Escape("Ha una skill?"), SkillButtons);
        }

        return new Reply(
            MarkdownV2.Escape("Ultimo passo: scrivimi le opzioni, per esempio ") +
            MarkdownV2.Code("ATK 1.404.374 DEF 3.359.312") +
            MarkdownV2.Escape(".\nSe vuoi, aggiungi anche il livello (") +
            MarkdownV2.Code("+7") +
            MarkdownV2.Escape(") e il combat point (") +
            MarkdownV2.Code("CP 151.216.255") +
            MarkdownV2.Escape(")."),
            OptionButtons);
    }

    /// <summary>
    /// A piece, read and valued. A message the parser cannot read is answered with what
    /// it could not read and why: this is a conversation, and an exception raised inside
    /// a polling loop would reach the sender as a silence.
    /// </summary>
    private Reply Valuation(string text) =>
        ValuationRequestParser.TryParse(text, _defaults.Planet, out var parsed, out var error)
            ? Valuation(parsed!)
            : Plain(error!);

    /// <summary>The range, with the buttons that take it apart or widen it.</summary>
    private Reply Valuation(ValuationQuery query) =>
        Measure(query, (settled, result) => new Reply(
            ValuationMessage.Echo(settled) + "\n\n" + ValuationMessage.Answer(settled, result),
            Follow(settled, result)));

    /// <summary>The listings behind the range: the same measurement, said in full.</summary>
    private Reply Listing(ValuationQuery query) =>
        Measure(query, (settled, result) =>
            new Reply(ValuationMessage.Comparables(settled, result)));

    /// <summary>
    /// Runs a valuation and says it. The settings of the running bot are applied here and
    /// not by the callers, so that a query typed out and the same query pressed on a
    /// button are measured the same way — and so that the history window is an instant
    /// taken now rather than one taken at startup.
    /// </summary>
    private Reply Measure(ValuationQuery query, Func<ValuationQuery, ValuationResult, Reply> say)
    {
        var settled = query with
        {
            MinSamples = _defaults.MinSamples,
            SinceUtc = _defaults.Days is int days ? _clock().AddDays(-days) : null,
        };

        try
        {
            using var db = _openDb();
            return say(settled, new ValuationService(db).Evaluate(settled));
        }
        catch (SqliteException e)
        {
            // Almost always the VACUUM of a scheduled prune. It lasts seconds, and saying
            // so is a better answer than a valuation that never arrives.
            Log($"Database non disponibile: {e.Message}");
            return Plain(
                "Il database è occupato da un altro comando (di solito la manutenzione " +
                "periodica): riprova fra qualche secondo.");
        }
    }

    /// <summary>
    /// What is worth offering under an answer. Every button re-asks a question that would
    /// otherwise cost typing the whole piece out again:
    /// <list type="bullet">
    /// <item>the comparables, when there are any — a range from 11 to 333 NCG cannot be
    /// used until the 333 has been seen for what it is;</item>
    /// <item>the same piece on every element, but only while the element is still
    /// narrowing something: on a bucket already widened past it, the button would promise
    /// a different answer and return the same one;</item>
    /// <item>the other planet, always. It is the one thing about the question that was
    /// never in the message.</item>
    /// </list>
    /// </summary>
    private static InlineKeyboard Follow(ValuationQuery query, ValuationResult result)
    {
        var buttons = new List<InlineButton>(3);

        if (result.Status == ValuationStatus.Ok && result.Listings.Count > 0)
        {
            buttons.Add(new InlineButton(
                "🔍 Vedi i comparabili",
                ValuationCallback.Encode(ValuationAction.Comparables, query)));
        }

        if (result.Step == ValuationStep.Exact)
        {
            buttons.Add(new InlineButton(
                "🌐 Senza elemento",
                ValuationCallback.Encode(
                    ValuationAction.Widen,
                    query with { StartStep = ValuationStep.AnyElement })));
        }

        var elsewhere = Planet.All.First(p => p.Name != query.Planet.Name);
        buttons.Add(new InlineButton(
            "🪐 Su " + elsewhere.Name,
            ValuationCallback.Encode(
                ValuationAction.OtherPlanet, query with { Planet = elsewhere })));

        return InlineKeyboard.Column(buttons.ToArray());
    }

    /// <summary>
    /// The guided flow of this chat, when there is one and it is still recent enough to be
    /// the conversation this message belongs to.
    /// </summary>
    private bool TryDialog(long chatId, out Dialog dialog)
    {
        if (_dialogs.TryGetValue(chatId, out dialog!))
        {
            if (_clock() - dialog.TouchedUtc <= _options.DialogExpiry)
            {
                return true;
            }

            _dialogs.Remove(chatId);
        }

        dialog = null!;
        return false;
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

    /// <summary>Prose, escaped, so Telegram prints it exactly as it reads here.</summary>
    private static Reply Plain(string text) => new(MarkdownV2.Escape(text));

    /// <summary>What one chat has sent lately, and whether it has been told to slow down.</summary>
    private sealed class ChatRate
    {
        public Queue<DateTime> Times { get; } = new();

        public bool Warned { get; set; }
    }

    /// <summary>
    /// A piece being described one button at a time. It holds the four enumerable fields
    /// and the instant it was last touched, and nothing else: the options are typed, and
    /// what is typed goes straight to the parser.
    /// </summary>
    private sealed class Dialog
    {
        public Grade? Grade { get; set; }

        public EquipmentType? Type { get; set; }

        public ElementalType? Element { get; set; }

        public bool? HasSkill { get; set; }

        public DateTime TouchedUtc { get; set; }

        /// <summary>Whether every button has been pressed, so that only the options are left.</summary>
        public bool IsComplete =>
            Grade is not null && Type is not null && Element is not null && HasSkill is not null;

        /// <summary>
        /// What was pressed, written as the message it stands for. The flow builds no
        /// <see cref="ValuationKey"/> of its own: it writes the free text a person would
        /// have typed and hands it to the same parser, so that there is one reading of a
        /// piece in this project rather than two that can drift apart.
        /// </summary>
        public string Prefix() =>
            $"{Grade} {Type} {Elementals.Name(Element!.Value)} " +
            $"skill {(HasSkill!.Value ? "si" : "no")}";
    }

    /// <summary>
    /// What goes back to a chat: the text, already in MarkdownV2, and what to offer next.
    /// The keyboard travels with the text because it is part of the same answer — buttons
    /// sent as a message of their own would be a message that says nothing on its own.
    /// </summary>
    private sealed record Reply(string Text, InlineKeyboard? Keyboard = null);

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
