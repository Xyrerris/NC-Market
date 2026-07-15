namespace NCMarket.Core;

/// <summary>
/// A Nine Chronicles planet (network) and its public market service endpoint.
/// Endpoints come from the official planet registry
/// (https://planets.nine-chronicles.com/planets/, key <c>market.rest</c>).
/// </summary>
public sealed record Planet(string Name, string MarketBaseUrl)
{
    public static readonly Planet Odin = new("odin", "https://b.9capi.com/marketProviderOdin");
    public static readonly Planet Heimdall = new("heimdall", "https://b.9capi.com/marketProviderHeimdall");

    public static readonly Planet[] All = { Odin, Heimdall };

    public static bool TryGet(string name, out Planet planet)
    {
        foreach (var p in All)
        {
            if (string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                planet = p;
                return true;
            }
        }

        planet = null!;
        return false;
    }
}
