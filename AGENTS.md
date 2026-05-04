## Project Notes

- This repository contains a .NET 8 Client Portal API solution.
- Keep custom business endpoints in `src/CpApis.Api\Controllers`.
- The API uses Microsoft Entra JWT authentication globally; controller actions can opt out with `[AllowAnonymous]`.
- Connection strings are read from configuration keys such as `ConnectionStrings:client-portal`.
- Generated/local database inventory and test artifacts under `artifacts*`, logs, and local settings can be environment-specific.

## Documentation

- Update `docs/` when API behavior, database calls, security behavior, or test expectations change.
- Document only behavior confirmed in code or explicitly requested.
