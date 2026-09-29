// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Live proof of N2 (gate token vocabulary uniformity): a DataDelivery.ShouldApplyExpression gate now
/// resolves the same version-token vocabulary a folder gate already resolves ({{ServerMajorVersion}},
/// {{CompatibilityLevel}}) instead of only {{SchemaName}}. Before the fix, {{CompatibilityLevel}} reached
/// the server as literal text and the gate query parse-errored — the exact user-facing symptom this
/// class proves is gone, and the control test below reproduces for documentation.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class DataDeliveryGateVersionTokenIntegrationTests
{
    private class TestColumn : IDeliverableColumn
    {
        public string Name { get; set; }
        public bool Nullable { get; set; }
    }

    private class TestTable : IDeliverableTable
    {
        public string Name { get; set; }
        public string Schema { get; set; }
        public IReadOnlyList<DataDelivery> DataDeliveries { get; set; } = new List<DataDelivery>();
        public IReadOnlyList<IDeliverableColumn> DeliverableColumns { get; set; } = new List<IDeliverableColumn>();
        public IReadOnlyList<IDeliverableForeignKey> DeliverableForeignKeys { get; set; } = new List<IDeliverableForeignKey>();
    }

    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void ResolveGateExpression_CompatibilityLevelToken_EvaluatesInsteadOfErroring()
    {
        using var command = _connection.CreateCommand();
        var info = TargetVersionDetector.Detect(command, Platform.SqlServer, FixtureSetup.MainDb);
        var versionTokens = new List<KeyValuePair<string, string>>
        {
            new("ServerMajorVersion", info.ServerComparable.ToString()),
            new("CompatibilityLevel", (info.CompatibilityLevel ?? info.ServerComparable).ToString())
        };

        var resolved = DataDeliveryProcessor.ResolveGateExpression(
            "{{CompatibilityLevel}} >= 130", "", versionTokens, Platform.SqlServer);
        Assert.That(resolved, Does.Not.Contain("{{"),
            "The version token must be substituted before the gate reaches the server.");

        var applies = false;
        Assert.DoesNotThrow(() => applies = GateEvaluator.ShouldApply(command, resolved),
            "A resolved version-token gate must evaluate cleanly against a live target.");
        Assert.That(applies, Is.True, "The integration sandbox runs SQL Server at compatibility level >= 130.");

        // Control: reproduces the bug this fix removes — the LITERAL unresolved token reaching the
        // server as text is not valid SQL and errors, rather than evaluating.
        Assert.That(() => GateEvaluator.ShouldApply(command, "{{CompatibilityLevel}} >= 130"), Throws.InstanceOf<SqlException>(),
            "Documents the defect: an unresolved version token in a delivery gate reaches the server as literal text and errors.");
    }

    [Test]
    public void DeliverTables_CompatibilityLevelGatedDelivery_DeliversRealDataEndToEnd()
    {
        var tableName = $"_test_gatever_{Guid.NewGuid():N}"[..40];
        // A real TemplateRootPath + on-disk content file — DataDeliveryProcessor.ResolveContentFilePath
        // resolves ContentFile against TemplateRootPath and requires the file to actually exist; an empty
        // root/no-op reader (fine for the gate-only test above) makes content resolution fail before the
        // gate result is even exercised. Mirrors SchemaQuench.IntegrationTests/SqlServer/TableDataDeliveryTests.cs.
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Join(tempDir, "seed.tabledata"), @"[{""Id"": 1, ""Name"": ""Widget""}]");

        using var command = _connection.CreateCommand();
        try
        {
            command.CommandText = $"CREATE TABLE [dbo].[{tableName}] ([Id] INT PRIMARY KEY, [Name] NVARCHAR(50))";
            command.ExecuteNonQuery();

            var info = TargetVersionDetector.Detect(command, Platform.SqlServer, FixtureSetup.MainDb);
            var versionTokens = new List<KeyValuePair<string, string>>
            {
                new("ServerMajorVersion", info.ServerComparable.ToString()),
                new("CompatibilityLevel", (info.CompatibilityLevel ?? info.ServerComparable).ToString())
            };

            var table = new TestTable
            {
                Name = tableName,
                Schema = "dbo",
                DeliverableColumns = new List<IDeliverableColumn>
                {
                    new TestColumn { Name = "Id", Nullable = false },
                    new TestColumn { Name = "Name", Nullable = true }
                },
                DataDeliveries = new List<DataDelivery>
                {
                    new DataDelivery
                    {
                        MergeType = "Insert",
                        ContentFile = "seed.tabledata",
                        MatchColumns = "Id",
                        // The scenario reported: a version-token gate on a plain (non-schema-template) delivery.
                        ShouldApplyExpression = "{{CompatibilityLevel}} >= 130"
                    }
                }
            };

            var context = new DataDeliveryContext
            {
                Tables = new List<IDeliverableTable> { table },
                Platform = "SqlServer",
                Command = command,
                DatabaseName = FixtureSetup.MainDb,
                TemplateRootPath = tempDir,
                ScriptHelper = new MergeScriptHelperAdapter(Platform.SqlServer),
                ReadFileContent = path => File.ReadAllText(path),
                ExecuteScript = (_, sql) => { command.CommandText = sql; command.ExecuteNonQuery(); },
                ProgressLog = _ => { },
                ProgressLogError = _ => { },
                VersionTokens = versionTokens,
                SqlServerCompatibilityLevel = info.CompatibilityLevel ?? 0
            };

            Assert.DoesNotThrow(() => DataDeliveryProcessor.GetFromFactory().DeliverTables(context),
                "The compatibility-level-gated delivery must deliver, not error, on a modern-compat target.");

            // The decisive assertion: the row actually landed. "Did not throw" alone would also pass if
            // the gate had silently evaluated false and the delivery were skipped — the exact failure
            // mode this test exists to catch (a regression back to only {{SchemaName}} resolving would
            // leave {{CompatibilityLevel}} unresolved, which errors rather than skips, but a future
            // resolver bug could plausibly skip-instead-of-error, and this assertion catches that too).
            command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{tableName}] WHERE [Id] = 1 AND [Name] = 'Widget'";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1),
                "The gate must have evaluated true and let the delivery insert its seed row.");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
