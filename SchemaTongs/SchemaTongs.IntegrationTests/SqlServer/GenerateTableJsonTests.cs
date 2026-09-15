// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System;
using System.Linq;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Index = Schema.Domain.Index;

namespace SchemaTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class GenerateTableJsonTests
{
    private string _integrationDb = "";
    private string _connectionString;
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("GenerateTableJson");

        CreateTestDatabases();

        _testConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], _integrationDb, config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
    }

    [Test]
    public void ShouldGenerateCorrectJsonForXMLIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestXMLIndexes (
    MyInt INT NOT NULL,
    MyXml XML,
    MyXmlWithSchema XML(ManuInstructionsSchemaCollection) NULL,
    CONSTRAINT [PK_TestXMLIndexes] PRIMARY KEY CLUSTERED (MyInt)
)

CREATE PRIMARY XML INDEX [XI_Primary_MyXml] ON dbo.TestXMLIndexes (MyXml)
CREATE XML INDEX [XI_Secondary_MyXml_Path] ON dbo.TestXMLIndexes (MyXml) USING XML INDEX [XI_Primary_MyXml] FOR PATH
CREATE PRIMARY XML INDEX [XI_Primary_MyXmlWithSchema] ON dbo.TestXMLIndexes (MyXmlWithSchema)
CREATE XML INDEX [XI_Secondary_MyXmlWithSchema_Path] ON dbo.TestXMLIndexes (MyXmlWithSchema) USING XML INDEX [XI_Primary_MyXmlWithSchema] FOR PATH
CREATE XML INDEX [XI_Secondary_MyXmlWithSchema_Value] ON dbo.TestXMLIndexes (MyXmlWithSchema) USING XML INDEX [XI_Primary_MyXmlWithSchema] FOR VALUE
CREATE XML INDEX [XI_Secondary_MyXmlWithSchema_Property] ON dbo.TestXMLIndexes (MyXmlWithSchema) USING XML INDEX [XI_Primary_MyXmlWithSchema] FOR PROPERTY

EXEC sys.sp_addextendedproperty 'Description', 'Secondary XML Index for PROPERTY', 'SCHEMA', [dbo], 'TABLE', [TestXMLIndexes], 'INDEX', [XI_Secondary_MyXmlWithSchema_Property];
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "TestXMLIndexes");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("[dbo]"));
        Assert.That(result.Name, Is.EqualTo("[TestXMLIndexes]"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.XmlIndexes, Is.Not.Null);
        Assert.That(result.XmlIndexes, Has.Count.EqualTo(6));

        AssertXmlIndexProperties(result.XmlIndexes[0], "XI_Primary_MyXml", "MyXml", true, null, null);
        AssertXmlIndexProperties(result.XmlIndexes[1], "XI_Primary_MyXmlWithSchema", "MyXmlWithSchema", true, null, null);
        AssertXmlIndexProperties(result.XmlIndexes[2], "XI_Secondary_MyXml_Path", "MyXml", false, "XI_Primary_MyXml", "PATH");
        AssertXmlIndexProperties(result.XmlIndexes[3], "XI_Secondary_MyXmlWithSchema_Path", "MyXmlWithSchema", false, "XI_Primary_MyXmlWithSchema", "PATH");
        AssertXmlIndexProperties(result.XmlIndexes[4], "XI_Secondary_MyXmlWithSchema_Property", "MyXmlWithSchema", false, "XI_Primary_MyXmlWithSchema", "PROPERTY");
        AssertXmlIndexProperties(result.XmlIndexes[5], "XI_Secondary_MyXmlWithSchema_Value", "MyXmlWithSchema", false, "XI_Primary_MyXmlWithSchema", "VALUE");

        Assert.That(result.XmlIndexes[4].Extensions?["ExtendedProperties"], Is.Not.Null);
        Assert.That(result.XmlIndexes[4].Extensions?["ExtendedProperties"]?["Description"]?.ToString(), Is.EqualTo("Secondary XML Index for PROPERTY"));

        conn.Close();
    }

    private void AssertXmlIndexProperties(XmlIndex xmlIndex, string name, string column, bool isPrimary, string primaryIndex, string secondaryIndexType)
    {
        Assert.That(xmlIndex.Name, Is.EqualTo($"[{name}]"), $"Name of {name}");
        Assert.That(xmlIndex.Column, Is.EqualTo($"[{column}]"), $"Column of {name}");
        Assert.That(xmlIndex.IsPrimary, Is.EqualTo(isPrimary), $"IsPrimary of {name}");
        Assert.That(xmlIndex.PrimaryIndex, Is.EqualTo(primaryIndex == null ? null : $"[{primaryIndex}]"), $"PrimaryIndex of {name}");
        Assert.That(xmlIndex.SecondaryIndexType, Is.EqualTo(secondaryIndexType), $"SecondaryIndexType of {name}");
    }

    [Test]
    public void ShouldGenerateCorrectJsonForForeignKeys()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.MyFKTable (Id INT NOT NULL PRIMARY KEY, Col2 INT, Col3 INT)
CREATE TABLE dbo.MyFKReferencedTable (Id INT NOT NULL PRIMARY KEY, RefCol INT NOT NULL)
CREATE UNIQUE INDEX IDX_RefKey ON dbo.MyFKReferencedTable (RefCol)
ALTER TABLE dbo.MyFKTable ADD CONSTRAINT FK_MyFKTable_Col3_MyFKReferencedTable_Id FOREIGN KEY (Col3) REFERENCES dbo.MyFKReferencedTable (Id) ON DELETE CASCADE
ALTER TABLE dbo.MyFKTable ADD CONSTRAINT FK_MyFKTable_Col2_MyFKReferencedTable_RefCol FOREIGN KEY (Col2) REFERENCES dbo.MyFKReferencedTable (RefCol) ON UPDATE CASCADE

