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
  -- scheme and a redeploy does not re-create the index. 'fail' aborts naming every such index. Called from
  -- DegradeUnsupportedFeatures (the TableQuench path) and IndexOnly[Xml]Quench (the --IndexOnly path).
  DECLARE @v_Major INT = SchemaSmith.fn_ServerMajorVersion()
  IF @v_Major >= 11 OR NOT EXISTS (SELECT 1 FROM #SpatialIndexes WITH (NOLOCK) WHERE [Tessellation] LIKE '%[_]AUTO[_]GRID') RETURN

  IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
  BEGIN
    DECLARE @v_List NVARCHAR(MAX) = STUFF((SELECT ', ' + s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName]
                                             FROM #SpatialIndexes s WITH (NOLOCK)
                                             WHERE s.[Tessellation] LIKE '%[_]AUTO[_]GRID'
                                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    DECLARE @v_FailMsg NVARCHAR(2048) = 'The AUTO_GRID spatial tessellations require SQL Server 2012 (detected major ' +
                                        CONVERT(NVARCHAR(10), @v_Major) + '); spatial index(es): ' + LEFT(@v_List, 1700) + '.'
    RAISERROR(@v_FailMsg, 16, 1)
    RETURN
  END

  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT @@SPID, 'spatial AUTO_GRID tessellation (SQL Server 2012)', s.[Schema] + '.' + s.[TableName] + '.' + s.[IndexName], 'downgraded'
      FROM #SpatialIndexes s WITH (NOLOCK)
      WHERE s.[Tessellation] LIKE '%[_]AUTO[_]GRID'
  RAISERROR('  Spatial AUTO_GRID tessellation created as GRID (requires SQL Server 2012 - downgraded)', 10, 100) WITH NOWAIT

  UPDATE #SpatialIndexes SET [Tessellation] = REPLACE([Tessellation], '_AUTO_GRID', '_GRID') WHERE [Tessellation] LIKE '%[_]AUTO[_]GRID'
END
