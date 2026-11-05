SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER PROCEDURE [SchemaSmith].[CustomTableRestore]
  @SchemaName SYSNAME,
  @TableName  SYSNAME
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @RecycledName SYSNAME;

  IF OBJECT_ID('recyclebin.Registry') IS NOT NULL 
  BEGIN
    -- Find the most recently recycled copy matching the original schema and table name
    SELECT TOP 1 @RecycledName = [RecycledName]
      FROM [recyclebin].[Registry]
      WHERE [OriginalSchema] = @SchemaName
        AND [OriginalName]   = @TableName
      ORDER BY [RecycledDate] DESC;
  END

  IF @RecycledName IS NULL
  BEGIN
    PRINT N'No recycled copies found for [' + @SchemaName + N'].[' + @TableName + N'].';
    RETURN;
  END

  EXEC [recyclebin].[RestoreTable] @RecycledName = @RecycledName;
END
GO
