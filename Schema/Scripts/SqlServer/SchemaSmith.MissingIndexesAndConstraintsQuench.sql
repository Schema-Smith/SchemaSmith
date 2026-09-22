-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.MissingIndexesAndConstraintsQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.MissingIndexesAndConstraintsQuench
GO
CREATE PROCEDURE SchemaSmith.MissingIndexesAndConstraintsQuench
    @ProductName NVARCHAR(50),
    @WhatIf BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON

  RAISERROR('Collect index level extended properties', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#IndexProperties') IS NOT NULL DROP TABLE #IndexProperties
  SELECT t.[Schema], t.[Name] AS TableName, objname COLLATE DATABASE_DEFAULT AS IndexName, x.[Name] COLLATE DATABASE_DEFAULT AS PropertyName, CONVERT(NVARCHAR(50), x.[value]) COLLATE DATABASE_DEFAULT AS [value]
  INTO #IndexProperties
  FROM #Tables t WITH (NOLOCK)
           CROSS APPLY fn_listextendedproperty(default, 'Schema', SchemaSmith.fn_StripBracketWrapping(t.[Schema]), 'Table', SchemaSmith.fn_StripBracketWrapping(t.[Name]), 'Index', default) x
  WHERE x.[Name] COLLATE DATABASE_DEFAULT = 'ProductName'

  UPDATE #Columns
    SET NewColumn = 0
    WHERE NewColumn = 1
      AND EXISTS (SELECT * 
                    FROM INFORMATION_SCHEMA.COLUMNS c
                    WHERE c.TABLE_SCHEMA = SchemaSmith.fn_StripBracketWrapping(#Columns.[Schema]) 
                      AND c.TABLE_NAME = SchemaSmith.fn_StripBracketWrapping(#Columns.[TableName]) 
                      AND c.COLUMN_NAME = SchemaSmith.fn_StripBracketWrapping(#Columns.[ColumnName]))

  RAISERROR('Add New Computed Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding ' + CAST(ColumnCount AS NVARCHAR(100)) + ' new column(s) to ' + T.[Schema] + '.' + T.[Name] +
                                  CASE WHEN RTRIM(ISNULL(VariantList, '')) <> '' THEN ' (variant: ' + VariantList + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' ADD ' + ScriptColumns + ';' AS NVARCHAR(MAX))
                           FROM (SELECT T.[Schema], T.[Name],
                                        ScriptColumns = STUFF((SELECT ', ' + CAST(c.[ColumnScript] AS NVARCHAR(MAX)) FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) <> '' ORDER BY c.[ColumnName] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''),
                                        ColumnCount = (SELECT COUNT(*) FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) <> ''),
                                        VariantList = STUFF((SELECT ', ' + CAST(REPLACE(RTRIM(c.[VariantName]), '''', '''''') AS NVARCHAR(MAX))
                                                               FROM #Columns C WITH (NOLOCK)
                                                               WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) <> ''
                                                                 AND RTRIM(ISNULL(c.[VariantName], '')) <> '' FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
                                   FROM #Tables T WITH (NOLOCK)
                                   WHERE EXISTS (SELECT * FROM #Columns c WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) <> '')) T
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- Object-change audit (#243 E5): one row per computed column added (the ALTER above folds a
  -- table's new computed columns into one statement, so this cannot weave into it).
  IF @WhatIf = 0
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'column', c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName], 'created'
        FROM #Columns c WITH (NOLOCK)
        WHERE c.NewColumn = 1 AND RTRIM(ISNULL(c.[ComputedExpression], '')) <> ''

  -- Filegroup placement (#filegroups): a NEW index/constraint declaring a filegroup name that does not
  -- exist on this target must fail loudly BEFORE any DDL runs, naming both the index and the filegroup --
  -- same contract as the table-create check in MissingTableAndColumnQuench.sql. Only indexes not yet
  -- present are checked; an already-existing index's declared vs. deployed filegroup is the "move"
  -- question, handled in ModifiedTableQuench.
  RAISERROR('Validate declared index filegroups exist', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #Indexes i WITH (NOLOCK)
               WHERE i.[FileGroup] IS NOT NULL
                 AND NOT EXISTS (SELECT * FROM sys.filegroups fg WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(i.[FileGroup]))
                 AND NOT EXISTS (SELECT * FROM sys.indexes si WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])))
  BEGIN
    DECLARE @v_IdxFGIndex NVARCHAR(1510), @v_IdxFGName NVARCHAR(500)
    SELECT TOP 1 @v_IdxFGIndex = i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName], @v_IdxFGName = i.[FileGroup]
      FROM #Indexes i WITH (NOLOCK)
      WHERE i.[FileGroup] IS NOT NULL
        AND NOT EXISTS (SELECT * FROM sys.filegroups fg WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(i.[FileGroup]))
        AND NOT EXISTS (SELECT * FROM sys.indexes si WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
    RAISERROR('Index %s declares filegroup %s, which does not exist on this database. SchemaSmith does not create filegroups -- create it on the target first, or correct the declared name.', 16, 1, @v_IdxFGIndex, @v_IdxFGName)
  END

  -- Partition placement (#partitioning, K1): the scheme must exist, same contract as the filegroup check
  -- above -- SchemaSmith creates neither a filegroup nor a partition function/scheme.
  RAISERROR('Validate declared index partition schemes exist', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #Indexes i WITH (NOLOCK)
               WHERE i.[PartitionScheme] IS NOT NULL
                 AND NOT EXISTS (SELECT * FROM sys.partition_schemes ps WHERE ps.[name] = SchemaSmith.fn_StripBracketWrapping(i.[PartitionScheme])))
  BEGIN
    DECLARE @v_IdxPsIndex NVARCHAR(1510), @v_IdxPsName NVARCHAR(500)
    SELECT TOP 1 @v_IdxPsIndex = i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName], @v_IdxPsName = i.[PartitionScheme]
      FROM #Indexes i WITH (NOLOCK)
      WHERE i.[PartitionScheme] IS NOT NULL
        AND NOT EXISTS (SELECT * FROM sys.partition_schemes ps WHERE ps.[name] = SchemaSmith.fn_StripBracketWrapping(i.[PartitionScheme]))
    RAISERROR('Index %s declares partition scheme %s, which does not exist on this database. SchemaSmith does not create partition functions or schemes -- create them on the target first, or correct the declared name.', 16, 1, @v_IdxPsIndex, @v_IdxPsName)
  END

  -- Both or neither, the index twin of the table-level pair check in MissingTableAndColumnQuench: ON
  -- <scheme> with no column is a syntax error naming nothing useful.
  IF EXISTS (SELECT 1 FROM #Indexes i WITH (NOLOCK)
              WHERE (i.[PartitionScheme] IS NOT NULL AND i.[PartitionColumn] IS NULL)
                 OR (i.[PartitionScheme] IS NULL AND i.[PartitionColumn] IS NOT NULL))
  BEGIN
    DECLARE @v_IdxPsHalf NVARCHAR(1510), @v_IdxPsHalfMissing NVARCHAR(30)
    SELECT TOP 1 @v_IdxPsHalf = i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName],
                 @v_IdxPsHalfMissing = CASE WHEN i.[PartitionColumn] IS NULL THEN 'PartitionColumn' ELSE 'PartitionScheme' END
      FROM #Indexes i WITH (NOLOCK)
      WHERE (i.[PartitionScheme] IS NOT NULL AND i.[PartitionColumn] IS NULL)
         OR (i.[PartitionScheme] IS NULL AND i.[PartitionColumn] IS NOT NULL)
    RAISERROR('Index %s declares one half of a partition placement but not the other -- %s is missing. PartitionScheme and PartitionColumn are declared together or not at all.', 16, 1, @v_IdxPsHalf, @v_IdxPsHalfMissing)
  END

  RAISERROR('Add Missing Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + 'RAISERROR(''  Creating ' + CASE WHEN i.PrimaryKey = 1 OR i.UniqueConstraint = 1 THEN 'constraint' ELSE 'index' END + ' ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + CASE WHEN RTRIM(ISNULL(i.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(i.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN i.PrimaryKey = 1 OR i.UniqueConstraint = 1
                                       THEN 'ALTER TABLE ' + i.[Schema] + '.' + i.[TableName] + ' ADD CONSTRAINT ' + i.[IndexName] +
                                            CASE WHEN i.PrimaryKey = 1 THEN ' PRIMARY KEY ' WHEN i.UniqueConstraint = 1 THEN ' UNIQUE ' END +
                                            CASE WHEN i.[Clustered] =  1 THEN '' ELSE 'NON' END + 'CLUSTERED (' + i.IndexColumns + ')' +
					                        CASE WHEN RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE')
                                                   OR ISNULL(i.[FillFactor], 100) NOT IN (0, 100)
                                                 THEN ' WITH (' +
                                                      CASE WHEN RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE') THEN 'DATA_COMPRESSION=' + i.[CompressionType] ELSE '' END +
                                                      CASE WHEN ISNULL(i.[FillFactor], 100) NOT IN (0, 100) 
                                                           THEN CASE WHEN RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE') THEN ', ' ELSE '' END +
                                                                'FILLFACTOR = ' + CAST(i.[FillFactor] AS NVARCHAR(20)) 
                                                           ELSE '' END +
                                                      CASE WHEN i.[IgnoreDuplicateKey] = 1 THEN ', IGNORE_DUP_KEY=ON' ELSE '' END +
                                                      CASE WHEN i.[PadIndex] = 1 THEN ', PAD_INDEX=ON' ELSE '' END +

                                                      -- XML_COMPRESSION rides the same WITH list. Leading comma is safe for the
                                                      -- same reason PAD_INDEX's is: CompressionType is ISNULL'd to 'NONE' in the
                                                      -- parse, so DATA_COMPRESSION always leads. Gated on 2022 by VALUE; only the
                                                      -- catalog READ needs kindle-time composition, because that names a column.
                                                      CASE WHEN i.[XmlCompression] = 1 AND SchemaSmith.fn_ServerMajorVersion() >= 16 THEN ', XML_COMPRESSION=ON' ELSE '' END +
							                          ')'
                                                 ELSE '' END +
                                            -- Filegroup placement (#filegroups): ON comes AFTER the WITH
                                            -- clause for ADD CONSTRAINT, per its own grammar (unlike CREATE
                                            -- TABLE, where ON precedes WITH). Existence validated above.
                                            CASE WHEN i.[PartitionScheme] IS NOT NULL THEN ' ON ' + i.[PartitionScheme] + '(' + i.[PartitionColumn] + ')'
                                                 WHEN i.[FileGroup] IS NOT NULL THEN ' ON ' + i.[FileGroup] ELSE '' END
                                       ELSE 'CREATE ' + 
                                            CASE WHEN i.[Unique] = 1 THEN 'UNIQUE ' ELSE '' END +
                                            CASE WHEN i.[Clustered] =  1 THEN '' ELSE 'NON' END + 'CLUSTERED ' +
                                            CASE WHEN i.[ColumnStore] = 1 THEN 'COLUMNSTORE ' ELSE '' END +
                                            'INDEX ' + i.[IndexName] +
                                            ' ON ' + i.[Schema] + '.' + i.[TableName] +
                                            CASE WHEN i.[ColumnStore] = 0 THEN ' (' + i.[IndexColumns] + ')' + CASE WHEN RTRIM(ISNULL(i.[IncludeColumns], '')) <> '' THEN ' INCLUDE (' + i.[IncludeColumns] + ')' ELSE '' END
                                            WHEN i.[ColumnStore] = 1 AND i.[Clustered] = 0 THEN ' (' + i.[IncludeColumns] + ')'
                                            ELSE '' END +
                                            CASE WHEN RTRIM(ISNULL(i.[FilterExpression], '')) <> '' THEN ' WHERE ' + i.[FilterExpression] ELSE '' END +
					                        CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                                                   OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                                                   OR ISNULL(i.[FillFactor], 100) NOT IN (0, 100)
                                                 THEN ' WITH (' +
                                                      CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                                                             OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                                                           THEN 'DATA_COMPRESSION=' + i.[CompressionType] ELSE '' END +
                                                      CASE WHEN ISNULL(i.[FillFactor], 100) NOT IN (0, 100) 
                                                           THEN CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                                                                       OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                                                                     THEN ', ' ELSE '' END +
                                                                'FILLFACTOR = ' + CAST(i.[FillFactor] AS NVARCHAR(20)) 
                                                           ELSE '' END +
                                                      CASE WHEN i.[IgnoreDuplicateKey] = 1 THEN ', IGNORE_DUP_KEY=ON' ELSE '' END +
                                                      CASE WHEN i.[PadIndex] = 1 THEN ', PAD_INDEX=ON' ELSE '' END +

                                                      -- XML_COMPRESSION rides the same WITH list. Leading comma is safe for the
                                                      -- same reason PAD_INDEX's is: CompressionType is ISNULL'd to 'NONE' in the
                                                      -- parse, so DATA_COMPRESSION always leads. Gated on 2022 by VALUE; only the
                                                      -- catalog READ needs kindle-time composition, because that names a column.
                                                      CASE WHEN i.[XmlCompression] = 1 AND SchemaSmith.fn_ServerMajorVersion() >= 16 THEN ', XML_COMPRESSION=ON' ELSE '' END +
							                          ')'
                                                 ELSE '' END +
                                            -- Filegroup placement (#filegroups): ON comes AFTER the WITH
                                            -- clause for CREATE INDEX too. Existence validated above.
                                            CASE WHEN i.[PartitionScheme] IS NOT NULL THEN ' ON ' + i.[PartitionScheme] + '(' + i.[PartitionColumn] + ')'
                                                 WHEN i.[FileGroup] IS NOT NULL THEN ' ON ' + i.[FileGroup] ELSE '' END
                                       END + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + CASE WHEN i.PrimaryKey = 1 OR i.UniqueConstraint = 1 THEN 'constraint' ELSE 'index' END + ''', ''' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + ''', ''created'');'
    FROM #Indexes i WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT *
                        FROM sys.indexes si
                        WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                          AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
    ORDER BY i.[Schema], i.[TableName], CASE WHEN i.[Clustered] =  1 THEN 0 ELSE 1 END, i.[IndexName]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'index'/'constraint' 'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, CASE WHEN i.PrimaryKey = 1 OR i.UniqueConstraint = 1 THEN 'constraint' ELSE 'index' END, i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName], 'wouldCreate'
        FROM #Indexes i WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT * FROM sys.indexes si
                            WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                              AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))

  RAISERROR('Add Missing Xml Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + 'RAISERROR(''  Creating index ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + CASE WHEN RTRIM(ISNULL(i.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(i.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'CREATE ' + CASE WHEN i.IsPrimary = 1 THEN 'PRIMARY ' ELSE '' END + 
                                  'XML INDEX ' + i.[IndexName] COLLATE DATABASE_DEFAULT + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' (' + i.[Column] + ')' +
                                  CASE WHEN i.IsPrimary = 0 THEN ' USING XML INDEX ' + i.PrimaryIndex + ' FOR ' + i.SecondaryIndexType ELSE '' END +
                                  ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''xmlIndex'', ''' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + ''', ''created'');'
    FROM #XmlIndexes i WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT *
                        FROM sys.xml_indexes si
                        WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                          AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
    ORDER BY i.[Schema], i.[TableName], CASE WHEN i.IsPrimary =  1 THEN 0 ELSE 1 END, i.[IndexName]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'xmlIndex'/'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'xmlIndex', i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName], 'wouldCreate'
        FROM #XmlIndexes i WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT * FROM sys.xml_indexes si
                            WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                              AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))

  RAISERROR('Turn on Temporal Tracking for tables defined as temporal', 10, 100) WITH NOWAIT
  -- HISTORY_TABLE + HISTORY_RETENTION_PERIOD only take effect here on the transition to versioned; see the
  -- reissue block below for an already-versioned table. ISNULL falls back to today's own-schema/<Table>_Hist
  -- default, so an IsTemporal-only package (HistoryTableSchema/Name both NULL) emits byte-for-byte the same
  -- ALTER as before this change. HistoryRetentionPeriod arrives already normalized to a canonical
  -- plural-unit form from the parse step (fn_NormalizeTemporalRetentionPeriod), so it is used as-is.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Turn ON Temporal Tracking for ' + T.[Schema] + '.' + T.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' ADD [ValidFrom] DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL DEFAULT ''0001-01-01 00:00:00.0000000'', ' +
                                                                                      '[ValidTo] DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL DEFAULT ''9999-12-31 23:59:59.9999999'', ' +
                                                                                      'PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo);' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = ' +
                                       ISNULL(T.[HistoryTableSchema], T.[Schema]) + '.[' + ISNULL(SchemaSmith.fn_StripBracketWrapping(T.[HistoryTableName]), SchemaSmith.fn_StripBracketWrapping(T.[Name]) + '_Hist') + ']' +
                                       CASE WHEN RTRIM(ISNULL(T.[HistoryRetentionPeriod], '')) <> '' THEN ', HISTORY_RETENTION_PERIOD = ' + T.[HistoryRetentionPeriod] ELSE '' END +
                                       '));' AS NVARCHAR(MAX))
                           FROM #Tables T WITH (NOLOCK)
                           WHERE t.IsTemporal = 1
                             AND OBJECTPROPERTY(OBJECT_ID([Schema] + '.' + [Name]), 'TableTemporalType') = 0
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #depth-gap: re-declaring HISTORY_TABLE on an already-versioned table needs no live-state pre-check --
  -- verified against a live SQL Server: re-issuing the SAME history table is a silent no-op (no error, no
  -- state change), and a DIFFERENT one raises SQL Server's own clear, actionable error (Msg 13757,
  -- "Temporal table '...' already has history table defined. Consider dropping system_versioning first if
  -- you want to use different history table."). That message names the table, states the cause, and gives
  -- the remedy -- strictly better than a hand-written check, so the engine is left to be the drift
  -- detector rather than reading live catalog state ourselves to pre-validate it. No 2016+ version gate is
  -- needed either: OBJECTPROPERTY(...,'TableTemporalType') itself is safe on every SQL Server version
  -- (0/NULL pre-2016, where DegradeUnsupportedFeatures has already forced IsTemporal off).
  RAISERROR('Reconcile history table for already-versioned temporal tables', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = ' +
                                       ISNULL(T.[HistoryTableSchema], T.[Schema]) + '.[' + ISNULL(SchemaSmith.fn_StripBracketWrapping(T.[HistoryTableName]), SchemaSmith.fn_StripBracketWrapping(T.[Name]) + '_Hist') + ']' +
                                       '));' AS NVARCHAR(MAX))
                           FROM #Tables T WITH (NOLOCK)
                           WHERE T.IsTemporal = 1
                             AND OBJECTPROPERTY(OBJECT_ID(T.[Schema] + '.' + T.[Name]), 'TableTemporalType') = 2
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- HISTORY_RETENTION_PERIOD alone IS documented as safely alterable on an already-versioned table (no
  -- OFF/ON cycle, no data at risk). Unlike the history table identity above, this DOES compare against
  -- live state first -- re-issuing the same retention every quench would be indistinguishable from a real
  -- change in the ChangeAudit/log output, so an unchanged retention must stay a true no-op deploy-to-deploy
  -- (see TableQuench_TemporalHistoryAndRetentionTests.TableQuench_RetentionPeriodDeployIsIdempotent).
  -- history_retention_period(_unit_desc) are SQL Server 2017 columns, NOT 2016 -- system-versioned tables
  -- are 2016 but a retention policy on them is 2017 -- so this gates at fn_ServerMajorVersion()>=14, not
  -- >=13. At >=13 an ordinary deploy to a genuine 2016 binary failed with "Invalid column name
  -- 'history_retention_period_unit_desc'" (unconditionally: the column binds for the whole statement even
  -- when no table is system-versioned). Covered by Sql2016TemporalRetentionGateTests, which runs only on
  -- major 13 -- the one version that reproduces it. The dynamic read (same pattern as ModifiedTableQuench's
  -- #RemovedTemporalHistory) also keeps this proc CREATEable on a genuine pre-2016 binary.
  -- The live value is canonicalized to the SAME plural-unit form
  -- fn_NormalizeTemporalRetentionPeriod produces at parse time. Reads history_retention_period_unit_desc
  -- ('DAY'/'WEEK'/'MONTH'/'YEAR'/'INFINITE') rather than the numeric history_retention_period_unit code --
  -- a first pass mapped the numeric codes from documentation (1/2/3/4) and got it wrong: measured live
  -- against a real server, DAY/WEEK/MONTH/YEAR are actually 3/4/5/6. The desc string needs no separately
  -- maintained code table (its 4 finite values pluralize by simple concatenation) and is what actually
  -- shipped correct. An ELSE this CASE cannot reach today (the 5 values above are exhaustive) still forces
  -- a loud Msg 245 rather than a silently-dropped-to-NULL retention if Microsoft ever adds a unit --
  -- CONVERT(INT, <text>) always fails on non-numeric text, and the outer CONVERT(NVARCHAR(50), ...) keeps
  -- this branch's static type matching its siblings so the CASE still compiles.
  RAISERROR('Update history retention period for already-versioned temporal tables', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#LiveTemporalRetention') IS NOT NULL DROP TABLE #LiveTemporalRetention
  CREATE TABLE #LiveTemporalRetention ([Schema] NVARCHAR(500) COLLATE DATABASE_DEFAULT NOT NULL, [Name] NVARCHAR(500) COLLATE DATABASE_DEFAULT NOT NULL,
                                        LiveRetentionText NVARCHAR(50) COLLATE DATABASE_DEFAULT NULL)
  IF SchemaSmith.fn_ServerMajorVersion() >= 14
    EXEC sp_executesql N'
      INSERT INTO #LiveTemporalRetention ([Schema], [Name], LiveRetentionText)
      SELECT T.[Schema], T.[Name],
             CASE mt.history_retention_period_unit_desc
               WHEN ''INFINITE'' THEN ''INFINITE''
               WHEN ''DAY'' THEN CAST(mt.history_retention_period AS NVARCHAR(10)) + '' DAYS''
               WHEN ''WEEK'' THEN CAST(mt.history_retention_period AS NVARCHAR(10)) + '' WEEKS''
               WHEN ''MONTH'' THEN CAST(mt.history_retention_period AS NVARCHAR(10)) + '' MONTHS''
               WHEN ''YEAR'' THEN CAST(mt.history_retention_period AS NVARCHAR(10)) + '' YEARS''
               ELSE CONVERT(NVARCHAR(50), CONVERT(INT, ''Unrecognized SYSTEM_VERSIONING retention unit: '' + ISNULL(mt.history_retention_period_unit_desc, CONVERT(NVARCHAR(20), mt.history_retention_period_unit))))
             END
        FROM #Tables T WITH (NOLOCK)
        JOIN sys.tables mt ON mt.[object_id] = OBJECT_ID(T.[Schema] + ''.'' + T.[Name]) AND mt.temporal_type = 2'

  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Updating history retention period for ' + T.[Schema] + '.' + T.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' SET (SYSTEM_VERSIONING = ON (HISTORY_RETENTION_PERIOD = ' +
                                       ISNULL(T.[HistoryRetentionPeriod], 'INFINITE') + '));' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''temporal'', ''' + T.[Schema] + '.' + T.[Name] + ' (retention)'', ''changed'');' AS NVARCHAR(MAX))
                           FROM #Tables T WITH (NOLOCK)
                           JOIN #LiveTemporalRetention L WITH (NOLOCK) ON L.[Schema] = T.[Schema] AND L.[Name] = T.[Name]
                           WHERE T.IsTemporal = 1
                             AND ISNULL(T.[HistoryRetentionPeriod], 'INFINITE') <> L.LiveRetentionText
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'temporal'/'changed' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'temporal', T.[Schema] + '.' + T.[Name] + ' (retention)', 'wouldChange'
        FROM #Tables T WITH (NOLOCK)
        JOIN #LiveTemporalRetention L WITH (NOLOCK) ON L.[Schema] = T.[Schema] AND L.[Name] = T.[Name]
        WHERE T.IsTemporal = 1
          AND ISNULL(T.[HistoryRetentionPeriod], 'INFINITE') <> L.LiveRetentionText

  RAISERROR('Add missing ProductName extended property to indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('EXEC sp_addextendedproperty @name = N''ProductName'', @value = ''' + @ProductName + ''', ' +
                                                              '@level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ' +
                                                              '@level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', ' +
                                                              '@level2type = N''Index'', @level2name = ''' + SchemaSmith.fn_StripBracketWrapping(i.IndexName) + ''';' AS NVARCHAR(MAX))
                           FROM #Indexes i WITH (NOLOCK)
                           JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = i.[Schema] AND t.[Name] = i.[TableName]
                           WHERE INDEXPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name]), SchemaSmith.fn_StripBracketWrapping(i.IndexName), 'IndexID') IS NOT NULL
                             AND t.[MemoryOptimized] = 0  -- memory-optimized indexes are inline and reject extended properties; they live and die with the table
                             AND NOT EXISTS (SELECT * FROM #IndexProperties ip WITH (NOLOCK) WHERE i.[Schema] = ip.[Schema] AND i.TableName = ip.TableName AND SchemaSmith.fn_StripBracketWrapping(i.IndexName) = ip.IndexName AND ip.PropertyName = 'ProductName')
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Add missing ProductName extended property to xml indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('EXEC sp_addextendedproperty @name = N''ProductName'', @value = ''' + @ProductName + ''', ' +
                                                              '@level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ' +
                                                              '@level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', ' +
                                                              '@level2type = N''Index'', @level2name = ''' + SchemaSmith.fn_StripBracketWrapping(i.IndexName) + ''';' AS NVARCHAR(MAX))
                           FROM #XmlIndexes i WITH (NOLOCK)
                           JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = i.[Schema] AND t.[Name] = i.[TableName]
                           WHERE INDEXPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name]), SchemaSmith.fn_StripBracketWrapping(i.IndexName), 'IndexID') IS NOT NULL
                             AND t.[MemoryOptimized] = 0  -- memory-optimized indexes are inline and reject extended properties; they live and die with the table
                             AND NOT EXISTS (SELECT * FROM #IndexProperties ip WITH (NOLOCK) WHERE i.[Schema] = ip.[Schema] AND i.TableName = ip.TableName AND SchemaSmith.fn_StripBracketWrapping(i.IndexName) = ip.IndexName AND ip.PropertyName = 'ProductName')
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Add Missing Statistics', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Creating statistics ' + s.[Schema] + '.' + s.[TableName] + '.' + s.[StatisticName] + CASE WHEN RTRIM(ISNULL(s.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(s.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'CREATE STATISTICS ' + s.[StatisticName] + ' ON ' + s.[Schema] + '.' + s.[TableName] + ' (' + s.[Columns] + ')' +
                                  CASE WHEN RTRIM(ISNULL(s.[FilterExpression], '')) <> '' THEN ' WHERE ' + s.[FilterExpression] ELSE '' END +
                                  ' WITH SAMPLE ' + CAST(ISNULL(s.[SampleSize], 100) AS NVARCHAR(20)) + ' PERCENT;' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''statistic'', ''' + s.[Schema] + '.' + s.[TableName] + '.' + s.[StatisticName] + ''', ''created'');' AS NVARCHAR(MAX))
                           FROM #Statistics s WITH (NOLOCK)
                           WHERE NOT EXISTS (SELECT *
                                               FROM sys.stats ss
                                               WHERE ss.[object_id] = OBJECT_ID(s.[Schema] + '.' + s.[TableName])
                                                 AND ss.[name] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'statistic'/'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'statistic', s.[Schema] + '.' + s.[TableName] + '.' + s.[StatisticName], 'wouldCreate'
        FROM #Statistics s WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT * FROM sys.stats ss
                            WHERE ss.[object_id] = OBJECT_ID(s.[Schema] + '.' + s.[TableName])
                              AND ss.[name] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]))

  RAISERROR('Add Missing Defaults', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Altering Column ' + c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + c.[Schema] + '.' + c.[TableName] + ' ADD DEFAULT ' + c.[Default] + ' FOR ' + c.[ColumnName] + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''constraint'', ''' + c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] + ' (default)'', ''created'');' AS NVARCHAR(MAX))
                           FROM #Columns c WITH (NOLOCK)
                           WHERE RTRIM(ISNULL(c.[Default], '')) <> ''
                             AND NOT EXISTS (SELECT *
                                               FROM sys.default_constraints dc
                                               WHERE dc.[parent_object_id] = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
                                                 AND COL_NAME(dc.parent_object_id, dc.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(c.ColumnName))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded default-constraint 'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'constraint', c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] + ' (default)', 'wouldCreate'
        FROM #Columns c WITH (NOLOCK)
        WHERE RTRIM(ISNULL(c.[Default], '')) <> ''
          AND NOT EXISTS (SELECT * FROM sys.default_constraints dc
                            WHERE dc.[parent_object_id] = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
                              AND COL_NAME(dc.parent_object_id, dc.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(c.ColumnName))

  RAISERROR('Add Missing Check Constraints', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding check constraint ' + cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ConstraintName] + CASE WHEN RTRIM(ISNULL(cc.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(cc.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + cc.[Schema] + '.' + cc.[TableName] + ' ADD CONSTRAINT [' + SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]) + '] CHECK (' + cc.[Expression] + ');' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''constraint'', ''' + cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ConstraintName] + ''', ''created'');' AS NVARCHAR(MAX))
                           FROM #CheckConstraints cc WITH (NOLOCK)
                           WHERE NOT EXISTS (SELECT *
                                               FROM sys.check_constraints sc
                                               WHERE sc.[parent_object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
                                                 AND sc.[name] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded check-constraint 'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'constraint', cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ConstraintName], 'wouldCreate'
        FROM #CheckConstraints cc WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT * FROM sys.check_constraints sc
                            WHERE sc.[parent_object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
                              AND sc.[name] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]))

  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding check constrain to column ' + c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + c.[Schema] + '.' + c.[TableName] + ' ADD CHECK (' + c.[CheckExpression] + ');' AS NVARCHAR(MAX))
                           FROM #Columns c WITH (NOLOCK)
                           WHERE RTRIM(ISNULL(c.[CheckExpression], '')) <> ''
                             AND NOT EXISTS (SELECT *
                                               FROM sys.check_constraints sc
                                               WHERE sc.[parent_object_id] = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
                                                 AND COL_NAME(sc.parent_object_id, sc.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Add Missing FullText Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding fulltext index on ' + fi.[Schema] + '.' + fi.[TableName] + CASE WHEN RTRIM(ISNULL(fi.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(fi.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'CREATE FULLTEXT INDEX ON ' + fi.[Schema] + '.' + fi.[TableName] + ' (' + [Columns] + ') KEY INDEX ' + [KeyIndex] + ' ON ' + [FullTextCatalog] +
                                  ' WITH CHANGE_TRACKING = ' + [ChangeTracking] +
                                  CASE WHEN RTRIM(ISNULL(fi.[StopList], '')) <> '' THEN ', STOPLIST = ' + [StopList] ELSE '' END + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''fullTextIndex'', ''' + fi.[Schema] + '.' + fi.[TableName] + ''', ''created'');' AS NVARCHAR(MAX))
                           FROM #FullTextIndexes fi WITH (NOLOCK)
                           WHERE NOT EXISTS (SELECT * FROM sys.fulltext_indexes ft WHERE ft.[object_id] = OBJECT_ID(fi.[Schema] + '.' + fi.[TableName]))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'fullTextIndex'/'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'fullTextIndex', fi.[Schema] + '.' + fi.[TableName], 'wouldCreate'
        FROM #FullTextIndexes fi WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT * FROM sys.fulltext_indexes ft WHERE ft.[object_id] = OBJECT_ID(fi.[Schema] + '.' + fi.[TableName]))

  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
  RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH
