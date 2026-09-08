using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NCMarket.Core;

/// <summary>
/// Credentials of the Telegram bot that delivers the alerts: the token issued by
/// <c>@BotFather</c> and the chat the messages go to (a personal chat, a group, or a
/// channel the bot administers; the id of a group or a channel is negative).
/// <para>
/// They are read from the environment and never from the command line: an option ends up
/// in the shell history, in the process list of the machine and in the scheduled task of
/// whoever deploys this, and a bot token is a bearer credential — anyone holding it can
/// write as the bot.
/// </para>
/// </summary>
public sealed record TelegramOptions
{
    /// <summary>Environment variable holding the bot token.</summary>
    public const string TokenVariable = "NCMARKET_TELEGRAM_TOKEN";

    /// <summary>Environment variable holding the destination chat id.</summary>
    public const string ChatVariable = "NCMARKET_TELEGRAM_CHAT_ID";

    public required string Token { get; init; }

    /// <summary>
    /// Chat the alerts go to. Null for the bot of <see cref="TelegramBot"/>, which has
    /// none: it answers whoever wrote to it, so the destination is a property of the
    /// message and not of the configuration. <see cref="TryFromEnvironment"/> still
    /// requires it, because a job that announces has to know where.
    /// </summary>
    public string? ChatId { get; init; }

    /// <summary>
    /// Reads the configuration from the environment. Returns false — naming the variables
    /// that are missing — instead of half-configured options, so that a job which cannot
    /// deliver says so before spending the minutes of a download rather than after.
    /// </summary>
    public static bool TryFromEnvironment(out TelegramOptions? options, out string? error)
    {
        options = null;
        error = null;

        var token = Environment.GetEnvironmentVariable(TokenVariable);
        var chat = Environment.GetEnvironmentVariable(ChatVariable);

        var missing = new List<string>(2);
        if (string.IsNullOrWhiteSpace(token))
        {
            missing.Add(TokenVariable);
        }

        if (string.IsNullOrWhiteSpace(chat))
        {
            missing.Add(ChatVariable);
        }

        if (missing.Count > 0)
        {
            error =
                "Notifiche Telegram non configurate: manca " + string.Join(" e ", missing) +
                ". Il token si ottiene da @BotFather; l'id della chat scrivendo al bot e " +
                "leggendo https://api.telegram.org/bot<token>/getUpdates (per un gruppo o " +
                "un canale l'id è negativo).";
            return false;
        }

        options = new TelegramOptions { Token = token!.Trim(), ChatId = chat!.Trim() };
        return true;
    }
}

/// <summary>
/// Delivers alerts through the Telegram Bot API.
/// <para>
/// Telegram's own "webhook" is the opposite direction — an address Telegram calls to hand
/// a bot the messages people send it — and there is nothing here to receive: an alert is
/// outbound, so it is a <c>POST</c> to
/// <c>https://api.telegram.org/bot&lt;token&gt;/sendMessage</c>. Which is also why the
/// machine running the job needs no public address, no inbound port and no certificate.
/// </para>
/// </summary>
public sealed class TelegramNotifier : INotificationChannel, IReplyChannel, IDisposable
{
    /// <summary>
    /// Characters Telegram accepts in one message. A longer text is not truncated by the
    /// API, it is refused, so <see cref="SendAsync"/> splits instead.
    /// </summary>
    public const int MaxMessageLength = 4096;

    /// <summary>
    /// What an error message is allowed to say about the endpoint. The real URL carries
    /// the token in its path, and an error message ends up in a log, on a console, in a
    /// paste to somebody else: it must not be where the credential leaks from.
    /// </summary>
    private const string SafeUrl = "https://api.telegram.org/bot<token>/sendMessage";

    /// <summary>Pause between the parts of a split message, which Telegram throttles.</summary>
    private static readonly TimeSpan PartDelay = TimeSpan.FromMilliseconds(500);

    private readonly TelegramOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>
    /// Set once Telegram has refused to parse a message: from then on this channel sends
    /// without a parse mode. It is state of the instance rather than of one request
    /// because a split alert is several requests, and the parts after the first would
    /// otherwise walk into the same refusal one at a time.
    /// </summary>
    private bool _plainText;

