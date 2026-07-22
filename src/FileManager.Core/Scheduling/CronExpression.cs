using System;
using System.Diagnostics.CodeAnalysis;

namespace FileManager.Core.Scheduling;

/// <summary>Hand-rolled 5-field cron parser (§4.2 [flagged] — zero dependencies, auditable).
/// This slice implements SYNTAX validation only (feeds PROFILE_CRON_INVALID); next-occurrence
/// evaluation lands with the scheduler, which reuses this type. Grammar per field:
/// "*", "*/step", "a", "a-b", "a-b/step", and comma-separated lists thereof. Numeric only —
/// month/day names are not part of v1's dialect.</summary>
public sealed class CronExpression
{
    private static readonly (string Name, int Min, int Max)[] Fields =
    [
        ("minute", 0, 59),
        ("hour", 0, 23),
        ("day-of-month", 1, 31),
        ("month", 1, 12),
        ("day-of-week", 0, 7),      // both 0 and 7 mean Sunday, per common cron
    ];

    private CronExpression(string expression) => Expression = expression;

    public string Expression { get; }

    public static bool TryParse(
        string expression,
        [NotNullWhen(true)] out CronExpression? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "cron expression is empty";
            return false;
        }

        string[] fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != Fields.Length)
        {
            error = $"cron expression must have exactly {Fields.Length} fields, got {fields.Length}";
            return false;
        }

        for (int i = 0; i < Fields.Length; i++)
        {
            if (!TryValidateField(fields[i], Fields[i].Name, Fields[i].Min, Fields[i].Max, out error))
                return false;
        }

        parsed = new CronExpression(expression);
        error = null;
        return true;
    }

    private static bool TryValidateField(string field, string name, int min, int max, [NotNullWhen(false)] out string? error)
    {
        foreach (string segment in field.Split(','))
        {
            if (segment.Length == 0)
            {
                error = $"{name} field has an empty list entry";
                return false;
            }

            string range = segment;
            int slash = segment.IndexOf('/');
            if (slash >= 0)
            {
                range = segment[..slash];
                string step = segment[(slash + 1)..];
                if (!int.TryParse(step, out int stepValue) || stepValue < 1)
                {
                    error = $"{name} field has an invalid step \"{step}\"";
                    return false;
                }
            }

            if (range == "*")
            {
                error = null;
                continue;
            }

            int dash = range.IndexOf('-');
            if (dash >= 0)
            {
                if (!TryParseBounded(range[..dash], name, min, max, out int low, out error) ||
                    !TryParseBounded(range[(dash + 1)..], name, min, max, out int high, out error))
                    return false;
                if (low > high)
                {
                    error = $"{name} field has an inverted range \"{range}\"";
                    return false;
                }
            }
            else
            {
                if (!TryParseBounded(range, name, min, max, out _, out error))
                    return false;
                if (slash >= 0)
                {
                    error = $"{name} field applies a step to a single value \"{segment}\" (steps need \"*\" or a range)";
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    private static bool TryParseBounded(string text, string name, int min, int max, out int value, [NotNullWhen(false)] out string? error)
    {
        if (!int.TryParse(text, out value))
        {
            error = $"{name} field has a non-numeric value \"{text}\"";
            return false;
        }
        if (value < min || value > max)
        {
            error = $"{name} field value {value} is outside [{min}..{max}]";
            return false;
        }
        error = null;
        return true;
    }
}
