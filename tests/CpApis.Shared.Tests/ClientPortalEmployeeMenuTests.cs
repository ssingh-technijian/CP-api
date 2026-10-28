using CpApis.Mcp.Core.Services;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class ClientPortalEmployeeMenuTests
{
    [Theory]
    [InlineData("T1", "production, rtm", "T1", true)]
    [InlineData("T1", "production, rtm", "T2", false)]
    [InlineData("T1,T2", "production,rtm", "T2", true)]
    [InlineData("T1,T2,T3", "production", "T3", true)]
    [InlineData("T10", "production", "T1", false)]
    [InlineData("T1", "preproduction", "T1", false)]
    [InlineData("T1", "rtm", "T1", false)]
    [InlineData("C1", "production", "C1", false)]
    [InlineData(null, "production", "T1", false)]
    [InlineData("T1", null, "T1", false)]
    [InlineData(" T1, T2 ", " production, rtm ", "T2", true)]
    public void UsesExactActiveProductionRoleAndStageTokens(string? roles, string? stages, string role, bool expected) =>
        Assert.Equal(expected, ClientPortalMenuAccess.Matches(roles, stages, role));
}
