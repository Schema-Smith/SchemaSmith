DECLARE @graph NVARCHAR(MAX) = N'{{TableSchema}}';
DECLARE @replicated NVARCHAR(MAX);

SELECT @replicated = N'[' + STRING_AGG(CAST(t.[value] AS NVARCHAR(MAX)), N',') + N']'
FROM OPENJSON(@graph) t
WHERE JSON_VALUE(t.[value], '$.Extensions.ReplicationEnabled') = 'true';

IF @replicated IS NOT NULL
BEGIN
  RAISERROR('Provisioning Shop_Replica', 10, 100) WITH NOWAIT;
  EXEC Shop_Replica.SchemaSmith.TableQuench
      @ProductName = N'{{ProductName}}',
      @TableDefinitions = @replicated,
      @WhatIf = 0,
      @DropUnknownIndexes = 0,
      @DropTablesRemovedFromProduct = 0,
      @UpdateFillFactor = 1;
END;
