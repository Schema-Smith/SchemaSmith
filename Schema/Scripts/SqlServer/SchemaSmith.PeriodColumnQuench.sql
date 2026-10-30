-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.PeriodColumnQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.PeriodColumnQuench
GO
-- A temporal table's period columns, before any index is created so an index may name them.
--
-- A table declared IsTemporal that has no period yet gets its pair and PERIOD FOR SYSTEM_TIME: the declared columns
-- (lifted onto #Tables by LiftPeriodColumns) or, when the package declares none, the ValidFrom / ValidTo DATETIME2(7)
-- convention, emitted byte for byte as before. MissingIndexesAndConstraintsQuench turns versioning on afterwards.
--
-- A table that already has a period and DECLARES its columns converges in place, which SQL Server allows on a
-- versioned table (measured on 2022): a different name is a sp_rename, which the history table follows, and a
-- different HIDDEN is ALTER COLUMN ... ADD | DROP HIDDEN. A different precision cannot be applied -- SQL Server refuses
-- to alter a period column while the table is versioned, and dropping the period to get round it strips GENERATED
-- ALWAYS from both columns -- so the deploy warns and leaves the column. An undeclared pair on an existing table is
-- unmanaged and left as it is.
--
-- Everything is read through COLUMNPROPERTY and sys.columns, which every supported version has, so this compiles on
-- the 2008 R2 floor; below 2016 DegradeUnsupportedFeatures has already turned IsTemporal off and nothing qualifies.
CREATE PROCEDURE SchemaSmith.PeriodColumnQuench
  @WhatIf BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX), @v_Warn NVARCHAR(MAX)

  IF OBJECT_ID('tempdb..#LivePeriods') IS NOT NULL DROP TABLE #LivePeriods
  SELECT t.[Schema], t.[Name], o.[object_id],
         t.[PeriodStart], t.[PeriodEnd],
         [StartType] = ISNULL(t.[PeriodStartType], 'DATETIME2(7)'), [EndType] = ISNULL(t.[PeriodEndType], 'DATETIME2(7)'),
         [StartHidden] = ISNULL(t.[PeriodStartHidden], 0), [EndHidden] = ISNULL(t.[PeriodEndHidden], 0),
         [LiveStart] = (SELECT TOP 1 c.[name] COLLATE DATABASE_DEFAULT FROM sys.columns c WHERE c.[object_id] = o.[object_id] AND COLUMNPROPERTY(c.[object_id], c.[name], 'GeneratedAlwaysType') = 1),
         [LiveEnd] = (SELECT TOP 1 c.[name] COLLATE DATABASE_DEFAULT FROM sys.columns c WHERE c.[object_id] = o.[object_id] AND COLUMNPROPERTY(c.[object_id], c.[name], 'GeneratedAlwaysType') = 2),
         [LiveStartScale] = (SELECT TOP 1 c.scale FROM sys.columns c WHERE c.[object_id] = o.[object_id] AND COLUMNPROPERTY(c.[object_id], c.[name], 'GeneratedAlwaysType') = 1),
         [LiveEndScale] = (SELECT TOP 1 c.scale FROM sys.columns c WHERE c.[object_id] = o.[object_id] AND COLUMNPROPERTY(c.[object_id], c.[name], 'GeneratedAlwaysType') = 2),
         [LiveStartHidden] = (SELECT TOP 1 ISNULL(COLUMNPROPERTY(c.[object_id], c.[name], 'IsHidden'), 0) FROM sys.columns c WHERE c.[object_id] = o.[object_id] AND COLUMNPROPERTY(c.[object_id], c.[name], 'GeneratedAlwaysType') = 1),
         [LiveEndHidden] = (SELECT TOP 1 ISNULL(COLUMNPROPERTY(c.[object_id], c.[name], 'IsHidden'), 0) FROM sys.columns c WHERE c.[object_id] = o.[object_id] AND COLUMNPROPERTY(c.[object_id], c.[name], 'GeneratedAlwaysType') = 2)
    INTO #LivePeriods
    FROM #Tables t WITH (NOLOCK)
    CROSS APPLY (SELECT OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U') AS [object_id]) o
    WHERE t.IsTemporal = 1 AND o.[object_id] IS NOT NULL

  RAISERROR('Add period columns for tables defined as temporal', 10, 100) WITH NOWAIT
  -- The end column's default is the largest value of its own precision: SQL Server fills existing rows with it, and
  -- refuses the period unless every row's end is that maximum. DATETIME2(7) gives the literal the convention always used.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  Adding period columns to ' + p.[Schema] + '.' + p.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'ALTER TABLE ' + p.[Schema] + '.' + p.[Name] + ' ADD ' +
          ISNULL(p.[PeriodStart], '[ValidFrom]') + ' ' + p.[StartType] + ' GENERATED ALWAYS AS ROW START' + CASE WHEN p.[StartHidden] = 1 THEN ' HIDDEN' ELSE '' END +
          ' NOT NULL DEFAULT ''0001-01-01 00:00:00.0000000'', ' +
          ISNULL(p.[PeriodEnd], '[ValidTo]') + ' ' + p.[EndType] + ' GENERATED ALWAYS AS ROW END' + CASE WHEN p.[EndHidden] = 1 THEN ' HIDDEN' ELSE '' END +
          ' NOT NULL DEFAULT ''9999-12-31 23:59:59' + ISNULL('.' + NULLIF(REPLICATE('9', s.[EndScale]), ''), '') + ''', ' +
          'PERIOD FOR SYSTEM_TIME (' + ISNULL(p.[PeriodStart], 'ValidFrom') + ', ' + ISNULL(p.[PeriodEnd], 'ValidTo') + ');'
      AS NVARCHAR(MAX))
    FROM #LivePeriods p
    CROSS APPLY (SELECT CONVERT(INT, SUBSTRING(p.[EndType], CHARINDEX('(', p.[EndType]) + 1, 1)) AS [EndScale]) s
    WHERE p.[LiveStart] IS NULL
      AND NOT EXISTS (SELECT * FROM sys.extended_properties ep
                       WHERE ep.class = 1 AND ep.major_id = p.[object_id] AND ep.minor_id = 0 AND ep.[name] = N'SchemaSmith_SuspendedHistory')
    ORDER BY p.[Schema], p.[Name]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Converge declared period columns', 10, 100) WITH NOWAIT
  -- One row per period column whose table declares its pair and already has one; rename first, then HIDDEN by the
  -- declared name. Compared with a binary collation so a case-only rename is a rename.
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      CASE WHEN x.[Live] <> SchemaSmith.fn_StripBracketWrapping(x.[Declared]) COLLATE Latin1_General_BIN2
           THEN 'RAISERROR(''  Rename period column ' + REPLACE(REPLACE(x.[Schema] + '.' + x.[Name] + '.' + QUOTENAME(x.[Live]) + ' to ' + x.[Declared], '''', ''''''), '%', '%%') + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                'EXEC sp_rename ''' + REPLACE(SchemaSmith.fn_StripBracketWrapping(x.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(x.[Name]) + '.' + x.[Live], '''', '''''') + ''', ''' +
                     REPLACE(SchemaSmith.fn_StripBracketWrapping(x.[Declared]), '''', '''''') + ''', ''COLUMN'';' + CHAR(13) + CHAR(10)
           ELSE '' END +
      CASE WHEN x.[Hidden] <> x.[LiveHidden]
           THEN 'RAISERROR(''  Making period column ' + REPLACE(REPLACE(x.[Schema] + '.' + x.[Name] + '.' + x.[Declared], '''', ''''''), '%', '%%') + CASE WHEN x.[Hidden] = 1 THEN ' hidden' ELSE ' visible' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                'ALTER TABLE ' + x.[Schema] + '.' + x.[Name] + ' ALTER COLUMN ' + x.[Declared] + CASE WHEN x.[Hidden] = 1 THEN ' ADD' ELSE ' DROP' END + ' HIDDEN;' + CHAR(13) + CHAR(10)
           ELSE '' END +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''column'', ''' + REPLACE(x.[Schema] + '.' + x.[Name] + '.' + x.[Declared], '''', '''''') + ''', ''modified'');'
      AS NVARCHAR(MAX))
    FROM #LivePeriods p
    CROSS APPLY (SELECT p.[Schema], p.[Name], p.[PeriodStart] AS [Declared], p.[LiveStart] AS [Live], p.[StartHidden] AS [Hidden], p.[LiveStartHidden] AS [LiveHidden]
                 UNION ALL
                 SELECT p.[Schema], p.[Name], p.[PeriodEnd], p.[LiveEnd], p.[EndHidden], p.[LiveEndHidden]) x
    WHERE p.[PeriodStart] IS NOT NULL AND p.[PeriodEnd] IS NOT NULL AND p.[LiveStart] IS NOT NULL AND p.[LiveEnd] IS NOT NULL
      AND (x.[Live] <> SchemaSmith.fn_StripBracketWrapping(x.[Declared]) COLLATE Latin1_General_BIN2 OR x.[Hidden] <> x.[LiveHidden])
    ORDER BY x.[Schema], x.[Name], x.[Declared]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 BEGIN EXEC SchemaSmith.PrintWithNoWait @v_SQL; EXEC SchemaSmith.RecordWouldChanges @v_SQL END ELSE EXEC(@v_SQL)

  -- A precision the engine cannot change: one warning per table, naming each column with its live and declared type.
  SELECT @v_Warn = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  WARNING: ' + REPLACE(REPLACE(p.[Schema] + '.' + p.[Name] + ': ' + w.[Columns] +
          '. SQL Server cannot change the precision of a period column, so the deploy leaves it as it is; re-create the table to change it.', '''', ''''''), '%', '%%') +
      ''', 10, 100) WITH NOWAIT;' AS NVARCHAR(MAX))
    FROM #LivePeriods p
    CROSS APPLY (SELECT STUFF(
                   CASE WHEN p.[LiveStartScale] <> CONVERT(INT, SUBSTRING(p.[StartType], CHARINDEX('(', p.[StartType]) + 1, 1))
                        THEN '; period column ' + SchemaSmith.fn_StripBracketWrapping(p.[PeriodStart]) + ' is DATETIME2(' + CONVERT(NVARCHAR(2), p.[LiveStartScale]) + '), declared ' + p.[StartType] ELSE '' END +
                   CASE WHEN p.[LiveEndScale] <> CONVERT(INT, SUBSTRING(p.[EndType], CHARINDEX('(', p.[EndType]) + 1, 1))
                        THEN '; period column ' + SchemaSmith.fn_StripBracketWrapping(p.[PeriodEnd]) + ' is DATETIME2(' + CONVERT(NVARCHAR(2), p.[LiveEndScale]) + '), declared ' + p.[EndType] ELSE '' END,
                   1, 2, '') AS [Columns]) w
    WHERE p.[PeriodStart] IS NOT NULL AND p.[PeriodEnd] IS NOT NULL AND p.[LiveStart] IS NOT NULL AND w.[Columns] IS NOT NULL
    ORDER BY p.[Schema], p.[Name]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC(@v_Warn)

  IF OBJECT_ID('tempdb..#LivePeriods') IS NOT NULL DROP TABLE #LivePeriods
  SET NOCOUNT OFF
END
