using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CpApis.Mcp.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace CpApis.Mcp.Core.Tools;

public sealed class ClientPortalEmployeeActionOptions { public bool Enabled { get; set; } }

/// <summary>Proposals only. No approval or execution tool is registered.</summary>
[McpServerToolType]
public sealed class ClientPortalEmployeeActionTools(ClientPortalEmployeeActionService actions,
    IOptions<ClientPortalEmployeeActionOptions> options)
{
    private readonly ILogger<ClientPortalEmployeeActionTools> diagnostics = NullLogger<ClientPortalEmployeeActionTools>.Instance;

    // Keep the existing two-argument constructor for compiled consumers and tests.
    [ActivatorUtilitiesConstructor]
    public ClientPortalEmployeeActionTools(ClientPortalEmployeeActionService actions,
        IOptions<ClientPortalEmployeeActionOptions> options, ILogger<ClientPortalEmployeeActionTools> logger)
        : this(actions, options) => diagnostics = logger;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [McpServerTool(Name="client_portal_create_ticket_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose a ticket assigned to your verified native account. First resolve exact clientDirectoryId and roleTypeId with the lookup tools. Supply a new UUID requestId; reuse it only for the identical retry. No business ticket is created until you open the returned review link and approve the exact changes. Email notifications start off; use the native Portal if notification is needed.")]
    public Task<EmployeeToolResult<object>> CreateTicketAsync(Guid requestId, int clientDirectoryId, int roleTypeId,
        string title, string description, int? priorityId=null, CancellationToken cancellationToken=default) =>
        Propose("ticket.create",requestId,new {clientDirectoryId,roleTypeId,title,description,priorityId},cancellationToken);

    [McpServerTool(Name="client_portal_update_ticket_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose a partial ticket edit. Read the exact ticket first; omitted fields are preserved. Supported fields: title, description, priorityId, statusId, estimatedHours. Project/invoice-linked completion is blocked. Returns a human review link; it does not execute the edit.")]
    public Task<EmployeeToolResult<object>> UpdateTicketAsync(Guid requestId,int ticketId,string? title=null,
        string? description=null,int? priorityId=null,int? statusId=null,decimal? estimatedHours=null,CancellationToken cancellationToken=default) =>
        Propose("ticket.edit",requestId,new {ticketId,title,description,priorityId,statusId,estimatedHours},cancellationToken);

    [McpServerTool(Name="client_portal_delete_ticket_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose native deactivation of an exact ticket. Work, financial, child-ticket, project, scheduled and external links are guarded. Requires a separate human review and approval; never report deletion from the proposal alone.")]
    public Task<EmployeeToolResult<object>> DeleteTicketAsync(Guid requestId,int ticketId,CancellationToken cancellationToken=default) => Propose("ticket.delete",requestId,new {ticketId},cancellationToken);

    [McpServerTool(Name="client_portal_create_time_entry_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose your work log on an exact accessible ticket. startLocal/endLocal are portal stored-local YYYY-MM-DDTHH:mm:ss (IST for the native India team, Pacific for native US users). Use exact workTypeId from lookup options. Native10hour new-entry backdate, work/break, duplicate-note, contract-role and payroll/EOD rules apply. Drive hours stay separate; this does not certify billable hours or change ticket status/assignees. Human approval required.")]
    public Task<EmployeeToolResult<object>> CreateTimeAsync(Guid requestId,int ticketId,string startLocal,string endLocal,
        string notes,int workTypeId,decimal driveHours=0,CancellationToken cancellationToken=default) =>
        Propose("time.create",requestId,new {ticketId,startLocal,endLocal,notes,workTypeId,driveHours},cancellationToken);

    [McpServerTool(Name="client_portal_update_time_entry_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose a partial time-entry edit after reading that exact entry. Supported fields: startLocal, endLocal, notes, driveHours. Omitted fields and ticket assignments stay unchanged. Approved, billed, exported, scheduled and closed payroll/EOD work is blocked. Requires human approval.")]
    public Task<EmployeeToolResult<object>> UpdateTimeAsync(Guid requestId,int ticketEntryId,string? startLocal=null,string? endLocal=null,
        string? notes=null,decimal? driveHours=null,CancellationToken cancellationToken=default) =>
        Propose("time.edit",requestId,new {ticketEntryId,startLocal,endLocal,notes,driveHours},cancellationToken);

    [McpServerTool(Name="client_portal_delete_time_entry_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose native deactivation of one exact unapproved, unbilled, unprocessed time entry in an open payroll/EOD month. Approval is a separate authenticated human action. Never report deletion from a proposal.")]
    public Task<EmployeeToolResult<object>> DeleteTimeAsync(Guid requestId,int ticketEntryId,CancellationToken cancellationToken=default) => Propose("time.delete",requestId,new {ticketEntryId},cancellationToken);

    [McpServerTool(Name="client_portal_create_invoice_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose an empty native draft Invoice header for an exact billingDirectoryId, title and optional YYYY-MM-DD dueDate. This does not generate charges, price line items, send an invoice, collect payment, sign or export accounting. Complete commercial lines in the native Portal. Human review/approval required.")]
    public Task<EmployeeToolResult<object>> CreateInvoiceAsync(Guid requestId,int billingDirectoryId,string title,string? dueDate=null,
        CancellationToken cancellationToken=default) => Propose("invoice.create",requestId,new {billingDirectoryId,title,dueDate},cancellationToken);

    [McpServerTool(Name="client_portal_update_invoice_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose a draft invoice title/dueDate edit, preserving omitted fields. Only unsigned, unpaid, unexported, unlinked draft Invoice records qualify. This does not edit commercial lines or change invoice status. Human approval required.")]
    public Task<EmployeeToolResult<object>> UpdateInvoiceAsync(Guid requestId,int invoiceId,string? title=null,string? dueDate=null,
        CancellationToken cancellationToken=default) => Propose("invoice.edit",requestId,new {invoiceId,title,dueDate},cancellationToken);

    [McpServerTool(Name="client_portal_delete_invoice_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose permanent deletion of one unsigned, unpaid, unexported, unlinked draft Invoice and its reviewed draft detail rows. A human must open the review and explicitly approve deletion. No payment, signing or accounting action is available here.")]
    public Task<EmployeeToolResult<object>> DeleteInvoiceAsync(Guid requestId,int invoiceId,CancellationToken cancellationToken=default) => Propose("invoice.delete",requestId,new {invoiceId},cancellationToken);

    [McpServerTool(Name="client_portal_create_contract_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose an unsigned draft contract shell for exact clientDirectoryId, name and contractType from native lookup options. No prices, activation, signing request or legally verified terms are created. Complete commercial terms in the native Portal. Human approval required.")]
    public Task<EmployeeToolResult<object>> CreateContractAsync(Guid requestId,int clientDirectoryId,string name,string contractType,
        CancellationToken cancellationToken=default) => Propose("contract.create",requestId,new {clientDirectoryId,name,contractType},cancellationToken);

    [McpServerTool(Name="client_portal_update_contract_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose renaming an unsigned, unlinked native draft contract. Existing dates, prices, SLA, role/service lines and other fields are preserved. Active/sent/signed/exported or work-linked contracts require the native Portal. Human approval required.")]
    public Task<EmployeeToolResult<object>> UpdateContractAsync(Guid requestId,int contractId,string name,CancellationToken cancellationToken=default) => Propose("contract.edit",requestId,new {contractId,name},cancellationToken);

    [McpServerTool(Name="client_portal_delete_contract_draft", ReadOnly=false, Destructive=false, OpenWorld=false)]
    [Description("Propose native deletion/deactivation of one unsigned, unlinked draft contract and its draft pricing activation flags. Existing business links and signed/active/exported states are blocked. Human approval required.")]
    public Task<EmployeeToolResult<object>> DeleteContractAsync(Guid requestId,int contractId,CancellationToken cancellationToken=default) => Propose("contract.delete",requestId,new {contractId},cancellationToken);

    [McpServerTool(Name="client_portal_get_action_status", ReadOnly=true, Destructive=false, OpenWorld=false)]
    [Description("Read your own proposal status/result by exact actionId. Applied means native execution succeeded; pending means nothing has been changed. Returns no approval token. The assistant cannot approve actions.")]
    public Task<EmployeeToolResult<object>> StatusAsync(Guid actionId,CancellationToken cancellationToken=default) => Safe(async()=>await actions.GetStatusAsync(actionId,cancellationToken),"status",null,actionId);

    private Task<EmployeeToolResult<object>> Propose(string operation,Guid requestId,object payload,CancellationToken ct) =>
        Safe(async()=>await actions.ProposeAsync(operation,requestId,JsonSerializer.SerializeToElement(payload,Json),ct),operation,requestId,null);
    private async Task<EmployeeToolResult<object>> Safe(Func<Task<object>> action,string operation,Guid? requestId,Guid? actionId)
    {
        if (!options.Value.Enabled) return new(false,null,"Employee action proposals are not enabled. Native Portal writes are unchanged.");
        try { return new(true,await action(),null); }
        catch(EmployeeAccessDeniedException) { RecordFailure(operation,requestId,actionId,"permission",403,null);return new(false,null,"Your current native identity, role or permission does not allow this action."); }
        catch(EmployeeActionException ex) { RecordFailure(operation,requestId,actionId,"action-rule",ex.StatusCode,null);return new(false,null,ex.Message); }
        catch(SqlException ex) { RecordFailure(operation,requestId,actionId,"database",null,ex.Number);return new(false,null,"The native Portal action could not be verified. No successful change is confirmed."); }
    }

    private void RecordFailure(string operation,Guid? requestId,Guid? actionId,string category,int? statusCode,int? sqlErrorNumber)
    {
        try
        {
            // Never log the exception/message, SQL, payload, employee claims or review tokens.
            diagnostics.LogWarning(new EventId(6201,"EmployeeMcpActionFailure"),
                "Employee MCP action failure. Operation={Operation} RequestId={RequestId} ActionId={ActionId} Category={Category} StatusCode={StatusCode} SqlErrorNumber={SqlErrorNumber}",
                operation,requestId,actionId,category,statusCode,sqlErrorNumber);
        }
        catch { /* Diagnostic sink failure must not change the action outcome. */ }
    }
}
