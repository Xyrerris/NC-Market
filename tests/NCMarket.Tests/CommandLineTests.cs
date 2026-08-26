using NCMarket.Cli;

namespace NCMarket.Tests;

public sealed class CommandLineTests
{
    private static CommandLine Parse(string verb, params string[] args)
    {
        Assert.True(
            CommandLine.TryParse(verb, args, out var parsed, out var error),
            $"Il parser ha rifiutato una riga di comando valida: {error}");
        return parsed!;
    }

    private static string Reject(string verb, params string[] args)
    {
        Assert.False(
            CommandLine.TryParse(verb, args, out _, out var error),
            "Il parser ha accettato una riga di comando che doveva rifiutare.");
        return error!;
    }

    [Fact]
    public void An_unknown_verb_is_rejected()
    {
        Assert.Contains("dealz", Reject("dealz"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mistyped_option_is_rejected_by_name()
    {
        // Il caso che il parser esiste per intercettare: prima --dicount veniva raccolto
        // e ignorato, e i risultati sembravano filtrati.
        var error = Reject("deals", "--dicount", "30");

        Assert.Contains("--dicount", error, StringComparison.Ordinal);
        Assert.Contains("--discount", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_belonging_to_another_verb_is_rejected()
    {
        Assert.Contains(
            "--discount", Reject("stats", "--discount", "30"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_argument_is_rejected()
    {
        Assert.Contains("'30'", Reject("deals", "30"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_option_is_rejected()
    {
        Assert.Contains(
            "--top", Reject("stats", "--top", "5", "--top", "10"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_option_left_without_a_value_is_rejected()
    {
        Assert.Contains("--top", Reject("stats", "--top"), StringComparison.Ordinal);
        Assert.Contains("--top", Reject("stats", "--top", "--no-names"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_flag_does_not_swallow_the_next_option()
    {
        var options = Parse("prune", "--dry-run", "--days", "10");

        Assert.True(options.ContainsKey("dry-run"));
        Assert.Equal(10, options.GetInt("days", 365, min: 1));
    }

    [Fact]
    public void Option_names_are_case_insensitive()
    {
        Assert.Equal(7, Parse("stats", "--TOP", "7").GetInt("top", 30, min: 1));
    }

    [Fact]
    public void A_missing_option_falls_back_to_its_default()
    {
        Assert.Equal(30, Parse("stats").GetInt("top", 30, min: 1));
    }

    [Theory]
    [InlineData("--top", "-5")]
    [InlineData("--top", "0")]
    [InlineData("--top", "abc")]
    public void An_out_of_range_or_non_numeric_value_throws(string option, string value)
    {
        var options = Parse("stats", option, value);
        var error = Assert.Throws<ArgumentException>(() => options.GetInt("top", 30, min: 1));
        Assert.Contains("--top", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_above_the_maximum_throws()
    {
        var options = Parse("deals", "--discount", "300");
        Assert.Throws<ArgumentException>(() => options.GetInt("discount", 25, min: 0, max: 100));
    }

    [Fact]
    public void Notify_is_a_flag_and_does_not_swallow_the_option_after_it()
    {
        var options = Parse("deals", "--notify", "--discount", "30");

        Assert.True(options.ContainsKey("notify"));
        Assert.Equal(30, options.GetInt("discount", 25, min: 0, max: 100));
    }

    [Fact]
    public void Fetch_accepts_the_filters_the_service_applies()
    {
        var options = Parse(
            "fetch", "--type", "weapon", "--item-ids", "10181000,10182000", "--custom", "true");

        Assert.Equal("10181000,10182000", options.GetValueOrDefault("item-ids"));
        Assert.Equal("true", options.GetValueOrDefault("custom"));
    }

    /// <summary>
    /// The service documents <c>stat</c> alongside <c>itemIds</c> and <c>iconIds</c>, and
    /// it narrows nothing: an unknown value is answered 200 with the full listing. An
    /// option that silently does not apply is what this parser exists to prevent, so
    /// <c>--stat</c> is not one — asking for it is an error, not a filter.
    /// </summary>
    [Fact]
    public void The_filter_the_service_ignores_is_not_an_option()
    {
        Assert.Contains("--stat", Reject("fetch", "--stat", "ATK"), StringComparison.Ordinal);
    }

    [Fact]
    public void Notify_test_takes_no_options_at_all()
    {
        Parse("notify-test");

        // Le credenziali sono variabili d'ambiente: non c'è niente da passare, e
        // passarlo è un errore come ovunque nella CLI.
        Assert.Contains(
            "--planet", Reject("notify-test", "--planet", "odin"), StringComparison.Ordinal);
    }
}
