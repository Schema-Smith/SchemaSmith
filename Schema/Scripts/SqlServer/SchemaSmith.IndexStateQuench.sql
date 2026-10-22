-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

IF OBJECT_ID('SchemaSmith.IndexStateQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.IndexStateQuench
GO
-- The Disabled state of every declared index, converged once the index exists. Declared true over a live enabled index
-- disables it (ALTER INDEX ... DISABLE); declared false -- the default, so a package that says nothing -- over an index
-- someone disabled by hand rebuilds it (ALTER INDEX ... REBUILD), which is what enables it, so a unique index the
-- package says exists enforces again. The rebuild reports the truth when the rows no longer satisfy a unique index.
-- Rebuilds go clustered-first (a disabled clustered index disables every nonclustered one, and rebuilding it does not
-- rebuild them) and disables go clustered-last, so neither order undoes its own work.
CREATE PROCEDURE SchemaSmith.IndexStateQuench
  @WhatIf BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX)

  RAISERROR('Converge index disabled state', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  ' + x.[Verb] + ' index ' + REPLACE(REPLACE(x.[QualifiedName], '''', ''''''), '%', '%%') + ''', 10, 100) WITH NOWAIT' + CHAR(13) + CHAR(10) +
      'ALTER INDEX ' + i.[IndexName] + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' ' + x.[Clause] + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + CASE WHEN i.[PrimaryKey] = 1 OR i.[UniqueConstraint] = 1 THEN 'constraint' ELSE 'index' END + ''', ''' + REPLACE(x.[QualifiedName], '''', '''''') + ''', ''modified'')'
      AS NVARCHAR(MAX))
    FROM #Indexes i WITH (NOLOCK)
    JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                       AND si.[name] COLLATE DATABASE_DEFAULT = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
    CROSS APPLY (SELECT i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] AS [QualifiedName],
                        CASE WHEN ISNULL(i.[Disabled], 0) = 1 THEN 'Disabling' ELSE 'Rebuilding disabled' END AS [Verb],
                        CASE WHEN ISNULL(i.[Disabled], 0) = 1 THEN 'DISABLE' ELSE 'REBUILD' END AS [Clause]) x
    WHERE ISNULL(i.[Disabled], 0) <> si.is_disabled
    ORDER BY i.[Schema], i.[TableName],
             CASE WHEN ISNULL(i.[Disabled], 0) = 1 THEN 1 ELSE 0 END,
             CASE WHEN ISNULL(i.[Disabled], 0) = 1 THEN CASE WHEN si.[type] IN (1, 5) THEN 1 ELSE 0 END
                  ELSE CASE WHEN si.[type] IN (1, 5) THEN 0 ELSE 1 END END,
             i.[IndexName]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 BEGIN EXEC SchemaSmith.PrintWithNoWait @v_SQL; EXEC SchemaSmith.RecordWouldChanges @v_SQL END ELSE EXEC(@v_SQL)

  SET NOCOUNT OFF
END
