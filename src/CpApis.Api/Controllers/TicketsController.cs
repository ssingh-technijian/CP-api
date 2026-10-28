using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CpApis.Api.OpenApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace CpApis.Api.Controllers;

[ApiController]
[Route("api/tickets")]
public sealed class TicketsController : ControllerBase
{
    private const int DefaultAssetId = 0;
    private const string DefaultClientCode = "ORX";
    private const int DefaultDayId = 0;
    private const decimal DefaultEstimatedHours = 0.00m;
    private const string DefaultEmptyText = "";
    private const string DefaultPriority = "Watch";
    private const int DefaultParentId = 0;
    private const string DefaultTimeEntryActorAlias = "Jian";
    private const string DefaultTimeEntryActorUsername = "jian@technijian.com";
    private const int DefaultRecurringOption = 0;
    private const string DefaultRecurringType = "0";
    private const string DefaultRequestType = "Client Portal";
    private const string DefaultStatus = "New";

    private readonly IConfiguration _configuration;

    public TicketsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("entries")]
    public async Task<IActionResult> GetTicketEntries([FromQuery] string? clientCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientCode))
        {
            return BadRequestProblem("Invalid request", "ClientCode query parameter is required.");
        }

        try
        {
            await using var connection = new SqlConnection(GetConnectionString("client-portal"));
            await connection.OpenAsync(cancellationToken);

            var normalizedClientCode = clientCode.Trim();
            var entries = await GetTicketEntriesAsync(connection, normalizedClientCode, cancellationToken);

            return Ok(entries);
        }
        catch (SqlException ex)
        {
            return Problem(
                title: "Ticket entry lookup failed",
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

    [HttpPost]
    [HttpPost("create")]
    public async Task<IActionResult> CreateTicket([FromBody] CreateTicketRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequestProblem("Invalid request", "Request body is required.");
        }

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return BadRequestProblem("Invalid request", validationError);
        }

        try
        {
            await using var connection = new SqlConnection(GetConnectionString("client-portal"));
            await connection.OpenAsync(cancellationToken);

            var assignedUser = await GetActiveUserAsync(connection, request.AssignedUser!, cancellationToken);
            if (assignedUser is null)
            {
                return BadRequestProblem("Assigned user not found", $"No active user was found for '{request.AssignedUser}'.");
            }

            var requestedClientCode = string.IsNullOrWhiteSpace(request.ClientCode)
                ? DefaultClientCode
                : request.ClientCode.Trim();

            var clientContext = await GetClientContextAsync(connection, requestedClientCode, cancellationToken);
            if (clientContext is null)
            {
                return Problem(
                    title: "Client not found",
                    detail: $"No active client configuration was found for ClientCode '{requestedClientCode}'.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var requestedRoleType = string.IsNullOrWhiteSpace(request.RoleTypeTxt)
                ? request.RoleType
                : request.RoleTypeTxt;

            var roleType = await ResolveRoleTypeAsync(connection, requestedRoleType!, request.POD!, cancellationToken);
            if (roleType is null)
            {
                return BadRequestProblem("Role type not found", $"No ticket role mapping was found for '{requestedRoleType}'.");
            }

            var contractId = await GetActiveContractIdAsync(connection, clientContext.ClientId, cancellationToken);
            if (contractId <= 0)
            {
                return BadRequestProblem("Contract not found", $"No active contract was found for ClientCode '{clientContext.ClientCode}'.");
            }

            var statusId = await GetLookupIdAsync(connection, "dbo.vw_LookupV_Ticket_Status", DefaultStatus, cancellationToken);
            var priorityId = await GetLookupIdAsync(connection, "dbo.vw_LookupV_Ticket_Priority", DefaultPriority, cancellationToken);
            var requestorDirId = clientContext.RequestorDirId;
            var locationTopFilter = clientContext.LocationTopFilter;

            if (string.IsNullOrWhiteSpace(locationTopFilter))
            {
                return Problem(
                    title: "Location not found",
                    detail: $"No location mapping was found for ClientCode '{clientContext.ClientCode}'.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var xmlInput = BuildTicketXml(
                requestorDirId,
                assignedUser.DirId,
                contractId,
                request.Title!,
                request.Desc!,
                request.CreatedBy!,
                clientContext.ClientId,
                roleType.Id,
                statusId,
                priorityId,
                locationTopFilter);

            var ticketId = await ExecuteCreateTicketAsync(connection, xmlInput, cancellationToken);
            if (ticketId <= 0)
            {
                return Problem(
                    title: "Ticket was not created",
                    detail: "The create stored procedure did not return a TicketID.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            await ApplyTicketDefaultsAsync(connection, ticketId, cancellationToken);

            var summary = await GetTicketSummaryAsync(connection, ticketId, cancellationToken);
            if (summary is null)
            {
                return Ok(new
                {
                    TicketID = ticketId
                });
            }

            return Ok(new
            {
                summary.TicketID,
                summary.Title,
                summary.CreatedBy,
                summary.ClientID,
                summary.ClientCode,
                summary.ClientName,
                summary.AssignTo_DirID,
                summary.Requestor_DirID,
                Requestor = summary.LocationName,
                summary.RoleType,
                summary.RoleTypeTxt,
                Location = summary.LocationName,
                summary.LocationName,
                Location_Name = summary.LocationName,
                summary.LocationTopFilter,
                summary.ParentID,
                summary.AssetID,
                summary.Estimated_Hours,
                summary.Description_RTF,
                summary.Internal_Notes,
                summary.DayID,
                summary.RecurringType,
                summary.RecurringOption,
                summary.StatusTxt,
                summary.PriorityTxt,
                summary.RequestType,
                summary.CreateDate
            });
        }
        catch (SqlException ex)
        {
            return Problem(
                title: "Ticket creation failed",
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

    [HttpPut("{ticketId:int}")]
    [HttpPatch("{ticketId:int}")]
    [RequestBodyExample(
        "Only send the ticket fields you want to change. For an internal-notes-only update, send a minimal JSON body like the example below and omit the other properties.",
        "{\"Internal_Notes\":\"this is test notes\"}")]
    public async Task<IActionResult> UpdateTicket(int ticketId, [FromBody] UpdateTicketRequest? request, CancellationToken cancellationToken)
    {
        if (ticketId <= 0)
        {
            return BadRequestProblem("Invalid request", "TicketID must be greater than zero.");
        }

        if (request is null)
        {
            return BadRequestProblem("Invalid request", "Request body is required.");
        }

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return BadRequestProblem("Invalid request", validationError);
        }

        try
        {
            await using var connection = new SqlConnection(GetConnectionString("client-portal"));
            await connection.OpenAsync(cancellationToken);

            var currentTicket = await GetTicketForUpdateAsync(connection, ticketId, cancellationToken);
            if (currentTicket is null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Ticket not found",
                    Detail = $"TicketID '{ticketId}' was not found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            var requestedAssignToUsername = FirstNonEmpty(request.AssignToUsername, request.AssignedUser);
            DirectoryUser? requestedAssignToUser = null;
            if (string.IsNullOrWhiteSpace(requestedAssignToUsername) is false)
            {
                requestedAssignToUser = await GetActiveUserAsync(connection, requestedAssignToUsername, cancellationToken);
                if (requestedAssignToUser is null)
                {
                    return BadRequestProblem(
                        "Invalid assigned user",
                        $"Assigned user '{requestedAssignToUsername}' was not found or is inactive. Please enter a valid active username.");
                }
            }

            var resolvedStatus = await ResolveLookupValueAsync(
                connection,
                "dbo.vw_LookupV_Ticket_Status",
                FirstNonEmpty(request.Status, currentTicket.Status),
                cancellationToken);

            if (resolvedStatus is null)
            {
                var validStatuses = await GetLookupDescriptionsAsync(connection, "dbo.vw_LookupV_Ticket_Status", cancellationToken);
                var attemptedStatus = FirstNonEmpty(request.Status, currentTicket.Status);

                if (string.IsNullOrWhiteSpace(request.Status) is false)
                {
                    return BadRequestProblem(
                        "Invalid status",
                        $"Status '{attemptedStatus}' was not found. Valid statuses: {string.Join(", ", validStatuses)}.");
                }

                return Problem(
                    title: "Ticket update failed",
                    detail: $"Existing ticket status '{attemptedStatus}' could not be resolved to a valid lookup value.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var resolvedPriority = await ResolveLookupValueAsync(
                connection,
                "dbo.vw_LookupV_Ticket_Priority",
                FirstNonEmpty(request.Priority, currentTicket.Priority),
                cancellationToken);

            if (resolvedPriority is null)
            {
                var validPriorities = await GetLookupDescriptionsAsync(connection, "dbo.vw_LookupV_Ticket_Priority", cancellationToken);
                var attemptedPriority = FirstNonEmpty(request.Priority, currentTicket.Priority);

                if (string.IsNullOrWhiteSpace(request.Priority) is false)
                {
                    return BadRequestProblem(
                        "Invalid priority",
                        $"Priority '{attemptedPriority}' was not found. Valid priorities: {string.Join(", ", validPriorities)}.");
                }

                return Problem(
                    title: "Ticket update failed",
                    detail: $"Existing ticket priority '{attemptedPriority}' could not be resolved to a valid lookup value.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var updatedTicket = Merge(
                currentTicket,
                request,
                requestedAssignToUser,
                resolvedPriority.Id.ToString(CultureInfo.InvariantCulture),
                resolvedStatus.Id.ToString(CultureInfo.InvariantCulture));
            await ExecuteUpdateTicketAsync(connection, updatedTicket, cancellationToken);

            var requestedInternalNotes = FirstNonEmpty(request.Internal_Notes, request.InternalNotes);
            var isInternalTicket = string.Equals(
                resolvedStatus.Description,
                "Internal Ticket",
                StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(requestedInternalNotes) is false && isInternalTicket)
            {
                var actorUsername = FirstNonEmpty(GetActorUsername(), currentTicket.CreatedBy);
                var assignToDirId = int.TryParse(updatedTicket.AssignToUsername, out var parsedAssignToDirId)
                    ? parsedAssignToDirId
                    : 0;

                await SyncTicketEntryNotesAsync(
                    connection,
                    ticketId,
                    requestedInternalNotes,
                    actorUsername,
                    assignToDirId,
                    currentTicket.LocationTopFilter,
                    cancellationToken);
            }

            var summary = await GetTicketSummaryAsync(connection, ticketId, cancellationToken);
            if (summary is null)
            {
                return Ok(new { TicketID = ticketId });
            }

            return Ok(summary);
        }
        catch (SqlException ex)
        {
            return Problem(
                title: "Ticket update failed",
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

    [HttpPost("{ticketId:int}/time-entries")]
    [RequestBodyExample(
        "Send the time window and notes for the work performed. If CreatedBy is omitted, the API logs the time entry under the AI service user Jian.",
        "{\"StartDateTime\":\"2026-05-05T10:00:00\",\"EndDateTime\":\"2026-05-05T10:15:00\",\"Notes\":\"AI follow-up completed\",\"InternalNotes\":\"Reviewed internally before client update\"}")]
    public async Task<IActionResult> AddTicketTimeEntry(int ticketId, [FromBody] AddTicketTimeEntryRequest? request, CancellationToken cancellationToken)
    {
        if (ticketId <= 0)
        {
            return BadRequestProblem("Invalid request", "TicketID must be greater than zero.");
        }

        if (request is null)
        {
            return BadRequestProblem("Invalid request", "Request body is required.");
        }

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return BadRequestProblem("Invalid request", validationError);
        }

        try
        {
            await using var connection = new SqlConnection(GetConnectionString("client-portal"));
            await connection.OpenAsync(cancellationToken);

            var ticket = await GetTicketTimeEntryContextAsync(connection, ticketId, cancellationToken);
            if (ticket is null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Ticket not found",
                    Detail = $"TicketID '{ticketId}' was not found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            var actorUsername = NormalizeTimeEntryUsername(
                FirstNonEmpty(request.CreatedBy, request.Username, DefaultTimeEntryActorAlias));
            var actorUser = await GetActiveUserAsync(connection, actorUsername, cancellationToken);
            if (actorUser is null)
            {
                return BadRequestProblem(
                    "Invalid created by user",
                    $"CreatedBy '{actorUsername}' was not found or is inactive. Please enter a valid active username.");
            }

            var requestedAssignToUsername = NormalizeTimeEntryUsername(FirstNonEmpty(request.AssignToUsername, request.AssignedUser));
            var assignToUsername = string.IsNullOrWhiteSpace(requestedAssignToUsername)
                ? actorUsername
                : requestedAssignToUsername;
            var assignToUser = await GetActiveUserAsync(connection, assignToUsername, cancellationToken);
            if (assignToUser is null)
            {
                return BadRequestProblem(
                    "Invalid assigned user",
                    $"Assigned user '{assignToUsername}' was not found or is inactive. Please enter a valid active username.");
            }

            var requestedStatus = FirstNonEmpty(request.Status);
            LookupValue? resolvedStatus = null;
            if (string.IsNullOrWhiteSpace(requestedStatus) is false)
            {
                resolvedStatus = await ResolveLookupValueAsync(
                    connection,
                    "dbo.vw_LookupV_Ticket_Status",
                    requestedStatus,
                    cancellationToken);

                if (resolvedStatus is null)
                {
                    var validStatuses = await GetLookupDescriptionsAsync(connection, "dbo.vw_LookupV_Ticket_Status", cancellationToken);
                    return BadRequestProblem(
                        "Invalid status",
                        $"Status '{requestedStatus}' was not found. Valid statuses: {string.Join(", ", validStatuses)}.");
                }
            }

            if (ticket.RoleTypeId <= 0)
            {
                return BadRequestProblem(
                    "Role type required",
                    "This ticket does not have a saved role type in tbl_Tickets.");
            }

            var driveTime = request.DriveTime ?? 0.00m;
            var calculatedHoursWorked = CalculateTimeEntryHours(request.StartDateTime!.Value, request.EndDateTime!.Value, driveTime);
            if (calculatedHoursWorked <= 0)
            {
                return BadRequestProblem(
                    "Invalid time range",
                    "HoursWorked must be greater than zero after subtracting DriveTime from the time between StartDateTime and EndDateTime.");
            }

            var hoursWorked = request.HoursWorked ?? calculatedHoursWorked;
            if (hoursWorked <= 0)
            {
                return BadRequestProblem("Invalid request", "HoursWorked must be greater than zero.");
            }

            if (hoursWorked > calculatedHoursWorked)
            {
                return BadRequestProblem(
                    "Invalid request",
                    "HoursWorked cannot be greater than the time between StartDateTime and EndDateTime after subtracting DriveTime.");
            }

            var effectiveStatusId = resolvedStatus?.Id ?? ticket.StatusId;
            if (effectiveStatusId <= 0)
            {
                return Problem(
                    title: "Time entry save failed",
                    detail: $"TicketID '{ticketId}' does not have a valid status ID.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var effectiveStatusText = resolvedStatus?.Description ?? ticket.StatusText;
            var effectiveRoleTypeId = ticket.RoleTypeId;
            var effectiveRoleTypeText = ticket.RoleTypeText;
            var effectiveWorkType = NormalizeTimeEntryWorkType(request.WorkType, driveTime);
            var internalNotes = FirstNonEmpty(request.Internal_Notes, request.InternalNotes);
            var trimmedNotes = request.Notes?.Trim() ?? string.Empty;

            var xmlInput = BuildTicketTimeEntryXml(
                ticketId,
                request.StartDateTime.Value,
                request.EndDateTime.Value,
                driveTime,
                hoursWorked,
                trimmedNotes,
                actorUsername,
                effectiveWorkType,
                effectiveRoleTypeId,
                internalNotes,
                assignToUser.DirId,
                effectiveStatusId);

            var ticketEntryId = await ExecuteAddTicketTimeEntryAsync(connection, xmlInput, cancellationToken);
            if (ticketEntryId <= 0)
            {
                return Problem(
                    title: "Time entry was not created",
                    detail: "The save stored procedure did not return a TicketEntryID.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Ok(new TicketTimeEntryResponse(
                ticketEntryId,
                ticketId,
                actorUsername,
                assignToUser.DirId,
                assignToUsername,
                request.StartDateTime.Value,
                request.EndDateTime.Value,
                driveTime,
                hoursWorked,
                trimmedNotes,
                internalNotes,
                effectiveWorkType,
                effectiveRoleTypeId,
                effectiveRoleTypeText,
                effectiveStatusId,
                effectiveStatusText,
                string.IsNullOrWhiteSpace(internalNotes) is false && hoursWorked > 0));
        }
        catch (SqlException ex)
        {
            return Problem(
                title: "Time entry save failed",
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

    private IActionResult BadRequestProblem(string title, string detail) =>
        BadRequest(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status400BadRequest
        });

    private string GetActorUsername() =>
        FirstNonEmpty(
            User.FindFirstValue("preferred_username"),
            User.FindFirstValue("upn"),
            User.FindFirstValue(ClaimTypes.Upn),
            User.FindFirstValue(ClaimTypes.Email),
            User.FindFirstValue("email"),
            User.Identity?.Name);

    private static string? Validate(CreateTicketRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.POD))
        {
            return "POD is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return "Title is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Desc))
        {
            return "Desc is required.";
        }

        if (string.IsNullOrWhiteSpace(request.CreatedBy))
        {
            return "CreatedBy is required.";
        }

        if (string.IsNullOrWhiteSpace(request.AssignedUser))
        {
            return "AssignedUser is required.";
        }

        if (string.IsNullOrWhiteSpace(request.RoleType) && string.IsNullOrWhiteSpace(request.RoleTypeTxt))
        {
            return "RoleType or RoleTypeTxt is required.";
        }

        return null;
    }

    private static string? Validate(UpdateTicketRequest request)
    {
        if (request.Estimated_Hours is < 0)
        {
            return "Estimated_Hours cannot be negative.";
        }

        return null;
    }

    private static string? Validate(AddTicketTimeEntryRequest request)
    {
        if (request.StartDateTime is null)
        {
            return "StartDateTime is required.";
        }

        if (request.EndDateTime is null)
        {
            return "EndDateTime is required.";
        }

        if (request.EndDateTime <= request.StartDateTime)
        {
            return "EndDateTime must be greater than StartDateTime.";
        }

        if (request.DriveTime is < 0)
        {
            return "DriveTime cannot be negative.";
        }

        if (request.HoursWorked is <= 0)
        {
            return "HoursWorked must be greater than zero.";
        }

        if (string.IsNullOrWhiteSpace(request.Notes))
        {
            return "Notes is required.";
        }

        return null;
    }

    private string GetConnectionString(string databaseAlias)
    {
        var connectionString = _configuration[$"ConnectionStrings:{databaseAlias}"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string for database alias '{databaseAlias}' is not configured.");
        }

        return connectionString;
    }

    private static XDocument BuildTicketXml(
        int requestorDirId,
        int assignToDirId,
        int contractId,
        string title,
        string description,
        string createdBy,
        int clientId,
        int roleTypeId,
        int statusId,
        int priorityId,
        string locationTopFilter)
    {
        return new XDocument(
            new XElement("Root",
                new XElement("Ticket",
                    new XElement("Requestor_DirID", requestorDirId),
                    new XElement("AssignTo_DirID", assignToDirId),
                    new XElement("ParentID", DefaultParentId),
                    new XElement("ContractID", contractId),
                    new XElement("AssetID", DefaultAssetId),
                    new XElement("Title", title.Trim()),
                    new XElement("Description", description.Trim()),
                    new XElement("AssetTxt", DefaultEmptyText),
                    new XElement("RequestType", DefaultRequestType),
                    new XElement("LocationTopFilter", locationTopFilter),
                    new XElement("RoleType", roleTypeId),
                    new XElement("ClientID", clientId),
                    new XElement("Status", statusId),
                    new XElement("StatusTxt", DefaultStatus),
                    new XElement("Priority", priorityId),
                    new XElement("PriorityTxt", DefaultPriority),
                    new XElement("CreatedBy", createdBy.Trim()))));
    }

    private static async Task<int> ExecuteCreateTicketAsync(SqlConnection connection, XDocument xmlInput, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("dbo.stp_xml_Tkt_API_CreateV3", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 180
        };

        command.Parameters.Add(new SqlParameter("@XML_IN", SqlDbType.Xml) { Value = xmlInput.ToString(SaveOptions.DisableFormatting) });
        command.Parameters.Add(new SqlParameter("@CheckPeriod", SqlDbType.Int) { Value = 0 });
        var xmlOut = new SqlParameter("@XML_OUT", SqlDbType.Xml)
        {
            Direction = ParameterDirection.Output
        };
        command.Parameters.Add(xmlOut);

        await command.ExecuteNonQueryAsync(cancellationToken);

        var rawXml = xmlOut.Value as string;
        if (string.IsNullOrWhiteSpace(rawXml))
        {
            return 0;
        }

        var document = XDocument.Parse(rawXml);
        return int.TryParse(document.Descendants("TicketID").FirstOrDefault()?.Value, out var ticketId)
            ? ticketId
            : 0;
    }

    private static async Task<List<TicketEntryListItem>> GetTicketEntriesAsync(SqlConnection connection, string clientCode, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CAST(e.TicketID AS varchar(20)) AS TicketID,
                   ISNULL(t.Title, '') AS Title,
                   ISNULL(t.Description, '') AS Description,
                   e.StartDateTime,
                   e.EndDateTime,
                   ISNULL(e.Notes, '') AS Notes,
                   CAST(ROUND(
                       CASE
                           WHEN e.StartDateTime IS NOT NULL AND e.EndDateTime IS NOT NULL
                               THEN DATEDIFF(second, e.StartDateTime, e.EndDateTime) / 3600.0
                           ELSE ISNULL(CONVERT(decimal(18, 4), e.NH_HoursWorked), 0)
                              + ISNULL(CONVERT(decimal(18, 4), e.AH_HoursWorked), 0)
                              + ISNULL(CONVERT(decimal(18, 4), e.Onsite_HoursWorked), 0)
                              + ISNULL(CONVERT(decimal(18, 4), e.DriveTime), 0)
                       END, 2) AS decimal(18, 2)) AS Duration,
                   ISNULL(NULLIF(e.AssignedName, ''), ISNULL(t.AssignedName, '')) AS Employee,
                   CONVERT(char(7), COALESCE(e.StartDateTime, e.EndDateTime, t.CreateDate), 126) AS [Month]
            FROM dbo.tbl_TicketEntries e
            INNER JOIN dbo.tbl_Tickets t ON t.TicketID = e.TicketID
            WHERE e.IsActive = 1
              AND ISNULL(e.LocationTopFilter, '') LIKE @LocationTopFilter
            ORDER BY e.StartDateTime,
                     e.TicketEntryID;
            """,
            connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = 180
        };

        command.Parameters.Add(new SqlParameter("@LocationTopFilter", SqlDbType.NVarChar, 4000)
        {
            Value = "%" + clientCode.Trim() + "%"
        });

        var entries = new List<TicketEntryListItem>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new TicketEntryListItem(
                reader.GetString(0),
                reader.GetString(1),
                StripHtml(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.GetString(5),
                reader.GetDecimal(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
        }

        return entries;
    }

    private static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value, "<.*?>", string.Empty);
    }

    private static async Task<ClientContext?> GetClientContextAsync(SqlConnection connection, string clientCode, CancellationToken cancellationToken)
    {
        var requestedClientCode = clientCode.Trim();
        ActiveClientRow? activeClient = null;

        await using (var activeClientsCommand = new SqlCommand("dbo.stp_Active_Clients", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 180
        })
        await using (var activeClientsReader = await activeClientsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await activeClientsReader.ReadAsync(cancellationToken))
            {
                var locationCode = activeClientsReader.IsDBNull(1) ? string.Empty : activeClientsReader.GetString(1);
                if (!string.Equals(locationCode, requestedClientCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                activeClient = new ActiveClientRow(
                    activeClientsReader.GetInt32(0),
                    locationCode,
                    activeClientsReader.IsDBNull(2) ? string.Empty : activeClientsReader.GetString(2));
                break;
            }

            await activeClientsReader.CloseAsync();
        }

        if (activeClient is null)
        {
            return null;
        }

        await using var metadataCommand = new SqlCommand(
            """
            SELECT TOP 1
                   ISNULL(c.LocationTopFilter, '') AS LocationTopFilter,
                   ISNULL(c.ClientCode, '') AS ClientCode,
                   ISNULL(c.ClientName, '') AS ClientName,
                   ISNULL(d.ClientCode, '') AS DirectoryClientCode,
                   ISNULL(d.Client_Name, '') AS DirectoryClientName,
                   ISNULL(d.Location_Name, '') AS DirectoryLocationName
            FROM dbo.tbl_Directory d
            LEFT JOIN dbo.vw_Clients c ON c.ClientID = d.DirID
            WHERE d.DirID = @DirID;
            """,
            connection);

        metadataCommand.Parameters.Add(new SqlParameter("@DirID", SqlDbType.Int) { Value = activeClient.DirId });

        await using var metadataReader = await metadataCommand.ExecuteReaderAsync(cancellationToken);

        var locationTopFilter = string.Empty;
        var resolvedClientCode = activeClient.LocationCode;
        var resolvedClientName = activeClient.LocationName;

        if (await metadataReader.ReadAsync(cancellationToken))
        {
            locationTopFilter = metadataReader.GetString(0);

            var clientCodeFromView = metadataReader.GetString(1);
            var clientNameFromView = metadataReader.GetString(2);
            var clientCodeFromDirectory = metadataReader.GetString(3);
            var clientNameFromDirectory = metadataReader.GetString(4);
            var locationNameFromDirectory = metadataReader.GetString(5);

            if (!string.IsNullOrWhiteSpace(clientCodeFromView))
            {
                resolvedClientCode = clientCodeFromView;
            }
            else if (!string.IsNullOrWhiteSpace(clientCodeFromDirectory))
            {
                resolvedClientCode = clientCodeFromDirectory;
            }

            if (!string.IsNullOrWhiteSpace(clientNameFromView))
            {
                resolvedClientName = clientNameFromView;
            }
            else if (!string.IsNullOrWhiteSpace(clientNameFromDirectory))
            {
                resolvedClientName = clientNameFromDirectory;
            }
            else if (!string.IsNullOrWhiteSpace(locationNameFromDirectory))
            {
                resolvedClientName = locationNameFromDirectory;
            }
        }

        await metadataReader.CloseAsync();

        if (string.IsNullOrWhiteSpace(locationTopFilter))
        {
            locationTopFilter = $"Tech.Clients.{resolvedClientCode}";
        }

        return new ClientContext(
            activeClient.DirId,
            activeClient.DirId,
            resolvedClientCode,
            resolvedClientName,
            locationTopFilter);
    }

    private static async Task ApplyTicketDefaultsAsync(SqlConnection connection, int ticketId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.tbl_Tickets
            SET ParentID = @ParentID,
                AssetID = @AssetID,
                Estimated_Hours = @EstimatedHours,
                Description_RTF = @DescriptionRtf,
                Internal_Notes = @InternalNotes,
                DayID = @DayID,
                RecurringType = @RecurringType,
                RecurringOption = @RecurringOption
            WHERE TicketID = @TicketID;
            """,
            connection);

        command.Parameters.Add(new SqlParameter("@ParentID", SqlDbType.Int) { Value = DefaultParentId });
        command.Parameters.Add(new SqlParameter("@AssetID", SqlDbType.Int) { Value = DefaultAssetId });
        command.Parameters.Add(new SqlParameter("@EstimatedHours", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = DefaultEstimatedHours });
        command.Parameters.Add(new SqlParameter("@DescriptionRtf", SqlDbType.NVarChar, 2000) { Value = DefaultEmptyText });
        command.Parameters.Add(new SqlParameter("@InternalNotes", SqlDbType.NVarChar, 2000) { Value = DefaultEmptyText });
        command.Parameters.Add(new SqlParameter("@DayID", SqlDbType.Int) { Value = DefaultDayId });
        command.Parameters.Add(new SqlParameter("@RecurringType", SqlDbType.NVarChar, 50) { Value = DefaultRecurringType });
        command.Parameters.Add(new SqlParameter("@RecurringOption", SqlDbType.Int) { Value = DefaultRecurringOption });
        command.Parameters.Add(new SqlParameter("@TicketID", SqlDbType.Int) { Value = ticketId });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DirectoryUser?> GetActiveUserAsync(SqlConnection connection, string username, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 DirID,
                         ISNULL(ParentID, 0) AS ParentID,
                         ISNULL(LocationTopFilter, '') AS LocationTopFilter
            FROM dbo.vw_Users
            WHERE Username = @Username
              AND IsActive = 1
            ORDER BY DirID DESC;
            """,
            connection);

        command.Parameters.Add(new SqlParameter("@Username", SqlDbType.NVarChar, 100) { Value = username.Trim() });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) is false)
        {
            return null;
        }

        var user = new DirectoryUser(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2));

        await reader.CloseAsync();
        return user;
    }

    private static async Task<PodSelection?> ResolvePodAsync(
        SqlConnection connection,
        string pod,
        DirectoryUser assignedUser,
        string roleType,
        string assignedUserName,
        CancellationToken cancellationToken)
    {
        var candidates = new List<PodCandidate>();
        await using (var command = new SqlCommand(
            """
            SELECT ID,
                   title
            FROM dbo.vw_POD_DSB_Ticket_Filter
            WHERE IsPod = 1
              AND (title = @Pod
                   OR title LIKE @PodPrefix
                   OR REPLACE(title, ' ', '') LIKE @PodCompactPrefix)
            ORDER BY ID;
            """,
            connection))
        {
            command.Parameters.Add(new SqlParameter("@Pod", SqlDbType.NVarChar, 100) { Value = pod.Trim() });
            command.Parameters.Add(new SqlParameter("@PodPrefix", SqlDbType.NVarChar, 110) { Value = pod.Trim() + "%" });
            command.Parameters.Add(new SqlParameter("@PodCompactPrefix", SqlDbType.NVarChar, 110) { Value = pod.Replace(" ", string.Empty).Trim() + "%" });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new PodCandidate(reader.GetInt32(0), reader.GetString(1)));
            }

            await reader.CloseAsync();
        }

        PodCandidate? selected = null;
        if (candidates.Count > 0)
        {
            selected = candidates.FirstOrDefault(item => item.Id == assignedUser.ParentId);

            if (selected is null)
            {
                var prefersDevelopment = roleType.Contains("development", StringComparison.OrdinalIgnoreCase);
                var preferredSegment = prefersDevelopment ? "PR1" : "TS1";

                selected = candidates.FirstOrDefault(item => item.Title.Contains(preferredSegment, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.First();
            }
        }

        var locationTopFilter = string.Empty;
        var selectedPodId = selected?.Id ?? assignedUser.ParentId;
        if (selectedPodId > 0)
        {
            await using var locationCommand = new SqlCommand(
                """
                SELECT TOP 1 ISNULL(LocationTopFilter, '') AS LocationTopFilter
                FROM dbo.vw_Users
                WHERE ParentID = @ParentID
                  AND IsActive = 1
                  AND ISNULL(LocationTopFilter, '') <> ''
                ORDER BY CASE WHEN Username = @AssignedUser THEN 0 ELSE 1 END,
                         DirID DESC;
                """,
                connection);

            locationCommand.Parameters.Add(new SqlParameter("@ParentID", SqlDbType.Int) { Value = selectedPodId });
            locationCommand.Parameters.Add(new SqlParameter("@AssignedUser", SqlDbType.NVarChar, 100) { Value = assignedUserName.Trim() });

            var result = await locationCommand.ExecuteScalarAsync(cancellationToken);
            locationTopFilter = result?.ToString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(locationTopFilter))
        {
            locationTopFilter = assignedUser.LocationTopFilter;
        }

        if (selectedPodId <= 0 && string.IsNullOrWhiteSpace(locationTopFilter))
        {
            return null;
        }

        return new PodSelection(selectedPodId, locationTopFilter);
    }

    private static async Task<LookupValue?> ResolveRoleTypeAsync(SqlConnection connection, string roleType, string pod, CancellationToken cancellationToken)
    {
        await using var exactCommand = new SqlCommand(
            """
            SELECT TOP 1 ID,
                         Description
            FROM dbo.vw_LookupV_Ticket_RoleType
            WHERE Description = @Description
            ORDER BY ID;
            """,
            connection);

        exactCommand.Parameters.Add(new SqlParameter("@Description", SqlDbType.NVarChar, 200) { Value = roleType.Trim() });

        await using var exactReader = await exactCommand.ExecuteReaderAsync(cancellationToken);
        if (await exactReader.ReadAsync(cancellationToken))
        {
            var exact = new LookupValue(exactReader.GetInt32(0), exactReader.GetString(1));
            await exactReader.CloseAsync();
            return exact;
        }

        await exactReader.CloseAsync();

        var prefersOffshore = pod.Contains("CHD", StringComparison.OrdinalIgnoreCase);
        await using var fallbackCommand = new SqlCommand(
            """
            SELECT ID,
                   Description
            FROM dbo.vw_LookupV_Ticket_RoleType
            WHERE Description LIKE @Description
            ORDER BY CASE
                         WHEN @PrefersOffshore = 1 AND Description LIKE 'Off-Shore%' THEN 0
                         WHEN @PrefersOffshore = 0 AND Description LIKE 'Onsite-%' THEN 0
                         ELSE 1
                     END,
                     ID;
            """,
            connection);

        fallbackCommand.Parameters.Add(new SqlParameter("@Description", SqlDbType.NVarChar, 200) { Value = "%" + roleType.Trim() + "%" });
        fallbackCommand.Parameters.Add(new SqlParameter("@PrefersOffshore", SqlDbType.Bit) { Value = prefersOffshore });

        await using var fallbackReader = await fallbackCommand.ExecuteReaderAsync(cancellationToken);
        if (await fallbackReader.ReadAsync(cancellationToken) is false)
        {
            return null;
        }

        var fallback = new LookupValue(fallbackReader.GetInt32(0), fallbackReader.GetString(1));
        await fallbackReader.CloseAsync();
        return fallback;
    }

    private static async Task<int> GetActiveContractIdAsync(SqlConnection connection, int clientId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT dbo.fn_int_GetActiveContractForLocation(@ClientID);", connection);
        command.Parameters.Add(new SqlParameter("@ClientID", SqlDbType.Int) { Value = clientId });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull ? 0 : Convert.ToInt32(result);
    }

    private static async Task<int> GetLookupIdAsync(SqlConnection connection, string viewName, string description, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"""
             SELECT TOP 1 ID
             FROM {viewName}
             WHERE Description = @Description;
             """,
            connection);

        command.Parameters.Add(new SqlParameter("@Description", SqlDbType.NVarChar, 200) { Value = description });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            throw new InvalidOperationException($"Lookup value '{description}' was not found in '{viewName}'.");
        }

        return Convert.ToInt32(result);
    }

    private static async Task<LookupValue?> ResolveLookupValueAsync(SqlConnection connection, string viewName, string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        await using var command = new SqlCommand(
            $"""
             SELECT TOP 1 ID,
                          Description
             FROM {viewName}
             WHERE Description = @Value
                OR CONVERT(nvarchar(50), ID) = @Value
             ORDER BY CASE WHEN Description = @Value THEN 0 ELSE 1 END,
                      ID;
             """,
            connection);

        command.Parameters.Add(new SqlParameter("@Value", SqlDbType.NVarChar, 200) { Value = value.Trim() });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) is false)
        {
            await reader.CloseAsync();
            return null;
        }

        var lookup = new LookupValue(reader.GetInt32(0), reader.GetString(1));
        await reader.CloseAsync();
        return lookup;
    }

    private static async Task<List<string>> GetLookupDescriptionsAsync(SqlConnection connection, string viewName, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"""
             SELECT Description
             FROM {viewName}
             WHERE NULLIF(LTRIM(RTRIM(ISNULL(Description, ''))), '') IS NOT NULL
             ORDER BY Description;
             """,
            connection);

        var descriptions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            descriptions.Add(reader.GetString(0).Trim());
        }

        await reader.CloseAsync();
        return descriptions;
    }

    private static async Task<TicketSummary?> GetTicketSummaryAsync(SqlConnection connection, int ticketId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 TicketID,
                         t.Title,
                         ISNULL(t.CreatedBy, '') AS CreatedBy,
                         t.ClientID,
                         CASE
                             WHEN NULLIF(LTRIM(RTRIM(ISNULL(client.ClientCode, ''))), '') IS NOT NULL THEN LTRIM(RTRIM(client.ClientCode))
                             WHEN NULLIF(LTRIM(RTRIM(ISNULL(clientDir.ClientCode, ''))), '') IS NOT NULL THEN LTRIM(RTRIM(clientDir.ClientCode))
                             WHEN ISNULL(t.LocationTopFilter, '') LIKE 'Tech.Clients.%'
                                  AND CHARINDEX('.', ISNULL(t.LocationTopFilter, ''), LEN('Tech.Clients.') + 1) = 0
                                 THEN SUBSTRING(t.LocationTopFilter, LEN('Tech.Clients.') + 1, LEN(t.LocationTopFilter))
                             WHEN ISNULL(t.LocationTopFilter, '') LIKE 'Tech.Clients.%'
                                 THEN SUBSTRING(
                                     t.LocationTopFilter,
                                     LEN('Tech.Clients.') + 1,
                                     CHARINDEX('.', t.LocationTopFilter, LEN('Tech.Clients.') + 1) - (LEN('Tech.Clients.') + 1))
                             ELSE ''
                         END AS ClientCode,
                         CASE
                             WHEN NULLIF(LTRIM(RTRIM(ISNULL(client.ClientName, ''))), '') IS NOT NULL THEN LTRIM(RTRIM(client.ClientName))
                             WHEN NULLIF(LTRIM(RTRIM(ISNULL(clientDir.Client_Name, ''))), '') IS NOT NULL THEN LTRIM(RTRIM(clientDir.Client_Name))
                             WHEN NULLIF(LTRIM(RTRIM(ISNULL(clientDir.Location_Name, ''))), '') IS NOT NULL THEN LTRIM(RTRIM(clientDir.Location_Name))
                             ELSE ''
                         END AS ClientName,
                         t.AssignTo_DirID,
                         t.Requestor_DirID,
                         t.RoleType,
                         ISNULL(t.RoleTypeTxt, '') AS RoleTypeTxt,
                         ISNULL(req.Location_Name, '') AS LocationName,
                         ISNULL(t.LocationTopFilter, '') AS LocationTopFilter,
                         ISNULL(t.ParentID, 0) AS ParentID,
                         ISNULL(t.AssetID, 0) AS AssetID,
                         ISNULL(t.Estimated_Hours, 0.00) AS Estimated_Hours,
                         ISNULL(t.Description_RTF, '') AS Description_RTF,
                         ISNULL(t.Internal_Notes, '') AS Internal_Notes,
                         ISNULL(t.DayID, 0) AS DayID,
                         CASE
                             WHEN NULLIF(LTRIM(RTRIM(ISNULL(t.RecurringType, ''))), '') IS NULL THEN '0'
                             ELSE LTRIM(RTRIM(t.RecurringType))
                         END AS RecurringType,
                         ISNULL(t.RecurringOption, 0) AS RecurringOption,
                         ISNULL(t.StatusTxt, '') AS StatusTxt,
                         ISNULL(t.PriorityTxt, '') AS PriorityTxt,
                         ISNULL(t.RequestType, '') AS RequestType,
                         t.CreateDate
            FROM dbo.vw_Tkt t
            LEFT JOIN dbo.tbl_Directory req ON req.DirID = t.Requestor_DirID
            LEFT JOIN dbo.vw_Clients client ON client.ClientID = t.ClientID
            LEFT JOIN dbo.tbl_Directory clientDir ON clientDir.DirID = t.ClientID
            WHERE t.TicketID = @TicketID;
            """,
            connection);

        command.Parameters.Add(new SqlParameter("@TicketID", SqlDbType.Int) { Value = ticketId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) is false)
        {
            return null;
        }

        var summary = new TicketSummary(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8)),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetDecimal(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetInt32(17),
            reader.GetString(18),
            reader.GetInt32(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetDateTime(23));

        await reader.CloseAsync();
        return summary;
    }

    private static async Task<TicketUpdateSnapshot?> GetTicketForUpdateAsync(SqlConnection connection, int ticketId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("dbo.sp_Get_Ticket", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 180
        };

        command.Parameters.Add(new SqlParameter("@TicketID", SqlDbType.Int) { Value = ticketId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) is false)
        {
            return null;
        }

        var snapshot = new TicketUpdateSnapshot(
            ticketId,
            GetString(reader, "Requestor_DirID"),
            GetString(reader, "AssignTo_DirID"),
            GetString(reader, "CreatedBy"),
            GetString(reader, "LocationTopFilter"),
            GetString(reader, "Title"),
            GetString(reader, "Description"),
            GetString(reader, "Priority"),
            GetString(reader, "Status"),
            GetDecimal(reader, "Estimated_Hours"),
            GetDateTime(reader, "RequestDate") ?? GetDateTime(reader, "CreateDate") ?? DateTime.UtcNow,
            GetString(reader, "Internal_Notes"),
            GetString(reader, "Description_RTF"));

        await reader.CloseAsync();
        return snapshot;
    }

    private static async Task<TicketTimeEntryContext?> GetTicketTimeEntryContextAsync(SqlConnection connection, int ticketId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP 1
                   t.TicketID,
                   ISNULL(t.Status, 0) AS StatusId,
                   ISNULL(statusLookup.Description, '') AS StatusTxt,
                   ISNULL(t.RoleType, 0) AS RoleTypeId,
                   ISNULL(roleLookup.Description, '') AS RoleTypeTxt
            FROM dbo.tbl_Tickets t
            LEFT JOIN dbo.vw_LookupV_Ticket_Status statusLookup ON statusLookup.ID = t.Status
            LEFT JOIN dbo.vw_LookupV_Ticket_RoleType roleLookup ON roleLookup.ID = t.RoleType
            WHERE t.TicketID = @TicketID;
            """,
            connection)
        {
            CommandTimeout = 180
        };

        command.Parameters.Add(new SqlParameter("@TicketID", SqlDbType.Int) { Value = ticketId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) is false)
        {
            await reader.CloseAsync();
            return null;
        }

        var context = new TicketTimeEntryContext(
            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.GetString(2),
            Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
            reader.GetString(4));

        await reader.CloseAsync();
        return context;
    }

    private static TicketUpdateSnapshot Merge(
        TicketUpdateSnapshot current,
        UpdateTicketRequest request,
        DirectoryUser? requestedAssignToUser,
        string priorityValue,
        string statusValue)
    {
        return current with
        {
            Requestor = request.Requestor_DirID?.ToString() ?? FirstNonEmpty(request.Requestor, current.Requestor),
            AssignToUsername = requestedAssignToUser?.DirId.ToString() ?? current.AssignToUsername,
            Title = FirstNonEmpty(request.Title, current.Title),
            Description = FirstNonEmpty(request.Description, request.Desc, current.Description),
            Priority = priorityValue,
            Status = statusValue,
            Estimated_Hours = request.Estimated_Hours ?? current.Estimated_Hours,
            RequestDate = request.RequestDate ?? current.RequestDate,
            Internal_Notes = FirstNonEmpty(request.Internal_Notes, request.InternalNotes, current.Internal_Notes),
            Description_RTF = FirstNonEmpty(request.Description_RTF, current.Description_RTF)
        };
    }

    private static async Task ExecuteUpdateTicketAsync(SqlConnection connection, TicketUpdateSnapshot ticket, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("dbo.sp_Ticket_Upd", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 180
        };

        command.Parameters.Add(new SqlParameter("@TicketID", SqlDbType.Int) { Value = ticket.TicketID });
        command.Parameters.Add(new SqlParameter("@Requestor", SqlDbType.NVarChar, 100) { Value = ToDbString(ticket.Requestor) });
        command.Parameters.Add(new SqlParameter("@AssignToUsername", SqlDbType.NVarChar, 100) { Value = ToDbString(ticket.AssignToUsername) });
        command.Parameters.Add(new SqlParameter("@Title", SqlDbType.NVarChar, 400) { Value = ToDbString(ticket.Title) });
        command.Parameters.Add(new SqlParameter("@Description", SqlDbType.Text) { Value = ToDbString(ticket.Description) });
        command.Parameters.Add(new SqlParameter("@Priority", SqlDbType.NVarChar, 100) { Value = ToDbString(ticket.Priority) });
        command.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 100) { Value = ToDbString(ticket.Status) });
        command.Parameters.Add(new SqlParameter("@Estimated_Hours", SqlDbType.Decimal) { Precision = 10, Scale = 2, Value = ticket.Estimated_Hours });
        command.Parameters.Add(new SqlParameter("@RequestDate", SqlDbType.DateTime) { Value = ticket.RequestDate });
        command.Parameters.Add(new SqlParameter("@Internal_Notes", SqlDbType.Text) { Value = ToDbString(ticket.Internal_Notes) });
        command.Parameters.Add(new SqlParameter("@Description_RTF", SqlDbType.Text) { Value = ToDbString(ticket.Description_RTF) });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static XDocument BuildTicketTimeEntryXml(
        int ticketId,
        DateTime startDateTime,
        DateTime endDateTime,
        decimal driveTime,
        decimal hoursWorked,
        string notes,
        string createdBy,
        string workType,
        int roleTypeId,
        string internalNotes,
        int assignToUserId,
        int statusId)
    {
        var root = new XElement("TicketEntries",
            new XElement("TicketEntryID", 0),
            new XElement("TicketID", ticketId),
            new XElement("StartDateTime", startDateTime.ToString("s", CultureInfo.InvariantCulture)),
            new XElement("EndDateTime", endDateTime.ToString("s", CultureInfo.InvariantCulture)),
            new XElement("DriveTime", driveTime.ToString("0.00", CultureInfo.InvariantCulture)),
            new XElement("HoursWorked", hoursWorked.ToString("0.00", CultureInfo.InvariantCulture)),
            new XElement("Notes", notes.Trim()),
            new XElement("CreatedBy", createdBy),
            new XElement("WorkType", workType),
            new XElement("RoleType", roleTypeId),
            new XElement("AssignToUserID", assignToUserId),
            new XElement("Status", statusId));

        if (string.IsNullOrWhiteSpace(internalNotes) is false)
        {
            root.Add(new XElement("InternalNotes", internalNotes.Trim()));
        }

        return new XDocument(root);
    }

    private static async Task<int> ExecuteAddTicketTimeEntryAsync(SqlConnection connection, XDocument xmlInput, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("dbo.stp_Save_Ticket_Time_Entry_V1", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 180
        };

        command.Parameters.Add(new SqlParameter("@XML_TimeEntry", SqlDbType.Xml)
        {
            Value = xmlInput.ToString(SaveOptions.DisableFormatting)
        });

        var returnValue = new SqlParameter("@ReturnValue", SqlDbType.Int)
        {
            Direction = ParameterDirection.ReturnValue
        };
        command.Parameters.Add(returnValue);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return returnValue.Value is DBNull or null ? 0 : Convert.ToInt32(returnValue.Value, CultureInfo.InvariantCulture);
    }

    private static async Task SyncTicketEntryNotesAsync(
        SqlConnection connection,
        int ticketId,
        string notes,
        string actorUsername,
        int assignToDirId,
        string locationTopFilter,
        CancellationToken cancellationToken)
    {
        await using (var updateCommand = new SqlCommand(
            """
            ;WITH latest AS
            (
                SELECT TOP 1 TicketEntryID
                FROM dbo.tbl_TicketEntries
                WHERE TicketID = @TicketID
                  AND ISNULL(IsDeleted, 0) = 0
                ORDER BY TicketEntryID DESC
            )
            UPDATE entryRow
            SET Notes = @Notes,
                ModifiedBy = @ModifiedBy,
                LastModifiedTime = @LastModifiedTime
            FROM dbo.tbl_TicketEntries entryRow
            INNER JOIN latest ON latest.TicketEntryID = entryRow.TicketEntryID;

            SELECT @@ROWCOUNT;
            """,
            connection)
        {
            CommandTimeout = 180
        })
        {
            var modifiedAt = DateTime.Now;

            updateCommand.Parameters.Add(new SqlParameter("@TicketID", SqlDbType.Int) { Value = ticketId });
            updateCommand.Parameters.Add(new SqlParameter("@Notes", SqlDbType.NVarChar, -1) { Value = notes.Trim() });
            updateCommand.Parameters.Add(new SqlParameter("@ModifiedBy", SqlDbType.VarChar, 50) { Value = ToDbString(actorUsername) });
            updateCommand.Parameters.Add(new SqlParameter("@LastModifiedTime", SqlDbType.DateTime) { Value = modifiedAt });

            var rowsUpdated = Convert.ToInt32(await updateCommand.ExecuteScalarAsync(cancellationToken));
            if (rowsUpdated > 0)
            {
                return;
            }
        }

        var utcNow = DateTime.UtcNow;
        var localNow = DateTime.Now;
        var offsetMinutes = Convert.ToInt32(TimeZoneInfo.Local.GetUtcOffset(localNow).TotalMinutes);

        await using var insertCommand = new SqlCommand(
            """
            INSERT INTO dbo.tbl_TicketEntries
            (
                TicketID,
                StartDateTime,
                EndDateTime,
                DriveTime,
                Notes,
                CreatedBy,
                CreateDate,
                ModifiedBy,
                LastModifiedTime,
                AssignToUserID,
                IsActive,
                TimeZoneOffset,
                LocationTopFilter,
                StartTimeUTC,
                EndTimeUTC
            )
            VALUES
            (
                @TicketID,
                @StartDateTime,
                @EndDateTime,
                @DriveTime,
                @Notes,
                @CreatedBy,
                @CreateDate,
                @ModifiedBy,
                @LastModifiedTime,
                @AssignToUserID,
                @IsActive,
                @TimeZoneOffset,
                @LocationTopFilter,
                @StartTimeUTC,
                @EndTimeUTC
            );
            """,
            connection)
        {
            CommandTimeout = 180
        };

        insertCommand.Parameters.Add(new SqlParameter("@TicketID", SqlDbType.Int) { Value = ticketId });
        insertCommand.Parameters.Add(new SqlParameter("@StartDateTime", SqlDbType.DateTime) { Value = localNow });
        insertCommand.Parameters.Add(new SqlParameter("@EndDateTime", SqlDbType.DateTime) { Value = localNow });
        insertCommand.Parameters.Add(new SqlParameter("@DriveTime", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = 0.00m });
        insertCommand.Parameters.Add(new SqlParameter("@Notes", SqlDbType.NVarChar, -1) { Value = notes.Trim() });
        insertCommand.Parameters.Add(new SqlParameter("@CreatedBy", SqlDbType.VarChar, 50) { Value = ToDbString(actorUsername) });
        insertCommand.Parameters.Add(new SqlParameter("@CreateDate", SqlDbType.DateTime) { Value = localNow });
        insertCommand.Parameters.Add(new SqlParameter("@ModifiedBy", SqlDbType.VarChar, 50) { Value = ToDbString(actorUsername) });
        insertCommand.Parameters.Add(new SqlParameter("@LastModifiedTime", SqlDbType.DateTime) { Value = localNow });
        insertCommand.Parameters.Add(new SqlParameter("@AssignToUserID", SqlDbType.Int) { Value = assignToDirId > 0 ? assignToDirId : DBNull.Value });
        insertCommand.Parameters.Add(new SqlParameter("@IsActive", SqlDbType.Bit) { Value = true });
        insertCommand.Parameters.Add(new SqlParameter("@TimeZoneOffset", SqlDbType.Int) { Value = offsetMinutes });
        insertCommand.Parameters.Add(new SqlParameter("@LocationTopFilter", SqlDbType.NVarChar, 4000) { Value = ToDbString(locationTopFilter) });
        insertCommand.Parameters.Add(new SqlParameter("@StartTimeUTC", SqlDbType.DateTime) { Value = utcNow });
        insertCommand.Parameters.Add(new SqlParameter("@EndTimeUTC", SqlDbType.DateTime) { Value = utcNow });

        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object ToDbString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => string.IsNullOrWhiteSpace(value) is false)?.Trim() ?? string.Empty;

    private static string NormalizeTimeEntryUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return string.Empty;
        }

        return string.Equals(username.Trim(), DefaultTimeEntryActorAlias, StringComparison.OrdinalIgnoreCase)
            ? DefaultTimeEntryActorUsername
            : username.Trim();
    }

    private static string NormalizeTimeEntryWorkType(string? workType, decimal driveTime)
    {
        if (string.IsNullOrWhiteSpace(workType))
        {
            return driveTime > 0 ? "Onsite" : "Offsite";
        }

        return workType.Trim().ToLowerInvariant() switch
        {
            "remote" => "Offsite",
            "offsite" => "Offsite",
            "onsite" => "Onsite",
            "assign" => "Assign",
            _ => workType.Trim()
        };
    }

    private static decimal CalculateTimeEntryHours(DateTime startDateTime, DateTime endDateTime, decimal driveTime)
    {
        var totalHours = ((decimal)(endDateTime - startDateTime).TotalMinutes / 60.00m) - driveTime;
        return Math.Round(totalHours, 2, MidpointRounding.AwayFromZero);
    }

    private static string GetString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty;
    }

    private static decimal GetDecimal(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? 0.00m : Convert.ToDecimal(reader.GetValue(ordinal));
    }

    private static DateTime? GetDateTime(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDateTime(reader.GetValue(ordinal));
    }

    public sealed class CreateTicketRequest
    {
        public string? POD { get; set; }
        public string? Title { get; set; }
        public string? Desc { get; set; }
        public string? CreatedBy { get; set; }
        public string? ClientCode { get; set; }
        public int ClientID { get; set; }
        public string? AssignedUser { get; set; }
        public string? RoleType { get; set; }
        public string? RoleTypeTxt { get; set; }
    }

    public sealed class UpdateTicketRequest
    {
        public string? Requestor { get; set; }
        public int? Requestor_DirID { get; set; }
        public string? AssignToUsername { get; set; }
        public string? AssignedUser { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Desc { get; set; }
        public string? Priority { get; set; }
        public string? Status { get; set; }
        public decimal? Estimated_Hours { get; set; }
        public DateTime? RequestDate { get; set; }
        public string? Internal_Notes { get; set; }
        public string? InternalNotes { get; set; }
        public string? Description_RTF { get; set; }
    }

    public sealed class AddTicketTimeEntryRequest
    {
        public DateTime? StartDateTime { get; set; }
        public DateTime? EndDateTime { get; set; }
        public decimal? DriveTime { get; set; }
        public decimal? HoursWorked { get; set; }
        public string? Notes { get; set; }
        public string? Internal_Notes { get; set; }
        public string? InternalNotes { get; set; }
        public string? Status { get; set; }
        public string? WorkType { get; set; }
        public string? AssignToUsername { get; set; }
        public string? AssignedUser { get; set; }
        public string? CreatedBy { get; set; }
        public string? Username { get; set; }
    }

    private sealed record DirectoryUser(int DirId, int ParentId, string LocationTopFilter);

    private sealed record PodCandidate(int Id, string Title);

    private sealed record PodSelection(int Id, string LocationTopFilter);

    private sealed record LookupValue(int Id, string Description);

    private sealed record ActiveClientRow(int DirId, string LocationCode, string LocationName);

    private sealed record ClientContext(int RequestorDirId, int ClientId, string ClientCode, string ClientName, string LocationTopFilter);

    private sealed record TicketTimeEntryContext(
        int TicketID,
        int StatusId,
        string StatusText,
        int RoleTypeId,
        string RoleTypeText);

    private sealed record TicketEntryListItem(
        string TicketID,
        string Title,
        string Description,
        DateTime? StartDateTime,
        DateTime? EndDateTime,
        string Notes,
        decimal Duration,
        string Employee,
        string Month);

    private sealed record TicketSummary(
        int TicketID,
        string Title,
        string CreatedBy,
        int ClientID,
        string ClientCode,
        string ClientName,
        int AssignTo_DirID,
        int Requestor_DirID,
        int RoleType,
        string RoleTypeTxt,
        string LocationName,
        string LocationTopFilter,
        int ParentID,
        int AssetID,
        decimal Estimated_Hours,
        string Description_RTF,
        string Internal_Notes,
        int DayID,
        string RecurringType,
        int RecurringOption,
        string StatusTxt,
        string PriorityTxt,
        string RequestType,
        DateTime? CreateDate);

    private sealed record TicketUpdateSnapshot(
        int TicketID,
        string Requestor,
        string AssignToUsername,
        string CreatedBy,
        string LocationTopFilter,
        string Title,
        string Description,
        string Priority,
        string Status,
        decimal Estimated_Hours,
        DateTime RequestDate,
        string Internal_Notes,
        string Description_RTF);

    private sealed record TicketTimeEntryResponse(
        int TicketEntryID,
        int TicketID,
        string CreatedBy,
        int AssignToUserID,
        string AssignToUsername,
        DateTime StartDateTime,
        DateTime EndDateTime,
        decimal DriveTime,
        decimal HoursWorked,
        string Notes,
        string InternalNotes,
        string WorkType,
        int RoleType,
        string RoleTypeTxt,
        int Status,
        string StatusTxt,
        bool CreatedInternalNoteEntry);
}
