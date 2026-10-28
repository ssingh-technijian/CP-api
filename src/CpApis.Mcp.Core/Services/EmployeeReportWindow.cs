using System.Globalization;

namespace CpApis.Mcp.Core.Services;

public sealed record EmployeeReportWindow(DateTimeOffset FromUtc, DateTimeOffset UntilUtc, string TimeZoneId, string Period)
{
    public DateTime NativeFrom(TimeZoneInfo nativeZone) => Native(FromUtc, nativeZone);
    public DateTime NativeUntil(TimeZoneInfo nativeZone) => Native(UntilUtc, nativeZone);
    private static DateTime Native(DateTimeOffset instant,TimeZoneInfo zone)
    {
        var local=TimeZoneInfo.ConvertTime(instant,zone).DateTime;
        if(zone.IsAmbiguousTime(local)||zone.IsInvalidTime(local))
            throw new EmployeeActionException(409,"The native timestamp has an ambiguous daylight-saving boundary. Use complete calendar dates for this report.");
        return local;
    }
}

/// <summary>Relative periods use the verified employee's work region and the database UTC clock.</summary>
public static class EmployeeReportPeriods
{
    public static TimeZoneInfo EmployeeZone(string? locationTopFilter) => TimeZoneInfo.FindSystemTimeZoneById(
        ClientPortalEmployeeTimeZonePolicy.Resolve(locationTopFilter) == PortalWorkRegion.India ? "India Standard Time" : "Pacific Standard Time");

    public static EmployeeReportWindow Resolve(string period, string? fromDate, string? throughDate,
        DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if(string.IsNullOrWhiteSpace(period))throw new EmployeeActionException(400,"Specify a supported report period.");
        period = period.Trim().ToLowerInvariant();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        DateOnly from, through;
        if (period == "custom")
        {
            if (!DateOnly.TryParseExact(fromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
                || !DateOnly.TryParseExact(throughDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out through)
                || through < from || through.DayNumber - from.DayNumber >= 31 || through > today)
                throw new EmployeeActionException(400, "Use an inclusive YYYY-MM-DD range of at most31days, ending no later than the employee's current local date.");
        }
        else
        {
            if (fromDate is not null || throughDate is not null)
                throw new EmployeeActionException(400, "Use period=custom when supplying date fields.");
            if (period is "last_2_days" or "last_7_days")
                return new(nowUtc.AddDays(period == "last_2_days" ? -2 : -7), nowUtc, zone.Id, period);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            (from, through) = period switch
            {
                "today" => (today, today),
                "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
                "this_week" => (monday, today),
                "last_week" => (monday.AddDays(-7), monday.AddDays(-1)),
                "this_month" => (new DateOnly(today.Year, today.Month, 1), today),
                "last_month" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
                _ => throw new EmployeeActionException(400, "Period must be today, yesterday, last_2_days, last_7_days, this_week, last_week, this_month, last_month, or custom.")
            };
        }
        static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo tz) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), tz), TimeSpan.Zero);
        var end = through == today ? nowUtc : Midnight(through.AddDays(1), zone);
        return new(Midnight(from, zone), end, zone.Id, period);
    }

    public static bool IsClosed(string? status, string? label)
    {
        // Native open-ticket membership uses numeric Status. A stale display label must not override it.
        if(int.TryParse(status,NumberStyles.Integer,CultureInfo.InvariantCulture,out var id))return id is 1272 or 1275;
        return string.Equals(label?.Trim(), "Completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(label?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase);
    }
}
