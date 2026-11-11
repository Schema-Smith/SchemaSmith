// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

/// <summary>
/// A data script DataTongs writes on a MySQL whose JSON_TABLE reads scalars correctly carries typed JSON_TABLE columns.
/// Delivered to MySQL 8.0.11-8.0.20, which misreads them, a JSON null became the text 'null' in a text column and failed a
/// number column under strict mode. Delivery there rebuilds the marked row source in the read-as-JSON form.
/// Runs on the 8.0.11-8.0.20 floor bands, generating on the demo MySQL (port 3306, a newer patch).
/// </summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
public class GeneratedDataScriptAcrossVersionsTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MySQL";
    protected override string ProductName => "GeneratedAcrossVersions";

    private const string Table = "gen_across_t";
    private const string CreateTable = $"CREATE TABLE `{Table}` (`id` INT NOT NULL PRIMARY KEY, `note` VARCHAR(20) NULL, `n` INT NULL)";

    [Test]
    public void AScriptGeneratedOnANewerMySql_DeliversJsonNullsAsSqlNull_OnAPatchThatMisreadsThem()
    {
        var targetVersion = Scalar("SELECT VERSION()")?.ToString() ?? "";
        if (!MergeScriptHelper.JsonTableMisreadsScalarsMySql(targetVersion))
            Assert.Ignore($"Needs a MySQL target whose JSON_TABLE misreads scalars (8.0.11-8.0.20); this one is {targetVersion}.");

        var source = new MySqlConnectionStringBuilder(MainConnectionString) { Port = 3306, Database = "" }.ConnectionString;
        var sourceDb = $"probe_gen_{Guid.NewGuid():N}"[..24];
        using var generator = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(source);
        generator.Open();
        using var gen = generator.CreateCommand();
        gen.CommandText = "SELECT VERSION()";
        var sourceVersion = gen.ExecuteScalar()?.ToString() ?? "";
        if (MergeScriptHelper.JsonTableMisreadsScalarsMySql(sourceVersion) || sourceVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
            Assert.Ignore($"Needs a generating MySQL on port 3306 whose JSON_TABLE reads scalars correctly; that one is {sourceVersion}.");

        Exec($"DROP TABLE IF EXISTS `{Table}`");
        try
        {
            gen.CommandText = $"CREATE DATABASE `{sourceDb}`; USE `{sourceDb}`; {CreateTable}";
            gen.ExecuteNonQuery();
            var script = MergeScriptHelper.BuildMergeScript(Platform.MySQL, gen, sourceDb, Table,
                """[{"id":1,"note":null,"n":null},{"id":2,"note":"x","n":7}]""", "`id`",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null,
                destSchemaOverride: "", mySqlServerVersionNum: TargetVersionDetector.Detect(gen, Platform.MySQL).ServerComparable);
            Assert.That(script, Does.Contain(MergeScriptHelper.TypedRowSourceOpenMySql), "setup: generated with a typed, marked row source");

            Exec(CreateTable);
            WritePackage("""{ "FolderPath": "Table Data", "QuenchSlot": "TableData" }""", "Table Data");
            WriteFile("Table Data", $"{Table}.sql", script);

            var result = Deploy(deliverData: true);

            Assert.That(Scalar($"SELECT GROUP_CONCAT(CONCAT(id, ':', COALESCE(note, '<NULL>'), ':', COALESCE(n, '<NULL>')) ORDER BY id SEPARATOR ' ') FROM `{Table}`")?.ToString(),
                Is.EqualTo("1:<NULL>:<NULL> 2:x:7"), () => "a JSON null arrives as SQL NULL, not the text 'null', on a patch that misreads typed JSON_TABLE"
                    + $"; scripts: {result.Describe()}; log: {string.Join(" | ", result.Log)}");
        }
        finally
        {
            gen.CommandText = $"DROP DATABASE IF EXISTS `{sourceDb}`";
            gen.ExecuteNonQuery();
            Exec($"DROP TABLE IF EXISTS `{Table}`");
        }
    }
}
