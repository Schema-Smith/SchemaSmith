// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Utility;
using Index = Schema.Domain.Index;

namespace SchemaTongs.IntegrationTests.Shared;

public abstract class GenerateTableJsonSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }

    // Extraction faithfully reports each engine's native metadata form (correct for a single-platform
    // product; the quench comparison normalizes both to stay idempotent). Two forms diverge:
    // MySQL 8.0 drops integer display widths (`int`), MariaDB keeps them (`int(11)`); and the default
    // FK referential action reads `NO ACTION` on MySQL, `RESTRICT` on MariaDB (semantically identical).
    protected virtual string ExpectedIntegerType(string canonical) => canonical;
    protected virtual string ExpectedDefaultFkAction => "NO ACTION";

    // Detected MySQL-family comparable (e.g. 507, 800, 1002) of the extraction source, for version-aware
    // expectations: MySQL 5.7 behaves like MariaDB here (keeps integer display widths, reports FK RESTRICT,
    // and has no CHECK constraints) where MySQL 8.0 does not.
    protected int ServerVersionNum { get; private set; }

    /// <summary>Whether the source enforces + exposes CHECK constraints: MySQL 8.0.16 (major >= 8); MariaDB
    /// (>= 10.2 floor) always.</summary>
    protected bool TargetSupportsCheckConstraints => Platform != Platform.MySQL || ServerVersionNum >= 800;

    protected string _integrationDb = "";
    private string _connectionString;
    protected string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform, config[$"{ConfigPrefix}:Server"], "mysql", config[$"{ConfigPrefix}:User"], config[$"{ConfigPrefix}:Password"], config[$"{ConfigPrefix}:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("GenerateTableJson");

        CreateTestDatabases();

        _testConnectionString = ConnectionString.Build(Platform, config[$"{ConfigPrefix}:Server"], _integrationDb, config[$"{ConfigPrefix}:User"], config[$"{ConfigPrefix}:Password"], config[$"{ConfigPrefix}:Port"], connProps);

        using var vconn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        vconn.Open();
        using var vcmd = vconn.CreateCommand();
        vcmd.CommandText = "SELECT VERSION()";
        var vp = (vcmd.ExecuteScalar()?.ToString() ?? "").Split('.');
        ServerVersionNum = vp.Length >= 2 && int.TryParse(vp[0], out var mj) && int.TryParse(vp[1], out var mn) ? mj * 100 + mn : int.MaxValue;
    }

    [Test]
    public void ShouldGenerateCorrectJsonForColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestColumns` (
    `MyBit` BIT(1) NOT NULL,
    `MyInt` INT NULL,
    `MyDecimal` DECIMAL(10, 2) NOT NULL,
    `MyNumeric` NUMERIC(5, 3) NULL,
    `MyString` VARCHAR(200) NULL,
    `MyDateTime` DATETIME NULL,
    `MyTimestamp` TIMESTAMP NULL,
    `MyText` TEXT NULL,
    `MyBlob` BLOB NULL,
    `MyEnum` ENUM('a','b','c') NULL,
    `MySet` SET('x','y','z') NULL,
    `MyFloat` FLOAT NULL,
    `MySmallint` SMALLINT NULL,
    `MyTinyint` TINYINT NULL,
    `MyBigint` BIGINT NULL,
    `MyDate` DATE NULL,
    `MyTime` TIME NULL,
    `MyBitWithDefault` BIT(1) NOT NULL DEFAULT b'1',
    `MyIntWithDefault` INT NOT NULL DEFAULT 42,
    `MyDecimalWithDefault` DECIMAL(12, 4) NOT NULL DEFAULT 3.14,
    `MyIdentity` INT NOT NULL AUTO_INCREMENT,
    `MyMediumText` MEDIUMTEXT NULL,
    PRIMARY KEY (`MyIdentity`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
";
        cmd.ExecuteNonQuery();

        var result = GenerateTable(cmd, _integrationDb, "TestColumns");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("`TestColumns`"));
        Assert.That(result.Engine, Is.EqualTo("InnoDB"));
        Assert.That(result.CharacterSet, Is.EqualTo("utf8mb4"));
        Assert.That(result.Collation, Is.EqualTo("utf8mb4_unicode_ci"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(22));

        // Columns are ordered by ORDINAL_POSITION (creation order)
        AssertColumnProperties(ColumnNamed(result, "MyBit"), "MyBit", "bit(1)", false, null);
        AssertColumnProperties(ColumnNamed(result, "MyInt"), "MyInt", ExpectedIntegerType("int"), true, null);
        AssertColumnProperties(ColumnNamed(result, "MyDecimal"), "MyDecimal", "decimal(10,2)", false, null);
        AssertColumnProperties(ColumnNamed(result, "MyNumeric"), "MyNumeric", "decimal(5,3)", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyString"), "MyString", "varchar(200)", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyDateTime"), "MyDateTime", "datetime", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyTimestamp"), "MyTimestamp", "timestamp", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyText"), "MyText", "text", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyBlob"), "MyBlob", "blob", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyEnum"), "MyEnum", "enum('a','b','c')", true, null);
        AssertColumnProperties(ColumnNamed(result, "MySet"), "MySet", "set('x','y','z')", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyFloat"), "MyFloat", "float", true, null);
        AssertColumnProperties(ColumnNamed(result, "MySmallint"), "MySmallint", ExpectedIntegerType("smallint"), true, null);
        AssertColumnProperties(ColumnNamed(result, "MyTinyint"), "MyTinyint", ExpectedIntegerType("tinyint"), true, null);
        AssertColumnProperties(ColumnNamed(result, "MyBigint"), "MyBigint", ExpectedIntegerType("bigint"), true, null);
        AssertColumnProperties(ColumnNamed(result, "MyDate"), "MyDate", "date", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyTime"), "MyTime", "time", true, null);
        AssertColumnProperties(ColumnNamed(result, "MyBitWithDefault"), "MyBitWithDefault", "bit(1)", false, "b'1'");
        AssertColumnProperties(ColumnNamed(result, "MyIntWithDefault"), "MyIntWithDefault", ExpectedIntegerType("int"), false, "42");
        AssertColumnProperties(ColumnNamed(result, "MyDecimalWithDefault"), "MyDecimalWithDefault", "decimal(12,4)", false, "3.1400");

        var identityCol = (MySqlColumn)ColumnNamed(result, "MyIdentity");
        Assert.That(identityCol.Name, Is.EqualTo("`MyIdentity`"), "Name of MyIdentity");
        Assert.That(identityCol.AutoIncrement, Is.True, "AutoIncrement of MyIdentity");

        AssertColumnProperties(ColumnNamed(result, "MyMediumText"), "MyMediumText", "mediumtext", true, null);

        Assert.That(result.ForeignKeys, Is.Not.Null);
        Assert.That(result.ForeignKeys, Has.Count.EqualTo(0));
        Assert.That(result.CheckConstraints, Is.Not.Null);
        Assert.That(result.CheckConstraints, Has.Count.EqualTo(0));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestIndexes` (
    `MyInt` INT NOT NULL,
    `MyBigInt` BIGINT NOT NULL,
    `MyString` VARCHAR(100) NULL,
    PRIMARY KEY (`MyInt`),
    UNIQUE KEY `UQ_TestIndexes_MyString` (`MyString`),
    INDEX `IX_TestIndexes_MyBigInt` (`MyBigInt`),
    INDEX `IX_TestIndexes_Composite` (`MyString`, `MyBigInt`)
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, _integrationDb, "TestIndexes");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("`TestIndexes`"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.Indexes, Is.Not.Null);
        Assert.That(result.Indexes, Has.Count.EqualTo(4));

        // Indexes are returned by GROUP BY on INDEX_NAME
        AssertIndexProperties(result.Indexes, "PRIMARY", true, true, true, "BTREE", "`MyInt`", true);
        AssertIndexProperties(result.Indexes, "IX_TestIndexes_MyBigInt", false, false, false, "BTREE", "`MyBigInt`", true);
        AssertIndexProperties(result.Indexes, "IX_TestIndexes_Composite", false, false, false, "BTREE", "`MyString`,`MyBigInt`", true);
        AssertIndexProperties(result.Indexes, "UQ_TestIndexes_MyString", false, true, true, "BTREE", "`MyString`", true);

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForForeignKeys()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`MyFKReferencedTable` (
    `Id` INT NOT NULL PRIMARY KEY,
    `RefCol` INT NOT NULL,
    UNIQUE KEY `IDX_RefKey` (`RefCol`)
) ENGINE=InnoDB;

