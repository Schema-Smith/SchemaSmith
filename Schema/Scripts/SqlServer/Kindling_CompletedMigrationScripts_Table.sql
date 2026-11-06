-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Table definition lives in the sibling Kindling_CompletedMigrationScripts.json resource.
-- ForgeKindler substitutes the JSON body into this script's TableDef token before execution.
-- BootstrapTableQuench has zero SchemaSmith_* dependencies, so this script runs early
-- in the kindling pipeline (right after the BootstrapTableQuench proc itself is created).

EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = '{{TableDef}}'

-- A table created while ScriptPath and ProductName were VARCHAR stored a character outside the database code page as
-- '?' or a look-alike, so the script's record was never found again: it ran on every deploy and then failed on the
-- primary key. Bootstrap does not change a column's type and both columns are keys, so the keys come off, the columns
-- widen, and the keys go back as declared. After Bootstrap, which adds the scope columns a legacy table lacks.
IF EXISTS (SELECT 1 FROM sys.columns c
           WHERE c.object_id = OBJECT_ID(N'SchemaSmith.CompletedMigrationScripts')
             AND c.name IN (N'ScriptPath', N'ProductName') AND TYPE_NAME(c.system_type_id) = N'varchar')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'SchemaSmith.CompletedMigrationScripts') AND name = N'IX_CompletedMigrationScripts_Slot_Scope')
        DROP INDEX [IX_CompletedMigrationScripts_Slot_Scope] ON SchemaSmith.CompletedMigrationScripts;
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'SchemaSmith.CompletedMigrationScripts') AND name = N'PK_CompletedMigrationScripts')
        ALTER TABLE SchemaSmith.CompletedMigrationScripts DROP CONSTRAINT [PK_CompletedMigrationScripts];
    ALTER TABLE SchemaSmith.CompletedMigrationScripts ALTER COLUMN [ScriptPath] NVARCHAR(800) NOT NULL;
    ALTER TABLE SchemaSmith.CompletedMigrationScripts ALTER COLUMN [ProductName] NVARCHAR(100) NOT NULL;
    ALTER TABLE SchemaSmith.CompletedMigrationScripts ADD CONSTRAINT [PK_CompletedMigrationScripts]
        PRIMARY KEY CLUSTERED ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name]);
    CREATE NONCLUSTERED INDEX [IX_CompletedMigrationScripts_Slot_Scope]
        ON SchemaSmith.CompletedMigrationScripts ([ProductName], [QuenchSlot], [template_name], [schema_name]);
END
