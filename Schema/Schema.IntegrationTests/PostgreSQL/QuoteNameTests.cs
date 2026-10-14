// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// <c>SchemaSmith.QuoteName</c> is the delimiter every new PostgreSQL emission site uses. It must always quote, so the
/// emitted text of an ordinary name does not change, and it must produce an identifier the engine reads back as the
/// raw name, whatever the name holds.
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class QuoteNameTests
{
    [TestCase("orders", "\"orders\"")]
    [TestCase("Order Details", "\"Order Details\"")]
    [TestCase("we\"ird", "\"we\"\"ird\"")]
    [TestCase("\"", "\"\"\"\"")]
    public void QuoteName_AlwaysQuotes_AndDoublesAnEmbeddedQuote(string raw, string expected)
    {
        Assert.That(Quote(raw), Is.EqualTo(expected));
    }

    [Test]
    public void QuoteName_OfNull_IsNull()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"SchemaSmith\".\"QuoteName\"(NULL)";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value));
    }

    [TestCase("we\"ird name")]
    [TestCase("MixedCase")]
    [TestCase("select")]
    public void QuoteName_EmitsAnIdentifierTheEngineReadsBackAsTheRawName(string raw)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TEMP TABLE {Quote(raw)} (id INT)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM pg_class WHERE relpersistence = 't' AND relname = @raw";
        var p = cmd.CreateParameter();
        p.ParameterName = "raw";
        p.Value = raw;
        cmd.Parameters.Add(p);
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));
    }

    private static string Quote(string raw)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"SchemaSmith\".\"QuoteName\"(@raw)";
        var p = cmd.CreateParameter();
        p.ParameterName = "raw";
        p.Value = raw;
        cmd.Parameters.Add(p);
        return (string)cmd.ExecuteScalar();
    }

    private static System.Data.IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        return conn;
    }
}
