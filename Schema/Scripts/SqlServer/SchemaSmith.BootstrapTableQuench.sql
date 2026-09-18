-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Lightweight bootstrap procedure with ZERO SchemaSmith_* table or proc dependencies.
-- Parses a TableQuench-shaped JSON definition and applies, in order:
--   1. TABLE rename when OldName is set (old table present, new absent) -- BEFORE CREATE TABLE so a
--      renamed table's history is not orphaned under an empty freshly-created new table
--   2. CREATE TABLE IF NOT EXISTS (built from Columns + any PrimaryKey index)
--   3. COLUMN rename when a column's OldName is set (old column present, new absent) -- BEFORE
--      ADD COLUMN so a renamed column's data is not left behind under an empty new column
--   4. ALTER TABLE ... ADD COLUMN per missing column (sys.columns guarded)
--   5. CREATE NONCLUSTERED INDEX per missing index (sys.indexes guarded)
-- Both a rename's old AND new name already present (table or column) is a hard failure, not a
-- silent skip -- it means an object exists that the model does not expect. Renames use sp_rename
-- (a system proc, not a SchemaSmith_* object).
-- Out of scope: column type changes (beyond a same-shape rename), drops, FKs, check constraints,
-- ownership tracking.
-- Idempotent: a second call on the same definition is a no-op.

