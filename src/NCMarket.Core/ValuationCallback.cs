using System.Collections.Immutable;
using System.Globalization;

namespace NCMarket.Core;

/// <summary>What a button under an answer asks for.</summary>
public enum ValuationAction
{
    /// <summary>
    /// The listings the range was built on. A range from 11 to 333 NCG is unusable until
    /// the 333 can be seen for the outlier it is — and the median for what it is not.
    /// </summary>
    Comparables,

    /// <summary>
    /// The same piece valued on every element, which is rung
    /// <see cref="ValuationStep.AnyElement"/> of the ladder taken deliberately instead of
    /// out of necessity.
    /// </summary>
    Widen,

    /// <summary>The same piece on the other planet.</summary>
    OtherPlanet,
}

/// <summary>A field the guided flow asks for with buttons.</summary>
public enum DialogField
{
    Grade,
    Type,
    Element,
    Skill,

    /// <summary>"No options": the one answer of the last step that is not typed.</summary>
    NoOptions,

    /// <summary>Give up on the flow.</summary>
    Cancel,
}

/// <summary>
/// The vocabulary of the buttons: what is written into <see cref="InlineButton.Data"/>
/// and read back out of <see cref="TelegramUpdate.CallbackData"/>.
/// <para>
/// <b>The piece travels inside the button; the conversation does not.</b> A follow-up
/// button carries the whole query it would re-run, so it still works after a redeploy and
/// costs the bot no memory per answer given — where the guided flow keeps its half-filled
/// fields in memory (see <see cref="TelegramBot"/>), because a conversation has two ends
/// and a restart legitimately loses it. The asymmetry is deliberate: a message already
/// sent stays on the phone that received it for weeks, and a button on it answering "non
/// me lo ricordo più" would be a button that looks broken.
/// </para>
/// <para>
/// The budget is <see cref="InlineButton.MaxDataBytes"/> bytes for everything, which is
/// why the encoding is positional and terse rather than JSON. The widest piece this
/// project can describe spells out to about fifty characters.
/// </para>
/// <para>
/// What is <em>not</em> in there is as deliberate: minimum samples, history window and
/// population are configuration of the running bot, not properties of the piece. A button
/// pressed tomorrow re-runs with the settings of tomorrow, which is the same answer the
/// same question would get if it were typed out again by hand.
/// </para>
/// </summary>
public static class ValuationCallback
{
    /// <summary>Separator of the fields.</summary>
    private const char Separator = '|';

    /// <summary>
    /// What the data of a follow-up button starts with. It says which of the two
    /// vocabularies the datum belongs to <em>and</em> which version of it wrote the datum:
    /// a keyboard outlives the process that sent it, so a button from an older version has
    /// to be recognizable as such instead of being decoded into something else.
    /// </summary>
    private const string AnswerPrefix = "v1";

    /// <inheritdoc cref="AnswerPrefix"/>
    private const string DialogPrefix = "d1";

    /// <summary>Options a piece can carry, the same bound the parser defends.</summary>
    private const int MaxOptions = 4;

    private const int MaxLevel = 255;

    /// <summary>Fields of an encoded query, prefix and action included.</summary>
    private const int AnswerFields = 12;

    /// <summary>The data of a button that re-asks <paramref name="query"/>.</summary>
    public static string Encode(ValuationAction action, ValuationQuery query)
    {
        var key = query.Key;
        return string.Join(Separator, new[]
        {
            AnswerPrefix,
            Letter(action),
            query.Planet.Name,
            Number(key.Grade),
            Number((int)key.Type),
            Number((int)key.Element),
            Number(key.Level),
            string.Join(",", key.OptionStats.Select(Number)),
            Flag(key.HasSkill),
            Flag(key.ByCustomCraft),
            query.CombatPoint is int cp ? Number(cp) : "",
            Number((int)query.StartStep),
        });
    }

