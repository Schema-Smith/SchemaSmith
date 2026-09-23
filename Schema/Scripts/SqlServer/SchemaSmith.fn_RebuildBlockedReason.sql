-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Answers "why can this table NOT be rebuilt?" -- a short reason naming the blocking state, or NULL when a
-- rebuild is safe. A rebuild is a shadow-copy-and-swap, and each state below lives OUTSIDE the definition a
-- schema package carries, so the copy silently destroys it and the package cannot put it back: a temporal
-- table's history rows, a CDC capture instance and its change table, a replication article's identity, a
-- Change Tracking baseline. Fail closed and leave those tables to Before/After migration scripts.
--
-- A reason string rather than a bit so the caller can name the state instead of saying "cannot"; a function
-- rather than inline logic so each state is verifiable on its own.
--
-- Version gate. sys.tables.temporal_type_desc is SQL Server 2016 (13.x), and a function body is COMPILED at
-- CREATE -- so a static reference is an "invalid column" error at KINDLE time on an older server, failing the
-- whole helper deployment rather than one path at runtime. The temporal predicate is therefore assembled into
-- the CREATE text only when the target is 2016+; below that no table can be system-versioned, so omitting the
-- predicate is complete rather than a compromise. Both forms are built from one body string, so the signature
-- and the return contract cannot drift apart.
--
-- The gate reads SchemaSmith.fn_ServerMajorVersion() (kindled earlier in the list) rather than the raw
-- {{ServerMajorVersion}} token. The token bakes to 0 whenever the caller detected no version -- several
-- SchemaTongs kindle paths pass nothing -- and a literal "IF 0 >= 13" would build the degraded body on a
-- modern server. fn_ServerMajorVersion already resolves the baked token, the CONTEXT_INFO test override and
-- the SERVERPROPERTY fallback, in that order, and returns NULL (-> ELSE) on a genuine pre-2016 binary.
--
-- Referenced statically because both predate the SQL Server 2008 floor: sys.tables.is_replicated (present
-- since sys.tables itself, 2005) and sys.tables.is_tracked_by_cdc (present since CDC shipped in 2008) --
-- neither carries an "Applies to" version qualifier in the catalog reference, unlike temporal_type_desc.
-- SchemaSmith.GenerateTableXml.sql, the compat-100 path exercised against genuine old binaries, already reads
-- is_tracked_by_cdc statically, so that half of the claim is codebase-certified rather than only documented.
-- sys.change_tracking_tables shipped with Change Tracking in 2008 for the same reason.
IF OBJECT_ID('SchemaSmith.fn_RebuildBlockedReason') IS NOT NULL DROP FUNCTION SchemaSmith.fn_RebuildBlockedReason
GO
DECLARE @v_TemporalCheck NVARCHAR(MAX) = ''
IF SchemaSmith.fn_ServerMajorVersion() >= 13
  SET @v_TemporalCheck = '
  IF EXISTS (SELECT 1 FROM sys.tables
             WHERE [object_id] = @v_ObjectId AND temporal_type_desc <> ''NON_TEMPORAL_TABLE'')
    RETURN ''system versioning is enabled (temporal table)''
'

DECLARE @v_Create NVARCHAR(MAX) = '
CREATE FUNCTION SchemaSmith.fn_RebuildBlockedReason(@p_Schema NVARCHAR(128), @p_Table NVARCHAR(128))
  RETURNS NVARCHAR(4000)
AS
BEGIN
  DECLARE @v_ObjectId INT = OBJECT_ID(QUOTENAME(@p_Schema) + ''.'' + QUOTENAME(@p_Table))
  -- Unknown table: there is nothing to rebuild, so nothing blocks one. The caller decides what a missing
  -- table means; this function does not invent a blocking reason for it.
  IF @v_ObjectId IS NULL RETURN NULL
' + @v_TemporalCheck + '
  IF EXISTS (SELECT 1 FROM sys.tables WHERE [object_id] = @v_ObjectId AND is_tracked_by_cdc = 1)
    RETURN ''Change Data Capture is enabled''

  IF EXISTS (SELECT 1 FROM sys.tables WHERE [object_id] = @v_ObjectId AND is_replicated = 1)
    RETURN ''the table is published for replication''

  IF EXISTS (SELECT 1 FROM sys.change_tracking_tables WHERE [object_id] = @v_ObjectId)
    RETURN ''Change Tracking is enabled''

  -- Partitioning, checked across EVERY index rather than only the table''s own data space. A rebuild is a
  -- shadow copy, and the shadow CREATE TABLE carries no placement clause at all -- so the copy lands on the
  -- default filegroup and the layout is gone while every row survives, which is the silent kind of loss this
  -- function exists to prevent. A nonclustered index aligned to a scheme on an otherwise ordinary heap is
  -- destroyed the same way (the old table is dropped whole and the index passes re-add from the package), so
  -- reading only index_id 0/1 would miss it.
  --
  -- sys.data_spaces.type = ''PS'' is the partition-scheme discriminator; both the view and partitioning
  -- itself predate the SQL Server 2008 floor, so this is referenced statically like the CDC and replication
  -- predicates above rather than staged behind a version gate.
  IF EXISTS (SELECT 1 FROM sys.indexes i
               JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
              WHERE i.[object_id] = @v_ObjectId AND ds.[type] = ''PS'')
    RETURN ''the table or one of its indexes is partitioned''

  RETURN NULL
END'

EXEC(@v_Create)
