// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// A data merge into a schema, table and columns whose names hold every character a name can carry that SQL treats
/// specially: a quote, a percent sign, the double quote, a dot, a backslash, a comma, a space and non-ASCII letters.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class MergeScriptHelperHostileNameTests
{
    private const string H = "a'b%c\"d.e\\f,g h éΩ";
    private const string Schema = "s" + H, Table = "t" + H, Key = "k" + H, Value = "v" + H, Other = "o" + H;
    private IDbConnection _connection = null!;

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string J(string name) => name.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string X(string name, string value) => $"<c n=\"{System.Security.SecurityElement.Escape(name)}\">{value}</c>";
    private static string Target => Q(Schema) + "." + Q(Table);

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        Exec($"CREATE SCHEMA IF NOT EXISTS {Q(Schema)}; DROP TABLE IF EXISTS {Target};" +
             $" CREATE TABLE {Target} ({Q(Key)} INT NOT NULL PRIMARY KEY, {Q(Value)} VARCHAR(40) NULL, {Q(Other)} INT NULL);" +
             $" INSERT INTO {Target} VALUES (1, 'one', 1), (2, 'two', 2);");
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Exec($"DROP SCHEMA IF EXISTS {Q(Schema)} CASCADE;");
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
        command.CommandText = "SHOW server_version_num";
        var version = Convert.ToInt32(command.ExecuteScalar()) / 10000;   // the major: below 15 there is no MERGE
        var keys = keysFromTheCatalog ? MergeScriptHelper.GetKeyColumns(Platform.PostgreSQL, command, Schema, Table) : Q(Key);
        var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, Schema, Table, data, keys,
            mergeUpdate: true, mergeDelete: true, disableTriggers: false, tokenizeScripts: false, mergeFilter: null,
            pgServerVersionNum: version, contentEncoding: encoding);

        if (deferred)
        {
            command.CommandText = DeferredMergeBuilder.Build(new MergeScriptHelperAdapter(Platform.PostgreSQL), command, "PostgreSQL", Schema, Table,
                data, keys, false, new System.Collections.Generic.List<string> { Other }, pgServerVersionNum: version, contentEncoding: encoding);
            command.ExecuteNonQuery();
        }
        command.CommandText = script;
        command.ExecuteNonQuery();

        command.CommandText = $"SELECT string_agg(concat({Q(Key)}, ':', {Q(Value)}, ':', {Q(Other)}), '|' ORDER BY {Q(Key)}) FROM {Target}";
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
