-- MCP ticket creation preserves native field/default/contract/trigger behavior while
-- explicitly disabling email notification at INSERT. Existing Portal/API procedures
-- are unchanged. Authorization and exact human approval run in the employee service.
CREATE OR ALTER PROCEDURE mcp.stp_EmployeeTicket_Create
    @XML_IN xml,
    @XML_OUT xml OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @XML_IN IS NULL OR @XML_IN.value('count(/Root/Ticket)','int')<>1
        THROW 51001,'Exactly one reviewed ticket is required.',1;
    DECLARE @ClientID int=@XML_IN.value('(/Root/Ticket/ClientID/text())[1]','int'),
        @Requestor int=@XML_IN.value('(/Root/Ticket/Requestor_DirID/text())[1]','int'),
        @Assigned int=@XML_IN.value('(/Root/Ticket/AssignTo_DirID/text())[1]','int'),
        @ContractID int=@XML_IN.value('(/Root/Ticket/ContractID/text())[1]','int'),
        @RoleType int=@XML_IN.value('(/Root/Ticket/RoleType/text())[1]','int'),
        @Priority int=@XML_IN.value('(/Root/Ticket/Priority/text())[1]','int'),
        @Status int=@XML_IN.value('(/Root/Ticket/Status/text())[1]','int'),
        @Title nvarchar(200)=@XML_IN.value('(/Root/Ticket/Title/text())[1]','nvarchar(200)'),
        @Description nvarchar(max)=@XML_IN.value('(/Root/Ticket/Description/text())[1]','nvarchar(max)'),
        @CreatedBy nvarchar(50)=@XML_IN.value('(/Root/Ticket/CreatedBy/text())[1]','nvarchar(50)'),
        @LTF nvarchar(100)=@XML_IN.value('(/Root/Ticket/LocationTopFilter/text())[1]','nvarchar(100)'),
        @CurrentContract int;
    IF ISNULL(@ClientID,0)<=0 OR ISNULL(@Assigned,0)<=0 OR @Requestor<>@ClientID
       OR NULLIF(LTRIM(RTRIM(@Title)),'') IS NULL OR NULLIF(LTRIM(RTRIM(@CreatedBy)),'') IS NULL
        THROW 51001,'Invalid reviewed ticket fields.',1;
    SET @CurrentContract=ISNULL(dbo.fn_int_GetActiveContractForLocation(@ClientID),0);
    IF @CurrentContract<>ISNULL(@ContractID,0)
        THROW 51001,'The native active contract changed after review.',1;
    DECLARE @IDs table(TicketID int);
    INSERT dbo.tbl_Tickets(Requestor_DirID,AssignTo_DirID,ContractID,AssetID,Title,Priority,Status,
        Description,RequestType,RequestDate,LocationTopFilter,CreatedBy,CreateDate,LastModifiedTime,
        RoleType,PriorityTxt,IsActive,ClientID,AssetTxt,ParentID,SendEmail,Email_Sent,RecurringType)
    OUTPUT inserted.TicketID INTO @IDs(TicketID)
    VALUES(@Requestor,@Assigned,@ContractID,0,@Title,@Priority,@Status,@Description,N'Client Portal',
        GETDATE(),@LTF,@CreatedBy,GETDATE(),GETDATE(),CONVERT(varchar(50),@RoleType),
        (SELECT Description FROM dbo.vw_LookupV_Ticket WHERE Type='Priority' AND ID=CONVERT(nvarchar(50),@Priority)),
        1,@ClientID,N'',0,0,0,N'0');
    SET @XML_OUT=(SELECT TicketID FROM @IDs FOR XML PATH('Tickets'),ROOT('Root'),TYPE);
END;
GO

-- Update only the supported fields; preserve omitted native columns and Unicode.
-- The existing native table triggers provide their normal audit/derived-field behavior.
CREATE OR ALTER PROCEDURE mcp.stp_EmployeeTicket_Edit
    @TicketID int,
    @ModifiedBy nvarchar(50),
    @Title nvarchar(200),
    @Description nvarchar(max),
    @Priority nvarchar(50),
    @Status nvarchar(50),
    @EstimatedHours decimal(18,2)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF ISNULL(@TicketID,0)<=0 OR NULLIF(LTRIM(RTRIM(@ModifiedBy)),'') IS NULL
        OR NULLIF(LTRIM(RTRIM(@Title)),'') IS NULL
        THROW 51001,'Invalid reviewed ticket edit.',1;
    UPDATE dbo.tbl_Tickets SET Title=@Title,Description=@Description,Priority=@Priority,
        PriorityTxt=(SELECT Description FROM dbo.vw_LookupV_Ticket WHERE Type='Priority' AND ID=@Priority),Status=@Status,
        Estimated_Hours=@EstimatedHours,ModifiedBy=@ModifiedBy,LastModifiedTime=GETDATE()
    WHERE TicketID=@TicketID AND IsActive=1;
    IF @@ROWCOUNT<>1 THROW 51001,'The active reviewed ticket no longer exists.',1;
END;
