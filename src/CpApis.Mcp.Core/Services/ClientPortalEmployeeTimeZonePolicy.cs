namespace CpApis.Mcp.Core.Services;

public enum PortalWorkRegion { India, Pacific }

/// <summary>Native work-region classification; never guess from the model, host or browser.</summary>
public static class ClientPortalEmployeeTimeZonePolicy
{
    public static PortalWorkRegion Resolve(string? locationTopFilter)
    {
        if(locationTopFilter?.StartsWith("Tech.Clients.Technijian.Tech-IN",StringComparison.OrdinalIgnoreCase)==true)return PortalWorkRegion.India;
        if(locationTopFilter?.StartsWith("Tech.Clients.Technijian.Tech-US",StringComparison.OrdinalIgnoreCase)==true)return PortalWorkRegion.Pacific;
        throw new EmployeeActionException(409,"Your native work region is not configured for an unambiguous time log. Use the Portal until the employee profile is reviewed.");
    }
}
