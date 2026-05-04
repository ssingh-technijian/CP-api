# Architecture

CP APIs is a .NET 8 solution for Client Portal database access.

- `src/CpApis.Api` hosts the ASP.NET Core Web API and Swagger UI.
- `src/CpApis.Api\Controllers` contains custom controller endpoints such as ticket operations.
- `src/CpApis.Api\Generated\ExplicitObjectEndpointMapper.cs` maps stored procedures from the manifest to module routes.
- `src/CpApis.Shared` contains shared manifest and SQL runtime helpers.
- `src/CpApis.Mcp`, `src/CpApis.Mcp.Core`, and `src/CpApis.Mcp.Http` expose the stored-procedure catalog through MCP transports.
- `src/CpApis.InventoryGenerator` generates database object inventory artifacts.

Runtime configuration comes from appsettings files, environment variables, and command-line arguments.
