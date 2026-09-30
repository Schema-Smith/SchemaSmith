// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
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
/// A declared FILESTREAM column, deployed through SchemaQuench's REAL path (#424). The FILESTREAM step ran only from
/// the <c>SchemaSmith.TableQuench</c> SQL wrapper, which the product never calls.
/// <para>[Explicit] with no SqlServer category, like <c>FileStreamDeployTests</c>: FILESTREAM is unsupported on SQL
/// Server on Linux, so only the local Windows instance <c>localhost\SQL2016</c> (FILESTREAM enabled, integrated
/// security — the TCP form fails SSPI) can prove it.</para>
/// </summary>
[Explicit("Requires localhost\\SQL2016 with FILESTREAM enabled; run manually.")]
[TestFixture]
public class DeployPathFileStreamTests
{
    private const string Instance = @"localhost\SQL2016";
    private const string MasterConn = @"Server=localhost\SQL2016;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True";

    [Test]
    public void AFileStreamColumn_IsCreated_ByARealDeploy()
    {
        var environment = Substitute.For<IEnvironment>();
        var progress = Substitute.For<ILog>();
        var errors = Substitute.For<ILog>();
        var db = $"DeployFs_{Guid.NewGuid():N}"[..30];
        var dir = Path.Join(Path.GetTempPath(), $"DeployFs_{Guid.NewGuid():N}");

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(MasterConn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CONVERT(INT, ISNULL(SERVERPROPERTY('FilestreamEffectiveLevel'), 0))";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("FILESTREAM is not enabled on " + Instance);
        cmd.CommandText = "SELECT CONVERT(NVARCHAR(400), SERVERPROPERTY('InstanceDefaultDataPath'))";
        var dataPath = cmd.ExecuteScalar().ToString();
        cmd.CommandText = $"CREATE DATABASE [{db}] ON PRIMARY (NAME = {db}_d, FILENAME = '{dataPath}{db}.mdf'), "
                          + $"FILEGROUP FsFg CONTAINS FILESTREAM DEFAULT (NAME = {db}_fs, FILENAME = '{dataPath}{db}Fs') "
                          + $"LOG ON (NAME = {db}_l, FILENAME = '{dataPath}{db}.ldf')";
        cmd.ExecuteNonQuery();
        WritePackage(dir, db);

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var keys = new[] { "SchemaPackagePath", "Target:Server", "Target:Port", "Target:User", "Target:Password",
                               "Target:ConnectionProperties:Column Encryption Setting" };
            var saved = keys.ToDictionary(k => k, k => config[k]);
            try
            {
                FactoryContainer.Register(environment);
                LogFactory.Register("ErrorLog", errors);
                LogFactory.Register("ProgressLog", progress);
                config["SchemaPackagePath"] = dir;
                config["Target:Server"] = Instance;
                config["Target:Port"] = null;
                config["Target:User"] = null;
                config["Target:Password"] = null;
                config["Target:ConnectionProperties:Column Encryption Setting"] = "Disabled";

                Program.Main([]);

                var logged = string.Join(" | ", progress.ReceivedCalls().Concat(errors.ReceivedCalls())
                    .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "")
                    .Where(m => m.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || m.Contains("error", StringComparison.OrdinalIgnoreCase)));
                Assert.That(environment.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Exit" && (int)c.GetArguments()[0] != 0),
                    Is.False, "the deploy must succeed; errors logged: " + logged);

                conn.ChangeDatabase(db);
                cmd.CommandText = "SELECT CONVERT(INT, is_filestream) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.DeployFs') AND name = 'Doc'";
                var isFileStream = cmd.ExecuteScalar();
                Assert.That(isFileStream, Is.Not.Null, "the declared FILESTREAM column must exist after a real deploy");
                Assert.That(Convert.ToInt32(isFileStream), Is.EqualTo(1), "and it must be FILESTREAM, not in-row VARBINARY(MAX)");
            }
            finally
            {
                foreach (var kv in saved) config[kv.Key] = kv.Value;
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try
                {
                    conn.ChangeDatabase("master");
                    cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
                    cmd.ExecuteNonQuery();
                }
                catch (DbException) { /* best-effort cleanup */ }
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (IOException) { /* a held log handle must not fail the test */ }
            }
        }
    }

    private static void WritePackage(string dir, string db)
    {
        var tables = Path.Join(dir, "Templates", "Main", "Tables");
        Directory.CreateDirectory(tables);
        File.WriteAllText(Path.Join(dir, "Product.json"),
            "{ \"Name\": \"DeployFs\", \"ValidationScript\": \"SELECT CAST(1 AS BIT)\", \"TemplateOrder\": [\"Main\"], "
            + "\"ScriptTokens\": {}, \"ScriptFolders\": [], \"Platform\": \"SqlServer\" }");
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"),
            "{ \"Name\": \"Main\", \"DatabaseIdentificationScript\": "
            + $"\"SELECT [name] FROM sys.databases WHERE [name] = '{db}'\", \"ScriptFolders\": [] }}");
        File.WriteAllText(Path.Join(tables, "dbo.DeployFs.json"),
            "{ \"Schema\": \"[dbo]\", \"Name\": \"[DeployFs]\", \"Columns\": ["
            + " { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },"
            + " { \"Name\": \"[G]\", \"DataType\": \"UNIQUEIDENTIFIER ROWGUIDCOL\", \"Nullable\": false, \"Default\": \"NEWID()\" },"
            + " { \"Name\": \"[Doc]\", \"DataType\": \"VARBINARY(MAX)\", \"Nullable\": true, \"FileStream\": true } ], \"Indexes\": ["
            + " { \"Name\": \"[PK_DeployFs]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true },"
            + " { \"Name\": \"[UQ_DeployFs_G]\", \"IndexColumns\": \"[G]\", \"UniqueConstraint\": true } ] }");
    }
}
