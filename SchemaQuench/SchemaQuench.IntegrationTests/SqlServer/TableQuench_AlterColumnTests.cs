// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_AlterColumnTests : BaseTableQuenchTests
{
    [TestCase("Col1", "BIGINT")]
    [TestCase("Col2", "CHAR(20)")]
    [TestCase("Col3", "BINARY(10)")]
    [TestCase("Col4", "VARCHAR(20)")]
    [TestCase("Col5", "VARBINARY(10)")]
    [TestCase("Col6", "VARCHAR(MAX)")]
    [TestCase("Col7", "VARBINARY(MAX)")]
    [TestCase("Col8", "VARCHAR(100)")]
    [TestCase("Col9", "VARBINARY(100)")]
    [TestCase("Col10", "DATETIME2(5)")]
    [TestCase("Col11", "DECIMAL(12, 3)")]
    [TestCase("Col12", "DECIMAL(10, 2)")]
    [TestCase("Col13", "NUMERIC(12, 3)")]
    [TestCase("Col14", "NUMERIC(10, 2)")]
    [TestCase("Col15", "SYSNAME")]
    [TestCase("Col16", "NVARCHAR(128)")]
    [TestCase("Col17", "FLAG")]
    [TestCase("Col18", "BIT")]
    public void TableQuench_ShouldModifyColumnForChangeDataType(string colName, string expectedType)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "ChangeType", colName), Is.EqualTo(expectedType));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldNotAlterColumnWithSYSNAMEorUserDefinedType()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "DontChangeType", "Col1"), Is.EqualTo("SYSNAME"));
        Assert.That(GetColumnDataType(cmd, "DontChangeType", "Col2"), Is.EqualTo("FLAG"));

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.DontChangeType'), 'IDX_Dependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyColumnNullability()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.ChangeNullability'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleGoingToComputedColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.ColumnToComputed'), 'Column2', 'IsComputed') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleGoingFromComputedColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.ColumnFromComputed'), 'Column2', 'IsComputed') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleChangeComputedExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.ChangeComputedExpression'), 'Column2', 'IsComputed') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT [definition] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID('dbo.ChangeComputedExpression') AND [name] = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("('NewResult')"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldHandleChangingComputedColumnPersistence()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.MakePersisted'), 'Column2', 'IsComputed') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT [is_persisted] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID('dbo.MakePersisted') AND [name] = 'Column2'";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInIndex'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInIndex'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInIndex'), 'IDX_Dependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInIndex'), 'IDX_NoDependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInIndex", "Column2"), Is.EqualTo("BIGINT"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInIndexInclude()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInIndexInclude'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInIndexInclude'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInIndexInclude'), 'IDX_Dependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInIndexInclude'), 'IDX_NoDependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnInIndexInclude", "Column2"), Is.EqualTo("BIGINT"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInUniqueIndexReferencedByFullTextIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInUniqueIndexInFTIndex'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInUniqueIndexInFTIndex'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInUniqueIndexInFTIndex'), 'Column3', 'AllowsNull') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInUniqueIndexInFTIndex'), 'IDX_Dependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS (SELECT * FROM sys.fulltext_indexes fi WITH (NOLOCK) WHERE fi.[object_id] = OBJECT_ID('dbo.AlterColumnInUniqueIndexInFTIndex')) THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnUsedInUniqueIndexReferencedByForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInUniqueIndexInFK'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnInUniqueIndexInFK'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.AlterColumnInUniqueIndexInFK'), 'IDX_Dependency', 'IndexId') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.FK_AlterColumnInUniqueIndexInFK_SelfRef') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithDefault'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithDefault'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithDefault", "Column2"), Is.EqualTo("BIGINT"));

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping(COLUMN_DEFAULT) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'AlterColumnWithDefault' AND COLUMN_NAME = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithColumnLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithCheckConstraint'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithCheckConstraint'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithCheckConstraint", "Column2"), Is.EqualTo("BIGINT"));

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping([definition]) FROM sys.check_constraints ck WITH (NOLOCK) WHERE ck.[parent_object_id] = OBJECT_ID('dbo.AlterColumnWithCheckConstraint') AND COL_NAME(ck.parent_object_id, ck.parent_column_id) = 'Column2'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Column2]<(50)"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithTableLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithTableCheckConstraint'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithTableCheckConstraint'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithTableCheckConstraint", "Column2"), Is.EqualTo("BIGINT"));

        cmd.CommandText = "SELECT ck.[Name] FROM sys.check_constraints ck WITH (NOLOCK) WHERE ck.[parent_object_id] = OBJECT_ID('dbo.AlterColumnWithTableCheckConstraint') AND ck.parent_column_id = 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("CK_AlterColumnWithTableCheckConstraint_Dependency"));

        conn.Close();
    }

    [Test]
    public void TableQuench_AlterColumnWithStatistics()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithStatistics'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithStatistics'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithStatistics", "Column2"), Is.EqualTo("BIGINT"));

        cmd.CommandText = "SELECT [Name] FROM sys.stats ss  WITH (NOLOCK) WHERE ss.[object_id] = OBJECT_ID('dbo.AlterColumnWithStatistics') AND [Name] = 'ST_Dependency'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ST_Dependency"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithStatisticsFilterExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithStatisticsFilter'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithStatisticsFilter'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithStatisticsFilter", "Column2"), Is.EqualTo("BIGINT"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithIndexFilterExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithIndexFilter'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithIndexFilter'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithIndexFilter", "Column2"), Is.EqualTo("BIGINT"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithForeignKey()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithFK'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithFK'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithFK", "Column2"), Is.EqualTo("BIGINT"));

        conn.Close();
    }

    [Test]
    public void TableQuench_AlterColumnWithComputedExpression()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithComputed'), 'Column2', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithComputed'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithComputed", "Column2"), Is.EqualTo("BIGINT"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColumnWithFullTextIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.AlterColumnWithFTIndex'), 'Column1', 'AllowsNull') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        Assert.That(GetColumnDataType(cmd, "AlterColumnWithFTIndex", "Column2"), Is.EqualTo("VARCHAR(2000)"));

        cmd.CommandText = "SELECT CAST(CASE WHEN EXISTS (SELECT * FROM sys.fulltext_indexes fi WITH (NOLOCK) WHERE fi.[object_id] = OBJECT_ID('dbo.AlterColumnWithFTIndex')) THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterXmlColumnWithXmlIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "AlterXmlColumnWithXmlIndex", "Column3"), Is.EqualTo("XML([DBO].[MANUINSTRUCTIONSSCHEMACOLLECTION])"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddRowGuidColToExistingColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "AddRowGuidColToExistingColumn", "Column2"), Is.EqualTo("UNIQUEIDENTIFIER ROWGUIDCOL"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldRemoveRowGuidColFromExistingColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "RemoveRowGuidColFromExistingColumn", "Column2"), Is.EqualTo("UNIQUEIDENTIFIER"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAddNotForReplicationToExistingColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "AddNotForReplicationToExistingColumn", "Column1"), Is.EqualTo("INT IDENTITY(1, 1) NOT FOR REPLICATION"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldRemoveNotForReplicationFromExistingColumn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "RemoveNotForReplicationFromExistingColumn", "Column1"), Is.EqualTo("INT IDENTITY(1, 1)"));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyColumnCollation()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        
        cmd.CommandText = "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'COLLATION') AS VARCHAR(200))";
        var dbCollation = cmd.ExecuteScalar() as string;
        Assert.That(dbCollation, Is.Not.EqualTo("Latin1_General_CS_AS"));

        cmd.CommandText = "SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'ModifyColumnCollation' AND COLUMN_NAME = 'Column1'";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo(dbCollation));

        cmd.CommandText = "SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'ModifyColumnCollation' AND COLUMN_NAME = 'Column2'";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("Latin1_General_CS_AS"));

        cmd.CommandText = "SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'ModifyColumnCollation' AND COLUMN_NAME = 'Column3'";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("Latin1_General_CS_AS"));
        conn.Close();
    }


    [Test]
    public void TableQuench_ShouldMakeTypedXmlADocument()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(is_xml_document AS INT) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.TypedXmlToDocument') AND [name] = 'Body'";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "a column declared XML(DOCUMENT ...) must be a document column");
        Assert.That(GetColumnDataType(cmd, "TypedXmlToDocument", "Body"), Is.EqualTo("XML(DOCUMENT [dbo].[AlterXsc])").IgnoreCase);
        conn.Close();
    }

    // A type change must not reset a character column's collation: ALTER COLUMN without COLLATE moves it to the
    // database default, and refuses outright (5074) when the column is indexed.
    [TestCase("Declared", "Latin1_General_100_CS_AS")]
    [TestCase("Ignored", "Latin1_General_100_CS_AS")]
    [TestCase("Indexed", "Latin1_General_100_CS_AS")]
    [TestCase("DbDefault", null)]
    public void TableQuench_ShouldKeepCollationWhenTheTypeChanges(string column, string expectedCollation)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'COLLATION') AS VARCHAR(200))";
        var dbCollation = cmd.ExecuteScalar() as string;
        Assert.That(dbCollation, Is.Not.EqualTo("Latin1_General_100_CS_AS"), "the test needs a non-default collation");

        Assert.That(GetColumnDataType(cmd, "KeepCollationOnTypeChange", column), Is.EqualTo("VARCHAR(20)"),
            "the type change must have been applied");
        cmd.CommandText = "SELECT collation_name FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.KeepCollationOnTypeChange') AND [name] = '" + column + "'";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo(expectedCollation ?? dbCollation));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColmnSparseness()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.ModifyColmnSparseness'), 'Column1', 'IsSparse') = 0 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        cmd.CommandText = "SELECT CAST(CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.ModifyColmnSparseness'), 'Column2', 'IsSparse') = 1 THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldAlterColmnMasking()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT mc.masking_function FROM sys.masked_columns mc WITH (NOLOCK) WHERE mc.[object_id] = OBJECT_ID('dbo.ModifyColmnDataMasking') AND mc.[name] = 'Column1'";
        Assert.That(cmd.ExecuteScalar() as string, Is.Null);

        cmd.CommandText = "SELECT mc.masking_function FROM sys.masked_columns mc WITH (NOLOCK) WHERE mc.[object_id] = OBJECT_ID('dbo.ModifyColmnDataMasking') AND mc.[name] = 'Column2'";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("default()"));

        cmd.CommandText = "SELECT mc.masking_function FROM sys.masked_columns mc WITH (NOLOCK) WHERE mc.[object_id] = OBJECT_ID('dbo.ModifyColmnDataMasking') AND mc.[name] = 'Column3'";
        Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("default()"));

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
--TableQuench_ShouldModifyColumnForChangeDataType
CREATE TABLE dbo.ChangeType 
  (Col1 INT NOT NULL, Col2 CHAR(10) NOT NULL, Col3 BINARY(20) NOT NULL, Col4 VARCHAR(10) NOT NULL, Col5 VARBINARY(20) NOT NULL, Col6 VARCHAR(10) NOT NULL, 
   Col7 VARBINARY(10) NOT NULL, Col8 VARBINARY(10) NOT NULL, Col9 VARBINARY(MAX) NOT NULL, Col10 DATETIME2(3) NOT NULL, Col11 DECIMAL(12, 2) NOT NULL, 
   Col12 DECIMAL(12, 2) NOT NULL, Col13 NUMERIC(12, 2) NOT NULL, Col14 NUMERIC(12, 2) NOT NULL, Col15 NVARCHAR(128) NULL, Col16 SYSNAME NULL,
   Col17 BIT NOT NULL, Col18 Flag NOT NULL)
