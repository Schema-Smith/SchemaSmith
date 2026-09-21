-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- WORKING-SET SHAPES ARE DECLARED, NOT INFERRED (2026-09-20). Each temp table below is an explicit
-- CREATE TABLE + INSERT rather than a SELECT ... INTO. Two reasons, and the second is the one that
-- matters: SQL owns the shapes so a second ingestion path cannot silently desync from this one, and a
-- PARAMETERIZED insert cannot reach a session-scoped temp table that does not already exist --
-- sp_executesql runs in a nested scope, where SELECT ... INTO #X creates a table that dies with the
-- scope (Msg 208 on the next phase call). That is the prerequisite for bulk-loading the working set
-- from C# instead of shipping the whole model as inlined command text.
--
-- The shapes were MEASURED from tempdb against the SELECT ... INTO forms these replace, verified
-- payload-independent, and the converted script re-measured byte-for-byte identical. Do not hand-edit a
-- type here to "fix" something; re-measure.
--
-- ParseTableXmlIntoTempTables.sql is DELIBERATELY left on SELECT ... INTO. That path serves targets
-- below the OPENJSON cliff and the bulk design bypasses both encodings, so it gains nothing from this
-- and is expensive to certify (it needs the genuine old-binary sweep). The asymmetry is a decision, not
-- an oversight.

  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON
  RAISERROR('Parse Tables from Json', 10, 100) WITH NOWAIT

  -- I5: missing/blank [Schema] is a programmer error after slice-1's SchemaDefaultResolver.
  -- The canonical Load path fills Schema with the platform default ('dbo' / 'public') or the
  -- {{SchemaName}} token; a blank value here means a caller built the JSON without going
  -- through Template.Load (or a downstream substitution swallowed the token). Silently
  -- defaulting to dbo here is data-loss-equivalent for schema templates — fail loud.
  IF EXISTS (SELECT 1 FROM OPENJSON(@TableDefinitions) WITH ([Schema] NVARCHAR(500) '$.Schema', [Name] NVARCHAR(500) '$.Name')
                 WHERE NULLIF(RTRIM(ISNULL([Schema], '')), '') IS NULL)
  BEGIN
    DECLARE @v_BadTable NVARCHAR(500) =
      (SELECT TOP 1 ISNULL([Name], '<unnamed>') FROM OPENJSON(@TableDefinitions) WITH ([Schema] NVARCHAR(500) '$.Schema', [Name] NVARCHAR(500) '$.Name')
         WHERE NULLIF(RTRIM(ISNULL([Schema], '')), '') IS NULL);
    DECLARE @v_Msg NVARCHAR(2000) = 'Table JSON is missing Schema for table ''' + @v_BadTable + '''. ' +
      'Schema must be populated before reaching ParseTableJsonIntoTempTables — this is a programmer error. ' +
      'In production the SchemaDefaultResolver fills Schema with the platform default or the {{SchemaName}} token; ' +
      'a blank value here means a caller bypassed Template.Load or substituted the token away.';
    THROW 51000, @v_Msg, 1;
  END

  DROP TABLE IF EXISTS #TableDefinitions
  -- [_RowId] gives each parsed row a unique identifier so the per-row ShouldApply DELETE
  -- below targets exactly the source row whose expression evaluated false. Without it,
  -- the DELETE matched on (Schema, Name) and would silently wipe both rows when two
  -- entries shared a name with mutually exclusive ShouldApply expressions.
  -- Merge note (2026-06-02): main added [_RowId] (kept) AND swapped the Schema column to
  -- ISNULL([Schema], 'dbo') as a silent-fallback. Schema-templates replaced silent fallback
  -- with explicit THROW upstream (lines 22-25 above), so the ISNULL is intentionally NOT
  -- applied here — strict-fail wins on schema-templates because [Schema] is guaranteed
  -- non-blank by the time we reach this SELECT.
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #TableDefinitions
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [Name] NVARCHAR(MAX) NULL,
    [CompressionType] NVARCHAR(100) NOT NULL,
    [XmlCompression] BIT NOT NULL,
    [IsTemporal] BIT NOT NULL,
    [UpdateFillFactor] BIT NOT NULL,
    [HistoryTableSchema] NVARCHAR(MAX) NULL,
    [HistoryTableName] NVARCHAR(MAX) NULL,
    [HistoryRetentionPeriod] NVARCHAR(50) NULL,
    [FileGroup] NVARCHAR(MAX) NULL,
    [PartitionScheme] NVARCHAR(MAX) NULL,
    [PartitionColumn] NVARCHAR(MAX) NULL,
    [FileStreamFileGroup] NVARCHAR(MAX) NULL,
    [TextImageFileGroup] NVARCHAR(MAX) NULL,
    [CdcFilegroup] NVARCHAR(MAX) NULL,
    [Indexes] NVARCHAR(MAX) NULL,
    [XmlIndexes] NVARCHAR(MAX) NULL,
    [Columns] NVARCHAR(MAX) NULL,
    [Statistics] NVARCHAR(MAX) NULL,
    [FullTextIndex] NVARCHAR(MAX) NULL,
    [ForeignKeys] NVARCHAR(MAX) NULL,
    [CheckConstraints] NVARCHAR(MAX) NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL,
    [GraphType] NVARCHAR(10) NULL,
    [Ledger] NVARCHAR(12) NULL,
    [MemoryOptimized] BIT NOT NULL,
    [Durability] NVARCHAR(20) NULL,
    [EnableCDC] BIT NOT NULL,
    [EnableChangeTracking] BIT NOT NULL,
    [TrackColumnsUpdated] BIT NOT NULL,
    [OldName] NVARCHAR(MAX) NULL,
    [DropColumnsRemovedFromProduct] BIT NULL,
    [DropForeignKeysRemovedFromProduct] BIT NULL,
    [DropCheckConstraintsRemovedFromProduct] BIT NULL,
    [DropExcludeConstraintsRemovedFromProduct] BIT NULL,
    [DropStatisticsRemovedFromProduct] BIT NULL,
    [DropIndexesRemovedFromProduct] BIT NULL,
    [RebuildPolicyMode] NVARCHAR(20) NULL,
    [RebuildPolicyThreshold] INT NULL,
    [RebuildPolicyOnOrderMismatch] BIT NULL,
    [RebuildPolicySpecified] BIT NULL,
    [PreventDrop] BIT NOT NULL
  )
  INSERT INTO #TableDefinitions ([_RowId], [Schema], [Name], [CompressionType], [XmlCompression], [IsTemporal], [UpdateFillFactor], [HistoryTableSchema], [HistoryTableName], [HistoryRetentionPeriod], [FileGroup], [PartitionScheme], [PartitionColumn], [FileStreamFileGroup], [TextImageFileGroup], [CdcFilegroup], [Indexes], [XmlIndexes], [Columns], [Statistics], [FullTextIndex], [ForeignKeys], [CheckConstraints], [ShouldApplyExpression], [VariantName], [GraphType], [Ledger], [MemoryOptimized], [Durability], [EnableCDC], [EnableChangeTracking], [TrackColumnsUpdated], [OldName], [DropColumnsRemovedFromProduct], [DropForeignKeysRemovedFromProduct], [DropCheckConstraintsRemovedFromProduct], [DropExcludeConstraintsRemovedFromProduct], [DropStatisticsRemovedFromProduct], [DropIndexesRemovedFromProduct], [RebuildPolicyMode], [RebuildPolicyThreshold], [RebuildPolicyOnOrderMismatch], [RebuildPolicySpecified], [PreventDrop])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         [Schema] = SchemaSmith.fn_SafeBracketWrap([Schema]), [Name] = SchemaSmith.fn_SafeBracketWrap([Name]), [CompressionType] = ISNULL(NULLIF(RTRIM([CompressionType]), ''), 'NONE'), [XmlCompression] = ISNULL([XmlCompression], 0),
         [IsTemporal] = ISNULL([IsTemporal], 0), [UpdateFillFactor] = ISNULL([UpdateFillFactor], 0),
         -- History table identity/retention (#depth-gap): schema/name left NULL (not defaulted here) so
         -- the apply-side quench can tell "unset -> use SchemaSmith's own <Table>_Hist default" apart from
         -- an explicit value. Retention is normalized here (singular unit -> plural, e.g. "5 YEAR" ->
         -- "5 YEARS") so a hand-authored singular form compares equal to the canonical plural form the
         -- live-state read and extraction both produce -- see fn_NormalizeTemporalRetentionPeriod.
         [HistoryTableSchema] = SchemaSmith.fn_SafeBracketWrap([HistoryTableSchema]), [HistoryTableName] = SchemaSmith.fn_SafeBracketWrap([HistoryTableName]), [HistoryRetentionPeriod] = SchemaSmith.fn_NormalizeTemporalRetentionPeriod([HistoryRetentionPeriod]),
         -- Filegroup placement (#filegroups): left NULL (not defaulted) when absent, same as
         -- HistoryTableSchema/Name above, so the apply side can tell "unset -> SQL Server's own default
         -- filegroup" apart from an explicit declaration.
         [FileGroup] = SchemaSmith.fn_SafeBracketWrap([FileGroup]),
         -- Partition placement (#partitioning): same null-means-unmanaged contract as [FileGroup] above.
         [PartitionScheme] = SchemaSmith.fn_SafeBracketWrap([PartitionScheme]), [PartitionColumn] = SchemaSmith.fn_SafeBracketWrap([PartitionColumn]),
         [FileStreamFileGroup] = SchemaSmith.fn_SafeBracketWrap([FileStreamFileGroup]),
         [TextImageFileGroup] = SchemaSmith.fn_SafeBracketWrap([TextImageFileGroup]),
         -- CDC change-table placement (#417): NULL means unmanaged; ModifiedTableQuench applies the template default.
         [CdcFilegroup] = SchemaSmith.fn_SafeBracketWrap([CdcFilegroup]),
         [Indexes], [XmlIndexes], [Columns], [Statistics], [FullTextIndex], [ForeignKeys], [CheckConstraints],
         [ShouldApplyExpression], [VariantName], [GraphType] = RTRIM(ISNULL([GraphType], 'None')), [Ledger] = RTRIM(ISNULL([Ledger], 'Off')), [MemoryOptimized] = ISNULL([MemoryOptimized], 0), [Durability] = UPPER(RTRIM(ISNULL(NULLIF([Durability], ''), 'SCHEMA_AND_DATA'))), [EnableCDC] = ISNULL([EnableCDC], 0), [EnableChangeTracking] = ISNULL([EnableChangeTracking], 0), [TrackColumnsUpdated] = ISNULL([TrackColumnsUpdated], 0), [OldName] = SchemaSmith.fn_SafeBracketWrap([OldName]),
         [DropColumnsRemovedFromProduct], [DropForeignKeysRemovedFromProduct], [DropCheckConstraintsRemovedFromProduct], [DropExcludeConstraintsRemovedFromProduct], [DropStatisticsRemovedFromProduct], [DropIndexesRemovedFromProduct],
         -- RebuildPolicy resolves MOST-SPECIFIC-WINS on the WHOLE object (ProductQuench.ResolveCascadedPolicy),
         -- so the apply side needs to know whether this table declared one AT ALL -- not just what its fields
         -- say. [RebuildPolicySpecified] is that sentinel, read from the presence of the OBJECT rather than
         -- from any field: a table declaring only { "Mode": "ALWAYS" } must NOT inherit a product-level
         -- Threshold, and a per-field COALESCE against the passed-in tier would graft one on. A JSON null
         -- ('"RebuildPolicy": null', which is what an undeclared policy serializes to) yields NULL for
         -- '$.RebuildPolicy' AS JSON in lax mode, so absent and null both read as not-specified.
         [RebuildPolicyMode], [RebuildPolicyThreshold], [RebuildPolicyOnOrderMismatch],
         [RebuildPolicySpecified] = CONVERT(BIT, CASE WHEN [RebuildPolicyJson] IS NOT NULL THEN 1 ELSE 0 END),
         [PreventDrop] = ISNULL([PreventDrop], 0)
    FROM OPENJSON(@TableDefinitions) WITH (
      [Schema] NVARCHAR(500) '$.Schema',
      [Name] NVARCHAR(500) '$.Name',
      [CompressionType] NVARCHAR(100) '$.CompressionType',
      [XmlCompression] BIT '$.XmlCompression',
      [IsTemporal] BIT '$.IsTemporal',
      [HistoryTableSchema] NVARCHAR(500) '$.HistoryTableSchema',
      [HistoryTableName] NVARCHAR(500) '$.HistoryTableName',
      [HistoryRetentionPeriod] NVARCHAR(50) '$.HistoryRetentionPeriod',
      [FileGroup] NVARCHAR(500) '$.FileGroup',
      [PartitionScheme] NVARCHAR(500) '$.PartitionScheme',
      [PartitionColumn] NVARCHAR(500) '$.PartitionColumn',
      [FileStreamFileGroup] NVARCHAR(500) '$.FileStreamFileGroup',
      [TextImageFileGroup] NVARCHAR(500) '$.TextImageFileGroup',
      [UpdateFillFactor] BIT '$.UpdateFillFactor',
      [OldName] NVARCHAR(500) '$.OldName',
	  [Indexes] NVARCHAR(MAX) '$.Indexes' AS JSON,
	  [XmlIndexes] NVARCHAR(MAX) '$.XmlIndexes' AS JSON,
      [Columns] NVARCHAR(MAX) '$.Columns' AS JSON,
	  [Statistics] NVARCHAR(MAX) '$.Statistics' AS JSON,
	  [FullTextIndex] NVARCHAR(MAX) '$.FullTextIndex' AS JSON,
      [ForeignKeys] NVARCHAR(MAX) '$.ForeignKeys' AS JSON,
      [CheckConstraints] NVARCHAR(MAX) '$.CheckConstraints' AS JSON,
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName',
      [EnableCDC] BIT '$.EnableCDC',
      [CdcFilegroup] NVARCHAR(500) '$.CdcFilegroup',
      [GraphType] NVARCHAR(10) '$.GraphType',
      [Ledger] NVARCHAR(12) '$.Ledger',
      [MemoryOptimized] BIT '$.MemoryOptimized',
      [Durability] NVARCHAR(20) '$.Durability',
      [EnableChangeTracking] BIT '$.EnableChangeTracking',
      [TrackColumnsUpdated] BIT '$.TrackColumnsUpdated',
      [DropColumnsRemovedFromProduct] BIT '$.DropColumnsRemovedFromProduct',
      [DropForeignKeysRemovedFromProduct] BIT '$.DropForeignKeysRemovedFromProduct',
      [DropCheckConstraintsRemovedFromProduct] BIT '$.DropCheckConstraintsRemovedFromProduct',
      [DropExcludeConstraintsRemovedFromProduct] BIT '$.DropExcludeConstraintsRemovedFromProduct',
      [DropStatisticsRemovedFromProduct] BIT '$.DropStatisticsRemovedFromProduct',
      [DropIndexesRemovedFromProduct] BIT '$.DropIndexesRemovedFromProduct',
      [RebuildPolicyMode] NVARCHAR(20) '$.RebuildPolicy.Mode',
      [RebuildPolicyThreshold] INT '$.RebuildPolicy.Threshold',
      [RebuildPolicyOnOrderMismatch] BIT '$.RebuildPolicy.OnOrderMismatch',
      -- The OBJECT, read only to answer "did this table declare a policy at all?" -- see the sentinel above.
      [RebuildPolicyJson] NVARCHAR(MAX) '$.RebuildPolicy' AS JSON,
      [PreventDrop] BIT '$.PreventDrop'
      ) t;
  
  -- Identify Tables to skip based on ShouldApply expression
  -- Scoped by [_RowId] so each generated DELETE targets exactly the source row whose
  -- expression evaluated false (no collateral damage to siblings with the same Name).
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #TableDefinitions WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #TableDefinitions WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  DROP TABLE IF EXISTS #Tables
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #Tables
  (
    [Schema] NVARCHAR(MAX) NULL,
    [Name] NVARCHAR(MAX) NULL,
    [CompressionType] NVARCHAR(100) NOT NULL,
    [XmlCompression] BIT NOT NULL,
    [IsTemporal] BIT NOT NULL,
    [HistoryTableSchema] NVARCHAR(MAX) NULL,
    [HistoryTableName] NVARCHAR(MAX) NULL,
    [HistoryRetentionPeriod] NVARCHAR(50) NULL,
    [FileGroup] NVARCHAR(MAX) NULL,
    [PartitionScheme] NVARCHAR(MAX) NULL,
    [PartitionColumn] NVARCHAR(MAX) NULL,
    [FileStreamFileGroup] NVARCHAR(MAX) NULL,
    [TextImageFileGroup] NVARCHAR(MAX) NULL,
    [UpdateFillFactor] BIT NOT NULL,
    [EnableCDC] BIT NOT NULL,
    [CdcFilegroup] NVARCHAR(MAX) NULL,
    [EnableChangeTracking] BIT NOT NULL,
    [TrackColumnsUpdated] BIT NOT NULL,
    [GraphType] NVARCHAR(10) NULL,
    [Ledger] NVARCHAR(12) NULL,
    [MemoryOptimized] BIT NOT NULL,
    [Durability] NVARCHAR(20) NULL,
    [OldName] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL,
    [NewTable] BIT NULL,
    [DropColumnsRemovedFromProduct] BIT NULL,
    [DropForeignKeysRemovedFromProduct] BIT NULL,
    [DropCheckConstraintsRemovedFromProduct] BIT NULL,
    [DropExcludeConstraintsRemovedFromProduct] BIT NULL,
    [DropStatisticsRemovedFromProduct] BIT NULL,
    [DropIndexesRemovedFromProduct] BIT NULL,
    [RebuildPolicyMode] NVARCHAR(20) NULL,
    [RebuildPolicyThreshold] INT NULL,
    [RebuildPolicyOnOrderMismatch] BIT NULL,
    [RebuildPolicySpecified] BIT NULL,
    [PreventDrop] BIT NOT NULL
  )
  INSERT INTO #Tables ([Schema], [Name], [CompressionType], [XmlCompression], [IsTemporal], [HistoryTableSchema], [HistoryTableName], [HistoryRetentionPeriod], [FileGroup], [PartitionScheme], [PartitionColumn], [FileStreamFileGroup], [TextImageFileGroup], [UpdateFillFactor], [EnableCDC], [CdcFilegroup], [EnableChangeTracking], [TrackColumnsUpdated], [GraphType], [Ledger], [MemoryOptimized], [Durability], [OldName], [VariantName], [NewTable], [DropColumnsRemovedFromProduct], [DropForeignKeysRemovedFromProduct], [DropCheckConstraintsRemovedFromProduct], [DropExcludeConstraintsRemovedFromProduct], [DropStatisticsRemovedFromProduct], [DropIndexesRemovedFromProduct], [RebuildPolicyMode], [RebuildPolicyThreshold], [RebuildPolicyOnOrderMismatch], [RebuildPolicySpecified], [PreventDrop])
  SELECT [Schema], [Name], [CompressionType], [XmlCompression], [IsTemporal], [HistoryTableSchema], [HistoryTableName], [HistoryRetentionPeriod], [FileGroup], [PartitionScheme], [PartitionColumn], [FileStreamFileGroup], [TextImageFileGroup], [UpdateFillFactor], [EnableCDC], [CdcFilegroup], [EnableChangeTracking], [TrackColumnsUpdated], [GraphType], [Ledger], [MemoryOptimized], [Durability], [OldName], [VariantName],
         CONVERT(BIT, CASE WHEN OBJECT_ID([Schema] + '.' + [Name], 'U') IS NULL AND OBJECT_ID([Schema] + '.' + [OldName], 'U') IS NULL THEN 1 ELSE 0 END) AS NewTable,
         [DropColumnsRemovedFromProduct], [DropForeignKeysRemovedFromProduct], [DropCheckConstraintsRemovedFromProduct], [DropExcludeConstraintsRemovedFromProduct], [DropStatisticsRemovedFromProduct], [DropIndexesRemovedFromProduct],
         [RebuildPolicyMode], [RebuildPolicyThreshold], [RebuildPolicyOnOrderMismatch], [RebuildPolicySpecified],
         ISNULL([PreventDrop], 0) AS [PreventDrop]
    FROM #TableDefinitions WITH (NOLOCK);
  
  RAISERROR('Parse Columns from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #Columns
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #Columns
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [TableName] NVARCHAR(MAX) NULL,
    [ColumnName] NVARCHAR(MAX) NULL,
    [DataType] NVARCHAR(MAX) NULL,
    [Nullable] BIT NOT NULL,
    [NullableDeclared] BIT NULL,
    [Default] NVARCHAR(MAX) NULL,
    [CheckExpression] NVARCHAR(MAX) NULL,
    [ComputedExpression] NVARCHAR(MAX) NULL,
    [Persisted] BIT NOT NULL,
    [Sparse] BIT NOT NULL,
    [FileStream] BIT NOT NULL,
    [IsColumnSet] BIT NOT NULL,
    [BackfillExistingRows] BIT NOT NULL,
    [Collation] NVARCHAR(500) NULL,
    [DataMaskFunction] NVARCHAR(500) NULL,
    [EncryptionType] NVARCHAR(100) NOT NULL,
    [EncryptionKey] NVARCHAR(500) NULL,
    [EncryptionAlgorithm] NVARCHAR(500) NULL,
    [OldName] NVARCHAR(MAX) NULL,
    [NewColumn] BIT NULL,
    [ColumnScript] NVARCHAR(MAX) NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL
  )
  INSERT INTO #Columns ([_RowId], [Schema], [TableName], [ColumnName], [DataType], [Nullable], [NullableDeclared], [Default], [CheckExpression], [ComputedExpression], [Persisted], [Sparse], [FileStream], [IsColumnSet], [BackfillExistingRows], [Collation], [DataMaskFunction], [EncryptionType], [EncryptionKey], [EncryptionAlgorithm], [OldName], [NewColumn], [ColumnScript], [ShouldApplyExpression], [VariantName])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [ColumnName] = SchemaSmith.fn_SafeBracketWrap(c.[ColumnName]),
         -- Canonicalize the JSON DataType so the live-vs-declared comparison
         -- in ModifiedTableQuench (which builds USER_TYPE + DATETIME_PRECISION
         -- as e.g. "DATETIME2(7)") matches a JSON-declared "DATETIME2" without
         -- explicit precision. SQL Server defaults DATETIME2 / TIME /
         -- DATETIMEOFFSET to precision 7 — without canonicalization, every
         -- re-quench against a column declared with the default precision sees
         -- false drift and emits a destructive ALTER COLUMN that cascades to
         -- any dependent computed columns and indexes.
         [DataType] = CASE WHEN UPPER(LTRIM(RTRIM(SchemaSmith.fn_NormalizeDataType(c.[DataType])))) IN ('DATETIME2', 'TIME', 'DATETIMEOFFSET')
                            THEN UPPER(LTRIM(RTRIM(SchemaSmith.fn_NormalizeDataType(c.[DataType])))) + '(7)'
                            ELSE SchemaSmith.fn_NormalizeDataType(c.[DataType]) END,
         [Nullable] = ISNULL(c.[Nullable], 0),
         -- The value AS DECLARED, NULL when the package omitted it. A computed column's nullability is the
         -- engine's to derive unless the author states one, and only an explicit value may narrow it.
         [NullableDeclared] = c.[Nullable],
         c.[Default], c.[CheckExpression], c.[ComputedExpression], [Persisted] = ISNULL(c.[Persisted], 0),
         [Sparse] = ISNULL(c.[Sparse], 0), [FileStream] = ISNULL(c.[FileStream], 0), [IsColumnSet] = ISNULL(c.[IsColumnSet], 0), [BackfillExistingRows] = ISNULL(c.[BackfillExistingRows], 0), [Collation] = RTRIM(ISNULL(c.[Collation], '')), [DataMaskFunction] = RTRIM(ISNULL(c.[DataMaskFunction], '')),
         [EncryptionType] = ISNULL(c.[EncryptionType], 'NONE'), [EncryptionKey] = RTRIM(ISNULL(c.[EncryptionKey], '')), [EncryptionAlgorithm] = RTRIM(ISNULL(c.[EncryptionAlgorithm], '')),
         [OldName] = SchemaSmith.fn_SafeBracketWrap(c.[OldName]),
         CONVERT(BIT, CASE WHEN (RTRIM(ISNULL([ComputedExpression], '')) <> '' OR NOT EXISTS (SELECT * FROM #Tables x WHERE x.[Name] = t.[Name] AND x.[Schema] = t.[Schema] AND x.NewTable = 1))
                            AND COLUMNPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U'), SchemaSmith.fn_StripBracketWrapping([ColumnName]), 'ColumnId') IS NULL
                            -- Not a new column if it exists by current name in the table being renamed from (table rename scenario)
                            AND COLUMNPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[OldName], 'U'), SchemaSmith.fn_StripBracketWrapping([ColumnName]), 'ColumnId') IS NULL
                            -- Not a new column if the column's own OldName exists (column rename scenario)
                            AND COLUMNPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U'), SchemaSmith.fn_StripBracketWrapping(c.[OldName]), 'ColumnId') IS NULL
                           THEN 1 ELSE 0 END) AS NewColumn,
         SchemaSmith.fn_SafeBracketWrap(c.[ColumnName]) + ' ' +
         -- For computed columns only the expression is needed
         CASE WHEN RTRIM(ISNULL([ComputedExpression], '')) <> '' THEN 'AS (' + ComputedExpression + ')' + CASE WHEN ISNULL(c.[Persisted], 0) = 1 THEN ' PERSISTED' ELSE '' END
                                                                                                     -- A computed column is created NOT NULL only when the package says so. Defaulting an omitted Nullable to
                                                                                                     -- NOT NULL here dropped an existing nullable column and failed to put it back when a row's expression
                                                                                                     -- evaluated to NULL -- the deploy aborted with the column gone.
                                                                                                     + CASE WHEN ISNULL(c.[Persisted], 0) = 1 AND c.[Nullable] = 0 THEN ' NOT NULL' ELSE '' END
              -- A column set is an aggregating XML column: no COLLATE/SPARSE/MASKED/ENCRYPTED/NULL/DEFAULT
              -- clause is legal on it, and SQL Server only accepts adding one (a) at CREATE TABLE time or
              -- (b) via ALTER TABLE in the SAME statement as the sparse columns it aggregates -- both of
              -- which this proc already satisfies by batching a table's new columns into one CREATE/ADD
              -- (see MissingTableAndColumnQuench.sql). A column set added to a table that already has
              -- standalone sparse columns from a prior deploy is left to the engine's own (clear) rejection
              -- rather than pre-validated here.
              WHEN ISNULL([IsColumnSet], 0) = 1 THEN UPPER(SchemaSmith.fn_NormalizeDataType(c.[DataType])) + ' COLUMN_SET FOR ALL_SPARSE_COLUMNS'
              -- Otherwise build the column definition
              ELSE UPPER(SchemaSmith.fn_NormalizeDataType(c.[DataType])) +
                   CASE WHEN ISNULL([FileStream], 0) = 1 THEN ' FILESTREAM' ELSE '' END +
                   CASE WHEN RTRIM(ISNULL([Collation], '')) NOT IN ('IGNORE', '') THEN ' COLLATE ' + [Collation] ELSE '' END +
                   CASE WHEN ISNULL([Sparse], 0) = 1 THEN ' SPARSE' ELSE '' END +
                   -- MASKED WITH / ENCRYPTED WITH are 2016 (major 13). The column DDL is assembled here at parse
                   -- time, so this is the one place the create-path emit is suppressed below the floor;
                   -- DegradeUnsupportedFeatures reports the downgrade and neutralizes the source columns for the
                   -- ALTER/detection passes. Kept gate-consistent with that proc's < 13 check.
                   CASE WHEN RTRIM(ISNULL([DataMaskFunction], '')) <> '' AND SchemaSmith.fn_ServerMajorVersion() >= 13 THEN ' MASKED WITH (FUNCTION = ''' + [DataMaskFunction] + ''')' ELSE '' END +
                   CASE WHEN RTRIM(ISNULL([EncryptionType], 'NONE')) <> 'NONE' AND SchemaSmith.fn_ServerMajorVersion() >= 13
                        THEN ' ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = ' + [EncryptionKey] + ', ENCRYPTION_TYPE = ' + [EncryptionType] + ', ALGORITHM = ''' + [EncryptionAlgorithm] + ''')'
                        ELSE '' END +
                   CASE WHEN ISNULL(Nullable, 0) = 1 THEN ' NULL' ELSE ' NOT NULL' END +
                   CASE WHEN RTRIM(ISNULL([Default], '')) <> '' THEN ' DEFAULT ' + [Default] ELSE '' END
              END AS [ColumnScript],
         c.[ShouldApplyExpression], c.[VariantName]
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON(Columns) WITH (
      [ColumnName] NVARCHAR(500) '$.Name',
      [DataType] NVARCHAR(100) '$.DataType',
      [Nullable] BIT '$.Nullable',
      [Default] NVARCHAR(MAX) '$.Default',
      [CheckExpression] NVARCHAR(MAX) '$.CheckExpression',
      [ComputedExpression] NVARCHAR(MAX) '$.ComputedExpression',
      [Persisted] BIT '$.Persisted',
      [Sparse] BIT '$.Sparse',
      [FileStream] BIT '$.FileStream',
      [IsColumnSet] BIT '$.IsColumnSet',
      [BackfillExistingRows] BIT '$.BackfillExistingRows',
      [Collation] NVARCHAR(500) '$.Collation',
      [DataMaskFunction] NVARCHAR(500) '$.DataMaskFunction',
      [EncryptionType] NVARCHAR(100) '$.EncryptionType',
      [EncryptionKey] NVARCHAR(500) '$.EncryptionKey',
      [EncryptionAlgorithm] NVARCHAR(500) '$.EncryptionAlgorithm',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName',
      [OldName] NVARCHAR(500) '$.OldName'
      ) c;

  -- Identify Columns to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #Columns WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Columns WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  -- Don't try to apply tables without columns
  DELETE FROM #Tables
    WHERE NOT EXISTS (SELECT * FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = #Tables.[Schema] AND C.[TableName] = #Tables.[Name])
  DELETE FROM #TableDefinitions
    WHERE NOT EXISTS (SELECT * FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = #TableDefinitions.[Schema] AND C.[TableName] = #TableDefinitions.[Name])

  RAISERROR('Parse Indexes from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #Indexes
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #Indexes
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [TableName] NVARCHAR(MAX) NULL,
    [IndexName] NVARCHAR(MAX) NULL,
    [CompressionType] NVARCHAR(100) NOT NULL,
    [XmlCompression] BIT NOT NULL,
    [PrimaryKey] BIT NOT NULL,
    [Unique] INT NULL,
    [UniqueConstraint] BIT NOT NULL,
    [Clustered] BIT NOT NULL,
    [ColumnStore] BIT NOT NULL,
    [FillFactor] TINYINT NOT NULL,
    [FilterExpression] NVARCHAR(MAX) NULL,
    [FileGroup] NVARCHAR(MAX) NULL,
    [PartitionScheme] NVARCHAR(MAX) NULL,
    [PartitionColumn] NVARCHAR(MAX) NULL,
    [BucketCount] INT NULL,
    [UpdateFillFactor] BIT NULL,
    [IndexColumns] NVARCHAR(MAX) NULL,
    [IncludeColumns] NVARCHAR(MAX) NULL,
    [IgnoreDuplicateKey] BIT NOT NULL,
    [PadIndex] BIT NOT NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL
  )
  INSERT INTO #Indexes ([_RowId], [Schema], [TableName], [IndexName], [CompressionType], [XmlCompression], [PrimaryKey], [Unique], [UniqueConstraint], [Clustered], [ColumnStore], [FillFactor], [FilterExpression], [FileGroup], [PartitionScheme], [PartitionColumn], [BucketCount], [UpdateFillFactor], [IndexColumns], [IncludeColumns], [IgnoreDuplicateKey], [PadIndex], [ShouldApplyExpression], [VariantName])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [IndexName] = SchemaSmith.fn_SafeBracketWrap(i.[IndexName]), [CompressionType] = ISNULL(NULLIF(RTRIM(i.[CompressionType]), ''), 'NONE'), [XmlCompression] = ISNULL(i.[XmlCompression], 0), [PrimaryKey] = ISNULL(i.[PrimaryKey], 0),
         [Unique] = COALESCE(NULLIF(i.[Unique], 0), NULLIF(i.[PrimaryKey], 0), i.[UniqueConstraint], 0),
         [UniqueConstraint] = ISNULL(i.[UniqueConstraint], 0), [Clustered] = ISNULL(i.[Clustered], 0), [ColumnStore] = ISNULL(i.[ColumnStore], 0), [FillFactor] = ISNULL(NULLIF(i.[FillFactor], 0), 100),
         i.[FilterExpression], [FileGroup] = SchemaSmith.fn_SafeBracketWrap(i.[FileGroup]),
         [PartitionScheme] = SchemaSmith.fn_SafeBracketWrap(i.[PartitionScheme]), [PartitionColumn] = SchemaSmith.fn_SafeBracketWrap(i.[PartitionColumn]), [BucketCount] = i.[BucketCount], [UpdateFillFactor] = CONVERT(BIT, CASE WHEN @UpdateFillFactor = 1 OR t.[UpdateFillFactor] = 1 OR i.[UpdateFillFactor] = 1 THEN 1 ELSE 0 END),
         [IndexColumns] = (SELECT STRING_AGG(CAST(CASE WHEN RTRIM([value]) LIKE '% DESC' 
                                                       THEN SchemaSmith.fn_SafeBracketWrap(SUBSTRING(RTRIM([value]), 1, LEN(RTRIM([value])) - 5)) + ' DESC'
                                                       ELSE SchemaSmith.fn_SafeBracketWrap([value])
                                                       END AS NVARCHAR(MAX)), ',') 
                             FROM STRING_SPLIT(i.[IndexColumns], ',') 
                             WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         [IncludeColumns] = (SELECT STRING_AGG(SchemaSmith.fn_SafeBracketWrap([value]), ',') WITHIN GROUP (ORDER BY SchemaSmith.fn_SafeBracketWrap([value]))
                               FROM STRING_SPLIT(i.[IncludeColumns], ',') 
                               WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         [IgnoreDuplicateKey] = ISNULL(i.[IgnoreDuplicateKey], 0), [PadIndex] = ISNULL(i.[PadIndex], 0),
         i.[ShouldApplyExpression], i.[VariantName]
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON(Indexes) WITH (
      [IndexName] NVARCHAR(500) '$.Name',
      [CompressionType] NVARCHAR(100) '$.CompressionType',
      [XmlCompression] BIT '$.XmlCompression',
      [PrimaryKey] BIT '$.PrimaryKey',
      [Unique] BIT '$.Unique',
	  [UniqueConstraint] BIT '$.UniqueConstraint',
      [Clustered] BIT '$.Clustered',
      [ColumnStore] BIT '$.ColumnStore',
      [IgnoreDuplicateKey] BIT '$.IgnoreDuplicateKey',
      [PadIndex] BIT '$.PadIndex',
      [FillFactor] TINYINT '$.FillFactor',
      [FilterExpression] NVARCHAR(MAX) '$.FilterExpression',
      [IndexColumns] NVARCHAR(MAX) '$.IndexColumns',
      [IncludeColumns] NVARCHAR(MAX) '$.IncludeColumns',
      [FileGroup] NVARCHAR(500) '$.FileGroup',
      [PartitionScheme] NVARCHAR(500) '$.PartitionScheme',
      [PartitionColumn] NVARCHAR(500) '$.PartitionColumn',
      [BucketCount] INT '$.BucketCount',
      [UpdateFillFactor] BIT '$.UpdateFillFactor',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) i;
  
  -- Identify Indexes to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #Indexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Indexes WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)
  
  RAISERROR('Parse XML Indexes from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #XmlIndexes
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #XmlIndexes
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [TableName] NVARCHAR(MAX) NULL,
    [IndexName] NVARCHAR(MAX) NULL,
    [IsPrimary] BIT NULL,
    [Column] NVARCHAR(MAX) NULL,
    [PrimaryIndex] NVARCHAR(MAX) NULL,
    [SecondaryIndexType] NVARCHAR(500) NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL
  )
  INSERT INTO #XmlIndexes ([_RowId], [Schema], [TableName], [IndexName], [IsPrimary], [Column], [PrimaryIndex], [SecondaryIndexType], [ShouldApplyExpression], [VariantName])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [IndexName] = SchemaSmith.fn_SafeBracketWrap(i.[IndexName]), i.[IsPrimary],
         [Column] = SchemaSmith.fn_SafeBracketWrap(i.[Column]), [PrimaryIndex] = SchemaSmith.fn_SafeBracketWrap(i.[PrimaryIndex]),
         i.[SecondaryIndexType], i.[ShouldApplyExpression], i.[VariantName]
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON(XmlIndexes) WITH (
      [IndexName] NVARCHAR(500) '$.Name',
      [IsPrimary] BIT '$.IsPrimary',
      [Column] NVARCHAR(500) '$.Column',
      [PrimaryIndex] NVARCHAR(500) '$.PrimaryIndex',
	  [SecondaryIndexType] NVARCHAR(500) '$.SecondaryIndexType',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) i;

  -- Identify XmlIndexes to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #XmlIndexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #XmlIndexes WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)
  
  RAISERROR('Parse Foreign Keys from Json', 10, 100) WITH NOWAIT
  -- I5: missing/blank RelatedTableSchema is a programmer error after slice-1's resolver
  -- (which fills it from the platform default for regular templates or {{SchemaName}} for
  -- schema templates). Silent 'dbo' fallback here is data-loss-equivalent — fail loud.
  -- Implementation note: this check runs against the #ForeignKeys temp table AFTER the
  -- main FK parse below, not against the raw @TableDefinitions JSON. Running it earlier
  -- against the JSON ran into OPENJSON 'AS JSON' edge cases with single-object inputs
  -- (the kindling JSON for SchemaSmith's own bootstrap tables passes a single object).
  -- Post-parse the check is uniform across object / array inputs.

  DROP TABLE IF EXISTS #ForeignKeys
  -- Merge note (2026-06-02): main added [_RowId] (kept) AND swapped RelatedTableSchema to
  -- ISNULL(f.[RelatedTableSchema], 'dbo'). Schema-templates added the explicit THROW
  -- check (I5, below the SELECT INTO) instead of a silent fallback, so ISNULL is
  -- intentionally NOT applied here — the post-parse check catches blank RelatedTableSchema
  -- loudly rather than silently rewriting it to 'dbo'.
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #ForeignKeys
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [TableName] NVARCHAR(MAX) NULL,
    [KeyName] NVARCHAR(MAX) NULL,
    [RelatedTableSchema] NVARCHAR(MAX) NULL,
    [RelatedTable] NVARCHAR(MAX) NULL,
    [Columns] NVARCHAR(MAX) NULL,
    [RelatedColumns] NVARCHAR(MAX) NULL,
    [DeleteAction] NVARCHAR(20) NOT NULL,
    [UpdateAction] NVARCHAR(20) NOT NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL
  )
  INSERT INTO #ForeignKeys ([_RowId], [Schema], [TableName], [KeyName], [RelatedTableSchema], [RelatedTable], [Columns], [RelatedColumns], [DeleteAction], [UpdateAction], [ShouldApplyExpression], [VariantName])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [KeyName] = SchemaSmith.fn_SafeBracketWrap(f.[KeyName]),
         [RelatedTableSchema] = SchemaSmith.fn_SafeBracketWrap(f.[RelatedTableSchema]), [RelatedTable] = SchemaSmith.fn_SafeBracketWrap(f.[RelatedTable]),
         [Columns] = (SELECT STRING_AGG(CAST(SchemaSmith.fn_SafeBracketWrap([value]) AS NVARCHAR(MAX)), ',') FROM STRING_SPLIT(f.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         [RelatedColumns] = (SELECT STRING_AGG(CAST(SchemaSmith.fn_SafeBracketWrap([value]) AS NVARCHAR(MAX)), ',') FROM STRING_SPLIT(f.[RelatedColumns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         [DeleteAction] = ISNULL(NULLIF(RTRIM([DeleteAction]), ''), 'NO ACTION'),
         [UpdateAction] = ISNULL(NULLIF(RTRIM([UpdateAction]), ''), 'NO ACTION'),
         f.[ShouldApplyExpression], f.[VariantName]
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON(ForeignKeys) WITH (
      [KeyName] NVARCHAR(500) '$.Name',
      [Columns] NVARCHAR(MAX) '$.Columns',
      [RelatedTableSchema] NVARCHAR(500) '$.RelatedTableSchema',
      [RelatedTable] NVARCHAR(500) '$.RelatedTable',
      [RelatedColumns] NVARCHAR(MAX) '$.RelatedColumns',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName',
      [DeleteAction] NVARCHAR(20) '$.DeleteAction',
      [UpdateAction] NVARCHAR(20) '$.UpdateAction'
      ) f;

  -- I5: post-parse RelatedTableSchema check. fn_SafeBracketWrap(NULL) returns NULL
  -- (string concat with NULL yields NULL), and fn_SafeBracketWrap('') returns '[]'.
  -- Both sentinels indicate a blank input — fail loud rather than letting downstream
  -- code emit DDL against an unintended schema.
  IF EXISTS (SELECT 1 FROM #ForeignKeys WITH (NOLOCK)
               WHERE [RelatedTableSchema] IS NULL OR [RelatedTableSchema] IN ('[]', '[ ]', ''))
  BEGIN
    DECLARE @v_BadFk NVARCHAR(500) =
      (SELECT TOP 1 ISNULL([KeyName], '<unnamed>') FROM #ForeignKeys WITH (NOLOCK)
         WHERE [RelatedTableSchema] IS NULL OR [RelatedTableSchema] IN ('[]', '[ ]', ''));
    DECLARE @v_FkMsg NVARCHAR(2000) = 'Foreign key ''' + @v_BadFk + ''' is missing RelatedTableSchema. ' +
      'RelatedTableSchema must be populated before reaching ParseTableJsonIntoTempTables — this is a programmer error. ' +
      'In production the SchemaDefaultResolver fills RelatedTableSchema with the platform default for regular templates ' +
      'or {{SchemaName}} for schema templates; a blank value here means a caller bypassed Template.Load.';
    THROW 51000, @v_FkMsg, 1;
  END

  -- Identify ForeignKeys to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #ForeignKeys WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #ForeignKeys WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  RAISERROR('Parse Table Level Check Constraints from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #CheckConstraints
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #CheckConstraints
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [TableName] NVARCHAR(MAX) NULL,
    [ConstraintName] NVARCHAR(500) NULL,
    [Expression] NVARCHAR(MAX) NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL
  )
  INSERT INTO #CheckConstraints ([_RowId], [Schema], [TableName], [ConstraintName], [Expression], [ShouldApplyExpression], [VariantName])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], c.[ConstraintName], c.[Expression], c.[ShouldApplyExpression], c.[VariantName]
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON(CheckConstraints) WITH (
      [ConstraintName] NVARCHAR(500) '$.Name',
      [Expression] NVARCHAR(MAX) '$.Expression',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) c;

  -- Identify CheckConstraints to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG('DELETE FROM #CheckConstraints WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');', CHAR(13) + CHAR(10))
    FROM #CheckConstraints WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)
  
  RAISERROR('Parse Statistics from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #Statistics
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #Statistics
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [TableName] NVARCHAR(MAX) NULL,
    [StatisticName] NVARCHAR(MAX) NULL,
    [SampleSize] TINYINT NOT NULL,
    [FilterExpression] NVARCHAR(MAX) NULL,
    [Columns] NVARCHAR(MAX) NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL
  )
  INSERT INTO #Statistics ([_RowId], [Schema], [TableName], [StatisticName], [SampleSize], [FilterExpression], [Columns], [ShouldApplyExpression], [VariantName])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [StatisticName] = SchemaSmith.fn_SafeBracketWrap(s.[StatisticName]), [SampleSize] = ISNULL(s.[SampleSize], 0), s.[FilterExpression],
         [Columns] = (SELECT STRING_AGG(CAST(SchemaSmith.fn_SafeBracketWrap([value]) AS NVARCHAR(MAX)), ',') FROM STRING_SPLIT(s.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         s.[ShouldApplyExpression], s.[VariantName]
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON([Statistics]) WITH (
      [StatisticName] NVARCHAR(500) '$.Name',
      [SampleSize] TINYINT '$.SampleSize',
      [FilterExpression] NVARCHAR(MAX) '$.FilterExpression',
      [Columns] NVARCHAR(MAX) '$.Columns',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) s;

  -- Identify Statistics to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #Statistics WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Statistics WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)
  
  RAISERROR('Parse Full Text Indexes from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #FullTextIndexes
  -- Shape measured from tempdb against the SELECT INTO this replaces, then round-trip
  -- verified: declaring it explicitly is what lets the INSERT be parameterized later.
  CREATE TABLE #FullTextIndexes
  (
    [_RowId] BIGINT NULL,
    [Schema] NVARCHAR(MAX) NULL,
    [TableName] NVARCHAR(MAX) NULL,
    [FullTextCatalog] NVARCHAR(MAX) NULL,
    [KeyIndex] NVARCHAR(MAX) NULL,
    [ChangeTracking] NVARCHAR(500) NULL,
    [StopList] NVARCHAR(MAX) NULL,
    [Columns] NVARCHAR(MAX) NULL,
    [ShouldApplyExpression] NVARCHAR(MAX) NULL,
    [VariantName] NVARCHAR(128) NULL
  )
  INSERT INTO #FullTextIndexes ([_RowId], [Schema], [TableName], [FullTextCatalog], [KeyIndex], [ChangeTracking], [StopList], [Columns], [ShouldApplyExpression], [VariantName])
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [FullTextCatalog] = SchemaSmith.fn_SafeBracketWrap(f.[FullTextCatalog]), [KeyIndex] = SchemaSmith.fn_SafeBracketWrap(f.[KeyIndex]),
         -- Guarded like StopList beside it. Unguarded, this concatenates into the CREATE FULLTEXT INDEX
         -- statement, and T-SQL concatenation with NULL yields NULL -- the whole statement becomes NULL and
         -- NO index is created, with no error and no log line. Reachable from an ordinary package: the C#
         -- default is AUTO, but an explicit "ChangeTracking": null in a table file overwrites it. 'AUTO'
         -- here matches that C# default, so an omitted and an explicitly-null value behave the same.
         [ChangeTracking] = COALESCE(NULLIF(RTRIM(f.[ChangeTracking]), ''), 'AUTO'),
         [StopList] = SchemaSmith.fn_SafeBracketWrap(COALESCE(NULLIF(RTRIM(f.[StopList]), ''), 'SYSTEM')),
         -- Full-text LANGUAGE churn: a per-column "LANGUAGE nnnn" suffix must round-trip byte-identical
         -- against the live-side build in ModifiedTableQuench.sql (drift compares these as strings). Peel
         -- it off before bracket-wrapping the column (+ optional TYPE COLUMN) part -- same shape as the
         -- " DESC" handling for IndexColumns -- then reattach it; the LCID is variable-length so it's
         -- located and sliced rather than trimmed by a fixed count. Mirrors IndexOnlyQuench.sql's
         -- declared-side parse exactly.
         [Columns] = (SELECT STRING_AGG(CAST(CASE WHEN RTRIM([value]) LIKE '% LANGUAGE [0-9]%'
                                                   THEN SchemaSmith.fn_SafeBracketWrap(LEFT(RTRIM([value]), CHARINDEX(' LANGUAGE ', RTRIM([value])) - 1)) +
                                                        ' LANGUAGE ' + SUBSTRING(RTRIM([value]), CHARINDEX(' LANGUAGE ', RTRIM([value])) + 10, 4000)
                                                   -- A column may carry STATISTICAL_SEMANTICS with no LANGUAGE. Without this branch the whole
                                                   -- token would be bracket-wrapped as part of the column name ([Body STATISTICAL_SEMANTICS]),
                                                   -- which never matches the live-side render and churns the index on every deploy.
                                                   WHEN RTRIM([value]) LIKE '% STATISTICAL[_]SEMANTICS'
                                                        THEN SchemaSmith.fn_SafeBracketWrap(LEFT(RTRIM([value]), CHARINDEX(' STATISTICAL_SEMANTICS', RTRIM([value])) - 1)) +
                                                             ' STATISTICAL_SEMANTICS'
                                                   ELSE SchemaSmith.fn_SafeBracketWrap([value])
                                                   END AS NVARCHAR(MAX)), ',') FROM STRING_SPLIT(f.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         f.[ShouldApplyExpression], f.[VariantName]
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON([FullTextIndex]) WITH (
      [Columns] NVARCHAR(MAX) '$.Columns',
      [FullTextCatalog] NVARCHAR(500) '$.FullTextCatalog',
      [KeyIndex] NVARCHAR(500) '$.KeyIndex',
      [ChangeTracking] NVARCHAR(500) '$.ChangeTracking',
      [StopList] NVARCHAR(500) '$.StopList',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) f;

  -- Identify FullTextIndexes to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #FullTextIndexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #FullTextIndexes WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  -- A table with 2+ surviving variants cannot be honored: SQL Server allows ONE full-text index per table
  IF EXISTS (SELECT 1 FROM #FullTextIndexes WITH (NOLOCK) GROUP BY [Schema], [TableName] HAVING COUNT(*) > 1)
  BEGIN
    DECLARE @v_FTDupTable NVARCHAR(1010) =
      (SELECT TOP 1 [Schema] + '.' + [TableName] FROM #FullTextIndexes WITH (NOLOCK) GROUP BY [Schema], [TableName] HAVING COUNT(*) > 1 ORDER BY [Schema], [TableName]);
    DECLARE @v_FTDupMsg NVARCHAR(2000) = 'Multiple full-text index variants matched on this target for table ' + @v_FTDupTable +
      '. SQL Server allows one full-text index per table — ShouldApplyExpressions must be mutually exclusive.';
    THROW 51000, @v_FTDupMsg, 1;
  END
