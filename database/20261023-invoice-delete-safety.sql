-- Invoice child cleanup safety; native schema acceptance passed 23 October 2026.
-- Preserves signature/accounting guard; no FK enabling, index changes or historical orphan cleanup.
CREATE OR ALTER PROCEDURE [dbo].[stp_Inv_Org_Loc_Inv_Delete_V2]
    @InvoiceID INT,
    @UserID INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @InvoiceType VARCHAR(25), @QBConfirm VARCHAR(20), @DeletedRows INT;
    BEGIN TRY
        BEGIN TRAN;
        -- Hold the header while checking its accounting state and deleting its children.
        SELECT @InvoiceType = InvoiceType, @QBConfirm = QBConfirm
        FROM dbo.vw_InvHeader WITH (UPDLOCK, HOLDLOCK)
        WHERE InvoiceID = @InvoiceID;
        IF @@ROWCOUNT = 0 OR NULLIF(@QBConfirm, '') IS NOT NULL
        BEGIN
            COMMIT;
            SELECT 0 AS RetVal;
            RETURN;
        END;
        IF @InvoiceType = 'Weekly' OR @InvoiceType = 'WeeklyOut'
            UPDATE dbo.vw_TktEntry_Save SET InvoiceID = NULL, InvDetID = NULL
            WHERE InvoiceID = @InvoiceID;
        UPDATE dbo.tbl_InvoiceHeader SET ModifiedUser = @UserID, ModifiedDatetime = GETDATE()
        WHERE InvoiceID = @InvoiceID;
        -- All seven declared cascade constraints are currently disabled in production.
        -- Scope every delete to the validated header; do not enable constraints or touch other invoices.
        DELETE FROM dbo.[tbl_Invoice_Detail] WHERE InvoiceID = @InvoiceID;
        DELETE FROM dbo.[tbl_Invoice_Monthly_ActualSupport_Detail] WHERE InvoiceID = @InvoiceID;
        DELETE FROM dbo.[tbl_Invoice_Monthly_Asset_Detail] WHERE InvoiceID = @InvoiceID;
        DELETE FROM dbo.[tbl_Invoice_Monthly_Labor_Detail] WHERE InvoiceID = @InvoiceID;
        DELETE FROM dbo.[tbl_Invoice_Monthly_SIP_Detail] WHERE InvoiceID = @InvoiceID;
        DELETE FROM dbo.[tbl_Invoice_Recur_Detail] WHERE InvoiceID = @InvoiceID;
        DELETE FROM dbo.[tbl_Invoice_Weekly_Detail] WHERE InvoiceID = @InvoiceID;
        DELETE FROM dbo.vw_InvHeader WHERE InvoiceID = @InvoiceID;
        SET @DeletedRows = @@ROWCOUNT;
        COMMIT;
        SELECT @DeletedRows AS RetVal;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