IF OBJECT_ID('SchemaSmith.BootstrapTableQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.BootstrapTableQuench
GO
CREATE PROCEDURE SchemaSmith.BootstrapTableQuench
    @TableDefinitions NVARCHAR(MAX)
AS
BEGIN TRY
    SET NOCOUNT ON;

    DECLARE @v_Schema NVARCHAR(500),
            @v_Name NVARCHAR(500),
            @v_OldName NVARCHAR(500),
            @v_SchemaBare NVARCHAR(500),
            @v_NameBare NVARCHAR(500),
            @v_OldNameBare NVARCHAR(500),
            @v_SQL NVARCHAR(MAX);

    SELECT @v_Schema = [Schema], @v_Name = [Name], @v_OldName = [OldName]
      FROM OPENJSON(@TableDefinitions) WITH (
          [Schema] NVARCHAR(500) '$.Schema',
          [Name] NVARCHAR(500) '$.Name',
          [OldName] NVARCHAR(500) '$.OldName'
      );

    -- Bracket-strip inline: keep dependencies off any SchemaSmith function.
    SET @v_SchemaBare = REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(@v_Schema, ''))), '[', ''), ']', '');
    SET @v_NameBare = REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(@v_Name, ''))), '[', ''), ']', '');
    -- #375's blank/whitespace-OldName trap (SchemaSmith_ParseTableJson.sql) applies here too: a blank
    -- OldName must normalize to "no rename" (empty string, checked via <> '' below), not a bogus name.
    SET @v_OldNameBare = REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(@v_OldName, ''))), '[', ''), ']', '');

    IF @v_SchemaBare = '' OR @v_NameBare = ''
        THROW 51000, 'BootstrapTableQuench: JSON must contain non-blank Schema and Name.', 1;

    DECLARE @v_QualifiedName NVARCHAR(1000) = '[' + @v_SchemaBare + '].[' + @v_NameBare + ']';
    DECLARE @v_FullKey NVARCHAR(1000) = @v_SchemaBare + '.' + @v_NameBare;

    -- Step 1: TABLE-level declarative rename (OldName), run BEFORE CREATE TABLE IF NOT EXISTS below --
    -- a freshly-created empty table under the new name would otherwise orphan the old table and every
    -- row of its history.
    IF @v_OldNameBare <> ''
    BEGIN
        DECLARE @v_OldQualifiedName NVARCHAR(1000) = '[' + @v_SchemaBare + '].[' + @v_OldNameBare + ']';
        IF OBJECT_ID(@v_OldQualifiedName, 'U') IS NOT NULL AND OBJECT_ID(@v_QualifiedName, 'U') IS NOT NULL
        BEGIN
            -- Name the objects. Refusing is right, but an operator told only that "a" table clashes has
            -- to go find which; PostgreSQL has always interpolated them and THROW takes a variable
            -- message (2048 chars), so there is no reason for the gap. Keep the 'already exist'
            -- substring -- the integration tests assert on it.
            DECLARE @v_TblClashMsg NVARCHAR(2048) =
              'BootstrapTableQuench: both ' + @v_OldQualifiedName + ' (OldName) and ' + @v_QualifiedName +
              ' already exist; resolve manually before bootstrap can rename.';
            THROW 51000, @v_TblClashMsg, 1;
        END

        IF OBJECT_ID(@v_OldQualifiedName, 'U') IS NOT NULL AND OBJECT_ID(@v_QualifiedName, 'U') IS NULL
        BEGIN
            -- EXEC does not accept an expression as an argument -- the concatenation must be
            -- assigned to a variable first (a single, direct, one-shot rename here, unlike the
            -- STUFF/FOR XML PATH precedents that batch N renames into one dynamic-SQL string).
            DECLARE @v_TableRenameFrom NVARCHAR(1000) = @v_SchemaBare + '.' + @v_OldNameBare;
            EXEC sp_rename @v_TableRenameFrom, @v_NameBare;
        END
    END

    -- Parse columns into a table variable.
    DECLARE @v_Columns TABLE (
        OrdinalPos INT IDENTITY(1, 1),
        ColumnName NVARCHAR(500),
        ColumnNameBare NVARCHAR(500),
        DataType NVARCHAR(200),
        Nullable BIT,
        [Default] NVARCHAR(MAX),
        OldNameBare NVARCHAR(500)
    );

    INSERT INTO @v_Columns (ColumnName, ColumnNameBare, DataType, Nullable, [Default], OldNameBare)
    SELECT [Name],
           REPLACE(REPLACE([Name], '[', ''), ']', ''),
           [DataType],
           ISNULL([Nullable], 0),
           [Default],
           REPLACE(REPLACE(LTRIM(RTRIM(ISNULL([OldName], ''))), '[', ''), ']', '')
      FROM OPENJSON(@TableDefinitions, '$.Columns') WITH (
          [Name] NVARCHAR(500) '$.Name',
          [DataType] NVARCHAR(200) '$.DataType',
          [Nullable] BIT '$.Nullable',
          [Default] NVARCHAR(MAX) '$.Default',
          [OldName] NVARCHAR(500) '$.OldName'
      );

    -- Parse indexes into a table variable.
    DECLARE @v_Indexes TABLE (
        OrdinalPos INT IDENTITY(1, 1),
        IndexName NVARCHAR(500),
        IndexNameBare NVARCHAR(500),
        PrimaryKey BIT,
        [Unique] BIT,
        [Clustered] BIT,
        IndexColumns NVARCHAR(MAX)
    );

    INSERT INTO @v_Indexes (IndexName, IndexNameBare, PrimaryKey, [Unique], [Clustered], IndexColumns)
    SELECT [Name],
           REPLACE(REPLACE([Name], '[', ''), ']', ''),
           ISNULL([PrimaryKey], 0),
           ISNULL([Unique], 0),
           ISNULL([Clustered], 0),
           [IndexColumns]
      FROM OPENJSON(@TableDefinitions, '$.Indexes') WITH (
          [Name] NVARCHAR(500) '$.Name',
          [PrimaryKey] BIT '$.PrimaryKey',
          [Unique] BIT '$.Unique',
          [Clustered] BIT '$.Clustered',
          [IndexColumns] NVARCHAR(MAX) '$.IndexColumns'
      );

    -- Step 2: CREATE TABLE if it does not exist (with inline PK constraint if defined).
    IF OBJECT_ID(@v_QualifiedName, 'U') IS NULL
    BEGIN
        DECLARE @v_ColumnList NVARCHAR(MAX) =
            (SELECT STRING_AGG(CAST(
                ColumnName + ' ' + DataType +
                CASE WHEN Nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END +
                CASE WHEN RTRIM(ISNULL([Default], '')) <> '' THEN ' DEFAULT ' + [Default] ELSE '' END
              AS NVARCHAR(MAX)), ', ') WITHIN GROUP (ORDER BY OrdinalPos)
             FROM @v_Columns);

        DECLARE @v_PkClause NVARCHAR(MAX) =
            (SELECT TOP 1 ', CONSTRAINT ' + IndexName + ' PRIMARY KEY ' +
                          CASE WHEN [Clustered] = 1 THEN 'CLUSTERED' ELSE 'NONCLUSTERED' END +
                          ' (' + IndexColumns + ')'
               FROM @v_Indexes
              WHERE PrimaryKey = 1
              ORDER BY OrdinalPos);

        SET @v_SQL = 'CREATE TABLE ' + @v_QualifiedName + ' (' + @v_ColumnList + ISNULL(@v_PkClause, '') + ')';
        EXEC(@v_SQL);
    END
    ELSE
    BEGIN
        -- Step 3: COLUMN-level declarative rename (OldName), run BEFORE the ADD COLUMN below so a
        -- renamed column's data is not left behind under an empty freshly-added new column. Works
        -- across the whole SQL Server support range -- no version gate needed.
        DECLARE @v_ColRenameIdx INT = 1;
        DECLARE @v_ColRenameCount INT = (SELECT COUNT(*) FROM @v_Columns WHERE OldNameBare <> '');
        DECLARE @v_ColRenameOld NVARCHAR(500), @v_ColRenameNew NVARCHAR(500), @v_ColRenameFrom NVARCHAR(1000);
        WHILE @v_ColRenameIdx <= @v_ColRenameCount
        BEGIN
            SELECT @v_ColRenameOld = OldNameBare, @v_ColRenameNew = ColumnNameBare
              FROM (SELECT OldNameBare, ColumnNameBare, ROW_NUMBER() OVER (ORDER BY OrdinalPos) AS Rn
                      FROM @v_Columns WHERE OldNameBare <> '') x
             WHERE Rn = @v_ColRenameIdx;

            IF COLUMNPROPERTY(OBJECT_ID(@v_QualifiedName), @v_ColRenameOld, 'AllowsNull') IS NOT NULL
               AND COLUMNPROPERTY(OBJECT_ID(@v_QualifiedName), @v_ColRenameNew, 'AllowsNull') IS NOT NULL
            BEGIN
                DECLARE @v_ColClashMsg NVARCHAR(2048) =
                  'BootstrapTableQuench: both ' + @v_QualifiedName + '.[' + @v_ColRenameOld + '] (OldName) and [' +
                  @v_ColRenameNew + '] already exist; resolve manually before bootstrap can rename.';
                THROW 51000, @v_ColClashMsg, 1;
            END

            IF COLUMNPROPERTY(OBJECT_ID(@v_QualifiedName), @v_ColRenameOld, 'AllowsNull') IS NOT NULL
               AND COLUMNPROPERTY(OBJECT_ID(@v_QualifiedName), @v_ColRenameNew, 'AllowsNull') IS NULL
            BEGIN
                -- EXEC does not accept an expression as an argument -- assign the concatenation to a
                -- variable first (see the table-rename fix above for the same reasoning). The third
                -- argument 'COLUMN' is required for a column rename -- omitting it makes sp_rename guess.
                SET @v_ColRenameFrom = @v_SchemaBare + '.' + @v_NameBare + '.' + @v_ColRenameOld;
                EXEC sp_rename @v_ColRenameFrom, @v_ColRenameNew, 'COLUMN';
            END

            SET @v_ColRenameIdx = @v_ColRenameIdx + 1;
        END

        -- Step 4: ADD COLUMN for any columns missing on an existing table (one multi-column ALTER).
        DECLARE @v_AddColumns NVARCHAR(MAX) =
            (SELECT STRING_AGG(CAST(
                ColumnName + ' ' + DataType +
                CASE WHEN Nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END +
                CASE WHEN RTRIM(ISNULL([Default], '')) <> '' THEN ' DEFAULT ' + [Default] ELSE '' END
              AS NVARCHAR(MAX)), ', ') WITHIN GROUP (ORDER BY OrdinalPos)
               FROM @v_Columns c
              WHERE NOT EXISTS (
                  SELECT 1 FROM sys.columns sc
                   WHERE sc.object_id = OBJECT_ID(@v_QualifiedName)
                     AND sc.name = c.ColumnNameBare
              ));

        IF @v_AddColumns IS NOT NULL
        BEGIN
            SET @v_SQL = 'ALTER TABLE ' + @v_QualifiedName + ' ADD ' + @v_AddColumns;
            EXEC(@v_SQL);
        END
    END;

    -- Step 4.4: a declared PRIMARY KEY whose deployed shape differs is swapped IN PLACE. A PK cannot be dropped
    -- with DROP INDEX and cannot be rebuilt by the create pass below, so it is handled here: DROP CONSTRAINT then
    -- ADD CONSTRAINT, inside one transaction so the table is never left without its key. The rows are untouched.
    DECLARE @v_DeclPkName NVARCHAR(500), @v_DeclPkBare NVARCHAR(500), @v_DeclPkCols NVARCHAR(MAX), @v_DeclPkClustered BIT;
    DECLARE @v_PkName SYSNAME, @v_PkCols NVARCHAR(MAX), @v_PkClustered BIT, @v_Dupes INT;

    SELECT TOP 1 @v_DeclPkName = IndexName, @v_DeclPkBare = IndexNameBare,
                 @v_DeclPkCols = IndexColumns, @v_DeclPkClustered = [Clustered]
      FROM @v_Indexes WHERE PrimaryKey = 1 ORDER BY OrdinalPos;

    IF @v_DeclPkName IS NOT NULL
    BEGIN
        SELECT @v_PkName = kc.[name],
               @v_PkClustered = CASE WHEN si.type_desc = 'CLUSTERED' THEN 1 ELSE 0 END,
               @v_PkCols = (SELECT STRING_AGG(CAST(c.[name] AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                        FROM sys.index_columns ic
                        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                       WHERE ic.object_id = si.object_id AND ic.index_id = si.index_id AND ic.is_included_column = 0)
          FROM sys.key_constraints kc
          JOIN sys.indexes si ON si.object_id = kc.parent_object_id AND si.index_id = kc.unique_index_id
         WHERE kc.parent_object_id = OBJECT_ID(@v_QualifiedName) AND kc.[type] = 'PK';

        IF @v_PkName IS NOT NULL
           AND (@v_PkName <> @v_DeclPkBare
                OR @v_PkClustered <> @v_DeclPkClustered
                OR REPLACE(REPLACE(REPLACE(@v_DeclPkCols, '[', ''), ']', ''), ' ', '') <> ISNULL(@v_PkCols, ''))
        BEGIN
            -- Refuse before touching anything when the data cannot satisfy the declared key. The ALTER would
            -- fail on its own, but this says which table, which key, and what to do about it.
            SET @v_SQL = N'SELECT @cnt = COUNT(*) FROM (SELECT 1 AS dup FROM ' + @v_QualifiedName +
                         N' GROUP BY ' + @v_DeclPkCols + N' HAVING COUNT(*) > 1) d';
            EXEC sp_executesql @v_SQL, N'@cnt INT OUTPUT', @cnt = @v_Dupes OUTPUT;
            IF @v_Dupes > 0
            BEGIN
                DECLARE @v_PkMsg NVARCHAR(600) =
                    N'SchemaSmith bootstrap: cannot rebuild PRIMARY KEY on ' + @v_QualifiedName + N' as (' +
                    @v_DeclPkCols + N') -- the table holds duplicate rows for that key. The existing key is ' +
                    N'unchanged; resolve the duplicates and re-run.';
                RAISERROR(@v_PkMsg, 16, 1);
                RETURN;
            END

            RAISERROR('  Rebuilding PRIMARY KEY on %s: the deployed key does not match its declaration', 10, 100, @v_QualifiedName) WITH NOWAIT;
            BEGIN TRANSACTION;
            SET @v_SQL = N'ALTER TABLE ' + @v_QualifiedName + N' DROP CONSTRAINT [' + @v_PkName + N']';
            EXEC(@v_SQL);
            SET @v_SQL = N'ALTER TABLE ' + @v_QualifiedName + N' ADD CONSTRAINT ' + @v_DeclPkName +
                         N' PRIMARY KEY ' + CASE WHEN @v_DeclPkClustered = 1 THEN N'CLUSTERED' ELSE N'NONCLUSTERED' END +
                         N' (' + @v_DeclPkCols + N')';
            EXEC(@v_SQL);
            COMMIT TRANSACTION;
        END
    END

    -- Step 4.5: a declared index that EXISTS UNDER THE RIGHT NAME BUT THE WRONG SHAPE is dropped here, so the
    -- create below rebuilds it. Existence-by-name alone was the hole: an index created by an older SchemaSmith
    -- (or by hand) kept whatever shape it had while the declaration in the JSON quietly did not hold. Shape is
    -- read from the catalog and compared only against what this JSON declares: uniqueness, clustering, and the
    -- key column list. A key backed by a UNIQUE CONSTRAINT cannot be dropped with DROP INDEX, so it is dropped
    -- as the constraint it is. PRIMARY KEY shape is out of scope: the loop below only considers indexes the
    -- declaration marks PrimaryKey = 0, because a PK index cannot be rebuilt without rebuilding the table.
    DECLARE @v_ReshapeSQL NVARCHAR(MAX);
    SET @v_ReshapeSQL =
        (SELECT STRING_AGG(CAST(
            -- A key backed by a CONSTRAINT (unique or primary key) cannot be dropped with DROP INDEX;
            -- SQL Server raises 3723 for a PK. Drop it as the constraint it is.
            CASE WHEN si.is_unique_constraint = 1 OR si.is_primary_key = 1
                 THEN 'ALTER TABLE ' + @v_QualifiedName + ' DROP CONSTRAINT ' + i.IndexName
                 ELSE 'DROP INDEX ' + i.IndexName + ' ON ' + @v_QualifiedName END
          AS NVARCHAR(MAX)), ';' + CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY i.OrdinalPos)
           FROM @v_Indexes i
           JOIN sys.indexes si
             ON si.object_id = OBJECT_ID(@v_QualifiedName)
            AND si.name = i.IndexNameBare
          WHERE i.PrimaryKey = 0
            AND (si.is_unique <> i.[Unique]
                 OR CASE WHEN si.type_desc = 'CLUSTERED' THEN 1 ELSE 0 END <> i.[Clustered]
                 OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(REPLACE(REPLACE(REPLACE(i.IndexColumns, '[', ''), ']', ''), ' ', '')),
                                       'ASC', ''), 'DESC', '~'), '~', ' DESC'), ',', ','), '  ', ' ') <>
                    ISNULL((SELECT STRING_AGG(CAST(c.[name] + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END AS NVARCHAR(MAX)), ',')
                                     WITHIN GROUP (ORDER BY ic.key_ordinal)
                              FROM sys.index_columns ic
                              JOIN sys.columns c
                                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                             WHERE ic.object_id = si.object_id
                               AND ic.index_id = si.index_id
                               AND ic.is_included_column = 0), '')
                 -- A filtered index, or one carrying INCLUDE columns, is a shape bootstrap cannot declare, so it
                 -- is by definition not what the declaration asks for.
                 OR si.has_filter = 1
                 OR EXISTS (SELECT 1 FROM sys.index_columns ic2
                             WHERE ic2.object_id = si.object_id AND ic2.index_id = si.index_id
                               AND ic2.is_included_column = 1)));

    IF @v_ReshapeSQL IS NOT NULL
    BEGIN
        RAISERROR('  Rebuilding index(es) on %s whose deployed shape does not match the declaration', 10, 100, @v_QualifiedName) WITH NOWAIT;
        EXEC(@v_ReshapeSQL);
    END;


    -- Step 5: CREATE INDEX for any non-PK indexes missing on the table.
    -- (PK indexes were attached at CREATE TABLE time; if the table already existed before
    -- this refactor, the legacy CREATE TABLE attached its own PK constraint.)
    SET @v_SQL =
        (SELECT STRING_AGG(CAST(
            'CREATE ' +
            CASE WHEN [Unique] = 1 THEN 'UNIQUE ' ELSE '' END +
            CASE WHEN [Clustered] = 1 THEN 'CLUSTERED ' ELSE 'NONCLUSTERED ' END +
            'INDEX ' + IndexName +
            ' ON ' + @v_QualifiedName + ' (' + IndexColumns + ')'
          AS NVARCHAR(MAX)), ';' + CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY OrdinalPos)
           FROM @v_Indexes i
          WHERE PrimaryKey = 0
            AND NOT EXISTS (
                SELECT 1 FROM sys.indexes si
                 WHERE si.object_id = OBJECT_ID(@v_QualifiedName)
                   AND si.name = i.IndexNameBare
            ));

    IF @v_SQL IS NOT NULL
        EXEC(@v_SQL);
END TRY
BEGIN CATCH
    THROW;
END CATCH
