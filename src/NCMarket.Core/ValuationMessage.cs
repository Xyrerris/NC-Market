using System.Globalization;
using System.Text;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// The words a valuation is said in: the echo of how a message was read, the range that
/// answers it, and the listings that range was built on.
/// <para>
/// The echo belongs here and not to the presentation of the answer: it is the cheapest
/// defence a free-text parser has, and the answer is not worth showing until it is clear
/// which piece it is about. A misread message does not produce a visible failure. It
/// produces a valuation of a different piece, correct in every respect except the one that
/// matters, and nothing in its shape says so. One line of echo turns that into something
/// the reader catches at a glance — the same principle as <c>deals --dicount 30</c> being
/// an error instead of a filter that silently does not apply.
/// </para>
/// <para>
/// Two things the echo says differently from how they may have been written, and both on
/// purpose:
/// </para>
/// <list type="bullet">
/// <item>the type is named by its <see cref="EquipmentType"/> — <c>Weapon</c>, the name
/// the rest of the project prints — and not by the alias that was typed. Echoing
/// <c>Sword</c> back would reflect the writer's own word at them and confirm nothing;
/// worse, it would suggest the item was identified, which is exactly what a valuation
/// without an item id cannot do;</item>
/// <item>numbers are formatted invariantly, like every other figure the project prints,
/// so a combat point read here and the same one read in a deal alert or a CSV are written
/// the same way. The parser accepts the game's own <c>151.216.255</c> on the way in; what
/// comes back out is this project's spelling of it.</item>
/// </list>
/// <para>
/// <b>The text is <see cref="MarkdownV2"/>, which Telegram parses.</b> That is what sets
/// the figures apart from the sentences around them, and it comes with the rules the
/// alerts already follow (see <see cref="DealMessage"/>): every value goes through
/// <see cref="MarkdownV2.Code"/> and every fixed sentence through
/// <see cref="MarkdownV2.Escape"/> — here by way of <c>Text</c>, so that the escaping is
/// something the prose cannot forget rather than something each string has to remember.
/// No entity spans a newline, which is what lets <see cref="TelegramNotifier.Split"/> cut
/// a long list between its lines without leaving half an entity in either part.
/// </para>
/// </summary>
public static class ValuationMessage
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Comparables written out one by one before the list starts summarizing itself. A
    /// bucket is small by construction, but the widest rung of the ladder can hold
    /// hundreds, and past a screenful the list stops being what makes a range checkable
    /// and becomes something to scroll past.
    /// </summary>
    private const int MaxListings = 20;

    /// <summary>
    /// How <paramref name="query"/> was understood, in one line. Every field of the key
    /// appears, including the ones that were defaulted rather than written: a level
    /// nobody typed still shows as <c>+0</c>, because "I assumed +0" is the reading most
    /// worth contradicting.
    /// </summary>
    public static string Echo(ValuationQuery query)
    {
        var key = query.Key;
        var sb = new StringBuilder("🏷️ ")
            .Append(Text("Ho letto: "))
            .Append('*')
            .Append(Text(GradeName(key.Grade) + " " + key.Type))
            .Append('*')
            .Append(Text(" · " + Elementals.Name(key.Element)))
            .Append(Text(" · +" + key.Level.ToString(Culture)))
            .Append(Text(" · " + Options(key)))
            .Append(Text(key.HasSkill ? " · con skill" : " · senza skill"));

        // Custom craft shows only when it is on. It is a rarity, its default is no, and a
        // line that carries every negative answer stops being read.
        if (key.ByCustomCraft)
        {
            sb.Append(Text(" · custom craft"));
        }

        if (query.CombatPoint is int cp)
        {
            sb.Append(Text(" · CP ")).Append(Code(cp.ToString("N0", Culture)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The valuation itself: a range, what it was measured on, and what had to be given
    /// up to measure it. There is no single price anywhere in it, because
    /// <see cref="ValuationResult"/> does not carry one — inside a bucket the price
    /// follows neither the combat point nor the option values, so a number would be a
    /// precision nobody measured.
    /// <para>
    /// Every line after the range is there to keep the range from being read as more than
    /// it is: how many listings are behind it, whether those are sales or asking prices,
    /// how far the bucket had to be widened. With a database of two snapshots the honest
    /// answer is nearly always "this is what people ask", and a bot that said so only in
    /// its documentation would be a bot that never says so.
    /// </para>
    /// </summary>
    public static string Answer(ValuationQuery query, ValuationResult result)
    {
        if (result.Status != ValuationStatus.Ok || result.Prices is null)
        {
            return Insufficient(query, result);
        }

        var prices = result.Prices;
        var sb = new StringBuilder("💰 ")
            .Append(Ncg(prices.Min))
            .Append(Text(" – "))
            .Append(Ncg(prices.Max))
            .Append(Text(" · mediana "))
            .Append(Ncg(prices.Median))
            .Append('\n');

        sb.Append("📊 ")
          .Append(Code(result.Comparables.ToString("N0", Culture)))
          .Append(Text(result.Comparables == 1 ? " comparabile · " : " comparabili · "))
          .Append(Text(PopulationName(result.Population)))
          .Append(Text(" · "))
          .Append(Code(query.Planet.Name))
          .Append(Window(result));

        if (result.Step != ValuationStep.Exact)
        {
            // The key in the result is the piece as it was described, not the bucket that
            // was measured: only the step knows what was given up, so only the step can
            // say it.
            sb.Append("\n🔎 ")
              .Append(Text("Bucket allargato: " + result.StepDeclaration));
        }

        if (result.CpPercentile is int percentile)
        {
            sb.Append("\n📈 ")
              .Append(Text("Il CP del pezzo sta nel "))
              .Append(Code(percentile.ToString(Culture)))
              .Append(Text("° percentile del gruppo"));
        }

        if (result.Population == BaselinePopulation.Listed)
        {
            sb.Append("\n⚠️ ")
              .Append(Text(result.PopulationFallback
                  ? "Vendite osservate troppo poche: è quanto si chiede, non quanto si paga"
                  : "Prezzi richiesti: è quanto si chiede, non quanto si paga"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The listings the range was built on, cheapest first. A range from 11 to 333 NCG is
    /// unusable as a range: with the listings under it, the 333 is visibly one piece
    /// somebody priced at random, and the median visibly is not.
    /// <para>
    /// It repeats the echo, because it is a message of its own arriving under a button
    /// that may have been pressed on an answer from days ago — and a column of prices with
    /// nothing saying which piece they belong to would be worse than no column at all.
    /// </para>
    /// </summary>
    public static string Comparables(ValuationQuery query, ValuationResult result)
    {
        var sb = new StringBuilder(Echo(query)).Append("\n\n");
        var listings = result.Listings.OrderBy(l => l.Price).ToList();

        if (listings.Count == 0)
        {
            return sb.Append("🤷 ")
                     .Append(Text(
                         "Non ho comparabili da elencare: anche il bucket più largo della " +
                         "scala è rimasto vuoto."))
                     .ToString();
        }

        sb.Append("🔍 ")
          .Append(Code(listings.Count.ToString("N0", Culture)))
          .Append(Text(listings.Count == 1
              ? " comparabile:"
              : listings.Count <= MaxListings
                  ? " comparabili, dal più economico:"
                  : " comparabili, i " + MaxListings.ToString(Culture) + " più economici:"));

        foreach (var (listing, index) in listings.Take(MaxListings).Select((l, i) => (l, i + 1)))
        {
            sb.Append('\n').Append(Text(index.ToString(Culture) + ". "));
            Append(sb, listing);
        }

        if (listings.Count > MaxListings)
        {
            sb.Append("\n➕ ")
              .Append(Text("Altri "))
              .Append(Code((listings.Count - MaxListings).ToString("N0", Culture)))
              .Append(Text(" non elencati, tutti più cari di questi."));
        }

        return sb.ToString();
    }

    /// <summary>
    /// One listing on one line: what it costs, the two fields a widened bucket may have
    /// merged (level and element), the combat point that places it, when it was last seen
    /// and what became of it. No entity crosses the newline that ends it, so the list can
    /// be cut between any two of its lines.
    /// </summary>
    private static void Append(StringBuilder sb, ComparableListing listing)
    {
        sb.Append(Ncg(listing.Price))
          .Append(Text(" · +" + listing.Level.ToString(Culture) + " " +
                       Elementals.Name(listing.Element)));

        // A listing without a combat point is not a listing whose combat point is zero:
        // the field is left out rather than printed as a number that would read like one.
        if (listing.CombatPoint > 0)
        {
            sb.Append(Text(" · CP ")).Append(Code(listing.CombatPoint.ToString("N0", Culture)));
        }

        sb.Append(Text(" · "))
          .Append(Code(listing.LastSeenAtUtc.ToString("yyyy-MM-dd", Culture)))
          .Append(Text(" · " + OutcomeName(listing.Outcome)));
    }

    /// <summary>
    /// What is said when the whole ladder was not enough. It reports what the widest
    /// bucket did find, because "two listings" and "none at all" are different answers —
    /// and neither of them is a range: one invented on two samples reads exactly like one
    /// measured on fifty.
    /// </summary>
    private static string Insufficient(ValuationQuery query, ValuationResult result) =>
        new StringBuilder("🤷 ")
            .Append(Text(
                "Non ho abbastanza dati per questo pezzo. Anche allargando fino alla " +
                "stima larga (stesso tipo, stessa rarità, stessa presenza di skill) ho " +
                "trovato "))
            .Append(Code(result.Comparables.ToString("N0", Culture)))
            .Append(Text(result.Comparables == 1 ? " inserzione" : " inserzioni"))
            .Append(Text(" sulle "))
            .Append(Code(query.MinSamples.ToString("N0", Culture)))
            .Append(Text(" che servono.\n"))
            .Append(Text(
                "La risposta arriva da sé man mano che lo storico si allunga: il " +
                "meccanismo c'è, i campioni no."))
            .ToString();

    /// <summary>
    /// When the comparables were last seen. Those dates are most of what says whether a
    /// range can be trusted, and they are written the way the rest of the project writes
    /// them.
    /// </summary>
    private static string Window(ValuationResult result)
    {
        if (result.OldestSeenUtc is not DateTime oldest
            || result.NewestSeenUtc is not DateTime newest)
        {
            return "";
        }

        var from = oldest.ToString("yyyy-MM-dd", Culture);
        var to = newest.ToString("yyyy-MM-dd", Culture);
        return from == to
            ? Text(" · visti il ") + Code(from)
            : Text(" · visti dal ") + Code(from) + Text(" al ") + Code(to);
    }

    /// <summary>
    /// What the range was measured on, in the words that keep the difference visible: a
    /// sale and an asking price are not the same claim about a piece.
    /// </summary>
    private static string PopulationName(BaselinePopulation population) =>
        population == BaselinePopulation.Sold ? "vendite stimate" : "prezzi richiesti";

    /// <summary>
    /// What became of one listing, by the heuristic the rest of the project applies to a
    /// whole bucket (see <see cref="ListingOutcome"/>): gone at a plausible price is a
    /// sale, gone well above the going rate is a withdrawal, everything else is still on
    /// sale.
    /// </summary>
    private static string OutcomeName(ListingOutcome outcome) => outcome switch
    {
        ListingOutcome.LikelySold => "probabile vendita",
        ListingOutcome.LikelyWithdrawn => "probabile ritiro",
        _ => "in vendita",
    };

    private static string Ncg(double price) => Code(price.ToString("N2", Culture) + " NCG");

    /// <summary>
    /// The options, named and counted. A piece with none is a piece the parser was told
    /// nothing about, and saying so is what lets the reader notice that the four lines
    /// they typed did not arrive.
    /// </summary>
    private static string Options(ValuationKey key) => key.OptionStats.Count switch
    {
        0 => "senza opzioni",
        1 => "opzione " + GameEnums.StatTypeName(key.OptionStats[0]),
        _ => "opzioni " + string.Join(", ", key.OptionStats.Select(GameEnums.StatTypeName)),
    };

    /// <summary>
    /// The rarity by name, falling back to the number for a grade lib9c grew after this
    /// enum was written.
    /// </summary>
    private static string GradeName(int grade) =>
        Enum.IsDefined(typeof(Grade), grade)
            ? ((Grade)grade).ToString()
            : "grado " + grade.ToString(Culture);

    /// <summary>
    /// Prose, escaped. Every fixed sentence of this file goes through here rather than
    /// being written with its backslashes already in it: the Italian is full of full stops
    /// and parentheses, MarkdownV2 gives a meaning to both, and an escape forgotten once
    /// does not produce an ugly message — it produces no message at all.
    /// </summary>
    private static string Text(string text) => MarkdownV2.Escape(text);

    /// <summary>A figure, in the monospace box every figure of this project is printed in.</summary>
    private static string Code(string value) => MarkdownV2.Code(value);
}
