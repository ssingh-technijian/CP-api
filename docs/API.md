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
