-- Employee action proposals only. No native business table, profile or permission changes.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF SCHEMA_ID(N'mcp') IS NULL EXEC(N'CREATE SCHEMA mcp AUTHORIZATION dbo;');
IF OBJECT_ID(N'mcp.EmployeeAction',N'U') IS NULL
BEGIN
 CREATE TABLE mcp.EmployeeAction
 (
  ActionId uniqueidentifier NOT NULL CONSTRAINT PK_mcp_EmployeeAction PRIMARY KEY,
  MicrosoftTenantId uniqueidentifier NOT NULL,
  MicrosoftObjectId uniqueidentifier NOT NULL,
  DirectoryUserId int NOT NULL,
  RequestId uniqueidentifier NOT NULL,
  Operation varchar(40) NOT NULL,
  PayloadJson nvarchar(max) NOT NULL,
  PayloadHash binary(32) NOT NULL,
  SnapshotHash binary(32) NOT NULL,
  ReviewJson nvarchar(max) NOT NULL,
  ActionVersion binary(32) NOT NULL,
  Status varchar(16) NOT NULL CONSTRAINT DF_mcp_EmployeeAction_Status DEFAULT('pending'),
  CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_mcp_EmployeeAction_Created DEFAULT(SYSUTCDATETIME()),
  ExpiresAtUtc datetime2(3) NOT NULL,
  ReviewNonceHash binary(32) NULL,
  ReviewNonceExpiresAtUtc datetime2(3) NULL,
  ResultJson nvarchar(max) NULL,
  AppliedAtUtc datetime2(3) NULL,
  RejectedAtUtc datetime2(3) NULL,
  CONSTRAINT UQ_mcp_EmployeeAction_Request UNIQUE(MicrosoftTenantId,MicrosoftObjectId,RequestId),
  CONSTRAINT CK_mcp_EmployeeAction_Status CHECK(Status IN('pending','applied','rejected')),
  CONSTRAINT CK_mcp_EmployeeAction_Payload CHECK(ISJSON(PayloadJson)=1 AND DATALENGTH(PayloadJson)<=32768),
  CONSTRAINT CK_mcp_EmployeeAction_Review CHECK(ISJSON(ReviewJson)=1 AND DATALENGTH(ReviewJson)<=32768),
  CONSTRAINT CK_mcp_EmployeeAction_Result CHECK(ResultJson IS NULL OR (ISJSON(ResultJson)=1 AND DATALENGTH(ResultJson)<=16384)),
  CONSTRAINT CK_mcp_EmployeeAction_Actor CHECK(DirectoryUserId>0)
 );
END;
IF OBJECT_ID(N'mcp.EmployeeActionAudit',N'U') IS NULL
BEGIN
 CREATE TABLE mcp.EmployeeActionAudit
 (
  AuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_mcp_EmployeeActionAudit PRIMARY KEY,
  ActionId uniqueidentifier NOT NULL,
  MicrosoftObjectId uniqueidentifier NOT NULL,
  DirectoryUserId int NOT NULL,
  Event varchar(20) NOT NULL,
  CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_mcp_EmployeeActionAudit_Created DEFAULT(SYSUTCDATETIME()),
  CONSTRAINT FK_mcp_EmployeeActionAudit_Action FOREIGN KEY(ActionId) REFERENCES mcp.EmployeeAction(ActionId) ON DELETE CASCADE,
  CONSTRAINT CK_mcp_EmployeeActionAudit_Event CHECK(Event IN('proposed','reviewed','applied','rejected'))
 );
 CREATE INDEX IX_mcp_EmployeeActionAudit_Action ON mcp.EmployeeActionAudit(ActionId,AuditId);
END;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'mcp.EmployeeAction'))<>19
 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'mcp.EmployeeActionAudit'))<>6
 THROW 51001,'Employee action schema differs from the reviewed contract; no automatic alteration.',1;
COMMIT TRANSACTION;
