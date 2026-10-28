# Security

The API configures Microsoft Entra JWT bearer authentication in `src/CpApis.Api\Program.cs`.

- Controllers are globally required to authorize through `app.MapControllers().RequireAuthorization()`.
- Stored-procedure module endpoints also require authorization.
- Specific controller actions can opt out with `[AllowAnonymous]`; ticket creation currently does this.
- Ticket time-entry creation remains authenticated; it is not marked `[AllowAnonymous]`.
- The ticket time-entry endpoint defaults business attribution to the active directory user `jian@technijian.com` when the AI alias `Jian` is used or when `CreatedBy` is omitted.
- CORS policy `Open` allows any origin, header, and method.
- Do not expose MCP HTTP or API services publicly without TLS, authentication, and network controls.
- The HTTP MCP transport requires the same Microsoft Entra JWT bearer authentication as the API. It forwards the caller's `Authorization` header to the protected API hop; the API authorizes the request before any tool can run.
- CardPointe gateway credentials remain only on the API host and are sent to CardPointe as server-side Basic Auth. MCP clients never receive those credentials.
- Credential-returning database objects are removed from the catalog at load time by `ManifestCatalogService` (built-in name patterns `Cred(?!it)`, `ApiKey`, `Secret`; extend with `RuntimeCatalog:AdditionalDeniedNamePatterns`). They are never routed, listed, described, or executable through REST or MCP, regardless of what the generated manifest contains. `/api/system/health` reports `deniedObjectCount`; `tests/CpApis.Shared.Tests` guards the list.
