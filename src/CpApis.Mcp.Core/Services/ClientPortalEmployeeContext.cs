using System.Data;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CpApis.Mcp.Core.Services;

public sealed class ClientPortalEmployeeOptions
{
    public bool Enabled { get; set; }
    public string[] TrustedClientIds { get; set; } = [];
    public string ResourceClientId { get; set; } = "";
}

public interface IEmployeePrincipalAccessor
{
    ClaimsPrincipal? GetPrincipal();
}

public sealed record MicrosoftEmployeeIdentity(Guid ObjectId, Guid ClientId);
public sealed record PortalEmployee(Guid ObjectId, int UserId, string Username, string Role, string LocationTopFilter)
{
    public bool IsPortalAdministrator => Role is "T1" or "T2";
}

public sealed class EmployeeAccessDeniedException(string message) : Exception(message);

/// <summary>
/// Uses authenticated Microsoft claims and the native directory's canonical DirID.
/// tbl_Users.UserID is deliberately not used: native vw_User_Profile.UserID is DirID.
/// The immutable tenant/object-ID mapping is administrator-controlled. No email/UPN,
/// caller-supplied actor ID, role or client list is authoritative.
/// </summary>
public sealed class ClientPortalEmployeeContext(
    IEmployeePrincipalAccessor principalAccessor, IOptions<ClientPortalEmployeeOptions> options)
{
    public const string MicrosoftTenant = "cab8077a-3f42-4277-b7bd-5c9023e826d8";
    public const string ResourceClient = "1cb85b95-8f51-4b69-a8a9-834c8d3ce0e1";
    public const string EmployeeScope = "ClientPortal.Access";

    public MicrosoftEmployeeIdentity GetIdentity() => ValidateIdentity(principalAccessor.GetPrincipal(), options.Value);

    public static MicrosoftEmployeeIdentity ValidateIdentity(ClaimsPrincipal? user, ClientPortalEmployeeOptions policy)
    {
        if (!policy.Enabled || user?.Identity?.IsAuthenticated != true)
            throw Denied();
        var issuer = SingleClaim(user, "iss");
        if (issuer != $"https://login.microsoftonline.com/{MicrosoftTenant}/v2.0"
            && issuer != $"https://sts.windows.net/{MicrosoftTenant}/") throw Denied();
        if (!string.Equals(SingleClaim(user, "tid"), MicrosoftTenant, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(SingleClaim(user, "oid"), out var objectId) || objectId == Guid.Empty)
            throw Denied();
        var audience = SingleClaim(user, "aud");
        if (!Guid.TryParse(policy.ResourceClientId, out var resource) || resource == Guid.Empty
            || resource.ToString("D") == ResourceClient
            || audience != resource.ToString("D") && audience != $"api://{resource:D}") throw Denied();
        if (!(SingleClaim(user, "scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(EmployeeScope, StringComparer.Ordinal)
            || SingleClaim(user, "acct") != "0")
            throw Denied();
        var azp = SingleClaim(user, "azp"); var appid = SingleClaim(user, "appid");
        if (azp is not null && appid is not null && !string.Equals(azp, appid, StringComparison.OrdinalIgnoreCase)) throw Denied();
        if (!Guid.TryParse(azp ?? appid, out var client) || client == Guid.Empty
            || !policy.TrustedClientIds.Any(id => Guid.TryParse(id, out var allowed) && allowed == client)) throw Denied();
        return new(objectId, client);
    }

    public async Task<PortalEmployee> ResolveAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken ct)
    {
        var identity = GetIdentity();
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10;
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "mcp.stp_EmployeeIdentity_Get";
        command.Parameters.Add("@MicrosoftTenantId", SqlDbType.UniqueIdentifier).Value = Guid.Parse(MicrosoftTenant);
        command.Parameters.Add("@MicrosoftObjectId", SqlDbType.UniqueIdentifier).Value = identity.ObjectId;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw Denied();
        var actor = new PortalEmployee(identity.ObjectId, reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3));
        if (actor.UserId <= 0 || await reader.ReadAsync(ct)) throw Denied();
        return actor;
    }

    public static async Task<IReadOnlyList<PortalPermission>> ReadPermissionsAsync(
        SqlConnection connection, SqlTransaction? transaction, int userId, string scope, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10;
        // This native function implements inheritance and Deny precedence. The older bool
        // helper ignores explicit Deny and therefore is not sufficient for MCP authorization.
        command.CommandText = "SELECT [Action],[Value] FROM dbo.fn_dt_UserPermScope_Check_Value(@UserID,@Scope);";
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Scope", SqlDbType.NVarChar, 300).Value = scope;
        var permissions = new List<PortalPermission>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) permissions.Add(new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return permissions;
    }

    private static string? SingleClaim(ClaimsPrincipal user, string type)
    {
        var claims = user.FindAll(type).ToArray();
        if (claims.Length > 1) throw Denied();
        return claims.Length == 0 ? null : claims[0].Value;
    }

    private static EmployeeAccessDeniedException Denied() => new("Client Portal requires an authorized Technijian Microsoft employee identity and one unique active native employee account.");
}

public sealed record PortalPermission(string? Action, string? Value);

public static class ClientPortalPermissionPolicy
{
    public static bool Allows(IReadOnlyList<PortalPermission> permissions, bool isOwner, bool allowUnqualified = false)
    {
        if (permissions.Any(p => string.Equals(p.Action, "Deny", StringComparison.OrdinalIgnoreCase))) return false;
        return permissions.Any(p => string.Equals(p.Action, "Allow", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(p.Value, "All", StringComparison.OrdinalIgnoreCase)
                || isOwner && string.Equals(p.Value, "Self", StringComparison.OrdinalIgnoreCase)
                || allowUnqualified && string.IsNullOrWhiteSpace(p.Value)));
    }
}