EXEC sys.sp_addextendedproperty 'Description', 'Foreign Key for Id', 'SCHEMA', [dbo], 'TABLE', [MyFKTable], 'CONSTRAINT', [FK_MyFKTable_Col3_MyFKReferencedTable_Id];
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "MyFKTable");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("[dbo]"));
        Assert.That(result.Name, Is.EqualTo("[MyFKTable]"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.ForeignKeys, Is.Not.Null);
        Assert.That(result.ForeignKeys, Has.Count.EqualTo(2));

        var fk0 = (SqlServerForeignKey)result.ForeignKeys[0];
        Assert.That(fk0.Name, Is.EqualTo("[FK_MyFKTable_Col2_MyFKReferencedTable_RefCol]"));
        Assert.That(fk0.Columns, Is.EqualTo("[Col2]"));
        Assert.That(fk0.RelatedTableSchema, Is.EqualTo("[dbo]"));
        Assert.That(fk0.RelatedTable, Is.EqualTo("[MyFKReferencedTable]"));
        Assert.That(fk0.RelatedColumns, Is.EqualTo("[RefCol]"));
        Assert.That(fk0.DeleteAction, Is.EqualTo("NO ACTION"));
        Assert.That(fk0.UpdateAction, Is.EqualTo("CASCADE"));
        var fk1 = (SqlServerForeignKey)result.ForeignKeys[1];
        Assert.That(fk1.Name, Is.EqualTo("[FK_MyFKTable_Col3_MyFKReferencedTable_Id]"));
        Assert.That(fk1.Columns, Is.EqualTo("[Col3]"));
        Assert.That(fk1.RelatedTableSchema, Is.EqualTo("[dbo]"));
        Assert.That(fk1.RelatedTable, Is.EqualTo("[MyFKReferencedTable]"));
        Assert.That(fk1.RelatedColumns, Is.EqualTo("[Id]"));
        Assert.That(fk1.DeleteAction, Is.EqualTo("CASCADE"));
        Assert.That(fk1.UpdateAction, Is.EqualTo("NO ACTION"));

        Assert.That(fk1.Extensions?["ExtendedProperties"], Is.Not.Null);
        Assert.That(fk1.Extensions?["ExtendedProperties"]?["Description"]?.ToString(), Is.EqualTo("Foreign Key for Id"));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForFullTextIndex()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestFullTextIndex (
    MyInt INT NOT NULL,
    MyBigInt BIGINT NOT NULL,
    MyString VARCHAR(100) NULL,
    CONSTRAINT [PK_TestFullTextIndex] PRIMARY KEY NONCLUSTERED (MyInt)
)

CREATE FULLTEXT INDEX ON dbo.TestFullTextIndex (MyString) KEY INDEX PK_TestFullTextIndex ON FT_Catalog WITH CHANGE_TRACKING = AUTO, STOPLIST = SL_TEST
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "TestFullTextIndex");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("[dbo]"));
        Assert.That(result.Name, Is.EqualTo("[TestFullTextIndex]"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.FullTextIndex, Has.Count.EqualTo(1));

        Assert.That(result.FullTextIndex[0].Columns, Is.EqualTo("[MyString]"));
        Assert.That(result.FullTextIndex[0].KeyIndex, Is.EqualTo("[PK_TestFullTextIndex]"));
        Assert.That(result.FullTextIndex[0].ChangeTracking, Is.EqualTo("AUTO"));
        Assert.That(result.FullTextIndex[0].StopList, Is.EqualTo("[SL_Test]"));
        Assert.That(result.FullTextIndex[0].FullTextCatalog, Is.EqualTo("[FT_Catalog]"));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestChecks (
    MyInt INT NOT NULL,
    MyBigInt BIGINT NOT NULL,
    MyString VARCHAR(100) NULL,
    CONSTRAINT [CK_TestChecks_MyInt] CHECK (MyInt < MyBigInt),
    CONSTRAINT [CK_TestChecks_MyBigInt] CHECK (MyBigInt > MyInt)
)

EXEC sys.sp_addextendedproperty 'Description', 'Table level check constraint', 'SCHEMA', [dbo], 'TABLE', [TestChecks], 'CONSTRAINT', [CK_TestChecks_MyBigInt];
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "TestChecks");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("[dbo]"));
        Assert.That(result.Name, Is.EqualTo("[TestChecks]"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.CheckConstraints, Is.Not.Null);
        Assert.That(result.CheckConstraints, Has.Count.EqualTo(2));

        Assert.That(result.CheckConstraints[0].Name, Is.EqualTo("[CK_TestChecks_MyBigInt]"));
        Assert.That(result.CheckConstraints[0].Expression, Is.EqualTo("[MyBigInt]>[MyInt]"));
        Assert.That(result.CheckConstraints[1].Name, Is.EqualTo("[CK_TestChecks_MyInt]"));
        Assert.That(result.CheckConstraints[1].Expression, Is.EqualTo("[MyInt]<[MyBigInt]"));

        Assert.That(result.CheckConstraints[0].Extensions?["ExtendedProperties"], Is.Not.Null);
        Assert.That(result.CheckConstraints[0].Extensions?["ExtendedProperties"]?["Description"]?.ToString(), Is.EqualTo("Table level check constraint"));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForStatistics()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestStatistics (
    MyInt INT NOT NULL,
    MyBigInt BIGINT NOT NULL,
    MyString VARCHAR(100) NULL
)

CREATE STATISTICS [ST_TestStatistics_MyInt] ON dbo.TestStatistics (MyInt)
CREATE STATISTICS [ST_TestStatistics_MyBigInt_ForNullStrings] ON dbo.TestStatistics (MyBigInt) WHERE MyString IS NULL
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "TestStatistics");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("[dbo]"));
        Assert.That(result.Name, Is.EqualTo("[TestStatistics]"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.Statistics, Is.Not.Null);
        Assert.That(result.Statistics, Has.Count.EqualTo(2));

        AssertStatisticProperties(result.Statistics[0], "ST_TestStatistics_MyBigInt_ForNullStrings", "[MyBigInt]", "[MyString] IS NULL");
        AssertStatisticProperties(result.Statistics[1], "ST_TestStatistics_MyInt", "[MyInt]", null);

        conn.Close();
    }

    private void AssertStatisticProperties(Schema.Domain.SqlServer.Statistic statistic, string name, string columns, string filterExpression)
    {
        Assert.That(statistic.Name, Is.EqualTo($"[{name}]"), $"Name of {name}");
        Assert.That(statistic.Columns, Is.EqualTo(columns), $"Columns of {name}");
        Assert.That(statistic.FilterExpression, Is.EqualTo(filterExpression), $"FilterExpression of {name}");
    }

    [Test]
    public void ShouldGenerateCorrectJsonForIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestIndexes (
    MyInt INT NOT NULL,
    MyBigInt BIGINT NOT NULL,
    MyString VARCHAR(100) NULL,
    CONSTRAINT [PK_TestIndexes] PRIMARY KEY NONCLUSTERED (MyInt) WITH (FILLFACTOR = 80, DATA_COMPRESSION = NONE),
    CONSTRAINT [UQ_TestIndexes_MyString] UNIQUE (MyString),
) WITH (DATA_COMPRESSION = NONE)

