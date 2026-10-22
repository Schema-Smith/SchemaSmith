// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Exercises SchemaSmith_BootstrapTableQuench's declarative OldName rename (TABLE and COLUMN level)
/// directly -- not through ForgeKindler.KindleTheForge -- against a throwaway test table, on the
/// MySQL/MariaDb family. Bootstrap has zero SchemaSmith_* dependencies by design (it runs before any
/// other helper exists), so calling it directly here mirrors how it is actually invoked during
/// kindling: one JSON payload in, no other proc involved. The MySQL and MariaDb subclasses supply the
/// platform + fixture accessors and the engine's real RENAME COLUMN floor-simulating override; every
/// [Test] body here runs on both engines.
/// </summary>
public abstract class BootstrapOldNameRenameSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    /// <summary>
    /// @schemasmith_version_override value that lands BELOW the engine's real RENAME COLUMN floor
    /// (MySQL 8.0 / MariaDB 10.6), forcing Bootstrap's inline version check to take the CHANGE COLUMN
    /// fallback path -- same values used by the sibling gating tests (InvisibleColumnGatingTests etc).
    /// </summary>
    protected abstract int BelowRenameColumnFloorVersionOverride { get; }

    private const string TableName = "bootstrap_oldname_test";
    private const string OldTableName = "bootstrap_oldname_test_old";

    private IDbConnection _connection = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        // ForgeKindler is already deployed into MainDb by the fixture, so SchemaSmith_BootstrapTableQuench exists.
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        SetVersionOverride(null);
        DropTable(TableName);
        DropTable(OldTableName);
    }

    [TearDown]
    public void TearDown()
    {
        SetVersionOverride(null);
        DropTable(TableName);
        DropTable(OldTableName);
    }

    // ---- helpers -----------------------------------------------------------

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private void SetVersionOverride(int? majorMinor) =>
        Exec(majorMinor.HasValue ? $"SET @schemasmith_version_override = {majorMinor.Value}" : "SET @schemasmith_version_override = NULL");

    private void DropTable(string tableName) => Exec($"DROP TABLE IF EXISTS `{tableName}`");

    private void CallBootstrap(string json) =>
        Exec($"CALL SchemaSmith_BootstrapTableQuench('{json.Replace("'", "''")}')");

    private long ColumnCount(string tableName, string columnName) =>
        Scalar($@"SELECT COUNT(*) FROM information_schema.columns
                   WHERE table_schema = '{MainDb}' AND table_name = '{tableName}' AND column_name = '{columnName}'");

    private long TableCount(string tableName) =>
        Scalar($@"SELECT COUNT(*) FROM information_schema.tables
                   WHERE table_schema = '{MainDb}' AND table_name = '{tableName}'");

    // Bootstrap's MySQL/MariaDb JSON contract uses BARE names (no backtick wrapping) -- unlike the
    // full ParseTableJson/TableQuench pipeline's MySqlTable/MySqlColumn domain objects, which expect
    // backtick-wrapped Name and would be double-wrapped if fed through Bootstrap (see the real
    // Kindling_CompletedMigrationScripts.json, which also uses bare names). Built with plain string
    // concatenation (not a domain-object serializer) so the exact bare-name shape is unambiguous.
    private static string ColumnRenameJson(string tableName, string newColOldName = null)
    {
        var valueColumnOldName = newColOldName == null ? "" : $", \"OldName\": \"{newColOldName}\"";
        return "{"
            + $"\"Name\": \"{tableName}\","
            + "\"Columns\": ["
            + "{\"Name\": \"Id\", \"DataType\": \"INT\", \"Nullable\": false, \"AutoIncrement\": true, \"PrimaryKey\": true},"
            + "{\"Name\": \"Value\", \"DataType\": \"VARCHAR(50)\", \"Nullable\": false, \"Default\": \"'0'\"" + valueColumnOldName + "}"
            + "],"
            + "\"Indexes\": ["
            + "{\"Name\": \"pk_bootstrap_oldname_test\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"Id\"}"
            + "]"
            + "}";
    }

    private static string TableRenameJson(string tableName, string oldTableName = null)
    {
        var tableOldName = oldTableName == null ? "" : $", \"OldName\": \"{oldTableName}\"";
        return "{"
            + $"\"Name\": \"{tableName}\"" + tableOldName + ","
            + "\"Columns\": ["
            + "{\"Name\": \"Id\", \"DataType\": \"INT\", \"Nullable\": false, \"AutoIncrement\": true, \"PrimaryKey\": true},"
            + "{\"Name\": \"Value\", \"DataType\": \"VARCHAR(50)\", \"Nullable\": false, \"Default\": \"'0'\"}"
            + "],"
            + "\"Indexes\": ["
            + "{\"Name\": \"pk_bootstrap_oldname_test\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"Id\"}"
            + "]"
            + "}";
    }

    // ---- COLUMN-level rename ------------------------------------------------

    [Test]
    public void ColumnRename_FreshTable_CreatesWithNewNameOnly_NoOldColumn()
    {
        // No legacy "OldCol" ever existed -- Bootstrap's rename guard (old exists) is false, so this
        // is a plain CREATE TABLE using the new column name. Proves OldName on a brand-new table is inert.
        CallBootstrap(ColumnRenameJson(TableName, newColOldName: "OldCol"));

        Assert.That(ColumnCount(TableName, "Value"), Is.EqualTo(1), "New column name must exist.");
        Assert.That(ColumnCount(TableName, "OldCol"), Is.EqualTo(0), "No legacy column ever existed; nothing to rename.");
    }

    [Test]
    public void ColumnRename_LegacyColumnPresent_RenamesAndDataSurvives()
    {
        // Pre-create the table with the OLD column name and a distinguishing value, matching what a
        // real field-deployed table looks like before the rename ships.
        Exec($"CREATE TABLE `{TableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `OldCol` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"INSERT INTO `{TableName}` (`OldCol`) VALUES ('distinguishing-value')");

        CallBootstrap(ColumnRenameJson(TableName, newColOldName: "OldCol"));

        Assert.That(ColumnCount(TableName, "Value"), Is.EqualTo(1), "Renamed column must exist under the new name.");
        Assert.That(ColumnCount(TableName, "OldCol"), Is.EqualTo(0), "Old column name must be gone after rename.");
        // The decisive assertion: a rename that dropped-and-re-added would pass the two checks above
        // while destroying the row's data. Reading the value back proves it is a genuine rename.
        Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 1"), Is.EqualTo("distinguishing-value"),
            "The pre-existing row's value must survive the rename, not read back as the column's DEFAULT.");
    }

    [Test]
    public void ColumnRename_SecondRun_IsIdempotent()
    {
        Exec($"CREATE TABLE `{TableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `OldCol` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"INSERT INTO `{TableName}` (`OldCol`) VALUES ('distinguishing-value')");

        CallBootstrap(ColumnRenameJson(TableName, newColOldName: "OldCol"));
        Assert.DoesNotThrow(() => CallBootstrap(ColumnRenameJson(TableName, newColOldName: "OldCol")),
            "A second call against an already-renamed table must be a no-op, not an error.");

        Assert.That(ColumnCount(TableName, "Value"), Is.EqualTo(1), "Exactly one Value column after two runs.");
        Assert.That(ColumnCount(TableName, "OldCol"), Is.EqualTo(0), "OldCol must still be gone.");
        Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 1"), Is.EqualTo("distinguishing-value"),
            "Data must still be intact after the idempotent second run.");
    }

    [Test]
    public void ColumnRename_BothOldAndNewColumnsExist_Throws()
    {
        // The dangerous case: someone (or a partial prior deploy) left both columns present. Bootstrap
        // must fail loudly rather than silently pick a side -- a silent skip would hide that the table
        // no longer matches what the model expects.
        Exec($"CREATE TABLE `{TableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `OldCol` VARCHAR(50) NOT NULL DEFAULT '0', `Value` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");

        var ex = Assert.Catch<Exception>(() => CallBootstrap(ColumnRenameJson(TableName, newColOldName: "OldCol")));
        Assert.That(ex!.Message + ex.InnerException?.Message, Does.Contain("already exist"));

        // MESSAGE_TEXT is capped at 128 chars so the SIGNAL cannot name the objects. Asserting only
        // that it fired would leave the operator no way to find out WHICH column clashed, so assert
        // the detail is actually retrievable -- that is the outcome, the SIGNAL is just the mechanism.
        var detail = ScalarStr(
            "SELECT Message FROM SchemaSmith_StatusMessages WHERE Message LIKE '%already exist%' " +
            "ORDER BY CreatedAt DESC, Id DESC LIMIT 1");
        Assert.That(detail, Is.Not.Null.And.Not.Empty,
            "the guard recorded no detail row, so the operator has no way to learn which column clashed");
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain(TableName), "the recorded detail must name the table");
            Assert.That(detail, Does.Contain("OldCol"), "the recorded detail must name the OldName column");
            Assert.That(detail, Does.Contain("Value"), "the recorded detail must name the current column");
        });
    }

    [Test]
    public void ColumnRename_BelowRenameColumnFloor_ChangeColumnFallback_DataAndDefaultSurvive()
    {
        // Forces Bootstrap's inline version check below the engine's real RENAME COLUMN floor so the
        // CHANGE COLUMN fallback fires. Verifies both that pre-existing data survives (proving it's a
        // rename, not a drop/re-add) AND that the DEFAULT was correctly reconstructed from the JSON's
        // own Default (a fresh INSERT omitting Value must pick up the default, not fail/NULL).
        Exec($"CREATE TABLE `{TableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `OldCol` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"INSERT INTO `{TableName}` (`OldCol`) VALUES ('distinguishing-value')");

        SetVersionOverride(BelowRenameColumnFloorVersionOverride);
        CallBootstrap(ColumnRenameJson(TableName, newColOldName: "OldCol"));

        Assert.That(ColumnCount(TableName, "Value"), Is.EqualTo(1), "Renamed column must exist under the new name.");
        Assert.That(ColumnCount(TableName, "OldCol"), Is.EqualTo(0), "Old column name must be gone after rename.");
        Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 1"), Is.EqualTo("distinguishing-value"),
            "The pre-existing row's value must survive the CHANGE COLUMN fallback.");

        Exec($"INSERT INTO `{TableName}` (`Id`) VALUES (2)");
        Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 2"), Is.EqualTo("0"),
            "The DEFAULT must have been reconstructed onto the renamed column by the CHANGE COLUMN fallback.");
    }

    // ---- TABLE-level rename --------------------------------------------------

    [Test]
    public void TableRename_NoLegacyTable_CreatesWithNewNameOnly()
    {
        CallBootstrap(TableRenameJson(TableName, oldTableName: OldTableName));

        Assert.That(TableCount(TableName), Is.EqualTo(1), "New table name must exist.");
        Assert.That(TableCount(OldTableName), Is.EqualTo(0), "No legacy table ever existed; nothing to rename.");
    }

    [Test]
    public void TableRename_LegacyTablePresent_RenamesAndDataSurvives()
    {
        Exec($"CREATE TABLE `{OldTableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `Value` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"INSERT INTO `{OldTableName}` (`Value`) VALUES ('distinguishing-value')");

        CallBootstrap(TableRenameJson(TableName, oldTableName: OldTableName));

        Assert.That(TableCount(TableName), Is.EqualTo(1), "Renamed table must exist under the new name.");
        Assert.That(TableCount(OldTableName), Is.EqualTo(0), "Old table name must be gone after rename.");
        Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 1"), Is.EqualTo("distinguishing-value"),
            "The old table's row -- and its whole history -- must survive the rename, not be orphaned under an empty new table.");
    }

    [Test]
    public void TableRename_SecondRun_IsIdempotent()
    {
        Exec($"CREATE TABLE `{OldTableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `Value` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"INSERT INTO `{OldTableName}` (`Value`) VALUES ('distinguishing-value')");

        CallBootstrap(TableRenameJson(TableName, oldTableName: OldTableName));
        Assert.DoesNotThrow(() => CallBootstrap(TableRenameJson(TableName, oldTableName: OldTableName)),
            "A second call against an already-renamed table must be a no-op, not an error.");

        Assert.That(TableCount(TableName), Is.EqualTo(1));
        Assert.That(TableCount(OldTableName), Is.EqualTo(0));
        Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 1"), Is.EqualTo("distinguishing-value"),
            "Data must still be intact after the idempotent second run.");
    }

    [Test]
    public void TableRename_BothOldAndNewTablesExist_Throws()
    {
        Exec($"CREATE TABLE `{OldTableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `Value` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"CREATE TABLE `{TableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `Value` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");

        var ex = Assert.Catch<Exception>(() => CallBootstrap(TableRenameJson(TableName, oldTableName: OldTableName)));
        Assert.That(ex!.Message + ex.InnerException?.Message, Does.Contain("already exist"));
    }

    // Column names are case-insensitive on every setting, so an OldName that differs from Name only in case names
    // the same column. Read as two names, both "existed" and the rename was refused as a clash.
    [Test]
    public void ColumnRename_CaseOnlyOldName_RespellsTheColumnAndDataSurvives()
    {
        Exec($"CREATE TABLE `{TableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `value` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"INSERT INTO `{TableName}` (`value`) VALUES ('distinguishing-value')");

        CallBootstrap(ColumnRenameJson(TableName, newColOldName: "value"));
        Assert.DoesNotThrow(() => CallBootstrap(ColumnRenameJson(TableName, newColOldName: "value")),
            "the second run finds the column already spelled as declared");

        Assert.Multiple(() =>
        {
            Assert.That(ScalarStr($@"SELECT column_name FROM information_schema.columns
                   WHERE table_schema = '{MainDb}' AND table_name = '{TableName}' AND column_name <> 'Id'"), Is.EqualTo("Value"),
                "the column must carry the declared spelling");
            Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 1"), Is.EqualTo("distinguishing-value"));
        });
    }

    // With lower_case_table_names 1 or 2 a case-only table OldName names the table itself. On 0 it names a table that
    // does not exist, so this guards the folding settings; CI runs 0.
    [Test]
    public void TableRename_CaseOnlyOldName_IsNotAClash()
    {
        Exec($"CREATE TABLE `{TableName}` (`Id` INT AUTO_INCREMENT PRIMARY KEY, `Value` VARCHAR(50) NOT NULL DEFAULT '0') ENGINE=InnoDB");
        Exec($"INSERT INTO `{TableName}` (`Value`) VALUES ('distinguishing-value')");

        Assert.DoesNotThrow(() => CallBootstrap(TableRenameJson(TableName, oldTableName: TableName.ToUpperInvariant())));

        Assert.That(ScalarStr($"SELECT `Value` FROM `{TableName}` WHERE `Id` = 1"), Is.EqualTo("distinguishing-value"));
    }
}
