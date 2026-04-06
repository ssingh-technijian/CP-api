using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CpApis.Shared.Inventory;
using Microsoft.Data.SqlClient;

namespace CpApis.Shared.Runtime;

public static class SqlSupport
{
    public static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static string NormalizeCompositeKey(string databaseAlias, string schema, string name, string kind) =>
        $"{databaseAlias}:{schema}.{name}:{kind}".ToLowerInvariant();

    public static string ComputeSha256(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NormalizeObjectName(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return string.Empty;
        }

        var parts = objectName
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim('[', ']'))
            .ToArray();

        return parts.Length switch
        {
            0 => string.Empty,
            1 => $"dbo.{parts[0]}".ToLowerInvariant(),
            _ => $"{parts[^2]}.{parts[^1]}".ToLowerInvariant()
        };
    }

    public static string ToKind(string typeCode) => typeCode.Trim().ToUpperInvariant() switch
    {
        "P" => "stored-procedure",
        "V" => "view",
        "FN" => "scalar-function",
        "TF" => "table-function",
        "IF" => "inline-table-function",
        _ => "unknown"
    };

    public static SqlDbType ResolveSqlDbType(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "bigint" => SqlDbType.BigInt,
        "binary" => SqlDbType.Binary,
        "bit" => SqlDbType.Bit,
        "char" => SqlDbType.Char,
        "date" => SqlDbType.Date,
        "datetime" => SqlDbType.DateTime,
        "datetime2" => SqlDbType.DateTime2,
        "datetimeoffset" => SqlDbType.DateTimeOffset,
        "decimal" => SqlDbType.Decimal,
        "float" => SqlDbType.Float,
        "image" => SqlDbType.Image,
        "int" => SqlDbType.Int,
        "money" => SqlDbType.Money,
        "nchar" => SqlDbType.NChar,
        "ntext" => SqlDbType.NText,
        "numeric" => SqlDbType.Decimal,
        "nvarchar" => SqlDbType.NVarChar,
        "real" => SqlDbType.Real,
        "smalldatetime" => SqlDbType.SmallDateTime,
        "smallint" => SqlDbType.SmallInt,
        "smallmoney" => SqlDbType.SmallMoney,
        "text" => SqlDbType.Text,
        "time" => SqlDbType.Time,
        "timestamp" => SqlDbType.Timestamp,
        "tinyint" => SqlDbType.TinyInt,
        "uniqueidentifier" => SqlDbType.UniqueIdentifier,
        "varbinary" => SqlDbType.VarBinary,
        "varchar" => SqlDbType.VarChar,
        "xml" => SqlDbType.Xml,
        _ => SqlDbType.Variant
    };

    public static object? ConvertJsonValue(JsonElement value, ParameterManifest parameter)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return DBNull.Value;
        }

        var sqlType = parameter.SqlType.ToLowerInvariant();

        return sqlType switch
        {
            "bit" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
                ? value.GetBoolean()
                : bool.Parse(value.ToString()),
            "bigint" => value.GetInt64(),
            "int" => value.GetInt32(),
            "smallint" => value.GetInt16(),
            "tinyint" => value.GetByte(),
            "decimal" or "numeric" or "money" or "smallmoney" => value.GetDecimal(),
            "float" => value.GetDouble(),
            "real" => value.GetSingle(),
            "date" => DateTime.Parse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).Date,
            "datetime" or "datetime2" or "smalldatetime" => DateTime.Parse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal),
            "datetimeoffset" => DateTimeOffset.Parse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "time" => TimeSpan.Parse(value.ToString(), CultureInfo.InvariantCulture),
            "uniqueidentifier" => Guid.Parse(value.ToString()),
            "binary" or "varbinary" or "image" => Convert.FromBase64String(value.ToString()),
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
        };
    }

    public static object? ConvertScalarFromReader(object? value)
    {
        if (value is DBNull)
        {
            return null;
        }

        return value switch
        {
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            _ => value
        };
    }

    public static async Task<List<ResultSetPayload>> ReadResultSetsAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        var resultSets = new List<ResultSetPayload>();
        var index = 0;

        do
        {
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(NameComparer);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = ConvertScalarFromReader(reader.GetValue(i));
                }

                rows.Add(row);
            }

            resultSets.Add(new ResultSetPayload
            {
                Index = index++,
                RowCount = rows.Count,
                Rows = rows
            });
        }
        while (await reader.NextResultAsync(cancellationToken));

        return resultSets;
    }

    public static string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
