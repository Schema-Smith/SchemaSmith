-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- FOR XML PATH twin of SchemaSmith.GenerateTableJSON.sql for the compare/extraction side below the
-- FOR JSON binary floor (SQL Server pre-2016, which lacks FOR JSON/JSON_QUERY entirely). Emits the SAME
-- object shape as the JSON proc but as XML: each array container (Columns/Indexes/XmlIndexes/ForeignKeys/
-- Statistics/CheckConstraints) is a repeated element carrying json:Array="true" so a single-element array
-- does not collapse to an object when C# (ModelXmlSerializer.FromIngestXml -> SerializeXNode) converts it
-- back to JSON for PlatformDeserializer.DeserializeTable. bit values are emitted as 'true'/'false' text so
-- Newtonsoft coerces them into the typed model. No fn_FormatJson/REPLACE wrapper is needed (FOR XML PATH
-- emits well-formed, entity-encoded XML directly).
--
-- Object ExtendedProperties round-trip on the legacy encoding too (B2). EP names are arbitrary sysname
-- (spaces/special chars) so they cannot be XML element names — they are emitted attribute-encoded as
-- <Extensions><ExtendedProperties><p n="Name">Value</p>...>, which ModelXmlSerializer.FromIngestXml rebuilds
-- into the {Name: Value} dict the JSON proc produces. The <Extensions> element is omitted entirely when an
-- object has no non-internal EP (a NULL FOR XML subquery column is dropped), matching the JSON proc where a
-- NULL STRING_AGG collapses Extensions to absent.
--
-- 2016-era catalog reads are version-gated (Slice E) so this procedure CREATEs on a genuine pre-2016 binary,
-- where a STATIC reference to any of them is an "invalid column"/"invalid object" error at CREATE time (not
-- runtime) and reproducible only on a real old binary, never on the modern CI container. The 2016+ reads —
-- sys.tables.temporal_type, sys.columns.generated_always_type, sys.masked_columns.masking_function, and the
-- Always Encrypted metadata (sys.columns.encryption_type_desc/column_encryption_key_id/
-- encryption_algorithm_name + sys.column_encryption_keys) — are staged through a fn_ServerMajorVersion()>=13
-- guarded DYNAMIC statement (the identifiers live only in a string, never in the compiled body). The static
-- SELECT reads @v_IsTemporal and LEFT JOINs #ColMeta, both simply empty/0 on an older binary (which has no
-- temporal/masking/Always-Encrypted objects anyway), so the emitted model degrades cleanly there.
--
-- Note the two DIFFERENT thresholds below, and do not merge them: the 2016 reads gate at >= 13, but the
-- temporal RETENTION reads (sys.tables.history_retention_period / _unit / _unit_desc) are SQL Server 2017
-- and gate at >= 14. Temporal tables are 2016; retention on them is 2017. Putting retention behind >= 13
-- broke every deploy to a genuine 2016 binary.
IF OBJECT_ID('SchemaSmith.GenerateTableXml', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.GenerateTableXml
GO
CREATE PROCEDURE SchemaSmith.GenerateTableXml
  @p_Schema SYSNAME = 'dbo',
  @p_Table SYSNAME
AS
SET NOCOUNT ON
DECLARE @v_DatabaseCollation NVARCHAR(200) = CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS NVARCHAR(200))
DECLARE @v_ObjectId INT = OBJECT_ID(@p_Schema + '.' + @p_Table)
DECLARE @v_IsTemporal BIT = 0
-- History table identity/retention (#depth-gap) -- see JSON twin for the emit-only-when-nonstandard
-- rationale. Populated by the same version-gated dynamic block as @v_IsTemporal below (0/NULL pre-2016).
DECLARE @v_HistTableSchema SYSNAME = NULL, @v_HistTableName SYSNAME = NULL, @v_HistRetentionText NVARCHAR(50) = NULL

-- Internal SchemaSmith ownership markers, excluded from the user Extensions/ExtendedProperties (mirrors the
-- JSON proc). PreventDrop is surfaced as its own top-level property, not a user EP (#270).
DECLARE @InternalEPNames TABLE ([Name] NVARCHAR(128))
INSERT @InternalEPNames VALUES (N'ProductName'), (N'PreventDrop')

CREATE TABLE #ColMeta
(
  [column_id] INT PRIMARY KEY,
  GeneratedAlwaysType TINYINT NOT NULL DEFAULT 0,
  MaskingFunction NVARCHAR(4000) NULL,
  EncryptionType NVARCHAR(64) NULL,
  EncryptionKey NVARCHAR(388) NULL,
  EncryptionAlgorithm NVARCHAR(128) NULL
)
IF SchemaSmith.fn_ServerMajorVersion() >= 13
  EXEC sp_executesql N'
    INSERT INTO #ColMeta ([column_id], GeneratedAlwaysType, MaskingFunction, EncryptionType, EncryptionKey, EncryptionAlgorithm)
    SELECT sc.column_id, sc.generated_always_type, mc.masking_function, sc.encryption_type_desc,
           (SELECT ''['' + cek.[name] + '']'' FROM sys.column_encryption_keys cek WITH (NOLOCK) WHERE cek.column_encryption_key_id = sc.column_encryption_key_id),
           sc.encryption_algorithm_name
      FROM sys.columns sc WITH (NOLOCK)
      LEFT JOIN sys.masked_columns mc WITH (NOLOCK) ON mc.[object_id] = sc.[object_id] AND mc.column_id = sc.column_id
      WHERE sc.[object_id] = @p_ObjId;
    -- History table identity -- see JSON twin (GenerateTableJson.sql) for the emit-only-when-nonstandard
    -- rationale; @p_Schema/@p_Table compare raw (unwrapped) against sys.schemas/sys.tables names, same as
    -- the JSON twin''s TABLE_SCHEMA/TABLE_NAME comparison. temporal_type and history_table_id are genuine
    -- 2016 columns and belong in this >= 13 block. The RETENTION columns are NOT -- they are 2017 -- so
    -- they are read in their own >= 14 block below rather than riding along here.
    SELECT @p_Out = CASE WHEN st.temporal_type = 2 THEN 1 ELSE 0 END,
           @p_HistSchema = CASE WHEN st.temporal_type = 2 AND (hs.[name] <> @p_Schema OR h.[name] <> @p_Table + ''_Hist'') THEN hs.[name] END,
           @p_HistName = CASE WHEN st.temporal_type = 2 AND (hs.[name] <> @p_Schema OR h.[name] <> @p_Table + ''_Hist'') THEN h.[name] END
      FROM sys.tables st WITH (NOLOCK)
      LEFT JOIN sys.tables h WITH (NOLOCK) ON h.[object_id] = st.history_table_id
      LEFT JOIN sys.schemas hs WITH (NOLOCK) ON hs.[schema_id] = h.[schema_id]
      WHERE st.[object_id] = @p_ObjId;',
    N'@p_ObjId INT, @p_Schema SYSNAME, @p_Table SYSNAME, @p_Out BIT OUTPUT, @p_HistSchema SYSNAME OUTPUT, @p_HistName SYSNAME OUTPUT',
    @p_ObjId = @v_ObjectId, @p_Schema = @p_Schema, @p_Table = @p_Table, @p_Out = @v_IsTemporal OUTPUT, @p_HistSchema = @v_HistTableSchema OUTPUT, @p_HistName = @v_HistTableName OUTPUT

-- HISTORY_RETENTION_PERIOD is SQL Server 2017 (major 14), not 2016: system-versioned tables arrived in 2016
-- but their retention policy -- and sys.tables.history_retention_period / _unit / _unit_desc -- arrived a
-- version later. Reading them behind the >= 13 gate above made an ordinary deploy to a genuine SQL Server
-- 2016 binary fail with "Invalid column name 'history_retention_period_unit_desc'", unconditionally and on
-- any table, because the column binds for the whole statement whether or not one is system-versioned.
-- Covered by Sql2016TemporalRetentionGateTests, which runs only on major 13 (the only version that can
-- reproduce it: below 13 this block is skipped, at 14+ the columns exist).
-- Reads history_retention_period_unit_desc rather than the numeric unit code -- see the JSON twin for why
-- (measured live-server codes disagreed with documentation once already). The unreachable ELSE still forces
-- a loud Msg 245 rather than a silently-dropped-to-NULL retention if an unrecognized unit ever appears; the
-- outer CONVERT(NVARCHAR(10), ...) keeps the branch statically typed like its siblings.
IF SchemaSmith.fn_ServerMajorVersion() >= 14
  EXEC sp_executesql N'
    SELECT @p_RetentionText = CASE WHEN st.temporal_type = 2 AND st.history_retention_period_unit_desc <> ''INFINITE''
                                    THEN CAST(st.history_retention_period AS NVARCHAR(10)) + '' '' +
                                         CASE st.history_retention_period_unit_desc
                                           WHEN ''DAY'' THEN ''DAYS'' WHEN ''WEEK'' THEN ''WEEKS'' WHEN ''MONTH'' THEN ''MONTHS'' WHEN ''YEAR'' THEN ''YEARS''
                                           ELSE CONVERT(NVARCHAR(10), CONVERT(INT, ''Unrecognized SYSTEM_VERSIONING retention unit: '' + ISNULL(st.history_retention_period_unit_desc, CONVERT(NVARCHAR(20), st.history_retention_period_unit))))
                                         END
                                    END
      FROM sys.tables st WITH (NOLOCK)
      WHERE st.[object_id] = @p_ObjId;',
    N'@p_ObjId INT, @p_RetentionText NVARCHAR(50) OUTPUT',
    @p_ObjId = @v_ObjectId, @p_RetentionText = @v_HistRetentionText OUTPUT

-- sys.stats.is_temporary is a 2012 column, so a STATIC reference is a CREATE-time "invalid column" error on a
-- pre-2012 binary. Stage the temporary-stat keys via a fn_ServerMajorVersion()>=11 guarded dynamic INSERT (empty
-- below 2012, where temporary statistics cannot exist) and exclude them from the extracted stats list below,
-- matching the JSON twin's `is_temporary = 0` filter (temporary stats appear on Always On readable secondaries).
-- sys.columns.graph_type is 2017+, so a STATIC reference is a CREATE-time "invalid column" error on an
-- older binary. Stage the graph pseudo-column ids behind a fn_ServerMajorVersion()>=14 guard -- empty
-- below 2017, where graph tables cannot exist. graph_type is the only reliable discriminator: these
-- columns report generated_always_type = 0 like any user column, and the four $-prefixed ones are
-- is_hidden = 0. Their names end in a per-table GUID, so emitting them yields a package that cannot be
-- deployed anywhere. Mirrors the JSON twin's `sc.graph_type IS NULL` filter.
-- sys.tables.is_node / is_edge are 2017+, staged behind the same guard as #GraphCols and simply
-- 'None' below 2017, where graph tables cannot exist.
DECLARE @v_GraphType NVARCHAR(10) = NULL
IF SchemaSmith.fn_ServerMajorVersion() >= 14
  EXEC sp_executesql N'SELECT @p_GraphType = CASE WHEN is_node = 1 THEN ''Node'' WHEN is_edge = 1 THEN ''Edge'' END FROM sys.tables WITH (NOLOCK) WHERE [object_id] = @p_ObjId',
    N'@p_ObjId INT, @p_GraphType NVARCHAR(10) OUTPUT', @p_ObjId = @v_ObjectId, @p_GraphType = @v_GraphType OUTPUT

-- Ledger is 2022, staged the same way and simply NULL below it.
DECLARE @v_Ledger NVARCHAR(12) = NULL
IF SchemaSmith.fn_ServerMajorVersion() >= 16
  EXEC sp_executesql N'SELECT @p_Ledger = CASE ledger_type_desc WHEN ''APPEND_ONLY_LEDGER_TABLE'' THEN ''AppendOnly'' WHEN ''UPDATABLE_LEDGER_TABLE'' THEN ''Updatable'' END FROM sys.tables WITH (NOLOCK) WHERE [object_id] = @p_ObjId',
    N'@p_ObjId INT, @p_Ledger NVARCHAR(12) OUTPUT', @p_ObjId = @v_ObjectId, @p_Ledger = @v_Ledger OUTPUT

-- Memory-optimized (Hekaton) is 2014 (major 12); is_memory_optimized / durability_desc are 2014 columns,
-- staged behind the >= 12 guard (like @v_GraphType/@v_Ledger) and simply 0/NULL below it, where a
-- memory-optimized table cannot exist. Without this the XML tier (compat-100 / genuine 2014) extracted a
-- memory-optimized table as an ordinary one -- the JSON twin reads these; the XML twin did not (#J1/#8).
DECLARE @v_MemoryOptimized BIT = 0, @v_Durability NVARCHAR(20) = NULL
IF SchemaSmith.fn_ServerMajorVersion() >= 12
  EXEC sp_executesql N'SELECT @p_Mo = ISNULL(is_memory_optimized, 0), @p_Dur = CASE WHEN is_memory_optimized = 1 THEN durability_desc END FROM sys.tables WITH (NOLOCK) WHERE [object_id] = @p_ObjId',
    N'@p_ObjId INT, @p_Mo BIT OUTPUT, @p_Dur NVARCHAR(20) OUTPUT', @p_ObjId = @v_ObjectId, @p_Mo = @v_MemoryOptimized OUTPUT, @p_Dur = @v_Durability OUTPUT

CREATE TABLE #GraphCols ([column_id] INT NOT NULL)
IF SchemaSmith.fn_ServerMajorVersion() >= 14
  EXEC sp_executesql N'INSERT INTO #GraphCols ([column_id]) SELECT column_id FROM sys.columns WITH (NOLOCK) WHERE graph_type IS NOT NULL AND [object_id] = @p_ObjId',
    N'@p_ObjId INT', @p_ObjId = @v_ObjectId

CREATE TABLE #TempStats ([object_id] INT NOT NULL, stats_id INT NOT NULL)
IF SchemaSmith.fn_ServerMajorVersion() >= 11
  EXEC sp_executesql N'INSERT INTO #TempStats ([object_id], stats_id) SELECT [object_id], stats_id FROM sys.stats WITH (NOLOCK) WHERE is_temporary = 1 AND [object_id] = @p_ObjId',
    N'@p_ObjId INT', @p_ObjId = @v_ObjectId

-- sys.fulltext_index_columns.statistical_semantics is a 2012 column, so the same CREATE-time hazard as
-- #TempStats above applies -- staged the same way. Empty below 2012, where semantic indexing does not exist.
CREATE TABLE #SemanticCols ([object_id] INT NOT NULL, column_id INT NOT NULL)
IF SchemaSmith.fn_ServerMajorVersion() >= 11
  EXEC sp_executesql N'INSERT INTO #SemanticCols ([object_id], column_id) SELECT [object_id], column_id FROM sys.fulltext_index_columns WITH (NOLOCK) WHERE statistical_semantics = 1 AND [object_id] = @p_ObjId',
    N'@p_ObjId INT', @p_ObjId = @v_ObjectId

-- sys.hash_indexes is 2014 (major 12) -- a memory-optimized hash index's bucket_count. Staged behind the
-- >= 12 guard and empty below it, where hash indexes cannot exist, so the index subquery below can LEFT JOIN
-- it unconditionally (#J1/#8; the JSON twin reads bucket_count as a scalar subquery).
CREATE TABLE #HashIndexMeta ([index_id] INT NOT NULL, [bucket_count] BIGINT NULL)
IF SchemaSmith.fn_ServerMajorVersion() >= 12
  EXEC sp_executesql N'INSERT INTO #HashIndexMeta ([index_id], [bucket_count]) SELECT index_id, bucket_count FROM sys.hash_indexes WITH (NOLOCK) WHERE [object_id] = @p_ObjId',
    N'@p_ObjId INT', @p_ObjId = @v_ObjectId
;WITH XMLNAMESPACES ('http://james.newtonking.com/projects/json' AS json)
SELECT '[' + TABLE_SCHEMA + ']' AS [Schema],
       '[' + TABLE_NAME + ']' AS [Name],
       -- Mirrors the JSON proc's per-partition aggregation (see GenerateTableJson.sql): sys.partitions
       -- is one row per partition, and a scalar read raised Msg 512 on a partitioned table. A shared
       -- value round-trips; non-uniform compression across partitions emits the 'MIXED' sentinel.
       COALESCE((SELECT CASE COUNT(DISTINCT p.data_compression_desc)
                           WHEN 0 THEN NULL
                           WHEN 1 THEN MIN(p.data_compression_desc)
                           ELSE 'MIXED'
                         END COLLATE DATABASE_DEFAULT
                   FROM sys.partitions AS p WITH (NOLOCK)
                   WHERE p.[object_id] = st.[object_id]
                     AND p.index_id < 2), 'NONE') AS [CompressionType],
       -- Filegroup placement (#filegroups) -- see JSON twin (GenerateTableJson.sql) for the
       -- emit-only-when-non-default rationale. Filegroups predate every supported SQL Server version, so
       -- (unlike temporal above) this needs no version gate and no staged dynamic-SQL variable.
       (SELECT '[' + fg.[name] + ']'
          FROM sys.indexes tfg WITH (NOLOCK)
          JOIN sys.filegroups fg WITH (NOLOCK) ON fg.data_space_id = tfg.data_space_id
         WHERE tfg.[object_id] = st.[object_id]
           AND tfg.index_id IN (0, 1)
           AND fg.is_default = 0) AS [FileGroup],
       -- Partition placement (#partitioning, K1): the scheme NAME and the column the function is applied
       -- to, read from the table's own data space (heap/clustered index, index_id 0/1). Before this the
       -- [FileGroup] read above joined sys.filegroups on that same data_space_id and simply found no row
       -- when the data space was a partition SCHEME -- so a partitioned table extracted as an ordinary one,
       -- cleanly and silently. Emitted only when the data space really is a scheme, so an unpartitioned
       -- table gains no key and every committed package keeps extracting byte-identically.
       --
       -- sys.data_spaces.type = 'PS' and sys.index_columns.partition_ordinal both predate the supported
       -- floor, so no version gate. partition_ordinal = 1 because SQL Server partitions on ONE column.
       (SELECT '[' + ds.[name] + ']'
          FROM sys.indexes tps WITH (NOLOCK)
          JOIN sys.data_spaces ds WITH (NOLOCK) ON ds.data_space_id = tps.data_space_id
         WHERE tps.[object_id] = st.[object_id]
           AND tps.index_id IN (0, 1)
           AND ds.[type] = 'PS') AS [PartitionScheme],
       (SELECT '[' + pc.[name] + ']'
          FROM sys.indexes tps WITH (NOLOCK)
          JOIN sys.data_spaces ds WITH (NOLOCK) ON ds.data_space_id = tps.data_space_id
          JOIN sys.index_columns pic WITH (NOLOCK) ON pic.[object_id] = tps.[object_id]
                                                  AND pic.index_id = tps.index_id
                                                  AND pic.partition_ordinal = 1
          JOIN sys.columns pc WITH (NOLOCK) ON pc.[object_id] = pic.[object_id]
                                           AND pc.column_id = pic.column_id
         WHERE tps.[object_id] = st.[object_id]
           AND tps.index_id IN (0, 1)
           AND ds.[type] = 'PS') AS [PartitionColumn],
       -- FILESTREAM_ON -- not implied by having FILESTREAM columns; the assignment outlives them.
       (SELECT ds.[name] FROM sys.data_spaces ds WITH (NOLOCK)
         WHERE ds.data_space_id = st.filestream_data_space_id) AS [FileStreamFileGroup],
       -- TEXTIMAGE_ON. Like FILESTREAM_ON above, read from the table's own data space rather than
       -- inferred from its columns -- dropping the last large-object column leaves the assignment.
       -- Only emitted when it is NOT the default filegroup, so an ordinary table gains no key.
       (SELECT lds.[name] FROM sys.data_spaces lds WITH (NOLOCK)
         JOIN sys.filegroups lfg WITH (NOLOCK) ON lfg.data_space_id = lds.data_space_id AND lfg.is_default = 0
        WHERE lds.data_space_id = st.lob_data_space_id) AS [TextImageFileGroup],
       CASE WHEN st.is_tracked_by_cdc = 1 THEN 'true' ELSE 'false' END AS [EnableCDC],
       @v_GraphType AS [GraphType],
       @v_Ledger AS [Ledger],
       -- Memory-optimized round-trip (#J1/#8): emit only when true, matching the JSON twin. Read into
       -- @v_MemoryOptimized/@v_Durability via the version-gated pre-stage above (0/NULL below 2014).
       CASE WHEN @v_MemoryOptimized = 1 THEN 'true' END AS [MemoryOptimized],
       CASE WHEN @v_MemoryOptimized = 1 THEN @v_Durability END AS [Durability],
       -- Table-level Change Tracking round-trip -- emitted only when ON, like IsTemporal above.
       CASE WHEN ctt.[object_id] IS NOT NULL THEN 'true' END AS [EnableChangeTracking],
       CASE WHEN ctt.is_track_columns_updated_on = 1 THEN 'true' END AS [TrackColumnsUpdated],
       -- System-versioning round-trip (#369): emit IsTemporal only when true. sys.tables.temporal_type is
       -- 2016+, so it is read into @v_IsTemporal via the version-gated dynamic pre-stage above (0 below 2016).
       CASE WHEN @v_IsTemporal = 1 THEN 'true' END AS [IsTemporal],
       -- History table identity/retention -- populated by the version-gated dynamic block above (NULL pre-2016).
       CASE WHEN @v_HistTableSchema IS NOT NULL THEN '[' + @v_HistTableSchema + ']' END AS [HistoryTableSchema],
       CASE WHEN @v_HistTableName IS NOT NULL THEN '[' + @v_HistTableName + ']' END AS [HistoryTableName],
       @v_HistRetentionText AS [HistoryRetentionPeriod],
       -- Sticky drop-protection marker (only when set true). Read from the PreventDrop extended property. #270
       CASE WHEN (SELECT CONVERT(NVARCHAR(50), [value])
                    FROM fn_listextendedproperty(N'PreventDrop', N'Schema', @p_Schema, N'Table', @p_Table, default, default)) = 'true'
            THEN 'true' END AS [PreventDrop],
       '' AS [OldName],
       (SELECT 'true' AS [@json:Array],
                       '[' + c.COLUMN_NAME + ']' AS [Name],
                       UPPER(USER_TYPE) + SchemaSmith.fn_ColumnTypeArguments(USER_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION,
                                               CASE WHEN sc.xml_collection_id <> 0
                                                    THEN (SELECT '[' + SCHEMA_NAME(xc.[schema_id]) + '].[' + xc.[name] + ']' FROM sys.xml_schema_collections xc WHERE xc.xml_collection_id = sc.xml_collection_id)
                                                    END,
                                               sc.is_rowguidcol) +
                                          CASE WHEN ic.column_id IS NOT NULL
                                               THEN ' IDENTITY(' + CONVERT(NVARCHAR(20), ic.seed_value) + ', ' + CONVERT(NVARCHAR(20), ic.increment_value) + ')' +
                                                    CASE WHEN ic.is_not_for_replication = 1 THEN ' NOT FOR REPLICATION' ELSE '' END
                                               ELSE '' END AS [DataType],
                       CASE WHEN c.IS_NULLABLE = 'Yes' THEN 'true' ELSE 'false' END AS [Nullable],
		               NULLIF(SchemaSmith.fn_StripParenWrapping(COLUMN_DEFAULT), 'NULL') AS [Default],
                       (SELECT SchemaSmith.fn_StripParenWrapping([definition])
                          FROM sys.check_constraints WITH (NOLOCK)
                          WHERE parent_object_id = st.[object_id]
                            AND parent_column_id = sc.column_id) AS [CheckExpression],
                       SchemaSmith.fn_StripParenWrapping(cc.[definition]) AS ComputedExpression,
                       CASE WHEN ISNULL(cc.is_persisted, 0) = 1 THEN 'true' ELSE 'false' END AS [Persisted],
                       CASE WHEN sc.is_sparse = 1 THEN 'true' ELSE 'false' END AS [Sparse],
                       -- FILESTREAM round-trip -- emitted only when set.
                       CASE WHEN sc.is_filestream = 1 THEN 'true' END AS [FileStream],
                       CASE WHEN sc.is_column_set = 1 THEN 'true' ELSE 'false' END AS [IsColumnSet],
                       ISNULL(NULLIF(ic.COLLATION_NAME, @v_DatabaseCollation), '') AS [Collation],
                       ISNULL(cm.MaskingFunction, '') COLLATE DATABASE_DEFAULT AS DataMaskFunction,
                       ISNULL(cm.EncryptionType, 'NONE') COLLATE DATABASE_DEFAULT AS EncryptionType,
                       ISNULL(cm.EncryptionKey, '') COLLATE DATABASE_DEFAULT AS EncryptionKey,
                       ISNULL(cm.EncryptionAlgorithm, '') COLLATE DATABASE_DEFAULT AS EncryptionAlgorithm,
                       '' AS [OldName],
                       (SELECT ep.[Name] AS [@n], CONVERT(NVARCHAR(MAX), ep.[Value]) AS [*]
                          FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Column', c.COLUMN_NAME) ep
                          WHERE ep.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)
                          FOR XML PATH('p'), ROOT('ExtendedProperties'), TYPE) AS [Extensions]
                  FROM INFORMATION_SCHEMA.COLUMNS c WITH (NOLOCK)
                  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = st.[object_id] AND sc.[name] = c.COLUMN_NAME
                  JOIN (SELECT CASE WHEN SCHEMA_NAME(typ.[schema_id]) IN ('sys', 'dbo')
                                    THEN '' ELSE SCHEMA_NAME(typ.[schema_id]) + '.' END + typ.[name] AS USER_TYPE, typ.user_type_id
                          FROM sys.types typ WITH (NOLOCK)) ut ON ut.user_type_id = sc.user_type_id
                  LEFT JOIN sys.computed_columns cc WITH (NOLOCK) ON cc.[object_id] = st.[object_id]
                                                                 AND cc.[name] = c.COLUMN_NAME
                  LEFT JOIN sys.identity_columns ic WITH (NOLOCK) ON ic.[object_id] = st.[object_id]
                                                                 AND ic.[Name] = c.COLUMN_NAME
                  LEFT JOIN #ColMeta cm ON cm.[column_id] = sc.column_id
                  WHERE c.TABLE_SCHEMA = t.TABLE_SCHEMA
                    AND c.TABLE_NAME = t.TABLE_NAME
                    -- Exclude the temporal period columns (GENERATED ALWAYS AS ROW START/END); regenerated
                    -- from IsTemporal on apply (#369). generated_always_type is 2016+ -> staged into #ColMeta
                    -- (0 below 2016, where no period columns exist).
                    AND ISNULL(cm.GeneratedAlwaysType, 0) = 0
                    -- and the graph pseudo-columns staged above (empty below 2017).
                    AND NOT EXISTS (SELECT 1 FROM #GraphCols g WITH (NOLOCK) WHERE g.[column_id] = sc.column_id)
                  ORDER BY c.COLUMN_NAME
                  FOR XML PATH('Columns'), TYPE),
       (SELECT 'true' AS [@json:Array],
               '[' + [Name] + ']' AS [Name],
               -- Same per-partition aggregation as the table-level [CompressionType] above.
               (SELECT CASE COUNT(DISTINCT p.data_compression_desc)
                          WHEN 0 THEN NULL
                          WHEN 1 THEN MIN(p.data_compression_desc)
                          ELSE 'MIXED'
                        END COLLATE DATABASE_DEFAULT
                  FROM sys.partitions AS p WITH (NOLOCK)
                  WHERE p.[object_id] = si.[object_id]
                    AND p.index_id = si.index_id) AS [CompressionType],
               -- Memory-optimized hash-index bucket count (#J1/#8): emit only for a hash index, matching the
               -- JSON twin. #HashIndexMeta is empty below 2014, so this is NULL (dropped) on the old binaries
               -- the XML tier also serves.
               (SELECT him.[bucket_count] FROM #HashIndexMeta him WHERE him.[index_id] = si.index_id) AS [BucketCount],
               -- Same emit-only-when-non-default rule as the table-level [FileGroup] above -- see JSON twin.
               (SELECT '[' + fg.[name] + ']'
                  FROM sys.filegroups fg WITH (NOLOCK)
                 WHERE fg.data_space_id = si.data_space_id
                   AND fg.is_default = 0) AS [FileGroup],
               -- Partition placement (#partitioning, K1): read from si's OWN data space, independently of
               -- the table's. An index is not required to be aligned -- a nonclustered index on a
               -- partitioned table may sit on one filegroup, and an index on an ordinary heap may itself be
               -- partitioned -- so inferring either from the other would lose a real design.
               (SELECT '[' + ds.[name] + ']'
                  FROM sys.data_spaces ds WITH (NOLOCK)
                 WHERE ds.data_space_id = si.data_space_id
                   AND ds.[type] = 'PS') AS [PartitionScheme],
               (SELECT '[' + pc.[name] + ']'
                  FROM sys.data_spaces ds WITH (NOLOCK)
                  JOIN sys.index_columns pic WITH (NOLOCK) ON pic.[object_id] = si.[object_id]
                                                          AND pic.index_id = si.index_id
                                                          AND pic.partition_ordinal = 1
                  JOIN sys.columns pc WITH (NOLOCK) ON pc.[object_id] = pic.[object_id]
                                                   AND pc.column_id = pic.column_id
                 WHERE ds.data_space_id = si.data_space_id
                   AND ds.[type] = 'PS') AS [PartitionColumn],
               CASE WHEN is_primary_key = 1 THEN 'true' ELSE 'false' END AS [PrimaryKey],
               CASE WHEN is_unique = 1 THEN 'true' ELSE 'false' END AS [Unique],
               CASE WHEN is_unique_constraint = 1 THEN 'true' ELSE 'false' END AS [UniqueConstraint],
               CASE WHEN [type] IN (1, 5) THEN 'true' ELSE 'false' END AS [Clustered],
               CASE WHEN [type] IN (5, 6) THEN 'true' ELSE 'false' END AS [ColumnStore],
               CASE WHEN fill_factor = 100 THEN 0 ELSE fill_factor END AS [FillFactor],
               CASE WHEN ignore_dup_key = 1 THEN 'true' ELSE 'false' END AS [IgnoreDuplicateKey],
               CASE WHEN is_padded = 1 THEN 'true' ELSE 'false' END AS [PadIndex],
               STUFF((SELECT ',' + '[' + COL_NAME(ic.[object_id], ic.column_id) + ']' + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                  FROM sys.index_columns ic WITH (NOLOCK)
                  WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 0
                  ORDER BY key_ordinal FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') AS [IndexColumns],
               STUFF((SELECT ',' + '[' + COL_NAME(ic.[object_id], ic.column_id) + ']'
                  FROM sys.index_columns ic WITH (NOLOCK)
                  WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1
                  ORDER BY index_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') AS [IncludeColumns],
			   CASE WHEN has_filter = 1 THEN SchemaSmith.fn_StripParenWrapping(filter_definition) ELSE NULL END AS [FilterExpression],
               (SELECT ep.[Name] AS [@n], CONVERT(NVARCHAR(MAX), ep.[Value]) AS [*]
                  FROM (SELECT ISNULL(i.[Name], c.[Name]) AS [Name], RTRIM(COALESCE(CONVERT(NVARCHAR(MAX), c.[Value]) + ' ', '') + COALESCE(CONVERT(NVARCHAR(MAX), i.[Value]), '')) AS [Value]
                          FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Index', si.[Name]) i
                          FULL OUTER JOIN fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Constraint', si.[Name]) c ON i.[Name] = c.[Name]) ep
                  WHERE ep.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)
                  FOR XML PATH('p'), ROOT('ExtendedProperties'), TYPE) AS [Extensions]
          FROM sys.indexes si WITH (NOLOCK)
          WHERE si.[object_id] = st.[object_id]
            AND NOT EXISTS (SELECT * FROM sys.xml_indexes xi WITH (NOLOCK) WHERE xi.[object_id] = si.[object_id] AND xi.index_id = si.index_id)
            AND is_hypothetical = 0
            AND is_disabled = 0
            AND index_id > 0
            -- GRAPH_UNIQUE_INDEX_<guid> over the graph_id column: excluding the columns alone
            -- would leave an index pointing at one that is no longer declared.
            AND NOT EXISTS (SELECT 1 FROM sys.index_columns gic WITH (NOLOCK)
                            JOIN #GraphCols g WITH (NOLOCK) ON g.[column_id] = gic.column_id
                           WHERE gic.[object_id] = si.[object_id] AND gic.index_id = si.index_id)
          ORDER BY [Name]
          FOR XML PATH('Indexes'), TYPE),
       (SELECT 'true' AS [@json:Array],
               '[' + i.[name] COLLATE DATABASE_DEFAULT + ']' AS [Name],
               '[' + COL_NAME(i.[Object_id], ic.column_id) + ']' AS [Column],
               CASE WHEN i.using_xml_index_id IS NULL THEN 'true' ELSE 'false' END AS [IsPrimary],
               (SELECT '[' + [Name] COLLATE DATABASE_DEFAULT + ']' FROM sys.xml_indexes i2 WHERE i2.[object_id] = i.[object_id] AND i2.index_id = i.using_xml_index_id AND i.using_xml_index_id IS NOT NULL) AS [PrimaryIndex],
               i.secondary_type_desc COLLATE DATABASE_DEFAULT AS [SecondaryIndexType],
               (SELECT ep.[Name] AS [@n], CONVERT(NVARCHAR(MAX), ep.[Value]) AS [*]
                  FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Index', i.[Name]) ep
                  WHERE ep.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)
                  FOR XML PATH('p'), ROOT('ExtendedProperties'), TYPE) AS [Extensions]
          FROM sys.xml_indexes i WITH (NOLOCK)
          JOIN sys.index_columns ic WITH (NOLOCK) ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id
          WHERE i.[object_id] = st.[object_id]
          ORDER BY i.[Name]
          FOR XML PATH('XmlIndexes'), TYPE),
	   (SELECT 'true' AS [@json:Array],
               '[' + [Name] + ']' AS [Name],
               STUFF((SELECT ',' + '[' + COL_NAME(fc.[parent_object_id], fc.parent_column_id) + ']'
                            FROM sys.foreign_key_columns fc WITH (NOLOCK)
                            WHERE fk.[object_id] = fc.[constraint_object_id]
                            ORDER BY fc.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') AS [Columns],
               '[' + OBJECT_SCHEMA_NAME(referenced_object_id) + ']' AS RelatedTableSchema,
               '[' + OBJECT_NAME(referenced_object_id) + ']' AS RelatedTable,
               STUFF((SELECT ',' + '[' + COL_NAME(fc.[referenced_object_id], fc.referenced_column_id) + ']'
                            FROM sys.foreign_key_columns fc WITH (NOLOCK)
                            WHERE fk.[object_id] = fc.[constraint_object_id]
                            ORDER BY fc.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') AS [RelatedColumns],
               REPLACE(fk.delete_referential_action_desc, '_', ' ') COLLATE DATABASE_DEFAULT AS [DeleteAction],
               REPLACE(fk.update_referential_action_desc, '_', ' ') COLLATE DATABASE_DEFAULT AS [UpdateAction],
               (SELECT ep.[Name] AS [@n], CONVERT(NVARCHAR(MAX), ep.[Value]) AS [*]
                  FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Constraint', fk.[Name]) ep
                  WHERE ep.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)
                  FOR XML PATH('p'), ROOT('ExtendedProperties'), TYPE) AS [Extensions]
          FROM sys.foreign_keys fk WITH (NOLOCK)
          WHERE fk.parent_object_id = st.[object_id]
          ORDER BY [Name]
          FOR XML PATH('ForeignKeys'), TYPE),
       (SELECT 'true' AS [@json:Array],
               '[' + [Name] + ']' AS [Name],
               STUFF((SELECT ',' + '[' + COL_NAME(sc.[object_id], sc.column_id) + ']'
                  FROM sys.stats_columns sc WITH (NOLOCK)
                  WHERE s.[object_id] = sc.[object_id] AND s.stats_id = sc.stats_id
                  ORDER BY sc.stats_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') AS [Columns],
               SchemaSmith.fn_StripParenWrapping([filter_definition]) AS FilterExpression,
               (SELECT ep.[Name] AS [@n], CONVERT(NVARCHAR(MAX), ep.[Value]) AS [*]
                  FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Statistic', s.[Name]) ep
                  WHERE ep.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)
                  FOR XML PATH('p'), ROOT('ExtendedProperties'), TYPE) AS [Extensions]
          FROM sys.stats s WITH (NOLOCK)
          WHERE [object_id] = st.[object_id]
            AND auto_created = 0
            AND user_created = 1
            AND NOT EXISTS (SELECT 1 FROM #TempStats ts WITH (NOLOCK) WHERE ts.[object_id] = s.[object_id] AND ts.stats_id = s.stats_id)  -- exclude 2012+ temporary stats (staged above; empty pre-2012)
            AND [Name] NOT LIKE 'stat[_]%'
            AND [Name] NOT LIKE 'hind[_]%'
          ORDER BY [Name]
          FOR XML PATH('Statistics'), TYPE),
       (SELECT 'true' AS [@json:Array],
               '[' + [Name] + ']' AS [Name],
               SchemaSmith.fn_StripParenWrapping([definition]) AS [Expression],
               (SELECT ep.[Name] AS [@n], CONVERT(NVARCHAR(MAX), ep.[Value]) AS [*]
                  FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Constraint', cc.[Name]) ep
                  WHERE ep.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)
                  FOR XML PATH('p'), ROOT('ExtendedProperties'), TYPE) AS [Extensions]
          FROM sys.check_constraints cc WITH (NOLOCK)
          WHERE parent_object_id = st.[object_id]
            AND parent_column_id = 0
          ORDER BY [Name]
          FOR XML PATH('CheckConstraints'), TYPE),
       (SELECT FullTextCatalog = '[' + (SELECT c.[name] FROM sys.fulltext_catalogs c WITH (NOLOCK) WHERE c.fulltext_catalog_id = fi.fulltext_catalog_id) + ']',
               KeyIndex = '[' + (SELECT i.[Name] FROM sys.indexes i WITH (NOLOCK) WHERE i.[object_id] = fi.[object_id] AND i.[index_id] = fi.[unique_index_id]) + ']',
               ChangeTracking = change_tracking_state_desc,
               [StopList] = '[' + (SELECT fs.[name] FROM sys.fulltext_stoplists fs WITH (NOLOCK) WHERE fs.stoplist_id = fi.stoplist_id) + ']',
               STUFF((SELECT ',' + '[' + COL_NAME(fc.[object_id], fc.column_id) + ']' +
                                       CASE WHEN fc.type_column_id IS NOT NULL
                                            THEN ' TYPE COLUMN [' + COL_NAME(fc.[object_id], fc.type_column_id) + ']'
                                            ELSE '' END +
                                       -- Full-text LANGUAGE churn: same emit-only-when-non-default rule and
                                       -- byte-identical contract as the JSON twin (GenerateTableJson.sql). Kept
                                       -- as a JOIN (not a subquery) here too even though FOR XML PATH's
                                       -- correlated-subquery form would compile -- the two rendering forms must
                                       -- never be allowed to diverge again. NULL collation (non-character
                                       -- column) has no default to compare against, so LANGUAGE is always
                                       -- emitted for it -- see GenerateTableJson.sql for the full rationale.
                                       CASE WHEN c.collation_name IS NULL OR fc.language_id <> COLLATIONPROPERTY(c.collation_name, 'LCID')
                                            THEN ' LANGUAGE ' + CAST(fc.language_id AS NVARCHAR(10))
                                            ELSE '' END
                                       + CASE WHEN EXISTS (SELECT 1 FROM #SemanticCols sc
                                                             WHERE sc.[object_id] = fc.[object_id] AND sc.column_id = fc.column_id)
                                              THEN ' STATISTICAL_SEMANTICS' ELSE '' END
                  FROM sys.fulltext_index_columns fc WITH (NOLOCK)
                  JOIN sys.columns c WITH (NOLOCK) ON c.[object_id] = fc.[object_id] AND c.column_id = fc.column_id
                  WHERE fi.[object_id] = fc.[object_id]
                  ORDER BY COL_NAME(fc.[object_id], fc.column_id) FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') AS [Columns]
          FROM sys.fulltext_indexes fi WITH (NOLOCK)
          WHERE fi.[object_id] = st.[object_id]
          FOR XML PATH('FullTextIndex'), TYPE),
       (SELECT ep.[Name] AS [@n], CONVERT(NVARCHAR(MAX), ep.[Value]) AS [*]
          FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, default, default) ep
          WHERE ep.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)
          FOR XML PATH('p'), ROOT('ExtendedProperties'), TYPE) AS [Extensions]
  FROM INFORMATION_SCHEMA.TABLES t WITH (NOLOCK)
  JOIN sys.tables st WITH (NOLOCK) ON st.[object_id] = OBJECT_ID(@p_Schema + '.' + @p_Table)
  LEFT JOIN sys.change_tracking_tables ctt WITH (NOLOCK) ON ctt.[object_id] = st.[object_id]
  WHERE TABLE_NAME = @p_Table
    AND TABLE_SCHEMA = @p_Schema
  FOR XML PATH('Table')
