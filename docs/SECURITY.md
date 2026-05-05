# Security

The API configures Microsoft Entra JWT bearer authentication in `src/CpApis.Api\Program.cs`.

- Controllers are globally required to authorize through `app.MapControllers().RequireAuthorization()`.
- Stored-procedure module endpoints also require authorization.
- Specific controller actions can opt out with `[AllowAnonymous]`; ticket creation currently does this.
- Ticket time-entry creation remains authenticated; it is not marked `[AllowAnonymous]`.
- The ticket time-entry endpoint defaults business attribution to the active directory user `jian@technijian.com` when the AI alias `Jian` is used or when `CreatedBy` is omitted.
- CORS policy `Open` allows any origin, header, and method.
- Do not expose MCP HTTP or API services publicly without TLS, authentication, and network controls.
