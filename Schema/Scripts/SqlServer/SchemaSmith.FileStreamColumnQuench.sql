-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- FILESTREAM columns, applied after the table's keys exist.
--
-- Its own procedure for the same reason SchemaSmith.ChangeTrackingQuench is: SQL Server refuses a
-- FILESTREAM column unless the table already carries a non-null ROWGUIDCOL column covered by a unique
-- CONSTRAINT (error 5505), and SchemaSmith creates columns in MissingTableAndColumnQuench but keys in
-- MissingIndexesAndConstraintsQuench -- after. A FILESTREAM column emitted with its table therefore
-- fails on every new table. MissingTableAndColumnQuench withholds them; this adds them once the keys
-- are in place.
--
-- Certified against a real instance, because the engine is stricter than error 5505 reads: a unique
-- INDEX does NOT satisfy it. Only a PRIMARY KEY or a UNIQUE constraint does. SchemaSmith already
-- distinguishes the two -- Indexes[].UniqueConstraint emits ADD CONSTRAINT ... UNIQUE, Unique emits an
-- index -- so the check below deliberately tests is_primary_key/is_unique_constraint and not is_unique.
IF OBJECT_ID('SchemaSmith.FileStreamColumnQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.FileStreamColumnQuench
GO
CREATE PROCEDURE SchemaSmith.FileStreamColumnQuench
    @WhatIf BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON

  -- Deliberately NOT filtered on NewColumn. For a table being created in this run, NewColumn is 0
  -- for every non-computed column -- they are expected to arrive with the CREATE TABLE. Because
  -- MissingTableAndColumnQuench withholds FILESTREAM columns from that CREATE, filtering on
  -- NewColumn here meant nothing ever added them on a new table: the column just vanished, with no
  -- error anywhere. Asking the catalog whether the column exists covers the new-table and the
  -- existing-table cases identically, and is idempotent by construction.
  IF NOT EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
                  WHERE c.[FileStream] = 1
                    AND COLUMNPROPERTY(OBJECT_ID(c.[Schema] + '.' + c.[TableName]), SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'ColumnId') IS NULL)
    RETURN

  RAISERROR('Add FILESTREAM Columns', 10, 100) WITH NOWAIT

  -- Refuse with the fix in the message rather than letting 5505 surface raw. 5505 names ROWGUIDCOL but
  -- not the constraint-vs-index distinction, which is the part that actually catches people out.
  DECLARE @v_Missing NVARCHAR(MAX) =
    STUFF((SELECT DISTINCT ', ' + c.[Schema] + '.' + c.[TableName]
             FROM #Columns c WITH (NOLOCK)
            WHERE c.[FileStream] = 1
              AND COLUMNPROPERTY(OBJECT_ID(c.[Schema] + '.' + c.[TableName]), SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'ColumnId') IS NULL
              AND NOT EXISTS (SELECT 1
                                FROM sys.columns rc
                                JOIN sys.index_columns ic
                                  ON ic.[object_id] = rc.[object_id] AND ic.column_id = rc.column_id AND ic.is_included_column = 0
                                JOIN sys.indexes i
                                  ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id
                               WHERE rc.[object_id] = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
                                 AND rc.is_rowguidcol = 1
                                 AND rc.is_nullable = 0
                                 AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1)
                                 -- a composite key does not make the guid column unique on its own
                                 AND (SELECT COUNT(*) FROM sys.index_columns ic2
                                       WHERE ic2.[object_id] = i.[object_id] AND ic2.index_id = i.index_id
                                         AND ic2.is_included_column = 0) = 1)
             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')

  IF @v_Missing IS NOT NULL
    RAISERROR('FILESTREAM column(s) declared on table(s) with no usable ROWGUIDCOL: %s. SQL Server requires a NOT NULL UNIQUEIDENTIFIER column with ROWGUIDCOL that is covered by a single-column PRIMARY KEY or UNIQUE CONSTRAINT. A unique INDEX does NOT satisfy this - declare the index entry with "UniqueConstraint": true (or "PrimaryKey": true) rather than "Unique": true. Declare the ROWGUIDCOL column itself as part of its DataType - "DataType": "UNIQUEIDENTIFIER ROWGUIDCOL" - the same way IDENTITY is declared. SchemaSmith does not add the column for you: one it invented would appear in no package and vanish on the next extract-redeploy round trip.', 16, 1, @v_Missing)

  -- Bind the table to its declared FILESTREAM filegroup BEFORE the column is added. Verified on a live
  -- server: ALTER TABLE ... SET (FILESTREAM_ON = <fg>) is accepted while the table still has no
  -- FILESTREAM column, and the column added afterwards lands on that filegroup. Doing it the other way
  -- round is not available -- once a table has a filestream data space the ALTER fails 1726 -- which is
  -- also why a CHANGED declaration is refused in ModifiedTableQuench rather than applied.
  DECLARE @v_FsOn NVARCHAR(MAX) =
    (SELECT 'RAISERROR(''  Binding ' + t.[Schema] + '.' + t.[Name] + ' to FILESTREAM filegroup ' + t.[FileStreamFileGroup] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
            'ALTER TABLE ' + t.[Schema] + '.' + t.[Name] + ' SET (FILESTREAM_ON = ' + t.[FileStreamFileGroup] + ');' + CHAR(13) + CHAR(10)
       FROM #Tables t WITH (NOLOCK)
       JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
      WHERE t.[FileStreamFileGroup] IS NOT NULL
        AND st.filestream_data_space_id IS NULL
        AND EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
                     WHERE c.[Schema] = t.[Schema] AND c.[TableName] = t.[Name] AND ISNULL(c.[FileStream], 0) = 1)
        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)')
  IF @v_FsOn IS NOT NULL
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_FsOn ELSE EXEC(@v_FsOn)

  SELECT @v_SQL = @v_SQL +
    'RAISERROR(''  Adding FILESTREAM column ' + c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
    'ALTER TABLE ' + c.[Schema] + '.' + c.[TableName] + ' ADD ' + CAST(c.[ColumnScript] AS NVARCHAR(MAX)) + ';' + CHAR(13) + CHAR(10)
    FROM #Columns c WITH (NOLOCK)
   WHERE c.[FileStream] = 1
     AND COLUMNPROPERTY(OBJECT_ID(c.[Schema] + '.' + c.[TableName]), SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'ColumnId') IS NULL
   ORDER BY c.[Schema], c.[TableName], c.[_RowId]

  IF @v_SQL <> ''
  BEGIN
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  END

  SET NOCOUNT OFF
END TRY
BEGIN CATCH
    DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH
