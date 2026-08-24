using System.Net;
using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class TelegramNotifierTests
{
    /// <summary>Not a real credential, but shaped like one so a leak would be visible.</summary>
    private const string Token = "123456:AA-token-di-prova";

    private const string Chat = "-1001234567890";

    private static TelegramOptions Options => new() { Token = Token, ChatId = Chat };

    [Fact]
    public async Task The_message_is_posted_to_the_configured_chat()
    {
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using var notifier = new TelegramNotifier(Options, http);

        await notifier.SendAsync("una occasione");

        Assert.Equal(
            $"https://api.telegram.org/bot{Token}/sendMessage", Assert.Single(handler.Urls));
        Assert.Contains(
            $"chat_id={Uri.EscapeDataString(Chat)}", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("una+occasione", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_is_reported_with_its_reason_and_without_the_token()
    {
        var handler = new FakeHttpHandler().Answering(
            HttpStatusCode.Unauthorized,
            """{"ok":false,"error_code":401,"description":"Unauthorized"}""");
        using var http = new HttpClient(handler);
        using var notifier = new TelegramNotifier(Options, http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.SendAsync("una occasione"));

        // Il token sta nel percorso dell'URL: un messaggio d'errore finisce in un log,
        // su una console, in un incolla a qualcun altro.
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.Contains("Unauthorized", error.Message, StringComparison.Ordinal);

        // Una risposta definitiva non si ritenta: sei secondi di attesa non cambiano un
        // token sbagliato.
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task The_message_is_sent_as_MarkdownV2()
    {
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using var notifier = new TelegramNotifier(Options, http);

        await notifier.SendAsync("*una occasione*");

        Assert.Contains(
            "parse_mode=MarkdownV2", handler.Bodies[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Un errore di escaping costerebbe l'intera segnalazione — e, siccome un invio
    /// fallito non registra niente, la costerebbe di nuovo a ogni esecuzione. Il testo
    /// riparte senza markup: qualche backslash a vista è il modo più economico di
    /// sbagliare.
    /// </summary>
    [Fact]
    public async Task A_message_Telegram_cannot_parse_goes_out_without_markup()
    {
        var handler = new FakeHttpHandler().Answering(
            HttpStatusCode.BadRequest,
            """
            {"ok":false,"error_code":400,
             "description":"Bad Request: can't parse entities: unexpected end"}
            """);
        using var http = new HttpClient(handler);
        using var notifier = new TelegramNotifier(Options, http);

        await notifier.SendAsync("*una occasione");

        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("parse_mode", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("parse_mode", handler.Bodies[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Telegram risponde 400 anche a una chat inesistente, che nessun secondo invio
    /// sistema: è la distinzione fra un ripiego e un "ritenta sempre".
    /// </summary>
    [Fact]
    public async Task A_refusal_that_is_not_about_the_markup_is_not_sent_again()
    {
        var handler = new FakeHttpHandler().Answering(
            HttpStatusCode.BadRequest,
            """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");
        using var http = new HttpClient(handler);
        using var notifier = new TelegramNotifier(Options, http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.SendAsync("una occasione"));

        Assert.Contains("chat not found", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public void A_message_over_the_limit_is_split_between_its_lines()
    {
        var message = string.Join(
            "\n", Enumerable.Range(0, 400).Select(i => $"{i}) " + new string('x', 30)));
        Assert.True(message.Length > TelegramNotifier.MaxMessageLength);

        var parts = TelegramNotifier.Split(message, TelegramNotifier.MaxMessageLength);

        Assert.True(parts.Count > 1);
        Assert.All(parts, part =>
            Assert.True(part.Length <= TelegramNotifier.MaxMessageLength, "parte troppo lunga"));

        // Le parti sono tagli fra le righe, non dentro: rimetterle insieme restituisce il
        // messaggio, che è ciò che rende il taglio invisibile a chi legge.
        Assert.Equal(message, string.Join("\n", parts));
    }

    [Fact]
    public async Task Every_part_of_a_split_message_is_sent()
    {
        var message = string.Join("\n", Enumerable.Repeat(new string('x', 100), 60));
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using var notifier = new TelegramNotifier(Options, http);

        await notifier.SendAsync(message);

        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public void A_channel_without_credentials_names_the_variables_that_are_missing()
    {
        var token = Environment.GetEnvironmentVariable(TelegramOptions.TokenVariable);
        var chat = Environment.GetEnvironmentVariable(TelegramOptions.ChatVariable);
        try
        {
            Environment.SetEnvironmentVariable(TelegramOptions.TokenVariable, null);
            Environment.SetEnvironmentVariable(TelegramOptions.ChatVariable, Chat);

            Assert.False(TelegramOptions.TryFromEnvironment(out var options, out var error));

            Assert.Null(options);
            Assert.Contains(TelegramOptions.TokenVariable, error!, StringComparison.Ordinal);
            Assert.DoesNotContain(TelegramOptions.ChatVariable, error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelegramOptions.TokenVariable, token);
            Environment.SetEnvironmentVariable(TelegramOptions.ChatVariable, chat);
        }
    }

    [Fact]
    public void A_configured_channel_is_read_from_the_environment()
    {
        var token = Environment.GetEnvironmentVariable(TelegramOptions.TokenVariable);
        var chat = Environment.GetEnvironmentVariable(TelegramOptions.ChatVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                TelegramOptions.TokenVariable, "  " + Token + "  ");
            Environment.SetEnvironmentVariable(TelegramOptions.ChatVariable, Chat);

            Assert.True(TelegramOptions.TryFromEnvironment(out var options, out var error));

            Assert.Null(error);
            Assert.Equal(Token, options!.Token);
            Assert.Equal(Chat, options.ChatId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelegramOptions.TokenVariable, token);
            Environment.SetEnvironmentVariable(TelegramOptions.ChatVariable, chat);
        }
    }
}
