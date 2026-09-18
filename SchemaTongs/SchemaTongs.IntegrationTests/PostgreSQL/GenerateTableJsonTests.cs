// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System;
using System.Linq;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Utility;
using Newtonsoft.Json;
using Index = Schema.Domain.Index;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
public class GenerateTableJsonTests
{
    private string _integrationDb = "";
    private string _connectionString;
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("GenerateTableJson");

        CreateTestDatabases();

        _testConnectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], _integrationDb, config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
    }

    [Test]
    public void ShouldGenerateCorrectJsonForForeignKeys()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE public.""MyFKTable"" (""Id"" INT NOT NULL PRIMARY KEY, ""Col2"" INT, ""Col3"" INT);
CREATE TABLE public.""MyFKReferencedTable"" (""Id"" INT NOT NULL PRIMARY KEY, ""RefCol"" INT NOT NULL);
CREATE UNIQUE INDEX ""IDX_RefKey"" ON public.""MyFKReferencedTable"" (""RefCol"");
ALTER TABLE public.""MyFKTable"" ADD CONSTRAINT ""FK_MyFKTable_Col3_MyFKReferencedTable_Id"" FOREIGN KEY (""Col3"") REFERENCES public.""MyFKReferencedTable"" (""Id"") ON DELETE CASCADE;
ALTER TABLE public.""MyFKTable"" ADD CONSTRAINT ""FK_MyFKTable_Col2_MyFKReferencedTable_RefCol"" FOREIGN KEY (""Col2"") REFERENCES public.""MyFKReferencedTable"" (""RefCol"") ON UPDATE CASCADE;
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "public", "MyFKTable");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("public"));
        Assert.That(result.Name, Is.EqualTo("MyFKTable"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.ForeignKeys, Is.Not.Null);
        Assert.That(result.ForeignKeys, Has.Count.EqualTo(2));

        var fk0 = (PostgreSqlForeignKey)result.ForeignKeys[0];
        Assert.That(fk0.Name, Is.EqualTo("FK_MyFKTable_Col2_MyFKReferencedTable_RefCol"));
        Assert.That(fk0.Columns, Is.EqualTo("Col2"));
        Assert.That(fk0.RelatedTableSchema, Is.EqualTo("public"));
        Assert.That(fk0.RelatedTable, Is.EqualTo("MyFKReferencedTable"));
        Assert.That(fk0.RelatedColumns, Is.EqualTo("RefCol"));
        Assert.That(fk0.DeleteAction, Is.EqualTo(""));
        Assert.That(fk0.UpdateAction, Is.EqualTo("CASCADE"));
        var fk1 = (PostgreSqlForeignKey)result.ForeignKeys[1];
        Assert.That(fk1.Name, Is.EqualTo("FK_MyFKTable_Col3_MyFKReferencedTable_Id"));
        Assert.That(fk1.Columns, Is.EqualTo("Col3"));
        Assert.That(fk1.RelatedTableSchema, Is.EqualTo("public"));
        Assert.That(fk1.RelatedTable, Is.EqualTo("MyFKReferencedTable"));
        Assert.That(fk1.RelatedColumns, Is.EqualTo("Id"));
        Assert.That(fk1.DeleteAction, Is.EqualTo("CASCADE"));
        Assert.That(fk1.UpdateAction, Is.EqualTo(""));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE public.""TestChecks"" (
    ""MyInt"" INT NOT NULL,
    ""MyBigInt"" BIGINT NOT NULL,
    ""MyString"" VARCHAR(100) NULL,
    CONSTRAINT ""CK_TestChecks_MyInt"" CHECK (""MyInt"" < ""MyBigInt""),
    CONSTRAINT ""CK_TestChecks_MyBigInt"" CHECK (""MyBigInt"" > ""MyInt"")
);
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "public", "TestChecks");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("public"));
        Assert.That(result.Name, Is.EqualTo("TestChecks"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.CheckConstraints, Is.Not.Null);
        Assert.That(result.CheckConstraints, Has.Count.EqualTo(2));

        Assert.That(result.CheckConstraints[0].Name, Is.EqualTo("CK_TestChecks_MyBigInt"));
        Assert.That(result.CheckConstraints[0].Expression, Is.EqualTo("\"MyBigInt\" > \"MyInt\""));
        Assert.That(result.CheckConstraints[1].Name, Is.EqualTo("CK_TestChecks_MyInt"));
        Assert.That(result.CheckConstraints[1].Expression, Is.EqualTo("\"MyInt\" < \"MyBigInt\""));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForStatistics()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 14) Assert.Ignore("Expression statistics (CREATE STATISTICS on an expression) require PostgreSQL 14+.");
        cmd.CommandText = @"
