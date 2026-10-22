// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// OnlineApply set the way a user sets it, in the environment's configuration, reaches the deploy's procedures; the
// statements they queue run after each pass, outside its transaction; and the form each change ran in reaches the
// Deployment Summary. The procedure-level behaviour is TableQuench_OnlineApplyTests'.
[Category("PostgreSQL")]
public class OnlineApplyDeployTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    public OnlineApplyDeployTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [Test]
    public void OnlineApplyPrefer_InTheEnvironment_BuildsConcurrentlyAndValidatesAfterThePass_AndTheSummarySaysSo()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"OnlineApplyPg_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            CopyDirectory(TestHelper.GetTestProductPath("PostgreSQL", "StickyPreventDrop"), tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                config["SchemaPackagePath"] = tempDir;
                Program.Main(["SkipKindlingForge"]);
                cmd.CommandText = "INSERT INTO public.\"KeeperTable\" (\"Id\", \"Notes\") SELECT g, 'n' || g FROM generate_series(1, 100) g";
                cmd.ExecuteNonQuery();

                var keeper = Path.Join(tempDir, "Templates", "Main", "Tables", "public.KeeperTable.json");
                var root = JObject.Parse(File.ReadAllText(keeper));
                ((JArray)root["Indexes"]!).Add(JObject.Parse("""{ "Name": "IX_KeeperTable_Id_Notes", "IndexColumns": "Id, Notes" }"""));
                // The key the index pass attaches must exist before the foreign-key pass adds a key referencing it, and the
                // foreign key's validation runs after that pass: each pass's queue runs before the next pass starts.
                ((JArray)root["Indexes"]!).Add(JObject.Parse("""{ "Name": "UQ_KeeperTable_Notes", "UniqueConstraint": true, "IndexColumns": "Notes" }"""));
                root["ForeignKeys"] = JArray.Parse("""[{ "Name": "FK_KeeperTable_Notes", "Columns": "Notes", "RelatedTableSchema": "public", "RelatedTable": "KeeperTable", "RelatedColumns": "Notes" }]""");
                ((JArray)root["CheckConstraints"]!).Add(JObject.Parse("""{ "Name": "CK_KeeperTable_Notes", "Expression": "\"Notes\" IS NOT NULL" }"""));
                File.WriteAllText(keeper, root.ToString());

                config["OnlineApply"] = "Prefer";
                _environment.ClearReceivedCalls();
                _progressLog.ClearReceivedCalls();
                Program.Main(["SkipKindlingForge"]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));

                var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
                var details = (JArray)summary.SelectToken("objectChanges.details")!;
                string ModeOf(string name) => (string)details.Single(d => (string)d["objectName"]! == name && (string)d["action"]! == "created")["mode"];
                var progress = _progressLog.ReceivedCalls().Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "").ToList();
                cmd.CommandText = "SELECT (SELECT idx.indisvalid FROM pg_class i JOIN pg_index idx ON idx.indexrelid = i.oid WHERE i.relname = 'IX_KeeperTable_Id_Notes')::text || '|' || " +
                                  "(SELECT convalidated FROM pg_constraint WHERE conname = 'CK_KeeperTable_Notes')::text || '|' || " +
                                  "COALESCE((SELECT convalidated FROM pg_constraint WHERE conname = 'FK_KeeperTable_Notes')::text, 'missing')";
                var state = cmd.ExecuteScalar()?.ToString();
                Assert.Multiple(() =>
                {
                    Assert.That(ModeOf("public.KeeperTable.IX_KeeperTable_Id_Notes"), Is.EqualTo("online"));
                    Assert.That(ModeOf("public.KeeperTable.CK_KeeperTable_Notes"), Is.EqualTo("online"));
                    Assert.That(ModeOf("public.KeeperTable.UQ_KeeperTable_Notes"), Is.EqualTo("online"));
                    Assert.That(ModeOf("public.KeeperTable.FK_KeeperTable_Notes"), Is.EqualTo("online"));
                    Assert.That(state, Is.EqualTo("true|true|true"), "the index is valid, and the check and the foreign key validated");
                    Assert.That(progress, Has.Some.Contains("Building Index public.KeeperTable.IX_KeeperTable_Id_Notes concurrently"));
                    Assert.That(progress, Has.Some.Contains("Validating constraint public.KeeperTable.CK_KeeperTable_Notes"));
                });
            }
            finally
            {
                config["OnlineApply"] = string.Empty;
                config["SchemaPackagePath"] = string.Empty;
                cmd.CommandText = "DROP TABLE IF EXISTS public.\"RemovableTable\" CASCADE; DROP TABLE IF EXISTS public.\"ProtectedTable\" CASCADE; DROP TABLE IF EXISTS public.\"KeeperTable\" CASCADE;";
                cmd.ExecuteNonQuery();
                conn.Close();
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void OnlineApplyPrefer_ANewIndexThatClustersTheTableAndCarriesItsReplicaIdentity_IsBuiltFirst_AndEveryDeploySucceeds()
    {
        WithDeployedPackage((tempDir, cmd, config) =>
        {
            EditKeeper(tempDir, root =>
            {
                ((JArray)root["Indexes"]!).Add(JObject.Parse("""{ "Name": "UX_KeeperTable_Id", "Unique": true, "Clustered": true, "IndexColumns": "Id" }"""));
                root["ReplicaIdentity"] = "INDEX";
                root["ReplicaIdentityIndex"] = "UX_KeeperTable_Id";
            });
            config["OnlineApply"] = "Prefer";
            for (var deploy = 1; deploy <= 2; deploy++)
            {
                _environment.ClearReceivedCalls();
                Program.Main(["SkipKindlingForge"]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));
            }

            cmd.CommandText = "SELECT idx.indisvalid::text || '|' || idx.indisclustered::text || '|' || idx.indisreplident::text " +
                              "FROM pg_class i JOIN pg_index idx ON idx.indexrelid = i.oid WHERE i.relname = 'UX_KeeperTable_Id'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("true|true|true"), "the index is valid, clusters the table and carries its replica identity");
        });
    }

    [Test]
    public void OnlineApplyPrefer_AnIndexWhoseNameAnotherTablesIndexHolds_FailsTheDeploy_AndLeavesThatIndexInPlace()
    {
        WithDeployedPackage((tempDir, cmd, config) =>
        {
            cmd.CommandText = "CREATE INDEX \"IX_Taken\" ON public.\"ProtectedTable\" (\"Id\")";
            cmd.ExecuteNonQuery();
            EditKeeper(tempDir, root => ((JArray)root["Indexes"]!).Add(JObject.Parse("""{ "Name": "IX_Taken", "IndexColumns": "Notes" }""")));
            config["OnlineApply"] = "Prefer";
            _environment.ClearReceivedCalls();
            Program.Main(["SkipKindlingForge"]);

            _environment.Received().Exit(Arg.Is<int>(code => code != 0));
            cmd.CommandText = "SELECT tc.relname || '|' || idx.indisvalid::text FROM pg_class i JOIN pg_index idx ON idx.indexrelid = i.oid " +
                              "JOIN pg_class tc ON tc.oid = idx.indrelid WHERE i.relname = 'IX_Taken'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ProtectedTable|true"), "the other table keeps its index; the name clash fails the deploy, as it does without OnlineApply");
        });
    }

    // The templates are loaded and checked before the product's Before scripts run, so a target naming no template stops
    // the deploy with the database as it was. Engine-independent: the order is the product run's, tested on one engine.
    [Test]
    public void ATargetNamingNoTemplate_StopsTheDeploy_BeforeTheProductsBeforeScriptsRun()
    {
        WithDeployedPackage((tempDir, cmd, config) =>
        {
            var product = Path.Join(tempDir, "Product.json");
            var root = JObject.Parse(File.ReadAllText(product));
            root["ScriptFolders"] = JArray.Parse("""[{ "FolderPath": "Before", "QuenchSlot": "Before" }]""");
            File.WriteAllText(product, root.ToString());
            Directory.CreateDirectory(Path.Join(tempDir, "Before"));
            // A product script runs on the server's own database, not the template's.
            File.WriteAllText(Path.Join(tempDir, "Before", "marker.sql"), "CREATE TABLE IF NOT EXISTS public.\"BeforeRanMarker\" (id int);");
            using var server = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            server.Open();
            using var serverCmd = server.CreateCommand();
            const string marker = "SELECT to_regclass('public.\"BeforeRanMarker\"') IS NOT NULL";
            _environment.ClearReceivedCalls();
            Program.Main(["SkipKindlingForge"]);
            serverCmd.CommandText = marker;
            Assert.That(serverCmd.ExecuteScalar(), Is.True, "setup: a deploy runs the Before script, or the check below proves nothing");
            serverCmd.CommandText = "DROP TABLE public.\"BeforeRanMarker\"";
            serverCmd.ExecuteNonQuery();

            const string unknownTarget = "Target:TemplateTargets:NoSuchTemplate:Databases:0";
            config[unknownTarget] = "x";
            try
            {
                _environment.ClearReceivedCalls();
                Program.Main(["SkipKindlingForge"]);
                _environment.Received().Exit(Arg.Is<int>(code => code >= 2));
            }
            finally
            {
                config[unknownTarget] = null;
            }

            serverCmd.CommandText = marker;
            var ran = serverCmd.ExecuteScalar();
            serverCmd.CommandText = "DROP TABLE IF EXISTS public.\"BeforeRanMarker\"";
            serverCmd.ExecuteNonQuery();
            Assert.That(ran, Is.False, "the Before script must not have run");
        });
    }

    private void WithDeployedPackage(Action<string, System.Data.IDbCommand, IConfigurationRoot> body)
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"OnlineApplyPg_{Guid.NewGuid():N}");
        lock (FactoryContainer.SharedLockObject)
        {
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            CopyDirectory(TestHelper.GetTestProductPath("PostgreSQL", "StickyPreventDrop"), tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                config["SchemaPackagePath"] = tempDir;
                Program.Main(["SkipKindlingForge"]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));
                cmd.CommandText = "INSERT INTO public.\"KeeperTable\" (\"Id\", \"Notes\") SELECT g, 'n' || g FROM generate_series(1, 100) g";
                cmd.ExecuteNonQuery();
                body(tempDir, cmd, config);
            }
            finally
            {
                config["OnlineApply"] = string.Empty;
                config["SchemaPackagePath"] = string.Empty;
                cmd.CommandText = "DROP TABLE IF EXISTS public.\"RemovableTable\" CASCADE; DROP TABLE IF EXISTS public.\"ProtectedTable\" CASCADE; DROP TABLE IF EXISTS public.\"KeeperTable\" CASCADE;";
                cmd.ExecuteNonQuery();
                conn.Close();
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    private static void EditKeeper(string tempDir, Action<JObject> edit)
    {
        var keeper = Path.Join(tempDir, "Templates", "Main", "Tables", "public.KeeperTable.json");
        var root = JObject.Parse(File.ReadAllText(keeper));
        edit(root);
        File.WriteAllText(keeper, root.ToString());
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Join(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Join(dest, Path.GetFileName(dir)));
    }
}
