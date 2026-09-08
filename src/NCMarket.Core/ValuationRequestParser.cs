using System.Collections.Immutable;
using System.Globalization;
using NCMarket.Core.Models;

namespace NCMarket.Core;

/// <summary>
/// Turns the free-form message someone writes to the bot into a <see cref="ValuationQuery"/>.
/// It knows nothing about Telegram — it is a function from text to a query, so every
/// crooked message it has to survive is a unit test and not a bot session.
/// <para>
/// <b>It classifies tokens, not lines.</b> Free line order was the requirement;
/// classifying by token satisfies it and, for free, makes <c>Sword Fire</c> work on one
/// line and <c>Transcendent Sword Fire +7</c> work on one line too. A stat alias
/// consumes the number that follows it, <c>CP</c> does the same, <c>skill</c> consumes
/// the yes/no that follows it, and <c>+N</c> stands alone.
/// </para>
/// <para>
/// The rules that are decisions rather than mechanics:
/// </para>
/// <list type="bullet">
/// <item><b>thousands separators are ignored inside a number</b>: <c>1.404.374</c>,
/// <c>1,404,374</c> and <c>1404374</c> are the same value, because the first is what the
/// game shows and nobody retypes it;</item>
/// <item><b>the stat aliases derive from <see cref="GameEnums.StatTypeName"/></b>, which
/// stays the single source of the names: this class builds its inverse and adds synonyms
/// to it, so a stat added to lib9c does not leave the parser behind;</item>
/// <item><b>a bare number is an error</b>, not a guess. <see cref="Grades.TryParse"/>
/// accepts <c>"8"</c>, so a lone <c>8</c> would silently become <em>Transcendent</em>:
/// the answer names the token and asks what it was;</item>
/// <item><b>a missing element is an error</b>, not a silent fallback to "any element".
/// The fallback exists — it is step <see cref="ValuationStep.AnyElement"/> of the ladder —
/// but it is chosen, not stumbled into: without the element, grade 8 mixes five items
/// whose prices are on different scales.</item>
/// </list>
/// <para>
/// The values of the option stats are read and dropped. They are consumed so that
/// <c>ATK 1.404.374</c> does not leave a bare number behind, and dropped because they do
/// not enter <see cref="ValuationKey"/> and do not predict the price inside a bucket.
/// </para>
/// </summary>
public static class ValuationRequestParser
{
    /// <summary>
    /// Options a single piece can carry. Five is not a piece the game can produce, so it
    /// is a message that was misread rather than one describing something rare.
    /// </summary>
    private const int MaxOptions = 4;

    /// <summary>
    /// Enhancement levels stop well below this; the bound is here to tell a level from a
    /// number that landed after a plus sign by accident.
    /// </summary>
    private const int MaxLevel = 255;

    /// <summary>
    /// Highest lib9c <c>StatType</c> value looked at when building the alias map.
    /// <see cref="GameEnums.StatTypeName"/> answers <c>StatNN</c> for anything it does not
    /// know, which is how the scan tells a real stat from an empty slot — and why a stat
    /// added to that switch tomorrow is parseable today without touching this file.
    /// </summary>
    private const int StatTypeScanLimit = 64;

    /// <summary>
    /// What separates two tokens. The middle dot is among them because it is how a piece
    /// gets written when it is copied out of an answer rather than typed field by field.
    /// </summary>
    private static readonly char[] Separators =
        { ' ', '\t', '\n', '\r', '\f', '\v', '·', ';', '|' };

    /// <summary>
    /// Punctuation that clings to a token without being part of it. The dot and the comma
    /// are trimmed at the edges only: inside a number they are thousands separators.
    /// </summary>
    private static readonly char[] Trimmed = { ',', '.', ':', ';', '·' };

    private static readonly Dictionary<string, int> StatsByName = BuildStatAliases();

