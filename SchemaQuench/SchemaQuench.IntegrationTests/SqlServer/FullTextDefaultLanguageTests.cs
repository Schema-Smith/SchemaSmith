// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// #606: a full-text index over an xml (or varbinary, image) column declared without LANGUAGE was dropped and repopulated
// on every deploy, because the live side rendered LANGUAGE for every column without a collation and the declaration had
// none. Such a column takes the server's default full-text language, which is what the declaration asked for.
[Category("SqlServer")]
public class FullTextDefaultLanguageTests : BaseTableQuenchTests
{
    private static string Json(string table, string columns) => $$"""
        [{ "Schema": "[dbo]", "Name": "[{{table}}]",
           "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Doc]", "DataType": "XML", "Nullable": true } ],
           "Indexes": [ { "Name": "[UDX_{{table}}]", "Unique": true, "IndexColumns": "[Id]" } ],
           "FullTextIndex": { "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_{{table}}]", "ChangeTracking": "OFF", "Columns": "{{columns}}" } }]
        """;

    [TestCase("TableQuench", false)]
    [TestCase("TableQuench", true)]
    [TestCase("IndexOnlyQuench", false)]
    [TestCase("IndexOnlyQuench", true)]
    [TestCase("IndexOnlyXmlQuench", false)]
    [TestCase("IndexOnlyXmlQuench", true)]
    public void AnXmlColumnAtTheDefaultLanguage_IsNotRebuilt_WhetherOrNotTheDeclarationNamesIt(string path, bool statesTheDefault)
    {
        var table = "FtDefLang" + path + (statesTheDefault ? "S" : "");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        try
        {
            Run(cmd, $"CREATE TABLE dbo.{table} (Id INT NOT NULL, Doc XML NULL); CREATE UNIQUE INDEX UDX_{table} ON dbo.{table} (Id);");
            var columns = statesTheDefault ? $"[Doc] LANGUAGE {DefaultLanguage(cmd)}" : "[Doc]";
            Deploy(cmd, path, Json(table, columns));
            Assert.That(FullTextAudits(cmd, table, path, Json(table, columns)), Is.Zero, "the second deploy leaves the index alone");
        }
        finally
        {
            Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        }
    }

    [TestCase("TableQuench")]
    [TestCase("IndexOnlyQuench")]
    [TestCase("IndexOnlyXmlQuench")]
    public void AStatedLanguageThatDiffers_StillRebuilds(string path)
    {
        var table = "FtOtherLang" + path;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        try
        {
            Run(cmd, $"CREATE TABLE dbo.{table} (Id INT NOT NULL, Doc XML NULL); CREATE UNIQUE INDEX UDX_{table} ON dbo.{table} (Id);");
            Deploy(cmd, path, Json(table, "[Doc]"));
            var other = DefaultLanguage(cmd) == 1041 ? 1033 : 1041;
            Assert.That(FullTextAudits(cmd, table, path, Json(table, $"[Doc] LANGUAGE {other}")), Is.GreaterThan(0), "a different language is a change");
        }
        finally
        {
            Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        }
    }

    private int FullTextAudits(IDbCommand cmd, string table, string path, string json)
    {
        Run(cmd, "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        Deploy(cmd, path, json);
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'fullTextIndex' AND ObjectName LIKE '%{table}%'";
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Deploy(IDbCommand cmd, string path, string json)
    {
        if (path == "TableQuench")
        {
            RunTableQuenchProc(cmd, json);
            return;
        }
        if (path == "IndexOnlyQuench")
        {
            Run(cmd, $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{_productName}', @TableDefinitions = N'{json.Replace("'", "''")}'");
            return;
        }
        foreach (var batch in SqlServerBatchSplitter.Split(IndexOnlyXmlQuenchTests.XmlTierIndexOnlyQuench())) Run(cmd, batch);
        var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table").Replace("'", "''");
        Run(cmd, $"EXEC SchemaSmith.IndexOnlyQuenchXmlTest @ProductName = '{_productName}', @TableDefinitions = N'{xml}'");
    }

    private static int DefaultLanguage(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT CONVERT(INT, value_in_use) FROM sys.configurations WHERE [name] = 'default full-text language'";
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    private IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn;
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
