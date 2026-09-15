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
    @CdcFilegroup NVARCHAR(128) = NULL
AS
BEGIN TRY
    SET NOCOUNT ON
{{ParseJson}}

  -- Sanitize the parsed working set for the detected target version BEFORE any emit/detection pass: below a
  -- feature's intro version the declared feature is refused ('fail') or neutralized in place ('warn'), so the
  -- quench procs stay gate-free. See SchemaSmith.DegradeUnsupportedFeatures.
  EXEC SchemaSmith.DegradeUnsupportedFeatures

  EXEC SchemaSmith.MissingTableAndColumnQuench @WhatIf
  EXEC SchemaSmith.ModifiedTableQuench @ProductName = @ProductName, @WhatIf = @WhatIf, @DropUnknownIndexes = @DropUnknownIndexes, @DropTablesRemovedFromProduct = @DropTablesRemovedFromProduct, @DropSchemaBoundDependents = @DropSchemaBoundDependents,
                                       @RebuildPolicyMode = @RebuildPolicyMode, @RebuildPolicyThreshold = @RebuildPolicyThreshold, @RebuildPolicyOnOrderMismatch = @RebuildPolicyOnOrderMismatch,
                                       @CdcFilegroup = @CdcFilegroup
  EXEC SchemaSmith.MissingIndexesAndConstraintsQuench @ProductName, @WhatIf
  -- Also after the indexes/constraints pass, and for a closely related reason: SQL Server refuses a
  -- FILESTREAM column unless the table already has a ROWGUIDCOL column covered by a unique CONSTRAINT.
  EXEC SchemaSmith.FileStreamColumnQuench @WhatIf
  -- After the indexes/constraints pass on purpose: enabling change tracking requires a primary key,
  -- which for a table created in this same run does not exist until the line above has run.
  EXEC SchemaSmith.ChangeTrackingQuench @WhatIf
  EXEC SchemaSmith.ForeignKeyQuench @ProductName, @WhatIf
  SET NOCOUNT OFF
END TRY
BEGIN CATCH
    DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
    RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH