# Architecture

CP APIs is a .NET 8 solution for Client Portal database access.

- `src/CpApis.Api` hosts the ASP.NET Core Web API and Swagger UI.
- `src/CpApis.Api\Controllers` contains custom controller endpoints such as ticket operations.
- `src/CpApis.Api\Generated\ExplicitObjectEndpointMapper.cs` maps stored procedures from the manifest to module routes.
- `src/CpApis.Shared` contains shared manifest and SQL runtime helpers.
- `src/CpApis.Mcp`, `src/CpApis.Mcp.Core`, and `src/CpApis.Mcp.Http` expose the stored-procedure catalog through MCP transports.
- `src/CpApis.Api\Controllers\CardPointeController.cs` exposes read-only CardPointe gateway inquiries; settlement and funding accept `YYYYMM` and return one dated result per calendar day (with `YYYYMMDD` retained for one-day compatibility). `src/CpApis.Mcp.Core\Tools\CardPointeTools.cs` exposes the matching AI tools. The HTTP MCP host forwards the authenticated caller token to the API, while CardPointe Basic Auth stays on the API host.
- `src/CpApis.InventoryGenerator` generates database object inventory artifacts.

Runtime configuration comes from appsettings files, environment variables, and command-line arguments.
