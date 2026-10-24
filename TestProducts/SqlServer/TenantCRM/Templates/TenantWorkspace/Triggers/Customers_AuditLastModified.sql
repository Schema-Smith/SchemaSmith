CREATE OR ALTER TRIGGER [{{SchemaName}}].[Customers_AuditLastModified]
    ON [{{SchemaName}}].[Customers]
    AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Don't recurse when our own UPDATE fires this trigger.
    IF NOT UPDATE([LastModifiedAt])
    BEGIN
        UPDATE c
           SET [LastModifiedAt] = SYSUTCDATETIME()
          FROM [{{SchemaName}}].[Customers] c
         INNER JOIN inserted i ON i.[CustomerID] = c.[CustomerID];
    END
END;
