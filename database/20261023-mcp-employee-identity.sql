-- New, opt-in employee identity boundary. Does not modify native directory profiles,
-- usernames, roles, menus, existing credentials, or existing application registrations.
-- Identity rows must come from an administrator-reviewed Entra/native-directory manifest.
-- No email/UPN-based automatic enrollment is permitted.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO
IF SCHEMA_ID(N'mcp') IS NULL EXEC(N'CREATE SCHEMA [mcp] AUTHORIZATION [dbo];');
GO
IF OBJECT_ID(N'mcp.EmployeeIdentity',N'U') IS NULL
BEGIN
    CREATE TABLE [mcp].[EmployeeIdentity]
    (
        [MicrosoftTenantId] uniqueidentifier NOT NULL,
        [MicrosoftObjectId] uniqueidentifier NOT NULL,
        [DirectoryUserId] int NOT NULL,
        [NativeUsernameAtApproval] nvarchar(50) NOT NULL,
        [IsEnabled] bit NOT NULL CONSTRAINT [DF_McpEmployeeIdentity_Enabled] DEFAULT(1),
        [ApprovedAtUtc] datetime2(3) NOT NULL CONSTRAINT [DF_McpEmployeeIdentity_ApprovedAt] DEFAULT(SYSUTCDATETIME()),
        [ApprovedBy] nvarchar(128) NOT NULL,
        [SourceManifestHash] binary(32) NOT NULL,
        CONSTRAINT [PK_McpEmployeeIdentity] PRIMARY KEY ([MicrosoftTenantId],[MicrosoftObjectId]),
        CONSTRAINT [UQ_McpEmployeeIdentity_NativeUser] UNIQUE ([MicrosoftTenantId],[DirectoryUserId]),
        CONSTRAINT [CK_McpEmployeeIdentity_PositiveUser] CHECK ([DirectoryUserId]>0),
        CONSTRAINT [CK_McpEmployeeIdentity_ValidTenant] CHECK ([MicrosoftTenantId]<>'00000000-0000-0000-0000-000000000000'),
        CONSTRAINT [CK_McpEmployeeIdentity_ValidObject] CHECK ([MicrosoftObjectId]<>'00000000-0000-0000-0000-000000000000')
    );
END;
GO
-- Intentionally no FK on tbl_Directory: native employee deactivation/deletion must retain
-- its existing behavior. A missing/inactive/non-Technijian/non-T-role directory row denies
-- access on every request. Unique native-user mapping prevents silent account reassignment.
CREATE OR ALTER PROCEDURE [mcp].[stp_EmployeeIdentity_Get]
    @MicrosoftTenantId uniqueidentifier,
    @MicrosoftObjectId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT d.DirID,d.Username,d.Role,d.LocationTopFilter
    FROM mcp.EmployeeIdentity e JOIN dbo.tbl_Directory d ON d.DirID=e.DirectoryUserId
    WHERE e.MicrosoftTenantId=@MicrosoftTenantId AND e.MicrosoftObjectId=@MicrosoftObjectId
      AND e.IsEnabled=1 AND d.IsActive=1 AND d.DirectoryType=N'User'
      AND d.ClientCode=N'Technijian' AND d.Role IN(N'T1',N'T2',N'T3')
      AND LEN(LTRIM(RTRIM(ISNULL(d.Username,N''))))>0
      -- Native time-entry ownership uses Username. Do not let a duplicate active
      -- directory profile expose another person's username-owned records.
      AND NOT EXISTS(SELECT 1 FROM dbo.tbl_Directory other
        WHERE other.Username=d.Username AND other.IsActive=1 AND other.DirID<>d.DirID);
END;
GO
