-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DegradeUnsupportedSpatialIndexes', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DegradeUnsupportedSpatialIndexes
GO
CREATE PROCEDURE SchemaSmith.DegradeUnsupportedSpatialIndexes
AS
BEGIN
  -- The AUTO_GRID tessellations arrive in SQL Server 2012 (major 11). Below it a declared GEOMETRY_AUTO_GRID or
  -- GEOGRAPHY_AUTO_GRID index is created with the GRID scheme of the same family instead, so the column is still
  -- indexed; the change is rewritten into the caller's #SpatialIndexes, so the compare that follows reads the GRID
  -- scheme and a redeploy does not re-create the index. CREATE SPATIAL INDEX takes DATA_COMPRESSION from 2012 too
  -- (2008 R2 refuses the option as a syntax error, on every edition; 2012-2016 Express accept it): below it the index
  -- is created uncompressed and its compression is not compared. 'fail' aborts naming every such index. Called from DegradeUnsupportedFeatures (the TableQuench
  -- path) and IndexOnly[Xml]Quench (the --IndexOnly path).
  DECLARE @v_Major INT = SchemaSmith.fn_ServerMajorVersion()
  DECLARE @v_AutoGrid BIT = CASE WHEN @v_Major < 11 AND EXISTS (SELECT 1 FROM #SpatialIndexes WITH (NOLOCK) WHERE [Tessellation] LIKE '%[_]AUTO[_]GRID') THEN 1 ELSE 0 END
  DECLARE @v_Compressed BIT = CASE WHEN @v_Major < 11 AND EXISTS (SELECT 1 FROM #SpatialIndexes WITH (NOLOCK) WHERE [CompressionType] IN ('ROW', 'PAGE')) THEN 1 ELSE 0 END
  IF @v_AutoGrid = 0 AND @v_Compressed = 0 RETURN

  IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
  BEGIN
    DECLARE @v_FailMsg NVARCHAR(2048) = ''
    IF @v_AutoGrid = 1
      SET @v_FailMsg = 'The AUTO_GRID spatial tessellations require SQL Server 2012 (detected major ' + CONVERT(NVARCHAR(10), @v_Major) +
                       '); spatial index(es): ' + LEFT(STUFF((SELECT ', ' + s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName]
                                                               FROM #SpatialIndexes s WITH (NOLOCK)
                                                               WHERE s.[Tessellation] LIKE '%[_]AUTO[_]GRID'
                                                               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''), 800) + '.'
    IF @v_Compressed = 1
      SET @v_FailMsg = @v_FailMsg + CASE WHEN @v_FailMsg <> '' THEN ' ' ELSE '' END +
                       'Spatial index compression requires SQL Server 2012 (detected major ' + CONVERT(NVARCHAR(10), @v_Major) + '); spatial index(es): ' +
                       LEFT(STUFF((SELECT ', ' + s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName]
                                     FROM #SpatialIndexes s WITH (NOLOCK)
                                     WHERE s.[CompressionType] IN ('ROW', 'PAGE')
                                     FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''), 800) + '.'
    RAISERROR(@v_FailMsg, 16, 1)
    RETURN
  END

  IF @v_AutoGrid = 1
  BEGIN
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'spatial AUTO_GRID tessellation (SQL Server 2012)', s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName], 'downgraded'
        FROM #SpatialIndexes s WITH (NOLOCK)
        WHERE s.[Tessellation] LIKE '%[_]AUTO[_]GRID'
    DECLARE @v_AutoGridList NVARCHAR(MAX) = STUFF((SELECT ', ' + s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName] FROM #SpatialIndexes s WITH (NOLOCK)
                                                     WHERE s.[Tessellation] LIKE '%[_]AUTO[_]GRID'
                                                     FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    EXEC SchemaSmith.PrintDowngrade N'  Spatial AUTO_GRID tessellation created as GRID (requires SQL Server 2012 - downgraded)', @v_AutoGridList
    UPDATE #SpatialIndexes SET [Tessellation] = REPLACE([Tessellation], '_AUTO_GRID', '_GRID') WHERE [Tessellation] LIKE '%[_]AUTO[_]GRID'
  END

  IF @v_Compressed = 1
  BEGIN
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'spatial index compression (SQL Server 2012)', s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName], 'downgraded'
        FROM #SpatialIndexes s WITH (NOLOCK)
        WHERE s.[CompressionType] IN ('ROW', 'PAGE')
    DECLARE @v_CompressedList NVARCHAR(MAX) = STUFF((SELECT ', ' + s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName] FROM #SpatialIndexes s WITH (NOLOCK)
                                                       WHERE s.[CompressionType] IN ('ROW', 'PAGE')
                                                       FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    EXEC SchemaSmith.PrintDowngrade N'  Spatial index created uncompressed (requires SQL Server 2012 - downgraded)', @v_CompressedList
    UPDATE #SpatialIndexes SET [CompressionType] = NULL WHERE [CompressionType] IN ('ROW', 'PAGE')
  END
END
