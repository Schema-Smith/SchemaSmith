// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// A data merge into a schema, table and columns whose names hold every character a name can carry that SQL treats
/// specially: a quote, a percent sign, both brackets, a dot, a backslash, a comma, a space and a non-ASCII letter.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class MergeScriptHelperHostileNameTests
{
    private const string H = "a'b%c[d]e.f\\g,h i éΩ";
    private const string Schema = "s" + H, Table = "t" + H, Key = "k" + H, Value = "v" + H, Other = "o" + H;
    private IDbConnection _connection = null!;

    private static string B(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string J(string name) => name.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Target => B(Schema) + "." + B(Table);
    private static string X(string name, string value) =>
        $"<c n=\"{System.Security.SecurityElement.Escape(name)}\">{value}</c>";

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        Exec($"IF SCHEMA_ID(N'{Schema.Replace("'", "''")}') IS NULL EXEC(N'CREATE SCHEMA {B(Schema).Replace("'", "''")}');");
        Exec($"DROP TABLE IF EXISTS {Target}; CREATE TABLE {Target} ({B(Key)} INT NOT NULL PRIMARY KEY, {B(Value)} NVARCHAR(40) NULL, {B(Other)} INT NULL);");
        Exec($"INSERT {Target} ({B(Key)}, {B(Value)}, {B(Other)}) VALUES (1, N'one', 1), (2, N'two', 2);");
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Exec($"DROP TABLE IF EXISTS {Target}; IF SCHEMA_ID(N'{Schema.Replace("'", "''")}') IS NOT NULL EXEC(N'DROP SCHEMA {B(Schema).Replace("'", "''")}');");
        }
        finally
        {
            _connection.Dispose();
        }
    }

    // The key list the caller names, or the one the catalog gives; the rows as JSON or XML.
    // With a deferred column, its first pass runs ahead of the merge, as delivery runs it for a self-referencing table.
    [TestCase("Json", false, false)]
    [TestCase("Xml", false, false)]
    [TestCase("Json", true, false)]
    [TestCase("Json", false, true)]
    [TestCase("Xml", false, true)]
    public void AMergeInsertsUpdatesAndDeletes_UnderTheExactNames(string encoding, bool keysFromTheCatalog, bool deferred)
    {
        var data = encoding == "Json"
            ? $"[{{\"{J(Key)}\":1,\"{J(Value)}\":\"uno\",\"{J(Other)}\":10}},{{\"{J(Key)}\":3,\"{J(Value)}\":\"tres\",\"{J(Other)}\":30}}]"
            : $"<rows><row>{X(Key, "1")}{X(Value, "uno")}{X(Other, "10")}</row><row>{X(Key, "3")}{X(Value, "tres")}{X(Other, "30")}</row></rows>";
        using var command = _connection.CreateCommand();
        var keys = keysFromTheCatalog ? MergeScriptHelper.GetKeyColumns(Platform.SqlServer, command, Schema, Table) : B(Key);
        var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, Schema, Table, data, keys,
            mergeUpdate: true, mergeDelete: true, disableTriggers: false, tokenizeScripts: false, mergeFilter: null,
            contentEncoding: encoding);

        if (deferred)
        {
            command.CommandText = DeferredMergeBuilder.Build(new MergeScriptHelperAdapter(Platform.SqlServer), command, "SqlServer", Schema, Table,
                data, keys, false, new System.Collections.Generic.List<string> { Other }, contentEncoding: encoding);
            command.ExecuteNonQuery();
        }
        command.CommandText = script;
        command.ExecuteNonQuery();

        command.CommandText = $"SELECT STRING_AGG(CONCAT({B(Key)}, ':', {B(Value)}, ':', {B(Other)}), '|') WITHIN GROUP (ORDER BY {B(Key)}) FROM {Target}";
        Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("1:uno:10|3:tres:30"),
            "row 1 updated, row 2 deleted, row 3 inserted");
    }

    private void Exec(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