--TableQuench_ShouldNotAlterColumnWithSYSNAMEorUserDefinedType
CREATE TABLE dbo.DontChangeType (Col1 SYSNAME NOT NULL, Col2 Flag NOT NULL)
CREATE INDEX IDX_Dependency ON dbo.DontChangeType (Col1) INCLUDE (Col2)
--TableQuench_ShouldModifyColumnNullability
CREATE TABLE dbo.ChangeNullability (Column1 INT NULL)
--TableQuench_ShouldHandleGoingToComputedColumn
CREATE TABLE dbo.ColumnToComputed (Column1 INT NOT NULL, Column2 INT NULL)
--TableQuench_ShouldHandleGoingFromComputedColumn
CREATE TABLE dbo.ColumnFromComputed (Column1 INT NOT NULL, Column2 AS ([Column1] * 2))
--TableQuench_ShouldHandleChangeComputedExpression
CREATE TABLE dbo.ChangeComputedExpression (Column1 INT NOT NULL, Column2 AS ('OldResult'))
--TableQuench_ShouldHandleChangingComputedColumnPersistence
CREATE TABLE dbo.MakePersisted (Column1 INT NOT NULL, Column2 AS ([Column1] * 2))
--TableQuench_ShouldAlterColumnUsedInIndex
CREATE TABLE dbo.AlterColumnInIndex (Column1 INT NOT NULL, Column2 INT NULL)
CREATE INDEX IDX_NoDependency ON dbo.AlterColumnInIndex ([Column1])
CREATE INDEX IDX_Dependency ON dbo.AlterColumnInIndex ([Column2])
--TableQuench_ShouldAlterColumnUsedInIndexInclude
CREATE TABLE dbo.AlterColumnInIndexInclude (Column1 INT NOT NULL, Column2 INT)
CREATE INDEX IDX_NoDependency ON dbo.AlterColumnInIndexInclude ([Column1])
CREATE INDEX IDX_Dependency ON dbo.AlterColumnInIndexInclude ([Column1]) INCLUDE ([Column2])
--TableQuench_ShouldAlterColumnUsedInUniqueIndexReferencedByFullTextIndex
CREATE TABLE dbo.AlterColumnInUniqueIndexInFTIndex (Column1 INT NOT NULL, Column2 INT NOT NULL, Column3 VARCHAR(200) NULL)
CREATE UNIQUE INDEX IDX_Dependency ON dbo.AlterColumnInUniqueIndexInFTIndex ([Column2])
CREATE FULLTEXT INDEX ON dbo.AlterColumnInUniqueIndexInFTIndex (Column3) KEY INDEX IDX_Dependency ON FT_Catalog WITH CHANGE_TRACKING = OFF
--TableQuench_ShouldAlterColumnUsedInUniqueIndexReferencedByForeignKey
CREATE TABLE dbo.AlterColumnInUniqueIndexInFK (Column1 INT NOT NULL, Column2 INT NOT NULL)
CREATE UNIQUE INDEX IDX_Dependency ON dbo.AlterColumnInUniqueIndexInFK ([Column2])
ALTER TABLE dbo.AlterColumnInUniqueIndexInFK ADD CONSTRAINT FK_AlterColumnInUniqueIndexInFK_SelfRef FOREIGN KEY (Column1) REFERENCES dbo.AlterColumnInUniqueIndexInFK (Column2)
--TableQuench_ShouldAlterColumnWithDefault
CREATE TABLE dbo.AlterColumnWithDefault (Column1 INT NOT NULL, Column2 INT NOT NULL DEFAULT 0)
--TableQuench_ShouldAlterColumnWithColumnLevelCheckConstraint
CREATE TABLE dbo.AlterColumnWithCheckConstraint (Column1 INT NOT NULL, Column2 INT CHECK ([Column2] < 50))
--TableQuench_ShouldAlterColumnWithTableLevelCheckConstraint
CREATE TABLE dbo.AlterColumnWithTableCheckConstraint (Column1 INT NOT NULL, Column2 INT, CONSTRAINT CK_AlterColumnWithTableCheckConstraint_Dependency CHECK (Column2 < Column1))
--TableQuench_AlterColumnWithStatistics
CREATE TABLE dbo.AlterColumnWithStatistics (Column1 INT NOT NULL, Column2 INT)
CREATE STATISTICS ST_Dependency ON dbo.AlterColumnWithStatistics (Column2)
--TableQuench_ShouldAlterColumnWithStatisticsFilterExpression
CREATE TABLE dbo.AlterColumnWithStatisticsFilter (Column1 INT NOT NULL, Column2 INT)
CREATE STATISTICS ST_Dependency ON dbo.AlterColumnWithStatisticsFilter (Column1) WHERE Column2 < 100
--TableQuench_ShouldAlterColumnWithIndexFilterExpression
CREATE TABLE dbo.AlterColumnWithIndexFilter (Column1 INT NOT NULL, Column2 INT)
CREATE INDEX IDX_Dependency ON dbo.AlterColumnWithIndexFilter (Column1) WHERE Column2 < 100
--TableQuench_ShouldAlterColumnWithForeignKey
CREATE TABLE dbo.AlterColumnWithFK (Column1 INT NOT NULL, Column2 INT, CONSTRAINT PK_AlterColumnWithFK PRIMARY KEY (Column1))
CREATE UNIQUE INDEX UQ_Colum2 ON dbo.AlterColumnWithFK (Column2)
CREATE TABLE dbo.AlterColumnWithFKRef (Column1 INT NOT NULL, Column2 INT, CONSTRAINT PK_AlterColumnWithFKRef PRIMARY KEY (Column1))
ALTER TABLE dbo.AlterColumnWithFK ADD CONSTRAINT FK_AlterColumnWithFK_SelfRef FOREIGN KEY (Column2) REFERENCES dbo.AlterColumnWithFK (Column1)
ALTER TABLE dbo.AlterColumnWithFKRef ADD CONSTRAINT FK_AlterColumnWithFK_Referenced FOREIGN KEY (Column2) REFERENCES dbo.AlterColumnWithFK (Column2)
ALTER TABLE dbo.AlterColumnWithFK ADD CONSTRAINT FK_AlterColumnWithFK_Referencing FOREIGN KEY (Column2) REFERENCES dbo.AlterColumnWithFKRef (Column1)
--TableQuench_AlterColumnWithComputedExpression
CREATE TABLE dbo.AlterColumnWithComputed (Column1 INT NOT NULL, Column2 INT, Column3 AS (Column2*3))
--TableQuench_ShouldAlterColumnWithFullTextIndex
CREATE TABLE dbo.AlterColumnWithFTIndex (Column1 INT NOT NULL, Column2 VARCHAR(1000) NULL, CONSTRAINT PK_AlterColumnWithFTIndex PRIMARY KEY (Column1))
CREATE FULLTEXT INDEX ON dbo.AlterColumnWithFTIndex (Column2) KEY INDEX PK_AlterColumnWithFTIndex ON FT_Catalog WITH CHANGE_TRACKING = OFF
--TableQuench_ShouldAlterXmlColumnWithXmlIndex
CREATE TABLE dbo.AlterXmlColumnWithXmlIndex (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 XML NULL, CONSTRAINT PK_AlterXmlColumnWithXmlIndex PRIMARY KEY CLUSTERED (Column1))
CREATE PRIMARY XML INDEX [XI_Primary] ON dbo.AlterXmlColumnWithXmlIndex (Column3)
CREATE XML INDEX [XI_Secondary_Path] ON dbo.AlterXmlColumnWithXmlIndex (Column3) USING XML INDEX [XI_Primary] FOR PATH 
--TableQuench_ShouldAddRowGuidColToExistingColumn
CREATE TABLE dbo.AddRowGuidColToExistingColumn (Column1 INT NOT NULL, Column2 UNIQUEIDENTIFIER NOT NULL)
--TableQuench_ShouldRemoveRowGuidColFromExistingColumn
CREATE TABLE dbo.RemoveRowGuidColFromExistingColumn (Column1 INT NOT NULL, Column2 UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL)
--TableQuench_ShouldAddNotForReplicationToExistingColumn
CREATE TABLE dbo.AddNotForReplicationToExistingColumn (Column1 INT IDENTITY(1,1) NOT NULL, Column2 UNIQUEIDENTIFIER NOT NULL)
--TableQuench_ShouldRemoveNotForReplicationFromExistingColumn
CREATE TABLE dbo.RemoveNotForReplicationFromExistingColumn (Column1 INT IDENTITY(1,1) NOT FOR REPLICATION NOT NULL, Column2 UNIQUEIDENTIFIER NOT NULL)
--TableQuench_ShouldModifyColumnCollation
CREATE TABLE dbo.ModifyColumnCollation (Column1 VARCHAR(10) COLLATE Latin1_General_CS_AS NULL, Column2 VARCHAR(10) NULL, Column3 VARCHAR(10) COLLATE Latin1_General_CS_AS NULL)
--TableQuench_ShouldKeepCollationWhenTheTypeChanges
CREATE TABLE dbo.KeepCollationOnTypeChange (Declared VARCHAR(10) COLLATE Latin1_General_100_CS_AS NULL, Ignored VARCHAR(10) COLLATE Latin1_General_100_CS_AS NULL, Indexed VARCHAR(10) COLLATE Latin1_General_100_CS_AS NOT NULL, DbDefault VARCHAR(10) NULL)
CREATE INDEX IX_KeepCollationOnTypeChange_Indexed ON dbo.KeepCollationOnTypeChange (Indexed)
--TableQuench_ShouldMakeTypedXmlADocument
CREATE XML SCHEMA COLLECTION dbo.AlterXsc AS N'<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""r"" type=""xsd:string""/></xsd:schema>'
EXEC('CREATE TABLE dbo.TypedXmlToDocument (Id INT NOT NULL, Body XML(CONTENT dbo.AlterXsc) NULL)')
--TableQuench_ShouldAlterColmnSparseness
CREATE TABLE dbo.ModifyColmnSparseness (Column1 INT SPARSE NULL, Column2 INT NULL)
--TableQuench_ShouldAlterColmnDataMasking
CREATE TABLE dbo.ModifyColmnDataMasking (Column1 VARCHAR(100) MASKED WITH (FUNCTION='default()') NULL, Column2 VARCHAR(100) NULL, Column3 VARCHAR(100) MASKED WITH (FUNCTION='email()') NULL)
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Schema": "[dbo]",
                "Name": "[ChangeType]",
                "Columns": [
                    {
                      "Name": "[Col1]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "CHAR(20)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col3]",
                      "DataType": "BINARY(10)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col4]",
                      "DataType": "VARCHAR(20)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col5]",
                      "DataType": "VARBINARY(10)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col6]",
                      "DataType": "VARCHAR(MAX)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col7]",
                      "DataType": "VARBINARY(MAX)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col8]",
                      "DataType": "VARCHAR(100)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col9]",
                      "DataType": "VARBINARY(100)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col10]",
                      "DataType": "DATETIME2(5)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col11]",
                      "DataType": "DECIMAL(12, 3)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col12]",
                      "DataType": "DECIMAL(10, 2)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col13]",
                      "DataType": "NUMERIC(12, 3)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col14]",
                      "DataType": "NUMERIC(10, 2)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col15]",
                      "DataType": "SYSNAME",
                      "Nullable": true
                    },
                    {
                      "Name": "[Col16]",
                      "DataType": "NVARCHAR(128)",
                      "Nullable": true
                    },
                    {
                      "Name": "[Col17]",
                      "DataType": "Flag",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col18]",
                      "DataType": "BIT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ChangeNullability]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ColumnToComputed]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "ComputedExpression": "[Column1] * 2"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ColumnFromComputed]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ChangeComputedExpression]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "ComputedExpression": "'NewResult'"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[MakePersisted]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "ComputedExpression": "[Column1] * 2",
                      "Persisted": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnInIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                       "Name": "[IDX_Dependency]",
                       "IndexColumns": "[Column2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnInIndexInclude]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                       "Name": "[IDX_Dependency]",
                       "IndexColumns": "[Column1]",
                       "IncludeColumns": "[Column2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnInUniqueIndexInFTIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    }
                ],
                "Indexes": [
                    {
                       "Name": "[IDX_Dependency]",
                       "IndexColumns": "[Column2]",
                       "Unique": true
                    }
                ],
                "FullTextIndex": {
                    "FullTextCatalog": "FT_Catalog",
                    "KeyIndex": "IDX_Dependency",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column3]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnInUniqueIndexInFK]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ],
                "Indexes": [
                    {
                       "Name": "[IDX_Dependency]",
                       "IndexColumns": "[Column2]",
                       "Unique": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_AlterColumnInUniqueIndexInFK_SelfRef]",
                      "Columns": "[Column1]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[AlterColumnInUniqueIndexInFK]",
                      "RelatedColumns": "[Column2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithDefault]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false,
                      "Default": "0"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithCheckConstraint]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false,
                      "CheckExpression": "[Column2] < 50"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithTableCheckConstraint]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ],
                "CheckConstraints": [
                    {
                       "Name": "CK_AlterColumnWithTableCheckConstraint_Dependency",
                       "Expression": "Column2 < Column1"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithStatistics]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ],
                "Statistics": [
                    {
                       "Name": "ST_Dependency",
                       "Columns": "[Column2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithStatisticsFilter]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithIndexFilter]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithFK]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithComputed]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column3]",
                      "ComputedExpression": "Column2*3"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterColumnWithFTIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(2000)",
                      "Nullable": true
                    }
                ],
                "FullTextIndex": {
                    "FullTextCatalog": "FT_Catalog",
                    "KeyIndex": "PK_AlterColumnWithFTIndex",
                    "ChangeTracking": "OFF",
                    "Columns": "[Column2]"
                }
            },
            {
                "Schema": "[dbo]",
                "Name": "[AlterXmlColumnWithXmlIndex]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "BIGINT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(200)",
                      "Nullable": true
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "XML([MANUINSTRUCTIONSSCHEMACOLLECTION])",
                      "Nullable": true
                    }
                ],
                "Indexes": [
                    {
                      "Name": "[PK_AlterXmlColumnWithXmlIndex]",
                      "IndexColumns": "[Column1]",
                      "Clustered": true,
                      "PrimaryKey": true,
                      "Unique": true
                    }
                ],
                "XmlIndexes": [
                    {
                      "Name": "[XI_Primary]",
                      "Column": "[Column3]",
                      "IsPrimary": true
                    },
                    {
                      "Name": "[XI_Secondary_Path]",
                      "Column": "[Column3]",
                      "IsPrimary": false,
                      "PrimaryIndex": "[XI_Primary]",
                      "SecondaryIndexType": "PATH"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddRowGuidColToExistingColumn]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "UNIQUEIDENTIFIER ROWGUIDCOL",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[RemoveRowGuidColFromExistingColumn]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "UNIQUEIDENTIFIER",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[AddNotForReplicationToExistingColumn]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT IDENTITY(1,1) NOT FOR REPLICATION",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "UNIQUEIDENTIFIER",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[RemoveNotForReplicationFromExistingColumn]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "INT IDENTITY(1,1)",
                      "Nullable": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "UNIQUEIDENTIFIER",
                      "Nullable": false
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyColumnCollation]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Collation": ""
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Collation": "Latin1_General_CS_AS"
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Collation": "IGNORE"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[TypedXmlToDocument]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Body]", "DataType": "XML(DOCUMENT [dbo].[AlterXsc])", "Nullable": true }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[KeepCollationOnTypeChange]",
                "Columns": [
                    { "Name": "[Declared]", "DataType": "VARCHAR(20)", "Nullable": true, "Collation": "Latin1_General_100_CS_AS" },
                    { "Name": "[Ignored]", "DataType": "VARCHAR(20)", "Nullable": true, "Collation": "IGNORE" },
                    { "Name": "[Indexed]", "DataType": "VARCHAR(20)", "Nullable": false, "Collation": "Latin1_General_100_CS_AS" },
                    { "Name": "[DbDefault]", "DataType": "VARCHAR(20)", "Nullable": true, "Collation": "" }
                ],
                "Indexes": [
                    { "Name": "[IX_KeepCollationOnTypeChange_Indexed]", "IndexColumns": "[Indexed]" }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyColmnSparseness]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Sparse": false
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(10)",
                      "Nullable": true,
                      "Sparse": true
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyColmnDataMasking]",
                "Columns": [
                    {
                      "Name": "[Column1]",
                      "DataType": "VARCHAR(100)",
                      "Nullable": true,
                      "DataMaskFunction": ""
                    },
                    {
                      "Name": "[Column2]",
                      "DataType": "VARCHAR(100)",
                      "Nullable": true,
                      "DataMaskFunction": "default()"
                    },
                    {
                      "Name": "[Column3]",
                      "DataType": "VARCHAR(100)",
                      "Nullable": true,
                      "DataMaskFunction": "default()"
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
