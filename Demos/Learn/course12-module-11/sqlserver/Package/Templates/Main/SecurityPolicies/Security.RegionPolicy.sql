SET QUOTED_IDENTIFIER ON
IF OBJECT_ID('sys.security_policies') IS NULL
  RAISERROR('  Security policy [Security].[RegionPolicy] skipped: row-level security needs SQL Server 2016', 10, 1) WITH NOWAIT
ELSE
BEGIN
  DECLARE @policy NVARCHAR(600) = N'[Security].[RegionPolicy]', @v_Live NVARCHAR(MAX)
  SET @v_Live = (SELECT CONVERT(NVARCHAR(1), p.is_enabled) + CONVERT(NVARCHAR(1), p.is_schema_bound) + CONVERT(NVARCHAR(1), p.is_not_for_replication) + ISNULL((SELECT N'|' + sp.predicate_type_desc + N':' + ISNULL(sp.operation_desc, N'') + N':' + QUOTENAME(OBJECT_SCHEMA_NAME(sp.target_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(sp.target_object_id)) + N':' + sp.predicate_definition FROM sys.security_predicates sp WHERE sp.[object_id] = p.[object_id] ORDER BY OBJECT_SCHEMA_NAME(sp.target_object_id), OBJECT_NAME(sp.target_object_id), sp.predicate_type_desc, sp.operation_desc, sp.predicate_definition FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'') FROM sys.security_policies p WHERE p.[object_id] = OBJECT_ID(@policy))
  IF @v_Live IS NULL OR @v_Live <> N'110|FILTER::[dbo].[Orders]:([Security].[fn_RegionFilter]([Region]))'
  BEGIN
  BEGIN TRY
    BEGIN TRANSACTION
    IF @v_Live IS NOT NULL EXEC(N'DROP SECURITY POLICY [Security].[RegionPolicy]')
    EXEC(N'CREATE SECURITY POLICY [Security].[RegionPolicy]
  ADD FILTER PREDICATE [Security].[fn_RegionFilter]([Region]) ON [dbo].[Orders]
  WITH (STATE = ON, SCHEMABINDING = ON)')
    COMMIT
  END TRY
  BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK
    DECLARE @v_Error NVARCHAR(2048) = ERROR_MESSAGE()
    RAISERROR(@v_Error, 16, 1)
  END CATCH
  END
END
