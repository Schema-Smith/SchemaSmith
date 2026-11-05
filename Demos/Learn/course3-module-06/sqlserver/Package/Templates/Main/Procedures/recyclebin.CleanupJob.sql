SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER PROCEDURE [recyclebin].[CleanupJob]
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SQL           NVARCHAR(MAX);
    DECLARE @TableName     SYSNAME;
    DECLARE @RecycledName  SYSNAME;
    DECLARE @RegistryId    INT;
    DECLARE @Now           DATETIME2 = SYSDATETIME();

    -- =========================================================================
    -- 1. Register untracked tables in the recyclebin schema
    --    (tables that exist in the schema but are not in the registry)
    -- =========================================================================
    INSERT INTO [recyclebin].[Registry] ([OriginalSchema], [OriginalName], [RecycledName], [RecycledDate], [RetentionDays])
    SELECT N'unknown',
           t.name,
           t.name,
           @Now,
           90
    FROM   sys.tables t
    JOIN   sys.schemas s ON t.schema_id = s.schema_id
    WHERE  s.name = N'recyclebin'
      AND  t.name <> N'Registry'
      AND  NOT EXISTS (
               SELECT 1
               FROM   [recyclebin].[Registry] r
               WHERE  r.[RecycledName] = t.name
           );

    -- =========================================================================
    -- 2. Drop tables that have exceeded their retention period
    -- =========================================================================
    DECLARE cleanup_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT r.[Id], r.[RecycledName]
        FROM   [recyclebin].[Registry] r
        WHERE  r.[ExpirationDate] <= @Now;

    OPEN cleanup_cursor;
    FETCH NEXT FROM cleanup_cursor INTO @RegistryId, @RecycledName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Verify the table still exists before dropping
        IF OBJECT_ID(N'recyclebin.' + QUOTENAME(@RecycledName)) IS NOT NULL
        BEGIN
            SET @SQL = N'DROP TABLE [recyclebin].' + QUOTENAME(@RecycledName);
            EXEC sp_executesql @SQL;

            PRINT N'Dropped expired table [recyclebin].' + QUOTENAME(@RecycledName);
        END

        -- Remove the registry entry
        DELETE FROM [recyclebin].[Registry] WHERE [Id] = @RegistryId;

        FETCH NEXT FROM cleanup_cursor INTO @RegistryId, @RecycledName;
    END

    CLOSE cleanup_cursor;
    DEALLOCATE cleanup_cursor;
END
GO
