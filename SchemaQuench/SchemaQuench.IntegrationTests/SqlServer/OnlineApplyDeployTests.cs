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

namespace SchemaQuench.IntegrationTests.SqlServer;

// OnlineApply set the way a user sets it, in the environment's configuration, reaches the deploy's procedures, and the
// form each change ran in reaches the Deployment Summary. The procedure-level behaviour is TableQuench_OnlineApplyTests'.
[Category("SqlServer")]
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
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [Test]
    public void OnlineApplyPrefer_InTheEnvironment_BuildsAChangedIndexOnline_AndTheSummarySaysSo()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"OnlineApply_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            CopyDirectory(TestHelper.GetTestProductPath("SqlServer", "StickyPreventDrop"), tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                config["SchemaPackagePath"] = tempDir;
                Program.Main(["SkipKindlingForge"]);

                var keeper = Path.Join(tempDir, "Templates", "Main", "Tables", "dbo.KeeperTable.json");
                var root = JObject.Parse(File.ReadAllText(keeper));
                ((JArray)root["Indexes"]!).First(i => (string)i["Name"]! == "[IX_KeeperTable_Notes]")["IndexColumns"] = "[Notes],[Id]";
                File.WriteAllText(keeper, root.ToString());

                config["OnlineApply"] = "Prefer";
                _environment.ClearReceivedCalls();
                Program.Main(["SkipKindlingForge"]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));

                var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
                var details = (JArray)summary.SelectToken("objectChanges.details")!;
                var created = details.Single(d => (string)d["objectName"]! == "[dbo].[KeeperTable].[IX_KeeperTable_Notes]" && (string)d["action"]! == "created");
                Assert.Multiple(() =>
                {
                    Assert.That((string)summary["schemaVersion"]!, Is.EqualTo("1.2"));
                    Assert.That((string)created["mode"]!, Is.EqualTo("online"));
                    Assert.That(details.Where(d => ((JObject)d).ContainsKey("mode")).All(d => (string)d["mode"]! is "online" or "unavailable" or "resumable"), Is.True);
                });
            }
            finally
            {
                config["OnlineApply"] = string.Empty;
                config["SchemaPackagePath"] = string.Empty;
                cmd.CommandText = "DROP TABLE IF EXISTS [dbo].[RemovableTable]; DROP TABLE IF EXISTS [dbo].[ProtectedTable]; DROP TABLE IF EXISTS [dbo].[KeeperTable];";
                cmd.ExecuteNonQuery();
                conn.Close();
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
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
