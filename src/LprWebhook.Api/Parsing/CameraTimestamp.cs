using System.Globalization;
using System.Text.RegularExpressions;
using LprWebhook.Api.Contracts;

namespace LprWebhook.Api.Parsing;

/// <summary>
/// Resolves the camera's local wall-clock time (DateTimeKind.Unspecified — the vendor sends no timezone).
/// </summary>
public static partial class CameraTimestamp
{
    // yyyy-M-d[T| ]H:m:s[.fraction] — single-digit parts and any fraction length are accepted.
    [GeneratedRegex(@"^\s*(\d{4})-(\d{1,2})-(\d{1,2})[T ](\d{1,2}):(\d{1,2}):(\d{1,2})(?:[.,](\d+))?\s*$")]
    private static partial Regex TimestampRegex();

    public static DateTime? Resolve(string? timestamp, PayloadDate? date) =>
        Parse(timestamp) ?? FromDate(date);

    public static DateTime? Parse(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return null;

        var m = TimestampRegex().Match(timestamp);
        if (m.Success)
        {
            int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            var fraction = m.Groups[7].Success ? m.Groups[7].Value : "";
            return Create(G(1), G(2), G(3), G(4), G(5), G(6), FractionToTicks(fraction));
        }

        // Anything else ISO-like (e.g. with an offset): keep the wall-clock time as written.
        return DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.DateTime
            : null;
    }

    public static DateTime? FromDate(PayloadDate? d)
    {
        if (d is null) return null;
        if (!TryInt(d.Year, out var y) || !TryInt(d.Month, out var mo) || !TryInt(d.Day, out var day))
            return null;

        TryInt(d.Hour, out var h);
        TryInt(d.Minute, out var mi);
        TryInt(d.Second, out var s);
        TryInt(d.Millisecond, out var ms);
        var ticks = ms is >= 0 and <= 999 ? ms * TimeSpan.TicksPerMillisecond : 0;
        return Create(y, mo, day, h, mi, s, ticks);
    }

    /// <summary>
    /// The template is ".${millisecond}", so a 1–3 digit fraction is the millisecond count itself
    /// ("5" = 5 ms, "51" = 51 ms, "512" = 512 ms). Longer fractions are treated as a decimal fraction.
    /// </summary>
    private static long FractionToTicks(string fraction) => fraction.Length switch
    {
        0 => 0,
        <= 3 => int.Parse(fraction, CultureInfo.InvariantCulture) * TimeSpan.TicksPerMillisecond,
        _ => long.Parse(fraction.PadRight(7, '0')[..7], CultureInfo.InvariantCulture),
    };

    private static DateTime? Create(int y, int mo, int d, int h, int mi, int s, long ticks)
    {
        if (y is < 1 or > 9999 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)
            || h is < 0 or > 23 || mi is < 0 or > 59 || s is < 0 or > 59 || ticks is < 0 or >= TimeSpan.TicksPerSecond)
            return null;

        return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified).AddTicks(ticks);
    }

    private static bool TryInt(string? value, out int result) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}
