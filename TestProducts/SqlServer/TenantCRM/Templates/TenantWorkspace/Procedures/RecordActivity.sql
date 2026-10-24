CREATE OR ALTER PROCEDURE [{{SchemaName}}].[RecordActivity]
    @CustomerID INT,
    @ActivityTypeID INT,
    @Note NVARCHAR(1024) = NULL,
    @ActivityID BIGINT = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [{{SchemaName}}].[Activities] ([CustomerID], [ActivityTypeID], [Note])
    VALUES (@CustomerID, @ActivityTypeID, @Note);

    SET @ActivityID = SCOPE_IDENTITY();

    INSERT INTO [dbo].[GlobalAuditLog] ([TenantName], [EventType], [Detail])
    VALUES (N'{{SchemaName}}', N'ActivityRecorded',
            N'CustomerID=' + CAST(@CustomerID AS NVARCHAR(16)) +
            N'; TypeID=' + CAST(@ActivityTypeID AS NVARCHAR(16)));
END;
