-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[CapturePermissions]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[CapturePermissions]
GO
-- Renders what a DROP, or a rename-and-replace, takes off an object that the package does not carry, as the
-- statements that put it back: the permissions on the object and on its columns, an owner set on the object itself,
-- and the extended properties on the object and its columns. The quench calls this before it drops or swaps an
-- object and ReapplyPermissions with the result once the replacement exists. @Script is NULL when there is nothing.
--
-- Aggregated with FOR XML PATH rather than STRING_AGG so the one procedure serves every compatibility level,
-- including the XML-encoded path below the OPENJSON cliff. An extended property is written through an EXISTS guard
-- because the quench adds its own ownership property to a re-created view before the reapply runs.
CREATE PROCEDURE [SchemaSmith].[CapturePermissions]
  @Schema NVARCHAR(128),
  @Name NVARCHAR(128),
  @Script NVARCHAR(MAX) OUTPUT
AS
BEGIN
  SET NOCOUNT ON
  SET @Script = NULL
  DECLARE @v_ObjectId INT = OBJECT_ID(QUOTENAME(@Schema) + '.' + QUOTENAME(@Name))
  IF @v_ObjectId IS NULL RETURN

  DECLARE @v_CrLf NVARCHAR(2) = CHAR(13) + CHAR(10)
  DECLARE @v_Qualified NVARCHAR(300) = QUOTENAME(@Schema) + '.' + QUOTENAME(@Name)
  DECLARE @v_QualifiedLiteral NVARCHAR(600) = 'N''' + REPLACE(@v_Qualified, '''', '''''') + ''''
  DECLARE @v_Level1Type NVARCHAR(20) =
    CASE WHEN OBJECTPROPERTY(@v_ObjectId, 'IsView') = 1 THEN 'VIEW'
         WHEN OBJECTPROPERTY(@v_ObjectId, 'IsProcedure') = 1 THEN 'PROCEDURE'
         WHEN OBJECTPROPERTY(@v_ObjectId, 'IsScalarFunction') = 1 OR OBJECTPROPERTY(@v_ObjectId, 'IsTableFunction') = 1 OR OBJECTPROPERTY(@v_ObjectId, 'IsInlineFunction') = 1 THEN 'FUNCTION'
         ELSE 'TABLE' END

  -- GRANT and DENY, with the grant option, on the object and on each column, to each principal.
  DECLARE @v_Permissions NVARCHAR(MAX) = (
    SELECT CASE p.[state] WHEN 'D' THEN 'DENY ' ELSE 'GRANT ' END
         + p.permission_name COLLATE DATABASE_DEFAULT
         + ISNULL(' (' + QUOTENAME(c.[name]) + ')', '')
         + ' ON OBJECT::' + @v_Qualified
         + ' TO ' + QUOTENAME(USER_NAME(p.grantee_principal_id))
         + CASE WHEN p.[state] = 'W' THEN ' WITH GRANT OPTION' ELSE '' END + ';' + @v_CrLf
      FROM sys.database_permissions p
      LEFT JOIN sys.columns c ON c.[object_id] = p.major_id AND c.column_id = p.minor_id
     WHERE p.class = 1 AND p.major_id = @v_ObjectId AND p.[state] IN ('G', 'W', 'D')
     ORDER BY p.minor_id, p.grantee_principal_id, p.permission_name
       FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)')

  -- An owner set on the object itself; NULL principal_id means it follows the schema, which the replacement does too.
  DECLARE @v_Owner NVARCHAR(MAX) = (
    SELECT 'ALTER AUTHORIZATION ON OBJECT::' + @v_Qualified + ' TO ' + QUOTENAME(USER_NAME(o.principal_id)) + ';' + @v_CrLf
      FROM sys.objects o
     WHERE o.[object_id] = @v_ObjectId AND o.principal_id IS NOT NULL)

  -- Extended properties on the object (minor_id 0) and on its columns, except SchemaSmith's own ownership stamps
  -- (ProductName on a table, SchemaSmith_Product on an indexed view), which the quench writes itself on the
  -- replacement. The value is written as text: every documentation property (MS_Description included) is one, and
  -- sp_addextendedproperty takes it as given.
  DECLARE @v_Properties NVARCHAR(MAX) = (
    SELECT 'IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND major_id = OBJECT_ID(' + @v_QualifiedLiteral + ') AND minor_id = '
         + CASE WHEN c.[name] IS NULL THEN '0' ELSE 'ISNULL(COLUMNPROPERTY(OBJECT_ID(' + @v_QualifiedLiteral + '), N''' + REPLACE(c.[name], '''', '''''') + ''', ''ColumnId''), -1)' END
         + ' AND [name] = N''' + REPLACE(ep.[name], '''', '''''') + ''')' + @v_CrLf
         + '  EXEC sp_addextendedproperty N''' + REPLACE(ep.[name], '''', '''''') + ''', N''' + REPLACE(CONVERT(NVARCHAR(MAX), ep.[value]), '''', '''''') + ''''
         + ', ''SCHEMA'', N''' + REPLACE(@Schema, '''', '''''') + ''', ''' + @v_Level1Type + ''', N''' + REPLACE(@Name, '''', '''''') + ''''
         + ISNULL(', ''COLUMN'', N''' + REPLACE(c.[name], '''', '''''') + '''', '') + ';' + @v_CrLf
      FROM sys.extended_properties ep
      LEFT JOIN sys.columns c ON c.[object_id] = ep.major_id AND c.column_id = ep.minor_id
     WHERE ep.class = 1 AND ep.major_id = @v_ObjectId AND ep.[name] NOT IN ('ProductName', 'SchemaSmith_Product')
     ORDER BY ep.minor_id, ep.[name]
       FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)')

  SET @Script = ISNULL(@v_Permissions, '') + ISNULL(@v_Owner, '') + ISNULL(@v_Properties, '')
  IF @Script = '' SET @Script = NULL
END