    public TelegramNotifier(TelegramOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Name => "Telegram";

    /// <summary>
    /// Sends the message, in as many parts as <see cref="MaxMessageLength"/> requires.
    /// Parts break on line boundaries, so an alert never splits in the middle of a
    /// listing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Telegram refused the message (wrong token, unknown chat, a bot the recipient never
    /// started) or kept failing after three attempts.
    /// </exception>
    public Task SendAsync(string message, CancellationToken ct = default) =>
        SendAsync(
            message,
            _options.ChatId
            ?? throw new InvalidOperationException(
                "Questo canale non ha una chat di destinazione: è stato costruito per " +
                "rispondere a chi scrive (vedi TelegramBot), non per annunciare."),
            keyboard: null,
            ct);

    /// <summary>
    /// Sends the message to <paramref name="chatId"/>, with the buttons of
    /// <paramref name="keyboard"/> under it, which is how the bot answers the chat that
    /// asked: same splitting, same retries, same fallback to unparsed text as an alert,
    /// because none of that has anything to do with who is being written to.
    /// </summary>
    /// <inheritdoc cref="SendAsync(string, CancellationToken)"/>
    public Task SendAsync(
        long chatId,
        string message,
        InlineKeyboard? keyboard = null,
        CancellationToken ct = default) =>
        SendAsync(message, chatId.ToString(CultureInfo.InvariantCulture), keyboard, ct);

    /// <summary>
    /// Tells Telegram the press was heard, which is what stops the little clock the
    /// sender's client spins on the button. It is deliberately neither retried nor
    /// reported: the answer to the press is already on its way through
    /// <see cref="SendAsync(long, string, InlineKeyboard?, CancellationToken)"/>, and a
    /// lost acknowledgement costs a spinner Telegram gives up on by itself — while
    /// throwing here would cost the answer.
    /// </summary>
    public async Task AcknowledgeAsync(string callbackId, CancellationToken ct = default)
    {
        var url = $"https://api.telegram.org/bot{_options.Token}/answerCallbackQuery";
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["callback_query_id"] = callbackId });

