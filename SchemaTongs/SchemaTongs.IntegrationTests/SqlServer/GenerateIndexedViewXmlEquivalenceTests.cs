// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

// Compare-side equivalence: GenerateIndexedViewXml (FOR XML PATH, for the pre-2016 binary floor) must
// extract an indexed view into the SAME SqlServerIndexedView model as GenerateIndexedViewJson. Both are
// created from their resources; the XML output is converted back to JSON via ModelXmlSerializer.FromIngestXml
// before deserialization. Empty-string scalars are normalized away (SerializeXNode maps an empty element to
// null; "" and absent are semantically identical for these placeholder fields).
[Category("SqlServer")]
[TestFixture]
public class GenerateIndexedViewXmlEquivalenceTests
{
    private string _integrationDb = "";
    private string _connectionString;
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = $"GenIvXml_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        _testConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], _integrationDb, config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}];";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        ForgeKindler.KindleOneFile(cmd, "SchemaSmith.GenerateIndexedViewJson.sql", Platform.SqlServer);
        ForgeKindler.KindleOneFile(cmd, "SchemaSmith.GenerateIndexedViewXml.sql", Platform.SqlServer);

        conn.Close();
    }

    [Test]
    public void GenerateIndexedViewXml_ExtractsSameModelAs_GenerateIndexedViewJson()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // CREATE SCHEMA / CREATE VIEW must each be the only statement in their batch — issue separately.
        Exec(cmd, "EXEC('CREATE SCHEMA [Test]')");
        Exec(cmd, "CREATE TABLE Test.SourceTable (Id INT NOT NULL, Name VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL)");
        Exec(cmd, "CREATE UNIQUE CLUSTERED INDEX UDX_SourceTable ON Test.SourceTable (Id)");
        Exec(cmd, @"CREATE VIEW Test.vTestSummary WITH SCHEMABINDING AS
SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount
FROM Test.SourceTable
GROUP BY Id, Name");
        Exec(cmd, "CREATE UNIQUE CLUSTERED INDEX [IX_vTestSummary_Id] ON Test.vTestSummary (Id)");
        Exec(cmd, "CREATE NONCLUSTERED INDEX [IX_vTestSummary_Name] ON Test.vTestSummary (Name) WITH (FILLFACTOR = 90)");

        var jsonView = JsonConvert.DeserializeObject<SqlServerIndexedView>(GenerateIndexedView(cmd, "GenerateIndexedViewJson", "Test", "vTestSummary"))!;
        var xmlView = JsonConvert.DeserializeObject<SqlServerIndexedView>(
            ModelXmlSerializer.FromIngestXml(GenerateIndexedView(cmd, "GenerateIndexedViewXml", "Test", "vTestSummary")))!;

        Assert.That(Normalize(xmlView), Is.EqualTo(Normalize(jsonView)));

        conn.Close();
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string GenerateIndexedView(IDbCommand cmd, string func, string schema, string view)
    {
        cmd.CommandText = $"SELECT [SchemaSmith].[{func}]('{schema}', '{view}')";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    // Serialize to JSON and drop empty-string scalars: the XML encoding round-trips an empty value as an
    // empty element, which SerializeXNode maps to null (then omitted); "" and absent are equivalent here.
    private static string Normalize(SqlServerIndexedView view)
    {
        var token = JToken.Parse(JsonConvert.SerializeObject(view));
        StripEmptyStrings(token);
        return token.ToString(Formatting.Indented);
    }

    private static void StripEmptyStrings(JToken node)
    {
        switch (node)
        {
            case JObject o:
                foreach (var p in o.Properties().ToList())
                {
                    if (p.Value is JValue { Type: JTokenType.String } v && (string)v.Value! == "")
                        p.Remove();
                    else
                        StripEmptyStrings(p.Value);
                }
                break;
            case JArray a:
                foreach (var item in a) StripEmptyStrings(item);
                break;
        }
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _integrationDb);
        conn.Close();
    }
}
