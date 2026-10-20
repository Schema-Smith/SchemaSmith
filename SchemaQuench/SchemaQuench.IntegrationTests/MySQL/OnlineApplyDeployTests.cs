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

namespace SchemaQuench.IntegrationTests.MySQL;

// OnlineApply set the way a user sets it, in the environment's configuration, reaches the MySQL-family procedures (as
// @ss_online_apply), and the form each change ran in reaches the Deployment Summary. The procedure-level behaviour is
// TableQuench_OnlineApplySharedTests'.
[Category("MySQL")]
public class OnlineApplyDeployTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private string _connectionString = null!;
    private string _mainDb = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        FixtureSetup.EnsureInitialized();
        _connectionString = FixtureSetup.ConnectionString + "Database=information_schema;";
        _mainDb = FixtureSetup.MainDb;
    }

    [Test]
    public void OnlineApplyPrefer_InTheEnvironment_AddsTheColumnInPlaceAndTheIndexOnline_AndTheSummarySaysSo()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"OnlineApplyMy_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);
            CopyDirectory(TestHelper.GetTestProductPath("MySQL", "StickyPreventDrop"), tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                config["SchemaPackagePath"] = tempDir;
                Program.Main(["SkipKindlingForge"]);
                cmd.CommandText = $"INSERT INTO `{_mainDb}`.`KeeperTable` (`Id`, `Notes`) VALUES (1, 'a'), (2, 'b')";
                cmd.ExecuteNonQuery();

                var keeper = Path.Join(tempDir, "Templates", "Main", "Tables", "KeeperTable.json");
                var root = JObject.Parse(File.ReadAllText(keeper));
                var columns = (JArray)root["Columns"]!;
                columns.Insert(1, JObject.Parse("""{ "Name": "`Extra`", "DataType": "INT", "Nullable": true }"""));
                ((JArray)root["Indexes"]!).Add(JObject.Parse("""{ "Name": "IX_KeeperTable_Extra", "IndexColumns": "`Extra`" }"""));
                File.WriteAllText(keeper, root.ToString());

                config["OnlineApply"] = "Prefer";
                _environment.ClearReceivedCalls();
                Program.Main(["SkipKindlingForge"]);
                _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));

                var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
                var details = (JArray)summary.SelectToken("objectChanges.details")!;
                string ModeOf(string type, string name) =>
                    (string)details.Single(d => (string)d["objectType"]! == type && ((string)d["objectName"]!).Replace("`", "").EndsWith(name) && (string)d["action"]! == "created")["mode"];
                cmd.CommandText = $"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = 'KeeperTable'";
                var order = cmd.ExecuteScalar()?.ToString();
                Assert.Multiple(() =>
                {
                    Assert.That(ModeOf("column", "KeeperTable.Extra"), Is.AnyOf("instant", "online"));
                    Assert.That(ModeOf("index", "KeeperTable.IX_KeeperTable_Extra"), Is.EqualTo("online"));
                    Assert.That(order, Is.EqualTo("Id,Extra,Notes"), "the column lands where it is declared");
                });
            }
            finally
            {
                config["OnlineApply"] = string.Empty;
                config["SchemaPackagePath"] = string.Empty;
                cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`RemovableTable`; DROP TABLE IF EXISTS `{_mainDb}`.`ProtectedTable`; DROP TABLE IF EXISTS `{_mainDb}`.`KeeperTable`;";
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
