using System.Globalization;
using System.Text;

namespace NCMarket.Core;

/// <summary>
/// The narrowing the market service is willing to apply to a listing query, on top of
/// the equipment type in the route. Asking for one item id instead of a whole sub type
/// is the difference between a question that can be answered in a chat and one that
/// costs sixty thousand listings.
/// <para>
/// What is missing here is <c>stat</c>, which the service's documentation lists next to
/// these: measured against <c>b.9capi.com</c> on 2026-08-25 it narrows nothing — not by
/// name, not by numeric <c>StatType</c>, not under <c>statType</c> or <c>stats</c> — and
/// an unknown value such as <c>stat=PIPPO</c> is answered <c>200</c> with the full
/// listing. An option that silently does not apply is what P0.4 exists to prevent, so it
/// is not exposed.
/// </para>
/// </summary>
public sealed record ListingFilter
{
    /// <summary>
    /// No narrowing: the whole sub type, which is what the callers that capture or scan
    /// the market want.
    /// </summary>
    public static readonly ListingFilter None = new();

    /// <summary>Item ids to keep. Empty means every item of the sub type.</summary>
    public IReadOnlyList<int> ItemIds { get; init; } = Array.Empty<int>();

    /// <summary>Icon ids to keep. Empty means every icon.</summary>
    public IReadOnlyList<int> IconIds { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Keep only custom-crafted pieces (<c>true</c>), only the others (<c>false</c>), or
    /// both (<c>null</c>). Custom craft rolls options an ordinary recipe does not, so the
    /// two populations are not always comparable.
    /// </summary>
    public bool? Custom { get; init; }

    public bool IsEmpty => ItemIds.Count == 0 && IconIds.Count == 0 && Custom is null;

    /// <summary>
    /// Refuses the one combination the service cannot answer. Asking for
    /// <see cref="Custom"/> <c>true</c> together with ids makes it drop the ids and
    /// return every custom-crafted piece of the sub type — a full listing wearing the
    /// shape of a filtered one, which is the failure this whole type is careful about.
    /// <para>
    /// It is not an arbitrary limitation on the service's part: a custom-crafted piece
    /// carries its own item id (the <c>2016…</c> range rather than the <c>1018…</c> of
    /// an ordinary Transcendent weapon), so "this item, but custom" names nothing. Ids
    /// alone are the way to ask, and <c>isCustom=false</c> does combine with them.
    /// </para>
    /// </summary>
    public void Validate()
    {
        if (Custom is true && (ItemIds.Count > 0 || IconIds.Count > 0))
        {
            throw new ArgumentException(
                "Il market service ignora itemIds e iconIds quando isCustom è true: " +
                "restituirebbe l'intero listino dei pezzi da custom craft con l'aspetto " +
                "di una risposta filtrata. I pezzi da custom craft hanno id propri, " +
                "quindi si chiedono per id senza isCustom.");
        }
    }

    /// <summary>
    /// Appends this filter to a query string that already carries at least one parameter,
    /// after <see cref="Validate">refusing</see> the combination the service mishandles.
    /// <para>
    /// The service binds a collection from the parameter <em>repeated once per value</em>
    /// (<c>itemIds=1&amp;itemIds=2</c>). The two forms one would otherwise reach for are
    /// both wrong, and only one of them says so: <c>itemIds=1,2</c> is refused with a
    /// <c>422</c>, while <c>itemIds[]=1</c> is answered <c>200</c> and <b>ignored</b> —
    /// the whole unfiltered market, wearing the shape of a filtered answer. A caller
    /// would notice the first immediately and the second never, which is why the shape of
    /// this query string is pinned by a test.
    /// </para>
    /// </summary>
    public void AppendTo(StringBuilder query)
    {
        Validate();

        foreach (var id in ItemIds)
        {
            query.Append("&itemIds=").Append(id.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var id in IconIds)
        {
            query.Append("&iconIds=").Append(id.ToString(CultureInfo.InvariantCulture));
        }

        if (Custom is bool custom)
        {
            query.Append("&isCustom=").Append(custom ? "true" : "false");
        }
    }
}
