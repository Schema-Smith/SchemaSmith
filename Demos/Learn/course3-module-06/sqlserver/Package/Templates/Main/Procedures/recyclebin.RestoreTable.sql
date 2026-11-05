SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER PROCEDURE [recyclebin].[RestoreTable]
  @RecycledName SYSNAME
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @v_SQL NVARCHAR(MAX);
  DECLARE @OriginalSchema SYSNAME;
  DECLARE @OriginalName SYSNAME;
  DECLARE @RegistryId INT;

  -- Look up the registry entry
  SELECT @RegistryId = [Id],
         @OriginalSchema = [OriginalSchema],
         @OriginalName = [OriginalName]
    FROM [recyclebin].[Registry]
    WHERE [RecycledName] = @RecycledName;

  IF @RegistryId IS NULL
  BEGIN
    RAISERROR(N'No registry entry found for recycled table [%s].', 10, 1, @RecycledName);
    RETURN;
  END

  -- Validate the recycled table still exists
  IF OBJECT_ID(N'[recyclebin].' + QUOTENAME(@RecycledName)) IS NULL
  BEGIN
    RAISERROR(N'Table [recyclebin].%s no longer exists. Removing registry entry.', 10, 1, @RecycledName);
    DELETE FROM [recyclebin].[Registry] WHERE [Id] = @RegistryId;
    RETURN;
  END

  -- Validate the target schema exists
  IF SCHEMA_ID(@OriginalSchema) IS NULL
  BEGIN
    RAISERROR(N'Original schema [%s] does not exist. Create it before restoring.', 16, 1, @OriginalSchema);
    RETURN;
  END

  -- Validate no name collision in the target schema
  IF OBJECT_ID(QUOTENAME(@OriginalSchema) + N'.' + QUOTENAME(@OriginalName)) IS NOT NULL
  BEGIN
    RAISERROR(N'A table named [%s].[%s] already exists. Rename or drop it before restoring.', 16, 1, @OriginalSchema, @OriginalName);
    RETURN;
  END

  -- Rename back to the original table name (if different)
  DECLARE @v_FulleRecycledName NVARCHAR(500) = N'recyclebin.' + @RecycledName;
  IF @RecycledName <> @OriginalName
    EXEC sp_rename @v_FulleRecycledName, @OriginalName, N'OBJECT';

  -- Transfer back to the original schema
  SET @v_SQL = N'ALTER SCHEMA ' + QUOTENAME(@OriginalSchema) + N' TRANSFER [recyclebin].' + QUOTENAME(@OriginalName);
  EXEC sp_executesql @v_SQL;

  -- Remove the registry entry
  DELETE FROM [recyclebin].[Registry] WHERE [Id] = @RegistryId;

  PRINT N'Table [recyclebin].' + QUOTENAME(@RecycledName) + N' has been restored as ' + QUOTENAME(@OriginalSchema) + N'.' + QUOTENAME(@OriginalName) + N'.';
END
GO
