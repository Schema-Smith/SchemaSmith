-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.TableQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.TableQuench
GO
CREATE PROCEDURE SchemaSmith.TableQuench
    @ProductName NVARCHAR(50),
    @TableDefinitions NVARCHAR(MAX),
    @WhatIf BIT = 0,
    @DropUnknownIndexes BIT = 0,
    @DropTablesRemovedFromProduct BIT = 1,
    @UpdateFillFactor BIT = 1,
    -- The resolved upper-tier RebuildPolicy, forwarded to ModifiedTableQuench (which owns the decision).
    -- Defaults are the domain object's NEVER default, so an existing caller that passes nothing behaves
    -- exactly as before and can never elect a rebuild.
    @RebuildPolicyMode NVARCHAR(20) = 'NEVER',
    @RebuildPolicyThreshold INT = NULL,
    @RebuildPolicyOnOrderMismatch BIT = 0,
    -- Opt-in (#323). OFF means a column change blocked by a schema-bound module is refused by name,
    -- which is the safe default: dropping a scripted object the package may not declare would destroy
    -- something SchemaSmith cannot put back. ON means the package accepts responsibility for
    -- recreating them -- which the after-tables object pass does, provided they live in a folder on
    -- the AfterTablesObjects slot. SchemaTongs places them there automatically on extraction.
    @DropSchemaBoundDependents BIT = 0,
    -- Template-level CdcFilegroup (#417): the default for tables that declare none. NULL = unmanaged.
    @CdcFilegroup NVARCHAR(128) = NULL,
    -- Template-level CdcSupportsNetChanges (#426): the default for CDC tables that declare none. NULL = unset.
    @CdcSupportsNetChanges BIT = NULL,
    -- OnlineApply (OFF, PREFER or REQUIRE) and the resumable build's minutes, forwarded to the table and index passes.
    @OnlineApply VARCHAR(7) = 'OFF',
    @OnlineApplyMaxDuration INT = NULL
AS
BEGIN TRY
    SET NOCOUNT ON
{{ParseJson}}

  -- Sanitize the parsed working set for the detected target version BEFORE any emit/detection pass: below a
  -- feature's intro version the declared feature is refused ('fail') or neutralized in place ('warn'), so the
  -- quench procs stay gate-free. See SchemaSmith.DegradeUnsupportedFeatures.
  -- Before anything is created or renamed, so a refusal leaves another product's table as it was.
  EXEC SchemaSmith.ValidateTableOwnership @ProductName = @ProductName, @WhatIf = @WhatIf
  EXEC SchemaSmith.DegradeUnsupportedFeatures
  -- Template CDC defaults, and the capture-instance limit refused before any column is added (#427).
  EXEC SchemaSmith.CdcPreflight @CdcFilegroup = @CdcFilegroup, @CdcSupportsNetChanges = @CdcSupportsNetChanges

  -- #508: undo for what ExpressionMapAdopt writes under WhatIf; ExpressionMapRecord puts the map back from it.
  IF OBJECT_ID('tempdb..#ExpressionMapAdoptUndo') IS NOT NULL DROP TABLE #ExpressionMapAdoptUndo
  CREATE TABLE #ExpressionMapAdoptUndo (ObjectSchema NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL, ObjectTable NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL,
    ObjectKind VARCHAR(32) COLLATE DATABASE_DEFAULT NOT NULL, ObjectName NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL, Slot VARCHAR(32) COLLATE DATABASE_DEFAULT NOT NULL,
    WasPresent BIT NOT NULL, AuthoredText NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL, CanonicalText NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
    PlatformName VARCHAR(20) COLLATE DATABASE_DEFAULT NULL, EngineVersion VARCHAR(200) COLLATE DATABASE_DEFAULT NULL, CompatLevel INT NULL, UpdatedUtc DATETIME2(3) NULL)
  -- #508: before any table or column is created, so only what already exists is checked on a scratch copy.
  EXEC SchemaSmith.ExpressionMapAdopt @WhatIf
  EXEC SchemaSmith.MissingTableAndColumnQuench @WhatIf
  -- Filled by ModifiedTableQuench's CDC headroom check, consumed by CdcQuench once every column exists.
  {{CdcRotateTable}}
  EXEC SchemaSmith.ModifiedTableQuench @ProductName = @ProductName, @WhatIf = @WhatIf, @DropUnknownIndexes = @DropUnknownIndexes, @DropTablesRemovedFromProduct = @DropTablesRemovedFromProduct, @DropSchemaBoundDependents = @DropSchemaBoundDependents,
                                       @RebuildPolicyMode = @RebuildPolicyMode, @RebuildPolicyThreshold = @RebuildPolicyThreshold, @RebuildPolicyOnOrderMismatch = @RebuildPolicyOnOrderMismatch,
                                       @OnlineApply = @OnlineApply, @OnlineApplyMaxDuration = @OnlineApplyMaxDuration
  EXEC SchemaSmith.MissingIndexesAndConstraintsQuench @ProductName, @WhatIf, @OnlineApply = @OnlineApply, @OnlineApplyMaxDuration = @OnlineApplyMaxDuration
  -- Once every index exists: a declared Disabled state is state on an index that must already be there. Before the
  -- foreign keys and constraint states, because disabling a key's index disables the keys that reference it, and
  -- ConstraintStateQuench is what puts a declared Enforced back.
  IF EXISTS (SELECT 1 FROM #Indexes WITH (NOLOCK) WHERE [Disabled] = 1)
       OR EXISTS (SELECT 1 FROM #Indexes i WITH (NOLOCK) JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) WHERE si.is_disabled = 1)
    EXEC SchemaSmith.IndexStateQuench @WhatIf
  ELSE RAISERROR('Skipping index disabled state: no index is declared disabled and none is disabled', 10, 100) WITH NOWAIT
  -- Also after the indexes/constraints pass, and for a closely related reason: SQL Server refuses a
  -- FILESTREAM column unless the table already has a ROWGUIDCOL column covered by a unique CONSTRAINT.
  EXEC SchemaSmith.FileStreamColumnQuench @WhatIf
  -- After both column-adding passes above: a capture instance records only the columns that exist when it is created.
  EXEC SchemaSmith.CdcQuench @WhatIf
  -- After the indexes/constraints pass on purpose: enabling change tracking requires a primary key,
  -- which for a table created in this same run does not exist until the line above has run.
  EXEC SchemaSmith.ChangeTrackingQuench @WhatIf
  EXEC SchemaSmith.ForeignKeyQuench @ProductName, @WhatIf, @OnlineApply = @OnlineApply
  -- Edge constraints need their node tables, which exist by now; the procedure is floor-safe, the guard only saves the call.
  IF SchemaSmith.fn_ServerMajorVersion() >= 15
    IF EXISTS (SELECT 1 FROM #EdgeConstraints WITH (NOLOCK))
           OR EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK) JOIN sys.objects o ON o.parent_object_id = OBJECT_ID(t.[Schema] + '.' + t.[Name]) WHERE o.[type] = 'EC')
      EXEC SchemaSmith.EdgeConstraintQuench @WhatIf
    ELSE RAISERROR('Skipping edge constraints: none declared and none on a declared table', 10, 100) WITH NOWAIT
  -- After the keys exist: enforcement and validation are state on a constraint that must already be there.
  IF EXISTS (SELECT 1 FROM #ForeignKeys WITH (NOLOCK) WHERE [Enforced] IS NOT NULL OR [Validated] IS NOT NULL)
       OR EXISTS (SELECT 1 FROM #CheckConstraints WITH (NOLOCK) WHERE [Enforced] IS NOT NULL OR [Validated] IS NOT NULL)
    EXEC SchemaSmith.ConstraintStateQuench @WhatIf
  ELSE RAISERROR('Skipping constraint enforcement and validation: no foreign key or check constraint declares it', 10, 100) WITH NOWAIT
  -- After every pass that creates an object: an extended property, a description included, belongs to something
  -- that must already exist, and a foreign key's only exists from the line above.
  IF EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE [Description] IS NOT NULL)
       OR EXISTS (SELECT 1 FROM #Columns WITH (NOLOCK) WHERE [Description] IS NOT NULL)
       OR EXISTS (SELECT 1 FROM #Indexes WITH (NOLOCK) WHERE [Description] IS NOT NULL)
       OR EXISTS (SELECT 1 FROM #ExtendedProperties WITH (NOLOCK))
    EXEC SchemaSmith.ExtendedPropertyQuench @WhatIf
  ELSE RAISERROR('Skipping extended properties: none declared', 10, 100) WITH NOWAIT
  -- #242: last, so anything the passes above created is recorded on this run rather than churning once more.
  EXEC SchemaSmith.ExpressionMapRecord @WhatIf
  SET NOCOUNT OFF
END TRY
BEGIN CATCH
    DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH