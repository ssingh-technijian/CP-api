-- Supports the existing guarded contract-linked-ticket lookup. No business rows are changed.
-- SQL Server 2022 Enterprise target: online, single worker, abandon own build if blocked.
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 5000;
IF DB_NAME() <> N'CP_Production_New' THROW 51000, 'Unexpected contract-index database.', 1;
IF TRY_CONVERT(int,SERVERPROPERTY('ProductMajorVersion')) < 16
 OR CONVERT(nvarchar(128),SERVERPROPERTY('Edition')) NOT LIKE N'%Enterprise%'
 THROW 51000, 'Expected SQL Server 2022 Enterprise online-index support.', 1;
IF COL_LENGTH(N'dbo.tbl_Tickets',N'ContractID') IS NULL OR COL_LENGTH(N'dbo.tbl_Tickets',N'TicketID') IS NULL
 THROW 51000, 'Native ticket reference columns differ.', 1;
IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.tbl_Tickets') AND name=N'IX_Mcp_Tickets_ContractID')
 THROW 51000, 'Index name already exists; reconcile its definition before applying.', 1;
IF NOT EXISTS(
 SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
 JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
 WHERE i.object_id=OBJECT_ID(N'dbo.tbl_Tickets') AND i.is_disabled=0 AND i.has_filter=0
 AND ic.key_ordinal=1 AND c.name=N'ContractID'
)
 CREATE NONCLUSTERED INDEX IX_Mcp_Tickets_ContractID ON dbo.tbl_Tickets(ContractID) INCLUDE(TicketID)
 WITH(ONLINE=ON(WAIT_AT_LOW_PRIORITY(MAX_DURATION=1 MINUTES,ABORT_AFTER_WAIT=SELF)),MAXDOP=1);