    /// <summary>
    /// The query a button carries, or false for anything else — a button of a version that
    /// no longer exists, or data nobody here wrote. It comes back from a client, so it is
    /// input: every field is bounded rather than trusted.
    /// </summary>
    public static bool TryDecode(
        string data, out ValuationAction action, out ValuationQuery? query)
    {
        action = default;
        query = null;

        var parts = data.Split(Separator);
        if (parts.Length != AnswerFields
            || parts[0] != AnswerPrefix
            || !TryAction(parts[1], out action)
            || !Planet.TryGet(parts[2], out var planet)
            || !TryNumber(parts[3], out var grade) || grade is < 1 or > 99
            || !TryNumber(parts[4], out var type)
            || !Enum.IsDefined(typeof(EquipmentType), type)
            || !TryNumber(parts[5], out var element)
            || !Enum.IsDefined(typeof(ElementalType), element)
            || !TryNumber(parts[6], out var level) || level > MaxLevel
            || !TryStats(parts[7], out var stats)
            || !TryFlag(parts[8], out var skill)
            || !TryFlag(parts[9], out var custom)
            || !TryOptionalNumber(parts[10], out var combatPoint)
            || !TryNumber(parts[11], out var step)
            || !Enum.IsDefined(typeof(ValuationStep), step))
        {
            return false;
        }

        query = new ValuationQuery
        {
            Planet = planet,
            Key = new ValuationKey(
                (EquipmentType)type, grade, (ElementalType)element, level, stats, skill,
                custom),
            CombatPoint = combatPoint,
            StartStep = (ValuationStep)step,
        };

        return true;
    }

    /// <summary>The data of a button that answers one field of the guided flow.</summary>
    public static string Encode(DialogField field, int value = 0) =>
        string.Join(Separator, DialogPrefix, Letter(field), Number(value));

    /// <inheritdoc cref="TryDecode(string, out ValuationAction, out ValuationQuery?)"/>
    public static bool TryDecodeDialog(string data, out DialogField field, out int value)
    {
        field = default;
        value = 0;

        var parts = data.Split(Separator);
        return parts.Length == 3
               && parts[0] == DialogPrefix
               && TryField(parts[1], out field)
               && TryNumber(parts[2], out value);
    }

    /// <summary>Whether the data was written by the answer side of the vocabulary.</summary>
    public static bool IsAnswer(string data) =>
        data.StartsWith(AnswerPrefix + Separator, StringComparison.Ordinal);

    private static string Letter(ValuationAction action) => action switch
    {
        ValuationAction.Comparables => "c",
        ValuationAction.Widen => "w",
        _ => "p",
    };

    private static bool TryAction(string letter, out ValuationAction action)
    {
        (action, var known) = letter switch
        {
            "c" => (ValuationAction.Comparables, true),
            "w" => (ValuationAction.Widen, true),
            "p" => (ValuationAction.OtherPlanet, true),
            _ => (default(ValuationAction), false),
        };

        return known;
    }

    private static string Letter(DialogField field) => field switch
    {
        DialogField.Grade => "g",
        DialogField.Type => "t",
        DialogField.Element => "e",
        DialogField.Skill => "s",
        DialogField.NoOptions => "o",
        _ => "x",
    };

    private static bool TryField(string letter, out DialogField field)
    {
        (field, var known) = letter switch
        {
            "g" => (DialogField.Grade, true),
            "t" => (DialogField.Type, true),
            "e" => (DialogField.Element, true),
            "s" => (DialogField.Skill, true),
            "o" => (DialogField.NoOptions, true),
            "x" => (DialogField.Cancel, true),
            _ => (default(DialogField), false),
        };

        return known;
    }

    private static string Flag(bool value) => value ? "1" : "0";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryOptionalNumber(string text, out int? value)
    {
        if (text.Length == 0)
        {
            value = null;
            return true;
        }

        var parsed = TryNumber(text, out var number);
        value = number;
        return parsed;
    }

    private static bool TryFlag(string text, out bool value)
    {
        value = text == "1";
        return text is "0" or "1";
    }

    private static bool TryStats(string text, out ImmutableSortedSet<int> stats)
    {
        stats = ImmutableSortedSet<int>.Empty;
        if (text.Length == 0)
        {
            return true;
        }

        var parsed = new List<int>(MaxOptions);
        foreach (var token in text.Split(','))
        {
            if (!TryNumber(token, out var stat))
            {
                return false;
            }

            parsed.Add(stat);
        }

        if (parsed.Count > MaxOptions)
        {
            return false;
        }

        stats = parsed.ToImmutableSortedSet();
        return true;
    }
}