CREATE TABLE public.""TestStatistics"" (
    ""MyInt"" INT4 NOT NULL,
    ""MyBigInt"" INT8 NOT NULL,
    ""MyString"" VARCHAR(100) NULL
);

CREATE STATISTICS ""ST_TestStatistics_MyInt"" ON ""MyInt"", ""MyBigInt"" FROM public.""TestStatistics"";
CREATE STATISTICS ""ST_TestStatistics_MyBigInt_ForNullStrings"" ON (""MyBigInt"" / 1) FROM public.""TestStatistics"";
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "public", "TestStatistics");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("public"));
        Assert.That(result.Name, Is.EqualTo("TestStatistics"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.Statistics, Is.Not.Null);
        Assert.That(result.Statistics, Has.Count.EqualTo(2));

        AssertStatisticProperties(result.Statistics[0], "ST_TestStatistics_MyBigInt_ForNullStrings", @"(""MyBigInt"" / 1)");
        AssertStatisticProperties(result.Statistics[1], "ST_TestStatistics_MyInt", "MyInt,MyBigInt");
        // The catalog marks any statistic with an expression with an 'e' kind. EXPRESSIONS is not a kind CREATE
        // STATISTICS accepts, so extracting it produced a package that could not be deployed.
        Assert.That(result.Statistics[0].Kind ?? "", Does.Not.Contain("EXPRESSIONS"), "a single-expression statistic has no declarable kind");
        Assert.That(result.Statistics[1].Kind ?? "", Does.Not.Contain("EXPRESSIONS"));

        conn.Close();
    }

    private void AssertStatisticProperties(Schema.Domain.PostgreSQL.Statistic statistic, string name, string columns)
    {
        Assert.That(statistic.Name, Is.EqualTo(name), $"Name of {name}");
        Assert.That(statistic.StatisticsColumns, Is.EqualTo(columns), $"Columns of {name}");
    }

    [Test]
    public void ShouldGenerateCorrectJsonForIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE public.""TestIndexes"" (
    ""MyInt"" INT NOT NULL,
    ""MyBigInt"" BIGINT NOT NULL,
    ""MyString"" VARCHAR(100) NULL
);

ALTER TABLE public.""TestIndexes""
    ADD CONSTRAINT ""PK_TestIndexes"" PRIMARY KEY (""MyInt"") WITH (FILLFACTOR = 80),
    ADD CONSTRAINT ""UQ_TestIndexes_MyString"" UNIQUE (""MyString"");

