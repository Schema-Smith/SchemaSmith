// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using NSubstitute;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for table data delivery functionality against MySQL.
/// Exercises MergeScriptHelper directly for single-table merge scenarios and
/// DatabaseQuench for multi-table scenarios (FK ordering, circular FKs, cascade
/// validation, error-continue semantics).
/// Uses dynamically created test databases via FixtureSetup.
/// </summary>
[Category("Integration")]
public abstract class TableDataDeliverySharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    // Config prefix ("MySQL"/"MariaDB") used to read the target server/port/creds for the
    // ViaQuench delivery tests — must be the running engine's prefix, not hardcoded MySQL.
    protected abstract string ConfigPrefix { get; }

    private IDbConnection _connection = null!;
    private string _testTableName = null!;
    private string _testDb = null!;
    // Detected target comparable (major*100+minor, e.g. 507, 800, 1002). Threaded into BuildMergeScript so the
    // MariaDB 10.2-10.5 recursive-CTE data-delivery path is generated instead of the modern JSON_TABLE path.
    private int _serverVersionNum;

    /// <summary>Whether SchemaSmith delivers table data on this target: MySQL needs 8.0 (5.7 has neither
    /// JSON_TABLE nor a recursive-CTE fallback); MariaDB 10.2 works via recursive CTE.</summary>
    private bool SupportsDataDelivery => Platform != Platform.MySQL || _serverVersionNum >= 800;

    /// <summary>BuildMergeScript with the detected target version baked in, and an Assert.Ignore below the
    /// MySQL data-delivery floor. Direct-merge tests call THIS, not MergeScriptHelper.BuildMergeScript, so the
    /// generated SQL matches the running engine (CTE on MariaDB 10.2, JSON_TABLE on 8.0+).</summary>
    protected string BuildMergeScript(IDbCommand command, string databaseName, string tableName, string tableData,
        string keyColumns, bool mergeUpdate, bool mergeDelete, bool disableTriggers, bool tokenizeScripts, string mergeFilter)
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0 (5.7 lacks JSON_TABLE and a recursive-CTE fallback); skipped below the floor.");
        return MergeScriptHelper.BuildMergeScript(Platform, command, databaseName, tableName, tableData, keyColumns,
            mergeUpdate, mergeDelete, disableTriggers, tokenizeScripts, mergeFilter, mySqlServerVersionNum: _serverVersionNum);
    }

    [SetUp]
    public void SetUp()
    {
        _testDb = MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        _testTableName = $"_test_data_{Guid.NewGuid():N}".Substring(0, 30);

        // Detect the target version (major*100+minor) so BuildMergeScript picks the version-adaptive shred.
        using (var vcmd = _connection.CreateCommand())
        {
            vcmd.CommandText = "SELECT VERSION()";
            var version = vcmd.ExecuteScalar()?.ToString() ?? "";
            var parts = version.Split('.');
            _serverVersionNum = parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
                ? major * 100 + minor
                : int.MaxValue;
        }

        // Create test table
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE `{_testDb}`.`{_testTableName}` (
                id INT AUTO_INCREMENT PRIMARY KEY,
                code VARCHAR(20) NOT NULL UNIQUE,
                name VARCHAR(100) NOT NULL,
                value DECIMAL(10,2) DEFAULT 0.00,
                active TINYINT(1) DEFAULT 1,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            )";
        command.ExecuteNonQuery();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{_testTableName}`";
            command.ExecuteNonQuery();
        }
        catch { /* ignore cleanup errors */ }

        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void DeliverTableData_ReplaceType_InsertsNewRows()
    {
        using var command = _connection.CreateCommand();
        var tableData = @"[{""code"":""A001"",""name"":""Item A"",""value"":10.50,""active"":1},{""code"":""B002"",""name"":""Item B"",""value"":20.00,""active"":1}]";

        // Insert/Update/Delete — insert fresh rows into empty target
        var script = BuildMergeScript(command, _testDb,_testTableName,
            tableData, "`code`", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        ExecuteScript(command, script);

        command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

        command.CommandText = $"SELECT name FROM `{_testDb}`.`{_testTableName}` WHERE code = 'A001'";
        Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Item A"));
    }

    [Test]
    public void DeliverTableData_ReplaceType_ReplacesExistingRows()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"INSERT INTO `{_testDb}`.`{_testTableName}` (code, name, value) VALUES ('A001', 'Original', 5.00)";
        command.ExecuteNonQuery();

        var tableData = @"[{""code"":""A001"",""name"":""Updated"",""value"":15.00,""active"":1}]";
        // Insert/Update/Delete — upsert existing rows and delete any not in source
        var script = BuildMergeScript(command, _testDb,_testTableName,
            tableData, "`code`", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        ExecuteScript(command, script);

        command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));

        command.CommandText = $"SELECT name, value FROM `{_testDb}`.`{_testTableName}` WHERE code = 'A001'";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("Updated"));
        Assert.That(reader.GetDecimal(1), Is.EqualTo(15.00m));
    }

    [Test]
    public void DeliverTableData_UpsertType_UpdatesExisting()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"INSERT INTO `{_testDb}`.`{_testTableName}` (code, name, value) VALUES ('A001', 'Original', 5.00)";
        command.ExecuteNonQuery();

        var tableData = @"[{""code"":""A001"",""name"":""Updated"",""value"":15.00,""active"":1},{""code"":""B002"",""name"":""New Item"",""value"":25.00,""active"":1}]";
        // Insert/Update maps to INSERT ON DUPLICATE KEY UPDATE in MySQL
        var script = BuildMergeScript(command, _testDb,_testTableName,
            tableData, "`code`", mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        ExecuteScript(command, script);

        command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

        command.CommandText = $"SELECT name FROM `{_testDb}`.`{_testTableName}` WHERE code = 'A001'";
        Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Updated"));

        command.CommandText = $"SELECT name FROM `{_testDb}`.`{_testTableName}` WHERE code = 'B002'";
        Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("New Item"));
    }

    [Test]
    public void DeliverTableData_InsertType_SkipsExisting()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"INSERT INTO `{_testDb}`.`{_testTableName}` (code, name, value) VALUES ('A001', 'Original', 5.00)";
        command.ExecuteNonQuery();

        var tableData = @"[{""code"":""A001"",""name"":""ShouldNotUpdate"",""value"":99.00,""active"":1},{""code"":""B002"",""name"":""New Item"",""value"":25.00,""active"":1}]";
        // Insert only maps to INSERT IGNORE in MySQL
        var script = BuildMergeScript(command, _testDb,_testTableName,
            tableData, "`code`", mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        ExecuteScript(command, script);

        command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

        command.CommandText = $"SELECT name, value FROM `{_testDb}`.`{_testTableName}` WHERE code = 'A001'";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("Original"));
        Assert.That(reader.GetDecimal(1), Is.EqualTo(5.00m));
    }

    // B3: dynamic XPath is rejected outright on MySQL/MariaDB, so an Xml-encoded delivery is converted to
    // JSON in C# and shredded through the same row source a JSON delivery uses. XML/JSON equivalence is
    // the assertion that matters here, mirroring PostgreSQL's DataDelivery_PostgreSql_XmlEncoding_
    // MatchesJsonEncodingRowForRow: the qty INT column exercises the string-to-INT coercion the converter
    // relies on, and the embedded double quote in name exercises the XML->JSON escaping hop live rather
    // than only in the unit test. Runs on both MySQL and MariaDb — this base class is shared by both.
    [Test]
    public void DataDelivery_XmlEncoding_MatchesJsonEncodingRowForRow()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");

        using var command = _connection.CreateCommand();
        var jsonTable = $"_test_xjson_{Guid.NewGuid():N}".Substring(0, 30);
        var xmlTable = $"_test_xxml_{Guid.NewGuid():N}".Substring(0, 30);
        try
        {
            foreach (var t in new[] { jsonTable, xmlTable })
            {
                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{t}` (
                        code VARCHAR(20) NOT NULL PRIMARY KEY,
                        name VARCHAR(100) NOT NULL,
                        qty INT NOT NULL
                    )";
                command.ExecuteNonQuery();
            }

            var jsonData = "[{\"code\":\"A001\",\"name\":\"He said \\\"hi\\\"\",\"qty\":7}]";
            var xmlData = "<rows><row><c n=\"code\">A001</c><c n=\"name\">He said \"hi\"</c><c n=\"qty\">7</c></row></rows>";

            var jsonScript = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, jsonTable,
                jsonData, "`code`", mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, mySqlServerVersionNum: _serverVersionNum);
            ExecuteScript(command, jsonScript);

            var xmlScript = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, xmlTable,
                xmlData, "`code`", mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, mySqlServerVersionNum: _serverVersionNum,
                contentEncoding: "Xml");
            ExecuteScript(command, xmlScript);

            // Positive assertion first: the anti-join checks below are equivalence proofs, and two empty
            // tables are trivially equivalent — without this, a gated-off delivery or a swallowed failure
            // that left both tables empty would pass while proving the XML route delivered nothing.
            command.CommandText = $"SELECT code, name, qty FROM `{_testDb}`.`{xmlTable}`";
            using (var reader = command.ExecuteReader())
            {
                Assert.That(reader.Read(), Is.True, "The XML delivery must have inserted its row.");
                Assert.Multiple(() =>
                {
                    Assert.That(reader.GetString(0), Is.EqualTo("A001"));
                    Assert.That(reader.GetString(1), Is.EqualTo("He said \"hi\""));
                    Assert.That(reader.GetInt32(2), Is.EqualTo(7),
                        "qty must round-trip as an integer through the converted-JSON row source.");
                });
                Assert.That(reader.Read(), Is.False, "The XML delivery must not have inserted extra rows.");
            }

            // MySQL has no EXCEPT — an anti-join proves the same row-for-row equivalence in both directions.
            command.CommandText = $@"
                SELECT COUNT(*) FROM `{_testDb}`.`{jsonTable}` j
                LEFT JOIN `{_testDb}`.`{xmlTable}` x ON j.code = x.code AND j.name = x.name AND j.qty = x.qty
                WHERE x.code IS NULL";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0),
                "Every JSON-delivered row must be reproduced exactly by the XML delivery.");

            command.CommandText = $@"
                SELECT COUNT(*) FROM `{_testDb}`.`{xmlTable}` x
                LEFT JOIN `{_testDb}`.`{jsonTable}` j ON x.code = j.code AND x.name = j.name AND x.qty = j.qty
                WHERE j.code IS NULL";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0),
                "The XML delivery must not introduce rows the JSON delivery did not produce.");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{jsonTable}`, `{_testDb}`.`{xmlTable}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void DeliverTableData_ViaTemplate_ProcessesTablesWithMergeType()
    {
        using var command = _connection.CreateCommand();

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var contentFilePath = Path.Combine(tempDir, "testdata.json");
        File.WriteAllText(contentFilePath, @"[{""code"":""T001"",""name"":""Template Test"",""value"":99.99,""active"":1}]");

        try
        {
            var template = new Template
            {
                Name = "TestTemplate",
                FilePath = Path.Combine(tempDir, "Template.json")
            };
            template.Tables.Add(new MySqlTable
            {
                Name = _testTableName,
                DataDelivery =
                [
                    new DataDelivery
                    {
                    MergeType = "Insert/Update/Delete",
                    ContentFile = "testdata.json"
                }
                ],
                Columns =
                [
                    new Column { Name = "code", DataType = "VARCHAR(20)" },
                    new Column { Name = "name", DataType = "VARCHAR(100)" },
                    new Column { Name = "value", DataType = "DECIMAL(10,2)" },
                    new Column { Name = "active", DataType = "TINYINT(1)" }
                ]
            });

            var fileWrapper = ProductFileWrapper.GetFromFactory();
            Assert.That(fileWrapper.Exists(contentFilePath), Is.True);

            var tableData = fileWrapper.ReadAllText(contentFilePath);
            Assert.That(tableData, Does.Contain("T001"));

            var mergeType = template.Tables[0].DataDelivery[0].MergeType;
            var mergeUpdate = mergeType.Contains("Update", StringComparison.OrdinalIgnoreCase);
            var mergeDelete = mergeType.Contains("Delete", StringComparison.OrdinalIgnoreCase);
            var script = BuildMergeScript(command, _testDb,_testTableName,
                tableData, "`code`", mergeUpdate, mergeDelete, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            ExecuteScript(command, script);

            command.CommandText = $"SELECT name FROM `{_testDb}`.`{_testTableName}` WHERE code = 'T001'";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Template Test"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void DeliverTableData_TableWithNoMergeType_IsSkipped()
    {
        // Tables without a DataDelivery block should not be processed
        var table = new MySqlTable
        {
            Name = _testTableName,
            DataDelivery = null
        };

        var tablesWithData = new List<Table> { table }
            .Where(t => t.DataDelivery != null &&
                        t.DataDelivery.Any(d => !string.IsNullOrWhiteSpace(d.MergeType) &&
                                     !d.MergeType.Equals("none", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.That(tablesWithData, Is.Empty);
    }

    [Test]
    public void DeliverTableData_MergeTypeNone_IsSkipped()
    {
        var table = new MySqlTable
        {
            Name = _testTableName,
            DataDelivery =
                [
                    new DataDelivery
                    { MergeType = "None", ContentFile = "data.json" }
                ]
        };

        var tablesWithData = new List<Table> { table }
            .Where(t => t.DataDelivery != null &&
                        t.DataDelivery.Any(d => !string.IsNullOrWhiteSpace(d.MergeType) &&
                                     !d.MergeType.Equals("none", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.That(tablesWithData, Is.Empty);
    }

    #region FK Dependency Ordering Tests

    [Test]
    public void DeliverTableData_FKDependencyOrdering_DeliversParentBeforeChild()
    {
        using var command = _connection.CreateCommand();
        var parentTable = $"_test_parent_{Guid.NewGuid():N}".Substring(0, 30);
        var childTable = $"_test_child_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{parentTable}` (
                    id INT PRIMARY KEY,
                    name VARCHAR(100) NOT NULL
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{childTable}` (
                    id INT PRIMARY KEY,
                    parent_id INT NOT NULL,
                    value VARCHAR(100),
                    CONSTRAINT fk_child_parent FOREIGN KEY (parent_id) REFERENCES `{parentTable}` (id)
                )";
            command.ExecuteNonQuery();

            var parentData = @"[{""id"":1,""name"":""Parent A""},{""id"":2,""name"":""Parent B""}]";
            var childData = @"[{""id"":10,""parent_id"":1,""value"":""Child 1""},{""id"":20,""parent_id"":2,""value"":""Child 2""}]";

            var parentScript = BuildMergeScript(command, _testDb,parentTable,
                parentData, "`id`", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);
            ExecuteScript(command, parentScript);

            var childScript = BuildMergeScript(command, _testDb,childTable,
                childData, "`id`", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);
            ExecuteScript(command, childScript);

            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{parentTable}`";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{childTable}`";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{childTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{parentTable}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void DeliverTableData_ChildBeforeParent_FailsWithoutFKOrdering()
    {
        using var command = _connection.CreateCommand();
        var parentTable = $"_test_parent_{Guid.NewGuid():N}".Substring(0, 30);
        var childTable = $"_test_child_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{parentTable}` (
                    id INT PRIMARY KEY,
                    name VARCHAR(100) NOT NULL
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{childTable}` (
                    id INT PRIMARY KEY,
                    parent_id INT NOT NULL,
                    value VARCHAR(100),
                    CONSTRAINT fk_child_parent FOREIGN KEY (parent_id) REFERENCES `{parentTable}` (id)
                )";
            command.ExecuteNonQuery();

            var childData = @"[{""id"":10,""parent_id"":1,""value"":""Child 1""}]";

            // Insert/Update (ON DUPLICATE KEY UPDATE) surfaces FK errors.
            // Insert-only would use INSERT IGNORE which swallows FK violations.
            var childScript = BuildMergeScript(command, _testDb,childTable,
                childData, "`id`", mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            Assert.Throws<MySqlException>(() => ExecuteScript(command, childScript));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{childTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{parentTable}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_OrdersByFKDependencies()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        // End-to-end: DatabaseQuench should order tables by FK and deliver successfully
        // even when child table is listed BEFORE parent in the template.
        lock (FactoryContainer.SharedLockObject)
        {
            using var command = _connection.CreateCommand();
            var parentTable = $"_test_qparent_{Guid.NewGuid():N}".Substring(0, 30);
            var childTable = $"_test_qchild_{Guid.NewGuid():N}".Substring(0, 30);
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Combine(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{parentTable}` (
                        id INT PRIMARY KEY,
                        name VARCHAR(100) NOT NULL
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{childTable}` (
                        id INT PRIMARY KEY,
                        parent_id INT NOT NULL,
                        value VARCHAR(100),
                        CONSTRAINT fk_qchild_parent FOREIGN KEY (parent_id) REFERENCES `{parentTable}` (id)
                    )";
                command.ExecuteNonQuery();

                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "parent.tabledata"),
                    @"[{""id"":1,""name"":""Parent A""},{""id"":2,""name"":""Parent B""}]");
                File.WriteAllText(Path.Combine(tempDir, "child.tabledata"),
                    @"[{""id"":10,""parent_id"":1,""value"":""Child 1""},{""id"":20,""parent_id"":2,""value"":""Child 2""}]");
                File.WriteAllText(Path.Combine(tempDir, "Template.json"), "{}");

                var template = new Template
                {
                    Name = "FKOrderTest",
                    FilePath = Path.Combine(tempDir, "Template.json")
                };

                // Child table listed first — FK ordering should re-order it after parent
                template.Tables.Add(new MySqlTable
                {
                    Name = childTable,
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "child.tabledata"
                    }
                ],
                    ForeignKeys =
                    [
                        new MySqlForeignKey
                        {
                            Name = "fk_qchild_parent",
                            Columns = "`parent_id`",
                            RelatedTable = parentTable,
                            RelatedColumns = "`id`"
                        }
                    ]
                });
                template.Tables.Add(new MySqlTable
                {
                    Name = parentTable,
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "parent.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{parentTable}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{childTable}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));
            }
            finally
            {
                command.CommandText = "SET FOREIGN_KEY_CHECKS = 0";
                command.ExecuteNonQuery();
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{childTable}`";
                command.ExecuteNonQuery();
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{parentTable}`";
                command.ExecuteNonQuery();
                command.CommandText = "SET FOREIGN_KEY_CHECKS = 1";
                command.ExecuteNonQuery();

                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_HandlesErrorAndContinues()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        // A failing table shouldn't prevent other tables from being delivered.
        lock (FactoryContainer.SharedLockObject)
        {
            using var command = _connection.CreateCommand();
            var goodTable = $"_test_good_{Guid.NewGuid():N}".Substring(0, 30);
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Combine(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{goodTable}` (
                        id INT PRIMARY KEY,
                        name VARCHAR(100) NOT NULL
                    )";
                command.ExecuteNonQuery();

                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "good.tabledata"),
                    @"[{""id"":1,""name"":""Good Data""}]");
                File.WriteAllText(Path.Combine(tempDir, "bad.tabledata"),
                    @"[{""id"":1,""name"":""Bad Data""}]");
                File.WriteAllText(Path.Combine(tempDir, "Template.json"), "{}");

                var template = new Template
                {
                    Name = "ErrorHandlingTest",
                    FilePath = Path.Combine(tempDir, "Template.json")
                };

                template.Tables.Add(new MySqlTable
                {
                    Name = "_nonexistent_table_xyz",
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "bad.tabledata"
                    }
                ]
                });
                template.Tables.Add(new MySqlTable
                {
                    Name = goodTable,
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "good.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                // "Continues" now means "delivers what it can, then fails the run" (#334):
                // the good table still receives its data even though the bad table failed...
                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{goodTable}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));
                // ...but the permanently-failed delivery must fail the deploy (exit 2), not exit 0.
                Assert.That(quench.QuenchSuccessful, Is.False,
                    "a permanently-failed DataDelivery must fail the quench");
            }
            finally
            {
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{goodTable}`";
                command.ExecuteNonQuery();

                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_FailedDelivery_FailsTheQuench_MySql()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        // #334: a DataDelivery that errors at execution on MySQL must fail the quench (exit 2),
        // consistent with SQL Server / PostgreSQL — not exit 0 with the failure only logged.
        lock (FactoryContainer.SharedLockObject)
        {
            using var command = _connection.CreateCommand();
            var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Join(tempDir, "bad.tabledata"),
                    @"[{""id"":1,""name"":""Bad Data""}]");
                File.WriteAllText(Path.Join(tempDir, "Template.json"), "{}");

                var template = new Template
                {
                    Name = "FailedDeliveryTest",
                    FilePath = Path.Join(tempDir, "Template.json")
                };

                // Delivery targets a table that does not exist -> execution throws -> permanent failure.
                template.Tables.Add(new MySqlTable
                {
                    Name = "_nonexistent_table_xyz",
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "bad.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.False, "a failed DataDelivery must fail the quench");
            }
            finally
            {
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_HandlesCircularFKDependencies()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        // Simulates store<->staff circular dependency: store.manager_id->staff, staff.store_id->store.
        // Two-pass approach: required edge first, deferred nullable edge filled in pass 2.
        lock (FactoryContainer.SharedLockObject)
        {
            using var command = _connection.CreateCommand();
            var storeTable = $"_test_store_{Guid.NewGuid():N}".Substring(0, 30);
            var staffTable = $"_test_staff_{Guid.NewGuid():N}".Substring(0, 30);
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Combine(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{storeTable}` (
                        store_id INT PRIMARY KEY,
                        name VARCHAR(100) NOT NULL,
                        manager_id INT
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{staffTable}` (
                        staff_id INT PRIMARY KEY,
                        name VARCHAR(100) NOT NULL,
                        store_id INT NOT NULL,
                        CONSTRAINT fk_staff_store FOREIGN KEY (store_id) REFERENCES `{storeTable}` (store_id)
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    ALTER TABLE `{_testDb}`.`{storeTable}`
                    ADD CONSTRAINT fk_store_manager FOREIGN KEY (manager_id) REFERENCES `{staffTable}` (staff_id)";
                command.ExecuteNonQuery();

                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "store.tabledata"),
                    $@"[{{""store_id"":1,""name"":""Main Store"",""manager_id"":1}},{{""store_id"":2,""name"":""Branch"",""manager_id"":2}}]");
                File.WriteAllText(Path.Combine(tempDir, "staff.tabledata"),
                    $@"[{{""staff_id"":1,""name"":""Alice"",""store_id"":1}},{{""staff_id"":2,""name"":""Bob"",""store_id"":2}}]");
                File.WriteAllText(Path.Combine(tempDir, "Template.json"), "{}");

                var template = new Template
                {
                    Name = "CircularFKTest",
                    FilePath = Path.Combine(tempDir, "Template.json")
                };

                // store.manager_id (NULLABLE) -> staff = deferred edge
                // staff.store_id (NOT NULL) -> store = required edge
                template.Tables.Add(new MySqlTable
                {
                    Name = storeTable,
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update",
                        ContentFile = "store.tabledata"
                    }
                ],
                    Columns =
                    [
                        new Column { Name = "store_id", DataType = "INT" },
                        new Column { Name = "name", DataType = "VARCHAR(100)" },
                        new Column { Name = "manager_id", DataType = "INT", Nullable = true }
                    ],
                    ForeignKeys =
                    [
                        new MySqlForeignKey
                        {
                            Name = "fk_store_manager",
                            Columns = "`manager_id`",
                            RelatedTable = staffTable,
                            RelatedColumns = "`staff_id`"
                        }
                    ]
                });
                template.Tables.Add(new MySqlTable
                {
                    Name = staffTable,
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update",
                        ContentFile = "staff.tabledata"
                    }
                ],
                    Columns =
                    [
                        new Column { Name = "staff_id", DataType = "INT" },
                        new Column { Name = "name", DataType = "VARCHAR(100)" },
                        new Column { Name = "store_id", DataType = "INT" }
                    ],
                    ForeignKeys =
                    [
                        new MySqlForeignKey
                        {
                            Name = "fk_staff_store",
                            Columns = "`store_id`",
                            RelatedTable = storeTable,
                            RelatedColumns = "`store_id`"
                        }
                    ]
                });

                RegisterTargetConfig();

                // updateTables: true so ForeignKeyQuench runs after data delivery
                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: false, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: true,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{storeTable}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{staffTable}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                // Pass 2 should have filled store.manager_id
                command.CommandText = $"SELECT manager_id FROM `{_testDb}`.`{storeTable}` WHERE store_id = 1";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));
                command.CommandText = $"SELECT manager_id FROM `{_testDb}`.`{storeTable}` WHERE store_id = 2";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                // Both FKs should still exist — two-pass approach never drops them
                command.CommandText = $@"
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                    WHERE TABLE_SCHEMA = '{_testDb}'
                      AND TABLE_NAME IN ('{storeTable}', '{staffTable}')
                      AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2), "Both FKs should exist — never dropped");
            }
            finally
            {
                command.CommandText = "SET FOREIGN_KEY_CHECKS = 0";
                command.ExecuteNonQuery();
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{staffTable}`";
                command.ExecuteNonQuery();
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{storeTable}`";
                command.ExecuteNonQuery();
                command.CommandText = "SET FOREIGN_KEY_CHECKS = 1";
                command.ExecuteNonQuery();

                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    // A mixed-case parent must still be found where the server folds table names and reports them in lowercase.
    [TestCase("_test_rparent_")]
    [TestCase("_Test_RParent_")]
    public void DeliverTableData_ViaQuench_FailsFastOnReplaceCascade(string parentPrefix)
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        // A table using MergeType=Insert/Update/Delete that is referenced by an ON DELETE CASCADE FK
        // should fail pre-flight validation before any data is delivered.
        lock (FactoryContainer.SharedLockObject)
        {
            using var command = _connection.CreateCommand();
            var parentTable = $"{parentPrefix}{Guid.NewGuid():N}".Substring(0, 30);
            var childTable = $"_test_rchild_{Guid.NewGuid():N}".Substring(0, 30);
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Combine(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{parentTable}` (
                        id INT PRIMARY KEY,
                        name VARCHAR(100) NOT NULL
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    CREATE TABLE `{_testDb}`.`{childTable}` (
                        id INT PRIMARY KEY,
                        parent_id INT NOT NULL,
                        CONSTRAINT fk_cascade FOREIGN KEY (parent_id) REFERENCES `{parentTable}` (id) ON DELETE CASCADE
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $"INSERT INTO `{_testDb}`.`{parentTable}` VALUES (1, 'Existing')";
                command.ExecuteNonQuery();
                command.CommandText = $"INSERT INTO `{_testDb}`.`{childTable}` VALUES (10, 1)";
                command.ExecuteNonQuery();

                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "parent.tabledata"),
                    @"[{""id"":1,""name"":""Updated""}]");
                File.WriteAllText(Path.Combine(tempDir, "Template.json"), "{}");

                var template = new Template
                {
                    Name = "CascadeTest",
                    FilePath = Path.Combine(tempDir, "Template.json")
                };

                template.Tables.Add(new MySqlTable
                {
                    Name = parentTable,
                    DataDelivery =
                [
                    new DataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "parent.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.False, "Should fail due to Insert/Update/Delete+CASCADE");

                // Child data must NOT have been cascade-deleted — pre-flight check prevented delivery
                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{childTable}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));
            }
            finally
            {
                command.CommandText = "SET FOREIGN_KEY_CHECKS = 0";
                command.ExecuteNonQuery();
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{childTable}`";
                command.ExecuteNonQuery();
                command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{parentTable}`";
                command.ExecuteNonQuery();
                command.CommandText = "SET FOREIGN_KEY_CHECKS = 1";
                command.ExecuteNonQuery();

                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    #endregion

    #region ShouldApplyExpression Gating and Variant Tests (#278)

    [Test]
    public void DeliverTableData_ViaQuench_GatedDelivery_AppliesWhenTrue()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        lock (FactoryContainer.SharedLockObject)
        {
            var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Join(tempDir, "gated.tabledata"),
                    @"[{""code"":""G001"",""name"":""Gated Row"",""value"":1.00,""active"":1}]");
                File.WriteAllText(Path.Join(tempDir, "Template.json"), "{}");

                var template = new Template { Name = "GateTrueTest", FilePath = Path.Join(tempDir, "Template.json") };
                template.Tables.Add(new MySqlTable
                {
                    Name = _testTableName,
                    DataDelivery =
                    [
                        new DataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "gated.tabledata",
                            MatchColumns = "`code`",
                            ShouldApplyExpression = $"DATABASE() = '{_testDb}'",
                            VariantName = "same-db"
                        }
                    ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));
            }
            finally
            {
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_GatedDelivery_SkippedWhenFalse_LogsSkip()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        lock (FactoryContainer.SharedLockObject)
        {
            var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();
            var progressLog = Substitute.For<ILog>();

            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Join(tempDir, "gated.tabledata"),
                    @"[{""code"":""G002"",""name"":""Should Not Land"",""value"":1.00,""active"":1}]");
                File.WriteAllText(Path.Join(tempDir, "Template.json"), "{}");

                var template = new Template { Name = "GateFalseTest", FilePath = Path.Join(tempDir, "Template.json") };
                template.Tables.Add(new MySqlTable
                {
                    Name = _testTableName,
                    DataDelivery =
                    [
                        new DataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "gated.tabledata",
                            MatchColumns = "`code`",
                            ShouldApplyExpression = "DATABASE() = 'nosuchdatabase_xyz_278'",
                            VariantName = "wrong-env"
                        }
                    ]
                });

                RegisterTargetConfig();
                LogFactory.Register("ProgressLog", progressLog);

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0));

                progressLog.Received().Info(Arg.Is<string>(s =>
                    s.Contains("Skipping data delivery") && s.Contains(_testTableName) && s.Contains("wrong-env")));
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_TwoVariants_OnlyActiveVariantApplies()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        lock (FactoryContainer.SharedLockObject)
        {
            var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Join(tempDir, "variantA.tabledata"),
                    @"[{""code"":""VA1"",""name"":""Variant A Row"",""value"":1.00,""active"":1}]");
                File.WriteAllText(Path.Join(tempDir, "variantB.tabledata"),
                    @"[{""code"":""VB1"",""name"":""Variant B Row"",""value"":2.00,""active"":1}]");
                File.WriteAllText(Path.Join(tempDir, "Template.json"), "{}");

                var template = new Template { Name = "VariantTest", FilePath = Path.Join(tempDir, "Template.json") };
                template.Tables.Add(new MySqlTable
                {
                    Name = _testTableName,
                    DataDelivery =
                    [
                        new DataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "variantA.tabledata",
                            MatchColumns = "`code`",
                            ShouldApplyExpression = $"DATABASE() = '{_testDb}'",
                            VariantName = "active-variant"
                        },
                        new DataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "variantB.tabledata",
                            MatchColumns = "`code`",
                            ShouldApplyExpression = "DATABASE() = 'nosuchdatabase_xyz_278'",
                            VariantName = "inactive-variant"
                        }
                    ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1), "Only the active variant's row should have landed.");

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}` WHERE code = 'VA1'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}` WHERE code = 'VB1'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0));
            }
            finally
            {
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_DisjointMergeFilters_EachDeliveryDeletesOnlyOwnSlice()
    {
        if (!SupportsDataDelivery)
            Assert.Ignore("Data delivery requires MySQL 8.0; skipped below the floor.");
        lock (FactoryContainer.SharedLockObject)
        {
            var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Join(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                using (var seedCommand = _connection.CreateCommand())
                {
                    seedCommand.CommandText = $@"
                        INSERT INTO `{_testDb}`.`{_testTableName}` (code, name, value, active) VALUES
                        ('STALE1', 'Stale Slice 1', 0.00, 1),
                        ('STALE2', 'Stale Slice 2', 0.00, 2)";
                    seedCommand.ExecuteNonQuery();
                }

                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Join(tempDir, "slice1.tabledata"),
                    @"[{""code"":""S1A"",""name"":""Slice 1 A"",""value"":1.00,""active"":1},{""code"":""S1B"",""name"":""Slice 1 B"",""value"":1.00,""active"":1}]");
                File.WriteAllText(Path.Join(tempDir, "slice2.tabledata"),
                    @"[{""code"":""S2A"",""name"":""Slice 2 A"",""value"":2.00,""active"":2}]");
                File.WriteAllText(Path.Join(tempDir, "Template.json"), "{}");

                var template = new Template { Name = "DisjointMergeFilterTest", FilePath = Path.Join(tempDir, "Template.json") };
                template.Tables.Add(new MySqlTable
                {
                    Name = _testTableName,
                    DataDelivery =
                    [
                        new DataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "slice1.tabledata",
                            MatchColumns = "`code`",
                            MergeFilter = "`active` = 1",
                            VariantName = "slice-1"
                        },
                        new DataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "slice2.tabledata",
                            MatchColumns = "`code`",
                            MergeFilter = "`active` = 2",
                            VariantName = "slice-2"
                        }
                    ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform };
                var quench = new DatabaseQuench("127.0.0.1", product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "0", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}`";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(3),
                    "Each delivery's delete must be scoped to its own MergeFilter slice — neither should wipe the other's rows.");

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}` WHERE code = 'STALE1'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0), "Stale row in slice 1 must be deleted by slice 1's delivery.");

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}` WHERE code = 'STALE2'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0), "Stale row in slice 2 must be deleted by slice 2's delivery.");

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}` WHERE active = 1";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2), "Slice 1 rows (S1A, S1B) must survive.");

                command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{_testTableName}` WHERE active = 2";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1), "Slice 2 row (S2A) must survive.");
            }
            finally
            {
                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    #endregion

    #region Helper Methods

    private void RegisterTargetConfig()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var configBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Target:User"] = config[$"{ConfigPrefix}:User"] ?? config["Target:User"] ?? "TestUser",
                ["Target:Password"] = config[$"{ConfigPrefix}:Password"] ?? config["Target:Password"] ?? "aCa2d805-41E5@40c4!98e7#92F93zzxo176",
                ["Target:Port"] = config[$"{ConfigPrefix}:Port"] ?? config["Target:Port"] ?? "3306",
                ["Target:Server"] = config[$"{ConfigPrefix}:Server"] ?? config["Target:Server"] ?? "127.0.0.1"
            }!)
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(configBuilder);
    }

    private static void ExecuteScript(IDbCommand command, string script)
    {
        foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            command.CommandText = batch;
            command.ExecuteNonQuery();
        }
    }

    #endregion
}
