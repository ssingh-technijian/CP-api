using System.Security.Claims;
using CpApis.Mcp.Core.Services;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class ClientPortalEmployeeIdentityTests
{
    private static readonly Guid Client = Guid.Parse("42873c3e-a7bc-49a2-808e-8fa895d3c01f");
    private const string EmployeeResource = "a5e2c432-af80-4954-b5c3-c84638803eed";
    private static ClientPortalEmployeeOptions Policy() => new() { Enabled = true, TrustedClientIds = [Client.ToString()], ResourceClientId = EmployeeResource };
    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(new[]
    {
        new Claim("iss", $"https://login.microsoftonline.com/{ClientPortalEmployeeContext.MicrosoftTenant}/v2.0"),
        new Claim("tid", ClientPortalEmployeeContext.MicrosoftTenant),
        new Claim("aud", "api://" + EmployeeResource),
        new Claim("oid", "d680f7d6-4d7e-42a3-9b24-2dd705105c5e"),
        new Claim("scp", ClientPortalEmployeeContext.EmployeeScope), new Claim("azp", Client.ToString()), new Claim("acct", "0"),
        new Claim("preferred_username", "mcpqa@technijian.com")
    }, "Bearer"));

    [Fact]
    public void ValidEmployeeUsesSignedMicrosoftIdentity()
    {
        var employee = ClientPortalEmployeeContext.ValidateIdentity(Principal(), Policy());
        Assert.Equal(Guid.Parse("d680f7d6-4d7e-42a3-9b24-2dd705105c5e"), employee.ObjectId); Assert.Equal(Client, employee.ClientId);
    }

    [Theory]
    [InlineData("tid", "other-tenant")]
    [InlineData("aud", "another-resource")]
    [InlineData("iss", "development")]
    [InlineData("oid", "")]
    [InlineData("scp", "")]
    [InlineData("azp", "8a00af77-0d11-40ac-8f06-b7940d75f3f7")]
    [InlineData("acct", "1")]
    [InlineData("acct", "")]
    public void RejectsWrongTenantResourceIssuerApplicationIdentityOrClient(string claim, string value)
    {
        var principal = Principal(); var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(claim)!); identity.AddClaim(new(claim, value));
        Assert.Throws<EmployeeAccessDeniedException>(() => ClientPortalEmployeeContext.ValidateIdentity(principal, Policy()));
    }

    [Theory]
    [InlineData("oid", "d680f7d6-4d7e-42a3-9b24-2dd705105c5e")]
    [InlineData("appid", "8a00af77-0d11-40ac-8f06-b7940d75f3f7")]
    public void RejectsDuplicateOrConflictingClaims(string type, string value)
    {
        var principal = Principal(); ((ClaimsIdentity)principal.Identity!).AddClaim(new(type, value));
        Assert.Throws<EmployeeAccessDeniedException>(() => ClientPortalEmployeeContext.ValidateIdentity(principal, Policy()));
    }

    [Fact]
    public void DisabledOrAnonymousOrUnconfiguredFailsClosed()
    {
        Assert.Throws<EmployeeAccessDeniedException>(() => ClientPortalEmployeeContext.ValidateIdentity(Principal(), new()));
        Assert.Throws<EmployeeAccessDeniedException>(() => ClientPortalEmployeeContext.ValidateIdentity(new(new ClaimsIdentity()), Policy()));
        Assert.Throws<EmployeeAccessDeniedException>(() => ClientPortalEmployeeContext.ValidateIdentity(Principal(), new() { Enabled = true }));
    }

    [Fact]
    public void EmailAndUsernameClaimsCannotChangeTheAuthorizedIdentity()
    {
        var principal = Principal(); var before = ClientPortalEmployeeContext.ValidateIdentity(principal, Policy());
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("preferred_username")!);
        identity.AddClaim(new("preferred_username", "another@example.com"));
        identity.AddClaim(new("upn", "reused@technijian.com"));
        Assert.Equal(before, ClientPortalEmployeeContext.ValidateIdentity(principal, Policy()));
    }

    [Fact]
    public void NativePermissionsDenyWinsAndSelfDoesNotBecomeAll()
    {
        Assert.False(ClientPortalPermissionPolicy.Allows([new("Allow", "All"), new("Deny", null)], true));
        Assert.False(ClientPortalPermissionPolicy.Allows([new("Allow", "Self")], false));
        Assert.True(ClientPortalPermissionPolicy.Allows([new("Allow", "Self")], true));
        Assert.False(ClientPortalPermissionPolicy.Allows([new("Allow", null)], true));
        Assert.True(ClientPortalPermissionPolicy.Allows([new("Allow", null)], false, allowUnqualified: true));
        Assert.False(ClientPortalPermissionPolicy.Allows([], true));
    }
}
