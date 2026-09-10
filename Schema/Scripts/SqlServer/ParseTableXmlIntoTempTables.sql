-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- XML ingest twin of ParseTableJsonIntoTempTables.sql, selected below the OPENJSON compat cliff
-- (compatibility level < 130) or via Target:CompatEncoding=legacy. Reads an ambient @TableDefinitions XML
-- variable (declared by the caller). Each block shreds with .nodes()/.value() into a derived projection
-- that reproduces the exact column set the JSON script's OPENJSON WITH produced, so the surrounding
-- projection/gate logic is identical to the JSON twin and the 8 consumed temp tables come out identical.
-- Booleans arrive as 'true'/'false' text (see ModelXmlSerializer); a direct BIT cast of 'true' errors, so
-- each is CASEd. Each <Table> subtree is carried in [TableXml] for the nested CROSS APPLY .nodes().

  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON
  RAISERROR('Parse Tables from Xml', 10, 100) WITH NOWAIT

  -- Accept @TableDefinitions whether it is XML-typed (standalone callers declare @TableDefinitions XML) or
  -- NVARCHAR(MAX) (when inlined via {{ParseJson}} into TableQuench, whose param is NVARCHAR(MAX) — .nodes()
  -- cannot bind to an nvarchar). CONVERT(XML, <xml>) is a no-op; CONVERT(XML, <nvarchar>) parses the text.
  DECLARE @v_TableXml XML = CONVERT(XML, @TableDefinitions)

  -- I5: missing/blank [Schema] is a programmer error after slice-1's SchemaDefaultResolver (see JSON twin).
  IF EXISTS (SELECT 1 FROM @v_TableXml.nodes('/Tables/Table') AS X(t)
                 WHERE NULLIF(RTRIM(ISNULL(t.value('(Schema/text())[1]', 'NVARCHAR(500)'), '')), '') IS NULL)
  BEGIN
    DECLARE @v_BadTable NVARCHAR(500) =
      (SELECT TOP 1 ISNULL(t.value('(Name/text())[1]', 'NVARCHAR(500)'), '<unnamed>') FROM @v_TableXml.nodes('/Tables/Table') AS X(t)
         WHERE NULLIF(RTRIM(ISNULL(t.value('(Schema/text())[1]', 'NVARCHAR(500)'), '')), '') IS NULL);
    DECLARE @v_Msg NVARCHAR(2000) = 'Table XML is missing Schema for table ''' + @v_BadTable + '''. ' +
      'Schema must be populated before reaching ParseTableXmlIntoTempTables — this is a programmer error. ' +
      'In production the SchemaDefaultResolver fills Schema with the platform default or the {{SchemaName}} token; ' +
      'a blank value here means a caller bypassed Template.Load or substituted the token away.';
    RAISERROR(@v_Msg, 16, 1);
  END

  IF OBJECT_ID('tempdb..#TableDefinitions') IS NOT NULL DROP TABLE #TableDefinitions
  -- [_RowId] gives each parsed row a unique identifier for the per-row ShouldApply DELETE (see JSON twin).
  -- [TableXml] carries the whole <Table> element so the nested blocks below re-navigate with .nodes().
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         [Schema] = SchemaSmith.fn_SafeBracketWrap(t.value('(Schema/text())[1]', 'NVARCHAR(500)')), [Name] = SchemaSmith.fn_SafeBracketWrap(t.value('(Name/text())[1]', 'NVARCHAR(500)')), [CompressionType] = ISNULL(NULLIF(RTRIM(t.value('(CompressionType/text())[1]', 'NVARCHAR(100)')), ''), 'NONE'),
         [XmlCompression] = ISNULL(t.value('(XmlCompression/text())[1]', 'BIT'), 0),
         [IsTemporal] = ISNULL(CONVERT(BIT, CASE LOWER(t.value('(IsTemporal/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END), 0), [UpdateFillFactor] = ISNULL(CONVERT(BIT, CASE LOWER(t.value('(UpdateFillFactor/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END), 0),
         -- History table identity/retention -- see JSON twin (schema/name left NULL when absent, retention
         -- normalized to a canonical plural-unit form so it compares like-for-like with the live state).
         [HistoryTableSchema] = SchemaSmith.fn_SafeBracketWrap(t.value('(HistoryTableSchema/text())[1]', 'NVARCHAR(500)')),
         [HistoryTableName] = SchemaSmith.fn_SafeBracketWrap(t.value('(HistoryTableName/text())[1]', 'NVARCHAR(500)')),
         [HistoryRetentionPeriod] = SchemaSmith.fn_NormalizeTemporalRetentionPeriod(t.value('(HistoryRetentionPeriod/text())[1]', 'NVARCHAR(50)')),
         -- Filegroup placement (#filegroups) -- see JSON twin for the left-NULL-when-absent rationale.
         [FileGroup] = SchemaSmith.fn_SafeBracketWrap(t.value('(FileGroup/text())[1]', 'NVARCHAR(500)')),
         -- Partition placement (#partitioning) -- see JSON twin.
         [PartitionScheme] = SchemaSmith.fn_SafeBracketWrap(t.value('(PartitionScheme/text())[1]', 'NVARCHAR(500)')),
         [PartitionColumn] = SchemaSmith.fn_SafeBracketWrap(t.value('(PartitionColumn/text())[1]', 'NVARCHAR(500)')),
         [FileStreamFileGroup] = SchemaSmith.fn_SafeBracketWrap(t.value('(FileStreamFileGroup/text())[1]', 'NVARCHAR(500)')),
         [TextImageFileGroup] = SchemaSmith.fn_SafeBracketWrap(t.value('(TextImageFileGroup/text())[1]', 'NVARCHAR(500)')),
         [TableXml] = t.query('.'),
         [ShouldApplyExpression] = t.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'), [VariantName] = t.value('(VariantName/text())[1]', 'NVARCHAR(128)'), [GraphType] = RTRIM(ISNULL(t.value('(GraphType/text())[1]', 'NVARCHAR(10)'), 'None')), [Ledger] = RTRIM(ISNULL(t.value('(Ledger/text())[1]', 'NVARCHAR(12)'), 'Off')), [MemoryOptimized] = ISNULL(CONVERT(BIT, CASE LOWER(t.value('(MemoryOptimized/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END), 0), [Durability] = UPPER(RTRIM(ISNULL(NULLIF(t.value('(Durability/text())[1]', 'NVARCHAR(20)'), ''), 'SCHEMA_AND_DATA'))), [EnableCDC] = ISNULL(CONVERT(BIT, CASE LOWER(t.value('(EnableCDC/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END), 0), [EnableChangeTracking] = ISNULL(CONVERT(BIT, CASE LOWER(t.value('(EnableChangeTracking/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END), 0), [TrackColumnsUpdated] = ISNULL(CONVERT(BIT, CASE LOWER(t.value('(TrackColumnsUpdated/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END), 0), [OldName] = SchemaSmith.fn_SafeBracketWrap(t.value('(OldName/text())[1]', 'NVARCHAR(500)')),
         [DropColumnsRemovedFromProduct] = CONVERT(BIT, CASE LOWER(t.value('(DropColumnsRemovedFromProduct/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
         [DropForeignKeysRemovedFromProduct] = CONVERT(BIT, CASE LOWER(t.value('(DropForeignKeysRemovedFromProduct/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
         [DropCheckConstraintsRemovedFromProduct] = CONVERT(BIT, CASE LOWER(t.value('(DropCheckConstraintsRemovedFromProduct/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
         [DropExcludeConstraintsRemovedFromProduct] = CONVERT(BIT, CASE LOWER(t.value('(DropExcludeConstraintsRemovedFromProduct/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
         [DropStatisticsRemovedFromProduct] = CONVERT(BIT, CASE LOWER(t.value('(DropStatisticsRemovedFromProduct/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
         [DropIndexesRemovedFromProduct] = CONVERT(BIT, CASE LOWER(t.value('(DropIndexesRemovedFromProduct/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
         -- RebuildPolicy -- see the JSON twin for why the SENTINEL (did this table declare a policy AT ALL?)
         -- matters and why a per-field COALESCE against the passed-in tier is wrong. The presence test is
         -- "any child of <RebuildPolicy> carries a text node" rather than "the <RebuildPolicy> element
         -- exists": an undeclared policy serializes as a JSON null, which DeserializeXNode renders as an
         -- EMPTY <RebuildPolicy/> element, so element existence alone would read a null policy as declared.
         [RebuildPolicyMode] = t.value('(RebuildPolicy/Mode/text())[1]', 'NVARCHAR(20)'),
         [RebuildPolicyThreshold] = t.value('(RebuildPolicy/Threshold/text())[1]', 'INT'),
         [RebuildPolicyOnOrderMismatch] = CONVERT(BIT, CASE LOWER(t.value('(RebuildPolicy/OnOrderMismatch/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
         [RebuildPolicySpecified] = CONVERT(BIT, t.exist('RebuildPolicy/*/text()')),
         [PreventDrop] = ISNULL(CONVERT(BIT, CASE LOWER(t.value('(PreventDrop/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END), 0)
    INTO #TableDefinitions
    FROM @v_TableXml.nodes('/Tables/Table') AS X(t);

  -- Identify Tables to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #TableDefinitions WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))
                           FROM #TableDefinitions WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  IF OBJECT_ID('tempdb..#Tables') IS NOT NULL DROP TABLE #Tables
  SELECT [Schema], [Name], [CompressionType], [XmlCompression], [IsTemporal], [HistoryTableSchema], [HistoryTableName], [HistoryRetentionPeriod], [FileGroup], [PartitionScheme], [PartitionColumn], [FileStreamFileGroup], [TextImageFileGroup], [UpdateFillFactor], [EnableCDC], [EnableChangeTracking], [TrackColumnsUpdated], [GraphType], [Ledger], [MemoryOptimized], [Durability], [OldName], [VariantName],
         CONVERT(BIT, CASE WHEN OBJECT_ID([Schema] + '.' + [Name], 'U') IS NULL AND OBJECT_ID([Schema] + '.' + [OldName], 'U') IS NULL THEN 1 ELSE 0 END) AS NewTable,
         [DropColumnsRemovedFromProduct], [DropForeignKeysRemovedFromProduct], [DropCheckConstraintsRemovedFromProduct], [DropExcludeConstraintsRemovedFromProduct], [DropStatisticsRemovedFromProduct], [DropIndexesRemovedFromProduct],
         [RebuildPolicyMode], [RebuildPolicyThreshold], [RebuildPolicyOnOrderMismatch], [RebuildPolicySpecified],
         ISNULL([PreventDrop], 0) AS [PreventDrop]
    INTO #Tables
    FROM #TableDefinitions WITH (NOLOCK);

  RAISERROR('Parse Columns from Xml', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#Columns') IS NOT NULL DROP TABLE #Columns
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [ColumnName] = SchemaSmith.fn_SafeBracketWrap(c.[ColumnName]),
         -- Canonicalize DATETIME2/TIME/DATETIMEOFFSET to explicit (7) — see JSON twin for the rationale.
         [DataType] = CASE WHEN UPPER(LTRIM(RTRIM(SchemaSmith.fn_NormalizeDataType(c.[DataType])))) IN ('DATETIME2', 'TIME', 'DATETIMEOFFSET')
                            THEN UPPER(LTRIM(RTRIM(SchemaSmith.fn_NormalizeDataType(c.[DataType])))) + '(7)'
                            ELSE SchemaSmith.fn_NormalizeDataType(c.[DataType]) END,
         [Nullable] = ISNULL(c.[Nullable], 0),
         c.[Default], c.[CheckExpression], c.[ComputedExpression], [Persisted] = ISNULL(c.[Persisted], 0),
         [Sparse] = ISNULL(c.[Sparse], 0), [FileStream] = ISNULL(c.[FileStream], 0), [IsColumnSet] = ISNULL(c.[IsColumnSet], 0), [BackfillExistingRows] = ISNULL(c.[BackfillExistingRows], 0), [Collation] = RTRIM(ISNULL(c.[Collation], '')), [DataMaskFunction] = RTRIM(ISNULL(c.[DataMaskFunction], '')),
         [EncryptionType] = ISNULL(c.[EncryptionType], 'NONE'), [EncryptionKey] = RTRIM(ISNULL(c.[EncryptionKey], '')), [EncryptionAlgorithm] = RTRIM(ISNULL(c.[EncryptionAlgorithm], '')),
         [OldName] = SchemaSmith.fn_SafeBracketWrap(c.[OldName]),
         CONVERT(BIT, CASE WHEN (RTRIM(ISNULL([ComputedExpression], '')) <> '' OR NOT EXISTS (SELECT * FROM #Tables x WHERE x.[Name] = t.[Name] AND x.[Schema] = t.[Schema] AND x.NewTable = 1))
                            AND COLUMNPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U'), SchemaSmith.fn_StripBracketWrapping([ColumnName]), 'ColumnId') IS NULL
                            AND COLUMNPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[OldName], 'U'), SchemaSmith.fn_StripBracketWrapping([ColumnName]), 'ColumnId') IS NULL
                            AND COLUMNPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U'), SchemaSmith.fn_StripBracketWrapping(c.[OldName]), 'ColumnId') IS NULL
                           THEN 1 ELSE 0 END) AS NewColumn,
         SchemaSmith.fn_SafeBracketWrap(c.[ColumnName]) + ' ' +
         CASE WHEN RTRIM(ISNULL([ComputedExpression], '')) <> '' THEN 'AS (' + ComputedExpression + ')' + CASE WHEN ISNULL(c.[Persisted], 0) = 1 THEN ' PERSISTED' ELSE '' END
                                                                                                     + CASE WHEN ISNULL(c.[Persisted], 0) = 1 AND ISNULL(c.[Nullable], 1) = 0 THEN ' NOT NULL' ELSE '' END
              -- See the JSON twin (ParseTableJsonIntoTempTables.sql) for why a column set gets its own
              -- branch instead of the COLLATE/SPARSE/MASKED/ENCRYPTED/NULL/DEFAULT chain below.
              WHEN ISNULL([IsColumnSet], 0) = 1 THEN UPPER(SchemaSmith.fn_NormalizeDataType(c.[DataType])) + ' COLUMN_SET FOR ALL_SPARSE_COLUMNS'
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
    INTO #Columns
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY t.[TableXml].nodes('/Table/Columns') AS Y(col)
    CROSS APPLY (SELECT
      [ColumnName] = col.value('(Name/text())[1]', 'NVARCHAR(500)'),
      [DataType] = col.value('(DataType/text())[1]', 'NVARCHAR(100)'),
      [Nullable] = CONVERT(BIT, CASE LOWER(col.value('(Nullable/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [Default] = col.value('(Default/text())[1]', 'NVARCHAR(MAX)'),
      [CheckExpression] = col.value('(CheckExpression/text())[1]', 'NVARCHAR(MAX)'),
      [ComputedExpression] = col.value('(ComputedExpression/text())[1]', 'NVARCHAR(MAX)'),
      [Persisted] = CONVERT(BIT, CASE LOWER(col.value('(Persisted/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [Sparse] = CONVERT(BIT, CASE LOWER(col.value('(Sparse/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [FileStream] = CONVERT(BIT, CASE LOWER(col.value('(FileStream/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [IsColumnSet] = CONVERT(BIT, CASE LOWER(col.value('(IsColumnSet/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [BackfillExistingRows] = CONVERT(BIT, CASE LOWER(col.value('(BackfillExistingRows/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [Collation] = col.value('(Collation/text())[1]', 'NVARCHAR(500)'),
      [DataMaskFunction] = col.value('(DataMaskFunction/text())[1]', 'NVARCHAR(500)'),
      [EncryptionType] = col.value('(EncryptionType/text())[1]', 'NVARCHAR(100)'),
      [EncryptionKey] = col.value('(EncryptionKey/text())[1]', 'NVARCHAR(500)'),
      [EncryptionAlgorithm] = col.value('(EncryptionAlgorithm/text())[1]', 'NVARCHAR(500)'),
      [ShouldApplyExpression] = col.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
      [VariantName] = col.value('(VariantName/text())[1]', 'NVARCHAR(128)'),
      [OldName] = col.value('(OldName/text())[1]', 'NVARCHAR(500)')
      ) c;

  -- Identify Columns to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #Columns WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))
                           FROM #Columns WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  -- Don't try to apply tables without columns
  DELETE FROM #Tables
    WHERE NOT EXISTS (SELECT * FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = #Tables.[Schema] AND C.[TableName] = #Tables.[Name])
  DELETE FROM #TableDefinitions
    WHERE NOT EXISTS (SELECT * FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = #TableDefinitions.[Schema] AND C.[TableName] = #TableDefinitions.[Name])

  RAISERROR('Parse Indexes from Xml', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#Indexes') IS NOT NULL DROP TABLE #Indexes
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [IndexName] = SchemaSmith.fn_SafeBracketWrap(i.[IndexName]), [CompressionType] = ISNULL(NULLIF(RTRIM(i.[CompressionType]), ''), 'NONE'), [XmlCompression] = ISNULL(i.[XmlCompression], 0), [PrimaryKey] = ISNULL(i.[PrimaryKey], 0),
         [Unique] = COALESCE(NULLIF(i.[Unique], 0), NULLIF(i.[PrimaryKey], 0), i.[UniqueConstraint], 0),
         [UniqueConstraint] = ISNULL(i.[UniqueConstraint], 0), [Clustered] = ISNULL(i.[Clustered], 0), [ColumnStore] = ISNULL(i.[ColumnStore], 0), [FillFactor] = ISNULL(NULLIF(i.[FillFactor], 0), 100),
         i.[FilterExpression], [FileGroup] = SchemaSmith.fn_SafeBracketWrap(i.[FileGroup]),
         [PartitionScheme] = SchemaSmith.fn_SafeBracketWrap(i.[PartitionScheme]), [PartitionColumn] = SchemaSmith.fn_SafeBracketWrap(i.[PartitionColumn]), [BucketCount] = i.[BucketCount], [UpdateFillFactor] = CONVERT(BIT, CASE WHEN @UpdateFillFactor = 1 OR t.[UpdateFillFactor] = 1 OR i.[UpdateFillFactor] = 1 THEN 1 ELSE 0 END),
         [IndexColumns] = STUFF((SELECT ',' + CASE WHEN RTRIM([value]) LIKE '% DESC'
                                                   THEN SchemaSmith.fn_SafeBracketWrap(SUBSTRING(RTRIM([value]), 1, LEN(RTRIM([value])) - 5)) + ' DESC'
                                                   ELSE SchemaSmith.fn_SafeBracketWrap([value])
                                                   END
                             FROM SchemaSmith.fn_SplitList(i.[IndexColumns], ',')
                             WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''
                             ORDER BY [Ordinal] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''),
         [IncludeColumns] = STUFF((SELECT ',' + SchemaSmith.fn_SafeBracketWrap([value])
                               FROM SchemaSmith.fn_SplitList(i.[IncludeColumns], ',')
                               WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''
                               ORDER BY SchemaSmith.fn_SafeBracketWrap([value]) FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''),
         [IgnoreDuplicateKey] = ISNULL(i.[IgnoreDuplicateKey], 0), [PadIndex] = ISNULL(i.[PadIndex], 0),
         i.[ShouldApplyExpression], i.[VariantName]
    INTO #Indexes
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY t.[TableXml].nodes('/Table/Indexes') AS Y(idx)
    CROSS APPLY (SELECT
      [IndexName] = idx.value('(Name/text())[1]', 'NVARCHAR(500)'),
      [CompressionType] = idx.value('(CompressionType/text())[1]', 'NVARCHAR(100)'),
      [XmlCompression] = idx.value('(XmlCompression/text())[1]', 'BIT'),
      [PrimaryKey] = CONVERT(BIT, CASE LOWER(idx.value('(PrimaryKey/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [Unique] = CONVERT(BIT, CASE LOWER(idx.value('(Unique/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [UniqueConstraint] = CONVERT(BIT, CASE LOWER(idx.value('(UniqueConstraint/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [Clustered] = CONVERT(BIT, CASE LOWER(idx.value('(Clustered/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [ColumnStore] = CONVERT(BIT, CASE LOWER(idx.value('(ColumnStore/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [IgnoreDuplicateKey] = CONVERT(BIT, CASE LOWER(idx.value('(IgnoreDuplicateKey/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [PadIndex] = CONVERT(BIT, CASE LOWER(idx.value('(PadIndex/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [FillFactor] = idx.value('(FillFactor/text())[1]', 'TINYINT'),
      [FilterExpression] = idx.value('(FilterExpression/text())[1]', 'NVARCHAR(MAX)'),
      [IndexColumns] = idx.value('(IndexColumns/text())[1]', 'NVARCHAR(MAX)'),
      [IncludeColumns] = idx.value('(IncludeColumns/text())[1]', 'NVARCHAR(MAX)'),
      [FileGroup] = idx.value('(FileGroup/text())[1]', 'NVARCHAR(500)'),
      [PartitionScheme] = idx.value('(PartitionScheme/text())[1]', 'NVARCHAR(500)'),
      [PartitionColumn] = idx.value('(PartitionColumn/text())[1]', 'NVARCHAR(500)'),
      [BucketCount] = idx.value('(BucketCount/text())[1]', 'INT'),
      [UpdateFillFactor] = CONVERT(BIT, CASE LOWER(idx.value('(UpdateFillFactor/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [ShouldApplyExpression] = idx.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
      [VariantName] = idx.value('(VariantName/text())[1]', 'NVARCHAR(128)')
      ) i;

  -- Identify Indexes to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #Indexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))
                           FROM #Indexes WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  RAISERROR('Parse XML Indexes from Xml', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#XmlIndexes') IS NOT NULL DROP TABLE #XmlIndexes
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [IndexName] = SchemaSmith.fn_SafeBracketWrap(i.[IndexName]), i.[IsPrimary],
         [Column] = SchemaSmith.fn_SafeBracketWrap(i.[Column]), [PrimaryIndex] = SchemaSmith.fn_SafeBracketWrap(i.[PrimaryIndex]),
         i.[SecondaryIndexType], i.[ShouldApplyExpression], i.[VariantName]
    INTO #XmlIndexes
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY t.[TableXml].nodes('/Table/XmlIndexes') AS Y(xi)
    CROSS APPLY (SELECT
      [IndexName] = xi.value('(Name/text())[1]', 'NVARCHAR(500)'),
      [IsPrimary] = CONVERT(BIT, CASE LOWER(xi.value('(IsPrimary/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
      [Column] = xi.value('(Column/text())[1]', 'NVARCHAR(500)'),
      [PrimaryIndex] = xi.value('(PrimaryIndex/text())[1]', 'NVARCHAR(500)'),
      [SecondaryIndexType] = xi.value('(SecondaryIndexType/text())[1]', 'NVARCHAR(500)'),
      [ShouldApplyExpression] = xi.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
      [VariantName] = xi.value('(VariantName/text())[1]', 'NVARCHAR(128)')
      ) i;

  -- Identify XmlIndexes to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #XmlIndexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))
                           FROM #XmlIndexes WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  RAISERROR('Parse Foreign Keys from Xml', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ForeignKeys') IS NOT NULL DROP TABLE #ForeignKeys
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [KeyName] = SchemaSmith.fn_SafeBracketWrap(f.[KeyName]),
         [RelatedTableSchema] = SchemaSmith.fn_SafeBracketWrap(f.[RelatedTableSchema]), [RelatedTable] = SchemaSmith.fn_SafeBracketWrap(f.[RelatedTable]),
         [Columns] = STUFF((SELECT ',' + SchemaSmith.fn_SafeBracketWrap([value]) FROM SchemaSmith.fn_SplitList(f.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> '' ORDER BY [Ordinal] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''),
         [RelatedColumns] = STUFF((SELECT ',' + SchemaSmith.fn_SafeBracketWrap([value]) FROM SchemaSmith.fn_SplitList(f.[RelatedColumns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> '' ORDER BY [Ordinal] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''),
         [DeleteAction] = ISNULL(NULLIF(RTRIM([DeleteAction]), ''), 'NO ACTION'),
         [UpdateAction] = ISNULL(NULLIF(RTRIM([UpdateAction]), ''), 'NO ACTION'),
         f.[ShouldApplyExpression], f.[VariantName]
    INTO #ForeignKeys
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY t.[TableXml].nodes('/Table/ForeignKeys') AS Y(fk)
    CROSS APPLY (SELECT
      [KeyName] = fk.value('(Name/text())[1]', 'NVARCHAR(500)'),
      [Columns] = fk.value('(Columns/text())[1]', 'NVARCHAR(MAX)'),
      [RelatedTableSchema] = fk.value('(RelatedTableSchema/text())[1]', 'NVARCHAR(500)'),
      [RelatedTable] = fk.value('(RelatedTable/text())[1]', 'NVARCHAR(500)'),
      [RelatedColumns] = fk.value('(RelatedColumns/text())[1]', 'NVARCHAR(MAX)'),
      [ShouldApplyExpression] = fk.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
      [VariantName] = fk.value('(VariantName/text())[1]', 'NVARCHAR(128)'),
      [DeleteAction] = fk.value('(DeleteAction/text())[1]', 'NVARCHAR(20)'),
      [UpdateAction] = fk.value('(UpdateAction/text())[1]', 'NVARCHAR(20)')
      ) f;

  -- I5: post-parse RelatedTableSchema check — identical to JSON twin.
  IF EXISTS (SELECT 1 FROM #ForeignKeys WITH (NOLOCK)
               WHERE [RelatedTableSchema] IS NULL OR [RelatedTableSchema] IN ('[]', '[ ]', ''))
  BEGIN
    DECLARE @v_BadFk NVARCHAR(500) =
      (SELECT TOP 1 ISNULL([KeyName], '<unnamed>') FROM #ForeignKeys WITH (NOLOCK)
         WHERE [RelatedTableSchema] IS NULL OR [RelatedTableSchema] IN ('[]', '[ ]', ''));
    DECLARE @v_FkMsg NVARCHAR(2000) = 'Foreign key ''' + @v_BadFk + ''' is missing RelatedTableSchema. ' +
      'RelatedTableSchema must be populated before reaching ParseTableXmlIntoTempTables — this is a programmer error. ' +
      'In production the SchemaDefaultResolver fills RelatedTableSchema with the platform default for regular templates ' +
      'or {{SchemaName}} for schema templates; a blank value here means a caller bypassed Template.Load.';
    RAISERROR(@v_FkMsg, 16, 1);
  END

  -- Identify ForeignKeys to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #ForeignKeys WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))
                           FROM #ForeignKeys WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  RAISERROR('Parse Table Level Check Constraints from Xml', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#CheckConstraints') IS NOT NULL DROP TABLE #CheckConstraints
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], c.[ConstraintName], c.[Expression], c.[ShouldApplyExpression], c.[VariantName]
    INTO #CheckConstraints
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY t.[TableXml].nodes('/Table/CheckConstraints') AS Y(cc)
    CROSS APPLY (SELECT
      [ConstraintName] = cc.value('(Name/text())[1]', 'NVARCHAR(500)'),
      [Expression] = cc.value('(Expression/text())[1]', 'NVARCHAR(MAX)'),
      [ShouldApplyExpression] = cc.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
      [VariantName] = cc.value('(VariantName/text())[1]', 'NVARCHAR(128)')
      ) c;

  -- Identify CheckConstraints to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + 'DELETE FROM #CheckConstraints WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');'
                           FROM #CheckConstraints WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  RAISERROR('Parse Statistics from Xml', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#Statistics') IS NOT NULL DROP TABLE #Statistics
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [StatisticName] = SchemaSmith.fn_SafeBracketWrap(s.[StatisticName]), [SampleSize] = ISNULL(s.[SampleSize], 0), s.[FilterExpression],
         [Columns] = STUFF((SELECT ',' + SchemaSmith.fn_SafeBracketWrap([value]) FROM SchemaSmith.fn_SplitList(s.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> '' ORDER BY [Ordinal] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''),
         s.[ShouldApplyExpression], s.[VariantName]
    INTO #Statistics
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY t.[TableXml].nodes('/Table/Statistics') AS Y(st)
    CROSS APPLY (SELECT
      [StatisticName] = st.value('(Name/text())[1]', 'NVARCHAR(500)'),
      [SampleSize] = st.value('(SampleSize/text())[1]', 'TINYINT'),
      [FilterExpression] = st.value('(FilterExpression/text())[1]', 'NVARCHAR(MAX)'),
      [Columns] = st.value('(Columns/text())[1]', 'NVARCHAR(MAX)'),
      [ShouldApplyExpression] = st.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
      [VariantName] = st.value('(VariantName/text())[1]', 'NVARCHAR(128)')
      ) s;

  -- Identify Statistics to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #Statistics WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))
                           FROM #Statistics WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  RAISERROR('Parse Full Text Indexes from Xml', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#FullTextIndexes') IS NOT NULL DROP TABLE #FullTextIndexes
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [FullTextCatalog] = SchemaSmith.fn_SafeBracketWrap(f.[FullTextCatalog]), [KeyIndex] = SchemaSmith.fn_SafeBracketWrap(f.[KeyIndex]),
         -- Guarded like StopList beside it. Unguarded, this concatenates into the CREATE FULLTEXT INDEX
         -- statement, and T-SQL concatenation with NULL yields NULL -- the whole statement becomes NULL and
         -- NO index is created, with no error and no log line. Reachable from an ordinary package: the C#
         -- default is AUTO, but an explicit "ChangeTracking": null in a table file overwrites it. 'AUTO'
         -- here matches that C# default, so an omitted and an explicitly-null value behave the same.
         [ChangeTracking] = COALESCE(NULLIF(RTRIM(f.[ChangeTracking]), ''), 'AUTO'),
         [StopList] = SchemaSmith.fn_SafeBracketWrap(COALESCE(NULLIF(RTRIM(f.[StopList]), ''), 'SYSTEM')),
         -- Full-text LANGUAGE churn: same round-trip contract as the JSON twin -- peel off a trailing
         -- "LANGUAGE nnnn" suffix before bracket-wrapping the column (+ optional TYPE COLUMN) part, then
         -- reattach it, so this matches the live-side build in ModifiedTableQuench.sql byte-for-byte.
         [Columns] = STUFF((SELECT ',' + CASE WHEN RTRIM([value]) LIKE '% LANGUAGE [0-9]%'
                                              THEN SchemaSmith.fn_SafeBracketWrap(LEFT(RTRIM([value]), CHARINDEX(' LANGUAGE ', RTRIM([value])) - 1)) +
                                                   ' LANGUAGE ' + SUBSTRING(RTRIM([value]), CHARINDEX(' LANGUAGE ', RTRIM([value])) + 10, 4000)
                                              -- A column may carry STATISTICAL_SEMANTICS with no LANGUAGE. Without this
                                              -- branch the token is bracket-wrapped into the column name and never matches
                                              -- the live side, churning the index on every deploy. (JSON twin: same shape.)
                                              WHEN RTRIM([value]) LIKE '% STATISTICAL[_]SEMANTICS'
                                                   THEN SchemaSmith.fn_SafeBracketWrap(LEFT(RTRIM([value]), CHARINDEX(' STATISTICAL_SEMANTICS', RTRIM([value])) - 1)) +
                                                        ' STATISTICAL_SEMANTICS'
                                              ELSE SchemaSmith.fn_SafeBracketWrap([value])
                                              END
                      FROM SchemaSmith.fn_SplitList(f.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> '' ORDER BY [Ordinal] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''),
         f.[ShouldApplyExpression], f.[VariantName]
    INTO #FullTextIndexes
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY t.[TableXml].nodes('/Table/FullTextIndex') AS Y(ft)
    CROSS APPLY (SELECT
      [Columns] = ft.value('(Columns/text())[1]', 'NVARCHAR(MAX)'),
      [FullTextCatalog] = ft.value('(FullTextCatalog/text())[1]', 'NVARCHAR(500)'),
      [KeyIndex] = ft.value('(KeyIndex/text())[1]', 'NVARCHAR(500)'),
      [ChangeTracking] = ft.value('(ChangeTracking/text())[1]', 'NVARCHAR(500)'),
      [StopList] = ft.value('(StopList/text())[1]', 'NVARCHAR(500)'),
      [ShouldApplyExpression] = ft.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
      [VariantName] = ft.value('(VariantName/text())[1]', 'NVARCHAR(128)')
      ) f;

  -- Identify FullTextIndexes to skip based on ShouldApply expression (scoped by [_RowId]) — identical to JSON twin.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #FullTextIndexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))
                           FROM #FullTextIndexes WITH (NOLOCK)
                           WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_SQL)

  -- A table with 2+ surviving full-text variants cannot be honored — identical to JSON twin.
  IF EXISTS (SELECT 1 FROM #FullTextIndexes WITH (NOLOCK) GROUP BY [Schema], [TableName] HAVING COUNT(*) > 1)
  BEGIN
    DECLARE @v_FTDupTable NVARCHAR(1010) =
      (SELECT TOP 1 [Schema] + '.' + [TableName] FROM #FullTextIndexes WITH (NOLOCK) GROUP BY [Schema], [TableName] HAVING COUNT(*) > 1 ORDER BY [Schema], [TableName]);
    DECLARE @v_FTDupMsg NVARCHAR(2000) = 'Multiple full-text index variants matched on this target for table ' + @v_FTDupTable +
      '. SQL Server allows one full-text index per table — ShouldApplyExpressions must be mutually exclusive.';
    RAISERROR(@v_FTDupMsg, 16, 1);
  END
