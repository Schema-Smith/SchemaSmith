-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.LedgerQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.LedgerQuench
GO
-- Converges an existing ledger table's generated-column names and HIDDEN flags, and its ledger view's name, to what the
-- package declares, and reports what SQL Server cannot change (the view's schema and column names). Reads the caller's
-- #Tables and #LedgerColumns. The ledger catalog columns arrive in SQL Server 2022 and a procedure binds the columns it
-- names when it is created, so the work is dynamic SQL, which the caller runs only on 2022 and later.
CREATE PROCEDURE SchemaSmith.LedgerQuench
  @WhatIf BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  RAISERROR('Converge ledger column and view names', 10, 100) WITH NOWAIT
  DECLARE @v_Body NVARCHAR(MAX) = N'
DECLARE @v_SQL NVARCHAR(MAX)

-- A declared generated column whose live column of the same role has another name is renamed, and its HIDDEN flag set;
-- SQL Server allows both on a ledger table. A role the package leaves out is not compared.
SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + x.[Stmt]
  FROM #LedgerColumns lc WITH (NOLOCK)
  JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = lc.[Schema] AND t.[Name] = lc.[TableName] AND t.NewTable = 0
  JOIN sys.columns sc ON sc.[object_id] = OBJECT_ID(t.[Schema] + ''.'' + t.[Name]) AND sc.generated_always_type = lc.[RoleType]
  CROSS APPLY (SELECT [Bare] = SchemaSmith.fn_StripBracketWrapping(lc.[ColumnName]),
                      [Qualified] = SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''.'' + SchemaSmith.fn_StripBracketWrapping(t.[Name])) d
  CROSS APPLY (SELECT [Stmt] =
                 CASE WHEN sc.[name] COLLATE DATABASE_DEFAULT <> d.[Bare]
                      THEN ''RAISERROR(''''  Renaming ledger column '' + REPLACE(REPLACE(d.[Qualified] + ''.'' + sc.[name], '''''''', ''''''''''''), ''%'', ''%%'') + '' to '' + REPLACE(REPLACE(d.[Bare], '''''''', ''''''''''''), ''%'', ''%%'') + '''''', 10, 100) WITH NOWAIT;'' +
                           '' EXEC sp_rename N'''''' + REPLACE(d.[Qualified] + ''.'' + sc.[name], '''''''', '''''''''''') + '''''', N'''''' + REPLACE(d.[Bare], '''''''', '''''''''''') + '''''', N''''COLUMN'''';'' +
                           '' INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''''column'''', N'''''' + REPLACE(d.[Qualified] + ''.'' + d.[Bare], '''''''', '''''''''''') + '''''', ''''modified'''');''
                      ELSE '''' END +
                 CASE WHEN sc.is_hidden <> lc.[Hidden]
                      THEN '' ALTER TABLE '' + t.[Schema] + ''.'' + t.[Name] + '' ALTER COLUMN '' + QUOTENAME(d.[Bare]) + CASE WHEN lc.[Hidden] = 1 THEN '' ADD HIDDEN;'' ELSE '' DROP HIDDEN;'' END +
                           '' INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''''column'''', N'''''' + REPLACE(d.[Qualified] + ''.'' + d.[Bare], '''''''', '''''''''''') + '''''', ''''modified'''');''
                      ELSE '''' END) x
  WHERE x.[Stmt] <> ''''
  FOR XML PATH(''''), TYPE).value(''.'', ''NVARCHAR(MAX)''), 1, 2, '''')
IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

-- The ledger view, managed only when the package declares one; then a member it leaves out is the engine''s default.
-- A new name in the same schema is applied with sp_rename. Another schema, and the view''s column names, cannot be
-- changed, so they are reported.
IF OBJECT_ID(''tempdb..#LedgerViews'') IS NOT NULL DROP TABLE #LedgerViews
SELECT [Qualified] = SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''.'' + SchemaSmith.fn_StripBracketWrapping(t.[Name]),
       [DeclaredSchema] = ISNULL(SchemaSmith.fn_StripBracketWrapping(t.[LedgerViewSchema]), SchemaSmith.fn_StripBracketWrapping(t.[Schema])),
       [DeclaredName] = ISNULL(SchemaSmith.fn_StripBracketWrapping(t.[LedgerViewName]), SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''_Ledger''),
       [LiveSchema] = CAST(OBJECT_SCHEMA_NAME(st.ledger_view_id) AS NVARCHAR(200)) COLLATE DATABASE_DEFAULT,
       [LiveName] = CAST(OBJECT_NAME(st.ledger_view_id) AS NVARCHAR(200)) COLLATE DATABASE_DEFAULT,
       [ColumnDifferences] = STUFF((SELECT '', '' + m.[Label] + '' '' + vc.[name] COLLATE DATABASE_DEFAULT + '' (declared '' + m.[Declared] + '')''
                                      FROM (VALUES (1, ''TransactionIdColumn'', ISNULL(SchemaSmith.fn_StripBracketWrapping(t.[LedgerTransactionIdColumn]), ''ledger_transaction_id'')),
                                                   (2, ''SequenceNumberColumn'', ISNULL(SchemaSmith.fn_StripBracketWrapping(t.[LedgerSequenceNumberColumn]), ''ledger_sequence_number'')),
                                                   (3, ''OperationTypeColumn'', ISNULL(SchemaSmith.fn_StripBracketWrapping(t.[LedgerOperationTypeColumn]), ''ledger_operation_type'')),
                                                   (4, ''OperationTypeDescColumn'', ISNULL(SchemaSmith.fn_StripBracketWrapping(t.[LedgerOperationTypeDescColumn]), ''ledger_operation_type_desc''))) m ([Type], [Label], [Declared])
                                      JOIN sys.columns vc ON vc.[object_id] = st.ledger_view_id AND vc.ledger_view_column_type = m.[Type]
                                     WHERE vc.[name] COLLATE DATABASE_DEFAULT <> m.[Declared]
                                     ORDER BY m.[Type]
                                     FOR XML PATH(''''), TYPE).value(''.'', ''NVARCHAR(MAX)''), 1, 2, '''')
  INTO #LedgerViews
  FROM #Tables t WITH (NOLOCK)
  JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + ''.'' + t.[Name])
 WHERE t.NewTable = 0 AND st.ledger_view_id IS NOT NULL
   AND COALESCE(t.[LedgerViewSchema], t.[LedgerViewName], t.[LedgerTransactionIdColumn], t.[LedgerSequenceNumberColumn],
                t.[LedgerOperationTypeColumn], t.[LedgerOperationTypeDescColumn]) IS NOT NULL

SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) +
         CASE WHEN v.[DeclaredSchema] <> v.[LiveSchema]
              THEN ''RAISERROR(''''  WARNING: '' + REPLACE(REPLACE(v.[Qualified], '''''''', ''''''''''''), ''%'', ''%%'') + '': the ledger view is in schema '' + REPLACE(REPLACE(v.[LiveSchema], '''''''', ''''''''''''), ''%'', ''%%'') +
                   '', the package declares '' + REPLACE(REPLACE(v.[DeclaredSchema], '''''''', ''''''''''''), ''%'', ''%%'') + ''. SQL Server cannot move a ledger view; it is left as it is.'''', 10, 100) WITH NOWAIT;''
              WHEN v.[DeclaredName] <> v.[LiveName]
              THEN ''RAISERROR(''''  Renaming ledger view '' + REPLACE(REPLACE(v.[LiveSchema] + ''.'' + v.[LiveName], '''''''', ''''''''''''), ''%'', ''%%'') + '' to '' + REPLACE(REPLACE(v.[DeclaredName], '''''''', ''''''''''''), ''%'', ''%%'') + '''''', 10, 100) WITH NOWAIT;'' +
                   '' EXEC sp_rename N'''''' + REPLACE(v.[LiveSchema] + ''.'' + v.[LiveName], '''''''', '''''''''''') + '''''', N'''''' + REPLACE(v.[DeclaredName], '''''''', '''''''''''') + '''''';'' +
                   '' INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''''view'''', N'''''' + REPLACE(v.[LiveSchema] + ''.'' + v.[DeclaredName], '''''''', '''''''''''') + '''''', ''''modified'''');''
              ELSE '''' END +
         CASE WHEN v.[ColumnDifferences] IS NOT NULL
              THEN '' RAISERROR(''''  WARNING: '' + REPLACE(REPLACE(v.[Qualified], '''''''', ''''''''''''), ''%'', ''%%'') + '': the ledger view''''''''s columns are '' + REPLACE(REPLACE(v.[ColumnDifferences], '''''''', ''''''''''''), ''%'', ''%%'') +
                   ''. SQL Server cannot rename them; they are left as they are.'''', 10, 100) WITH NOWAIT;''
              ELSE '''' END
  FROM #LedgerViews v
  FOR XML PATH(''''), TYPE).value(''.'', ''NVARCHAR(MAX)''), 1, 2, '''')
IF @v_SQL <> '''' BEGIN IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL) END
'
  EXEC sp_executesql @v_Body, N'@WhatIf BIT', @WhatIf = @WhatIf
END
