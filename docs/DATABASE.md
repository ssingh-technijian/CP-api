# Database

The API reads database connection strings from configuration.

Confirmed aliases:

- `client-portal`

Ticket behavior confirmed in code:

- Ticket creation calls `dbo.stp_xml_Tkt_API_CreateV3`.
- Ticket creation then applies default values directly to `dbo.tbl_Tickets`.
- Ticket update loads the current row with `dbo.sp_Get_Ticket`.
- Ticket update writes through `dbo.sp_Ticket_Upd`.
- Ticket time entry creation writes through `dbo.stp_Save_Ticket_Time_Entry_V1` using `/TicketEntries` XML.
- Ticket time entry status input is resolved to the numeric `dbo.tbl_Tickets.Status` lookup ID before save.
- Ticket time entry role input is always taken from `dbo.tbl_Tickets.RoleType`; the endpoint does not accept `RoleType` or `RoleTypeTxt` from the request body.
- Ticket time entry `CreatedBy` and `AssignToUsername` values resolve against active `dbo.vw_Users` rows; the alias `Jian` maps to `jian@technijian.com`.
- Non-empty ticket update `Internal_Notes` values are synced to `dbo.tbl_TicketEntries.Notes` only when the ticket status resolves to `Internal Ticket`; if no entry exists, the API inserts a minimal active row in `dbo.tbl_TicketEntries`.
- Ticket entries are queried from `dbo.tbl_TicketEntries` joined to `dbo.tbl_Tickets`.
