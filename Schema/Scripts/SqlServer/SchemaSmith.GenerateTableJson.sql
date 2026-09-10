-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.GenerateTableJSON', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.GenerateTableJSON
GO
CREATE PROCEDURE SchemaSmith.GenerateTableJSON 
  @p_Schema SYSNAME = 'dbo',
  @p_Table SYSNAME,
  @p_ObjectOrder SYSNAME = 'Name'
  -- 'Name' (default, alphabetical) or 'Physical' (the table's own column order).
  --
  -- COLUMNS ONLY, and that is the whole story rather than a layering detail. Indexes, foreign keys,
  -- check constraints, statistics and XML indexes are ALWAYS emitted in name order below, whatever
  -- this argument says: 'Physical' has no meaning for a set with no physical sequence. This proc is
  -- the only thing that orders them -- nothing re-sequences the lists after it returns. A comment
  -- here used to say the caller did, and it was describing a helper that had no callers.
AS
SET NOCOUNT ON
DECLARE @v_DatabaseCollation NVARCHAR(200) = CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS NVARCHAR(200))
-- SchemaSmith-internal extended properties to exclude from extraction (one-line change to add new names)
DECLARE @InternalEPNames TABLE ([Name] NVARCHAR(128))
INSERT @InternalEPNames VALUES (N'ProductName'), (N'PreventDrop')  -- PreventDrop is a SchemaSmith ownership marker, not a user extended property (#270)

-- Ledger (#ledger) is SQL Server 2022, and this proc is a plain CREATE PROCEDURE -- every column it
-- names is bound when the proc is created, so a static sys.tables.ledger_type_desc reference would fail
-- to create the helper at all on a 2017 or 2019 target. Read it through a version-guarded dynamic
-- statement instead, which stays NULL below 2022 where ledger tables cannot exist. Same pattern the XML
-- twin already uses for its 2016/2017-only reads.
DECLARE @v_Ledger NVARCHAR(12) = NULL
IF SchemaSmith.fn_ServerMajorVersion() >= 16
  EXEC sp_executesql N'
    SELECT @p_Ledger = CASE ledger_type_desc WHEN ''APPEND_ONLY_LEDGER_TABLE'' THEN ''AppendOnly''
                                             WHEN ''UPDATABLE_LEDGER_TABLE'' THEN ''Updatable'' END
      FROM sys.tables WITH (NOLOCK)
     WHERE [object_id] = OBJECT_ID(@p_Schema + ''.'' + @p_Table);',
    N'@p_Schema NVARCHAR(128), @p_Table NVARCHAR(128), @p_Ledger NVARCHAR(12) OUTPUT',
    @p_Schema = @p_Schema, @p_Table = @p_Table, @p_Ledger = @v_Ledger OUTPUT
SELECT [Line] FROM SchemaSmith.fn_FormatJson(REPLACE(REPLACE(REPLACE((
SELECT '[' + TABLE_SCHEMA + ']' AS [Schema],
       '[' + TABLE_NAME + ']' AS [Name],
       -- sys.partitions is one row PER PARTITION, and compression can legitimately differ across
       -- partitions of the same index -- a scalar read here raised Msg 512 on a partitioned table.
       -- Aggregate instead: a single shared value round-trips as before; non-uniform compression
       -- emits 'MIXED', a sentinel outside Quench's managed NONE/ROW/PAGE/COLUMNSTORE* set so
       -- re-deploy leaves an already-mixed table alone rather than flattening it to one value.
       COALESCE((SELECT CASE COUNT(DISTINCT p.data_compression_desc)
                           WHEN 0 THEN NULL
                           WHEN 1 THEN MIN(p.data_compression_desc)
                           ELSE 'MIXED'
                         END COLLATE DATABASE_DEFAULT
                   FROM sys.partitions AS p WITH (NOLOCK)
                   WHERE p.[object_id] = st.[object_id]
                     AND p.index_id < 2), 'NONE') AS [CompressionType],
       -- {{XmlCompressionRead}} resolves to p.xml_compression on SQL Server 2025+ and to a NULL literal
       -- below it, because the COLUMN DOES NOT EXIST before 2025 and naming it would stop this procedure
       -- being created at all. Resolved at kindle time by ForgeKindler, which knows the server version
       -- before it creates anything; see the comment there. NULL means "this server cannot report it",
       -- which SchemaTongs turns into "keep what the package already said" rather than a silent drop.
       (SELECT CASE WHEN MAX(CONVERT(TINYINT, {{XmlCompressionRead}})) = 1 THEN CONVERT(BIT, 1) END
          FROM sys.partitions AS p WITH (NOLOCK)
          WHERE p.[object_id] = st.[object_id]
            AND p.index_id < 2) AS [XmlCompression],
       -- Filegroup placement (#filegroups): emit only when the table's data (heap/clustered index,
       -- index_id 0/1) lives on a non-default filegroup, so an ordinary table on PRIMARY (or whatever
       -- the target's default filegroup is) stays exactly as minimal as before this change. Filegroups
       -- predate every supported SQL Server version -- no version gate needed.
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
       -- FILESTREAM_ON. Read from the table's filestream data space, which is NOT implied by having
       -- FILESTREAM columns: dropping the last one leaves the assignment behind.
       (SELECT ds.[name] FROM sys.data_spaces ds WITH (NOLOCK)
         WHERE ds.data_space_id = st.filestream_data_space_id) AS [FileStreamFileGroup],
       -- TEXTIMAGE_ON. Like FILESTREAM_ON above, read from the table's own data space rather than
       -- inferred from its columns -- dropping the last large-object column leaves the assignment.
       -- Only emitted when it is NOT the default filegroup, so an ordinary table gains no key.
       (SELECT lds.[name] FROM sys.data_spaces lds WITH (NOLOCK)
         JOIN sys.filegroups lfg WITH (NOLOCK) ON lfg.data_space_id = lds.data_space_id AND lfg.is_default = 0
        WHERE lds.data_space_id = st.lob_data_space_id) AS [TextImageFileGroup],
       st.is_tracked_by_cdc AS [EnableCDC],
       -- Graph tables (#graph). Emitted only when the table IS one, so no existing package gains a
       -- "GraphType": "None" on every table. is_node/is_edge are 2017+, which the JSON tier requires.
       CASE WHEN st.is_node = 1 THEN 'Node' WHEN st.is_edge = 1 THEN 'Edge' END AS [GraphType],
       -- Memory-optimized (#J1). is_memory_optimized and durability_desc are SQL Server 2014 columns,
       -- referenced statically here because the JSON extraction path only ever runs on 2016+ (OPENJSON) --
       -- the XML path is the one that reaches 2008-2012 and gates them. Emitted only when the table IS
       -- memory-optimized, so an ordinary table's package gains no key.
       CASE WHEN st.is_memory_optimized = 1 THEN CONVERT(BIT, 1) END AS [MemoryOptimized],
       CASE WHEN st.is_memory_optimized = 1 THEN st.durability_desc END AS [Durability],
       -- Ledger (#ledger, 2022+). Emitted only when the table IS one. ledger_type_desc is 2022, so
       -- it is read through a version-gated helper rather than referenced here -- see @v_Ledger.
       @v_Ledger AS [Ledger],
       -- Table-level Change Tracking round-trip. Emitted only when ON, like IsTemporal above: every
       -- extracted package would otherwise gain "EnableChangeTracking": false on every table.
       -- sys.change_tracking_tables shipped with Change Tracking in 2008, so it is safe to read
       -- statically at the floor -- SchemaSmith.fn_RebuildBlockedReason already does.
       CASE WHEN ctt.[object_id] IS NOT NULL THEN CAST(1 AS BIT) END AS [EnableChangeTracking],
       CASE WHEN ctt.is_track_columns_updated_on = 1 THEN CAST(1 AS BIT) END AS [TrackColumnsUpdated],
       -- System-versioning round-trip (#369): emit IsTemporal so an extracted temporal table re-deploys
       -- as temporal (previously omitted -> silently lost on round-trip). Only when true, to keep non-
       -- temporal tables minimal. sys.tables.temporal_type is 2016+ (safe at the current 2017 floor;
       -- gate this + generated_always_type below when the SQL Server floor drops below 2016).
       CASE WHEN st.temporal_type = 2 THEN CAST(1 AS BIT) END AS [IsTemporal],
       -- History table identity/retention (#depth-gap): emit only when they deviate from SchemaSmith's own
       -- apply-side default (same schema, "<Table>_Hist", INFINITE retention) so a default-named temporal
       -- table's JSON stays exactly as minimal as it was before this change. history_table_id is 2016;
       -- history_retention_period(_unit_desc) are 2017 (system-versioned tables are 2016, a retention
       -- policy on them is 2017). Both are safe to reference STATICALLY here -- but because of the
       -- ENCODING gate, not the server floor: CompatEncoding selects JSON only at major >= 14 (the JSON
       -- path's STRING_AGG is 2017), so this proc never kindles on a binary lacking either. The XML twin
       -- has no such shield and must gate the retention reads at >= 14 explicitly; see the note there.
       CASE WHEN st.temporal_type = 2 AND (hs.[name] <> TABLE_SCHEMA OR h.[name] <> TABLE_NAME + '_Hist')
            THEN '[' + hs.[name] + ']' END AS [HistoryTableSchema],
       CASE WHEN st.temporal_type = 2 AND (hs.[name] <> TABLE_SCHEMA OR h.[name] <> TABLE_NAME + '_Hist')
            THEN '[' + h.[name] + ']' END AS [HistoryTableName],
       -- Reads history_retention_period_unit_desc ('DAY'/'WEEK'/'MONTH'/'YEAR'/'INFINITE') rather than the
       -- numeric history_retention_period_unit code: the desc needs no separately-maintained code table
       -- (its 4 finite values pluralize by simple string concatenation), which is exactly what went wrong
       -- here once already -- the numeric codes actually measured on a live server are 3/4/5/6 for
       -- DAY/WEEK/MONTH/YEAR, not the 1/2/3/4 a first pass assumed from documentation. An ELSE branch this
       -- CASE cannot reach today (the 5 values above are exhaustive) still forces a loud runtime error
       -- rather than a silently-dropped-to-NULL retention if Microsoft ever adds a unit: CONVERT(INT,
       -- <text>) always fails to convert non-numeric text, so the surrounding CONVERT(NVARCHAR(10), ...)
       -- keeps this branch's static type consistent with its siblings while still raising Msg 245 with the
       -- offending unit named in the message text.
       CASE WHEN st.temporal_type = 2 AND st.history_retention_period_unit_desc <> 'INFINITE'
            THEN CAST(st.history_retention_period AS NVARCHAR(10)) + ' ' +
                 CASE st.history_retention_period_unit_desc
                   WHEN 'DAY' THEN 'DAYS' WHEN 'WEEK' THEN 'WEEKS' WHEN 'MONTH' THEN 'MONTHS' WHEN 'YEAR' THEN 'YEARS'
                   ELSE CONVERT(NVARCHAR(10), CONVERT(INT, 'Unrecognized SYSTEM_VERSIONING retention unit: ' + ISNULL(st.history_retention_period_unit_desc, CONVERT(NVARCHAR(20), st.history_retention_period_unit))))
                 END
            END AS [HistoryRetentionPeriod],
       -- Emit the sticky drop-protection marker first-class (only when set true, so unprotected tables stay minimal).
       -- Read from the PreventDrop extended property (excluded from generic Extensions via @InternalEPNames). #270
       CASE WHEN (SELECT CONVERT(NVARCHAR(50), [value])
                    FROM fn_listextendedproperty(N'PreventDrop', N'Schema', @p_Schema, N'Table', @p_Table, default, default)) = 'true'
            THEN CAST(1 AS BIT) END AS [PreventDrop],
       '' AS [OldName],
       (SELECT *
          FROM (SELECT '[' + c.COLUMN_NAME + ']' AS [Name],
                       UPPER(USER_TYPE) + SchemaSmith.fn_ColumnTypeArguments(USER_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION,
                                               CASE WHEN sc.xml_collection_id <> 0
                                                    THEN (SELECT '[' + SCHEMA_NAME(xc.[schema_id]) + '].[' + xc.[name] + ']' FROM sys.xml_schema_collections xc WHERE xc.xml_collection_id = sc.xml_collection_id)
                                                    END,
                                               sc.is_rowguidcol) +
                                          CASE WHEN ic.column_id IS NOT NULL
                                               THEN ' IDENTITY(' + CONVERT(NVARCHAR(20), ic.seed_value) + ', ' + CONVERT(NVARCHAR(20), ic.increment_value) + ')' +
                                                    CASE WHEN ic.is_not_for_replication = 1 THEN ' NOT FOR REPLICATION' ELSE '' END
                                               ELSE '' END AS [DataType],                   
                       CAST(CASE WHEN c.IS_NULLABLE = 'Yes' THEN 1 ELSE 0 END AS BIT) AS [Nullable],
		               NULLIF(SchemaSmith.fn_StripParenWrapping(COLUMN_DEFAULT), 'NULL') AS [Default],
                       (SELECT SchemaSmith.fn_StripParenWrapping([definition])
                          FROM sys.check_constraints WITH (NOLOCK)
                          WHERE parent_object_id = st.[object_id]
                            AND parent_column_id = sc.column_id) AS [CheckExpression],
                       SchemaSmith.fn_StripParenWrapping(cc.[definition]) AS ComputedExpression,
                       ISNULL(cc.is_persisted, CAST(0 AS BIT)) AS [Persisted],
                       sc.is_sparse AS [Sparse],
                       -- FILESTREAM round-trip. Emitted only when set, so no existing package gains
                       -- a false on every column. Both catalog columns predate the 2008 floor.
                       CASE WHEN sc.is_filestream = 1 THEN CAST(1 AS BIT) END AS [FileStream],
                       sc.is_column_set AS [IsColumnSet],
                       ISNULL(NULLIF(ic.COLLATION_NAME, @v_DatabaseCollation), '') AS [Collation],
                       ISNULL(mc.masking_function, '') COLLATE DATABASE_DEFAULT AS DataMaskFunction,
                       ISNULL(sc.encryption_type_desc, 'NONE') COLLATE DATABASE_DEFAULT AS EncryptionType,
                       ISNULL((SELECT '[' + cek.[name] + ']'
                                 FROM sys.column_encryption_keys cek WITH (NOLOCK)
                                WHERE cek.column_encryption_key_id = sc.column_encryption_key_id), '') COLLATE DATABASE_DEFAULT AS EncryptionKey,
                       ISNULL(sc.encryption_algorithm_name, '') COLLATE DATABASE_DEFAULT AS EncryptionAlgorithm,
                       '' AS [OldName],
                       JSON_QUERY('{"ExtendedProperties": {' + (SELECT STRING_AGG(CAST('"' + [Name] + '": "' + CONVERT(NVARCHAR(MAX), [Value]) + '"' AS NVARCHAR(MAX)), ',') FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Column', c.COLUMN_NAME) x WHERE x.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)) + '}}') AS [Extensions]
                  FROM INFORMATION_SCHEMA.COLUMNS c WITH (NOLOCK)
                  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = st.[object_id] AND sc.[name] = c.COLUMN_NAME
                  JOIN (SELECT CASE WHEN SCHEMA_NAME(typ.[schema_id]) IN ('sys', 'dbo')
                                    THEN '' ELSE SCHEMA_NAME(typ.[schema_id]) + '.' END + typ.[name] AS USER_TYPE, typ.user_type_id
                          FROM sys.types typ WITH (NOLOCK)) ut ON ut.user_type_id = sc.user_type_id
                  LEFT JOIN sys.computed_columns cc WITH (NOLOCK) ON cc.[object_id] = st.[object_id]
                                                                 AND cc.[name] = c.COLUMN_NAME
                  LEFT JOIN sys.identity_columns ic WITH (NOLOCK) ON ic.[object_id] = st.[object_id]
                                                                 AND ic.[Name] = c.COLUMN_NAME
                  LEFT JOIN sys.masked_columns mc WITH (NOLOCK) ON mc.[object_id] = st.[object_id]
                                                               AND mc.[name] = c.COLUMN_NAME
                  WHERE c.TABLE_SCHEMA = t.TABLE_SCHEMA
                    AND c.TABLE_NAME = t.TABLE_NAME
                    -- Exclude the temporal period columns (GENERATED ALWAYS AS ROW START/END). SchemaSmith
                    -- regenerates ValidFrom/ValidTo from IsTemporal by convention on apply, so emitting them
                    -- as user columns would double-declare them on re-deploy (#369).
                    AND sc.generated_always_type = 0
                    -- Exclude SQL Server graph pseudo-columns. A node or edge table carries
                    -- system-generated columns ($node_id, $edge_id, $from_id, $to_id, graph_id and
                    -- the *_obj_id pair) whose names end in a per-table GUID, so emitting them
                    -- produces a package that cannot be deployed anywhere -- not even back to the
                    -- database it came from. generated_always_type does NOT catch them (they all
                    -- report 0/NOT_APPLICABLE) and neither does is_hidden (the four $-prefixed ones
                    -- are is_hidden = 0). sys.columns.graph_type is the discriminator: non-null for
                    -- exactly these, null for every user column -- including one merely NAMED like
                    -- them. 2017+, which the JSON ingest tier already requires (STRING_AGG).
                    AND sc.graph_type IS NULL) x
          -- Column sequence: 'Name' (default) or 'Physical', the table's own order. The ordinal is looked up
          -- rather than projected: this is SELECT * over the derived table, so adding ORDINAL_POSITION to it
          -- would write the ordinal into the package file. The lookup only runs when Physical is asked for.
          ORDER BY CASE WHEN @p_ObjectOrder = 'Physical'
                        THEN (SELECT c2.ORDINAL_POSITION FROM INFORMATION_SCHEMA.COLUMNS c2
                               WHERE c2.TABLE_SCHEMA = @p_Schema AND c2.TABLE_NAME = @p_Table
                                 AND '[' + c2.COLUMN_NAME + ']' = x.[Name]) END,
                   CASE WHEN @p_ObjectOrder = 'Physical' THEN NULL ELSE x.[Name] END
          FOR JSON AUTO) AS [Columns],
       (SELECT '[' + [Name] + ']' AS [Name],
               -- Same per-partition aggregation as the table-level [CompressionType] above.
               (SELECT CASE COUNT(DISTINCT p.data_compression_desc)
                          WHEN 0 THEN NULL
                          WHEN 1 THEN MIN(p.data_compression_desc)
                          ELSE 'MIXED'
                        END COLLATE DATABASE_DEFAULT
                  FROM sys.partitions AS p WITH (NOLOCK)
                  WHERE p.[object_id] = si.[object_id]
                    AND p.index_id = si.index_id) AS [CompressionType],
               -- Same kindle-time resolution as the table-level [XmlCompression] above.
               (SELECT CASE WHEN MAX(CONVERT(TINYINT, {{XmlCompressionRead}})) = 1 THEN CONVERT(BIT, 1) END
                  FROM sys.partitions AS p WITH (NOLOCK)
                  WHERE p.[object_id] = si.[object_id]
                    AND p.index_id = si.index_id) AS [XmlCompression],
               -- Same emit-only-when-non-default rule as the table-level [FileGroup] above -- a table and
               -- its indexes are commonly split across filegroups on purpose, so this reads si's own
               -- data_space_id independently of the table's.
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
               -- BUCKET_COUNT for a HASH index on a memory-optimized table (#J1). sys.hash_indexes is 2014;
               -- safe statically here because the JSON path is 2016+. NULL for any non-hash index, so an
               -- ordinary index gains no key.
               (SELECT hi.bucket_count FROM sys.hash_indexes hi WITH (NOLOCK)
                 WHERE hi.[object_id] = si.[object_id] AND hi.index_id = si.index_id) AS [BucketCount],
               is_primary_key AS [PrimaryKey],
               is_unique AS [Unique],
               is_unique_constraint AS [UniqueConstraint], 
               CAST(CASE WHEN [type] IN (1, 5) THEN 1 ELSE 0 END AS BIT) AS [Clustered], 
               CAST(CASE WHEN [type] IN (5, 6) THEN 1 ELSE 0 END AS BIT) AS [ColumnStore], 
               CASE WHEN fill_factor = 100 THEN 0 ELSE fill_factor END AS [FillFactor],
               CONVERT(BIT, ignore_dup_key) AS [IgnoreDuplicateKey],
               CONVERT(BIT, is_padded) AS [PadIndex],
               (SELECT STRING_AGG(CAST('[' + COL_NAME(ic.[object_id], ic.column_id) + ']' + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY key_ordinal)
                  FROM sys.index_columns ic WITH (NOLOCK)
                  WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 0) AS [IndexColumns],
               (SELECT STRING_AGG(CAST('[' + COL_NAME(ic.[object_id], ic.column_id) + ']' AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY index_column_id)
                  FROM sys.index_columns ic WITH (NOLOCK)
                  WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1) AS [IncludeColumns],
			   CASE WHEN has_filter = 1 THEN SchemaSmith.fn_StripParenWrapping(filter_definition) ELSE NULL END AS [FilterExpression],
			   JSON_QUERY('{"ExtendedProperties": {' + (SELECT STRING_AGG(CAST('"' + [Name] + '": "' + [Value] + '"' AS NVARCHAR(MAX)), ',')
                        FROM (SELECT ISNULL(i.[Name], c.[Name]) AS [Name], RTRIM(COALESCE(CONVERT(NVARCHAR(MAX), c.[Value]) + ' ', '') + COALESCE(CONVERT(NVARCHAR(MAX), i.[Value]), '')) AS [Value]
                                FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Index', si.[Name]) i
                                FULL OUTER JOIN fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Constraint', si.[Name]) c ON i.[Name] = c.[Name]) x
                        WHERE x.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames))
                   + '}}') AS [Extensions]
          FROM sys.indexes si WITH (NOLOCK)
          WHERE si.[object_id] = st.[object_id]
            AND NOT EXISTS (SELECT * FROM sys.xml_indexes xi WITH (NOLOCK) WHERE xi.[object_id] = si.[object_id] AND xi.index_id = si.index_id)
            AND is_hypothetical = 0
            AND is_disabled = 0
            AND index_id > 0
            -- A graph table also gets a system-generated GRAPH_UNIQUE_INDEX_<guid> over its
            -- graph_id column. Both names carry a per-table GUID, so emitting the index is the
            -- same undeployable-package problem as emitting the column, and excluding the columns
            -- alone leaves an index pointing at one that is no longer declared.
            AND NOT EXISTS (SELECT 1 FROM sys.index_columns gic WITH (NOLOCK)
                            JOIN sys.columns gc WITH (NOLOCK)
                              ON gc.[object_id] = gic.[object_id] AND gc.column_id = gic.column_id
                           WHERE gic.[object_id] = si.[object_id] AND gic.index_id = si.index_id
                             AND gc.graph_type IS NOT NULL)
          ORDER BY [Name]
          FOR JSON AUTO) AS [Indexes],
       (SELECT '[' + i.[name] COLLATE DATABASE_DEFAULT + ']' AS [Name],
               '[' + COL_NAME(i.[Object_id], ic.column_id) + ']' AS [Column],
               CONVERT(BIT, CASE WHEN i.xml_index_type = 0 THEN 1 ELSE 0 END) AS [IsPrimary],
               (SELECT '[' + [Name] COLLATE DATABASE_DEFAULT + ']' FROM sys.xml_indexes i2 WHERE i2.[object_id] = i.[object_id] AND i2.index_id = i.using_xml_index_id AND i.xml_index_type = 1) AS [PrimaryIndex],
               i.secondary_type_desc COLLATE DATABASE_DEFAULT AS [SecondaryIndexType],
			   JSON_QUERY('{"ExtendedProperties": {' + (SELECT STRING_AGG(CAST('"' + x.[Name] + '": "' + CONVERT(NVARCHAR(MAX), [Value]) + '"' AS NVARCHAR(MAX)), ',') FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Index', i.[Name]) x WHERE x.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)) + '}}') AS [Extensions]
          FROM sys.xml_indexes i WITH (NOLOCK)
          JOIN sys.index_columns ic WITH (NOLOCK) ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id
          WHERE i.[object_id] = st.[object_id]
          ORDER BY i.[Name]
          FOR JSON AUTO) AS [XmlIndexes],
	   (SELECT '[' + [Name] + ']' AS [Name],
               (SELECT STRING_AGG(CAST('[' + COL_NAME(fc.[parent_object_id], fc.parent_column_id) + ']' AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY fc.constraint_column_id)
                            FROM sys.foreign_key_columns fc WITH (NOLOCK)
                            WHERE fk.[object_id] = fc.[constraint_object_id]) AS [Columns],
               '[' + OBJECT_SCHEMA_NAME(referenced_object_id) + ']' AS RelatedTableSchema,
               '[' + OBJECT_NAME(referenced_object_id) + ']' AS RelatedTable,
               (SELECT STRING_AGG(CAST('[' + COL_NAME(fc.[referenced_object_id], fc.referenced_column_id) + ']' AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY fc.constraint_column_id)
                            FROM sys.foreign_key_columns fc WITH (NOLOCK)
                            WHERE fk.[object_id] = fc.[constraint_object_id]) AS [RelatedColumns],
               REPLACE(fk.delete_referential_action_desc, '_', ' ') COLLATE DATABASE_DEFAULT AS [DeleteAction],
               REPLACE(fk.update_referential_action_desc, '_', ' ') COLLATE DATABASE_DEFAULT AS [UpdateAction],
               JSON_QUERY('{"ExtendedProperties": {' + (SELECT STRING_AGG(CAST('"' + [Name] + '": "' + CONVERT(NVARCHAR(MAX), [Value]) + '"' AS NVARCHAR(MAX)), ',') FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Constraint', fk.[Name]) x WHERE x.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)) + '}}') AS [Extensions]
          FROM sys.foreign_keys fk WITH (NOLOCK)
          WHERE fk.parent_object_id = st.[object_id]
          ORDER BY [Name]
          FOR JSON AUTO) AS [ForeignKeys],
       (SELECT '[' + [Name] + ']' AS [Name], 
               (SELECT STRING_AGG(CAST('[' + COL_NAME(sc.[object_id], sc.column_id) + ']' AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY sc.stats_column_id)
                  FROM sys.stats_columns sc WITH (NOLOCK)
                  WHERE s.[object_id] = sc.[object_id] AND s.stats_id = sc.stats_id) AS [Columns],
               SchemaSmith.fn_StripParenWrapping([filter_definition]) AS FilterExpression,
			   JSON_QUERY('{"ExtendedProperties": {' + (SELECT STRING_AGG(CAST('"' + [Name] + '": "' + CONVERT(NVARCHAR(MAX), [Value]) + '"' AS NVARCHAR(MAX)), ',') FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Statistic', s.[Name]) x WHERE x.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)) + '}}') AS [Extensions]
          FROM sys.stats s WITH (NOLOCK)
          WHERE [object_id] = st.[object_id]
            AND auto_created = 0
            AND user_created = 1
            AND is_temporary = 0
            AND [Name] NOT LIKE 'stat[_]%'
            AND [Name] NOT LIKE 'hind[_]%'
          ORDER BY [Name]
          FOR JSON AUTO) AS [Statistics],
       (SELECT '[' + [Name] + ']' AS [Name],
               SchemaSmith.fn_StripParenWrapping([definition]) AS [Expression],
               JSON_QUERY('{"ExtendedProperties": {' + (SELECT STRING_AGG(CAST('"' + [Name] + '": "' + CONVERT(NVARCHAR(MAX), [Value]) + '"' AS NVARCHAR(MAX)), ',') FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, 'Constraint', cc.[Name]) x WHERE x.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)) + '}}') AS [Extensions]
          FROM sys.check_constraints cc WITH (NOLOCK)
          WHERE parent_object_id = st.[object_id]
            AND parent_column_id = 0
          ORDER BY [Name]
          FOR JSON AUTO) AS [CheckConstraints],
       (SELECT FullTextCatalog = '[' + (SELECT c.[name] FROM sys.fulltext_catalogs c WITH (NOLOCK) WHERE c.fulltext_catalog_id = fi.fulltext_catalog_id) + ']',
               KeyIndex = '[' + (SELECT i.[Name] FROM sys.indexes i WITH (NOLOCK) WHERE i.[object_id] = fi.[object_id] AND i.[index_id] = fi.[unique_index_id]) + ']',
               ChangeTracking = change_tracking_state_desc,
               [StopList] = '[' + (SELECT fs.[name] FROM sys.fulltext_stoplists fs WITH (NOLOCK) WHERE fs.stoplist_id = fi.stoplist_id) + ']',
               (SELECT STRING_AGG(CAST('[' + COL_NAME(fc.[object_id], fc.column_id) + ']' +
                                       CASE WHEN fc.type_column_id IS NOT NULL
                                            THEN ' TYPE COLUMN [' + COL_NAME(fc.[object_id], fc.type_column_id) + ']'
                                            ELSE '' END +
                                       -- Full-text LANGUAGE churn: emit only when it deviates from the column's
                                       -- own collation-implied default -- stamping every column would churn every
                                       -- existing full-text index once. Must render byte-identical to the
                                       -- live-side build in IndexOnlyQuench.sql/ModifiedTableQuench.sql; drift
                                       -- detection compares these as strings. c.collation_name comes from a JOIN,
                                       -- not a correlated subquery -- STRING_AGG rejects an aggregate expression
                                       -- containing a subquery. A NULL collation (a non-character column, e.g. a
                                       -- VARBINARY document column indexed via TYPE COLUMN) has no collation-implied
                                       -- default to compare against, so it is always treated as non-default and
                                       -- LANGUAGE is always emitted for it -- the alternative (never emitting) would
                                       -- make such a column's language permanently unrepresentable.
                                       CASE WHEN c.collation_name IS NULL OR fc.language_id <> COLLATIONPROPERTY(c.collation_name, 'LCID')
                                            THEN ' LANGUAGE ' + CAST(fc.language_id AS NVARCHAR(10))
                                            ELSE '' END +
                                       -- STATISTICAL_SEMANTICS is the last of the per-column trio and
                                       -- follows LANGUAGE, matching SQL Server's own DDL order.
                                       CASE WHEN fc.statistical_semantics = 1
                                            THEN ' STATISTICAL_SEMANTICS' ELSE '' END
                                             AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY COL_NAME(fc.[object_id], fc.column_id))
                  FROM sys.fulltext_index_columns fc WITH (NOLOCK)
                  JOIN sys.columns c WITH (NOLOCK) ON c.[object_id] = fc.[object_id] AND c.column_id = fc.column_id
                  WHERE fi.[object_id] = fc.[object_id]) AS [Columns]
          FROM sys.fulltext_indexes fi WITH (NOLOCK)
          WHERE fi.[object_id] = st.[object_id]
          FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS [FullTextIndex],
	   JSON_QUERY('{"ExtendedProperties": {' + (SELECT STRING_AGG(CAST('"' + [Name] + '": "' + CONVERT(NVARCHAR(MAX), [Value]) + '"' AS NVARCHAR(MAX)), ',') FROM fn_listextendedproperty(default, 'Schema', @p_Schema, 'Table', @p_Table, default, default) x WHERE x.[Name] COLLATE DATABASE_DEFAULT NOT IN (SELECT [Name] FROM @InternalEPNames)) + '}}') AS [Extensions]
  FROM INFORMATION_SCHEMA.TABLES t WITH (NOLOCK)
  JOIN sys.tables st WITH (NOLOCK) ON st.[object_id] = OBJECT_ID(@p_Schema + '.' + @p_Table)
  LEFT JOIN sys.change_tracking_tables ctt WITH (NOLOCK) ON ctt.[object_id] = st.[object_id]
  LEFT JOIN sys.tables h WITH (NOLOCK) ON h.[object_id] = st.history_table_id
  LEFT JOIN sys.schemas hs WITH (NOLOCK) ON hs.[schema_id] = h.[schema_id]
  WHERE TABLE_NAME = @p_Table
    AND TABLE_SCHEMA = @p_Schema
  FOR JSON AUTO, WITHOUT_ARRAY_WRAPPER
), '\"', '"'), '"}"', '" }'), '"{"', '{ "'), 1)
ORDER BY [LineNo]