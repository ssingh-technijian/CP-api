using CpApis.Mcp.Core.Services;
using Xunit;
namespace CpApis.Shared.Tests;
public sealed class ClientPortalEmployeeTimeZoneTests
{
    [Theory]
    [InlineData("Tech.Clients.Technijian.Tech-IN.Team",PortalWorkRegion.India)]
    [InlineData("tech.clients.technijian.tech-in",PortalWorkRegion.India)]
    [InlineData("Tech.Clients.Technijian.Tech-US.Team",PortalWorkRegion.Pacific)]
    public void UsesNativeRegionInsteadOfHostOrModelTimezone(string filter,PortalWorkRegion expected)=>Assert.Equal(expected,ClientPortalEmployeeTimeZonePolicy.Resolve(filter));
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Tech.Clients.Technijian")]
    [InlineData("Unknown.Timezone")]
    public void UnresolvedRegionCannotBeAssumedPacific(string? filter)=>Assert.Equal(409,Assert.Throws<EmployeeActionException>(()=>ClientPortalEmployeeTimeZonePolicy.Resolve(filter)).StatusCode);
}
