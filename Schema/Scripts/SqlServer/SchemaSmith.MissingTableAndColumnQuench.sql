-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.MissingTableAndColumnQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.MissingTableAndColumnQuench
GO
CREATE PROCEDURE SchemaSmith.MissingTableAndColumnQuench
    @WhatIf BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''

  -- Filegroup placement (#filegroups): a NEW table declaring a filegroup name that does not exist on this
  -- target must fail loudly BEFORE any DDL runs, naming both the table and the filegroup -- not create the
  -- filegroup (that is the user's job -- provisioning stays out of package portability) and not silently
  -- fall back to the default. Only NewTable=1 rows are checked here; an already-existing table's declared
  -- vs. deployed filegroup is a different question (a possible "move"), handled in ModifiedTableQuench.
  RAISERROR('Validate declared table filegroups exist', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #Tables t WITH (NOLOCK)
               WHERE t.NewTable = 1
                 AND t.[FileGroup] IS NOT NULL
                 AND NOT EXISTS (SELECT * FROM sys.filegroups fg WITH (NOLOCK) WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.[FileGroup])))
  BEGIN
    DECLARE @v_FGTable NVARCHAR(1010), @v_FGName NVARCHAR(500)
    SELECT TOP 1 @v_FGTable = t.[Schema] + '.' + t.[Name], @v_FGName = t.[FileGroup]
      FROM #Tables t WITH (NOLOCK)
      WHERE t.NewTable = 1
        AND t.[FileGroup] IS NOT NULL
        AND NOT EXISTS (SELECT * FROM sys.filegroups fg WITH (NOLOCK) WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.[FileGroup]))
    RAISERROR('Table %s declares filegroup %s, which does not exist on this database. SchemaSmith does not create filegroups -- create it on the target first, or correct the declared name.', 16, 1, @v_FGTable, @v_FGName)
  END

  -- Memory-optimized (Hekaton) prerequisites (#J1 / #9). A memory-optimized table needs In-Memory OLTP
  -- support (SERVERPROPERTY('IsXTPSupported') = 1) AND a MEMORY_OPTIMIZED_DATA filegroup (sys.filegroups
  -- type 'FX'). SchemaSmith creates neither and deliberately does NOT degrade a memory-optimized table to an
  -- ordinary disk table (that would silently change its durability and concurrency semantics). Fail here, by
  -- name and BEFORE any DDL, with a clear message rather than letting the raw CREATE error surface -- the
  -- same detect-don't-create posture the filegroup check above takes.
  RAISERROR('Validate memory-optimized prerequisites', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK) WHERE t.NewTable = 1 AND t.[MemoryOptimized] = 1)
     AND (CONVERT(INT, ISNULL(SERVERPROPERTY('IsXTPSupported'), 0)) <> 1
          OR NOT EXISTS (SELECT 1 FROM sys.filegroups fg WITH (NOLOCK) WHERE fg.[type] = 'FX'))
  BEGIN
    DECLARE @v_MoTable NVARCHAR(1010)
    SELECT TOP 1 @v_MoTable = t.[Schema] + '.' + t.[Name]
      FROM #Tables t WITH (NOLOCK) WHERE t.NewTable = 1 AND t.[MemoryOptimized] = 1
    RAISERROR('Table %s is memory-optimized, but this database cannot host one: it needs In-Memory OLTP support (SERVERPROPERTY(''IsXTPSupported'') = 1) and a MEMORY_OPTIMIZED_DATA filegroup. SchemaSmith creates neither and does not degrade a memory-optimized table to a disk table (that would change its durability). Add the filegroup / use a supporting edition, or drop MemoryOptimized.', 16, 1, @v_MoTable)
  END

  -- Partition placement (#partitioning, K1). Three checks, all BEFORE any DDL, because each one produces
  -- either a wrong physical layout or an engine error that names nothing useful.
  --
  -- (a) The scheme must exist. SchemaSmith does not create partition functions or schemes any more than it
  --     creates filegroups -- provisioning stays the user's job so packages stay portable. Falling back to
  --     the default filegroup would build the wrong layout and report success, which is the failure this
  --     whole feature exists to close.
  RAISERROR('Validate declared partition schemes exist', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #Tables t WITH (NOLOCK)
               WHERE t.NewTable = 1
                 AND t.[PartitionScheme] IS NOT NULL
                 AND NOT EXISTS (SELECT * FROM sys.partition_schemes ps WITH (NOLOCK)
                                  WHERE ps.[name] = SchemaSmith.fn_StripBracketWrapping(t.[PartitionScheme])))
  BEGIN
    DECLARE @v_PsMissingTable NVARCHAR(1010), @v_PsMissingName NVARCHAR(500)
    SELECT TOP 1 @v_PsMissingTable = t.[Schema] + '.' + t.[Name], @v_PsMissingName = t.[PartitionScheme]
      FROM #Tables t WITH (NOLOCK)
      WHERE t.NewTable = 1
        AND t.[PartitionScheme] IS NOT NULL
        AND NOT EXISTS (SELECT * FROM sys.partition_schemes ps WITH (NOLOCK)
                         WHERE ps.[name] = SchemaSmith.fn_StripBracketWrapping(t.[PartitionScheme]))
    RAISERROR('Table %s declares partition scheme %s, which does not exist on this database. SchemaSmith does not create partition functions or schemes -- create them on the target first, or correct the declared name.', 16, 1, @v_PsMissingTable, @v_PsMissingName)
  END

  -- (b) Both or neither. SQL Server's ON clause needs the column the partition function is applied to, so
  --     half a declaration is not a placement -- and emitting ON <scheme> with no column is a syntax error
  --     whose message names neither the table nor the property. Checked on EVERY declared table, not just
  --     new ones: a half-pair is equally wrong on one that already exists.
  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
              WHERE (t.[PartitionScheme] IS NOT NULL AND t.[PartitionColumn] IS NULL)
                 OR (t.[PartitionScheme] IS NULL AND t.[PartitionColumn] IS NOT NULL))
  BEGIN
    DECLARE @v_PsHalfTable NVARCHAR(1010), @v_PsHalfMissing NVARCHAR(30)
    SELECT TOP 1 @v_PsHalfTable = t.[Schema] + '.' + t.[Name],
                 @v_PsHalfMissing = CASE WHEN t.[PartitionColumn] IS NULL THEN 'PartitionColumn' ELSE 'PartitionScheme' END
      FROM #Tables t WITH (NOLOCK)
      WHERE (t.[PartitionScheme] IS NOT NULL AND t.[PartitionColumn] IS NULL)
         OR (t.[PartitionScheme] IS NULL AND t.[PartitionColumn] IS NOT NULL)
    RAISERROR('Table %s declares one half of a partition placement but not the other -- %s is missing. PartitionScheme and PartitionColumn are declared together or not at all.', 16, 1, @v_PsHalfTable, @v_PsHalfMissing)
  END

  -- (c) Not both placements. A table lives on ONE data space; declaring a filegroup AND a partition scheme
  --     is a contradiction the CREATE would otherwise resolve by clause order, quietly honouring whichever
  --     was emitted. The mirror of this for an ALREADY-DEPLOYED table lives in ModifiedTableQuench.
  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
              WHERE t.[PartitionScheme] IS NOT NULL AND t.[FileGroup] IS NOT NULL)
  BEGIN
    DECLARE @v_PsBothTable NVARCHAR(1010), @v_PsBothFg NVARCHAR(500), @v_PsBothPs NVARCHAR(500)
    SELECT TOP 1 @v_PsBothTable = t.[Schema] + '.' + t.[Name], @v_PsBothFg = t.[FileGroup], @v_PsBothPs = t.[PartitionScheme]
      FROM #Tables t WITH (NOLOCK)
      WHERE t.[PartitionScheme] IS NOT NULL AND t.[FileGroup] IS NOT NULL
    RAISERROR('Table %s declares both filegroup %s and partition scheme %s. A table lives on one data space -- declare one or the other, not both.', 16, 1, @v_PsBothTable, @v_PsBothFg, @v_PsBothPs)
  END

  -- Same check for the other two placement clauses. A FILESTREAM filegroup must additionally BE one
  -- (type 'FD'): naming an ordinary filegroup there fails with the engine's own message, which does not
  -- say which table asked for it.
  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
              WHERE t.[TextImageFileGroup] IS NOT NULL
                AND NOT EXISTS (SELECT * FROM sys.filegroups fg WITH (NOLOCK)
                                 WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.[TextImageFileGroup])))
  BEGIN
    DECLARE @v_TiTable NVARCHAR(1010), @v_TiName NVARCHAR(500)
    SELECT TOP 1 @v_TiTable = t.[Schema] + '.' + t.[Name], @v_TiName = t.[TextImageFileGroup]
      FROM #Tables t WITH (NOLOCK)
     WHERE t.[TextImageFileGroup] IS NOT NULL
       AND NOT EXISTS (SELECT * FROM sys.filegroups fg WITH (NOLOCK)
                        WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.[TextImageFileGroup]))
    RAISERROR('Table %s declares TextImageFileGroup %s, which does not exist on this database. SchemaSmith does not create filegroups -- create it on the target first, or correct the declared name.', 16, 1, @v_TiTable, @v_TiName)
  END

  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
              WHERE t.[FileStreamFileGroup] IS NOT NULL
                AND NOT EXISTS (SELECT * FROM sys.filegroups fg WITH (NOLOCK)
                                 WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.[FileStreamFileGroup])
                                   AND fg.[type] = 'FD'))
  BEGIN
    DECLARE @v_FsTable NVARCHAR(1010), @v_FsName NVARCHAR(500)
    SELECT TOP 1 @v_FsTable = t.[Schema] + '.' + t.[Name], @v_FsName = t.[FileStreamFileGroup]
      FROM #Tables t WITH (NOLOCK)
     WHERE t.[FileStreamFileGroup] IS NOT NULL
       AND NOT EXISTS (SELECT * FROM sys.filegroups fg WITH (NOLOCK)
                        WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.[FileStreamFileGroup])
                          AND fg.[type] = 'FD')
    RAISERROR('Table %s declares FileStreamFileGroup %s, which is not a FILESTREAM filegroup on this database. SchemaSmith does not create filegroups -- create it with CONTAINS FILESTREAM on the target first, or correct the declared name.', 16, 1, @v_FsTable, @v_FsName)
  END

  RAISERROR('Handle Table Renames', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Rename ' + T.[Schema] + '.' + T.[OldName] + ' to ' + T.[Schema] + '.' + T.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'EXEC sp_rename ''' + SchemaSmith.fn_StripBracketWrapping(T.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(T.[OldName]) + ''', ''' + SchemaSmith.fn_StripBracketWrapping(T.[Name]) + ''';' + CHAR(13) + CHAR(10) AS NVARCHAR(MAX))
                           FROM #Tables T WITH (NOLOCK)
                           WHERE OBJECT_ID(T.[Schema] + '.' + T.[OldName]) IS NOT NULL
                             AND OBJECT_ID(T.[Schema] + '.' + T.[Name]) IS NULL
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Handle Column Renames', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Rename ' + c.[Schema] + '.' + c.[TableName] + '.' + c.[OldName] + ' to ' + c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'EXEC sp_rename ''' + SchemaSmith.fn_StripBracketWrapping(c.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(c.[TableName]) + '.' + SchemaSmith.fn_StripBracketWrapping(c.[OldName]) + ''', ''' + SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]) + ''', ''COLUMN'';' + CHAR(13) + CHAR(10) AS NVARCHAR(MAX))
                           FROM #Columns c WITH (NOLOCK)
                           WHERE COLUMNPROPERTY(OBJECT_ID(c.[Schema] + '.' + c.[TableName]), SchemaSmith.fn_StripBracketWrapping(c.[OldName]), 'AllowsNull') IS NOT NULL
                             AND COLUMNPROPERTY(OBJECT_ID(c.[Schema] + '.' + c.[TableName]), SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'AllowsNull') IS NULL
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  IF OBJECT_ID('SchemaSmith.CustomTableRestore') IS NOT NULL
  BEGIN
    RAISERROR('Attempt custom table restore for tables being added in case they were custom dropped previously', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('EXEC SchemaSmith.CustomTableRestore ''' + SchemaSmith.fn_StripBracketWrapping(T.[Schema]) + ''', ''' + SchemaSmith.fn_StripBracketWrapping(T.[Name]) + ''';' AS NVARCHAR(MAX))
                             FROM #Tables T WITH (NOLOCK)
                             WHERE NewTable = 1
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  ' + T.[Schema] + '.' + T.[Name] + ' Restored'', 10, 100) WITH NOWAIT;' AS NVARCHAR(MAX))
                             FROM #Tables T WITH (NOLOCK)
                             WHERE NewTable = 1
                               AND OBJECT_ID([Schema] + '.' + [Name]) IS NOT NULL
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

    UPDATE #Tables
      SET NewTable = 0
      WHERE NewTable = 1
        AND OBJECT_ID([Schema] + '.' + [Name]) IS NOT NULL
  END


  -- TEXTIMAGE_ON is REJECTED by SQL Server (error 1709) on a table with no large-object column, and that
  -- message names neither the table nor the property. A template-level declaration would otherwise break
  -- every ordinary table in the package. FILESTREAM columns are deliberately excluded -- 1709 says
  -- "non-FILESTREAM varbinary(max)", and a FILESTREAM column does not satisfy the clause.
  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
              WHERE t.[TextImageFileGroup] IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
                                 WHERE c.[Schema] = t.[Schema] AND c.[TableName] = t.[Name]
                                   AND ISNULL(c.[FileStream], 0) = 0
                                   AND (UPPER(REPLACE(c.[DataType], ' ', '')) LIKE '%(MAX)%'
                                        OR UPPER(c.[DataType]) IN ('TEXT', 'NTEXT', 'IMAGE', 'XML')
                                        OR UPPER(c.[DataType]) LIKE 'XML(%')))
  BEGIN
    DECLARE @v_NoLobTable NVARCHAR(1010)
    SELECT TOP 1 @v_NoLobTable = t.[Schema] + '.' + t.[Name]
      FROM #Tables t WITH (NOLOCK)
     WHERE t.[TextImageFileGroup] IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
                        WHERE c.[Schema] = t.[Schema] AND c.[TableName] = t.[Name]
                          AND ISNULL(c.[FileStream], 0) = 0
                          AND (UPPER(REPLACE(c.[DataType], ' ', '')) LIKE '%(MAX)%'
                               OR UPPER(c.[DataType]) IN ('TEXT', 'NTEXT', 'IMAGE', 'XML')
                               OR UPPER(c.[DataType]) LIKE 'XML(%'))
    RAISERROR('Table %s declares TextImageFileGroup but has no large-object column to place. SQL Server rejects TEXTIMAGE_ON on such a table (error 1709). Large-object columns are text, ntext, image, xml, and the (MAX) types -- a FILESTREAM column does not count. Remove TextImageFileGroup, or declare a large-object column.', 16, 1, @v_NoLobTable)
  END
  RAISERROR('Add New Tables', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding new table ' + T.[Schema] + '.' + T.[Name] +
                                  CASE WHEN RTRIM(ISNULL(T.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(T.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'EXEC(''CREATE TABLE ' + T.[Schema] + '.' + T.[Name] + ' (' + REPLACE(ScriptColumns, '''', '''''') + REPLACE(InlineIndexes, '''', '''''') + ')' +
                                  -- Filegroup placement (#filegroups): ON comes right after the column list,
                                  -- BEFORE the WITH clause, per CREATE TABLE's own grammar. Existence was
                                  -- already validated above, so this can emit unconditionally.
                                  -- Graph tables (#graph): AS NODE / AS EDGE follows the column list.
                                  -- Create-time only -- SQL Server has no ALTER for it -- so a change on an
                                  -- existing table is refused in ModifiedTableQuench rather than attempted.
                                  CASE WHEN T.[GraphType] = 'Node' THEN ' AS NODE'
                                       WHEN T.[GraphType] = 'Edge' THEN ' AS EDGE' ELSE '' END +
                                  -- Partition placement wins over FileGroup because the two cannot both be
                                  -- declared -- validated above -- so this is a branch, not a precedence
                                  -- rule. ON <scheme>(<column>) is the same clause slot as ON <filegroup>.
                                  CASE WHEN T.[PartitionScheme] IS NOT NULL THEN ' ON ' + T.[PartitionScheme] + '(' + T.[PartitionColumn] + ')'
                                       WHEN T.[FileGroup] IS NOT NULL THEN ' ON ' + T.[FileGroup] ELSE '' END +
                                  -- TEXTIMAGE_ON follows ON, in SQL Server's own clause order. FILESTREAM_ON
                                  -- deliberately does NOT appear here: the FILESTREAM column is withheld from
                                  -- this CREATE (it needs a unique constraint first, see FileStreamColumnQuench),
                                  -- so the table has no FILESTREAM column yet and the clause is rejected.
                                  -- It is applied there instead, by ALTER, before the column is added.
                                  -- Both are create-time only; a change on a deployed table is refused in
                                  -- ModifiedTableQuench rather than silently ignored.
                                  CASE WHEN T.[TextImageFileGroup] IS NOT NULL THEN ' TEXTIMAGE_ON ' + T.[TextImageFileGroup] ELSE '' END +
                                  -- Sparse columns and a COLUMN_SET are incompatible with data compression, and SQL Server 2008
                                  -- REJECTS the clause outright on such a table -- even DATA_COMPRESSION=NONE. Modern servers
                                  -- accept the redundant NONE, so this only fails at the floor, where the XML ingest path runs.
                                  -- One WITH clause, built from whatever applies. Ledger and
                                  -- DATA_COMPRESSION are legal together but must share a single WITH,
                                  -- so each part contributes ', <option>' and the leading comma is
                                  -- stripped once. An empty list emits no WITH at all, which is what
                                  -- keeps a table with neither exactly as it was before ledger existed.
                                  CASE WHEN t.[WithOptions] <> '' THEN ' WITH (' + STUFF(t.[WithOptions], 1, 2, '') + ')' ELSE '' END + ''');' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''table'', ''' + T.[Schema] + '.' + T.[Name] + ''', ''created'');' AS NVARCHAR(MAX))
                           FROM (SELECT T.[Schema], T.[Name], t.[CompressionType], t.[XmlCompression], t.[FileGroup], t.[PartitionScheme], t.[PartitionColumn], t.[FileStreamFileGroup], t.[TextImageFileGroup], T.[VariantName], T.[GraphType], T.[MemoryOptimized],
                                        -- Memory-optimized indexes must be declared INLINE in the CREATE TABLE
                                        -- (#J1) -- CREATE INDEX is rejected on such a table. Built here from
                                        -- #Indexes; each is NONCLUSTERED (the only kind a memory-optimized
                                        -- table has), HASH with a BUCKET_COUNT when one is declared, and a
                                        -- range index otherwise. The PK is a named constraint; the rest are
                                        -- INDEX clauses. Empty for an ordinary disk table, so nothing changes
                                        -- for one. The ordinary index passes skip memory-optimized tables.
                                        InlineIndexes = CASE WHEN T.[MemoryOptimized] = 1 THEN
                                            ISNULL((SELECT ', ' +
                                                      CASE WHEN I.[PrimaryKey] = 1
                                                           THEN 'CONSTRAINT ' + I.[IndexName] + ' PRIMARY KEY NONCLUSTERED '
                                                           ELSE 'INDEX ' + I.[IndexName] + CASE WHEN I.[Unique] = 1 THEN ' UNIQUE' ELSE '' END + ' NONCLUSTERED ' END +
                                                      CASE WHEN I.[BucketCount] IS NOT NULL
                                                           THEN 'HASH (' + I.[IndexColumns] + ') WITH (BUCKET_COUNT = ' + CAST(I.[BucketCount] AS NVARCHAR(20)) + ')'
                                                           ELSE '(' + I.[IndexColumns] + ')' END
                                                     FROM #Indexes I WITH (NOLOCK)
                                                    WHERE I.[Schema] = T.[Schema] AND I.[TableName] = T.[Name]
                                                    ORDER BY I.[PrimaryKey] DESC, I.[IndexName]
                                                    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), '')
                                          ELSE '' END,
                                        WithOptions =
                                            CASE T.[Ledger] WHEN 'AppendOnly' THEN ', LEDGER = ON (APPEND_ONLY = ON)'
                                                            WHEN 'Updatable'  THEN ', SYSTEM_VERSIONING = ON, LEDGER = ON'
                                                            ELSE '' END +
                                            -- Memory-optimized (#J1): the WITH that switches on the Hekaton
                                            -- storage engine. DURABILITY defaults to SCHEMA_AND_DATA in the parse.
                                            CASE WHEN T.[MemoryOptimized] = 1 THEN ', MEMORY_OPTIMIZED = ON, DURABILITY = ' + T.[Durability] ELSE '' END +
                                            -- Sparse columns and a COLUMN_SET are incompatible with data compression, and SQL
                                            -- Server 2008 REJECTS the clause outright on such a table -- even DATA_COMPRESSION=NONE.
                                            -- Memory-optimized tables reject DATA_COMPRESSION (and XML_COMPRESSION)
                                            -- outright -- the in-memory engine has no page compression -- so both
                                            -- are suppressed for them here.
                                            CASE WHEN T.[MemoryOptimized] = 0
                                                  AND NOT EXISTS (SELECT 1 FROM #Columns C2 WITH (NOLOCK)
                                                                   WHERE C2.[Schema] = T.[Schema] AND C2.[TableName] = T.[Name]
                                                                     AND (ISNULL(C2.[Sparse], 0) = 1 OR ISNULL(C2.[IsColumnSet], 0) = 1))
                                                      AND ISNULL(T.[CompressionType], 'NONE') IN ('NONE', 'ROW', 'PAGE')
                                                 THEN ', DATA_COMPRESSION=' + ISNULL(T.[CompressionType], 'NONE') ELSE '' END +
                                            -- XML_COMPRESSION joins the same WITH list. Independent of
                                            -- DATA_COMPRESSION -- a table can carry both -- and unaffected by
                                            -- the sparse/COLUMN_SET restriction above, which is specific to data
                                            -- compression. Gated on 2022 by VALUE (fn_ServerMajorVersion), which
                                            -- is safe anywhere; only the CATALOG READ in extraction needs
                                            -- kindle-time composition, because that names a column.
                                            CASE WHEN ISNULL(T.[XmlCompression], 0) = 1 AND T.[MemoryOptimized] = 0 AND SchemaSmith.fn_ServerMajorVersion() >= 16
                                                 THEN ', XML_COMPRESSION = ON' ELSE '' END,
                                        HasSparseOrColumnSet = CASE WHEN EXISTS (SELECT 1 FROM #Columns C2 WITH (NOLOCK)
                                                                                  WHERE C2.[Schema] = T.[Schema] AND C2.[TableName] = T.[Name]
                                                                                    AND (ISNULL(C2.[Sparse], 0) = 1 OR ISNULL(C2.[IsColumnSet], 0) = 1)) THEN 1 ELSE 0 END,
                                        ScriptColumns = STUFF((SELECT ', ' + [ColumnScript] FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND RTRIM(ISNULL([ComputedExpression], '')) = '' AND ISNULL(C.[FileStream], 0) = 0 ORDER BY c.[_RowId] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
                                   FROM #Tables T WITH (NOLOCK)
                                   WHERE NewTable = 1) T
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- Object-change audit (#363): WhatIf twin of the embedded 'table'/'created' row above. That row
  -- rides the CREATE TABLE DDL (executed only on a real run); under WhatIf the DDL is printed, so
  -- capture the would-create here from the same #Tables state.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'table', T.[Schema] + '.' + T.[Name], 'wouldCreate'
        FROM #Tables T WITH (NOLOCK) WHERE NewTable = 1

  RAISERROR('Add New Physical Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding ' + CAST(ColumnCount AS NVARCHAR(100)) + ' new columns to ' + T.[Schema] + '.' + T.[Name] +
                                  CASE WHEN RTRIM(ISNULL(VariantList, '')) <> '' THEN ' (variant: ' + VariantList + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' ADD ' + ColumnScripts + ';' AS NVARCHAR(MAX))
                           FROM (SELECT T.[Schema], T.[Name],
                                        ColumnScripts = STUFF((SELECT ', ' + CAST([ColumnScript] +
                                                              -- WITH VALUES is an ALTER-only clause, and it is per COLUMN, not per statement:
                                                              -- one column in a multi-column ADD can carry it while its neighbours do not
                                                              -- (verified live). It must follow DEFAULT, which is the last clause ColumnScript
                                                              -- emits. Guarded on a default being present because WITH VALUES without one is a
                                                              -- SYNTAX error, not a no-op -- it would fail the whole batch. A column authored
                                                              -- that way is reported by --Validate rather than silently dropped here.
                                                              CASE WHEN ISNULL(c.[BackfillExistingRows], 0) = 1 AND RTRIM(ISNULL(c.[Default], '')) <> ''
                                                                   THEN ' WITH VALUES' ELSE '' END
                                                         AS NVARCHAR(MAX)) FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = '' AND ISNULL(C.[FileStream], 0) = 0 ORDER BY c.[_RowId] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''),
                                        ColumnCount = (SELECT COUNT(*) FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = '' AND ISNULL(C.[FileStream], 0) = 0),
                                        VariantList = STUFF((SELECT ', ' + CAST(REPLACE(RTRIM(c.[VariantName]), '''', '''''') AS NVARCHAR(MAX))
                                                               FROM #Columns C WITH (NOLOCK)
                                                               WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = '' AND ISNULL(C.[FileStream], 0) = 0
                                                                 AND RTRIM(ISNULL(c.[VariantName], '')) <> '' FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
                                   FROM #Tables T WITH (NOLOCK)
                                   WHERE NewTable = 0
                                     AND EXISTS (SELECT * FROM #Columns c WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = '' AND ISNULL(C.[FileStream], 0) = 0)) T
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- Object-change audit (#243 E5, #363): one row per physical column added to an EXISTING table. New
  -- tables' columns are covered by the table/created row above, so NewTable = 0 only. Per-source-row
  -- (the ALTER above folds a table's new columns into one statement, so this cannot weave into it).
  -- Runs regardless of @WhatIf so a WhatIf preview is captured; the action carries the mode.
  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT @@SPID, 'column', c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName], CASE WHEN @WhatIf = 1 THEN 'wouldCreate' ELSE 'created' END
      FROM #Columns c WITH (NOLOCK)
      JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = c.[Schema] AND t.[Name] = c.[TableName]
      WHERE t.NewTable = 0 AND c.NewColumn = 1 AND RTRIM(ISNULL(c.[ComputedExpression], '')) = '' AND ISNULL(c.[FileStream], 0) = 0

  SET NOCOUNT OFF
END TRY
BEGIN CATCH
    DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH