# Database

The API reads database connection strings from configuration.

Confirmed aliases:

- `client-portal`

Ticket behavior confirmed in code:

- Ticket creation calls `dbo.stp_xml_Tkt_API_CreateV3`.
- Ticket creation then applies default values directly to `dbo.tbl_Tickets`.
- Ticket update loads the current row with `dbo.sp_Get_Ticket`.
- Ticket update writes through `dbo.sp_Ticket_Upd`.
- Non-empty ticket update `Internal_Notes` values are also synced to `dbo.tbl_TicketEntries.Notes` for the latest ticket entry; if no entry exists, the API inserts a minimal active row in `dbo.tbl_TicketEntries`.
- Ticket entries are queried from `dbo.tbl_TicketEntries` joined to `dbo.tbl_Tickets`.
