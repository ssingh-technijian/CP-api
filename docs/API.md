# API

The API exposes Swagger and redirects `/` to `/swagger`.

Confirmed endpoints include:

- `GET /api/system/health`
- `GET /api/catalog/databases`
- `GET /api/catalog/objects?kind=stored-procedure`
- `POST /api/modules/{module}/stored-procedures/{databaseAlias}/{schema}/{name}/execute`
- `GET /api/tickets/entries?clientCode={clientCode}`
- `POST /api/tickets`
- `POST /api/tickets/create`
- `PUT /api/tickets/{ticketId}`
- `PATCH /api/tickets/{ticketId}`
- `POST /api/tickets/{ticketId}/time-entries`

## Ticket Update

`PUT /api/tickets/{ticketId}` and `PATCH /api/tickets/{ticketId}` update one existing ticket.

Request fields are optional; omitted fields are loaded from the current ticket and preserved before calling `dbo.sp_Ticket_Upd`.
String fields with `null`, empty, or whitespace-only values are also ignored and do not update that property.
For `Status` and `Priority`, API text values are resolved to the legacy lookup IDs expected by the underlying ticket update procedure.

Supported request fields:

- `Requestor` or `Requestor_DirID`
- `AssignToUsername` or `AssignedUser`
- `Title`
- `Description` or `Desc`
- `Priority`
- `Status`
- `Estimated_Hours`
- `RequestDate`
- `Internal_Notes` or `InternalNotes`
- `Description_RTF`

Internal note behavior:

- A supplied non-empty `Internal_Notes` or `InternalNotes` value still updates `dbo.tbl_Tickets.Internal_Notes`.
- If the ticket's effective status resolves to `Internal Ticket`, the same non-empty value is also synced to the latest `dbo.tbl_TicketEntries.Notes` row for that ticket.
- If the ticket's effective status resolves to `Internal Ticket` and no entry row exists yet, the API inserts a minimal active `dbo.tbl_TicketEntries` row and stores the value in `Notes`.
- If the ticket status is not `Internal Ticket`, the API does not update `dbo.tbl_TicketEntries.Notes`.
- `null`, empty, and whitespace-only internal note values are ignored and do not change the ticket note or ticket entry note.

Validation:

- `ticketId` must be greater than zero.
- Negative `Estimated_Hours` is rejected.
- A supplied non-empty `Status` must exist in `dbo.vw_LookupV_Ticket_Status`; invalid values return `400` with valid statuses as a comma-separated list.
- A supplied non-empty `Priority` must exist in `dbo.vw_LookupV_Ticket_Priority`; invalid values return `400` with valid priorities as a comma-separated list.
- A supplied non-empty `AssignToUsername` or `AssignedUser` must match an active `dbo.vw_Users.Username`; invalid values return `400` asking for a valid active username. Valid usernames are resolved to `DirID` before calling `dbo.sp_Ticket_Upd`.
- Unknown tickets return `404`.

## Ticket Time Entry

`POST /api/tickets/{ticketId}/time-entries` adds a new ticket time entry through the legacy time-entry save procedure.

Supported request fields:

- `StartDateTime`
- `EndDateTime`
- `DriveTime`
- `HoursWorked`
- `Notes`
- `Internal_Notes` or `InternalNotes`
- `Status`
- `WorkType`
- `AssignToUsername` or `AssignedUser`
- `CreatedBy` or `Username`

Behavior:

- If `CreatedBy` is omitted, the API logs the entry under the AI service user alias `Jian`, which resolves to the active directory username `jian@technijian.com`.
- A request value of `CreatedBy: "Jian"` or `AssignToUsername: "Jian"` is also normalized to `jian@technijian.com`.
- If `AssignToUsername` is omitted, the saved time entry is assigned to the same user as `CreatedBy`.
- If `Status` is omitted, the API preserves the ticket's current status ID.
- The API always uses the ticket's saved `RoleType` from `dbo.tbl_Tickets`; callers do not send `RoleType` or `RoleTypeTxt`.
- If `HoursWorked` is omitted, the API calculates it from `EndDateTime - StartDateTime - DriveTime`.
- If `WorkType` is omitted, the API defaults to `Offsite` when `DriveTime` is zero and `Onsite` when `DriveTime` is greater than zero.
- A non-empty `InternalNotes` value causes the legacy save procedure to create its normal internal-note companion row in `dbo.tbl_TicketEntries`.

Validation:

- `ticketId` must be greater than zero.
- `StartDateTime`, `EndDateTime`, and `Notes` are required.
- `EndDateTime` must be greater than `StartDateTime`.
- `DriveTime` cannot be negative.
- `HoursWorked`, whether supplied or calculated, must be greater than zero.
- A supplied `HoursWorked` cannot exceed the time between `StartDateTime` and `EndDateTime` after subtracting `DriveTime`.
- A supplied non-empty `Status` must exist in `dbo.vw_LookupV_Ticket_Status`; invalid values return `400` with valid statuses as a comma-separated list.
- The ticket must already have a saved `RoleType` in `dbo.tbl_Tickets`; otherwise the request is rejected.
- A supplied non-empty `CreatedBy`, `Username`, `AssignToUsername`, or `AssignedUser` must resolve to an active `dbo.vw_Users.Username` after alias normalization.
- Unknown tickets return `404`.
