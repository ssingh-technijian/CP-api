# Security

The API configures Microsoft Entra JWT bearer authentication in `src/CpApis.Api\Program.cs`.

- Controllers are globally required to authorize through `app.MapControllers().RequireAuthorization()`.
- Stored-procedure module endpoints also require authorization.
- Specific controller actions can opt out with `[AllowAnonymous]`; ticket creation currently does this.
- CORS policy `Open` allows any origin, header, and method.
- Do not expose MCP HTTP or API services publicly without TLS, authentication, and network controls.