CREATE UNIQUE NONCLUSTERED INDEX [UX_TestIndexes_MyInt] ON dbo.TestIndexes (MyInt) INCLUDE(MyString, MyBigInt) WITH (FILLFACTOR = 100, DATA_COMPRESSION = ROW)
CREATE NONCLUSTERED INDEX [IX_TestIndexes_MyString] ON dbo.TestIndexes (MyString, MyBigInt) WHERE MyString IS NOT NULL WITH (FILLFACTOR = 90, DATA_COMPRESSION = PAGE)
CREATE CLUSTERED INDEX [CX_TestIndexes_MyBigInt] ON dbo.TestIndexes (MyBigInt) WITH (FILLFACTOR = 100, DATA_COMPRESSION = PAGE)

EXEC sys.sp_addextendedproperty 'Description', 'An index on MyString', 'SCHEMA', [dbo], 'TABLE', [TestIndexes], 'INDEX', [IX_TestIndexes_MyString];
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "TestIndexes");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("[dbo]"));
        Assert.That(result.Name, Is.EqualTo("[TestIndexes]"));
        Assert.That(result.CompressionType, Is.EqualTo("PAGE")); // will match the clustered index
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.Indexes, Is.Not.Null);
        Assert.That(result.Indexes, Has.Count.EqualTo(5));

        AssertIndexProperties(result.Indexes[0], "CX_TestIndexes_MyBigInt", true, false, false, false, "[MyBigInt]", null, 0, null, "PAGE");
        AssertIndexProperties(result.Indexes[1], "IX_TestIndexes_MyString", false, false, false, false, "[MyString],[MyBigInt]", null, 90, "[MyString] IS NOT NULL", "PAGE");
        AssertIndexProperties(result.Indexes[2], "PK_TestIndexes", false, true, true, false, "[MyInt]", null, 80, null, "NONE");
        AssertIndexProperties(result.Indexes[3], "UQ_TestIndexes_MyString", false, true, false, true, "[MyString]", null, 0, null, "NONE");
        AssertIndexProperties(result.Indexes[4], "UX_TestIndexes_MyInt", false, true, false, false, "[MyInt]", "[MyString],[MyBigInt]", 0, null, "ROW");

        Assert.That(result.Indexes[1].Extensions?["ExtendedProperties"], Is.Not.Null);
        Assert.That(result.Indexes[1].Extensions?["ExtendedProperties"]?["Description"]?.ToString(), Is.EqualTo("An index on MyString"));

        conn.Close();
    }

    private void AssertIndexProperties(Index index, string name, bool isCustered, bool isUnique, bool isPrimaryKey, bool isUniqueConstraint, string columns, string includeColumns, int fillFactor, string filterExpression, string compression)
    {
        var sqlIndex = (SqlServerIndex)index;
        Assert.That(sqlIndex.Name, Is.EqualTo($"[{name}]"), $"Name of {name}");
        Assert.That(sqlIndex.PrimaryKey, Is.EqualTo(isPrimaryKey), $"PrimaryKey of {name}");
        Assert.That(sqlIndex.Unique, Is.EqualTo(isUnique), $"Unique of {name}");
        Assert.That(sqlIndex.Clustered, Is.EqualTo(isCustered), $"Clustered of {name}");
        Assert.That(sqlIndex.UniqueConstraint, Is.EqualTo(isUniqueConstraint), $"UniqueConstraint of {name}");
        Assert.That(sqlIndex.CompressionType, Is.EqualTo(compression), $"Compression of {name}");
        Assert.That(sqlIndex.IndexColumns, Is.EqualTo(columns), $"IndexColumns of {name}");
        Assert.That(sqlIndex.IncludeColumns, Is.EqualTo(includeColumns), $"IncludeColumns of {name}");
        Assert.That(sqlIndex.FillFactor, Is.EqualTo(fillFactor), $"FillFactor of {name}");
        Assert.That(sqlIndex.FilterExpression, Is.EqualTo(filterExpression), $"FilterExpression of {name}");
    }

    [Test]
    public void ShouldGenerateCorrectJsonForColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestColumns (
    MyBit BIT NOT NULL,
    MyInt INT NULL,
    MyDecimal DECIMAL(10, 2) NOT NULL,
    MyNumeric NUMERIC(5, 3) NULL,
    MyNString NVARCHAR(100) NULL,
    MyString VARCHAR(200) NULL,
    MyDateTime DATETIME NULL,
    MyDateTime2 DATETIME2(4) NULL,
    MyXml XML NULL,
    MyMaxVarchar VARCHAR(MAX) NULL,
    MyMaxNvarchar NVARCHAR(MAX) NULL,
    MyMaxVarbinary VARBINARY(MAX) NULL,
    MyFloat FLOAT NULL,
    MyMoney MONEY NULL,
    MySmallint SMALLINT NULL,
    MyTinyint TINYINT NULL,
    MyBigint BIGINT NULL,
    MyUniqueIdentifier UNIQUEIDENTIFIER NULL,
    MyDate DATE NULL,
    MyTime TIME NULL,
    MySmallDateTime SMALLDATETIME NULL,
    MyBitWithDefault BIT NOT NULL DEFAULT 1,
    MyIntWithDefault INT NOT NULL DEFAULT 42,
    MyDecimalWithDefault DECIMAL(12, 4) NOT NULL DEFAULT 3.14,
    MyFlag [Test].[Flag],
    MySysname SYSNAME NULL,
    MyRowGuidCol UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL,
    MyIdentity INT IDENTITY(13,2) NOT NULL,
    MyXmlWithSchema XML(ManuInstructionsSchemaCollection) NULL,
    MyRowVersion ROWVERSION NULL,
    CHECK (MyMoney > 10)
)

