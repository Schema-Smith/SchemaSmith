-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DegradeUnsupportedFullText', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DegradeUnsupportedFullText
GO
CREATE PROCEDURE SchemaSmith.DegradeUnsupportedFullText
AS
BEGIN
  -- STATISTICAL_SEMANTICS on a full-text column needs a registered semantic language statistics database (SQL Server
  -- 2012+); without one SQL Server refuses the whole full-text index with 41209. Under 'warn' the option is
  -- removed, so the index is still created; under 'fail' the deploy is refused before anything is created. Operates on
  -- the caller's #FullTextIndexes, and is called once it is filled, on both the table and --IndexOnly paths.
  --
  -- Without Full-Text Search installed SQL Server refuses every full-text index, so under 'warn' the table deploys
  -- without one and under 'fail' the deploy is refused, naming each table.
  IF EXISTS (SELECT 1 FROM #FullTextIndexes WITH (NOLOCK)) AND SchemaSmith.fn_FullTextInstalled() = 0
  BEGIN
    DECLARE @v_Tables NVARCHAR(MAX) = STUFF((SELECT ', ' + f.[Schema] + '.' + f.[TableName] FROM #FullTextIndexes f WITH (NOLOCK)
                                               ORDER BY f.[Schema], f.[TableName]
                                               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
    BEGIN
      DECLARE @v_NotInstalled NVARCHAR(2048) = 'Full-text indexes require Full-Text Search, which is not installed on this server; table(s): ' +
                                               LEFT(@v_Tables, 1800) + '.'
      RAISERROR(@v_NotInstalled, 16, 1)
      RETURN
    END

    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'full-text index (Full-Text Search not installed)', f.[Schema] + '.' + f.[TableName], 'downgraded'
        FROM #FullTextIndexes f WITH (NOLOCK)
    DECLARE @v_Skipped NVARCHAR(2048) = '  Full-text index skipped on ' + LEFT(@v_Tables, 1800) +
                                        ': Full-Text Search is not installed - the table deploys without it (downgraded)'
    RAISERROR('%s', 10, 100, @v_Skipped) WITH NOWAIT
    DELETE FROM #FullTextIndexes
    RETURN
  END

  IF NOT EXISTS (SELECT 1 FROM #FullTextIndexes WITH (NOLOCK) WHERE [Columns] LIKE '% STATISTICAL[_]SEMANTICS%')
    RETURN

  -- Through sp_executesql: the catalog view is 2012+, and naming it directly would fail at run time on 2008 R2.
  DECLARE @v_Registered BIT = 0
  IF SchemaSmith.fn_ServerMajorVersion() >= 11
    EXEC sp_executesql N'SELECT @r = CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_semantic_language_statistics_database) THEN 1 ELSE 0 END',
                       N'@r BIT OUTPUT', @r = @v_Registered OUTPUT
  IF @v_Registered = 1 RETURN

  IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
  BEGIN
    DECLARE @v_List NVARCHAR(MAX) = STUFF((SELECT ', ' + f.[Schema] + '.' + f.[TableName] FROM #FullTextIndexes f WITH (NOLOCK)
                                             WHERE f.[Columns] LIKE '% STATISTICAL[_]SEMANTICS%'
                                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    DECLARE @v_Msg NVARCHAR(2048) = 'Full-text STATISTICAL_SEMANTICS requires a registered semantic language statistics database; table(s): ' +
                                    LEFT(@v_List, 1800) + '.'
    RAISERROR(@v_Msg, 16, 1)
    RETURN
  END

  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT @@SPID, 'full-text statistical semantics (no semantic database)', f.[Schema] + '.' + f.[TableName], 'downgraded'
      FROM #FullTextIndexes f WITH (NOLOCK) WHERE f.[Columns] LIKE '% STATISTICAL[_]SEMANTICS%'
  RAISERROR('  Full-text STATISTICAL_SEMANTICS skipped (no semantic language statistics database registered - downgraded)', 10, 100) WITH NOWAIT
  UPDATE #FullTextIndexes SET [Columns] = REPLACE([Columns], ' STATISTICAL_SEMANTICS', '')
    WHERE [Columns] LIKE '% STATISTICAL[_]SEMANTICS%'
END
