// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A1 end-to-end proof of the {{ServerMajorVersion}} / {{CompatibilityLevel}} script tokens against a
/// live SQL Server. The decisive case is the compat-level footgun: a MODERN binary hosting a
/// compatibility-level-100 database. There, {{CompatibilityLevel}} resolves to 100 while
/// {{ServerMajorVersion}} resolves to the (modern) server major version, so a gate on
/// {{CompatibilityLevel}} &gt;= 130 is FALSE (correct — OPENJSON/STRING_AGG would parse-error at compat
/// 100) while a gate on {{ServerMajorVersion}} &gt;= 13 is TRUE. This is the rule the tokens exist to
/// make expressible: gate syntax on compatibility level, gate features on server version.
/// </summary>
[Category("SqlServer")]
public class VersionCompatTokenIntegrationTests
{
    private readonly string _connectionString;

    public VersionCompatTokenIntegrationTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    [Test]
    public void CompatLevel100OnModernBinary_SeparatesSyntaxGatingFromFeatureGating()
    {
        var compat100Db = "vctdb_c100_" + Guid.NewGuid().ToString("N")[..8];
        DropTransientDb(compat100Db);
        CreateDatabaseAtCompatLevel(compat100Db, 100);

        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();

            // The footgun premise, straight from detection: a modern binary hosting a compat-100 database.
            var info = TargetVersionDetector.Detect(cmd, Platform.SqlServer, compat100Db);
            Assert.Multiple(() =>
            {
                Assert.That(info.CompatibilityLevel, Is.EqualTo(100),
                    "The database is at compatibility level 100.");
                Assert.That(info.ServerComparable, Is.GreaterThanOrEqualTo(13),
                    "The sandbox binary is modern (>= SQL Server 2016), so version-gating and syntax-gating disagree.");
            });

            var product = new Product { Name = "P", Platform = Platform.SqlServer };
            var template = new Template { Name = "T" };
            // Syntax gate: false at compat 100 (OPENJSON/STRING_AGG would parse-error there).
            var syntaxGate = new TemplateFolder
            {
                FolderPath = "syntax",
                QuenchSlot = TemplateQuenchSlot.Before,
                ShouldApplyExpression = "SELECT CASE WHEN {{CompatibilityLevel}} >= 130 THEN 1 ELSE 0 END"
            };
            syntaxGate.Scripts.Add(new SqlScript { Name = "syntax.sql" });
            // Feature gate: true on the modern binary.
            var featureGate = new TemplateFolder
            {
                FolderPath = "feature",
                QuenchSlot = TemplateQuenchSlot.Before,
                ShouldApplyExpression = "SELECT CASE WHEN {{ServerMajorVersion}} >= 13 THEN 1 ELSE 0 END"
            };
            featureGate.Scripts.Add(new SqlScript { Name = "feature.sql" });
            template.ScriptFolders.Add(syntaxGate);
            template.ScriptFolders.Add(featureGate);

            var quench = new DatabaseQuench("srv", product, template, compat100Db,
                false, "0", false, "0", "1", "1", "1", "1", "1", "1", "0", false, false, null);
            quench.PrepareIterationContent();
            quench.PrepareVersionScriptTokens(info.ServerComparable, info.CompatibilityLevel);

            quench.ApplyFolderGates(cmd);

            Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "feature.sql" }),
                "The compat-130 syntax gate must skip (100 >= 130 is false) while the version-13 feature gate applies.");
        }
        finally
        {
            DropTransientDb(compat100Db);
        }
    }

    [Test]
    public void TableXmlTwin_ShredsOnCompat100_WhereJsonOpenJsonParseErrors()
    {
        // A2 end-to-end: the XML twin is shreddable with XQuery on a compat-100 database, where the JSON
        // sibling's OPENJSON path parse-errors — the exact gap the twins close for legacy-tier authors.
        var compat100Db = "vctxml_c100_" + Guid.NewGuid().ToString("N")[..8];
        DropTransientDb(compat100Db);
        CreateDatabaseAtCompatLevel(compat100Db, 100);

        try
        {
            var template = new Template { TableSchema = "[{\"Name\":\"[Orders]\"}]" };

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(compat100Db); // OPENJSON's parse depends on THIS database's compatibility level

            using (var xmlCmd = conn.CreateCommand())
            {
                xmlCmd.CommandText =
                    "SELECT q.x.value('(/Tables/Table/Name)[1]','nvarchar(200)') FROM (SELECT CAST(@x AS xml) AS x) q";
                var p = xmlCmd.CreateParameter();
                p.ParameterName = "@x";
                p.Value = template.TableXml;
                xmlCmd.Parameters.Add(p);
                Assert.That(xmlCmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Orders]"),
                    "The {{TableXml}} twin must shred with .value()/.nodes() at compatibility level 100.");
            }

            using (var jsonCmd = conn.CreateCommand())
            {
                jsonCmd.CommandText = "SELECT [value] FROM OPENJSON(@j)";
                var p = jsonCmd.CreateParameter();
                p.ParameterName = "@j";
                p.Value = template.TableSchema;
                jsonCmd.Parameters.Add(p);
                Assert.That(() => jsonCmd.ExecuteScalar(), Throws.InstanceOf<SqlException>(),
                    "OPENJSON must parse-error at compatibility level 100 — the reason the XML twin exists.");
            }
        }
        finally
        {
            DropTransientDb(compat100Db);
        }
    }

    private void CreateDatabaseAtCompatLevel(string databaseName, int compatLevel)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"CREATE DATABASE [{databaseName}]; ALTER DATABASE [{databaseName}] SET COMPATIBILITY_LEVEL = {compatLevel};";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropTransientDb(string databaseName)
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $@"
IF DB_ID('{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch
        {
            // Best-effort cleanup — a missing or already-dropped DB is fine.
        }
    }
}