EXEC sys.sp_addextendedproperty 'Description', 'Test Table', 'SCHEMA', [dbo], 'TABLE', [TestColumns], NULL, NULL;
EXEC sys.sp_addextendedproperty 'Description', 'An integer column', 'SCHEMA', [dbo], 'TABLE', [TestColumns], 'COLUMN', [MyInt];
";
        cmd.ExecuteNonQuery();

        var result = GenerateTable(cmd, "dbo", "TestColumns");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("[dbo]"));
        Assert.That(result.Name, Is.EqualTo("[TestColumns]"));
        Assert.That(result.CompressionType, Is.EqualTo("NONE"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(30));
        AssertColumnProperties(result.Columns[0], "MyBigint", "BIGINT", true, null, null);
        AssertColumnProperties(result.Columns[1], "MyBit", "BIT", false, null, null);
        AssertColumnProperties(result.Columns[2], "MyBitWithDefault", "BIT", false, "1", null);
        AssertColumnProperties(result.Columns[3], "MyDate", "DATE", true, null, null);
        AssertColumnProperties(result.Columns[4], "MyDateTime", "DATETIME", true, null, null);
        AssertColumnProperties(result.Columns[5], "MyDateTime2", "DATETIME2(4)", true, null, null);
        AssertColumnProperties(result.Columns[6], "MyDecimal", "DECIMAL(10, 2)", false, null, null);
        AssertColumnProperties(result.Columns[7], "MyDecimalWithDefault", "DECIMAL(12, 4)", false, "3.14", null);
        AssertColumnProperties(result.Columns[8], "MyFlag", "TEST.FLAG", false, null, null);
        AssertColumnProperties(result.Columns[9], "MyFloat", "FLOAT", true, null, null);
        AssertColumnProperties(result.Columns[10], "MyIdentity", "INT IDENTITY(13, 2)", false, null, null);
        AssertColumnProperties(result.Columns[11], "MyInt", "INT", true, null, null);
        AssertColumnProperties(result.Columns[12], "MyIntWithDefault", "INT", false, "42", null);
        AssertColumnProperties(result.Columns[13], "MyMaxNvarchar", "NVARCHAR(MAX)", true, null, null);
        AssertColumnProperties(result.Columns[14], "MyMaxVarbinary", "VARBINARY(MAX)", true, null, null);
        AssertColumnProperties(result.Columns[15], "MyMaxVarchar", "VARCHAR(MAX)", true, null, null);
        AssertColumnProperties(result.Columns[16], "MyMoney", "MONEY", true, null, "[MyMoney]>(10)");
        AssertColumnProperties(result.Columns[17], "MyNString", "NVARCHAR(100)", true, null, null);
        AssertColumnProperties(result.Columns[18], "MyNumeric", "NUMERIC(5, 3)", true, null, null);
        AssertColumnProperties(result.Columns[19], "MyRowGuidCol", "UNIQUEIDENTIFIER ROWGUIDCOL", false, null, null);
        AssertColumnProperties(result.Columns[20], "MyRowVersion", "TIMESTAMP", true, null, null); // we can't seem to get ROWVERSION back from SqlServer even thought they support the type
        AssertColumnProperties(result.Columns[21], "MySmallDateTime", "SMALLDATETIME", true, null, null);
        AssertColumnProperties(result.Columns[22], "MySmallint", "SMALLINT", true, null, null);
        AssertColumnProperties(result.Columns[23], "MyString", "VARCHAR(200)", true, null, null);
        AssertColumnProperties(result.Columns[24], "MySysname", "SYSNAME", true, null, null);
        // Bare TIME defaults to precision 7 -- extraction always renders it explicitly (matching
        // DATETIME2's established behavior and the canonicalization ParseTableJsonIntoTempTables.sql
        // applies to a bare-declared JSON DataType in this family), so it round-trips as TIME(7).
        AssertColumnProperties(result.Columns[25], "MyTime", "TIME(7)", true, null, null);
        AssertColumnProperties(result.Columns[26], "MyTinyint", "TINYINT", true, null, null);
        AssertColumnProperties(result.Columns[27], "MyUniqueIdentifier", "UNIQUEIDENTIFIER", true, null, null);
        AssertColumnProperties(result.Columns[28], "MyXml", "XML", true, null, null);
        AssertColumnProperties(result.Columns[29], "MyXmlWithSchema", "XML([dbo].[ManuInstructionsSchemaCollection])", true, null, null);

        Assert.That(result.CheckConstraints, Is.Not.Null);
        Assert.That(result.CheckConstraints, Has.Count.EqualTo(0));
        Assert.That(result.ForeignKeys, Is.Not.Null);
        Assert.That(result.ForeignKeys, Has.Count.EqualTo(0));
        Assert.That(result.Indexes, Is.Not.Null);
        Assert.That(result.Indexes, Has.Count.EqualTo(0));
        Assert.That(result.Statistics, Is.Not.Null);
        Assert.That(result.Statistics, Has.Count.EqualTo(0));
        Assert.That(result.FullTextIndex, Is.Not.Null.And.Empty);
        Assert.That(result.XmlIndexes, Is.Not.Null);
        Assert.That(result.XmlIndexes, Has.Count.EqualTo(0));
        Assert.That(result.UpdateFillFactor, Is.False);
        Assert.That(result.IsTemporal, Is.False);

        Assert.That(result.Extensions?["ExtendedProperties"], Is.Not.Null);
        Assert.That(result.Extensions?["ExtendedProperties"]?["Description"]?.ToString(), Is.EqualTo("Test Table"));
        Assert.That(result.Columns[11].Extensions?["ExtendedProperties"], Is.Not.Null);
        Assert.That(result.Columns[11].Extensions?["ExtendedProperties"]?["Description"]?.ToString(), Is.EqualTo("An integer column"));

        conn.Close();
    }

    private void AssertColumnProperties(Column column, string name, string dataType, bool nullable, string defaultValue, string check)
    {
        var sqlColumn = (SqlServerColumn)column;
        Assert.That(sqlColumn.Name, Is.EqualTo($"[{name}]"), $"Name of {name}");
        Assert.That(sqlColumn.DataType, Is.EqualTo(dataType), $"Type of {name}");
        Assert.That(sqlColumn.Nullable, Is.EqualTo(nullable), $"Nullability of {name}");
        Assert.That(sqlColumn.Default, Is.EqualTo(defaultValue), $"Default of {name}");
        Assert.That(sqlColumn.CheckExpression, Is.EqualTo(check), $"Check of {name}");
    }

    [Test]
    public void ShouldPreserveFractionalSecondsPrecisionOnExtraction()
    {
        // TIME(3)/DATETIMEOFFSET(3) precision-loss regression: the extraction CASE covered DATETIME2
        // among the fractional-seconds-precision types but not its two siblings, so both extracted as
        // the bare type name — the declared precision was silently dropped. Bare TIME (default
        // precision 7) is covered by ShouldGenerateCorrectJsonForColumns; this locks in the explicit-
        // precision case for both siblings.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestFractionalSecondsPrecision (
    MyInt INT NOT NULL PRIMARY KEY,
    MyTimePrecise TIME(3) NULL,
    MyDateTimeOffsetPrecise DATETIMEOFFSET(3) NULL
)
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "TestFractionalSecondsPrecision");
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        AssertColumnProperties(result.Columns.Single(c => c.Name == "[MyDateTimeOffsetPrecise]"), "MyDateTimeOffsetPrecise", "DATETIMEOFFSET(3)", true, null, null);
        AssertColumnProperties(result.Columns.Single(c => c.Name == "[MyTimePrecise]"), "MyTimePrecise", "TIME(3)", true, null, null);

        conn.Close();
    }

    [Test]
    public void ShouldExtractColumnSetAndSparseColumns()
    {
        // Backlog E3: COLUMN_SET FOR ALL_SPARSE_COLUMNS aggregates a table's sparse columns into one
        // updatable XML column. Both halves of the pairing must round-trip: the sparse columns keep
        // Sparse:true, and the aggregator gets the new IsColumnSet:true (not folded into DataType, so it
        // extracts as plain "XML" -- see SqlServerColumn.IsColumnSet).
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestColumnSet (
    MyInt INT NOT NULL PRIMARY KEY,
    SparseA VARCHAR(20) SPARSE NULL,
    SparseB INT SPARSE NULL,
    Aggregated XML COLUMN_SET FOR ALL_SPARSE_COLUMNS
)
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "dbo", "TestColumnSet");
        Assert.That(result.Columns, Has.Count.EqualTo(4));

        var sparseA = (SqlServerColumn)result.Columns.Single(c => c.Name == "[SparseA]");
        var sparseB = (SqlServerColumn)result.Columns.Single(c => c.Name == "[SparseB]");
        var aggregated = (SqlServerColumn)result.Columns.Single(c => c.Name == "[Aggregated]");

        Assert.Multiple(() =>
        {
            Assert.That(sparseA.Sparse, Is.True, "SparseA must extract as Sparse:true");
            Assert.That(sparseA.IsColumnSet, Is.False, "SparseA is not itself the column set");
            Assert.That(sparseB.Sparse, Is.True, "SparseB must extract as Sparse:true");
            Assert.That(sparseB.IsColumnSet, Is.False, "SparseB is not itself the column set");
            Assert.That(aggregated.IsColumnSet, Is.True, "Aggregated must extract as IsColumnSet:true");
            Assert.That(aggregated.Sparse, Is.False, "the column set column is not itself sparse");
            Assert.That(aggregated.DataType, Is.EqualTo("XML"), "the column set is an XML column -- COLUMN_SET FOR ALL_SPARSE_COLUMNS is carried by IsColumnSet, not folded into DataType");
        });

        conn.Close();
    }

    [Test]
    public void ShouldFilterInternalExtendedProperties()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.TestEPFilter (
    MyInt INT NOT NULL,
    MyString VARCHAR(100) NULL,
    CONSTRAINT [PK_TestEPFilter] PRIMARY KEY CLUSTERED (MyInt)
)

