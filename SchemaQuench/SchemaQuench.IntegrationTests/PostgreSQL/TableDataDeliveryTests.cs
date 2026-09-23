// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NSubstitute;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Integration tests for table data delivery functionality against PostgreSQL.
/// Exercises MergeScriptHelper directly for single-table merge scenarios and
/// DatabaseQuench for multi-table scenarios (FK ordering, circular FKs,
/// error-continue semantics). Uses dynamically created test databases via FixtureSetup.
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class TableDataDeliveryTests
{
    private IDbConnection _connection = null!;
    private string _testTableName = null!;
    private string _testDb = null!;
    // The container's real PG major, threaded into BuildMergeScript so these direct-helper tests
    // exercise the version-correct path (MERGE on 15+, a manual INSERT/UPDATE upsert below 15) rather than
    // defaulting to 0 (= modern), which emits MERGE and fails on a genuine PG14 container.
    private int _pgServerVersionNum;
    private const string SchemaName = "public";

    [SetUp]
    public void SetUp()
    {
        _testDb = Schema.IntegrationTests.PostgreSQL.FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(Schema.IntegrationTests.PostgreSQL.FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _testTableName = $"_test_data_{Guid.NewGuid():N}".Substring(0, 30);

        using (var verCmd = _connection.CreateCommand())
        {
            verCmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
            _pgServerVersionNum = Convert.ToInt32(verCmd.ExecuteScalar());
        }

        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE ""{SchemaName}"".""{_testTableName}"" (
                ""id"" SERIAL PRIMARY KEY,
                ""code"" VARCHAR(20) NOT NULL UNIQUE,
                ""name"" VARCHAR(100) NOT NULL,
                ""value"" DECIMAL(10,2) DEFAULT 0.00,
                ""active"" SMALLINT DEFAULT 1,
                ""created_at"" TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            )";
        command.ExecuteNonQuery();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{_testTableName}"" CASCADE";
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

        var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,_testTableName,
            tableData, @"""code""", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

        command.CommandText = script;
        command.ExecuteNonQuery();

        command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

        command.CommandText = $@"SELECT ""name"" FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'A001'";
        Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Item A"));
    }

    [Test]
    public void DeliverTableData_ReplaceType_ReplacesExistingRows()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"INSERT INTO ""{SchemaName}"".""{_testTableName}"" (""code"", ""name"", ""value"") VALUES ('A001', 'Original', 5.00)";
        command.ExecuteNonQuery();

        var tableData = @"[{""code"":""A001"",""name"":""Updated"",""value"":15.00,""active"":1}]";
        var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,_testTableName,
            tableData, @"""code""", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

        command.CommandText = script;
        command.ExecuteNonQuery();

        command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));

        command.CommandText = $@"SELECT ""name"", ""value"" FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'A001'";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("Updated"));
        Assert.That(reader.GetDecimal(1), Is.EqualTo(15.00m));
    }

    [Test]
    public void DeliverTableData_UpsertType_UpdatesExisting()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"INSERT INTO ""{SchemaName}"".""{_testTableName}"" (""code"", ""name"", ""value"") VALUES ('A001', 'Original', 5.00)";
        command.ExecuteNonQuery();

        var tableData = @"[{""code"":""A001"",""name"":""Updated"",""value"":15.00,""active"":1},{""code"":""B002"",""name"":""New Item"",""value"":25.00,""active"":1}]";
        var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,_testTableName,
            tableData, @"""code""", mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

        command.CommandText = script;
        command.ExecuteNonQuery();

        command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

        command.CommandText = $@"SELECT ""name"" FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'A001'";
        Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Updated"));

        command.CommandText = $@"SELECT ""name"" FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'B002'";
        Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("New Item"));
    }

    [Test]
    public void DeliverTableData_InsertType_SkipsExisting()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $@"INSERT INTO ""{SchemaName}"".""{_testTableName}"" (""code"", ""name"", ""value"") VALUES ('A001', 'Original', 5.00)";
        command.ExecuteNonQuery();

        var tableData = @"[{""code"":""A001"",""name"":""ShouldNotUpdate"",""value"":99.00,""active"":1},{""code"":""B002"",""name"":""New Item"",""value"":25.00,""active"":1}]";
        var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,_testTableName,
            tableData, @"""code""", mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

        command.CommandText = script;
        command.ExecuteNonQuery();

        command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
        Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

        command.CommandText = $@"SELECT ""name"", ""value"" FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'A001'";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("Original"));
        Assert.That(reader.GetDecimal(1), Is.EqualTo(5.00m));
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
            template.Tables.Add(new PostgreSqlTable
            {
                Name = _testTableName,
                Schema = SchemaName,
                DataDelivery =
                [
                    new PostgreSqlDataDelivery
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
                    new Column { Name = "active", DataType = "SMALLINT" }
                ]
            });

            var fileWrapper = ProductFileWrapper.GetFromFactory();
            Assert.That(fileWrapper.Exists(contentFilePath), Is.True);

            var tableData = fileWrapper.ReadAllText(contentFilePath);
            Assert.That(tableData, Does.Contain("T001"));

            var mergeType = template.Tables[0].DataDelivery[0].MergeType;
            var mergeUpdate = mergeType.Contains("Update", StringComparison.OrdinalIgnoreCase);
            var mergeDelete = mergeType.Contains("Delete", StringComparison.OrdinalIgnoreCase);
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,_testTableName,
                tableData, @"""code""", mergeUpdate, mergeDelete, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT ""name"" FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'T001'";
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
        var table = new PostgreSqlTable
        {
            Name = _testTableName,
            Schema = SchemaName,
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
        var table = new PostgreSqlTable
        {
            Name = _testTableName,
            Schema = SchemaName,
            DataDelivery =
                [
                    new PostgreSqlDataDelivery
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

    // Proper PG<15 upsert (manual UPDATE + INSERT WHERE NOT EXISTS): matches a NULL-valued key and needs
    // no unique constraint — both cases the ON CONFLICT fallback could not handle (duplicate rows / "no
    // unique or exclusion constraint matching the ON CONFLICT specification"). The table has NO unique
    // constraint and a NULL-valued '*' NULL-safe key.
    [Test]
    public void NullSafeKey_BelowPg15_ManualUpsert_MatchesNullKey_NoDuplicates()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_nullkey_{Guid.NewGuid():N}".Substring(0, 30);
        try
        {
            command.CommandText = $@"CREATE TABLE ""{SchemaName}"".""{tableName}"" (""code"" INT NULL, ""name"" VARCHAR(50));";
            command.ExecuteNonQuery();

            var data = @"[{""code"":null,""name"":""Alpha""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName, tableName,
                data, @"*""code""", mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: 14);

            command.CommandText = script; command.ExecuteNonQuery();               // first delivery: insert
            command.CommandText = script; command.ExecuteNonQuery();               // re-deliver: must MATCH the NULL-keyed row

            command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{tableName}"";";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1),
                "a NULL-safe key upsert must match the existing NULL-keyed row (no duplicate) on PG < 15");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{tableName}"";";
            command.ExecuteNonQuery();
        }
    }

    // B1: XML/JSON equivalence is the assertion that matters here — the two encodings carrying the same
    // rows must produce identical table contents. The qty INT column is deliberate: a text-only payload
    // would hide type-coercion drift between xmltable()'s COLUMNS typing and the JSON row source's casts.
    [Test]
    public void DataDelivery_PostgreSql_XmlEncoding_MatchesJsonEncodingRowForRow()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"pgxml_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Join(tempDir, "json.tabledata"),
            @"[{""code"":""A001"",""name"":""Anvil"",""qty"":7}]");
        File.WriteAllText(Path.Join(tempDir, "xml.tabledata"),
            @"<rows><row><c n=""code"">A001</c><c n=""name"">Anvil</c><c n=""qty"">7</c></row></rows>");

        var jsonTable = $"_test_pgxml_json_{Guid.NewGuid():N}".Substring(0, 30);
        var xmlTable = $"_test_pgxml_xml_{Guid.NewGuid():N}".Substring(0, 30);
        try
        {
            DeliverOneEncoding(tempDir, "json.tabledata", null, jsonTable);
            DeliverOneEncoding(tempDir, "xml.tabledata", "Xml", xmlTable);

            using var c = _connection.CreateCommand();

            // Positive assertion first: the two EXCEPT checks below are equivalence proofs, and two
            // empty tables are trivially equivalent — without this, a gated-off delivery or a swallowed
            // failure that left both tables empty would pass the equivalence checks while proving the XML
            // shred delivered nothing. Assert on the XML table specifically (the path under test), and
            // assert qty as an integer so the type-coercion point the payload was designed to exercise is
            // actually checked, not just present.
            c.CommandText = $@"SELECT code, name, qty FROM ""{SchemaName}"".""{xmlTable}""";
            using (var reader = c.ExecuteReader())
            {
                Assert.That(reader.Read(), Is.True, "The XML delivery must have inserted its row.");
                Assert.Multiple(() =>
                {
                    Assert.That(reader.GetString(0), Is.EqualTo("A001"));
                    Assert.That(reader.GetString(1), Is.EqualTo("Anvil"));
                    Assert.That(reader.GetInt32(2), Is.EqualTo(7),
                        "qty must round-trip as an integer through xmltable()'s COLUMNS typing.");
                });
                Assert.That(reader.Read(), Is.False, "The XML delivery must not have inserted extra rows.");
            }

            c.CommandText = $@"
                SELECT COUNT(*) FROM (
                    SELECT code, name, qty FROM ""{SchemaName}"".""{jsonTable}""
                    EXCEPT
                    SELECT code, name, qty FROM ""{SchemaName}"".""{xmlTable}""
                ) diff";
            Assert.That(Convert.ToInt32(c.ExecuteScalar()), Is.EqualTo(0),
                "Every JSON-delivered row must be reproduced exactly by the XML delivery.");

            c.CommandText = $@"
                SELECT COUNT(*) FROM (
                    SELECT code, name, qty FROM ""{SchemaName}"".""{xmlTable}""
                    EXCEPT
                    SELECT code, name, qty FROM ""{SchemaName}"".""{jsonTable}""
                ) diff";
            Assert.That(Convert.ToInt32(c.ExecuteScalar()), Is.EqualTo(0),
                "The XML delivery must not introduce rows the JSON delivery did not produce.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
            using var dropCmd = _connection.CreateCommand();
            dropCmd.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{jsonTable}"", ""{SchemaName}"".""{xmlTable}"";";
            dropCmd.ExecuteNonQuery();
        }
    }

    // Mirrors SqlServer/TableDataDeliveryTests.cs's slice-2 DataDeliveryContext shape — Platform =
    // "PostgreSQL", a PostgreSQL-flavored ScriptHelper, and PostgreSqlServerVersionNum threaded from the
    // live container (rather than defaulting to 0 = modern) so this exercises the version-correct MERGE
    // vs. manual-upsert path on whatever PG major the integration container is actually running.
    private void DeliverOneEncoding(string tempDir, string contentFile, string contentEncoding, string tableName)
    {
        using (var createCmd = _connection.CreateCommand())
        {
            createCmd.CommandText = $@"
                CREATE TABLE ""{SchemaName}"".""{tableName}"" (
                    ""code"" VARCHAR(20) NOT NULL PRIMARY KEY,
                    ""name"" VARCHAR(100) NOT NULL,
                    ""qty"" INT NOT NULL
                )";
            createCmd.ExecuteNonQuery();
        }

        var table = new PostgreSqlTable
        {
            Name = tableName,
            Schema = SchemaName,
            Columns =
            [
                new Column { Name = "code", DataType = "VARCHAR(20)" },
                new Column { Name = "name", DataType = "VARCHAR(100)" },
                new Column { Name = "qty", DataType = "INT" }
            ],
            DataDelivery =
            [
                new DataDelivery { MergeType = "Insert", ContentFile = contentFile, ContentEncoding = contentEncoding }
            ]
        };

        using var deliverCmd = _connection.CreateCommand();
        var context = new DataDeliveryContext
        {
            Tables = new List<IDeliverableTable> { table },
            Platform = "PostgreSQL",
            Command = deliverCmd,
            DatabaseName = _testDb,
            TemplateRootPath = tempDir,
            ScriptHelper = new MergeScriptHelperAdapter(Platform.PostgreSQL),
            ReadFileContent = File.ReadAllText,
            ExecuteScript = (_, script) =>
            {
                using var ec = _connection.CreateCommand();
                ec.CommandText = script;
                ec.ExecuteNonQuery();
            },
            ProgressLog = _ => { },
            ProgressLogError = _ => { },
            PostgreSqlServerVersionNum = _pgServerVersionNum
        };

        DataDeliveryProcessor.GetFromFactory().DeliverTables(context);
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
                CREATE TABLE ""{SchemaName}"".""{parentTable}"" (
                    ""id"" INT PRIMARY KEY,
                    ""name"" VARCHAR(100) NOT NULL
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                CREATE TABLE ""{SchemaName}"".""{childTable}"" (
                    ""id"" INT PRIMARY KEY,
                    ""parent_id"" INT NOT NULL,
                    ""value"" VARCHAR(100),
                    CONSTRAINT ""fk_child_parent"" FOREIGN KEY (""parent_id"") REFERENCES ""{SchemaName}"".""{parentTable}"" (""id"")
                )";
            command.ExecuteNonQuery();

            var parentData = @"[{""id"":1,""name"":""Parent A""},{""id"":2,""name"":""Parent B""}]";
            var childData = @"[{""id"":10,""parent_id"":1,""value"":""Child 1""},{""id"":20,""parent_id"":2,""value"":""Child 2""}]";

            var parentScript = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,parentTable,
                parentData, @"""id""", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);
            command.CommandText = parentScript;
            command.ExecuteNonQuery();

            var childScript = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,childTable,
                childData, @"""id""", mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);
            command.CommandText = childScript;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{parentTable}""";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

            command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{childTable}""";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{childTable}"" CASCADE";
            command.ExecuteNonQuery();
            command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{parentTable}"" CASCADE";
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
                CREATE TABLE ""{SchemaName}"".""{parentTable}"" (
                    ""id"" INT PRIMARY KEY,
                    ""name"" VARCHAR(100) NOT NULL
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                CREATE TABLE ""{SchemaName}"".""{childTable}"" (
                    ""id"" INT PRIMARY KEY,
                    ""parent_id"" INT NOT NULL,
                    ""value"" VARCHAR(100),
                    CONSTRAINT ""fk_child_parent"" FOREIGN KEY (""parent_id"") REFERENCES ""{SchemaName}"".""{parentTable}"" (""id"")
                )";
            command.ExecuteNonQuery();

            var childData = @"[{""id"":10,""parent_id"":1,""value"":""Child 1""}]";

            var childScript = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, SchemaName,childTable,
                childData, @"""id""", mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

            // Assert.Catch matches NpgsqlException or derived PostgresException
            Assert.Catch<NpgsqlException>(() =>
            {
                command.CommandText = childScript;
                command.ExecuteNonQuery();
            });
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{childTable}"" CASCADE";
            command.ExecuteNonQuery();
            command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{parentTable}"" CASCADE";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_OrdersByFKDependencies()
    {
        // End-to-end: DatabaseQuench orders tables by FK and delivers successfully
        // even when child is listed BEFORE parent in the template.
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
                    CREATE TABLE ""{SchemaName}"".""{parentTable}"" (
                        ""id"" INT PRIMARY KEY,
                        ""name"" VARCHAR(100) NOT NULL
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    CREATE TABLE ""{SchemaName}"".""{childTable}"" (
                        ""id"" INT PRIMARY KEY,
                        ""parent_id"" INT NOT NULL,
                        ""value"" VARCHAR(100),
                        CONSTRAINT ""fk_qchild_parent"" FOREIGN KEY (""parent_id"") REFERENCES ""{SchemaName}"".""{parentTable}"" (""id"")
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

                template.Tables.Add(new PostgreSqlTable
                {
                    Name = childTable,
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "child.tabledata"
                    }
                ],
                    ForeignKeys =
                    [
                        new PostgreSqlForeignKey
                        {
                            Name = "fk_qchild_parent",
                            Columns = @"""parent_id""",
                            RelatedTable = parentTable,
                            RelatedColumns = @"""id"""
                        }
                    ]
                });
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = parentTable,
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "parent.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{parentTable}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{childTable}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));
            }
            finally
            {
                command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{childTable}"" CASCADE";
                command.ExecuteNonQuery();
                command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{parentTable}"" CASCADE";
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
                    CREATE TABLE ""{SchemaName}"".""{goodTable}"" (
                        ""id"" INT PRIMARY KEY,
                        ""name"" VARCHAR(100) NOT NULL
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

                template.Tables.Add(new PostgreSqlTable
                {
                    Name = "_nonexistent_table_xyz",
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "bad.tabledata"
                    }
                ]
                });
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = goodTable,
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "good.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                // "Continues" now means "delivers what it can, then fails the run" (#334):
                // the good table still receives its data even though the bad table failed...
                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{goodTable}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));
                // ...but the permanently-failed delivery must fail the deploy (exit 2), not exit 0.
                Assert.That(quench.QuenchSuccessful, Is.False,
                    "a permanently-failed DataDelivery must fail the quench");
            }
            finally
            {
                command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{goodTable}"" CASCADE";
                command.ExecuteNonQuery();

                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_FailedDelivery_FailsTheQuench_PostgreSql()
    {
        // #334: a DataDelivery that errors at execution on PostgreSQL must fail the quench (exit 2) —
        // not exit 0 with the failure only logged.
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
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = "_nonexistent_table_xyz",
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "bad.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
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
                    CREATE TABLE ""{SchemaName}"".""{storeTable}"" (
                        ""store_id"" INT PRIMARY KEY,
                        ""name"" VARCHAR(100) NOT NULL,
                        ""manager_id"" INT
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    CREATE TABLE ""{SchemaName}"".""{staffTable}"" (
                        ""staff_id"" INT PRIMARY KEY,
                        ""name"" VARCHAR(100) NOT NULL,
                        ""store_id"" INT NOT NULL,
                        CONSTRAINT ""fk_staff_store"" FOREIGN KEY (""store_id"") REFERENCES ""{SchemaName}"".""{storeTable}"" (""store_id"")
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    ALTER TABLE ""{SchemaName}"".""{storeTable}""
                    ADD CONSTRAINT ""fk_store_manager"" FOREIGN KEY (""manager_id"") REFERENCES ""{SchemaName}"".""{staffTable}"" (""staff_id"")";
                command.ExecuteNonQuery();

                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "store.tabledata"),
                    @"[{""store_id"":1,""name"":""Main Store"",""manager_id"":1},{""store_id"":2,""name"":""Branch"",""manager_id"":2}]");
                File.WriteAllText(Path.Combine(tempDir, "staff.tabledata"),
                    @"[{""staff_id"":1,""name"":""Alice"",""store_id"":1},{""staff_id"":2,""name"":""Bob"",""store_id"":2}]");
                File.WriteAllText(Path.Combine(tempDir, "Template.json"), "{}");

                var template = new Template
                {
                    Name = "CircularFKTest",
                    FilePath = Path.Combine(tempDir, "Template.json")
                };

                template.Tables.Add(new PostgreSqlTable
                {
                    Name = storeTable,
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
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
                        new PostgreSqlForeignKey
                        {
                            Name = "fk_store_manager",
                            Columns = @"""manager_id""",
                            RelatedTable = staffTable,
                            RelatedColumns = @"""staff_id"""
                        }
                    ]
                });
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = staffTable,
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
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
                        new PostgreSqlForeignKey
                        {
                            Name = "fk_staff_store",
                            Columns = @"""store_id""",
                            RelatedTable = storeTable,
                            RelatedColumns = @"""store_id"""
                        }
                    ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{storeTable}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{staffTable}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                command.CommandText = $@"SELECT ""manager_id"" FROM ""{SchemaName}"".""{storeTable}"" WHERE ""store_id"" = 1";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));
                command.CommandText = $@"SELECT ""manager_id"" FROM ""{SchemaName}"".""{storeTable}"" WHERE ""store_id"" = 2";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

                command.CommandText = $@"
                    SELECT COUNT(*) FROM information_schema.table_constraints
                    WHERE table_schema = '{SchemaName}'
                      AND table_name IN ('{storeTable}', '{staffTable}')
                      AND constraint_type = 'FOREIGN KEY'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2), "Both FKs should exist — never dropped");
            }
            finally
            {
                command.CommandText = $@"ALTER TABLE IF EXISTS ""{SchemaName}"".""{storeTable}"" DROP CONSTRAINT IF EXISTS ""fk_store_manager""";
                try { command.ExecuteNonQuery(); } catch { }
                command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{staffTable}"" CASCADE";
                command.ExecuteNonQuery();
                command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{storeTable}"" CASCADE";
                command.ExecuteNonQuery();

                FactoryContainer.Register<IConfigurationRoot>(savedConfig);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }

    [Test]
    public void DeliverTableData_ViaQuench_FailsFastOnReplaceCascade()
    {
        // A table using MergeType=Insert/Update/Delete that is referenced by an ON DELETE CASCADE FK
        // should fail pre-flight validation before any data is delivered.
        lock (FactoryContainer.SharedLockObject)
        {
            using var command = _connection.CreateCommand();
            var parentTable = $"_test_rparent_{Guid.NewGuid():N}".Substring(0, 30);
            var childTable = $"_test_rchild_{Guid.NewGuid():N}".Substring(0, 30);
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var checkpointDir = Path.Combine(Path.GetTempPath(), $"Checkpoint_{Guid.NewGuid():N}");
            var savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

            try
            {
                command.CommandText = $@"
                    CREATE TABLE ""{SchemaName}"".""{parentTable}"" (
                        ""id"" INT PRIMARY KEY,
                        ""name"" VARCHAR(100) NOT NULL
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"
                    CREATE TABLE ""{SchemaName}"".""{childTable}"" (
                        ""id"" INT PRIMARY KEY,
                        ""parent_id"" INT NOT NULL,
                        CONSTRAINT ""fk_cascade_{childTable}"" FOREIGN KEY (""parent_id"")
                            REFERENCES ""{SchemaName}"".""{parentTable}"" (""id"") ON DELETE CASCADE
                    )";
                command.ExecuteNonQuery();

                command.CommandText = $@"INSERT INTO ""{SchemaName}"".""{parentTable}"" VALUES (1, 'Existing')";
                command.ExecuteNonQuery();
                command.CommandText = $@"INSERT INTO ""{SchemaName}"".""{childTable}"" VALUES (10, 1)";
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

                template.Tables.Add(new PostgreSqlTable
                {
                    Name = parentTable,
                    Schema = SchemaName,
                    DataDelivery =
                [
                    new PostgreSqlDataDelivery
                    {
                        MergeType = "Insert/Update/Delete",
                        ContentFile = "parent.tabledata"
                    }
                ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.False, "Should fail due to Insert/Update/Delete+CASCADE");

                // Child data must NOT have been cascade-deleted — pre-flight check prevented delivery
                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{childTable}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));
            }
            finally
            {
                command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{childTable}"" CASCADE";
                command.ExecuteNonQuery();
                command.CommandText = $@"DROP TABLE IF EXISTS ""{SchemaName}"".""{parentTable}"" CASCADE";
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
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = _testTableName,
                    Schema = SchemaName,
                    DataDelivery =
                    [
                        new PostgreSqlDataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "gated.tabledata",
                            MatchColumns = @"""code""",
                            ShouldApplyExpression = $"current_database() = '{_testDb}'",
                            VariantName = "same-db"
                        }
                    ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
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
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = _testTableName,
                    Schema = SchemaName,
                    DataDelivery =
                    [
                        new PostgreSqlDataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "gated.tabledata",
                            MatchColumns = @"""code""",
                            ShouldApplyExpression = "current_database() = 'nosuchdatabase_xyz_278'",
                            VariantName = "wrong-env"
                        }
                    ]
                });

                RegisterTargetConfig();
                LogFactory.Register("ProgressLog", progressLog);

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
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
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = _testTableName,
                    Schema = SchemaName,
                    DataDelivery =
                    [
                        new PostgreSqlDataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "variantA.tabledata",
                            MatchColumns = @"""code""",
                            ShouldApplyExpression = $"current_database() = '{_testDb}'",
                            VariantName = "active-variant"
                        },
                        new PostgreSqlDataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "variantB.tabledata",
                            MatchColumns = @"""code""",
                            ShouldApplyExpression = "current_database() = 'nosuchdatabase_xyz_278'",
                            VariantName = "inactive-variant"
                        }
                    ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1), "Only the active variant's row should have landed.");

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'VA1'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'VB1'";
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
                        INSERT INTO ""{SchemaName}"".""{_testTableName}"" (""code"", ""name"", ""value"", ""active"") VALUES
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
                template.Tables.Add(new PostgreSqlTable
                {
                    Name = _testTableName,
                    Schema = SchemaName,
                    DataDelivery =
                    [
                        new PostgreSqlDataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "slice1.tabledata",
                            MatchColumns = @"""code""",
                            MergeFilter = @"""active"" = 1",
                            VariantName = "slice-1"
                        },
                        new PostgreSqlDataDelivery
                        {
                            MergeType = "Insert/Update/Delete",
                            ContentFile = "slice2.tabledata",
                            MatchColumns = @"""code""",
                            MergeFilter = @"""active"" = 2",
                            VariantName = "slice-2"
                        }
                    ]
                });

                RegisterTargetConfig();

                var product = new Product { Name = "TestProduct", Platform = Platform.PostgreSQL };
                var quench = new DatabaseQuench(FactoryContainer.Resolve<IConfigurationRoot>()["Target:Server"], product, template, _testDb,
                    suppressKindling: true, whatIfOnly: "false", runScriptsTwice: false,
                    dropRemovedTables: "0", dropRemovedColumns: "1", dropRemovedForeignKeys: "1", dropRemovedCheckConstraints: "1", dropRemovedExcludeConstraints: "1", dropRemovedStatistics: "1", dropRemovedIndexes: "1", dropUnknownIndexes: false, updateTables: false,
                    deliverData: true, checkpointing: new FileCheckpointManager(checkpointDir));
                quench.Execute();

                Assert.That(quench.QuenchSuccessful, Is.True);

                using var command = _connection.CreateCommand();
                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}""";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(3),
                    "Each delivery's delete must be scoped to its own MergeFilter slice — neither should wipe the other's rows.");

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'STALE1'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0), "Stale row in slice 1 must be deleted by slice 1's delivery.");

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""code"" = 'STALE2'";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0), "Stale row in slice 2 must be deleted by slice 2's delivery.");

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""active"" = 1";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2), "Slice 1 rows (S1A, S1B) must survive.");

                command.CommandText = $@"SELECT COUNT(*) FROM ""{SchemaName}"".""{_testTableName}"" WHERE ""active"" = 2";
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

    private static void RegisterTargetConfig()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var configBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Target:User"] = config["PostgreSQL:User"] ?? config["Target:User"],
                ["Target:Password"] = config["PostgreSQL:Password"] ?? config["Target:Password"],
                ["Target:Port"] = config["PostgreSQL:Port"] ?? config["Target:Port"],
                ["Target:Server"] = config["PostgreSQL:Server"] ?? config["Target:Server"] ?? "127.0.0.1"
            }!)
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(configBuilder);
    }

    #endregion
}

