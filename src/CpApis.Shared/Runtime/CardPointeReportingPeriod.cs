using System.Globalization;

namespace CpApis.Shared.Runtime;

public static class CardPointeReportingPeriod
{
    public static IReadOnlyList<string>? Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return [day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)];
        }

        if (DateTime.TryParseExact(value, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return Enumerable.Range(1, DateTime.DaysInMonth(month.Year, month.Month))
                .Select(dayOfMonth => new DateTime(month.Year, month.Month, dayOfMonth)
                    .ToString("yyyyMMdd", CultureInfo.InvariantCulture))
                .ToArray();
        }

        return null;
    }
}
