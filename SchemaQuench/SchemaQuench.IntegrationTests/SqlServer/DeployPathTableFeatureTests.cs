// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Table-level features that live in their own procedure, deployed through SchemaQuench's REAL path
/// (<c>Program.Main</c> → <c>DatabaseQuench</c>), not the <c>SchemaSmith.TableQuench</c> SQL wrapper.
/// <para>Every other test of these features calls that wrapper, and the product never does: it runs each table
/// step itself. A step added only to the wrapper therefore passed every test while a real deploy never ran it —
/// CDC (#423), table Change Tracking and FILESTREAM columns (#424), and the below-floor degrade (#425). These
/// tests assert what a user gets from a deploy, so a step the product stops calling fails here.</para>
/// </summary>
[Category("SqlServer")]
[TestFixture]
public class DeployPathTableFeatureTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void ACdcTable_IsEnabledRotatedAndDisabled_ByARealDeploy()
    {
        RunScenario("DeployCdc", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            // 1. A new table: CDC is enabled after every column exists, computed column included (#420, #423).
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(IsTrackedByCdc(cmd), Is.True, "a new EnableCDC table must be tracked after a real deploy");
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "Id", "Twice" }),
                "the capture instance must cover every declared column, computed ones included");

            // 2. A column change rotates to an instance that includes the new column.
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "B", "Id", "Twice" }),
                "a column change must rotate to a capture instance that includes the new column");
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "rotation keeps the old instance for the operator to drain");

            // 3. Turning it off disables it.
            deploy(CdcTable(enableCdc: false, extraColumn: true));
            Assert.That(IsTrackedByCdc(cmd), Is.False, "EnableCDC false must disable CDC on the table");
        });
    }

    // #426. SQL Server defaults @supports_net_changes to ON once the table has a primary key, and since the #420 move a
    // new table's key exists when CDC is enabled -- so leaving it to the engine flipped every new table to ON. Unset keeps
    // the pre-2.7 result, OFF; declaring it rotates, because an instance cannot change it in place.
    [Test]
    public void NetChanges_AreOffWhenUnset_AndADeclarationRotatesToThem()
    {
        RunScenario("DeployNet", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.False, "an unset CdcSupportsNetChanges must not get SQL Server's ON default");

            deploy(CdcTable(enableCdc: true, extraColumn: false, netChanges: true));
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "a declared value the instance lacks rotates to a new instance");
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "and the new instance has it");
        });
    }

    [Test]
    public void ARotation_KeepsTheInstancesNetChanges_WhenUnset()
    {
        RunScenario("DeployNetKeep", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            // OFF on a table WITH a primary key -- the one case where "keep" and SQL Server's own default (ON once a key
            // exists) disagree. Starting from ON would pass whether or not the rotation carried the value.
            deploy(CdcTable(enableCdc: true, extraColumn: false, netChanges: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.False);

            // The declaration goes away and a column is added: the column rotates, and unset keeps what the table had.
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(InstanceCount(cmd), Is.EqualTo(2));
            Assert.That(NewestInstanceNetChanges(cmd), Is.False, "a rotation must keep net changes it was not told to change, as it keeps its filegroup");
        });
    }

    [Test]
    public void ATemplateDefault_SetsNetChanges_ForATableThatDeclaresNone()
    {
        RunScenario("DeployNetTpl", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "the template's CdcSupportsNetChanges applies to a table that declares none");
        }, templateExtra: ", \"CdcSupportsNetChanges\": true");
    }

    [Test]
    public void NetChangesWithoutAPrimaryKey_IsRefusedByName()
    {
        RunScenario("DeployNetNoPk", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false, netChanges: true, primaryKey: false));
            // Refused by our own message, before sp_cdc_enable_table could fail on it mid-run. A failure is logged to the
            // error log, so the text is looked for across both logs rather than assuming which one carries it.
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("sets CdcSupportsNetChanges true") && m.Contains("declares no primary key")), Is.True,
                "the deploy must refuse by name, not fail inside sp_cdc_enable_table");
        }, expectFailure: true);
    }

    [Test]
    public void CdcDeclaredOnADatabaseWithoutCdc_IsRecordedAsDowngraded_ByARealDeploy()
    {
        RunScenario("DeployCdcOff", setupDatabase: null, (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(IsTrackedByCdc(cmd), Is.False);
            // Asserted on the log, not on SchemaSmith.ChangeAudit: a real deploy reads its session's audit rows into the
            // deployment summary and then deletes them, so the table is empty afterwards whether or not the degrade ran.
            _progressLog.Received().Info(Arg.Is<string>(m => m.Contains("CDC skipped: not enabled on this database")));
        });
    }

    [Test]
    public void AChangeTrackingTable_IsTracked_ByARealDeploy()
    {
        RunScenario("DeployCt", setupDatabase: "ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 1 DAYS)", (deploy, db, cmd) =>
        {
            deploy("""
                { "Schema": "[dbo]", "Name": "[DeployProbe]", "EnableChangeTracking": true, "TrackColumnsUpdated": true,
                  "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[A]", "DataType": "INT", "Nullable": true } ],
                  "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }
                """);
            cmd.CommandText = "SELECT is_track_columns_updated_on FROM sys.change_tracking_tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe')";
            var result = cmd.ExecuteScalar();
            Assert.That(result, Is.Not.Null.And.Not.EqualTo(DBNull.Value), "an EnableChangeTracking table must be tracked after a real deploy");
            Assert.That(Convert.ToBoolean(result), Is.True, "TrackColumnsUpdated must be applied");
        });
    }

    private static string CdcTable(bool enableCdc, bool extraColumn, bool? netChanges = null, bool primaryKey = true) => $$"""
        { "Schema": "[dbo]", "Name": "[DeployProbe]", "EnableCDC": {{(enableCdc ? "true" : "false")}},
          {{(netChanges is { } nc ? $"\"CdcSupportsNetChanges\": {(nc ? "true" : "false")}," : "")}}
          "Columns": [
            { "Name": "[Id]", "DataType": "INT", "Nullable": false },
            { "Name": "[A]", "DataType": "INT", "Nullable": true },
            {{(extraColumn ? """{ "Name": "[B]", "DataType": "INT", "Nullable": true },""" : "")}}
            { "Name": "[Twice]", "DataType": "INT", "Nullable": true, "ComputedExpression": "[A] * 2" }
          ],
          "Indexes": [ {{(primaryKey
              ? """{ "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }"""
              : """{ "Name": "[IX_DeployProbe]", "IndexColumns": "[Id]" }""")}} ] }
        """;

    private void RunScenario(string prefix, string setupDatabase, Action<Action<string>, string, IDbCommand> body,
                             string templateExtra = "", bool expectFailure = false)
    {
        var db = prefix + "_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
                ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
                    config["Target:User"], config["Target:Password"], config["Target:Port"], connProps));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var savedPath = config["SchemaPackagePath"];

            try
            {
                cmd.CommandText = $"CREATE DATABASE [{db}];";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                if (setupDatabase != null) ExecuteWithDeadlockRetry(cmd, setupDatabase);
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
                config["SchemaPackagePath"] = tempDir;

                body(tableJson =>
                {
                    WritePackage(tempDir, db, tableJson, templateExtra);
                    _environment.ClearReceivedCalls();
                    Program.Main(["SkipKindlingForge"]);
                    if (expectFailure) _environment.Received().Exit(Arg.Is<int>(code => code != 0));
                    else _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));
                }, db, cmd);
            }
            finally
            {
                config["SchemaPackagePath"] = savedPath;
                try
                {
                    conn.ChangeDatabase("master");
                    cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN "
                                      + $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
                    cmd.ExecuteNonQuery();
                }
                catch (DbException) { /* best-effort cleanup */ }
                catch (InvalidOperationException) { /* connection already unusable */ }
                conn.Close();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
    }

    // sp_cdc_enable_db writes database-wide replication metadata and can be chosen as a deadlock victim against a
    // sibling fixture's DDL; retrying is what the CDC fixture does for the same reason.
    private static void ExecuteWithDeadlockRetry(IDbCommand cmd, string sql)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return;
            }
            catch (DbException e) when (e.Message.Contains("deadlock victim", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(1000);
            }
        }
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool IsTrackedByCdc(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe')";
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static bool NewestInstanceNetChanges(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT TOP 1 supports_net_changes FROM cdc.change_tables
                            WHERE source_object_id = OBJECT_ID('dbo.DeployProbe') ORDER BY create_date DESC, [object_id] DESC";
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static int InstanceCount(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.DeployProbe')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string[] NewestInstanceColumns(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT cc.column_name FROM cdc.captured_columns cc
                            WHERE cc.object_id = (SELECT TOP 1 ct.object_id FROM cdc.change_tables ct
                                                   WHERE ct.source_object_id = OBJECT_ID('dbo.DeployProbe')
                                                   ORDER BY ct.create_date DESC, ct.object_id DESC)
                            ORDER BY cc.column_name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static void WritePackage(string dir, string db, string tableJson, string templateExtra = "")
    {
        var tables = Path.Join(dir, "Templates", "Main", "Tables");
        Directory.CreateDirectory(tables);
        File.WriteAllText(Path.Join(dir, "Product.json"),
            "{ \"Name\": \"DeployPathProbe\", \"ValidationScript\": \"SELECT CAST(1 AS BIT)\", \"TemplateOrder\": [\"Main\"], "
            + "\"ScriptTokens\": {}, \"ScriptFolders\": [], \"Platform\": \"SqlServer\" }");
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"),
            "{ \"Name\": \"Main\", \"DatabaseIdentificationScript\": "
            + $"\"SELECT [name] FROM sys.databases WHERE [name] = '{db}'\", \"ScriptFolders\": []{templateExtra} }}");
        File.WriteAllText(Path.Join(tables, "dbo.DeployProbe.json"), tableJson);
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }
}
