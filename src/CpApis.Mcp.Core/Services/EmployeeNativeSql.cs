using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace CpApis.Mcp.Core.Services;

/// <summary>Internal, parameterized native SQL. Command text is always a source constant.</summary>
internal static class EmployeeNativeSql
{
    internal static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 15;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal static async Task<List<Dictionary<string, object?>>> RowsAsync(SqlConnection connection,
        SqlTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= 1000) throw new EmployeeActionException(409, "This record has too many linked rows for a safe action. Use the native Portal.");
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    internal static async Task<Dictionary<string, object?>> OneAsync(SqlConnection connection, SqlTransaction transaction,
        string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var rows = await RowsAsync(connection, transaction, sql, ct, parameters);
        if (rows.Count != 1) throw new EmployeeActionException(404, "The active Portal record was not found or is ambiguous.");
        return rows[0];
    }

    internal static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql,
        CancellationToken ct, params (string Name, object? Value)[] parameters)
    { await using var command = Command(connection, transaction, sql, parameters); await command.ExecuteNonQueryAsync(ct); }

    internal static byte[] Snapshot(params object?[] rows) => EmployeeActionJson.Hash(JsonSerializer.Serialize(rows, EmployeeActionJson.Options));
    internal static string? Text(this IReadOnlyDictionary<string, object?> row, string key) => row[key] is null ? null : Convert.ToString(row[key], CultureInfo.InvariantCulture);
    internal static int Number(this IReadOnlyDictionary<string, object?> row, string key) => row[key] is null ? 0 : Convert.ToInt32(row[key], CultureInfo.InvariantCulture);
    internal static bool Flag(this IReadOnlyDictionary<string, object?> row, string key) => row[key] is not null && Convert.ToBoolean(row[key], CultureInfo.InvariantCulture);
    internal static DateTime? Date(this IReadOnlyDictionary<string, object?> row, string key) => row[key] as DateTime?;
    internal static string? Display(object? value) => value switch
    {
        null => null, DateTime date => date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    internal static EmployeeActionException Locked(string message) => new(409, message);

    internal static void Payload(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal)))
            throw EmployeeActionJson.Invalid();
        // Also reject duplicate keys, including case-ambiguous duplicates.
        EmployeeActionJson.Canonicalize(payload);
    }
    internal static int Id(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id <= 0) throw EmployeeActionJson.Invalid();
        return id;
    }
    internal static string RequiredText(JsonElement payload, string name, int maximum)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw EmployeeActionJson.Invalid();
        var text = value.GetString()!.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Contains('\0')) throw EmployeeActionJson.Invalid();
        return text;
    }
    internal static object? PatchText(JsonElement payload, string name, object? current, int maximum, bool allowEmpty = true)
    {
        if (!payload.TryGetProperty(name, out var value)) return current;
        if (value.ValueKind != JsonValueKind.String) throw EmployeeActionJson.Invalid();
        var text = value.GetString()!.Trim();
        if (text.Length > maximum || text.Contains('\0') || !allowEmpty && text.Length == 0) throw EmployeeActionJson.Invalid();
        return text;
    }
    internal static decimal Decimal(JsonElement payload, string name, decimal fallback, decimal maximum)
    {
        if (!payload.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0 || number > maximum || decimal.Round(number, 2) != number) throw EmployeeActionJson.Invalid();
        return number;
    }
    internal static DateTime LocalTime(JsonElement payload, string name, DateTime? fallback = null)
    {
        if (!payload.TryGetProperty(name, out var value) && fallback.HasValue) return fallback.Value;
        if (value.ValueKind != JsonValueKind.String || !DateTime.TryParseExact(value.GetString(),
            ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date.Year < 2000 || date.Year > 2099) throw EmployeeActionJson.Invalid();
        return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
    }

    internal static async Task RequireScopeAsync(SqlConnection connection, SqlTransaction transaction, PortalEmployee actor,
        string scope, bool isOwner, bool requireAllow, CancellationToken ct)
    {
        var permissions = await ClientPortalEmployeeContext.ReadPermissionsAsync(connection, transaction, actor.UserId, scope, ct);
        if (permissions.Any(p => string.Equals(p.Action, "Deny", StringComparison.OrdinalIgnoreCase))
            || requireAllow && !ClientPortalPermissionPolicy.Allows(permissions, isOwner, allowUnqualified: true))
            throw new EmployeeAccessDeniedException("The current native permission does not allow this action.");
    }

    internal static async Task<XDocument> XmlAsync(SqlConnection connection, SqlTransaction transaction, string procedure,
        string inputName, XDocument input, string outputName, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, procedure); command.CommandType = CommandType.StoredProcedure;
        command.Parameters.Add(inputName, SqlDbType.Xml).Value = input.ToString(SaveOptions.DisableFormatting);
        var output = command.Parameters.Add(outputName, SqlDbType.Xml); output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(ct);
        if (output.Value is not string text || text.Length > 2_000_000) throw Locked("The native Portal did not return a valid action result.");
        using var sr = new StringReader(text);
        using var reader = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        return XDocument.Load(reader);
    }
}