CREATE NONCLUSTERED INDEX [IX_TestEPFilter_MyString] ON dbo.TestEPFilter (MyString)

-- Table-level EPs: one internal, one user
EXEC sys.sp_addextendedproperty 'ProductName', 'TestProduct', 'SCHEMA', [dbo], 'TABLE', [TestEPFilter], NULL, NULL;
EXEC sys.sp_addextendedproperty 'MS_Description', 'A test table', 'SCHEMA', [dbo], 'TABLE', [TestEPFilter], NULL, NULL;

-- Column-level EPs: one internal, one user
EXEC sys.sp_addextendedproperty 'ProductName', 'TestProduct', 'SCHEMA', [dbo], 'TABLE', [TestEPFilter], 'COLUMN', [MyInt];
EXEC sys.sp_addextendedproperty 'MS_Description', 'An integer column', 'SCHEMA', [dbo], 'TABLE', [TestEPFilter], 'COLUMN', [MyInt];

-- Index-level EP: one internal
EXEC sys.sp_addextendedproperty 'ProductName', 'TestProduct', 'SCHEMA', [dbo], 'TABLE', [TestEPFilter], 'INDEX', [IX_TestEPFilter_MyString];
EXEC sys.sp_addextendedproperty 'MS_Description', 'An index', 'SCHEMA', [dbo], 'TABLE', [TestEPFilter], 'INDEX', [IX_TestEPFilter_MyString];
";
        cmd.ExecuteNonQuery();

        var json = GenerateTableJson(cmd, "dbo", "TestEPFilter");
        var result = GenerateTable(cmd, "dbo", "TestEPFilter");

        Assert.Multiple(() =>
        {
            // ProductName should be filtered from all EP locations
            Assert.That(json, Does.Not.Contain("ProductName"));

            // User EPs should still be present
            Assert.That(result.Extensions?["ExtendedProperties"]?["MS_Description"]?.ToString(), Is.EqualTo("A test table"));
            Assert.That(result.Columns[0].Extensions?["ExtendedProperties"]?["MS_Description"]?.ToString(), Is.EqualTo("An integer column"));
            Assert.That(result.Indexes[0].Extensions?["ExtendedProperties"]?["MS_Description"]?.ToString(), Is.EqualTo("An index"));
        });

        conn.Close();
    }

    [Test]
    public void ShouldPreserveNonDefaultSchemaThroughTongsRoundTrip()
    {
        // Bug A — round-trip regression: SchemaTongs's extraction at
        // SchemaTongs.cs:2266 deserializes the JSON output of GenerateTableJSON
        // with `JsonConvert.DeserializeObject<Table>(json)` — the BASE Table type
        // which has no Schema property. The Schema field emitted by
        // GenerateTableJSON is silently dropped, the file gets written without it,
        // and downstream JsonHelper.TableLoad falls back to SqlServerTable.Schema's
        // class default of "dbo". On the demo Northwind walkthrough this round-trips
        // recyclebin.Registry into a duplicate dbo.Registry on quench.
        // The fix is to deserialize with PlatformDeserializer (which returns the
        // SqlServerTable subclass) before re-serializing. This test simulates that
        // exact extraction path.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE [Test].[NonDefaultSchemaTable] (
    Id INT NOT NULL,
    CONSTRAINT [PK_NonDefaultSchemaTable] PRIMARY KEY CLUSTERED (Id)
)
";
        cmd.ExecuteNonQuery();

        var json = GenerateTableJson(cmd, "Test", "NonDefaultSchemaTable");

        // Mirror SchemaTongs.cs:2266 — production extraction path's
        // deserialize-then-serialize round trip. The fix swaps in
        // PlatformDeserializer.DeserializeTable so the platform subclass
        // materializes and Schema survives.
        var tableObj = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
        var roundTrippedJson = JsonHelper.Serialize(tableObj);

        Assert.That(roundTrippedJson, Does.Contain("\"Schema\""), "After SchemaTongs's deserialize+serialize round-trip, the Schema property must survive — otherwise non-default-schema tables get round-tripped into dbo.");
        conn.Close();
    }

    [Test]
    public void ShouldEmitPreventDropOnlyForProtectedTables()
    {
        // #270 round-trip: a table protected in the source DB (sticky PreventDrop marker set) must extract with
        // "PreventDrop": true so an extract -> re-deploy preserves protection; an unprotected sibling omits the key.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.ProtectedExtractTable (Id INT NOT NULL PRIMARY KEY)
CREATE TABLE dbo.UnprotectedExtractTable (Id INT NOT NULL PRIMARY KEY)

-- Stamp the sticky PreventDrop marker exactly as ModifiedTableQuench does for a protected table.
EXEC sys.sp_addextendedproperty 'PreventDrop', 'true', 'SCHEMA', [dbo], 'TABLE', [ProtectedExtractTable], NULL, NULL;
";
        cmd.ExecuteNonQuery();

        var protectedJson = GenerateTableJson(cmd, "dbo", "ProtectedExtractTable");
        var protectedTable = (SqlServerTable)PlatformDeserializer.DeserializeTable(protectedJson, Platform.SqlServer);
        var unprotectedJson = GenerateTableJson(cmd, "dbo", "UnprotectedExtractTable");
        var unprotectedTable = (SqlServerTable)PlatformDeserializer.DeserializeTable(unprotectedJson, Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(protectedTable.PreventDrop, Is.True, "Protected table must round-trip PreventDrop:true.");
            Assert.That(protectedJson, Does.Contain("PreventDrop"), "Extracted JSON for a protected table must carry the PreventDrop marker.");
            Assert.That(unprotectedTable.PreventDrop, Is.False, "Unprotected table must deserialize to PreventDrop:false.");
            Assert.That(unprotectedJson, Does.Not.Contain("PreventDrop"), "Extracted JSON for an unprotected table must omit the PreventDrop key.");
            // The internal marker must not leak into the generic ExtendedProperties extraction.
            Assert.That(protectedTable.Extensions?["ExtendedProperties"]?["PreventDrop"], Is.Null, "PreventDrop must stay out of generic Extensions.");
        });

        conn.Close();
    }

    [Test]
    public void ShouldRoundTripAuthoredDataDeliveryAcrossReExtraction()
    {
        // DataDelivery is authored config, not catalog metadata -- GenerateTableJson never emits it
        // (the vestigial table-level ContentFile/MergeType this proc used to emit instead were the
        // strict-deserialization bug; they are now gone). Re-extraction must still deserialize the raw
        // proc output cleanly, then let ImportTableHelper carry a previously-authored DataDelivery block
        // forward onto the freshly-extracted table.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE dbo.DeliveryRoundTripTable (Id INT NOT NULL PRIMARY KEY)";
        cmd.ExecuteNonQuery();

        var extracted = GenerateTable(cmd, "dbo", "DeliveryRoundTripTable");
        Assert.That(extracted.DataDelivery, Is.Empty, "Raw extraction carries no DataDelivery -- it is authored config, not catalog metadata.");

        var original = new SqlServerTable
        {
            Name = "DeliveryRoundTripTable",
            DataDelivery = [new DataDelivery { ContentFile = "DeliveryRoundTripTable.tabledata", MergeType = "Insert/Update" }]
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(extracted, original, _ => true);

        Assert.Multiple(() =>
        {
            Assert.That(extracted.DataDelivery, Has.Count.EqualTo(1));
            Assert.That(extracted.DataDelivery[0].ContentFile, Is.EqualTo("DeliveryRoundTripTable.tabledata"));
            Assert.That(extracted.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
        });

        conn.Close();
    }

    [Test]
    public void ShouldRoundTripSystemVersionedTemporalTable()
    {
        // #369: SchemaTongs extraction previously emitted no IsTemporal for a system-versioned (temporal)
        // table, so an extract -> re-deploy round-trip silently dropped system-versioning. Extraction must
        // (a) emit IsTemporal:true and (b) EXCLUDE the period columns (ValidFrom/ValidTo, GENERATED ALWAYS
        // AS ROW START/END) — SchemaSmith regenerates those from IsTemporal by convention on apply (see
        // TableQuench_TemporalTables, whose IsTemporal:true JSON declares no period columns yet produces
        // them), so emitting them as user columns would double-declare them on re-deploy.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.MyTemporalExtract (
    Id INT NOT NULL,
    Somedata VARCHAR(500) NOT NULL,
    ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
    CONSTRAINT [PK_MyTemporalExtract] PRIMARY KEY NONCLUSTERED (Id),
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.MyTemporalExtract_Hist))
";
        cmd.ExecuteNonQuery();

        var json = GenerateTableJson(cmd, "dbo", "MyTemporalExtract");
        var result = GenerateTable(cmd, "dbo", "MyTemporalExtract");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsTemporal, Is.True, "a system-versioned table must extract with IsTemporal:true so the round-trip preserves system-versioning");
            Assert.That(result.Columns, Has.Count.EqualTo(2), "only the user columns (Id, Somedata) remain — the period columns are SchemaSmith-generated from IsTemporal, not user columns");
            Assert.That(json, Does.Not.Contain("ValidFrom"), "the ROW START period column must not be extracted as a user column");
            Assert.That(json, Does.Not.Contain("ValidTo"), "the ROW END period column must not be extracted as a user column");
        });

        conn.Close();
    }

    [Test]
    public void ShouldRoundTripNonDefaultHistoryTableNameAndRetentionPeriod()
    {
        // #depth-gap: extraction previously emitted only IsTemporal, so a temporal table with a
        // non-default history table (name/schema) or an explicit retention policy silently lost both on
        // an extract -> re-deploy round trip -- a retention policy disappearing is compliance-shaped loss.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE SCHEMA history
CREATE TABLE dbo.MyCustomHistTemporal (
    Id INT NOT NULL,
    Somedata VARCHAR(500) NOT NULL,
    ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
    CONSTRAINT [PK_MyCustomHistTemporal] PRIMARY KEY NONCLUSTERED (Id),
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = history.MyCustomHistTemporal_Archive, HISTORY_RETENTION_PERIOD = 5 YEARS))
";
        cmd.ExecuteNonQuery();

        var result = GenerateTable(cmd, "dbo", "MyCustomHistTemporal");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsTemporal, Is.True);
            Assert.That(result.HistoryTableSchema, Is.EqualTo("[history]"), "a non-default history schema must round-trip");
            Assert.That(result.HistoryTableName, Is.EqualTo("[MyCustomHistTemporal_Archive]"), "a non-default history table name must round-trip");
            Assert.That(result.HistoryRetentionPeriod, Is.EqualTo("5 YEARS"), "an explicit retention period must round-trip");
        });

        conn.Close();
    }

    [Test]
    public void ShouldOmitHistoryTableNameAndRetentionWhenDefault()
    {
        // Default-named history table + no explicit retention (SQL Server's own INFINITE default) must
        // stay minimal, matching how IsTemporal/PreventDrop/etc. only emit when non-default -- otherwise
        // every existing temporal table's extracted JSON would grow two new properties for nothing.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.MyDefaultHistTemporal (
    Id INT NOT NULL,
    Somedata VARCHAR(500) NOT NULL,
    ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
    CONSTRAINT [PK_MyDefaultHistTemporal] PRIMARY KEY NONCLUSTERED (Id),
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.MyDefaultHistTemporal_Hist))
";
        cmd.ExecuteNonQuery();

        var json = GenerateTableJson(cmd, "dbo", "MyDefaultHistTemporal");
        var result = GenerateTable(cmd, "dbo", "MyDefaultHistTemporal");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsTemporal, Is.True);
            Assert.That(result.HistoryTableSchema, Is.Null);
            Assert.That(result.HistoryTableName, Is.Null);
            Assert.That(result.HistoryRetentionPeriod, Is.Null);
            Assert.That(json, Does.Not.Contain("HistoryTableSchema"));
            Assert.That(json, Does.Not.Contain("HistoryTableName"));
            Assert.That(json, Does.Not.Contain("HistoryRetentionPeriod"));
        });

        conn.Close();
    }

    [Test]
    public void ShouldRoundTripFileGroupOnTableAndIndex()
    {
        // #filegroups: a table can sit on a different filegroup from its own indexes (the normal reason
        // to use them at all), so this exercises the table-level and both index-level (default vs.
        // non-default) FileGroup extraction independently. Indexes extract ORDER BY [Name], so
        // "IX_..." sorts before "PK_..." -- Indexes[0]/[1] below are deterministic, matching this file's
        // established convention (see ShouldFlagMixedCompressionAcrossPartitionsRatherThanPickOne).
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.MyFileGroupTable (
    Id INT NOT NULL,
    Somedata VARCHAR(50) NULL,
    CONSTRAINT [PK_MyFileGroupTable] PRIMARY KEY NONCLUSTERED (Id) ON [PRIMARY]
) ON [FG_Test]

CREATE NONCLUSTERED INDEX [IX_MyFileGroupTable_Somedata] ON dbo.MyFileGroupTable (Somedata) ON [FG_Test]
";
        cmd.ExecuteNonQuery();

        var json = GenerateTableJson(cmd, "dbo", "MyFileGroupTable");
        var result = GenerateTable(cmd, "dbo", "MyFileGroupTable");

        Assert.Multiple(() =>
        {
            Assert.That(result.FileGroup, Is.EqualTo("[FG_Test]"), "the table's own (heap) data lives on the non-default filegroup");
            Assert.That(result.Indexes, Has.Count.EqualTo(2));
            Assert.That(((SqlServerIndex)result.Indexes[0]).FileGroup, Is.EqualTo("[FG_Test]"), "IX_MyFileGroupTable_Somedata was explicitly placed on the non-default filegroup");
            Assert.That(((SqlServerIndex)result.Indexes[1]).FileGroup, Is.Null, "PK_MyFileGroupTable was explicitly placed on PRIMARY (the default) -- must stay minimal, not emit '[PRIMARY]'");
        });
        Assert.That(json, Does.Not.Contain("PRIMARY"), "the default-filegroup PK index must not carry a FileGroup value anywhere in the extracted JSON");

        conn.Close();
    }

    // #417: CdcFilegroup round-trips, and only when it says something -- a change table on the default
    // filegroup extracts no key, so existing CDC packages re-extract unchanged.
    [Test]
    public void ShouldExtractCdcFilegroup_OnlyWhenTheChangeTableIsOffTheDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1) EXEC sys.sp_cdc_enable_db;
