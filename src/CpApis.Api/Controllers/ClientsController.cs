using CpApis.Api.Services;
using CpApis.Shared.Inventory;
using CpApis.Shared.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace CpApis.Api.Controllers;

[ApiController]
[Route("api/clients")]
public sealed class ClientsController : ControllerBase
{
    private static readonly DatabaseObjectManifest ActiveClientsProcedure = new()
    {
        Key = "client-portal:dbo.stp_Active_Clients:stored-procedure",
        Module = "Clients",
        DatabaseAlias = "client-portal",
        DatabaseName = "CP_Production_New",
        Schema = "dbo",
        Name = "stp_Active_Clients",
        Kind = "stored-procedure",
        TypeCode = "P",
        TypeDescription = "SQL_STORED_PROCEDURE",
        IsActiveRoot = true,
        IsIncluded = true,
        IsWriteOperation = false
    };

    private readonly DatabaseExecutionService _executionService;

    public ClientsController(DatabaseExecutionService executionService)
    {
        _executionService = executionService;
    }

    [HttpGet("active")]
    [ProducesResponseType(typeof(CommandExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetActiveClients(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _executionService.ExecuteStoredProcedureAsync(
                ActiveClientsProcedure,
                new StoredProcedureExecutionRequest(),
                cancellationToken);

            return Ok(response);
        }
        catch (SqlException ex)
        {
            return Problem(
                title: "Stored procedure execution failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            return Problem(
                title: "Unexpected error",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