CREATE TABLE `{_integrationDb}`.`MyFKTable` (
    `Id` INT NOT NULL PRIMARY KEY,
    `Col2` INT NULL,
    `Col3` INT NULL,
    CONSTRAINT `FK_MyFKTable_Col3_Ref_Id` FOREIGN KEY (`Col3`) REFERENCES `{_integrationDb}`.`MyFKReferencedTable` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_MyFKTable_Col2_Ref_RefCol` FOREIGN KEY (`Col2`) REFERENCES `{_integrationDb}`.`MyFKReferencedTable` (`RefCol`) ON UPDATE CASCADE
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, _integrationDb, "MyFKTable");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("`MyFKTable`"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.ForeignKeys, Is.Not.Null);
        Assert.That(result.ForeignKeys, Has.Count.EqualTo(2));

        var fk0 = FindForeignKey(result, "FK_MyFKTable_Col2_Ref_RefCol");
        Assert.That(fk0, Is.Not.Null, "FK_MyFKTable_Col2_Ref_RefCol should exist");
        Assert.That(fk0.Columns, Is.EqualTo("`Col2`"));
        Assert.That(fk0.RelatedTable, Is.EqualTo("`MyFKReferencedTable`"));
        Assert.That(fk0.RelatedColumns, Is.EqualTo("`RefCol`"));
        Assert.That(fk0.DeleteAction, Is.EqualTo(ExpectedDefaultFkAction));
        Assert.That(fk0.UpdateAction, Is.EqualTo("CASCADE"));

        var fk1 = FindForeignKey(result, "FK_MyFKTable_Col3_Ref_Id");
        Assert.That(fk1, Is.Not.Null, "FK_MyFKTable_Col3_Ref_Id should exist");
        Assert.That(fk1.Columns, Is.EqualTo("`Col3`"));
        Assert.That(fk1.RelatedTable, Is.EqualTo("`MyFKReferencedTable`"));
        Assert.That(fk1.RelatedColumns, Is.EqualTo("`Id`"));
        Assert.That(fk1.DeleteAction, Is.EqualTo("CASCADE"));
        Assert.That(fk1.UpdateAction, Is.EqualTo(ExpectedDefaultFkAction));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForCheckConstraint()
    {
        if (!TargetSupportsCheckConstraints)
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; MySQL 5.7 parses-and-ignores them, so none are extracted.");
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestChecks` (
    `MyInt` INT NOT NULL,
    `MyBigInt` BIGINT NOT NULL,
    `MyString` VARCHAR(100) NULL,
    CONSTRAINT `CK_TestChecks_MyInt` CHECK (`MyInt` < `MyBigInt`),
    CONSTRAINT `CK_TestChecks_MyBigInt` CHECK (`MyBigInt` > `MyInt`)
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, _integrationDb, "TestChecks");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("`TestChecks`"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(3));
        Assert.That(result.CheckConstraints, Is.Not.Null);
        Assert.That(result.CheckConstraints, Has.Count.EqualTo(2));

        var ck0 = FindCheckConstraint(result, "CK_TestChecks_MyBigInt");
        Assert.That(ck0, Is.Not.Null, "CK_TestChecks_MyBigInt should exist");
        Assert.That(ck0.Expression, Does.Contain("`MyBigInt`"));
        Assert.That(ck0.Expression, Does.Contain("`MyInt`"));

        var ck1 = FindCheckConstraint(result, "CK_TestChecks_MyInt");
        Assert.That(ck1, Is.Not.Null, "CK_TestChecks_MyInt should exist");
        Assert.That(ck1.Expression, Does.Contain("`MyInt`"));
        Assert.That(ck1.Expression, Does.Contain("`MyBigInt`"));

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForFullTextIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestFullText` (
    `Id` INT NOT NULL AUTO_INCREMENT,
    `Title` VARCHAR(200) NULL,
    `Body` TEXT NULL,
    PRIMARY KEY (`Id`),
    FULLTEXT KEY `FT_Title` (`Title`),
    FULLTEXT KEY `FT_TitleBody` (`Title`, `Body`)
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, _integrationDb, "TestFullText");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("`TestFullText`"));
        Assert.That(result.FullTextIndexes, Is.Not.Null);
        Assert.That(result.FullTextIndexes, Has.Count.EqualTo(2));

        var ft0 = FindFullTextIndex(result, "FT_Title");
        Assert.That(ft0, Is.Not.Null, "FT_Title should exist");
        Assert.That(ft0.Columns, Is.EqualTo("`Title`"));

        var ft1 = FindFullTextIndex(result, "FT_TitleBody");
        Assert.That(ft1, Is.Not.Null, "FT_TitleBody should exist");
        Assert.That(ft1.Columns, Is.EqualTo("`Title`,`Body`"));

        // Fulltext indexes should NOT appear in regular Indexes list
        foreach (var idx in result.Indexes)
        {
            var mysqlIdx = (MySqlIndex)idx;
            Assert.That(mysqlIdx.IndexType, Is.Not.EqualTo("FULLTEXT"), $"Index {mysqlIdx.Name} should not be FULLTEXT in regular indexes");
        }

        conn.Close();
    }

    [Test]
    public void ShouldGenerateCorrectJsonForGeneratedColumns()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestGenerated` (
    `Price` DECIMAL(10,2) NOT NULL,
    `Quantity` INT NOT NULL,
    `Total` DECIMAL(10,2) GENERATED ALWAYS AS (`Price` * `Quantity`) STORED,
    `DoubleTotal` DECIMAL(10,2) GENERATED ALWAYS AS (`Total` * 2) VIRTUAL
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();
        var result = GenerateTable(cmd, _integrationDb, "TestGenerated");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("`TestGenerated`"));
        Assert.That(result.Columns, Is.Not.Null);
        Assert.That(result.Columns, Has.Count.EqualTo(4));

        var totalCol = (MySqlColumn)ColumnNamed(result, "Total");
        Assert.That(totalCol.Name, Is.EqualTo("`Total`"));
        Assert.That(totalCol.Generated, Is.EqualTo("STORED"));
        Assert.That(totalCol.GenerationExpression, Is.Not.Null.And.Not.Empty);

        var doubleTotalCol = (MySqlColumn)ColumnNamed(result, "DoubleTotal");
        Assert.That(doubleTotalCol.Name, Is.EqualTo("`DoubleTotal`"));
        Assert.That(doubleTotalCol.Generated, Is.EqualTo("VIRTUAL"));
        Assert.That(doubleTotalCol.GenerationExpression, Is.Not.Null.And.Not.Empty);

        conn.Close();
    }

    // Look columns up by name, not by array position. Extraction order is a formatting decision -- it
    // changed once already when MySQL moved from ordinal to alphabetical to match the other engines --
    // and these tests are about a column's PROPERTIES, so coupling them to position made an unrelated
    // change look like a regression in bit/decimal/generated-column handling.
    private static Column ColumnNamed(Table table, string bareName)
    {
        var column = table.Columns.FirstOrDefault(c =>
            string.Equals(c.Name?.Trim('`'), bareName, System.StringComparison.Ordinal));
        Assert.That(column, Is.Not.Null, $"extraction produced no column named '{bareName}'");
        return column;
    }

    private void AssertColumnProperties(Column column, string name, string dataType, bool nullable, string defaultValue)
    {
        var mysqlColumn = (MySqlColumn)column;
        Assert.That(mysqlColumn.Name, Is.EqualTo($"`{name}`"), $"Name of {name}");
        Assert.That(mysqlColumn.DataType, Is.EqualTo(dataType), $"Type of {name}");
        Assert.That(mysqlColumn.Nullable, Is.EqualTo(nullable), $"Nullability of {name}");
        Assert.That(mysqlColumn.Default, Is.EqualTo(defaultValue), $"Default of {name}");
    }

    private void AssertIndexProperties(List<Index> indexes, string name, bool isPrimaryKey, bool isUnique, bool isUniqueConstraint, string indexType, string columns, bool visible)
    {
        var index = indexes.Find(i => i.Name == name || i.Name == $"`{name}`");
        Assert.That(index, Is.Not.Null, $"Index {name} should exist");
        var mysqlIndex = (MySqlIndex)index;
        Assert.That(mysqlIndex.PrimaryKey, Is.EqualTo(isPrimaryKey), $"PrimaryKey of {name}");
        Assert.That(mysqlIndex.Unique, Is.EqualTo(isUnique), $"Unique of {name}");
        Assert.That(mysqlIndex.UniqueConstraint, Is.EqualTo(isUniqueConstraint), $"UniqueConstraint of {name}");
        Assert.That(mysqlIndex.IndexType, Is.EqualTo(indexType), $"IndexType of {name}");
        Assert.That(mysqlIndex.IndexColumns, Is.EqualTo(columns), $"IndexColumns of {name}");
        Assert.That(mysqlIndex.Visible, Is.EqualTo(visible), $"Visible of {name}");
    }

    private static MySqlForeignKey FindForeignKey(MySqlTable table, string name)
    {
        return (MySqlForeignKey)table.ForeignKeys.Find(fk => fk.Name == name || fk.Name == $"`{name}`");
    }

    private static CheckConstraint FindCheckConstraint(MySqlTable table, string name)
    {
        return table.CheckConstraints.Find(ck => ck.Name == name || ck.Name == $"`{name}`");
    }

    private static FullTextIndex FindFullTextIndex(MySqlTable table, string name)
    {
        return table.FullTextIndexes.Find(ft => ft.Name == name || ft.Name == $"`{name}`");
    }

    [Test]
    public void ShouldEmitPreventDropOnlyForProtectedTables()
    {
        // #270 round-trip: a table protected in the source DB (sticky PreventDrop marker set) must extract with
        // "PreventDrop": true so an extract -> re-deploy preserves protection; an unprotected sibling omits the key.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`ProtectedExtractTable` (`Id` INT NOT NULL PRIMARY KEY) ENGINE=InnoDB;
CREATE TABLE `{_integrationDb}`.`UnprotectedExtractTable` (`Id` INT NOT NULL PRIMARY KEY) ENGINE=InnoDB;

INSERT INTO SchemaSmith_ProductOwnership (ObjectType, ObjectSchema, ObjectName, ProductName, TemplateName, PreventDrop)
VALUES ('TABLE', '{_integrationDb}', 'ProtectedExtractTable', 'TestProduct', '', 1);
";
        cmd.ExecuteNonQuery();

        var protectedJson = GenerateTableJson(cmd, _integrationDb, "ProtectedExtractTable");
        var protectedTable = (MySqlTable)PlatformDeserializer.DeserializeTable(protectedJson, Platform);
        var unprotectedJson = GenerateTableJson(cmd, _integrationDb, "UnprotectedExtractTable");
        var unprotectedTable = (MySqlTable)PlatformDeserializer.DeserializeTable(unprotectedJson, Platform);

        Assert.Multiple(() =>
        {
            Assert.That(protectedTable.PreventDrop, Is.True, "Protected table must round-trip PreventDrop:true.");
            Assert.That(protectedJson, Does.Contain("PreventDrop"), "Extracted JSON for a protected table must carry the PreventDrop marker.");
            Assert.That(unprotectedTable.PreventDrop, Is.False, "Unprotected table must deserialize to PreventDrop:false.");
            Assert.That(unprotectedJson, Does.Not.Contain("PreventDrop"), "Extracted JSON for an unprotected table must omit the PreventDrop key.");
        });

        conn.Close();
    }

    /// <summary>
    /// Extraction from a database whose default collation DIFFERS from the server's.
    ///
    /// <para><b>Why this did not exist, and why it is the gap worth closing.</b> Every database these
    /// fixtures create is made with a bare <c>CREATE DATABASE</c>, so it inherits the server default and
    /// both sides of every catalog comparison collate identically. The whole suite has therefore never
    /// exercised the mixed case -- and neither has MySQL in the demo estate, where the sample databases
    /// happen to carry that server's default. A customer database created with an explicit
    /// <c>COLLATE</c> is ordinary, not exotic.</para>
    ///
    /// <para>The failure mode being guarded against is <c>Illegal mix of collations (…,COERCIBLE) and
    /// (…,COERCIBLE) for operation '='</c>, which aborts extraction outright rather than degrading: a
    /// catalog string collated by the database compared against a literal or function result collated by
    /// the connection. The MySQL-family scripts guard it with <c>BINARY</c> or <c>CONVERT(… USING
    /// utf8mb4)</c> comparisons; this test is what says every path that needs the guard has it.</para>
    ///
    /// <para>Asserted at the outcome rather than on any one query, because the point is that NO
    /// comparison anywhere in the extraction path is left uncollated -- a test naming a single statement
    /// would go stale the moment a new one is added.</para>
    /// </summary>
    [Test]
    public void ShouldExtractFromADatabaseWhoseCollationDiffersFromTheServers()
    {
        using var admin = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        admin.Open();
        using var acmd = admin.CreateCommand();
        acmd.CommandTimeout = 300;

        acmd.CommandText = "SELECT @@collation_server";
        var serverCollation = acmd.ExecuteScalar()?.ToString() ?? "";

        // utf8mb4_unicode_ci exists on every supported version of both engines and is the default on
        // none of them -- MySQL 8.0 defaults to utf8mb4_0900_ai_ci, MariaDB 11.4 to utf8mb4_uca1400_ai_ci,
        // and the 5.7 / 10.2 floors to latin1_swedish_ci.
        const string differing = "utf8mb4_unicode_ci";
        if (string.Equals(serverCollation, differing, StringComparison.OrdinalIgnoreCase))
            Assert.Ignore($"Server default is already {differing}; this test needs the two to differ.");

        var collDb = GenerateUniqueDBName("CollationMismatch");
        try
        {
            acmd.CommandText = $"CREATE DATABASE `{collDb}` CHARACTER SET utf8mb4 COLLATE {differing}";
            acmd.ExecuteNonQuery();

            admin.ChangeDatabase(collDb);
            ForgeKindler.KindleTheForge(acmd, Platform);

            acmd.CommandText = $@"
CREATE TABLE `{collDb}`.`CollWidget` (
    `Id` INT NOT NULL PRIMARY KEY,
    `Name` VARCHAR(50) NULL,
    KEY `ix_coll_name` (`Name`)
) ENGINE=InnoDB;

CREATE TABLE `{collDb}`.`CollGadget` (
    `Id` INT NOT NULL PRIMARY KEY,
    `WidgetId` INT NULL,
    CONSTRAINT `fk_coll_gadget_widget` FOREIGN KEY (`WidgetId`) REFERENCES `CollWidget` (`Id`)
) ENGINE=InnoDB;";
            acmd.ExecuteNonQuery();

            // The database's collation really does differ -- without this the test could pass by
            // accidentally recreating the matched case it exists to avoid.
            acmd.CommandText =
                $"SELECT DEFAULT_COLLATION_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{collDb}'";
            Assert.That(acmd.ExecuteScalar()?.ToString(), Is.EqualTo(differing).IgnoreCase,
                "Setup: the database must carry the non-default collation for this test to mean anything.");

            var widgetJson = GenerateTableJson(acmd, collDb, "CollWidget");
            var gadgetJson = GenerateTableJson(acmd, collDb, "CollGadget");

            Assert.Multiple(() =>
            {
                Assert.That(widgetJson, Is.Not.Null.And.Not.Empty,
                    $"extraction returned nothing for a database collated {differing} against a server "
                    + $"collated {serverCollation} -- an uncollated catalog comparison aborts the whole "
                    + "extraction, it does not degrade");
                Assert.That(gadgetJson, Is.Not.Null.And.Not.Empty);
            });

            var widget = PlatformDeserializer.DeserializeTable(widgetJson, Platform);
            var gadget = PlatformDeserializer.DeserializeTable(gadgetJson, Platform);

            Assert.Multiple(() =>
            {
                Assert.That(widget.Columns.Select(c => c.Name.Replace("`", "")),
                    Is.EquivalentTo(new[] { "Id", "Name" }),
                    "columns must survive -- the collation guard has to cover the column read too");
                Assert.That(widget.Indexes.Any(i => i.Name.Replace("`", "") == "ix_coll_name"), Is.True,
                    "and the index read");
                Assert.That(gadget.ForeignKeys.Any(f => f.Name.Replace("`", "") == "fk_coll_gadget_widget"),
                    Is.True, "and the foreign-key read, which compares names across two tables");
            });
        }
        finally
        {
            admin.ChangeDatabase(Platform == Platform.PostgreSQL ? "postgres" : "mysql");
            DropOneDatabase(acmd, collDb);
        }
        admin.Close();
    }

    protected string GenerateTableJson(IDbCommand cmd, string schema, string table)
    {
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{schema}', '{table}')";
        using var reader = cmd.ExecuteReader();

        var tableJson = string.Empty;
        while (reader.Read())
        {
            // DBNull, not an empty string, is what the proc returns for a table it did not match -- and a
            // table it does not match is exactly the silent-omission failure these tests exist to detect.
            // GetString threw InvalidCastException there, which reported as a cast bug and buried the real
            // finding under it. Degrade to empty so the caller's own assertion gets to speak.
            if (reader.IsDBNull(0)) continue;
            tableJson += reader.GetString(0);
        }

        return tableJson;
    }

    [Test]
    public void ShouldOrderColumnsPhysicallyWhenTheSessionAsksForIt()
    {
        // The Physical branch is opt-in, so nothing else in the suite exercises it -- an untested branch
        // is an unimplemented one. MySQL and MariaDB carry the choice in a session variable because their
        // stored procedures take no default parameter values, so this also pins that mechanism.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestPhysicalOrder` (
    `Zebra` INT NOT NULL,
    `Apple` INT NULL,
    `Mango` INT NULL
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SET @SchemaSmith_ObjectOrder = 'Physical'";
        cmd.ExecuteNonQuery();
        var physical = GenerateTable(cmd, _integrationDb, "TestPhysicalOrder");

        cmd.CommandText = "SET @SchemaSmith_ObjectOrder = 'Name'";
        cmd.ExecuteNonQuery();
        var byName = GenerateTable(cmd, _integrationDb, "TestPhysicalOrder");

        Assert.Multiple(() =>
        {
            Assert.That(physical.Columns.Select(c => c.Name.Trim('`')),
                Is.EqualTo(new[] { "Zebra", "Apple", "Mango" }),
                "Physical must give the table's own column order");
            Assert.That(byName.Columns.Select(c => c.Name.Trim('`')),
                Is.EqualTo(new[] { "Apple", "Mango", "Zebra" }),
                "Name must sort alphabetically, and must not be affected by the previous session value");
        });

        conn.Close();
    }

    [Test]
    public void ShouldOrderIndexesForeignKeysAndCheckConstraintsByName()
    {
        // #10. Product:ObjectOrder orders COLUMNS. Every other list is a set -- there is no physical
        // sequence to preserve -- so all three engines emit them in name order unconditionally. SQL Server
        // and PostgreSQL always did. MySQL and MariaDB had NO ORDER BY on any of them, so the sequence was
        // whatever the INFORMATION_SCHEMA plan happened to produce.
        //
        // MEASURED before the fix: MariaDB 11.4 returned this table's check constraints in creation order
        // (zzz, mmm, aaa), and MySQL 8.0 returned the SAME table's two different ways on two runs. A first
        // extraction therefore recorded an arbitrary order, and a later one could reshuffle the file with
        // no schema change behind it.
        //
        // Everything below is created in deliberately reverse-alphabetical order, so creation order and
        // name order cannot be mistaken for one another.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestOrderParent` (
    `pid` INT NOT NULL PRIMARY KEY,
    `qid` INT NOT NULL,
    `rid` INT NOT NULL,
    UNIQUE KEY `uq_q` (`qid`),
    UNIQUE KEY `uq_r` (`rid`)
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`TestOrderChild` (
    `id` INT NOT NULL PRIMARY KEY,
    `zcol` INT NULL, `mcol` INT NULL, `acol` INT NULL,
    `fz` INT NULL, `fm` INT NULL, `fa` INT NULL
) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();

        foreach (var ddl in new[]
                 {
                     $"CREATE INDEX `zzz_idx` ON `{_integrationDb}`.`TestOrderChild` (`zcol`)",
                     $"CREATE INDEX `mmm_idx` ON `{_integrationDb}`.`TestOrderChild` (`mcol`)",
                     $"CREATE INDEX `aaa_idx` ON `{_integrationDb}`.`TestOrderChild` (`acol`)",
                     $"ALTER TABLE `{_integrationDb}`.`TestOrderChild` ADD CONSTRAINT `zzz_fk` FOREIGN KEY (`fz`) REFERENCES `{_integrationDb}`.`TestOrderParent` (`pid`)",
                     $"ALTER TABLE `{_integrationDb}`.`TestOrderChild` ADD CONSTRAINT `mmm_fk` FOREIGN KEY (`fm`) REFERENCES `{_integrationDb}`.`TestOrderParent` (`qid`)",
                     $"ALTER TABLE `{_integrationDb}`.`TestOrderChild` ADD CONSTRAINT `aaa_fk` FOREIGN KEY (`fa`) REFERENCES `{_integrationDb}`.`TestOrderParent` (`rid`)"
                 })
        {
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        if (TargetSupportsCheckConstraints)
            foreach (var name in new[] { "zzz", "mmm", "aaa" })
            {
                cmd.CommandText = $"ALTER TABLE `{_integrationDb}`.`TestOrderChild` ADD CONSTRAINT `{name}_chk` CHECK (`{name[0]}col` > 0)";
                cmd.ExecuteNonQuery();
            }

        var table = GenerateTable(cmd, _integrationDb, "TestOrderChild");

        // The FK constraints each carry a backing index of the same name, so both lists are covered by one
        // table. PRIMARY sorts on 'P', between the m- and z- names.
        var expectedIndexes = new[] { "aaa_fk", "aaa_idx", "mmm_fk", "mmm_idx", "PRIMARY", "zzz_fk", "zzz_idx" };

        Assert.Multiple(() =>
        {
            Assert.That(table.Indexes.Select(i => i.Name.Trim('`')), Is.EqualTo(expectedIndexes),
                "indexes must extract in name order -- they were created in reverse-alphabetical order, so "
                + "any other sequence means the generator is emitting whatever the plan returned");
            Assert.That(table.ForeignKeys.Select(f => f.Name.Trim('`')), Is.EqualTo(new[] { "aaa_fk", "mmm_fk", "zzz_fk" }),
                "foreign keys must extract in name order, not creation order");
            if (TargetSupportsCheckConstraints)
                Assert.That(table.CheckConstraints.Select(c => c.Name.Trim('`')), Is.EqualTo(new[] { "aaa_chk", "mmm_chk", "zzz_chk" }),
                    "check constraints must extract in name order -- this is the one MariaDB reliably got "
                    + "wrong before #10, returning them in creation order");
        });

        // 'Physical' is a column preference and must leave these sets exactly where they were. If it moved
        // them, the sort would be reading the setting when it has nothing to say about a set.
        cmd.CommandText = "SET @SchemaSmith_ObjectOrder = 'Physical'";
        cmd.ExecuteNonQuery();
        var physical = GenerateTable(cmd, _integrationDb, "TestOrderChild");
        cmd.CommandText = "SET @SchemaSmith_ObjectOrder = 'Name'";
        cmd.ExecuteNonQuery();

        Assert.That(physical.Indexes.Select(i => i.Name.Trim('`')), Is.EqualTo(expectedIndexes),
            "ObjectOrder=Physical must not reorder a set -- it selects the COLUMN sequence and nothing else");

        conn.Close();
    }

    private MySqlTable GenerateTable(IDbCommand cmd, string schema, string table)
    {
        return (MySqlTable)PlatformDeserializer.DeserializeTable(GenerateTableJson(cmd, schema, table), Platform);
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE `{_integrationDb}`";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform);

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
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{dbName}`";
        cmd.ExecuteNonQuery();
    }
}