CREATE TABLE dbo.CdcOnFgTest (Id INT NOT NULL PRIMARY KEY, Val INT NULL);
CREATE TABLE dbo.CdcOnDefaultTest (Id INT NOT NULL PRIMARY KEY, Val INT NULL);
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcOnFgTest', @role_name = NULL, @filegroup_name = N'FG_Test';
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'CdcOnDefaultTest', @role_name = NULL;
";
        cmd.ExecuteNonQuery();

        var onFg = GenerateTable(cmd, "dbo", "CdcOnFgTest");
        var onDefaultJson = GenerateTableJson(cmd, "dbo", "CdcOnDefaultTest");

        Assert.Multiple(() =>
        {
            Assert.That(onFg.EnableCDC, Is.True);
            Assert.That(onFg.CdcFilegroup, Is.EqualTo("[FG_Test]"));
            Assert.That(onDefaultJson, Does.Contain("EnableCDC"));
            Assert.That(onDefaultJson, Does.Not.Contain("CdcFilegroup"), "a change table on the default filegroup must not gain a key");
        });

        conn.Close();
    }

    [Test]
    public void ShouldOmitFileGroupWhenDefault()
    {
        // Backward-compat guard: an ordinary table/index with no explicit filegroup placement (every
        // existing package) must extract exactly as before this feature -- no FileGroup key anywhere.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE dbo.MyOrdinaryPlacementTable (
    Id INT NOT NULL,
    Somedata VARCHAR(50) NULL,
    CONSTRAINT [PK_MyOrdinaryPlacementTable] PRIMARY KEY CLUSTERED (Id)
)
";
        cmd.ExecuteNonQuery();

        var json = GenerateTableJson(cmd, "dbo", "MyOrdinaryPlacementTable");
        var result = GenerateTable(cmd, "dbo", "MyOrdinaryPlacementTable");

        Assert.Multiple(() =>
        {
            Assert.That(result.FileGroup, Is.Null);
            Assert.That(((SqlServerIndex)result.Indexes[0]).FileGroup, Is.Null);
            Assert.That(json, Does.Not.Contain("\"FileGroup\""));
        });

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForPartitionedTable()
    {
        // Repro for the extraction-abort defect (task C1-0b): sys.partitions is one row PER PARTITION,
        // so the prior scalar CompressionType subquery raised Msg 512 ("Subquery returned more than
        // one value") the moment a table had more than one partition -- independent of what compression
        // was actually set. Four partitions (matching the field repro) with the default uniform NONE
        // compression must extract cleanly and round-trip a single shared value, not throw.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE PARTITION FUNCTION PF_TestPartitioned (INT) AS RANGE LEFT FOR VALUES (100, 200, 300)
CREATE PARTITION SCHEME PS_TestPartitioned AS PARTITION PF_TestPartitioned ALL TO ([PRIMARY])

CREATE TABLE dbo.TestPartitioned (
    Id INT NOT NULL,
    Val VARCHAR(50) NULL,
    CONSTRAINT PK_TestPartitioned PRIMARY KEY CLUSTERED (Id) ON PS_TestPartitioned(Id)
) ON PS_TestPartitioned(Id)
";
        cmd.ExecuteNonQuery();

        var result = GenerateTable(cmd, "dbo", "TestPartitioned");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.CompressionType, Is.EqualTo("NONE"), "a partitioned table with uniform per-partition compression must round-trip the shared value, not throw or emit MIXED");
        Assert.That(result.Indexes, Has.Count.EqualTo(1));
        Assert.That(((SqlServerIndex)result.Indexes[0]).CompressionType, Is.EqualTo("NONE"));

        conn.Close();
    }

    [Test]
    public void ShouldFlagMixedCompressionAcrossPartitionsRatherThanPickOne()
    {
        // Compression can legitimately differ per partition. Extraction must not silently report one
        // partition's value as if it applied to the whole table/index -- that would mislead a reader of
        // the extracted JSON into thinking a mixed table is uniformly compressed. 'MIXED' is a sentinel
        // deliberately outside ModifiedTableQuench.sql's managed NONE/ROW/PAGE/COLUMNSTORE* set, so
        // re-deploy leaves an already-mixed table alone instead of flattening it to one compression.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE PARTITION FUNCTION PF_TestPartitionedMixed (INT) AS RANGE LEFT FOR VALUES (100, 200, 300)
CREATE PARTITION SCHEME PS_TestPartitionedMixed AS PARTITION PF_TestPartitionedMixed ALL TO ([PRIMARY])

CREATE TABLE dbo.TestPartitionedMixed (
    Id INT NOT NULL,
    Val VARCHAR(50) NULL,
    CONSTRAINT PK_TestPartitionedMixed PRIMARY KEY CLUSTERED (Id) ON PS_TestPartitionedMixed(Id)
) ON PS_TestPartitionedMixed(Id)

ALTER TABLE dbo.TestPartitionedMixed REBUILD PARTITION = 2 WITH (DATA_COMPRESSION = PAGE)
";
        cmd.ExecuteNonQuery();

        var result = GenerateTable(cmd, "dbo", "TestPartitionedMixed");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.CompressionType, Is.EqualTo("MIXED"));
        Assert.That(result.Indexes, Has.Count.EqualTo(1));
        Assert.That(((SqlServerIndex)result.Indexes[0]).CompressionType, Is.EqualTo("MIXED"));

        conn.Close();
    }

    private string GenerateTableJson(IDbCommand cmd, string schema, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJson @p_Schema = '{schema}', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();

        var tableJson = string.Empty;
        while (reader.Read())
        {
            tableJson += $"{reader.GetString(0)}\r\n";
        }

        return tableJson;
    }

    private SqlServerTable GenerateTable(IDbCommand cmd, string schema, string table)
    {
        return (SqlServerTable)PlatformDeserializer.DeserializeTable(GenerateTableJson(cmd, schema, table), Platform.SqlServer);
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE [{_integrationDb}];
";
        cmd.ExecuteNonQuery();

        // A non-default filegroup for the #filegroups extraction round-trip tests below. Reuses the
        // new database's own data-file directory rather than assuming one, so this works regardless of
        // where the SQL Server instance's default data path is.
        cmd.CommandText = @$"
DECLARE @v_DataPath NVARCHAR(500) = (SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(physical_name)) + 1) FROM sys.master_files WHERE database_id = DB_ID('{_integrationDb}') AND file_id = 1);
ALTER DATABASE [{_integrationDb}] ADD FILEGROUP [FG_Test];
EXEC('ALTER DATABASE [{_integrationDb}] ADD FILE (NAME = ''FG_Test_1'', FILENAME = ''' + @v_DataPath + 'FG_Test_1.ndf'') TO FILEGROUP [FG_Test]');
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        cmd.CommandText = @"
CREATE FULLTEXT CATALOG [FT_Catalog]
CREATE FULLTEXT STOPLIST [SL_Test];
ALTER FULLTEXT STOPLIST [SL_Test] ADD '$' LANGUAGE 'Neutral';

