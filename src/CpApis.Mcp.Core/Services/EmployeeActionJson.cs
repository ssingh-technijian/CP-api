using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CpApis.Mcp.Core.Services;

internal static class EmployeeActionJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static string Canonicalize(JsonElement input)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, input, 0);
        if (stream.Length > 16_384) throw Invalid();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 16) throw Invalid();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            if (fields.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length) throw Invalid();
            writer.WriteStartObject();
            foreach (var field in fields) { writer.WritePropertyName(field.Name); Write(writer, field.Value, depth + 1); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item, depth + 1);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    internal static bool Same(byte[]? first, byte[]? second) => first is not null && second is not null
        && first.Length == second.Length && CryptographicOperations.FixedTimeEquals(first, second);
    internal static byte[] Version(Guid actionId, byte[] payloadHash, byte[] snapshotHash, DateTime expires) =>
        Hash($"{actionId:D}\n{Convert.ToHexString(payloadHash)}\n{Convert.ToHexString(snapshotHash)}\n{expires.Ticks}");
    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] ReadVersion(string? value)
    {
        if (value?.Length != 64) throw Invalid();
        try { return Convert.FromHexString(value); } catch (FormatException) { throw Invalid(); }
    }
    internal static void RequireToken(string? token)
    {
        if (token?.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw Invalid();
    }
    internal static EmployeeActionException Invalid() => new(400, "The action request is invalid.");
}
