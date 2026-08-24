using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class MarkdownV2Tests
{
    [Fact]
    public void Every_character_with_a_meaning_is_escaped()
    {
        // La regola di Telegram è cieca: ognuno di questi va preceduto da un backslash
        // ovunque compaia come testo, anche dove non potrebbe essere letto come markup.
        Assert.Equal(
            @"\_\*\[\]\(\)\~\`\>\#\+\-\=\|\{\}\.\!\\",
            MarkdownV2.Escape(@"_*[]()~`>#+-=|{}.!\"));
    }

    [Fact]
    public void Text_without_them_comes_out_as_it_went_in()
    {
        Assert.Equal("Valkyrie Ring", MarkdownV2.Escape("Valkyrie Ring"));
        Assert.Equal("", MarkdownV2.Escape(""));
    }

    [Fact]
    public void A_code_span_escapes_only_what_would_close_it()
    {
        // Dentro un'entità code il punto non è markup: è la ragione per cui un prezzo si
        // legge "6.00" e non "6\.00".
        Assert.Equal("`6.00 NCG`", MarkdownV2.Code("6.00 NCG"));
        Assert.Equal(@"`a\`b\\c`", MarkdownV2.Code(@"a`b\c"));
    }
}
