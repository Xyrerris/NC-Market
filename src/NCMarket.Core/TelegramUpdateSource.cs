using System.Globalization;
using System.Net;
using System.Text.Json;

namespace NCMarket.Core;

/// <summary>
/// One thing Telegram has to hand over: an update, reduced to what a bot that answers
/// questions needs. Anything that is not a text message — a photo, someone joining a
/// group, an edited message — arrives with <see cref="Text"/> null and is still an
/// update: its <see cref="UpdateId"/> has to advance the offset like any other, or the
/// next poll asks for it again and the bot never moves past it.
/// </summary>
public sealed record TelegramUpdate(long UpdateId, long ChatId, string? Text);

/// <summary>
/// Where the messages written to the bot come from. It is an interface because the bot's
/// whole job is what it does with them, and that has to be testable without a network:
/// the implementation below is the only part of the bot that speaks HTTP inbound.
/// </summary>
public interface ITelegramUpdateSource
{
    /// <summary>
    /// Waits for the updates from <paramref name="offset"/> onwards, returning an empty
    /// list when the long poll expires with nothing to report.
    /// </summary>
    /// <exception cref="TelegramConflictException">
    /// Another process is polling the same token.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Telegram refused the request for a reason no retry fixes (a wrong token).
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// The request failed in a way that may not happen again (network, 5xx, throttling).
    /// </exception>
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken ct);
}

/// <summary>
/// Two processes are reading the same bot. Telegram hands <c>getUpdates</c> to one caller
/// at a time and answers 409 to the other, so this is what a redeploy that left the old
/// container alive looks like from inside the new one.
/// <para>
/// It is a type of its own because it must not be retried: the two instances would keep
/// taking the conflict from each other until one happened to win, and the symptom of that
/// — messages answered twice, or not at all, at random — says nothing about its cause.
/// </para>
/// </summary>
public sealed class TelegramConflictException : InvalidOperationException
{
    public TelegramConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reads the messages written to the bot with <c>getUpdates</c> long polling.
/// <para>
/// <b>Long polling, not a webhook</b>, for the same reason an alert is a plain
/// <c>POST</c> (see <see cref="TelegramNotifier"/>): a webhook is an address Telegram
/// calls, and the machine running this would then need a public name, an inbound port and
/// a certificate. Here the connection is opened from the inside and held until Telegram
/// has something to say, which costs one request per
/// <see cref="TelegramBotOptions.PollTimeout"/> and no infrastructure at all.
/// </para>
/// <para>
/// The offset is not kept here. It belongs to the bot — it survives a restart only if it
/// is written down (see <see cref="MarketDb.SetTelegramOffset"/>), and a poller that held
/// it in a field would quietly reread or lose a queue at every restart.
/// </para>
/// </summary>
public sealed class TelegramUpdateSource : ITelegramUpdateSource, IDisposable
{
    /// <summary>
    /// What an error message may say about the endpoint: the real URL carries the token
    /// in its path, and an error message ends up in a log.
    /// </summary>
    private const string SafeUrl = "https://api.telegram.org/bot<token>/getUpdates";

    /// <summary>
    /// Only messages are asked for. Telegram sends a bot the update types it subscribes
    /// to and nothing else, so the button callbacks of F5 will have to be listed here
    /// too — an update type left out is not an error, it is silence.
    /// </summary>
    private const string AllowedUpdates = "[\"message\"]";

    private readonly string _token;
    private readonly TimeSpan _pollTimeout;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public TelegramUpdateSource(string token, TimeSpan pollTimeout, HttpClient? http = null)
    {
        _token = token;
        _pollTimeout = pollTimeout;
        _ownsHttp = http is null;

        // The client has to outwait the long poll it is making, and by enough that a slow
        // answer is not mistaken for a dead connection every single time.
        _http = http ?? new HttpClient { Timeout = pollTimeout + TimeSpan.FromSeconds(30) };
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long offset, CancellationToken ct)
    {
        var url =
            $"https://api.telegram.org/bot{_token}/getUpdates" +
            $"?offset={offset.ToString(CultureInfo.InvariantCulture)}" +
            $"&timeout={((int)_pollTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)}" +
            $"&allowed_updates={Uri.EscapeDataString(AllowedUpdates)}";

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            // The client's own timeout: the long poll should have answered first, so this
            // is a connection that went away rather than a poll with nothing in it.
            throw new HttpRequestException($"Nessuna risposta da {SafeUrl}.", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                return Parse(body);
            }

            var description = Description(body);
            var failure =
                $"Telegram ha risposto {(int)response.StatusCode} ({response.ReasonPhrase}) " +
                $"a {SafeUrl}" +
                (string.IsNullOrWhiteSpace(description) ? "." : $": {description}.");

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new TelegramConflictException(
                    "Un'altra istanza sta già leggendo i messaggi di questo bot: Telegram " +
                    "consegna gli aggiornamenti a un lettore per volta. Ferma il vecchio " +
                    "processo (tipicamente un container rimasto vivo dopo un redeploy) e " +
                    $"riavvia. {failure}");
            }

            if (IsTransient(response.StatusCode))
            {
                throw new HttpRequestException(failure);
            }

            throw new InvalidOperationException(failure);
        }
    }

    /// <summary>
    /// The updates of a successful answer. An update whose message carries no text — a
    /// photo, someone joining a group — is kept without one: it costs nothing to ignore,
    /// and dropping it here would drop its id with it.
    /// </summary>
    private static List<TelegramUpdate> Parse(string body)
    {
        var updates = new List<TelegramUpdate>();
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return updates;
        }

        foreach (var element in result.EnumerateArray())
        {
            if (!element.TryGetProperty("update_id", out var id)
                || !id.TryGetInt64(out var updateId))
            {
                continue;
            }

            var chatId = 0L;
            string? text = null;
            if (element.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("chat", out var chat)
                    && chat.ValueKind == JsonValueKind.Object
                    && chat.TryGetProperty("id", out var chatIdElement))
                {
                    chatIdElement.TryGetInt64(out chatId);
                }

                if (message.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    text = textElement.GetString();
                }
            }

            updates.Add(new TelegramUpdate(updateId, chatId, text));
        }

        return updates;
    }

    /// <summary>
    /// The <c>description</c> of a refusal, which is the whole diagnosis. A body that is
    /// not the expected JSON — a proxy between here and Telegram answering HTML — leaves
    /// the status code to explain what happened.
    /// </summary>
    private static string? Description(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("description", out var d)
                   && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