    private static readonly Dictionary<string, bool> YesNo =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["si"] = true,
            ["sì"] = true,
            ["yes"] = true,
            ["y"] = true,
            ["true"] = true,
            ["con"] = true,
            ["no"] = false,
            ["n"] = false,
            ["false"] = false,
            ["senza"] = false,
        };

    /// <summary>
    /// Reads <paramref name="message"/> as the description of a piece on
    /// <paramref name="planet"/>. Returns false — with a message naming the offending
    /// token, or the field that is missing — for anything it cannot place.
    /// <para>
    /// Rarity, type and element are required; the level defaults to <c>+0</c> (92% of the
    /// listings are), skill and custom craft default to no, and the combat point is
    /// optional because it only places the piece inside the range.
    /// </para>
    /// </summary>
    public static bool TryParse(
        string message, Planet planet, out ValuationQuery? query, out string? error)
    {
        query = null;
        error = null;

        var tokens = Tokenize(message);
        if (tokens.Count == 0)
        {
            error = "Messaggio vuoto. Scrivimi il pezzo, per esempio: " +
                    "'Transcendent Sword Fire +7 / ATK 1.404.374 / DEF 3.359.312 / skill si'.";
            return false;
        }

        Grade? grade = null;
        EquipmentType? type = null;
        ElementalType? element = null;
        string? gradeToken = null, typeToken = null, elementToken = null;
        int? level = null;
        bool? hasSkill = null;
        bool? byCustomCraft = null;
        long? combatPoint = null;
        var options = new List<int>(MaxOptions);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;

            if (token[0] == '+')
            {
                if (!TryNumber(token[1..], out var value) || value > MaxLevel)
                {
                    error = $"'{token}' non è un livello. Il livello si scrive col più e " +
                            "un numero, per esempio '+7'.";
                    return false;
                }

                if (level is not null && level != (int)value)
                {
                    error = $"Due livelli: '+{level}' e '{token}'. Indicane uno solo.";
                    return false;
                }

                level = (int)value;
                continue;
            }

            // A yes/no on its own binds forward, so 'con skill' reads like 'skill si'.
            // The echo says "con skill", and an echo that cannot be sent back as a
            // correction is a one-way conversation.
            if (YesNo.TryGetValue(token, out var standalone))
            {
                if (IsSkill(next))
                {
                    if (!Set(ref hasSkill, standalone, "skill", token, out error))
                    {
                        return false;
                    }

                    i++;
                    continue;
                }

                if (IsCustom(next))
                {
                    if (!Set(ref byCustomCraft, standalone, "custom craft", token, out error))
                    {
                        return false;
                    }

                    i += CustomTokenLength(i + 1, tokens);
                    continue;
                }

                error = $"'{token}' da solo non dice a cosa si riferisce. " +
                        "Scrivi 'skill si', 'skill no', 'custom craft si'.";
                return false;
            }

            if (IsSkill(token))
            {
                // A bare 'skill' is a yes: nobody writes the word to say the piece has
                // none, and the echo shows which way it was read.
                var value = true;
                if (next is not null && YesNo.TryGetValue(next, out var stated))
                {
                    value = stated;
                    i++;
                }

                if (!Set(ref hasSkill, value, "skill", token, out error))
                {
                    return false;
                }

                continue;
            }

            if (IsCustom(token))
            {
                var length = CustomTokenLength(i, tokens);
                var after = i + length < tokens.Count ? tokens[i + length] : null;
                var value = true;
                if (after is not null && YesNo.TryGetValue(after, out var stated))
                {
                    value = stated;
                    length++;
                }

                if (!Set(ref byCustomCraft, value, "custom craft", token, out error))
                {
                    return false;
                }

                i += length - 1;
                continue;
            }

            if (string.Equals(token, "cp", StringComparison.OrdinalIgnoreCase))
            {
                if (next is null || !TryNumber(next, out var value))
                {
                    error = next is null
                        ? "'CP' vuole il valore che segue, per esempio 'CP 151.216.255'."
                        : $"'CP' vuole un numero, e '{next}' non lo è.";
                    return false;
                }

                if (value > int.MaxValue)
                {
                    error = $"CP fuori scala: '{next}'.";
                    return false;
                }

                if (combatPoint is not null && combatPoint != value)
                {
                    error = $"Due CP diversi: '{combatPoint}' e '{next}'. Indicane uno solo.";
                    return false;
                }

                combatPoint = value;
                i++;
                continue;
            }

            if (StatsByName.TryGetValue(token, out var stat))
            {
                // The value is consumed when it is there — that is what keeps it from
                // being read as a bare number — and then dropped: it is not part of the
                // bucket. A stat named without its value is still a stat.
                if (next is not null && TryNumber(next, out _))
                {
                    i++;
                }

                // The same stat named twice is one option: two rolls landing on it come
                // back merged into one row, so one row is what its owner reads off it.
                if (options.Contains(stat))
                {
                    continue;
                }

                if (options.Count == MaxOptions)
                {
                    error = $"Più di quattro opzioni ({Names(options)}, e ora '{token}'). " +
                            "Un pezzo ne ha al massimo quattro.";
                    return false;
                }

                options.Add(stat);
                continue;
            }

            if (TryNumber(token, out _))
            {
                error = $"Un numero da solo non dice cosa sia: '{token}'. Il livello si " +
                        $"scrive '+{token}', una stat col suo nome ('ATK {token}'), il " +
                        $"combat point con 'CP {token}'.";
                return false;
            }

            var isGrade = Grades.TryParse(token, out var parsedGrade);
            var isType = EquipmentTypes.TryParse(token, out var parsedType);
            var isElement = Elementals.TryParse(token, out var parsedElement);

            // Order matters only for the words that are two things at once: 'Normal' is
            // both a rarity and an element, and the field still free is the one meant.
            if (isGrade && grade is null)
            {
                (grade, gradeToken) = (parsedGrade, token);
            }
            else if (isType && type is null)
            {
                (type, typeToken) = (parsedType, token);
            }
            else if (isElement && element is null)
            {
                (element, elementToken) = (parsedElement, token);
            }
            else if (isGrade || isType || isElement)
            {
                var (label, first) =
                    isGrade && grade is not null ? ("rarità", gradeToken!)
                    : isType && type is not null ? ("tipi", typeToken!)
                    : ("elementi", elementToken!);
                error = $"Due {label}: '{first}' e '{token}'. Indicane uno solo.";
                return false;
            }
            else
            {
                error = $"Non ho riconosciuto '{token}'. Mi aspetto la rarità " +
                        "(Normal…Transcendent), il tipo (Weapon, Armor, Belt, Necklace, " +
                        "Ring), l'elemento (Normal, Fire, Water, Land, Wind), le opzioni " +
                        "col loro valore ('ATK 1.404.374'), e se vuoi '+7', 'skill si', " +
                        "'custom craft si', 'CP 151.216.255'.";
                return false;
            }
        }

        var missing = new List<string>(3);
        if (grade is null)
        {
            missing.Add("la rarità (Normal…Transcendent, oppure 1-8)");
        }

        if (type is null)
        {
            missing.Add("il tipo (Weapon, Armor, Belt, Necklace, Ring)");
        }

        if (element is null)
        {
            // The element does not fall back on its own: at grade 8 the triple
            // (type, grade, element) is the item, and without it five items whose prices
            // are on different scales end up in one bucket. Widening to every element is
            // step 1 of the ladder — something chosen, not stumbled into.
            missing.Add("l'elemento (Normal, Fire, Water, Land, Wind)");
        }

        if (missing.Count > 0)
        {
            error = "Manca " + Join(missing) + ".";
            return false;
        }

        query = new ValuationQuery
        {
            Planet = planet,
            Key = new ValuationKey(
                type!.Value,
                (int)grade!.Value,
                element!.Value,
                level ?? 0,
                options.ToImmutableSortedSet(),
                hasSkill ?? false,
                byCustomCraft ?? false),
            CombatPoint = combatPoint is null ? null : (int)combatPoint.Value,
        };

        return true;
    }

    /// <summary>
    /// Splits on whitespace and on the separators the bot's own answers use, then drops
    /// the punctuation that clings to a token's edges.
    /// </summary>
    private static List<string> Tokenize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new List<string>();
        }

        return message
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(Trimmed))
            .Where(t => t.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Reads a whole number written the way the game shows it. The thousands separators
    /// are dropped wherever they are, because <c>1.404.374</c> and <c>1,404,374</c> are
    /// the same number written by two locales and neither of them is a decimal.
    /// </summary>
    private static bool TryNumber(string token, out long value)
    {
        value = 0;
        Span<char> digits = stackalloc char[token.Length];
        var length = 0;
        foreach (var c in token)
        {
            if (c is '.' or ',' or '\'' or '_')
            {
                continue;
            }

            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            digits[length++] = c;
        }

        return length > 0
            && long.TryParse(
                digits[..length], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsSkill(string? token) =>
        token is not null && string.Equals(token, "skill", StringComparison.OrdinalIgnoreCase);

    private static bool IsCustom(string? token) =>
        token is not null
        && (string.Equals(token, "custom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "customcraft", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How many tokens the custom craft keyword takes, so that the two-word spelling and
    /// the one-word spelling behave alike.
    /// </summary>
    private static int CustomTokenLength(int index, IReadOnlyList<string> tokens) =>
        index + 1 < tokens.Count
        && string.Equals(tokens[index], "custom", StringComparison.OrdinalIgnoreCase)
        && string.Equals(tokens[index + 1], "craft", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 1;

    /// <summary>
    /// Assigns a yes/no field once, and refuses a second, opposite answer instead of
    /// letting the last one win in silence.
    /// </summary>
    private static bool Set(
        ref bool? field, bool value, string label, string token, out string? error)
    {
        if (field is not null && field != value)
        {
            error = $"Due risposte diverse su {label}, l'ultima è '{token}'. " +
                    "Indicane una sola.";
            return false;
        }

        error = null;
        field = value;
        return true;
    }

    private static string Names(IEnumerable<int> stats) =>
        string.Join(", ", stats.Select(GameEnums.StatTypeName));

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " e " + parts[^1];

    /// <summary>
    /// Stat names to lib9c values, built by reading <see cref="GameEnums.StatTypeName"/>
    /// backwards, plus the synonyms someone actually types. The display side stays the
    /// only place the names are written down.
    /// </summary>
    private static Dictionary<string, int> BuildStatAliases()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var type = 1; type <= StatTypeScanLimit; type++)
        {
            var name = GameEnums.StatTypeName(type);
            if (name == $"Stat{type}")
            {
                continue;
            }

            map[name] = type;
        }

        Alias(map, "ATK", "ATTACK", "ATTACCO");
        Alias(map, "SPD", "SPEED");
        Alias(map, "CRI", "CRIT");
        return map;
    }

    private static void Alias(
        Dictionary<string, int> map, string canonical, params string[] names)
    {
        if (!map.TryGetValue(canonical, out var type))
        {
            return;
        }

        foreach (var name in names)
        {
            map[name] = type;
        }
    }
}