        try
        {
            using var response = await _http.PostAsync(url, content, ct);
        }
        catch (Exception e)
            when (e is HttpRequestException
                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(
        string message, string chatId, InlineKeyboard? keyboard, CancellationToken ct)
    {
        var parts = Split(message, MaxMessageLength);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(PartDelay, ct);
            }

            // The keyboard belongs to the message it is about, which is the last part of
            // it: buttons halfway up a split answer would sit above the range they offer
            // to take apart.
            await PostAsync(parts[i], chatId, i == parts.Count - 1 ? keyboard : null, ct);
        }
    }

    /// <summary>
    /// Cuts a text into parts of at most <paramref name="max"/> characters, breaking
    /// between lines: as long as every line fits, joining the parts back with a newline
    /// returns the original text minus any trailing one. A single line longer than the
    /// limit — which the alerts do not produce, but a caller might — is cut where the
    /// limit falls, and there the break is inside the line rather than between two.
    /// <para>
    /// Parts that would come out empty are dropped: an alert ending in a newline can
    /// land exactly on the limit, and Telegram answers 400 to a message with no text.
    /// </para>
    /// <para>
    /// Cutting between lines is also what keeps each part parseable on its own: the
    /// markup of an alert never spans a newline (see <see cref="DealMessage"/>), so no
    /// part can begin or end inside an entity Telegram would then refuse.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Split(string message, int max)
    {
        if (message.Length <= max)
        {
            return new[] { message };
        }

        var parts = new List<string>();
        var current = new StringBuilder();

        // Tells "the part being built is empty" from "there is no part being built": a
        // blank line belongs to the part it was found in, an absent one to nothing.
        var open = false;

        void Emit()
        {
            if (current.Length > 0)
            {
                parts.Add(current.ToString());
            }

            current.Clear();
            open = false;
        }

        foreach (var line in message.Split('\n'))
        {
            var rest = line;
            while (rest.Length > max)
            {
                Emit();
                parts.Add(rest[..max]);
                rest = rest[max..];
            }

            // +1 for the newline that would rejoin this line to the ones before it.
            if (open && current.Length + 1 + rest.Length > max)
            {
                Emit();
            }

            if (open)
            {
                current.Append('\n');
            }

            current.Append(rest);
            open = true;
        }

        Emit();
        return parts;
    }

    private async Task PostAsync(
        string text, string chatId, InlineKeyboard? keyboard, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{_options.Token}/sendMessage";
        var delay = TimeSpan.FromSeconds(1);
        var lastError = default(Exception);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(delay, ct);
                delay += delay;
            }

            HttpResponseMessage response;
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["chat_id"] = chatId,
                    ["text"] = text,
                    ["disable_web_page_preview"] = "true",
                };

                // The alerts are written in MarkdownV2 (see DealMessage), which is what
                // makes a price stand out from the sentence around it. Everything they
                // insert is escaped — but if some name still gets through unescaped, the
                // message goes out unparsed rather than not at all.
                if (!_plainText)
                {
                    form["parse_mode"] = "MarkdownV2";
                }

                // The buttons are markup of their own and survive the fallback above: a
                // message Telegram would not parse is worth sending unparsed, and it is
                // worth sending with the follow-ups it was going to offer.
                if (keyboard is not null)
                {
                    form["reply_markup"] = keyboard.ToJson();
                }

                using var content = new FormUrlEncodedContent(form);

                response = await _http.PostAsync(url, content, ct);
            }
            catch (HttpRequestException e)
            {
                lastError = e;
                continue;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                lastError = e; // HttpClient timeout
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var (description, retryAfter) = await ReadErrorAsync(response, ct);
                var failure = Failure(response, description);

                // "Bad Request: can't parse entities: ...". An escaping mistake would
                // otherwise cost the whole alert — and, since nothing is recorded when a
                // send fails, cost it again at every run. Sending the same text without
                // markup costs a few backslashes in view, which is the cheaper failure.
                if (!_plainText && IsParseFailure(response.StatusCode, description))
                {
                    _plainText = true;
                    lastError = new HttpRequestException(failure);
                    continue;
                }

                if (!IsTransient(response.StatusCode))
                {
                    // A wrong token, an unknown chat, a bot the recipient never started:
                    // no number of attempts fixes any of them, and the description is
                    // what says which one it is.
                    throw new InvalidOperationException(failure);
                }

                // A 429 carries the wait Telegram expects: honouring it is what keeps the
                // next attempt from being throttled harder than this one.
                if (retryAfter is TimeSpan wait)
                {
                    delay = wait;
                }

                lastError = new HttpRequestException(failure);
            }
        }

        throw new InvalidOperationException(
            $"Impossibile inviare la notifica Telegram ({SafeUrl}) dopo 3 tentativi.", lastError);
    }

    private static string Failure(HttpResponseMessage response, string? description) =>
        $"Telegram ha risposto {(int)response.StatusCode} ({response.ReasonPhrase}) a {SafeUrl}" +
        (string.IsNullOrWhiteSpace(description) ? "." : $": {description}.");

    /// <summary>
    /// The <c>description</c> Telegram attaches to a refusal ("chat not found",
    /// "Unauthorized"), which is the whole diagnosis, and the <c>retry_after</c> of a
    /// throttled request. A body that is not the expected JSON — a proxy between here and
    /// Telegram answering HTML — leaves the status code to explain what happened.
    /// </summary>
    private static async Task<(string? Description, TimeSpan? RetryAfter)> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var description = root.TryGetProperty("description", out var d)
                && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            var retryAfter = root.TryGetProperty("parameters", out var p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("retry_after", out var r)
                && r.TryGetInt32(out var seconds)
                && seconds > 0
                    ? TimeSpan.FromSeconds(Math.Min(seconds, 60))
                    : (TimeSpan?)null;

            return (description, retryAfter);
        }
        catch (Exception e) when (e is JsonException or HttpRequestException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Whether the refusal is about the markup rather than about the request: Telegram
    /// answers 400 to both a message it cannot parse and a chat that does not exist, and
    /// only the first of the two is worth sending again.
    /// </summary>
    private static bool IsParseFailure(HttpStatusCode status, string? description) =>
        status == HttpStatusCode.BadRequest &&
        description is not null &&
        description.Contains("parse", StringComparison.OrdinalIgnoreCase);

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
