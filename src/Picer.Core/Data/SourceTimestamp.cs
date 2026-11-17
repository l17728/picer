using System.Globalization;

namespace Picer.Core.Data;

/// <summary>File identity compares instants at tick precision, never display formatting or rounded Julian days.</summary>
public static class SourceTimestamp
{
    public static bool SameInstant(string? left, string? right) =>
        TryParse(left, out var first) && TryParse(right, out var second)
        && first.UtcTicks == second.UtcTicks;

    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        return CompleteTimestamp(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
    }

    private static bool CompleteTimestamp(string? value) => value is { Length: >= 19 }
        && value[4] == '-' && value[7] == '-' && value[10] is 'T' or ' '
        && value[13] == ':' && value[16] == ':'
        && value.Take(19).Where((_, index) => index is not (4 or 7 or 10 or 13 or 16)).All(char.IsAsciiDigit);
}
