# Testing

Current confirmed verification:

- Build the solution with:

```powershell
dotnet build .\CpApis.slnx
```

Manual API testing can be done through Swagger after running:

```powershell
dotnet run --project .\src\CpApis.Api -- --urls http://127.0.0.1:5180
```

Swagger URL:

```text
http://127.0.0.1:5180/swagger
```

Recent manual verification:

- `POST /api/tickets/{ticketId}/time-entries` was exercised locally with a real JWT bearer token obtained through the configured Entra app registration.
- The local smoke test used ticket `1451537`, sent `CreatedBy: "Jian"`, confirmed the API normalized that alias to `jian@technijian.com`, and verified the saved `dbo.tbl_TicketEntries` row used:
  - `CreatedBy = jian@technijian.com`
  - `AssignToUserID = 8166`
  - `WorkType = Offsite`
  - `RoleType = 1237`
- The smoke-test row was soft-deleted immediately after verification.