CREATE UNIQUE INDEX ""UX_TestIndexes_MyInt"" ON public.""TestIndexes"" (""MyInt"") INCLUDE(""MyString"", ""MyBigInt"") WITH (FILLFACTOR = 100);
CREATE INDEX ""IX_TestIndexes_MyString"" ON public.""TestIndexes"" (""MyString"", ""MyBigInt"") WITH (FILLFACTOR = 90) WHERE ""MyString"" IS NOT NULL;
CREATE INDEX ""CX_TestIndexes_MyBigInt"" ON public.""TestIndexes"" (""MyBigInt"") WITH (FILLFACTOR = 100);
ALTER TABLE public.""TestIndexes"" CLUSTER ON ""CX_TestIndexes_MyBigInt""
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "public", "TestIndexes");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("public"));
        Assert.That(result.Name, Is.EqualTo("TestIndexes"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.Indexes, Is.Not.Null);
        Assert.That(result.Indexes, Has.Count.EqualTo(5));

        AssertIndexProperties(result.Indexes[0], "CX_TestIndexes_MyBigInt", true, false, false, false, "MyBigInt", null, 100, null);
        AssertIndexProperties(result.Indexes[1], "IX_TestIndexes_MyString", false, false, false, false, "MyString,MyBigInt", null, 90, "\"MyString\" IS NOT NULL");
        AssertIndexProperties(result.Indexes[2], "PK_TestIndexes", false, true, true, false, "MyInt", null, 80, null);
        AssertIndexProperties(result.Indexes[3], "UQ_TestIndexes_MyString", false, true, false, true, "MyString", null, 90, null);
        AssertIndexProperties(result.Indexes[4], "UX_TestIndexes_MyInt", false, true, false, false, "MyInt", "MyString,MyBigInt", 100, null);

        conn.Close();
    }

    private void AssertIndexProperties(Index index, string name, bool isCustered, bool isUnique, bool isPrimaryKey, bool isUniqueConstraint, string columns, string includeColumns, int fillFactor, string filterExpression)
    {
        var pgIndex = (PostgreSqlIndex)index;
        Assert.That(pgIndex.Name, Is.EqualTo($"{name}"), $"Name of {name}");
        Assert.That(pgIndex.PrimaryKey, Is.EqualTo(isPrimaryKey), $"PrimaryKey of {name}");
        Assert.That(pgIndex.Unique, Is.EqualTo(isUnique), $"Unique of {name}");
        Assert.That(pgIndex.Clustered, Is.EqualTo(isCustered), $"Clustered of {name}");
        Assert.That(pgIndex.UniqueConstraint, Is.EqualTo(isUniqueConstraint), $"UniqueConstraint of {name}");
        Assert.That(pgIndex.IndexColumns, Is.EqualTo(columns), $"IndexColumns of {name}");
        Assert.That(pgIndex.IncludeColumns, Is.EqualTo(includeColumns), $"IncludeColumns of {name}");
        Assert.That(pgIndex.FillFactor, Is.EqualTo(fillFactor), $"FillFactor of {name}");
        Assert.That(pgIndex.FilterExpression, Is.EqualTo(filterExpression), $"FilterExpression of {name}");
    }

    [Test]
    public void ShouldGenerateCorrectJsonForColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE public.""TestColumns"" (
    ""MyBit"" BIT NOT NULL,
    ""MyBool"" BOOL NOT NULL,
    ""MyInt"" INT NULL,
    ""MyDecimal"" DECIMAL(10, 2) NOT NULL,
    ""MyNumeric"" NUMERIC(5, 3) NULL,
    ""MyString"" VARCHAR(200) NULL,
    ""MyTimeStamp"" TIMESTAMP NULL,
    ""MyTimeStamp2"" TIMESTAMP(4) NULL,
    ""MyMaxVarchar"" VARCHAR NULL,
    ""MyMaxBYTEA"" BYTEA NULL,
    ""MyFloat"" FLOAT NULL,
    ""MyMoney"" MONEY NULL,
    ""MySmallint"" SMALLINT NULL,
    ""MyBigint"" BIGINT NULL,
    ""MyUniqueIdentifier"" UUID NULL,
    ""MyDate"" DATE NULL,
    ""MyTime"" TIME NULL,
    ""MyBitWithDefault"" BIT NOT NULL DEFAULT 1::bit,
    ""MyIntWithDefault"" INT NOT NULL DEFAULT 42,
    ""MyDecimalWithDefault"" DECIMAL(12, 4) NOT NULL DEFAULT 3.14,
    ""MyFlag"" ""Test"".""Flag"",
    ""MyIdentity"" INT GENERATED ALWAYS AS IDENTITY(START WITH 13 INCREMENT BY 2) NOT NULL
);
";
        cmd.ExecuteNonQuery();

        var result = GenerateTable(cmd, "public", "TestColumns");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Schema, Is.EqualTo("public"));
        Assert.That(result.Name, Is.EqualTo("TestColumns"));
        Assert.That(result.AccessMethod, Is.EqualTo("heap"));
        Assert.That(result.RowLevelSecurity, Is.False);
        Assert.That(result.ForceRowLevelSecurity, Is.False);
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(22));
        AssertColumnProperties(result.Columns[0], "MyBigint", "int8", true, null);
        AssertColumnProperties(result.Columns[1], "MyBit", "bit", false, null);
        AssertColumnProperties(result.Columns[2], "MyBitWithDefault", "bit", false, "(1)::bit(1)");
        AssertColumnProperties(result.Columns[3], "MyBool", "bool", false, null);
        AssertColumnProperties(result.Columns[4], "MyDate", "date", true, null);
        AssertColumnProperties(result.Columns[5], "MyDecimal", "numeric(10, 2)", false, null);
        AssertColumnProperties(result.Columns[6], "MyDecimalWithDefault", "numeric(12, 4)", false, "3.14");
        AssertColumnProperties(result.Columns[7], "MyFlag", @"""Test"".""Flag""", false, null);
        AssertColumnProperties(result.Columns[8], "MyFloat", "float8", true, null);
        AssertColumnProperties(result.Columns[9], "MyIdentity", "int4", false, null);
        Assert.That(((PostgreSqlColumn)result.Columns[9]).Generated.ToUpper(), Is.EqualTo("GENERATED ALWAYS AS IDENTITY(START WITH 13 INCREMENT BY 2)"));
        AssertColumnProperties(result.Columns[10], "MyInt", "int4", true, null);
        AssertColumnProperties(result.Columns[11], "MyIntWithDefault", "int4", false, "42");
        AssertColumnProperties(result.Columns[12], "MyMaxBYTEA", "bytea", true, null);
        AssertColumnProperties(result.Columns[13], "MyMaxVarchar", "varchar", true, null);
        AssertColumnProperties(result.Columns[14], "MyMoney", "money", true, null);
        AssertColumnProperties(result.Columns[15], "MyNumeric", "numeric(5, 3)", true, null);
        AssertColumnProperties(result.Columns[16], "MySmallint", "int2", true, null);
        AssertColumnProperties(result.Columns[17], "MyString", "varchar(200)", true, null);
        AssertColumnProperties(result.Columns[18], "MyTime", "time", true, null);
        AssertColumnProperties(result.Columns[19], "MyTimeStamp", "timestamp", true, null);
        AssertColumnProperties(result.Columns[20], "MyTimeStamp2", "timestamp(4)", true, null);
        AssertColumnProperties(result.Columns[21], "MyUniqueIdentifier", "uuid", true, null);

        Assert.That(result.CheckConstraints, Is.Not.Null);
        Assert.That(result.CheckConstraints, Has.Count.EqualTo(0));
        Assert.That(result.ForeignKeys, Is.Not.Null);
        Assert.That(result.ForeignKeys, Has.Count.EqualTo(0));
        Assert.That(result.Indexes, Is.Not.Null);
        Assert.That(result.Indexes, Has.Count.EqualTo(0));
        Assert.That(result.Statistics, Is.Not.Null);
        Assert.That(result.Statistics, Has.Count.EqualTo(0));
        Assert.That(result.ExcludeConstraints, Is.Not.Null);
        Assert.That(result.ExcludeConstraints, Has.Count.EqualTo(0));
        Assert.That(result.UpdateFillFactor, Is.False);

        conn.Close();
    }

    private void AssertColumnProperties(Column column, string name, string dataType, bool nullable, string defaultValue)
    {
        Assert.That(column.Name, Is.EqualTo($"{name}"), $"Name of {name}");
        Assert.That(column.DataType, Is.EqualTo(dataType), $"Type of {name}");
        Assert.That(column.Nullable, Is.EqualTo(nullable), $"Nullability of {name}");
        Assert.That(column.Default, Is.EqualTo(defaultValue), $"Default of {name}");
    }

    [Test]
    public void ShouldPreserveFractionalSecondsPrecisionOnExtraction()
    {
        // timestamptz(3)/time(3) precision-loss regression: the extraction CASE special-cased only
        // 'timestamp' among PostgreSQL's fractional-seconds-precision types, so timestamptz(3) and
        // time(3) both fell through to no argument at all — the declared precision was silently
        // dropped. Bare time (default precision 6) is covered by ShouldGenerateCorrectJsonForColumns;
        // this locks in the explicit-precision case for both siblings.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE public.""TestFractionalSecondsPrecision"" (
    ""MyInt"" INT NOT NULL PRIMARY KEY,
    ""MyTimestampTzPrecise"" TIMESTAMPTZ(3) NULL,
    ""MyTimePrecise"" TIME(3) NULL
);
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, "public", "TestFractionalSecondsPrecision");
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        AssertColumnProperties(result.Columns.Single(c => c.Name == "MyTimestampTzPrecise"), "MyTimestampTzPrecise", "timestamptz(3)", true, null);
        AssertColumnProperties(result.Columns.Single(c => c.Name == "MyTimePrecise"), "MyTimePrecise", "time(3)", true, null);

        conn.Close();
    }

    [Test]
    public void ShouldEmitPreventDropOnlyForProtectedTables()
    {
        // #270 round-trip: a table protected in the source DB (sticky PreventDrop marker set) must extract with
        // "PreventDrop": true so an extract -> re-deploy preserves protection; an unprotected sibling omits the key.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE public.""ProtectedExtractTable"" (""Id"" INT NOT NULL PRIMARY KEY);
CREATE TABLE public.""UnprotectedExtractTable"" (""Id"" INT NOT NULL PRIMARY KEY);

-- Establish the sticky PreventDrop marker as the deploy side would (table-level ProductOwnership row).
INSERT INTO ""SchemaSmith"".""ProductOwnership"" (""Schema"", ""TableName"", ""IndexName"", ""ProductName"", template_name, ""PreventDrop"")
VALUES ('public', 'ProtectedExtractTable', NULL, 'TestProduct', '', TRUE);
";
        cmd.ExecuteNonQuery();

        var protectedJson = GenerateTableJson(cmd, "public", "ProtectedExtractTable");
        var protectedTable = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(protectedJson, Platform.PostgreSQL);
        var unprotectedJson = GenerateTableJson(cmd, "public", "UnprotectedExtractTable");
        var unprotectedTable = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(unprotectedJson, Platform.PostgreSQL);

        Assert.Multiple(() =>
        {
            Assert.That(protectedTable.PreventDrop, Is.True, "Protected table must round-trip PreventDrop:true.");
            Assert.That(protectedJson, Does.Contain("PreventDrop"), "Extracted JSON for a protected table must carry the PreventDrop marker.");
            Assert.That(unprotectedTable.PreventDrop, Is.False, "Unprotected table must deserialize to PreventDrop:false.");
            Assert.That(unprotectedJson, Does.Not.Contain("PreventDrop"), "Extracted JSON for an unprotected table must omit the PreventDrop key.");
        });

        conn.Close();
    }

    [Test]
    public void ShouldRoundTripAuthoredDataDeliveryAcrossReExtraction()
    {
        // DataDelivery is authored config, not catalog metadata -- GenerateTableJSON never emits it
        // (the vestigial table-level ContentFile/MergeType/MergeUpdateDescendents this proc used to emit
        // instead were the strict-deserialization bug; they are now gone). Re-extraction must still
        // deserialize the raw proc output cleanly, then let ImportTableHelper carry a previously-authored
        // DataDelivery block forward onto the freshly-extracted table.
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE TABLE public.""DeliveryRoundTripTable"" (""Id"" INT NOT NULL PRIMARY KEY);";
        cmd.ExecuteNonQuery();

        var extracted = GenerateTable(cmd, "public", "DeliveryRoundTripTable");
        Assert.That(extracted.DataDelivery, Is.Empty, "Raw extraction carries no DataDelivery -- it is authored config, not catalog metadata.");

        var original = new PostgreSqlTable
        {
            Name = "DeliveryRoundTripTable",
            DataDelivery = [new DataDelivery { ContentFile = "DeliveryRoundTripTable.tabledata", MergeType = "Insert/Update", MergeUpdateDescendents = true }]
        };

        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(extracted, original, _ => true);

        Assert.Multiple(() =>
        {
            Assert.That(extracted.DataDelivery, Has.Count.EqualTo(1));
            Assert.That(extracted.DataDelivery[0].ContentFile, Is.EqualTo("DeliveryRoundTripTable.tabledata"));
            Assert.That(extracted.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
            Assert.That(extracted.DataDelivery[0].MergeUpdateDescendents, Is.True);
        });

        conn.Close();
    }

    private string GenerateTableJson(IDbCommand cmd, string schema, string table)
    {
        cmd.CommandText = $@"SELECT ""SchemaSmith"".""GenerateTableJSON""('{schema}', '{table}');";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private PostgreSqlTable GenerateTable(IDbCommand cmd, string schema, string table)
    {
        return (PostgreSqlTable)PlatformDeserializer.DeserializeTable(GenerateTableJson(cmd, schema, table), Platform.PostgreSQL);
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE ""{_integrationDb}"";
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        cmd.CommandText = @"
CREATE SCHEMA ""Test"";
CREATE DOMAIN ""Test"".""Flag"" AS BOOLEAN NOT NULL;
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = @$"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @$"DROP DATABASE IF EXISTS ""{dbName}"";";
        cmd.ExecuteNonQuery();
    }
}