EXEC('CREATE SCHEMA [Test]')
CREATE TYPE [Test].[Flag] FROM BIT NOT NULL
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE XML SCHEMA COLLECTION ManuInstructionsSchemaCollection AS
N'<?xml version=""1.0"" encoding=""UTF-16""?>
<xsd:schema targetNamespace=""https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions""
   xmlns          =""https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions""
   elementFormDefault=""qualified""
   attributeFormDefault=""unqualified""
   xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" >

    <xsd:complexType name=""StepType"" mixed=""true"" >
        <xsd:choice  minOccurs=""0"" maxOccurs=""unbounded"" >
            <xsd:element name=""tool"" type=""xsd:string"" />
            <xsd:element name=""material"" type=""xsd:string"" />
            <xsd:element name=""blueprint"" type=""xsd:string"" />
            <xsd:element name=""specs"" type=""xsd:string"" />
            <xsd:element name=""diag"" type=""xsd:string"" />
        </xsd:choice>
    </xsd:complexType>

    <xsd:element  name=""root"">
        <xsd:complexType mixed=""true"">
            <xsd:sequence>
                <xsd:element name=""Location"" minOccurs=""1"" maxOccurs=""unbounded"">
                    <xsd:complexType mixed=""true"">
                        <xsd:sequence>
                            <xsd:element name=""step"" type=""StepType"" minOccurs=""1"" maxOccurs=""unbounded"" />
                        </xsd:sequence>
                        <xsd:attribute name=""LocationID"" type=""xsd:integer"" use=""required""/>
                        <xsd:attribute name=""SetupHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""MachineHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""LaborHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""LotSize"" type=""xsd:decimal"" use=""optional""/>
                    </xsd:complexType>
                </xsd:element>
            </xsd:sequence>
        </xsd:complexType>
    </xsd:element>
</xsd:schema>';
";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace(" - ", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = @$"
IF DB_ID('{dbName}') IS NOT NULL
  ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{dbName}];
";
        cmd.ExecuteNonQuery();
    }
}
