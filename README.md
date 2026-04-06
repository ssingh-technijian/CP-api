# CP-Apis

Metadata-driven API for the Client Portal SQL estate discovered from `D:\Technijian`.

## Projects

- `src/CpApis.InventoryGenerator`
  - Scans the Client Portal source tree.
  - Reads connection strings from the legacy `web.config` files.
  - Pulls SQL metadata from the live databases.
  - Writes the manifest and markdown report under `artifacts/`.
- `src/CpApis.Api`
  - ASP.NET Core Web API with Swagger.
  - Exposes catalog endpoints plus explicit module-based routes for stored procedures only.
  - Removes the old generic executors so each stored procedure is exposed only once.
- `src/CpApis.Mcp`
  - MCP server over `stdio` for attaching the stored-procedure catalog to LLM clients.
  - Uses the local manifest for discovery and calls the HTTP API for execution.
- `src/CpApis.Mcp.Core`
  - Shared MCP tool implementations, models, and API client used by both transports.
- `src/CpApis.Mcp.Http`
  - MCP server over HTTP transport for remote or internet-facing clients.
  - Uses the same local manifest and calls the same HTTP API for execution.
- `src/CpApis.Shared`
  - Shared manifest models and SQL execution helpers.

## Generated Artifacts

These files are generated locally and intentionally excluded from git because they reflect environment-specific database metadata.

- `artifacts/database-object-manifest.json`
- `artifacts/database-object-report.md`

## Regenerate Inventory

```powershell
dotnet run --project .\src\CpApis.InventoryGenerator -- --repo-root D:\Technijian --output-dir D:\CP-Apis\artifacts
```

## Run The API

Private connection strings should be supplied through `src/CpApis.Api/appsettings.Local.json` (copy `src/CpApis.Api/appsettings.Local.example.json`) or environment variables such as `ConnectionStrings__client-portal`.

```powershell
dotnet run --project .\src\CpApis.Api -- --urls http://127.0.0.1:5180
```

Swagger:

```text
http://127.0.0.1:5180/swagger
```

## Sample Endpoints

- `GET /api/system/health`
- `GET /api/catalog/databases`
- `GET /api/catalog/objects?kind=stored-procedure`
- `POST /api/modules/{module}/stored-procedures/{databaseAlias}/{schema}/{name}/execute`

Swagger expands these into concrete routes for each included stored procedure. The request body example for each execute route is generated from the actual SQL parameters, so users can fill values like `ProposalID`, `InvoiceID`, `ClientID`, and execute the API directly.

Examples:

- `POST /api/modules/teams/stored-procedures/teams/dbo/stp_xml_TeamShift_OnShift_List_Get/execute`

## Run The MCP Server

The MCP server uses `stdio` and expects the API to already be running.

```powershell
dotnet run --project .\src\CpApis.Mcp
```

Default assumptions:

- API base URL: `http://127.0.0.1:5180`
- Manifest path: `..\..\artifacts\database-object-manifest.json`

Available MCP tools:

- `list_stored_procedure_modules`
- `list_stored_procedures`
- `describe_stored_procedure`
- `execute_stored_procedure`

Example client config:

```json
{
  "mcpServers": {
    "cp-apis": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "D:\\CP-Apis\\src\\CpApis.Mcp\\CpApis.Mcp.csproj",
        "--no-build"
      ]
    }
  }
}
```

Safety behavior:

- The MCP server blocks stored procedures marked as write operations unless `allowWrite=true` is supplied to `execute_stored_procedure`.
- The MCP server does not connect directly to SQL Server. It calls the existing HTTP API execute endpoint.

## Run The HTTP MCP Server

The HTTP MCP server exposes the same tools over Streamable HTTP at `/mcp`.
Optional local overrides can be provided with `appsettings.Local.json` next to the checked-in appsettings file.

Local-only example:

```powershell
dotnet run --project .\src\CpApis.Mcp.Http -- --urls http://127.0.0.1:8090
```

Internet-facing example:

```powershell
dotnet run --project .\src\CpApis.Mcp.Http -- --urls http://0.0.0.0:8090
```

Endpoints:

- `GET /`
- `GET /health`
- `POST /mcp`

Example MCP client config using HTTP transport:

```json
{
  "mcpServers": {
    "cp-apis-http": {
      "type": "http",
      "url": "http://127.0.0.1:8090/mcp"
    }
  }
}
```

Internet exposure note:

- Do not expose this directly without network controls.
- Put it behind TLS, authentication, and a reverse proxy if it will be reachable from outside your network.
