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
