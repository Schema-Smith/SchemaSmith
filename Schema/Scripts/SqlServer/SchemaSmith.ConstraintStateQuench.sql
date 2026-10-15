-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

IF OBJECT_ID('SchemaSmith.ConstraintStateQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ConstraintStateQuench
GO
-- The enforcement and validation state of every declared foreign key and check constraint, converged once the
-- constraint exists. Enforced false disables it (NOCHECK CONSTRAINT); Enforced true enables a disabled one, re-checking
-- the existing rows when Validated is true and not when it is false; Validated true over a constraint the engine does
-- not trust re-checks the rows (WITH CHECK CHECK CONSTRAINT), which fails when rows violate it -- the engine reporting
-- the truth. Validated false over a trusted constraint changes nothing: SQL Server cannot un-validate. An omitted
-- attribute is its default, so a package that says nothing re-enables a constraint someone disabled by hand.
-- NotForReplication is create-time state; ModifiedTableQuench converges it by drop and re-create.
CREATE PROCEDURE SchemaSmith.ConstraintStateQuench
  @WhatIf BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX)

  IF OBJECT_ID('tempdb..#ConstraintStates') IS NOT NULL DROP TABLE #ConstraintStates
  SELECT d.[ObjectType], d.[Schema], d.[Table], d.[Name], d.[Enforced], d.[Validated], o.is_disabled, o.is_not_trusted
    INTO #ConstraintStates
    FROM (SELECT 'foreignKey' AS [ObjectType], SchemaSmith.fn_StripBracketWrapping(k.[Schema]) AS [Schema], SchemaSmith.fn_StripBracketWrapping(k.[TableName]) AS [Table],
                 SchemaSmith.fn_StripBracketWrapping(k.[KeyName]) AS [Name], ISNULL(k.[Enforced], 1) AS [Enforced], ISNULL(k.[Validated], 1) AS [Validated]
            FROM #ForeignKeys k WITH (NOLOCK)
          UNION ALL
          SELECT 'constraint', SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
                 SchemaSmith.fn_StripBracketWrapping(c.[ConstraintName]), ISNULL(c.[Enforced], 1), ISNULL(c.[Validated], 1)
            FROM #CheckConstraints c WITH (NOLOCK)) d
    JOIN (SELECT parent_object_id, [name], is_disabled, is_not_trusted FROM sys.foreign_keys
          UNION ALL
          SELECT parent_object_id, [name], is_disabled, is_not_trusted FROM sys.check_constraints) o
      ON o.parent_object_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
     AND o.[name] COLLATE DATABASE_DEFAULT = d.[Name]

  RAISERROR('Converge constraint enforcement and validation', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  ' + x.[Verb] + ' ' + x.[Kind] + ' ' + REPLACE(REPLACE(x.[QualifiedName], '''', ''''''), '%', '%%') + ''', 10, 100) WITH NOWAIT' + CHAR(13) + CHAR(10) +
      'ALTER TABLE ' + QUOTENAME(s.[Schema]) + '.' + QUOTENAME(s.[Table]) + ' ' + x.[Clause] + ' CONSTRAINT ' + QUOTENAME(s.[Name]) + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + s.[ObjectType] + ''', ''' + REPLACE(x.[QualifiedName], '''', '''''') + ''', ''modified'')'
      AS NVARCHAR(MAX))
    FROM #ConstraintStates s
    CROSS APPLY (SELECT s.[Schema] + '.' + s.[Table] + '.' + s.[Name] AS [QualifiedName],
                        CASE s.[ObjectType] WHEN 'foreignKey' THEN 'foreign key' ELSE 'check constraint' END AS [Kind],
                        CASE WHEN s.[Enforced] = 0 THEN 'Disabling'
                             WHEN s.is_disabled = 1 THEN 'Enabling'
                             ELSE 'Validating' END AS [Verb],
                        CASE WHEN s.[Enforced] = 0 THEN 'NOCHECK'
                             WHEN s.[Validated] = 1 THEN 'WITH CHECK CHECK'
                             ELSE 'WITH NOCHECK CHECK' END AS [Clause]) x
    WHERE (s.[Enforced] = 0 AND s.is_disabled = 0)
       OR (s.[Enforced] = 1 AND s.is_disabled = 1)
       OR (s.[Enforced] = 1 AND s.[Validated] = 1 AND s.is_not_trusted = 1)
    ORDER BY s.[ObjectType], s.[Schema], s.[Table], s.[Name]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 BEGIN EXEC SchemaSmith.PrintWithNoWait @v_SQL; EXEC SchemaSmith.RecordWouldChanges @v_SQL END ELSE EXEC(@v_SQL)

  IF OBJECT_ID('tempdb..#ConstraintStates') IS NOT NULL DROP TABLE #ConstraintStates
  SET NOCOUNT OFF
END
