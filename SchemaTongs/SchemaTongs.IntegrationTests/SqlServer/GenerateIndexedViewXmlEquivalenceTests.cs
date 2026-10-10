// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
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
    // Runs on the Schema suite's shared kindled database, isolated by a fixture-owned schema. That database is
    // kindled with the JSON encoding, so GenerateIndexedViewJson is already there; the XML twin is not, and is
    // installed here.
    private readonly string _p = "ivx_" + Guid.NewGuid().ToString("N")[..8];
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _testConnectionString = Schema.IntegrationTests.SqlServer.FixtureSetup.GetMainDbConnectionString();

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Schema.IntegrationTests.SqlServer.TestSchemaDrop.Drop(cmd, _p);
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
        Exec(cmd, $"EXEC('CREATE SCHEMA [{_p}]')");
        Exec(cmd, $"CREATE TABLE [{_p}].SourceTable (Id INT NOT NULL, Name VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL)");
        Exec(cmd, $"CREATE UNIQUE CLUSTERED INDEX UDX_SourceTable ON [{_p}].SourceTable (Id)");
        Exec(cmd, $@"CREATE VIEW [{_p}].vTestSummary WITH SCHEMABINDING AS
SELECT Id, Name, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount
FROM [{_p}].SourceTable
GROUP BY Id, Name");
        Exec(cmd, $"CREATE UNIQUE CLUSTERED INDEX [IX_vTestSummary_Id] ON [{_p}].vTestSummary (Id)");
        Exec(cmd, $"CREATE NONCLUSTERED INDEX [IX_vTestSummary_Name] ON [{_p}].vTestSummary (Name) WITH (FILLFACTOR = 90)");

        var jsonView = JsonConvert.DeserializeObject<SqlServerIndexedView>(GenerateIndexedView(cmd, "GenerateIndexedViewJson", _p, "vTestSummary"))!;
        var xmlView = JsonConvert.DeserializeObject<SqlServerIndexedView>(
            ModelXmlSerializer.FromIngestXml(GenerateIndexedView(cmd, "GenerateIndexedViewXml", _p, "vTestSummary")))!;

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
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            Schema.IntegrationTests.SqlServer.TestSchemaDrop.Drop(cmd, _p);
            conn.Close();
        }
        catch (DbException) { /* best-effort cleanup must not mask a test result */ }
    }
}
