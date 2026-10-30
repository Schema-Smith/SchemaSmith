-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.StampTableOwnership', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.StampTableOwnership
GO
CREATE PROCEDURE SchemaSmith.StampTableOwnership
  @ProductName NVARCHAR(50),
  @WhatIf BIT = 0
AS
BEGIN
  -- Marks each declared table as this product's, from the caller's #Tables, #TableProperties and #SchemaList: a table
  -- this deploy created gets its marks here and nowhere else.
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX) = ''

  RAISERROR('Add missing ProductName extended property to tables', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('EXEC sp_addextendedproperty @name = N''ProductName'', @value = ''' + @ProductName + ''', ' +
                                                              '@level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ' +
                                                              '@level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''';' AS NVARCHAR(MAX))
                           FROM #Tables t WITH (NOLOCK)
                           WHERE NOT EXISTS (SELECT * FROM #TableProperties tp WITH (NOLOCK) WHERE t.[Schema] = tp.[Schema] AND t.[Name] = QUOTENAME(tp.TableName) AND tp.PropertyName = 'ProductName')
                             AND OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL  -- and the table physically exists
                             AND t.[MemoryOptimized] = 0  -- memory-optimized tables reject extended properties; their ownership is tracked in SchemaSmith.ProductOwnership below
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- Stamp/refresh the sticky PreventDrop protection marker so it tracks the package value each run
  -- (an existing table newly marked PreventDrop:true gets its property; one toggled back to false is
  -- updated). Covers the same present-table set as the ProductName stamp above (physical tables in #Tables).
  -- WHAT THE MARKER ALREADY SAYS, READ ONCE. The statement below generates one
  -- fn_listextendedproperty probe plus an sp_updateextendedproperty (or sp_addextendedproperty) PER
  -- TABLE, and then executes all of them -- unconditionally, so a re-deploy where nothing had changed
  -- still re-stamped every table in the package. On a 1,783-table model that is ~1,783 system-procedure
  -- calls to write values that already said what they were going to say, and it measured 5,541 ms.
  -- Reading the current values in one pass lets the generator skip tables that already agree, which on
  -- an unchanged package is all of them. The end state is identical either way.
  IF OBJECT_ID('tempdb..#ExistingPreventDrop') IS NOT NULL DROP TABLE #ExistingPreventDrop
  SELECT [MajorId] = ep.major_id, [Value] = CONVERT(NVARCHAR(100), ep.[value])
    INTO #ExistingPreventDrop
    FROM sys.extended_properties ep
   WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.[name] = 'PreventDrop'
  CREATE CLUSTERED INDEX [ixExistingPreventDrop] ON #ExistingPreventDrop ([MajorId])

  RAISERROR('Stamp/refresh PreventDrop protection marker', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
    'IF EXISTS (SELECT 1 FROM fn_listextendedproperty(N''PreventDrop'', N''Schema'', ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', N''Table'', ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', default, default)) ' +
    'EXEC sp_updateextendedproperty @name = N''PreventDrop'', @value = ''' + CASE WHEN t.[PreventDrop] = 1 THEN 'true' ELSE 'false' END + ''', @level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', @level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + '''; ' +
    'ELSE EXEC sp_addextendedproperty @name = N''PreventDrop'', @value = ''' + CASE WHEN t.[PreventDrop] = 1 THEN 'true' ELSE 'false' END + ''', @level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', @level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''';' AS NVARCHAR(MAX))
    FROM #Tables t WITH (NOLOCK)
    WHERE OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL  -- table physically exists
      AND t.[MemoryOptimized] = 0  -- memory-optimized tables reject extended properties; PreventDrop is tracked in SchemaSmith.ProductOwnership below
      -- Only where the marker is missing or disagrees. The generated SQL still probes and branches
      -- add-vs-update itself, so this narrows WHICH tables are visited, not what happens when one is.
      AND ISNULL((SELECT x.[Value] FROM #ExistingPreventDrop x WHERE x.[MajorId] = OBJECT_ID(t.[Schema] + '.' + t.[Name])), '')
          <> CASE WHEN t.[PreventDrop] = 1 THEN 'true' ELSE 'false' END
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  IF OBJECT_ID('tempdb..#ExistingPreventDrop') IS NOT NULL DROP TABLE #ExistingPreventDrop

  -- Memory-optimized tables reject extended properties, so their ProductName / PreventDrop ownership is
  -- recorded in SchemaSmith.ProductOwnership instead of stamped on the table (the read-side fold into
  -- #TableProperties near the top of ModifiedTableQuench turns these rows back into the ownership records every
  -- downstream check reads). Insert missing owner rows and refresh the marker each run so it tracks the
  -- package, mirroring the two extended-property stamps above. IndexName = '' is the table-level marker.
  -- Ownership bookkeeping is a real mutation, so like the other engines it is skipped under WhatIf.
  RAISERROR('Record memory-optimized table ownership in ProductOwnership', 10, 100) WITH NOWAIT
  IF @WhatIf = 0
  BEGIN
    INSERT INTO SchemaSmith.ProductOwnership ([Schema], [TableName], [IndexName], [ProductName], [PreventDrop])
      SELECT t.[Schema], SchemaSmith.fn_StripBracketWrapping(t.[Name]), '', @ProductName, ISNULL(t.[PreventDrop], 0)
        FROM #Tables t WITH (NOLOCK)
        WHERE t.[MemoryOptimized] = 1
          AND OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM SchemaSmith.ProductOwnership po WITH (NOLOCK)
                            WHERE po.[Schema] = t.[Schema] COLLATE DATABASE_DEFAULT
                              AND po.[TableName] = SchemaSmith.fn_StripBracketWrapping(t.[Name]) COLLATE DATABASE_DEFAULT
                              AND po.[IndexName] = '')

    UPDATE po
      SET po.[ProductName] = @ProductName, po.[PreventDrop] = ISNULL(t.[PreventDrop], 0)
      FROM SchemaSmith.ProductOwnership po
      JOIN #Tables t WITH (NOLOCK) ON t.[Schema] COLLATE DATABASE_DEFAULT = po.[Schema]
                                  AND SchemaSmith.fn_StripBracketWrapping(t.[Name]) COLLATE DATABASE_DEFAULT = po.[TableName]
      WHERE t.[MemoryOptimized] = 1
        AND po.[IndexName] = ''
        AND OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL
        AND (po.[ProductName] <> @ProductName OR po.[PreventDrop] <> ISNULL(t.[PreventDrop], 0))
  END

  -- Prune ownership rows for memory-optimized tables that no longer exist -- dropped by absence earlier in
  -- ModifiedTableQuench, or removed out of band. Mirrors the catalog-existence prune the other engines run; a
  -- PreventDrop-retained table still physically exists, so its row survives. Scoped to this product and
  -- this run's schemas. Runs after the drops above, so a table removed this run is already gone here.
  RAISERROR('Prune ProductOwnership for memory-optimized tables no longer present', 10, 100) WITH NOWAIT
  IF @WhatIf = 0
    DELETE po
      FROM SchemaSmith.ProductOwnership po
      JOIN #SchemaList sl WITH (NOLOCK) ON sl.[Schema] = po.[Schema] COLLATE DATABASE_DEFAULT
      WHERE po.[ProductName] = @ProductName COLLATE DATABASE_DEFAULT
        AND OBJECT_ID(po.[Schema] + '.' + QUOTENAME(po.[TableName]) + '') IS NULL
END
