using System.Globalization;

namespace NCMarket.Cli;

/// <summary>
/// The options every verb accepts, and the parser that enforces them. A parser that
/// collects whatever it is given turns a typo into a filter that silently does not apply
/// (<c>deals --dicount 30</c> prints unfiltered results that look filtered), so each
/// token is checked against the list of its own verb and anything else is an error.
/// </summary>
internal sealed class CommandLine
{
    /// <summary>Options that carry no value; every other option consumes the next token.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "details", "no-names", "from-snapshot", "dry-run", "notify",
    };

    private static readonly Dictionary<string, string[]> ByVerb = new(StringComparer.Ordinal)
    {
        ["fetch"] = new[] { "planet", "type", "order", "limit", "offset", "details", "no-names" },
        ["snapshot"] = new[] { "planet", "db", "types", "max-per-type" },
        ["snapshots"] = new[] { "planet", "db" },
        ["history"] = new[] { "planet", "db", "item", "no-names" },
        ["stats"] = new[] { "planet", "db", "type", "top", "no-names" },
        ["deals"] = new[]
        {
            "planet", "db", "type", "grade", "discount", "min-samples", "days",
            "baseline", "sale-margin", "from-snapshot", "max-per-type", "top", "no-names",
            "notify",
        },
        ["export"] = new[] { "planet", "db", "snapshot", "type", "out", "sep", "no-names" },
        ["prune"] = new[] { "db", "days", "dry-run" },

        // The credentials of the notification channel are environment variables, not
        // options (see TelegramOptions), so there is nothing left for this verb to take.
        ["notify-test"] = Array.Empty<string>(),
    };

    private readonly Dictionary<string, string> _values;

    private CommandLine(string verb, Dictionary<string, string> values)
    {
        Verb = verb;
        _values = values;
    }

    public string Verb { get; }

    /// <summary>The verbs the CLI implements, in declaration order.</summary>
    public static IEnumerable<string> Verbs => ByVerb.Keys;

    /// <summary>
    /// Parses the arguments that follow the verb. Returns false — with a message naming
    /// the offending token — for an unknown verb, an unknown or repeated option, a bare
    /// argument, or a value option left without a value.
    /// </summary>
    public static bool TryParse(
        string verb, IReadOnlyList<string> args, out CommandLine? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (!ByVerb.TryGetValue(verb, out var allowed))
        {
            error = $"Comando sconosciuto: '{verb}'. Comandi disponibili: " +
                    string.Join(", ", ByVerb.Keys) + ".";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Argomento non riconosciuto: '{token}'. Le opzioni si indicano con " +
                        $"--nome (es. --top 10). Opzioni di '{verb}': {Describe(allowed)}.";
                return false;
            }

            var key = token[2..];
            var canonical = Array.Find(
                allowed, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                error = $"Opzione non riconosciuta per '{verb}': '{token}'. " +
                        $"Opzioni ammesse: {Describe(allowed)}.";
                return false;
            }

            if (values.ContainsKey(canonical))
            {
                error = $"Opzione ripetuta: '--{canonical}'.";
                return false;
            }

            if (Flags.Contains(canonical))
            {
                values[canonical] = "true";
                continue;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"L'opzione '--{canonical}' richiede un valore.";
                return false;
            }

            values[canonical] = args[++i];
        }

        parsed = new CommandLine(verb, values);
        return true;
    }

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

    public string? GetValueOrDefault(string key) => _values.GetValueOrDefault(key);

    public string GetValueOrDefault(string key, string fallback) =>
        _values.GetValueOrDefault(key, fallback);

    /// <summary>
    /// Value of a numeric option, checked against its admissible range: an out-of-range
    /// <c>--top -5</c> or <c>--discount 300</c> is a mistake, not a filter.
    /// </summary>
    public int GetInt(string key, int fallback, int min, int? max = null)
    {
        if (!_values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"Valore non numerico per '--{key}': '{raw}'.");
        }

        if (value < min || (max is int upper && value > upper))
        {
            throw new ArgumentException(
                $"Valore fuori intervallo per '--{key}': {value}. Ammessi valori " +
                (max is int m ? $"da {min} a {m}." : $"maggiori o uguali a {min}."));
        }

        return value;
    }

    /// <inheritdoc cref="GetInt"/>
    public long GetLong(string key, long fallback, long min)
    {
        if (!_values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"Valore non numerico per '--{key}': '{raw}'.");
        }

        if (value < min)
        {
            throw new ArgumentException(
                $"Valore fuori intervallo per '--{key}': {value}. " +
                $"Ammessi valori maggiori o uguali a {min}.");
        }

        return value;
    }

    private static string Describe(string[] allowed) =>
        allowed.Length == 0
            ? "nessuna"
            : string.Join(", ", allowed.Order(StringComparer.Ordinal).Select(a => "--" + a));
}
