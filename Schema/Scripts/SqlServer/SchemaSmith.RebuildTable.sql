-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Replaces one table with a table built to the DECLARED definition, carrying its rows across:
-- refuse-if-blocked, capture the identity counter, drop inbound foreign keys, create a shadow, copy,
-- reseed, swap, drop the old one. Nothing in here decides WHEN a rebuild should happen -- the caller
-- decides that and calls this; the procedure is also directly callable, which is what makes it testable
-- before any decision path exists.
--
-- Deliberately NOT this procedure's job: indexes, primary keys, unique/check constraints, foreign keys
-- and named defaults. The old table is dropped whole, which takes all of them with it, and the ordinary
-- quench passes that follow re-add them from the same JSON that produced #Columns. Re-adding them here
-- would duplicate that logic against a second source of truth -- so the surface stays small on purpose,
-- and the one thing this procedure owns is the DATA.
--
-- Reads the declared definition from #Columns / #Tables, so it MUST be called after
-- ParseTableJsonIntoTempTables has run in the caller's scope -- the same contract every quench
-- procedure already has. Called with no parse in scope it refuses rather than reading a stale or absent
-- working set.
--
-- Everything destructive runs inside ONE transaction with XACT_ABORT ON. See the transaction comment
-- below for exactly what a mid-flight failure leaves behind.
IF OBJECT_ID('SchemaSmith.RebuildTable', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.RebuildTable
GO
CREATE PROCEDURE SchemaSmith.RebuildTable
  @p_Schema NVARCHAR(128),
  @p_Table NVARCHAR(128),
  @p_WhatIf BIT = 0
AS
BEGIN
  SET NOCOUNT ON

  DECLARE @v_CrLf NCHAR(2) = CHAR(13) + CHAR(10)

  -- Callers pass names in either form ('dbo' from a catalog read, '[dbo]' from #Tables), so normalize
  -- once: strip to the raw name for sp_rename / catalog lookups, and bracket-wrap for emitted DDL.
  DECLARE @v_SchemaRaw NVARCHAR(128) = SchemaSmith.fn_StripBracketWrapping(LTRIM(RTRIM(ISNULL(@p_Schema, '')))),
          @v_TableRaw NVARCHAR(128) = SchemaSmith.fn_StripBracketWrapping(LTRIM(RTRIM(ISNULL(@p_Table, ''))))
  DECLARE @v_SchemaBr NVARCHAR(300) = SchemaSmith.fn_SafeBracketWrap(@v_SchemaRaw),
          @v_TableBr NVARCHAR(300) = SchemaSmith.fn_SafeBracketWrap(@v_TableRaw)
  DECLARE @v_Qualified NVARCHAR(600) = @v_SchemaBr + '.' + @v_TableBr
  DECLARE @v_ObjectId INT = OBJECT_ID(@v_Qualified, 'U')

  ----------------------------------------------------------------------------------------------------
  -- 1. REFUSE WHEN BLOCKED -- before any DDL, and in WhatIf too.
  --
  -- fn_RebuildBlockedReason names the live state a shadow copy would silently destroy (temporal history,
  -- a CDC capture instance, a replication article's identity, a Change Tracking baseline). A WhatIf
  -- preview that hid the refusal would tell the operator a rebuild is available on a table where it can
  -- never be, so the refusal fires in both modes. Shape mirrors the Always Encrypted refusal in
  -- ModifiedTableQuench.sql: name the table, name the state, point at Before/After migration scripts.
  ----------------------------------------------------------------------------------------------------
  DECLARE @v_BlockedReason NVARCHAR(4000) = SchemaSmith.fn_RebuildBlockedReason(@v_SchemaRaw, @v_TableRaw)
  IF @v_BlockedReason IS NOT NULL
  BEGIN
    RAISERROR('Table rebuild refused for %s.%s: %s. A rebuild replaces the table with a shadow copy, and that state lives outside the schema package -- the copy discards it and no re-deploy can put it back. Move this table with Before/After migration scripts, or clear the blocking state first and re-run.', 16, 1, @v_SchemaBr, @v_TableBr, @v_BlockedReason)
    RETURN
  END

  ----------------------------------------------------------------------------------------------------
  -- 2. CONTRACT AND SAFETY REFUSALS -- all before any DDL, all in both modes.
  ----------------------------------------------------------------------------------------------------

  -- No parsed working set in scope. Reaching the copy without one would build a shadow from nothing.
  IF OBJECT_ID('tempdb..#Columns') IS NULL OR OBJECT_ID('tempdb..#Tables') IS NULL
  BEGIN
    RAISERROR('Table rebuild refused for %s.%s: SchemaSmith.RebuildTable was called with no parsed table definition in scope. It reads the declared column set from the #Columns / #Tables temp tables that ParseTableJsonIntoTempTables populates, so it must be called from a scope where that parse has already run.', 16, 1, @v_SchemaBr, @v_TableBr)
    RETURN
  END

  IF @v_ObjectId IS NULL
  BEGIN
    RAISERROR('Table rebuild refused: %s does not exist as a table. There is nothing to rebuild. If this table is mid-rename, the rename pass has to land before a rebuild can be considered.', 16, 1, @v_Qualified)
    RETURN
  END

  IF NOT EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = @v_SchemaBr AND t.[Name] = @v_TableBr)
  BEGIN
    RAISERROR('Table rebuild refused for %s: the parsed working set carries no declaration for this table. Rebuilding to a definition that is not in the package would replace the table with an empty one.', 16, 1, @v_Qualified)
    RETURN
  END

  -- An UNAPPLIED TABLE RENAME. The package renames [OldName] -> [Name]; if BOTH names resolve to live
  -- tables the rename has not happened (or has been re-declared), and rebuilding the destination would
  -- act on the wrong table while the source still holds rows. Refuse rather than pick one.
  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
               WHERE t.[Schema] = @v_SchemaBr AND t.[Name] = @v_TableBr
                 AND RTRIM(ISNULL(t.[OldName], '')) NOT IN ('', '[]')
                 AND OBJECT_ID(@v_SchemaBr + '.' + t.[OldName], 'U') IS NOT NULL)
  BEGIN
    RAISERROR('Table rebuild refused for %s: the package declares an OldName that still resolves to a live table, so a table rename is pending. Let the rename land first -- rebuilding now would copy from the wrong table.', 16, 1, @v_Qualified)
    RETURN
  END

  -- An UNAPPLIED COLUMN RENAME. The copy matches columns BY CURRENT NAME. A column declared under its
  -- new name whose data still lives under [OldName] would match nothing, and the rebuild would drop that
  -- column's data with no error at all. This is the quietest data-loss shape in the whole procedure, so
  -- it is refused outright rather than guessed at.
  IF EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
               WHERE c.[Schema] = @v_SchemaBr AND c.[TableName] = @v_TableBr
                 AND RTRIM(ISNULL(c.[OldName], '')) NOT IN ('', '[]')
                 AND COLUMNPROPERTY(@v_ObjectId, SchemaSmith.fn_StripBracketWrapping(c.[OldName]), 'ColumnId') IS NOT NULL
                 AND COLUMNPROPERTY(@v_ObjectId, SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'ColumnId') IS NULL)
  BEGIN
    RAISERROR('Table rebuild refused for %s: a declared column carries an OldName that still exists on the live table under that old name, so a column rename is pending. The copy matches columns by their current name and would silently discard that column''s data. Let the rename land first.', 16, 1, @v_Qualified)
    RETURN
  END

  -- ALWAYS ENCRYPTED. The server holds no Column Master Key, so it cannot re-encrypt; a server-side
  -- INSERT ... SELECT across encrypted columns is not something this procedure can guarantee, and the
  -- failure mode of guessing wrong is ciphertext written under the wrong scheme. Refuse, matching the
  -- ModifiedTableQuench refusal, and send the operator to a client-side copy.
  IF EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
               WHERE c.[Schema] = @v_SchemaBr AND c.[TableName] = @v_TableBr
                 AND RTRIM(ISNULL(c.[EncryptionType], 'NONE')) <> 'NONE')
  BEGIN
    RAISERROR('Table rebuild refused for %s: the declared definition contains Always Encrypted columns, and a shadow copy would have to move ciphertext server-side on a standard (non-enclave) server. Use Before/After migration scripts to rebuild this table and copy the data client-side over a Column Encryption Setting=Enabled connection.', 16, 1, @v_Qualified)
    RETURN
  END

  ----------------------------------------------------------------------------------------------------
  -- 3. NAMES FOR THE SHADOW AND THE RENAMED-OUT ORIGINAL.
  --
  -- sysname caps at 128, so the base name is truncated before the suffix is appended rather than
  -- producing a name the engine rejects. Both names are refused if already taken -- a leftover from a
  -- previous run is an operator decision, not something to overwrite.
  ----------------------------------------------------------------------------------------------------
  DECLARE @v_ShadowRaw NVARCHAR(128) = LEFT(@v_TableRaw, 108) + '_SchemaSmithRebuild',
          @v_OldRaw NVARCHAR(128) = LEFT(@v_TableRaw, 108) + '_SchemaSmithOld'
  DECLARE @v_ShadowQualified NVARCHAR(600) = @v_SchemaBr + '.' + SchemaSmith.fn_SafeBracketWrap(@v_ShadowRaw),
          @v_OldQualified NVARCHAR(600) = @v_SchemaBr + '.' + SchemaSmith.fn_SafeBracketWrap(@v_OldRaw)

  IF OBJECT_ID(@v_ShadowQualified) IS NOT NULL OR OBJECT_ID(@v_OldQualified) IS NOT NULL
  BEGIN
    RAISERROR('Table rebuild refused for %s: the working names %s / %s are already in use. That is normally a leftover from an interrupted rebuild -- inspect it and drop it deliberately rather than having this run overwrite it.', 16, 1, @v_Qualified, @v_ShadowQualified, @v_OldQualified)
    RETURN
  END

  ----------------------------------------------------------------------------------------------------
  -- 4. IDENTITY -- capture the counter BEFORE anything is created or copied.
  --
  -- Identity is expressed INSIDE the DataType string in this codebase ("INT IDENTITY(1,1)"), never as a
  -- separate JSON property, so the declared side is detected by looking for a SPACE-PREFIXED "IDENTITY"
  -- token in #Columns.[DataType]. The leading space matters: a plain '%IDENTITY%' would also match a
  -- user-defined type whose NAME merely starts with those letters.
  --
  -- The captured value is IDENT_CURRENT on the ORIGINAL table -- the last value the table ever HANDED
  -- OUT, which is not the same as the largest value it still holds. With ids 1-3 and id 3 deleted,
  -- IDENT_CURRENT is 3 while max(id) is 2; reseeding to the copied max makes the next insert re-issue
  -- 3, a value already given to a row that existed. Anything that recorded the old id then aliases two
  -- different entities, and nothing errors. So: capture first, reseed to the capture, never to max().
  -- (It is also the only form that survives a NEGATIVE increment, where the counter is the LOWEST value
  -- issued and max() points the wrong way entirely.)
  --
  -- sys.identity_columns.last_value is NULL until the counter has actually issued something, which
  -- distinguishes "never inserted" from "seeded at 1". Reseeding a never-used counter would burn its
  -- seed value for no reason, so that case skips the reseed and lets the shadow keep its declared seed.
  ----------------------------------------------------------------------------------------------------
  DECLARE @v_DeclaredIdentityColumn NVARCHAR(300)
  SELECT TOP 1 @v_DeclaredIdentityColumn = c.[ColumnName]
    FROM #Columns c WITH (NOLOCK)
    WHERE c.[Schema] = @v_SchemaBr AND c.[TableName] = @v_TableBr
      AND UPPER(LTRIM(RTRIM(c.[DataType]))) LIKE '% IDENTITY%'
    ORDER BY c.[_RowId]

  DECLARE @v_CapturedIdentity DECIMAL(38, 0) = NULL
  IF EXISTS (SELECT 1 FROM sys.identity_columns WHERE [object_id] = @v_ObjectId AND last_value IS NOT NULL)
    SET @v_CapturedIdentity = CONVERT(DECIMAL(38, 0), IDENT_CURRENT(@v_Qualified))

  -- IDENTITY_INSERT is only needed when the declared identity column ALSO exists on the live table, i.e.
  -- when its values are actually being carried across. A brand-new identity column is not in the copy
  -- list at all, so the engine generates its values -- and a captured counter from the old table would
  -- then be meaningless (possibly lower than what the shadow just generated), which is why the reseed
  -- below is gated on the same flag.
  DECLARE @v_IdentityInCopy BIT = 0
  IF @v_DeclaredIdentityColumn IS NOT NULL
     AND COLUMNPROPERTY(@v_ObjectId, SchemaSmith.fn_StripBracketWrapping(@v_DeclaredIdentityColumn), 'ColumnId') IS NOT NULL
    SET @v_IdentityInCopy = 1

  ----------------------------------------------------------------------------------------------------
  -- 5. COLUMN LISTS.
  --
  -- The shadow's CREATE takes the WHOLE declared column set, ordered by [_RowId] -- the order the
  -- columns appear in the package file. Computed columns are included here (they are part of the
  -- declared definition, and nothing downstream re-adds a computed column that was silently dropped).
  --
  -- The COPY moves only the INTERSECTION of declared and live, which is what makes the three cases fall
  -- out without special-casing: a column declared but not live is new (it takes its DEFAULT or NULL and
  -- must not appear in the SELECT), a column live but not declared is being removed (it appears in
  -- neither list), and a column on both sides carries its data.
  --
  -- Three kinds of column are excluded from the copy even when they are on both sides, because INSERT
  -- cannot target them at all:
  --   * computed / persisted -- the shadow derives them, and inserting into one is an error;
  --   * TIMESTAMP / ROWVERSION -- the engine owns the value ("Cannot insert an explicit value into a
  --     timestamp column"); the row version is per-row-version state, not user data;
  --   * a COLUMN_SET -- it aggregates the sparse columns that are ALSO in the list, and writing both the
  --     set and its members in one INSERT is rejected. Copying the sparse columns individually carries
  --     the same data.
  -- The insert list and the select list are ONE string by construction: same columns, same order, so
  -- they cannot drift apart into a positional mismatch that would write data into the wrong column.
  ----------------------------------------------------------------------------------------------------
  DECLARE @v_ShadowColumnList NVARCHAR(MAX), @v_CopyColumnList NVARCHAR(MAX)

  SELECT @v_ShadowColumnList = STUFF((SELECT ', ' + CAST(c.[ColumnScript] AS NVARCHAR(MAX))
                                        FROM #Columns c WITH (NOLOCK)
                                        WHERE c.[Schema] = @v_SchemaBr AND c.[TableName] = @v_TableBr
                                        ORDER BY c.[_RowId]
                                        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')

  SELECT @v_CopyColumnList = STUFF((SELECT ', ' + CAST(SchemaSmith.fn_SafeBracketWrap(c.[ColumnName]) AS NVARCHAR(MAX))
                                      FROM #Columns c WITH (NOLOCK)
                                      WHERE c.[Schema] = @v_SchemaBr AND c.[TableName] = @v_TableBr
                                        AND RTRIM(ISNULL(c.[ComputedExpression], '')) = ''
                                        AND ISNULL(c.[IsColumnSet], 0) = 0
                                        AND UPPER(LTRIM(RTRIM(c.[DataType]))) <> 'TIMESTAMP'
                                        AND COLUMNPROPERTY(@v_ObjectId, SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'ColumnId') IS NOT NULL
                                      ORDER BY c.[_RowId]
                                      FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')

  IF RTRIM(ISNULL(@v_ShadowColumnList, '')) = ''
  BEGIN
    RAISERROR('Table rebuild refused for %s: the declared definition produced no columns to build the replacement from.', 16, 1, @v_Qualified)
    RETURN
  END

  -- Nothing to copy AND rows to lose. Every live column is being removed, so the rows would survive only
  -- as empty shells -- and manufacturing those is a guess about intent, not a data-preserving rebuild.
  IF @v_CopyColumnList IS NULL
  BEGIN
    DECLARE @v_HasRows BIT = 0
    DECLARE @v_ProbeSql NVARCHAR(MAX) = N'IF EXISTS (SELECT 1 FROM ' + @v_Qualified + N') SET @p_Has = 1'
    EXEC sp_executesql @v_ProbeSql, N'@p_Has BIT OUTPUT', @p_Has = @v_HasRows OUTPUT
    IF @v_HasRows = 1
    BEGIN
      RAISERROR('Table rebuild refused for %s: no declared column also exists on the live table, so there is nothing to copy, and the table is not empty. Rebuilding would destroy every row. Use Before/After migration scripts if the rows are meant to survive a full column replacement.', 16, 1, @v_Qualified)
      RETURN
    END
  END

  ----------------------------------------------------------------------------------------------------
  -- 6. BUILD EVERY STATEMENT UP FRONT.
  --
  -- Built before anything executes so WhatIf can print exactly what a real run would do, from exactly
  -- the same source -- a preview assembled by a second code path is a preview of something else.
  ----------------------------------------------------------------------------------------------------

  -- Inbound foreign keys: OTHER tables pointing AT this one. These must go before the swap, and the
  -- reason is NOT that they block the DROP.
  --
  -- Proven live on all four engines: sp_rename of the old table SUCCEEDS and the inbound FK FOLLOWS the
  -- rename onto the renamed-away table. After a swap the child would be constrained against the table
  -- that was moved aside instead of the one that replaced it. The DROP failing afterwards is merely what
  -- makes that visible -- if the drop were ever permitted (a CASCADE convenience, a future engine), the
  -- rebuild would silently sever the child's referential integrity instead of failing loudly.
  --
  -- They are NOT re-added here. Each one is defined in its OWNING table's JSON, so that table's
  -- foreign-key quench pass sees it missing and re-creates it from the package. Re-adding them inside
  -- this procedure would mean maintaining FK construction against a second source of truth.
  DECLARE @v_DropInboundFkSql NVARCHAR(MAX)
  SELECT @v_DropInboundFkSql = STUFF((SELECT @v_CrLf + CAST(
             'RAISERROR(''  Dropping inbound foreign key [' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '].[' + fk.[name] + ']'', 10, 100) WITH NOWAIT;' + @v_CrLf +
             'ALTER TABLE [' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '] DROP CONSTRAINT [' + fk.[name] + '];' + @v_CrLf +
             'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''foreignKey'', ''[' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '].[' + fk.[name] + ']'', ''dropped'');'
             AS NVARCHAR(MAX))
           FROM sys.foreign_keys fk
           WHERE fk.referenced_object_id = @v_ObjectId
           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')

  DECLARE @v_CreateShadowSql NVARCHAR(MAX) =
    'CREATE TABLE ' + @v_ShadowQualified + ' (' + @v_ShadowColumnList + ');'

  -- TABLOCKX on the source, held for the life of the transaction: without it a row inserted by another
  -- session AFTER the copy scan and BEFORE the rename is copied nowhere and then dropped with the old
  -- table -- a silent loss with no error on either side.
  DECLARE @v_CopySql NVARCHAR(MAX) =
    CASE WHEN @v_CopyColumnList IS NULL THEN NULL
         ELSE CASE WHEN @v_IdentityInCopy = 1 THEN 'SET IDENTITY_INSERT ' + @v_ShadowQualified + ' ON;' + @v_CrLf ELSE '' END +
              'INSERT INTO ' + @v_ShadowQualified + ' (' + @v_CopyColumnList + ')' + @v_CrLf +
              '  SELECT ' + @v_CopyColumnList + ' FROM ' + @v_Qualified + ' WITH (TABLOCKX);' +
              CASE WHEN @v_IdentityInCopy = 1 THEN @v_CrLf + 'SET IDENTITY_INSERT ' + @v_ShadowQualified + ' OFF;' ELSE '' END
    END

  -- DBCC takes a literal, not a variable, so the reseed is built as text. It applies the CAPTURED
  -- counter, never a value derived from the rows that were just copied.
  DECLARE @v_ReseedSql NVARCHAR(MAX) =
    CASE WHEN @v_IdentityInCopy = 1 AND @v_CapturedIdentity IS NOT NULL
         THEN 'DBCC CHECKIDENT(''' + REPLACE(@v_ShadowQualified, '''', '''''') + ''', RESEED, ' + CONVERT(NVARCHAR(40), @v_CapturedIdentity) + ') WITH NO_INFOMSGS;'
    END

  -- sp_rename's second argument is a bare name -- bracket-wrapping it would make the brackets part of
  -- the stored name.
  DECLARE @v_SwapSql NVARCHAR(MAX) =
    'EXEC sp_rename N''' + REPLACE(@v_Qualified, '''', '''''') + ''', N''' + REPLACE(@v_OldRaw, '''', '''''') + ''', ''OBJECT'';' + @v_CrLf +
    'EXEC sp_rename N''' + REPLACE(@v_ShadowQualified, '''', '''''') + ''', N''' + REPLACE(@v_TableRaw, '''', '''''') + ''', ''OBJECT'';'

  DECLARE @v_DropOldSql NVARCHAR(MAX) = 'DROP TABLE ' + @v_OldQualified + ';'

  ----------------------------------------------------------------------------------------------------
  -- 7. WHATIF -- print, execute nothing.
  ----------------------------------------------------------------------------------------------------
  IF @p_WhatIf = 1
  BEGIN
    RAISERROR('  Would rebuild table %s', 10, 100, @v_Qualified) WITH NOWAIT
    EXEC SchemaSmith.PrintWithNoWait @v_DropInboundFkSql
    EXEC SchemaSmith.PrintWithNoWait @v_CreateShadowSql
    EXEC SchemaSmith.PrintWithNoWait @v_CopySql
    EXEC SchemaSmith.PrintWithNoWait @v_ReseedSql
    EXEC SchemaSmith.PrintWithNoWait @v_SwapSql
    EXEC SchemaSmith.PrintWithNoWait @v_DropOldSql

    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      VALUES (@@SPID, 'table', @v_Qualified, 'wouldRebuild')

    -- WhatIf twin of the 'foreignKey'/'dropped' rows embedded in the drop batch above (that batch is
    -- printed, not executed, under WhatIf). Same source, same ObjectName shape as ModifiedTableQuench,
    -- so a preview's manifest lists the inbound keys a real run would take out.
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'foreignKey', '[' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '].[' + fk.[name] + ']', 'wouldDrop'
        FROM sys.foreign_keys fk
        WHERE fk.referenced_object_id = @v_ObjectId
    RETURN
  END

  ----------------------------------------------------------------------------------------------------
  -- 8. THE DESTRUCTIVE SEQUENCE -- one transaction, XACT_ABORT ON.
  --
  -- The window this closes is the one between the two renames: a failure there leaves the original moved
  -- aside under _SchemaSmithOld with nothing standing in its place, and the table the application reads
  -- simply does not exist any more. Every step from the first FK drop through the final DROP TABLE is
  -- therefore in ONE transaction, so a failure at ANY point rolls the whole thing back: the inbound
  -- foreign keys are restored, the shadow never existed, and the original table is untouched under its
  -- original name with all of its rows. The same holds if the session dies -- the transaction is rolled
  -- back on connection reset. XACT_ABORT ON makes that automatic for the statement-level errors (a
  -- constraint violation on the copy, a rename collision) that would otherwise merely fail the statement
  -- and let the batch carry on into the swap.
  --
  -- DBCC CHECKIDENT's reseed is itself not transactional, which does not matter here: it only ever
  -- targets the shadow, and a rollback drops the shadow entirely.
  --
  -- This opens its OWN transaction rather than joining one. Nothing in SchemaSmith's SQL Server path
  -- opens a transaction today -- not the quench procedures, not the C# driver -- so @@TRANCOUNT is 0 on
  -- entry and the ROLLBACK below unwinds exactly this rebuild. A future caller that wrapped it in an
  -- outer transaction would find that rollback taking their work with it, which is why that is called
  -- out here rather than left for someone to discover.
  ----------------------------------------------------------------------------------------------------
  SET XACT_ABORT ON

  BEGIN TRY
    BEGIN TRANSACTION

    RAISERROR('  Rebuilding table %s', 10, 100, @v_Qualified) WITH NOWAIT

    -- Counted under the same TABLOCKX the copy takes, so the before/after comparison below is a real
    -- invariant and not a race. This is the one operation in SchemaSmith that destroys user data, so it
    -- pays for a verification scan rather than trusting that INSERT ... SELECT moved everything.
    DECLARE @v_RowsBefore BIGINT, @v_RowsAfter BIGINT, @v_CountSql NVARCHAR(MAX)
    SET @v_CountSql = N'SELECT @p_Count = COUNT_BIG(*) FROM ' + @v_Qualified + N' WITH (TABLOCKX)'
    EXEC sp_executesql @v_CountSql, N'@p_Count BIGINT OUTPUT', @p_Count = @v_RowsBefore OUTPUT

    IF @v_DropInboundFkSql IS NOT NULL EXEC(@v_DropInboundFkSql)

    EXEC(@v_CreateShadowSql)

    IF @v_CopySql IS NOT NULL
    BEGIN
      EXEC(@v_CopySql)

      SET @v_CountSql = N'SELECT @p_Count = COUNT_BIG(*) FROM ' + @v_ShadowQualified
      EXEC sp_executesql @v_CountSql, N'@p_Count BIGINT OUTPUT', @p_Count = @v_RowsAfter OUTPUT

      IF ISNULL(@v_RowsAfter, -1) <> @v_RowsBefore
      BEGIN
        DECLARE @v_BeforeText NVARCHAR(40) = CONVERT(NVARCHAR(40), @v_RowsBefore),
                @v_AfterText NVARCHAR(40) = CONVERT(NVARCHAR(40), ISNULL(@v_RowsAfter, -1))
        RAISERROR('Table rebuild aborted for %s: the replacement holds %s rows but the original holds %s. Nothing has been changed -- the whole rebuild is rolled back.', 16, 1, @v_Qualified, @v_AfterText, @v_BeforeText)
      END
    END

    IF @v_ReseedSql IS NOT NULL EXEC(@v_ReseedSql)

    EXEC(@v_SwapSql)
    EXEC(@v_DropOldSql)

    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      VALUES (@@SPID, 'table', @v_Qualified, 'rebuilt')

    COMMIT TRANSACTION
  END TRY
  BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION
    DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE()
    RAISERROR(@v_RethrowMsg, 16, 1)
  END CATCH
END
