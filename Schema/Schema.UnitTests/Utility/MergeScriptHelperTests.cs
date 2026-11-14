// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.Isolators;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class MergeScriptHelperTests
{
    #region B3 — XmlPayloadToJson converter (MySQL/MariaDB XML delivery route)

    [Test]
    public void XmlPayloadToJson_ProducesTheJsonRowSourceEquivalent()
    {
        var json = MergeScriptHelper.XmlPayloadToJson(
            "<rows><row><c n=\"code\">A001</c><c n=\"qty\">7</c></row></rows>");
        Assert.That(json, Is.EqualTo(@"[{""code"":""A001"",""qty"":""7""}]"));
    }

    [Test]
    public void XmlPayloadToJson_EscapesJsonMetacharactersFromXmlText()
    {
        var json = MergeScriptHelper.XmlPayloadToJson(
            "<rows><row><c n=\"name\">He said \"hi\" \\ bye</c></row></rows>");
        Assert.That(json, Does.Contain(@"\""hi\"""));
        Assert.That(json, Does.Contain(@"\\"));
    }

    [Test]
    public void XmlPayloadToJson_PreservesAnAbsentElementAsAbsent()
    {
        var json = MergeScriptHelper.XmlPayloadToJson(
            "<rows><row><c n=\"code\">A001</c></row><row><c n=\"code\">B002</c><c n=\"qty\">3</c></row></rows>");
        Assert.That(json, Is.EqualTo(@"[{""code"":""A001""},{""code"":""B002"",""qty"":""3""}]"));
    }

    #endregion

    #region B4b — JsonPayloadToXml converter (non-SQL-Server DeliveryEncoding=Xml extraction)

    [Test]
    public void JsonPayloadToXml_RoundTripsWithXmlPayloadToJson()
    {
        const string json = @"[{""code"":""A001"",""name"":""An \""odd\"" name"",""qty"":""7""}]";
        var xml = MergeScriptHelper.JsonPayloadToXml(json);
        Assert.That(MergeScriptHelper.XmlPayloadToJson(xml), Is.EqualTo(json));
    }

    [Test]
    public void JsonPayloadToXml_OmitsNullColumnsRatherThanEmittingEmptyElements()
    {
        var xml = MergeScriptHelper.JsonPayloadToXml(@"[{""code"":""A001"",""note"":null}]");
        Assert.That(xml, Does.Not.Contain("note"));
    }

    [Test]
    public void JsonPayloadToXml_EscapesXmlMetacharacters()
    {
        var xml = MergeScriptHelper.JsonPayloadToXml(@"[{""name"":""a < b & c""}]");
        Assert.That(MergeScriptHelper.XmlPayloadToJson(xml), Is.EqualTo(@"[{""name"":""a < b & c""}]"));
    }

    [Test]
    public void JsonPayloadToXml_BooleanColumn_WritesZeroOrOneNotTrueFalse()
    {
        // The shred casts <c> text straight to the target SQL type via XQuery .value(); a literal
        // "true"/"false" string fails a BIT cast there, so a JSON boolean must normalize to "0"/"1" —
        // matching exactly what GetTableDataXmlSqlServer itself emits for a bit column.
        var xml = MergeScriptHelper.JsonPayloadToXml(@"[{""active"":true},{""active"":false}]");
        Assert.That(xml, Does.Contain("<c n=\"active\">1</c>"));
        Assert.That(xml, Does.Contain("<c n=\"active\">0</c>"));
        Assert.That(xml, Does.Not.Contain("true").IgnoreCase);
        Assert.That(xml, Does.Not.Contain("false").IgnoreCase);
    }

    // Numbers went through double: a wide numeric lost its tail digits, and 100.50 became 100.5.
    [Test]
    public void JsonPayloadToXml_NumberColumn_KeepsItsLiteralText()
    {
        var xml = MergeScriptHelper.JsonPayloadToXml(
            @"[{""big"":12345678901234567890.123456789,""cents"":100.50,""neg"":-1.5E-7,""n"":42,""doc"":{""x"":1.25,""s"":""7""}}]");
        Assert.Multiple(() =>
        {
            Assert.That(xml, Does.Contain("<c n=\"big\">12345678901234567890.123456789</c>"));
            Assert.That(xml, Does.Contain("<c n=\"cents\">100.50</c>"));
            Assert.That(xml, Does.Contain("<c n=\"neg\">-1.5E-7</c>"));
            Assert.That(xml, Does.Contain("<c n=\"n\">42</c>"));
            Assert.That(xml, Does.Contain("<c n=\"doc\">{\"x\":1.25,\"s\":\"7\"}</c>"),
                "a number inside a json column's value is part of that document and stays a number, and the document is " +
                "written compact, as the JSON path delivers it (#567)");
        });
    }

    // #567: on the MySQL family a row's object or array value for a text column is handed over as a string of its own text.
    [Test]
    public void StringifyRowStructures_RewritesOnlyTheNamedColumnsDirectlyInARow()
    {
        var json = "[{\"doc\":{\"a\":\"x]\\\"y\",\"doc\":[1,2.50]},\"keep\":{\"b\":1},\"n\":[1]},{\"doc\":\"plain\",\"n\":null}]";

        var result = MergeScriptHelper.StringifyRowStructures(json, new HashSet<string> { "doc", "n" });

        Assert.That(result, Is.EqualTo(
            "[{\"doc\":\"{\\\"a\\\":\\\"x]\\\\\\\"y\\\",\\\"doc\\\":[1,2.50]}\",\"keep\":{\"b\":1},\"n\":\"[1]\"},{\"doc\":\"plain\",\"n\":null}]"));
        Assert.That(JsonText.ParseArray(result)[0]["doc"]!.ToString(), Is.EqualTo("{\"a\":\"x]\\\"y\",\"doc\":[1,2.50]}"),
            "the string holds the value's exact text, a bracket and an escaped quote inside a string included");
    }

    // Read with Newtonsoft's defaults, a date-like string became a DateTime and was written back in the host's culture
    // and time zone: the fraction dropped, an offset shifted, and plain text that looked like a date was rewritten.
    [Test]
    public void JsonPayloadToXml_DateLikeStrings_KeepTheirLiteralText()
    {
        var xml = MergeScriptHelper.JsonPayloadToXml(
            @"[{""ts"":""2020-01-02T03:04:05.123456"",""tz"":""2020-01-02T03:04:05+05:00"",""txt"":""2020-01-02 03:04:05""}]");
        Assert.Multiple(() =>
        {
            Assert.That(xml, Does.Contain("<c n=\"ts\">2020-01-02T03:04:05.123456</c>"));
            Assert.That(xml, Does.Contain("<c n=\"tz\">2020-01-02T03:04:05+05:00</c>"));
            Assert.That(xml, Does.Contain("<c n=\"txt\">2020-01-02 03:04:05</c>"));
        });
    }

    [Test]
    public void JsonPayloadToXml_EmptyOrNullPayload_ReturnsEmptyString()
    {
        // Mirrors GetTableDataXmlSqlServer's own "no data" result, so DataTongs' existing
        // IsNullOrEmpty(tableData) empty-content check recognizes it without any new special-casing.
        Assert.That(MergeScriptHelper.JsonPayloadToXml(""), Is.EqualTo(""));
        Assert.That(MergeScriptHelper.JsonPayloadToXml("null"), Is.EqualTo(""));
        Assert.That(MergeScriptHelper.JsonPayloadToXml("[]"), Is.EqualTo(""));
    }

    [Test]
    public void JsonPayloadToXml_MultipleRows_EachRowEmittedSeparately()
    {
        var xml = MergeScriptHelper.JsonPayloadToXml(
            @"[{""code"":""A001""},{""code"":""B002"",""qty"":""3""}]");
        Assert.That(MergeScriptHelper.XmlPayloadToJson(xml),
            Is.EqualTo(@"[{""code"":""A001""},{""code"":""B002"",""qty"":""3""}]"));
    }

    #endregion

    #region MariaDB 10.2-10.5 chunked shred

    private static string BuildPayload(int rows) =>
        "[" + string.Join(",", Enumerable.Range(0, rows).Select(i => $"{{\"Id\":{i},\"Name\":\"n{i}\"}}")) + "]";

    [Test]
    public void TryChunkMySqlPayload_OnlyChunksTheCtePathWithRealDataAboveTheThreshold()
    {
        Assert.Multiple(() =>
        {
            // JSON_TABLE parses the document once, so chunking buys nothing and must not kick in.
            Assert.That(MergeScriptHelper.TryChunkMySqlPayload(true, false, BuildPayload(5000), out _), Is.False,
                "A JSON_TABLE target must keep the single-statement form.");
            // The payload is a {{table.tabledata}} placeholder at build time -- nothing to slice.
            Assert.That(MergeScriptHelper.TryChunkMySqlPayload(false, true, BuildPayload(5000), out _), Is.False,
                "Tokenized scripts have no payload to chunk.");
            // Small payloads are already fast; the extra statements would just be noise.
            Assert.That(MergeScriptHelper.TryChunkMySqlPayload(false, false, BuildPayload(10), out _), Is.False,
                "A payload under the threshold must not be chunked.");
            Assert.That(MergeScriptHelper.TryChunkMySqlPayload(false, false, "not json", out _), Is.False,
                "Unparseable data must fall through to the existing path, not throw.");
            Assert.That(MergeScriptHelper.TryChunkMySqlPayload(false, false, BuildPayload(500), out var rows), Is.True);
            Assert.That(rows.Count, Is.EqualTo(500));
        });
    }

    [Test]
    public void BuildChunkedMergeMySql_EmitsOneStatementPerChunk_AndExactlyOneDelete()
    {
        // The load-bearing invariant: the full-sync DELETE is a NOT EXISTS over the payload, so running it
        // per chunk would delete every row that lives in another chunk. It must run ONCE, against a key
        // set every chunk contributed to.
        // Sized off the constant so retuning the chunk size doesn't silently invalidate the assertions.
        var expectedChunks = 3;
        var rowCount = MergeScriptHelper.MariaDbShredChunkRows * expectedChunks;
        var rows = MergeScriptHelper.SplitJsonArrayRows(BuildPayload(rowCount));
        var columns = new List<MergeScriptHelper.MySqlColumnInfo>
        {
            new() { Name = "Id", DataType = "int" }
        };
        var sql = MergeScriptHelper.BuildChunkedMergeMySql("db", "t", "`Id`, `Name`", "jt.`Id`, jt.`Name`",
            "(SELECT 1) AS jt", [new() { Name = "Id", DataType = "int" }, new() { Name = "Name", DataType = "varchar" }], "`Id`", rows, columns,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);

        Assert.Multiple(() =>
        {
            Assert.That(CountOf(sql, "SET @json_data"), Is.EqualTo(expectedChunks), "One payload assignment per chunk.");
            Assert.That(CountOf(sql, "INSERT INTO `db`.`t`"), Is.EqualTo(expectedChunks), "One upsert per chunk.");
            Assert.That(CountOf(sql, "DELETE Target"), Is.EqualTo(1), "The delete must run exactly once.");
            Assert.That(CountOf(sql, "CREATE TEMPORARY TABLE `_ss_merge_keys`"), Is.EqualTo(1));
            Assert.That(CountOf(sql, "DROP TEMPORARY TABLE `_ss_merge_keys`"), Is.EqualTo(1));
            Assert.That(CountOf(sql, "INSERT INTO `_ss_merge_keys`"), Is.EqualTo(expectedChunks), "Every chunk contributes its keys.");
            // The delete must come after the last chunk, or it would see an incomplete key set.
            Assert.That(sql.IndexOf("DELETE Target", StringComparison.Ordinal),
                Is.GreaterThan(sql.LastIndexOf("INSERT INTO `_ss_merge_keys`", StringComparison.Ordinal)),
                "The delete must follow every key-collecting insert.");
        });
    }

    // The chunked path re-serialises the rows it parsed. Read with the default reader, a date-like value became a
    // DateTime and was sent converted to the host's time zone.
    [Test]
    public void BuildChunkedMergeMySql_SendsADateLikeValueExactlyAsDelivered()
    {
        const string when = "2026-10-07T12:00:00.000+02:00";
        var payload = "[" + string.Join(",", Enumerable.Range(0, MergeScriptHelper.MariaDbShredChunkRows + 1)
            .Select(i => $"{{\"Id\":{i},\"When\":\"{when}\"}}")) + "]";
        Assert.That(MergeScriptHelper.TryChunkMySqlPayload(false, false, payload, out var rows), Is.True);
        var columns = new List<MergeScriptHelper.MySqlColumnInfo> { new() { Name = "Id", DataType = "int" } };

        var sql = MergeScriptHelper.BuildChunkedMergeMySql("db", "t", "`Id`, `When`", "jt.`Id`, jt.`When`",
            "(SELECT 1) AS jt", null, "`Id`", rows, columns, null, null);

        Assert.That(CountOf(sql, when), Is.EqualTo(MergeScriptHelper.MariaDbShredChunkRows + 1));
    }

    // Re-serialised from parsed rows, a number went through double: a wide DECIMAL lost its tail digits.
    [Test]
    public void BuildChunkedMergeMySql_SendsEachRowExactlyAsDelivered()
    {
        var rowText = Enumerable.Range(0, MergeScriptHelper.MariaDbShredChunkRows + 1)
            .Select(i => "{\"Id\":" + i + ",\"Amount\":12345678901234567890.123456789,\"Doc\":{\"a\":[1.50,\"},{\\\"\"]}}")
            .ToList();
        Assert.That(MergeScriptHelper.TryChunkMySqlPayload(false, false, "[" + string.Join(", ", rowText) + "]", out var rows), Is.True);
        Assert.That(rows, Is.EqualTo(rowText), "each row is split out as the exact text it arrived in");
        var columns = new List<MergeScriptHelper.MySqlColumnInfo> { new() { Name = "Id", DataType = "int" } };

        var sql = MergeScriptHelper.BuildChunkedMergeMySql("db", "t", "`Id`, `Amount`", "jt.`Id`, jt.`Amount`",
            "(SELECT 1) AS jt", null, "`Id`", rows, columns, null, null);

        Assert.That(CountOf(sql, "12345678901234567890.123456789"), Is.EqualTo(MergeScriptHelper.MariaDbShredChunkRows + 1));
    }

    [Test]
    public void BuildChunkedMergeMySql_WithoutDelete_EmitsNoKeyTable()
    {
        var noDeleteChunks = 2;
        var rows = MergeScriptHelper.SplitJsonArrayRows(BuildPayload(MergeScriptHelper.MariaDbShredChunkRows * noDeleteChunks));
        var columns = new List<MergeScriptHelper.MySqlColumnInfo> { new() { Name = "Id", DataType = "int" } };
        var sql = MergeScriptHelper.BuildChunkedMergeMySql("db", "t", "`Id`", "jt.`Id`",
            "(SELECT 1) AS jt", null, "`Id`", rows, columns, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(CountOf(sql, "SET @json_data"), Is.EqualTo(noDeleteChunks));
            Assert.That(CountOf(sql, "INSERT IGNORE INTO `db`.`t`"), Is.EqualTo(noDeleteChunks), "No update columns => INSERT IGNORE.");
            Assert.That(sql, Does.Not.Contain("_ss_merge_keys"), "No delete half means no key table.");
            Assert.That(sql, Does.Not.Contain("DELETE Target"));
        });
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    #endregion

    #region EncodeTableDisplayName / BuildContentFileToken Tests (#390 — the single shared derivation)

    [Test]
    public void EncodeTableDisplayName_NonEmptySchema_NotForced_QualifiesWithSchema()
    {
        Assert.That(MergeScriptHelper.EncodeTableDisplayName("dbo", "Widget", forceUnqualified: false),
            Is.EqualTo("dbo.Widget"));
    }

    [Test]
    public void EncodeTableDisplayName_ForceUnqualified_IgnoresNonEmptySchema()
    {
        Assert.That(MergeScriptHelper.EncodeTableDisplayName("dbo", "Widget", forceUnqualified: true),
            Is.EqualTo("Widget"));
    }

    [Test]
    public void EncodeTableDisplayName_EmptySchema_UnqualifiesEvenWhenNotForced()
    {
        // The bug case: the old per-engine derivation only unqualified when an override was set,
        // so an empty schema outside schema-template mode produced a leading-dot mismatch
        // ("." + tableName) against the (correctly unqualified) filename. Empty schema must
        // unqualify on its own, independent of forceUnqualified.
        Assert.That(MergeScriptHelper.EncodeTableDisplayName("", "Widget", forceUnqualified: false),
            Is.EqualTo("Widget"));
    }

    [Test]
    public void EncodeTableDisplayName_NameNeedingEncoding_EncodesBothSchemaAndTable()
    {
        // The other bug case: the old per-engine derivation embedded raw names, disagreeing with
        // the FileNameEncoder-encoded filename DataTongs writes.
        Assert.That(MergeScriptHelper.EncodeTableDisplayName("Sales:Q1", "Report:Detail", forceUnqualified: false),
            Is.EqualTo("Sales%3AQ1.Report%3ADetail"));
    }

    [Test]
    public void BuildContentFileToken_AppendsTabledataSuffix()
    {
        Assert.That(MergeScriptHelper.BuildContentFileToken("dbo", "Widget", forceUnqualified: false),
            Is.EqualTo("dbo.Widget.tabledata"));
    }

    #endregion

    #region GetKeyColumns Tests

    [Test]
    public void GetKeyColumns_SqlServer_ExecutesPlatformSpecificQuery()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Id],[Name]");

        var result = MergeScriptHelper.GetKeyColumns(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.That(result, Is.EqualTo("[Id],[Name]"));
        Assert.That(cmd.CommandText, Does.Contain("sys.indexes"));
        Assert.That(cmd.CommandText, Does.Contain("COL_NAME"));
    }

    [Test]
    public void GetKeyColumns_PostgreSQL_ExecutesPlatformSpecificQuery()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("\"id\",\"name\"");

        var result = MergeScriptHelper.GetKeyColumns(Platform.PostgreSQL, cmd, "public", "test_table");

        Assert.That(result, Is.EqualTo("\"id\",\"name\""));
        Assert.That(cmd.CommandText, Does.Contain("pg_index"));
        Assert.That(cmd.CommandText, Does.Contain("pg_attribute"));
    }

    [Test]
    public void GetKeyColumns_MySQL_ExecutesPlatformSpecificQuery()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("`id`,`name`");

        var result = MergeScriptHelper.GetKeyColumns(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Is.EqualTo("`id`,`name`"));
        Assert.That(cmd.CommandText, Does.Contain("TABLE_CONSTRAINTS"));
        Assert.That(cmd.CommandText, Does.Contain("PRIMARY KEY"));
    }

    [Test]
    public void GetKeyColumns_SqlServer_TrimsBrackets()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Id]");

        MergeScriptHelper.GetKeyColumns(Platform.SqlServer, cmd, "[dbo]", "[TestTable]");

        Assert.That(cmd.CommandText, Does.Not.Contain("[["));
    }

    [Test]
    public void GetKeyColumns_PostgreSQL_TrimsQuotes()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("\"id\"");
        var bound = CaptureBoundParameters(cmd);

        MergeScriptHelper.GetKeyColumns(Platform.PostgreSQL, cmd, "\"public\"", "\"test\"");

        // Quotes are trimmed and the identifiers are passed as parameters, not interpolated.
        Assert.That(bound.Any(p => p.Name == "@schema" && Equals(p.Value, "public")), Is.True);
        Assert.That(bound.Any(p => p.Name == "@table" && Equals(p.Value, "test")), Is.True);
        Assert.That(cmd.CommandText, Does.Not.Contain("'public'"));
        Assert.That(cmd.CommandText, Does.Not.Contain("'test'"));
    }

    [Test]
    public void GetKeyColumns_MySQL_TrimsBackticks()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("`id`");
        var bound = CaptureBoundParameters(cmd);

        MergeScriptHelper.GetKeyColumns(Platform.MySQL, cmd, "`testdb`", "`test`");

        // Backticks are trimmed and the identifiers are passed as parameters, not interpolated.
        Assert.That(bound.Any(p => p.Name == "@db" && Equals(p.Value, "testdb")), Is.True);
        Assert.That(bound.Any(p => p.Name == "@table" && Equals(p.Value, "test")), Is.True);
        Assert.That(cmd.CommandText, Does.Not.Contain("'testdb'"));
        Assert.That(cmd.CommandText, Does.Not.Contain("'test'"));
    }

    [Test]
    public void GetKeyColumns_NullResult_ReturnsEmptyString()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(null);

        var result = MergeScriptHelper.GetKeyColumns(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.That(result, Is.EqualTo(""));
    }

    #endregion

    #region CompatEncoding override reaches the merge-script cliff

    // The merge-script builders detect the STRING_AGG/OPENJSON cliff by probing the server, and that probe
    // was the ONLY input -- so DataTongs, whose output IS the product, could not be made to build a legacy
    // delivery script on modern hardware. It is the same decision the model-ingest encoding makes (compat
    // < 130 OR server major < 14), which only SchemaQuench and SchemaTongs could force.
    //
    // Asserted at the outcome: whether the probe runs at all, and which aggregation the built
    // query uses -- not "the resolver returned X".
    private static (IDbCommand cmd, List<string> statements) RecordingSqlServerCommand(string scalar)
    {
        var statements = new List<string>();
        var cmd = Substitute.For<IDbCommand>();
        cmd.When(c => c.CommandText = Arg.Any<string>()).Do(ci => statements.Add(ci.Arg<string>()));
        cmd.Parameters.Returns(Substitute.For<IDataParameterCollection>());
        cmd.CreateParameter().Returns(_ => Substitute.For<IDbDataParameter>());
        cmd.ExecuteScalar().Returns(scalar);
        var reader = Substitute.For<IDataReader>();
        reader.Read().Returns(false);
        cmd.ExecuteReader().Returns(reader);
        return (cmd, statements);
    }

    private static void RegisterCompatEncoding(string sourceValue)
    {
        var config = Substitute.For<IConfigurationRoot>();
        config[Arg.Any<string>()].Returns(_ => null);
        config["Source:CompatEncoding"].Returns(sourceValue);
        FactoryContainer.Register<IConfigurationRoot>(config);
    }

    [TearDown]
    public void ClearCompatEncodingOverride() => FactoryContainer.Clear();

    [Test]
    public void MergeScriptCliff_WithNoOverride_ProbesTheServer()
    {
        // The control. Without it, the two tests below could pass because the probe never runs for some
        // unrelated reason, and they would be pinning nothing.
        var (cmd, statements) = RecordingSqlServerCommand("[Id]");

        MergeScriptHelper.GetKeyColumns(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.That(statements.Any(t => t.Contains("compatibility_level")), Is.True,
            "with no override the cliff must still be detected from the server");
    }

    // auto, or a value that is not legacy or modern, forces nothing: the server still decides.
    [TestCase("auto")]
    [TestCase("Json")]
    public void MergeScriptCliff_AValueThatForcesNothing_StillProbesTheServer(string value)
    {
        RegisterCompatEncoding(value);
        var (cmd, statements) = RecordingSqlServerCommand("[Id]");

        MergeScriptHelper.GetKeyColumns(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.That(statements.Any(t => t.Contains("compatibility_level")), Is.True,
            $"'{value}' must not stand in for the server's answer");
    }

    [Test]
    public void MergeScriptCliff_ForcedLegacy_SkipsTheProbeAndAvoidsStringAgg()
    {
        RegisterCompatEncoding("legacy");
        var (cmd, statements) = RecordingSqlServerCommand("[Id]");

        MergeScriptHelper.GetKeyColumns(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.Multiple(() =>
        {
            Assert.That(statements.Any(t => t.Contains("compatibility_level")), Is.False,
                "a forced encoding is the answer -- asking the server as well would let the server override it");
            Assert.That(statements.Any(t => t.Contains("STRING_AGG")), Is.False,
                "legacy must build the ordered row-by-row form, which is the whole point of forcing it");
        });
    }

    [Test]
    public void MergeScriptCliff_ForcedModern_SkipsTheProbeAndUsesStringAgg()
    {
        RegisterCompatEncoding("modern");
        var (cmd, statements) = RecordingSqlServerCommand("[Id]");

        MergeScriptHelper.GetKeyColumns(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.Multiple(() =>
        {
            Assert.That(statements.Any(t => t.Contains("compatibility_level")), Is.False);
            Assert.That(statements.Any(t => t.Contains("STRING_AGG")), Is.True,
                "both directions are pinned -- an override that only ever forced legacy would pass a one-sided test");
        });
    }

    #endregion

    #region BuildMergeScript - SQL Server Tests

    [Test]
    public void BuildMergeScript_SqlServer_InsertOnly_ContainsMergeAndInsert()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int"), new("Name", "nvarchar", 100)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("MERGE INTO [dbo].[TestTable] AS Target"));
        Assert.That(result, Does.Contain("OPENJSON(@v_json)"));
        Assert.That(result, Does.Contain("WHEN NOT MATCHED BY TARGET THEN"));
        Assert.That(result, Does.Contain("INSERT"));
        Assert.That(result, Does.Not.Contain("WHEN MATCHED"));
        Assert.That(result, Does.Not.Contain("DELETE"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_InsertUpdate_ContainsMatchedClause()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int"), new("Name", "nvarchar", 100)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN MATCHED AND"));
        Assert.That(result, Does.Contain("UPDATE SET"));
        Assert.That(result, Does.Contain("WHEN NOT MATCHED BY TARGET THEN"));
        Assert.That(result, Does.Not.Contain("WHEN NOT MATCHED BY SOURCE"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_InsertUpdateDelete_ContainsDeleteClause()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int"), new("Name", "nvarchar", 100)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN NOT MATCHED BY SOURCE"));
        Assert.That(result, Does.Contain("DELETE"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_WithMergeFilter_IncludesFilterInDelete()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int"), new("Name", "nvarchar", 100)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: "Target.[Type] = 'Active'");

        Assert.That(result, Does.Contain("AND (Target.[Type] = 'Active')"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_WithDisableTriggers_IncludesTriggerStatements()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: true,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("DISABLE TRIGGER ALL"));
        Assert.That(result, Does.Contain("ENABLE TRIGGER ALL"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_WithIdentityInsert_IncludesIdentityStatements()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int", Identity: true)], flags: 1);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("SET IDENTITY_INSERT [dbo].[TestTable] ON"));
        Assert.That(result, Does.Contain("SET IDENTITY_INSERT [dbo].[TestTable] OFF"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_WithTokenize_UsesTokenPlaceholder()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null);

        Assert.That(result, Does.Contain("{{dbo.TestTable.tabledata}}"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_WithTokenize_ContentFileTokenParameter_IsEmbeddedVerbatim()
    {
        // #390 "derive the key once": DataTongs computes the key alongside the .tabledata filename
        // and passes it here; BuildMergeScript must embed exactly that value, not re-derive it.
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null,
            contentFileToken: "Sales%3ATestTable.tabledata");

        Assert.That(result, Does.Contain("{{Sales%3ATestTable.tabledata}}"));
        Assert.That(result, Does.Not.Contain("{{dbo.TestTable.tabledata}}"),
            "The passed-in token must win over the platform's own derivation.");
    }

    [Test]
    public void BuildMergeScript_SqlServer_EscapesSingleQuotes()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int"), new("Name", "nvarchar", 100)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Name\":\"O'Brien\"}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("O''Brien"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_NullableKeyColumn_GeneratesIsNullComparison()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "*[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("IS NULL AND Target.[Id] IS NULL"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_TrimsBracketsFromSchemaAndTable()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "[dbo]", "[TestTable]", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("MERGE INTO [dbo].[TestTable] AS Target"));
        Assert.That(result, Does.Not.Contain("[[dbo]]"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_SchemaTemplate_EmitsResolvedSchemaQualifier()
    {
        // The DataDeliveryProcessor is responsible for substituting {{SchemaName}} ->
        // iteration value BEFORE calling BuildMergeScript. This test verifies that when
        // the caller passes the already-resolved schema, the MERGE INTO clause carries
        // the resolved name and no token leak occurs.
        var cmd = CreateSqlServerShapeCommand([new("LookupID", "int"), new("Code", "nvarchar", 32)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "tenant_acme", "Lookups", "[{\"LookupID\":1}]", "[LookupID]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("MERGE INTO [tenant_acme].[Lookups] AS Target"));
        Assert.That(result, Does.Not.Contain("{{SchemaName}}"),
            "Resolved schema name must replace the iteration token before reaching the helper.");
    }

    #endregion

    #region BuildMergeScript - PostgreSQL Tests

    [Test]
    public void BuildMergeScript_PostgreSQL_InsertOnly_ContainsMergeAndInsert()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("name", "varchar", "character varying(100)")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[{\"id\":1}]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("MERGE INTO"));
        Assert.That(result, Does.Contain("\"public\".\"test_table\""));
        Assert.That(result, Does.Contain("JSON_ARRAY_ELEMENTS"));
        Assert.That(result, Does.Contain("WHEN NOT MATCHED"));
        Assert.That(result, Does.Contain("INSERT"));
        Assert.That(result, Does.Contain("DO $SchemaSmithData$"));
        Assert.That(result, Does.Contain("END $SchemaSmithData$ LANGUAGE plpgsql"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_InsertUpdate_ContainsMatchedClause()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("name", "varchar", "character varying(100)")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[{\"id\":1}]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN MATCHED AND"));
        Assert.That(result, Does.Contain("UPDATE SET"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_InsertUpdateDelete_ContainsDeleteClause()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN NOT MATCHED BY SOURCE"));
        Assert.That(result, Does.Contain("DELETE"));
    }

    [Test]
    public void BuildMergeScript_PostgreSql_Pg17_EmitsMergeNotMatchedBySource()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "TestTable", "[{\"Id\":1}]", "\"Id\"",
            mergeUpdate: false, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: false,
            destSchemaOverride: null, pgServerVersionNum: 17);

        Assert.That(result, Does.Contain("WHEN NOT MATCHED BY SOURCE"));
        Assert.That(result, Does.Contain("DELETE"));
        Assert.That(result, Does.Not.Contain("WHERE NOT EXISTS"));
    }

    [Test]
    public void BuildMergeScript_PostgreSql_Pg16_EmitsDeleteWhereNotExists()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "TestTable", "[{\"Id\":1}]", "\"Id\"",
            mergeUpdate: false, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: false,
            destSchemaOverride: null, pgServerVersionNum: 16);

        Assert.That(result, Does.Not.Contain("WHEN NOT MATCHED BY SOURCE"));
        Assert.That(result, Does.Contain("DELETE FROM"));
        Assert.That(result, Does.Contain("WHERE NOT EXISTS"));
    }

    [Test]
    public void BuildMergeScript_PostgreSql_Pg16_FallbackDeleteAliasesTargetForMergeFilter()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "TestTable", "[{\"Id\":1}]", "\"Id\"",
            mergeUpdate: false, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: "\"Target\".\"Type\" = 'A'",
            disableRules: false, updateDescendents: false,
            destSchemaOverride: null, pgServerVersionNum: 16);

        // The fallback DELETE must alias its target so the mergeFilter's "Target" ref resolves.
        var fallback = result.Substring(result.IndexOf("DELETE FROM", StringComparison.Ordinal));
        Assert.That(fallback, Does.Contain("DELETE FROM ONLY \"public\".\"TestTable\" AS \"Target\""));
        Assert.That(fallback, Does.Contain("\"Target\".\"Type\" = 'A'"));
        Assert.That(fallback, Does.Contain("NOT EXISTS"));
    }

    [Test]
    public void BuildMergeScript_PostgreSql_Pg16_FallbackDeleteHandlesNullSafeKey()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "TestTable", "[{\"Id\":1}]", "*\"Id\"",
            mergeUpdate: false, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: false,
            destSchemaOverride: null, pgServerVersionNum: 16);

        // *-prefixed key must produce the NULL-safe arm with the '*' stripped from the column ref.
        // Scope assertions to the fallback DELETE block (the MERGE ON path is out of scope here).
        var fallback = result.Substring(result.IndexOf("DELETE FROM", StringComparison.Ordinal));
        Assert.That(fallback, Does.Contain("NOT EXISTS"));
        Assert.That(fallback, Does.Contain(
            "(\"DeleteSource\".\"Id\" = \"Target\".\"Id\" OR (\"DeleteSource\".\"Id\" IS NULL AND \"Target\".\"Id\" IS NULL))"));
        Assert.That(fallback, Does.Not.Contain("*"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithDisableTriggers()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: true,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("DISABLE TRIGGER ALL"));
        Assert.That(result, Does.Contain("ENABLE TRIGGER ALL"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithIdentitySequence_IncludesSetval()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer", Identity: true, Sequence: "public.test_table_id_seq")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("OVERRIDING SYSTEM VALUE"));
        Assert.That(result, Does.Contain("SETVAL"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithTokenize_UsesTokenPlaceholder()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null);

        Assert.That(result, Does.Contain("{{public.test_table.tabledata}}"));
    }

    [Test]
    public void BuildMergeScript_PostgreSql_WithTokenize_ContentFileTokenParameter_IsEmbeddedVerbatim()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null,
            contentFileToken: "Sales%3Atest_table.tabledata");

        Assert.That(result, Does.Contain("{{Sales%3Atest_table.tabledata}}"));
        Assert.That(result, Does.Not.Contain("{{public.test_table.tabledata}}"),
            "The passed-in token must win over the platform's own derivation.");
    }

    [Test]
    public void BuildMergeScript_PostgreSql_XmlEncoding_EmitsXmltableRowSource()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer"), new("Name", "varchar", "character varying(100)")]);

        var script = MergeScriptHelper.BuildMergeScript(
            Platform.PostgreSQL, cmd, "public", "Widget",
            "<rows><row><c n=\"Id\">1</c><c n=\"Name\">Anvil</c></row></rows>",
            "\"Id\"", mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml");

        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Contain("xmltable("), "PostgreSQL shreds XML with xmltable.");
            Assert.That(script, Does.Contain("'/rows/row'"), "Row path matches the SQL Server shred.");
            // JSON_ARRAY_ELEMENTS is the real PostgreSQL JSON row-source function (verified in
            // GetJsonColumnDefinitionsPostgreSql's caller) — an XML delivery must not fall back to it.
            Assert.That(script, Does.Not.Contain("JSON_ARRAY_ELEMENTS"),
                "An XML delivery must not fall back to the JSON row source.");
        });
    }

    [Test]
    public void BuildMergeScript_PostgreSql_XmlEncoding_NoLongerThrows()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer"), new("Name", "varchar", "character varying(100)")]);

        Assert.DoesNotThrow(() => MergeScriptHelper.BuildMergeScript(
            Platform.PostgreSQL, cmd, "public", "Widget",
            "<rows><row><c n=\"Id\">1</c></row></rows>",
            "\"Id\"", mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml"));
    }

    // B1 fix round 1: geometry/bytea/array columns must get the same per-type transform the JSON row
    // source applies (GetJsonColumnDefinitionsPostgreSql) — a plain PATH-typed cast is wrong for all
    // three (WKT text isn't a valid geometry literal, base64 text isn't valid bytea escape/hex format,
    // and the '*,*'-delimited text isn't a valid PG array literal). These three tests are the coverage
    // that was missing when the gap shipped.
    [Test]
    public void BuildMergeScript_PostgreSql_XmlEncoding_GeometryColumn_UsesStGeomFromText()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer"), new("Geom", "geometry", "public.geometry")]);

        var script = MergeScriptHelper.BuildMergeScript(
            Platform.PostgreSQL, cmd, "public", "Shapes",
            "<rows><row><c n=\"Id\">1</c><c n=\"Geom\">POINT(1 2)</c></row></rows>",
            "\"Id\"", mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml");

        Assert.Multiple(() =>
        {
            // The WKT alone has no reference system. The shred must also declare the "<col>.STSrid"
            // companion extraction now emits, and pass it as ST_GeomFromText's second argument --
            // defaulting to 0 so packages extracted before the companion existed behave as they did.
            Assert.That(script, Does.Contain("\"Geom.STSrid\" text PATH"),
                "the SRID companion must be declared as an xmltable column");
            Assert.That(script, Does.Contain("COALESCE"),
                "a package with no companion must fall back rather than fail");
            Assert.That(script, Does.Contain("\"Geom\" text PATH"),
                "A geometry column must be shredded as text, not cast directly by xmltable's COLUMNS typing.");
            Assert.That(script, Does.Contain("ST_GeomFromText(\"x\".\"Geom\","),
                "Same function GetJsonColumnDefinitionsPostgreSql applies to the JSON row source.");
        });
    }

    [Test]
    public void BuildMergeScript_PostgreSql_XmlEncoding_ByteaColumn_UsesDecodeBase64()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer"), new("Data", "bytea", "bytea")]);

        var script = MergeScriptHelper.BuildMergeScript(
            Platform.PostgreSQL, cmd, "public", "Blobs",
            "<rows><row><c n=\"Id\">1</c><c n=\"Data\">QQ==</c></row></rows>",
            "\"Id\"", mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml");

        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Contain("\"Data\" text PATH"),
                "A bytea column must be shredded as text — casting base64 text straight to bytea corrupts the bytes.");
            Assert.That(script, Does.Contain("decode(\"x\".\"Data\", 'base64')"),
                "Same function GetJsonColumnDefinitionsPostgreSql applies to the JSON row source.");
        });
    }

    [Test]
    public void BuildMergeScript_PostgreSql_XmlEncoding_ArrayColumn_ReadsLiteralOrLegacyDelimiter()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("Id", "int4", "integer"), new("Tags", "_int4", "integer[]")]);

        var script = MergeScriptHelper.BuildMergeScript(
            Platform.PostgreSQL, cmd, "public", "Tagged",
            "<rows><row><c n=\"Id\">1</c><c n=\"Tags\">1*,*2</c></row></rows>",
            "\"Id\"", mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml");

        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Contain("\"Tags\" text PATH"),
                "An array column must be shredded as text — the '*,*'-delimited form is not a PG array literal.");
            Assert.That(script,
                Does.Contain("CASE WHEN LEFT(\"x\".\"Tags\", 1) IN ('{', '[') THEN \"x\".\"Tags\"::integer[] "),
                "An array is extracted as its own literal, which keeps dimensions, bounds and quoting.");
            Assert.That(script,
                Does.Contain("ELSE STRING_TO_ARRAY(\"x\".\"Tags\", '*,*', '*NULL_VALUE_REPRESENTATION*')::integer[] END"),
                "A file extracted in the older '*,*' form must still load.");
        });
    }

    #endregion

    #region BuildMergeScript - MariaDB below 10.2.22 (#590)

    // INFORMATION_SCHEMA.CHECK_CONSTRAINTS arrived in MariaDB 10.2.22. Reading it on an older server fails, which failed
    // every delivery there; the read is skipped where the view is absent. The stub answers as such a server does.
    private sealed class StubDbException(string message) : System.Data.Common.DbException(message);

    private static IDbCommand CreateMariaDbCommand(bool hasCheckView, MySqlColumnDef[] columns)
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(_ =>
            cmd.CommandText == "SELECT VERSION()" ? (hasCheckView ? "10.6.20-MariaDB" : "10.2.21-MariaDB")
            : cmd.CommandText != null && cmd.CommandText.Contains("TABLE_NAME = 'CHECK_CONSTRAINTS'") ? (hasCheckView ? 1L : 0L)
            : null);
        cmd.ExecuteReader().Returns(_ =>
            cmd.CommandText != null && cmd.CommandText.Contains("FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS")
                ? hasCheckView ? CreateEmptyMockReader() : throw new StubDbException("Unknown table 'CHECK_CONSTRAINTS' in information_schema")
                : CreateMySqlMockReader(columns));
        return cmd;
    }

    private static readonly MySqlColumnDef[] MariaDbColumns =
    [
        new("id", "int", null, 10L, 0L, null, "int", "", null),
        new("doc", "longtext", 4294967295L, null, null, null, "longtext", "", null)
    ];

    private static string BuildMariaDb(IDbCommand cmd) => MergeScriptHelper.BuildMergeScript(Platform.MariaDb, cmd,
        "testdb", "testtable", "[{\"id\":1,\"doc\":\"x\"}]", "`id`",
        mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);

    [Test]
    public void BuildMergeScript_MariaDbWithoutTheCheckView_StillBuildsTheScript()
    {
        var cmd = CreateMariaDbCommand(hasCheckView: false, MariaDbColumns);

        var script = BuildMariaDb(cmd);

        Assert.That(script, Does.Contain("`testdb`.`testtable`"), "a delivery is built where the view is missing");
    }

    [Test]
    public void BuildMergeScript_MariaDbWithTheCheckView_StillLooksForJsonChecks()
    {
        var cmd = CreateMariaDbCommand(hasCheckView: true, MariaDbColumns);

        BuildMariaDb(cmd);

        Assert.That(cmd.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "ExecuteReader"), Is.GreaterThanOrEqualTo(2),
            "with the view present the json_valid check read still runs, as well as the column read");
    }

    [TestCase(0L, false)]
    [TestCase(1L, true)]
    public void ReportsCheckConstraints_ReadsTheCatalog(long count, bool expected)
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(count);

        Assert.That(ForgeKindler.ReportsCheckConstraints(cmd), Is.EqualTo(expected));
    }

    [Test]
    public void ReportsCheckConstraints_AnUnanswerableServer_IsTreatedAsHavingTheView()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(_ => throw new StubDbException("no answer"));

        Assert.That(ForgeKindler.ReportsCheckConstraints(cmd), Is.True);
    }

    #endregion

    #region BuildMergeScript - MySQL Tests

    [Test]
    public void BuildMergeScript_MySQL_Insert_GeneratesInsertIgnore()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[{\"id\":1}]", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("INSERT IGNORE INTO `testdb`.`testtable`"));
        Assert.That(result, Does.Contain("JSON_TABLE("));
        Assert.That(result, Does.Not.Contain("ON DUPLICATE KEY"));
    }

    [Test]
    public void BuildMergeScript_MySql_WithTokenize_UsesTokenPlaceholder()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[{\"id\":1}]", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null);

        // MySQL has no schema concept — always unqualified, unlike SQL Server/PostgreSQL's default.
        Assert.That(result, Does.Contain("{{testtable.tabledata}}"));
    }

    [Test]
    public void BuildMergeScript_MySql_WithTokenize_ContentFileTokenParameter_IsEmbeddedVerbatim()
    {
        // #390 "derive the key once": DataTongs computes the key alongside the .tabledata filename
        // (through FileNameEncoder) and passes it here; BuildMergeScript must embed exactly that
        // value rather than its own always-unqualified, always-unencoded derivation.
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[{\"id\":1}]", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null,
            contentFileToken: "Sales%3AWidget.tabledata");

        Assert.That(result, Does.Contain("{{Sales%3AWidget.tabledata}}"));
        Assert.That(result, Does.Not.Contain("{{testtable.tabledata}}"),
            "The passed-in token must win over the platform's own derivation.");
    }

    [Test]
    public void BuildMergeScript_MySQL_InsertUpdate_GeneratesOnDuplicateKey()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[{\"id\":1,\"name\":\"test\"}]", "`id`",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("INSERT INTO `testdb`.`testtable`"));
        Assert.That(result, Does.Contain("ON DUPLICATE KEY UPDATE"));
        Assert.That(result, Does.Contain("`name` = VALUES(`name`)"));
        Assert.That(result, Does.Not.Contain("`id` = VALUES(`id`)"));
    }

    [Test]
    public void BuildMergeScript_MySQL_InsertUpdateDelete_UsesUpsertPlusDelete()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[{\"id\":1,\"name\":\"test\"}]", "`id`",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // Must NOT use REPLACE INTO — it deletes+reinserts, breaking ON DELETE RESTRICT FKs
        Assert.That(result, Does.Not.Contain("REPLACE INTO"));
        // Should use upsert (INSERT ... ON DUPLICATE KEY UPDATE)
        Assert.That(result, Does.Contain("INSERT INTO `testdb`.`testtable`"));
        Assert.That(result, Does.Contain("ON DUPLICATE KEY UPDATE"));
        // Should have a delete pass for rows not in source (MySQL multi-table delete syntax)
        Assert.That(result, Does.Contain("DELETE Target FROM `testdb`.`testtable` Target"));
        Assert.That(result, Does.Contain("NOT EXISTS"));
    }

    [Test]
    public void BuildMergeScript_MySQL_InsertUpdateDelete_DeleteMatchesOnKeyColumns()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("code", "varchar", 10L, null, null, null, "varchar(10)", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "`id`,`code`",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // Delete pass should join on both key columns. The numeric key compares plainly; the string
        // key forces utf8mb4_unicode_ci on both sides so a MariaDB 11.4 target column
        // (utf8mb4_uca1400_ai_ci) and a JSON_TABLE-extracted value (utf8mb4_general_ci) don't raise
        // "Illegal mix of collations" (1267).
        Assert.That(result, Does.Contain("Target.`id` = jt.`id`"));
        // String keys transcode both sides to utf8mb4 before collating so the forced collation is valid for a
        // non-utf8mb4 key column (latin1 / utf8mb3) — see BuildKeyMatchMySql / CHANGELOG #359.
        Assert.That(result, Does.Contain("CONVERT(Target.`code` USING utf8mb4) COLLATE utf8mb4_unicode_ci = CONVERT(jt.`code` USING utf8mb4) COLLATE utf8mb4_unicode_ci"));
    }

    [Test]
    public void BuildMergeScript_MySQL_InsertUpdateDelete_DeleteRespectsMergeFilter()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "`id`",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: "Target.`category` = 'active'");

        // Delete pass should include the merge filter
        Assert.That(result, Does.Contain("Target.`category` = 'active'"));
    }

    [Test]
    public void BuildMergeScript_MySql_FullSyncDelete_UsesTargetAlias_ForPortableMergeFilter()
    {
        // #333: MySQL delete must alias the target `Target` (like SS/PG) so a portable
        // MergeFilter `Target.<col>` resolves instead of failing "Unknown column 'Target.Region'".
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("Region", "varchar", 50L, null, null, null, "varchar(50)", "", null)
        });

        var script = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "`id`",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: "Target.Region = 'GLOBAL'");

        Assert.That(script, Does.Contain("DELETE Target FROM"));
        Assert.That(script, Does.Not.Contain("DELETE t FROM"));
        Assert.That(script, Does.Contain("Target.`")); // key predicate now Target-aliased
        Assert.That(script, Does.Contain("AND (Target.Region = 'GLOBAL')"));
    }

    [Test]
    public void BuildMergeScript_MySQL_DeleteOnly_UsesUpsertPlusDelete()
    {
        // mergeDelete=true, mergeUpdate=false — still must not use REPLACE INTO
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "`id`",
            mergeUpdate: false, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Not.Contain("REPLACE INTO"));
        Assert.That(result, Does.Contain("DELETE Target FROM `testdb`.`testtable` Target"));
    }

    [Test]
    public void BuildMergeScript_MySQL_IncludesAutoIncrementColumns()
    {
        // MySQL allows explicit inserts into AUTO_INCREMENT columns without any special command.
        // Auto-increment columns must be included to preserve original ID values from data files,
        // matching how SQL Server (IDENTITY_INSERT ON) and PostgreSQL (OVERRIDING VALUE) work.
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "auto_increment", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("`id`"));
        Assert.That(result, Does.Contain("`name`"));
    }

    [Test]
    public void BuildMergeScript_MySQL_ExcludesGeneratedColumns()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("first_name", "varchar", 50L, null, null, null, "varchar(50)", "", null),
            new("full_name", "varchar", 101L, null, null, null, "varchar(101)", "", "CONCAT(`first_name`)")
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("`first_name`"));
        Assert.That(result, Does.Not.Contain("`full_name`"));
    }

    [Test]
    public void BuildMergeScript_MySQL_EscapesSingleQuotes()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[{\"name\":\"O'Connor\"}]", "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("O''Connor"));
    }

    [Test]
    public void BuildMergeScript_MySQL_NullTableData_TreatsAsEmptyArray()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", null, "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("@json_data = '[]'"));
    }

    [Test]
    public void BuildMergeScript_MySQL_NoColumnsAfterFiltering_ThrowsInvalidOperationException()
    {
        // Only generated columns remain after filtering — no insertable columns
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("full_name", "varchar", 101L, null, null, null, "varchar(101)", "", "CONCAT(`first`,`last`)")
        });

        Assert.Throws<InvalidOperationException>(() =>
            MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
                "testdb", "testtable", "[]", "",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null));
    }

    [Test]
    public void BuildMergeScript_MySQL_VariousDataTypes()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("int_col", "int", null, 10L, 0L, null, "int", "", null),
            new("dec_col", "decimal", null, 10L, 2L, null, "decimal(10,2)", "", null),
            new("dt_col", "datetime", null, null, null, 6, "datetime(6)", "", null),
            new("vc_col", "varchar", 100L, null, null, null, "varchar(100)", "", null),
            new("json_col", "json", null, null, null, null, "json", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("`int_col` INT PATH '$.int_col'"));
        Assert.That(result, Does.Contain("`dec_col` DECIMAL(10,2) PATH '$.dec_col'"));
        Assert.That(result, Does.Contain("`dt_col` DATETIME(6) PATH '$.dt_col'"));
        Assert.That(result, Does.Contain("`vc_col` VARCHAR(100) CHARACTER SET utf8mb4 PATH '$.vc_col'"));
        Assert.That(result, Does.Contain("`json_col` JSON PATH '$.json_col'"));
    }

    [Test]
    public void BuildMergeScript_MySQL_BinaryType_UsesFromBase64()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("data", "blob", null, null, null, null, "blob", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("FROM_BASE64(`data`)"));
        Assert.That(result, Does.Contain("`data` LONGTEXT CHARACTER SET utf8mb4 PATH '$.data'"));
    }

    [Test]
    public void BuildMergeScript_MySQL_GeometryType_UsesStGeomFromText()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("location", "point", null, null, null, null, "point", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("ST_GeomFromText(`location`, COALESCE(`location.STSrid`, 0))"));
    }

    [Test]
    public void BuildMergeScript_MySQL_TrimsBackticks()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "`testdb`", "`testtable`", "[]", "",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("`testdb`.`testtable`"));
        Assert.That(result, Does.Not.Contain("``testdb``"));
    }

    [Test]
    public void BuildMergeScript_MySQL_AllColumnsAreKeys_UsesNoOpUpdate()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("code", "varchar", 10L, null, null, null, "varchar(10)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "`id`,`code`",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("ON DUPLICATE KEY UPDATE"));
        Assert.That(result, Does.Contain("`id` = VALUES(`id`)"));
    }

    [Test]
    public void BuildMergeScript_MySQL_JsonColumn_UsesConditionalComparisonInUpsert()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null),
            new("metadata", "json", null, null, null, null, "json", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", @"[{""id"":1,""name"":""test"",""metadata"":{""key"":""value""}}]", "`id`",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // JSON column should use conditional comparison to prevent false updates from key reordering.
        // JSON_EXTRACT(x,'$') is the MySQL/MariaDB-portable form (MariaDB rejects CAST(x AS JSON)).
        Assert.That(result, Does.Contain("IF(JSON_EXTRACT(VALUES(`metadata`), '$') = JSON_EXTRACT(`testdb`.`testtable`.`metadata`, '$'), `testdb`.`testtable`.`metadata`, VALUES(`metadata`))"));

        // Non-JSON column should use simple assignment
        Assert.That(result, Does.Contain("`name` = VALUES(`name`)"));
        Assert.That(result, Does.Not.Contain("IF(JSON_EXTRACT(VALUES(`name`)"));
    }

    [Test]
    public void BuildMergeScript_MySql_XmlEncoding_NoLongerThrows()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null)
        });

        Assert.DoesNotThrow(() => MergeScriptHelper.BuildMergeScript(
            Platform.MySQL, cmd, "testdb", "testtable",
            "<rows><row><c n=\"id\">1</c></row></rows>", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml"));
    }

    [Test]
    public void BuildMergeScript_MySql_XmlEncoding_RoutesThroughTheUnchangedJsonRowSource()
    {
        // B3: dynamic XPath is rejected outright on MySQL/MariaDB, so an Xml delivery is converted to
        // JSON in C# and shredded exactly the way a hand-authored JSON payload would be — same
        // JSON_TABLE row source, no XML-specific shred path exists on this platform.
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable",
            "<rows><row><c n=\"id\">1</c><c n=\"name\">Anvil</c></row></rows>", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml");

        Assert.That(result, Does.Contain("JSON_TABLE("));
        Assert.That(result, Does.Not.Contain("xmltable"));
        Assert.That(result, Does.Not.Contain(".nodes("));
    }

    [Test]
    public void BuildMergeScript_MySql_XmlEncoding_ExcludesColumnsNotInTheConvertedData()
    {
        // Column filtering must key off the JSON produced by the conversion, not the original XML —
        // otherwise a column present in the XML but dropped by the converter (or vice versa) could
        // desync the filter from what is actually being shredded.
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null),
            new("rowguid", "char", 36L, null, null, null, "char(36)", "", null)
        });

        // The payload only carries "id" and "name" — "rowguid" must be excluded.
        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable",
            "<rows><row><c n=\"id\">1</c><c n=\"name\">Anvil</c></row></rows>", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml");

        Assert.That(result, Does.Contain("`id`"));
        Assert.That(result, Does.Contain("`name`"));
        Assert.That(result, Does.Not.Contain("`rowguid`"));
    }

    #endregion

    #region B4 — Xml content encoding is a four-engine parity feature, not a SQL-Server-only one

    // The guard that made Xml a SqlServer-only ContentEncoding was removed once B3 gave MySQL/MariaDB a
    // route (XML->JSON conversion in C#, since both reject dynamic XPath). This test is the parity check:
    // every platform must accept ContentEncoding: "Xml" without throwing. MariaDb is exercised directly
    // (not just inferred from MySQL coverage above) because BuildMergeScript takes it as its own enum
    // member — GetBasePlatform() collapses it to MySQL internally, so the MySQL mock command is reused.
    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void BuildMergeScript_XmlEncoding_IsSupportedOnEveryEngine(Platform platform)
    {
        const string xmlPayload = "<rows><row><c n=\"id\">1</c><c n=\"name\">Anvil</c></row></rows>";

        IDbCommand cmd;
        string schemaOrDb;
        string keyColumns;

        switch (platform)
        {
            case Platform.SqlServer:
                cmd = CreateSqlServerShapeCommand([new("id", "int"), new("name", "varchar", 100)]);
                schemaOrDb = "dbo";
                keyColumns = "[id]";
                break;
            case Platform.PostgreSQL:
                cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("name", "varchar", "character varying(100)")]);
                schemaOrDb = "public";
                keyColumns = "\"id\"";
                break;
            case Platform.MySQL:
            case Platform.MariaDb:
                cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
                {
                    new("id", "int", null, 10L, 0L, null, "int", "", null),
                    new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null)
                });
                schemaOrDb = "testdb";
                keyColumns = "`id`";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unhandled platform in parity test.");
        }

        Assert.DoesNotThrow(() => MergeScriptHelper.BuildMergeScript(
            platform, cmd, schemaOrDb, "Widget", xmlPayload, keyColumns,
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml"));
    }

    #endregion

    #region Cross-Platform Consistency Tests

    [Test]
    public void BuildMergeScript_AllPlatforms_InsertEscapesSingleQuotes()
    {
        // SQL Server
        var sqlCmd = CreateSqlServerShapeCommand([new("Id", "int")]);
        var sqlResult = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, sqlCmd,
            "dbo", "T", "[{\"v\":\"it's\"}]", "[Id]", false, false, false, false, null);
        Assert.That(sqlResult, Does.Contain("it''s"));

        // PostgreSQL
        var pgCmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);
        var pgResult = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, pgCmd,
            "public", "t", "[{\"v\":\"it's\"}]", "\"id\"", false, false, false, false, null);
        Assert.That(pgResult, Does.Contain("it''s"));

        // MySQL
        var myCmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null)
        });
        var myResult = MergeScriptHelper.BuildMergeScript(Platform.MySQL, myCmd,
            "db", "t", "[{\"id\":1,\"v\":\"it's\"}]", "`id`", false, false, false, false, null);
        Assert.That(myResult, Does.Contain("it''s"));
    }

    #endregion

    #region PostgreSQL-specific: DisableRules and UpdateDescendents

    [Test]
    public void BuildMergeScript_PostgreSQL_WithDisableRules()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")], rules: ["rule_insert_p1", "rule_insert_p2"]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: true);

        // Rules are disabled/enabled individually by name (DISABLE RULE ALL is not valid PostgreSQL syntax)
        Assert.That(result, Does.Contain("DISABLE RULE \"rule_insert_p1\""));
        Assert.That(result, Does.Contain("DISABLE RULE \"rule_insert_p2\""));
        Assert.That(result, Does.Contain("ENABLE RULE \"rule_insert_p1\""));
        Assert.That(result, Does.Contain("ENABLE RULE \"rule_insert_p2\""));
        // Disable statements should be outside the DO $$ block (before it)
        Assert.That(result.IndexOf("DISABLE RULE"), Is.LessThan(result.IndexOf("DO $SchemaSmithData$")));
        // Enable statements should be outside the DO $$ block (after it)
        Assert.That(result.IndexOf("ENABLE RULE"), Is.GreaterThan(result.IndexOf("END $SchemaSmithData$ LANGUAGE plpgsql")));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithUpdateDescendents_OmitsOnlyKeyword()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            updateDescendents: true);

        Assert.That(result, Does.Not.Contain("ONLY "));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithoutUpdateDescendents_IncludesOnlyKeyword()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            updateDescendents: false);

        Assert.That(result, Does.Contain("ONLY "));
    }

    [TestCase("geometry", true)]
    [TestCase("geography", true)]
    // PostgreSQL's own geometric types: ST_AsText / ST_GeomFromText reject them.
    [TestCase("point", false)]
    [TestCase("polygon", false)]
    [TestCase("box", false)]
    [TestCase("int4", false)]
    [TestCase("varchar", false)]
    [TestCase("text", false)]
    [TestCase("bytea", false)]
    public void IsGeometryTypePostgreSql_ReturnsExpected(string udtName, bool expected)
    {
        Assert.That(MergeScriptHelper.IsGeometryTypePostgreSql(udtName), Is.EqualTo(expected));
    }

    [TestCase("bytea", true)]
    [TestCase("int4", false)]
    [TestCase("varchar", false)]
    [TestCase("geometry", false)]
    public void IsByteaTypePostgreSql_ReturnsExpected(string udtName, bool expected)
    {
        Assert.That(MergeScriptHelper.IsByteaTypePostgreSql(udtName), Is.EqualTo(expected));
    }

    [TestCase("xml", true)]
    [TestCase("XML", true)]
    [TestCase("int4", false)]
    [TestCase("text", false)]
    [TestCase("varchar", false)]
    public void IsXmlTypePostgreSql_ReturnsExpected(string udtName, bool expected)
    {
        Assert.That(MergeScriptHelper.IsXmlTypePostgreSql(udtName), Is.EqualTo(expected));
    }

    [Test]
    public void GetJsonDataKeys_ReturnsUnionOfAllRowKeys()
    {
        var keys = MergeScriptHelper.GetJsonDataKeys("[{\"a\":1},{\"b\":2},{\"a\":3,\"c\":null}]");
        Assert.That(keys, Is.Not.Null);
        Assert.That(keys, Does.Contain("a"));
        Assert.That(keys, Does.Contain("b"));
        Assert.That(keys, Does.Contain("c"));
        Assert.That(keys.Count, Is.EqualTo(3));
    }

    [Test]
    public void GetJsonDataKeys_ReturnsNull_ForEmptyArray()
    {
        Assert.That(MergeScriptHelper.GetJsonDataKeys("[]"), Is.Null);
    }

    [Test]
    public void GetJsonDataKeys_ReturnsNull_ForNullOrEmpty()
    {
        Assert.That(MergeScriptHelper.GetJsonDataKeys(null), Is.Null);
        Assert.That(MergeScriptHelper.GetJsonDataKeys(""), Is.Null);
    }

    [Test]
    public void BuildMergeScript_MySQL_ExcludesColumnsNotInJsonData()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null),
            new("name", "varchar", 100L, null, null, null, "varchar(100)", "", null),
            new("rowguid", "char", 36L, null, null, null, "char(36)", "", null)
        });

        // Data only has "id" and "name" — "rowguid" should be excluded
        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[{\"id\":1,\"name\":\"test\"}]", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("`id`"));
        Assert.That(result, Does.Contain("`name`"));
        Assert.That(result, Does.Not.Contain("`rowguid`"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_GeometryType_UsesStGeomFromText()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("location", "geometry", "public.geometry")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("public.ST_GeomFromText(elem ->> 'location', COALESCE(NULLIF(elem ->> 'location.STSrid', '')::int, 0)) AS \"location\""),
            "PostGIS's function by its type's schema, with the SRID beside the value");
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_ByteaType_UsesDecodeBase64()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("photo", "bytea", "bytea")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("decode(elem ->> 'photo', 'base64') AS \"photo\""));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithGeometryUpdate_UsesStandardEquality()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("location", "geometry", "public.geometry")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN MATCHED AND"));
        Assert.That(result, Does.Contain("UPDATE SET"));
        Assert.That(result, Does.Contain("\"location\" = \"Source\".\"location\""));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_XmlType_UsesTextCastForComparison()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("demographics", "xml", "xml", CompareCast: "::text")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN MATCHED AND"));
        Assert.That(result, Does.Contain("\"Target\".\"demographics\"::text IS DISTINCT FROM \"Source\".\"demographics\"::text"));
        Assert.That(result, Does.Contain("\"demographics\" = \"Source\".\"demographics\""));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_JsonType_UsesTextCastForComparison()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("metadata", "json", "json", CompareCast: "::text")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN MATCHED AND"));
        Assert.That(result, Does.Contain("\"Target\".\"metadata\"::text IS DISTINCT FROM \"Source\".\"metadata\"::text"));
        Assert.That(result, Does.Contain("\"metadata\" = \"Source\".\"metadata\""));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_JsonbType_UsesJsonbCastForComparison()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("settings", "jsonb", "jsonb", CompareCast: "::jsonb")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN MATCHED AND"));
        Assert.That(result, Does.Contain("\"Target\".\"settings\"::jsonb IS DISTINCT FROM \"Source\".\"settings\"::jsonb"));
        Assert.That(result, Does.Contain("\"settings\" = \"Source\".\"settings\""));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_NonJsonColumn_UsesStandardComparison()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("name", "varchar", "character varying(100)")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("WHEN MATCHED AND"));
        Assert.That(result, Does.Contain("\"Target\".\"name\" IS DISTINCT FROM \"Source\".\"name\""));
        Assert.That(result, Does.Not.Contain("::jsonb"));
        Assert.That(result, Does.Not.Contain("::text"));
    }

    #endregion

    #region SQL Server GEOMETRY Support Tests

    [Test]
    public void BuildMergeScript_SqlServer_GeometryColumn_ComparesItsText()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int"), new("Shape", "geometry")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // A spatial value has no = operator: compared as its text
        Assert.That(result, Does.Contain("Target.[Shape].ToString()) <> (Source.[Shape].ToString()"));
        Assert.That(result, Does.Contain("[Shape] = Source.[Shape]"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_GeometrySelectColumns_UsesGeometryType()
    {
        // Verify the SQL query for JSON select uses geometry::STGeomFromText
        var cmd = CreateSqlServerShapeCommand([new("Shape", "geometry")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("geometry::STGeomFromText([Shape], COALESCE([Shape.STSrid], 0)) AS [Shape]"));
    }

    #endregion

    // A type with no = operator is compared through one that has it, so an unchanged row is not rewritten and a changed
    // one is: each kind below, against a plain column compared as it is.
    [TestCase("geography", "Target.[C].ToString()")]
    [TestCase("geometry", "Target.[C].ToString()")]
    [TestCase("xml", "CAST(Target.[C] AS NVARCHAR(MAX))")]
    [TestCase("ntext", "CAST(Target.[C] AS NVARCHAR(MAX))")]
    [TestCase("vector", "CAST(Target.[C] AS NVARCHAR(MAX))")]
    [TestCase("text", "CAST(Target.[C] AS VARCHAR(MAX))")]
    [TestCase("image", "CAST(Target.[C] AS VARBINARY(MAX))")]
    [TestCase("datetimeoffset", "CAST(Target.[C] AS NVARCHAR(50))")]
    [TestCase("int", "(Target.[C]) <> (Source.[C])")]
    public void BuildMergeScript_SqlServer_UpdateCompare_ReadsEachTypeThroughOneWithEquality(string dataType, string expected)
    {
        var cmd = CreateSqlServerShapeCommand([new("C", dataType), new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", "T", "[{\"Id\":1,\"C\":null}]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);

        var compare = result[result.IndexOf("WHEN MATCHED AND", StringComparison.Ordinal)..result.IndexOf("UPDATE SET", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(compare, Does.Contain(expected));
            Assert.That(compare, Does.Contain("(Target.[Id]) <> (Source.[Id])"), "a plain column is compared as it is");
            Assert.That(result, Does.Contain("        [C] = Source.[C]"), "and assigned by its own name");
        });
    }

    // The OPENJSON WITH clause: spatial and datetimeoffset values arrive as text (a spatial one with its SRID beside it),
    // a typed xml column keeps its schema collection, an alias type is named as declared, and a character column gets an
    // AS JSON reading whose path escapes the name for the path and the literal.
    [Test]
    public void BuildMergeScript_SqlServer_OpenJsonColumns_TypeEachColumnForItsShred()
    {
        var cmd = CreateSqlServerShapeCommand([
            new("Id", "int"),
            new("Geo", "geography"),
            new("At", "datetimeoffset"),
            new("Doc", "xml", XmlCollection: "[s].[docs]"),
            new("Code", "nvarchar", 10, Declared: "s.Code"),
            new("Big", "varchar", -1),
            new("it's \"x\\", "nvarchar", 20)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", "T", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("[Geo] NVARCHAR(4000), [Geo.STSrid] INT"));
            Assert.That(result, Does.Contain("geography::STGeomFromText([Geo], COALESCE([Geo.STSrid], 4326)) AS [Geo]"));
            Assert.That(result, Does.Contain("[At] NVARCHAR(50)"));
            Assert.That(result, Does.Contain("[Doc] xml([s].[docs])"));
            Assert.That(result, Does.Contain("[Code] s.Code,"), "an alias type is named as declared, and takes no length of its own");
            Assert.That(result, Does.Contain("[Big] varchar(MAX)"), "a system type is named as the catalog names it");
            Assert.That(result, Does.Contain("[it's \"x\\] nvarchar(20)").Or.Contain("[it's \"x\\] NVARCHAR(20)"));
            Assert.That(result, Does.Contain("[it's \"x\\ (as JSON)] NVARCHAR(MAX) '$.\"it''s \\\"x\\\\\"' AS JSON"));
            Assert.That(result, Does.Contain("COALESCE([it's \"x\\], [it's \"x\\ (as JSON)]) AS [it's \"x\\]"));
        });
    }

    // IDENTITY_INSERT is for a delivery that carries the identity column's values; one that does not lets the table number
    // its rows, and turning it on would fail the insert.
    [TestCase(true, true)]
    [TestCase(false, false)]
    public void BuildMergeScript_SqlServer_IdentityInsert_OnlyWhenTheDataCarriesTheIdentityColumn(bool delivered, bool expected)
    {
        SsCol[] columns = delivered ? [new("Id", "int", Identity: true), new("Name", "nvarchar", 10)] : [new("Name", "nvarchar", 10)];
        var cmd = CreateSqlServerShapeCommand(columns, flags: 1);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", "T", "[{\"Name\":\"a\"}]", "[Name]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);

        Assert.That(result.Contains("SET IDENTITY_INSERT [dbo].[T] ON", StringComparison.Ordinal), Is.EqualTo(expected));
        if (delivered) Assert.That(result, Does.Not.Contain("[Id] = Source.[Id]"), "an identity column is never updated");
    }

    // Every clause of the MERGE comes from one read of the table: the columns, the unsupported ones and the table's flags.
    // It took four to twelve round trips, one per clause and each with its own version probe.
    [Test]
    public void BuildMergeScript_SqlServer_ReadsTheCatalogOnce()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int", Identity: true), new("G", "geography"), new("N", "nvarchar", 10)], flags: 1,
            unsupported: [("V", "sql_variant")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", "T", "[{\"Id\":1,\"G\":null,\"N\":null}]", "[Id]",
            mergeUpdate: true, mergeDelete: true, disableTriggers: true, tokenizeScripts: false, mergeFilter: null);

        var reads = cmd.ReceivedCalls().Count(c => c.GetMethodInfo().Name is "ExecuteReader" or "ExecuteScalar" or "ExecuteNonQuery");
        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(result, Does.StartWith("-- Column [V] skipped: sql_variant is not supported for data delivery"));
            Assert.That(cmd.CommandText, Does.Contain("'sql_variant', 'rowversion', 'timestamp'"), "the read names the types it cannot deliver");
        });
    }

    [Test]
    public void GetUnsupportedColumnComments_SqlServer_NamesEachColumnItCannotDeliver()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")], unsupported: [("VariantCol", "sql_variant"), ("VersionCol", "rowversion")]);

        var result = MergeScriptHelper.GetUnsupportedColumnComments(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.That(result, Is.EqualTo("-- Column [VariantCol] skipped: sql_variant is not supported for data delivery\r\n" +
                                       "-- Column [VersionCol] skipped: rowversion is not supported for data delivery"));
    }

    [Test]
    public void GetUnsupportedColumnComments_SqlServer_NoUnsupportedColumns_ReturnsEmpty()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        Assert.That(MergeScriptHelper.GetUnsupportedColumnComments(Platform.SqlServer, cmd, "dbo", "TestTable"), Is.Empty);
    }

    // The update list is plain names: the type information it once carried as a one-letter prefix is in GetColumnMetadata.
    [Test]
    public void GetUpdateColumns_SqlServer_ListsTheUpdatableColumnsByName()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int", Identity: true), new("G", "geography"), new("T", "datetimeoffset")]);

        Assert.That(MergeScriptHelper.GetUpdateColumns(Platform.SqlServer, cmd, "dbo", "T"), Is.EqualTo("[G],[T]"));
    }

    #region SQL Server DATETIMEOFFSET Support Tests

    [Test]
    public void BuildMergeScript_SqlServer_DateTimeOffsetColumn_ComparesItsText()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int"), new("EventTime", "datetimeoffset")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // An offset change naming the same instant is still a change: compared as its text
        Assert.That(result, Does.Contain("CAST(Target.[EventTime] AS NVARCHAR(50))) <> (CAST(Source.[EventTime] AS NVARCHAR(50))"));
        Assert.That(result, Does.Contain("[EventTime] = Source.[EventTime]"));
    }

    #endregion

    #region PostgreSQL table shape

    // Every clause of the PostgreSQL merge comes from one read of the table; only DisableRules adds the rule lookup. It took
    // five to eight round trips.
    [TestCase(false, 1)]
    [TestCase(true, 2)]
    public void BuildMergeScript_PostgreSQL_ReadsTheCatalogOnce(bool disableRules, int expected)
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer", Identity: true, Sequence: "public.t_id_seq"),
                                                new("doc", "jsonb", "jsonb", CompareCast: "::jsonb")],
            unsupported: [("price", "money")], rules: ["r"]);

        MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd, "public", "t", "[{\"id\":1,\"doc\":null}]", "\"id\"",
            mergeUpdate: true, mergeDelete: true, disableTriggers: true, tokenizeScripts: false, mergeFilter: null, disableRules: disableRules);

        Assert.That(cmd.ReceivedCalls().Count(c => c.GetMethodInfo().Name is "ExecuteReader" or "ExecuteScalar" or "ExecuteNonQuery"), Is.EqualTo(expected));
    }

    // IS DISTINCT FROM reads each column through a type the script can always compare: text for xml, the geometric types
    // and a type outside pg_catalog, jsonb as itself, json as text; anything else as it is.
    [TestCase("::text", "\"Target\".\"c\"::text IS DISTINCT FROM \"Source\".\"c\"::text")]
    [TestCase("::jsonb", "\"Target\".\"c\"::jsonb IS DISTINCT FROM \"Source\".\"c\"::jsonb")]
    [TestCase(null, "\"Target\".\"c\" IS DISTINCT FROM \"Source\".\"c\"")]
    public void BuildMergeScript_PostgreSQL_UpdateCompare_UsesTheColumnsCompareCast(string cast, string expected)
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer"), new("c", "anytype", "anytype", CompareCast: cast)]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd, "public", "t", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain(expected));
    }

    // A column a sequence numbers is never updated; the identity column, not a serial one, decides the OVERRIDING clause.
    [Test]
    public void BuildMergeScript_PostgreSQL_SequenceFedColumns_AreInsertedButNotUpdated()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("a_serial", "int4", "integer", Serial: true, Sequence: "public.t_a_seq"),
                                                new("id", "int4", "integer", Identity: true, Sequence: "public.t_id_seq"),
                                                new("v", "text", "text")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd, "public", "t", "[]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);

        var update = result[result.IndexOf("UPDATE SET", StringComparison.Ordinal)..result.IndexOf("WHEN NOT MATCHED", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(update, Does.Contain("\"v\" = \"Source\".\"v\""));
            Assert.That(update, Does.Not.Contain("\"id\" =").And.Not.Contain("\"a_serial\" ="));
            Assert.That(result, Does.Contain("\"Source\".\"a_serial\"").And.Contain("\"Source\".\"id\""), "both are still inserted");
            Assert.That(result, Does.Contain("OVERRIDING SYSTEM VALUE"));
            Assert.That(result, Does.Contain("public.t_id_seq"), "the identity column's sequence is the one reset");
        });
    }

    // A key is read from the row element by its name as quote_literal writes it: quotes doubled, and an E'' literal with
    // doubled backslashes when the name holds one.
    [Test]
    public void BuildMergeScript_PostgreSQL_JsonRowSource_QuotesEachNameAsALiteral()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("it's", "text", "text"), new("back\\slash", "int4", "integer"), new("q\"uote", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd, "public", "t", "[]", "\"q\"\"uote\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("(elem ->> 'it''s')::text AS \"it's\""));
            Assert.That(result, Does.Contain("(elem ->> E'back\\\\slash')::integer AS \"back\\slash\""));
            Assert.That(result, Does.Contain("(elem ->> 'q\"uote')::integer AS \"q\"\"uote\""));
        });
    }

    [Test]
    public void GetUnsupportedColumnComments_PostgreSQL_NamesEachColumnItCannotDeliver()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")], unsupported: [("search_vector", "tsvector"), ("price", "money")]);

        Assert.That(MergeScriptHelper.GetUnsupportedColumnComments(Platform.PostgreSQL, cmd, "public", "t"),
            Is.EqualTo("-- Column \"price\" skipped: money is not supported for data delivery\n" +
                       "-- Column \"search_vector\" skipped: tsvector is not supported for data delivery"));
    }

    // GetColumnMetadata leaves out a type delivery cannot carry, as its contract says; the XML row source built from it
    // no longer shreds one either.
    [Test]
    public void GetColumnMetadata_PostgreSQL_LeavesOutUnsupportedTypes()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")], unsupported: [("price", "money")]);

        Assert.That(MergeScriptHelper.GetColumnMetadata(Platform.PostgreSQL, cmd, "public", "t").Select(c => c.Name), Is.EqualTo(new[] { "id" }));
    }

    #endregion

    #region Helper Methods - SQL Server Mock

    // A column of the table shape a SQL Server mock command answers with.
    private sealed record SsCol(string Name, string DataType, int MaxLength = 0, bool Identity = false, string Declared = null, string XmlCollection = null);

    // SQL Server reads a table's whole shape in one query: the flags (1 identity, 2 memory-optimized, 4 append-only
    // ledger) on every row, beside each column, an unsupported one flagged. The flags row comes back with no column too.
    private static IDbCommand CreateSqlServerShapeCommand(SsCol[] columns, int flags = 0, (string Name, string DataType)[] unsupported = null)
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteReader().Returns(_ => SqlServerShapeReader(columns, flags, unsupported ?? []));
        return cmd;
    }

    private static IDataReader SqlServerShapeReader(SsCol[] columns, int flags, (string Name, string DataType)[] unsupported)
    {
        var rows = columns.Select(c => (c.Name, c.DataType, Declared: c.Declared ?? c.DataType, c.MaxLength, c.Identity, c.XmlCollection, Unsupported: false))
            .Concat(unsupported.Select(u => (u.Name, u.DataType, Declared: u.DataType, MaxLength: 0, Identity: false, XmlCollection: (string)null, Unsupported: true)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reader = Substitute.For<IDataReader>();
        var i = -1;
        reader.Read().Returns(_ => ++i < Math.Max(rows.Length, 1));
        reader.IsDBNull(0).Returns(false);
        reader.GetValue(0).Returns(flags);
        reader.IsDBNull(1).Returns(_ => rows.Length == 0);
        reader.GetString(1).Returns(_ => rows[i].Name);
        reader.GetString(2).Returns(_ => rows[i].DataType);
        reader.GetString(3).Returns(_ => rows[i].Declared);
        reader.IsDBNull(4).Returns(_ => rows[i].MaxLength == 0);
        reader.GetValue(4).Returns(_ => (object)rows[i].MaxLength);
        reader.IsDBNull(5).Returns(true);
        reader.IsDBNull(6).Returns(true);
        reader.IsDBNull(7).Returns(true);
        reader.GetString(8).Returns("YES");
        reader.GetValue(9).Returns(_ => rows[i].Identity);
        reader.IsDBNull(10).Returns(_ => rows[i].XmlCollection == null);
        reader.GetString(10).Returns(_ => rows[i].XmlCollection);
        reader.GetValue(11).Returns(_ => rows[i].Unsupported);
        return reader;
    }

    /// <summary>
    /// Wires an NSubstitute command to record every parameter bound through
    /// CreateParameter()/Parameters.Add(...). Identifiers are now passed as parameters rather
    /// than interpolated into the SQL, so tests assert on the captured (name, value) pairs.
    /// The returned list fills as the command under test runs.
    /// </summary>
    private static List<(string Name, object Value)> CaptureBoundParameters(IDbCommand cmd)
    {
        var bound = new List<(string Name, object Value)>();
        cmd.CreateParameter().Returns(_ => Substitute.For<IDbDataParameter>());
        cmd.Parameters.When(p => p.Add(Arg.Any<object>())).Do(ci =>
        {
            var parameter = (IDbDataParameter)ci.Arg<object>();
            bound.Add((parameter.ParameterName, parameter.Value));
        });
        return bound;
    }

    #endregion

    #region Helper Methods - PostgreSQL Mock

    // A column of the table shape a PostgreSQL mock command answers with: its udt_name, the type as format_type renders
    // it (schema-qualified outside pg_catalog), and what feeds or compares it.
    private sealed record PgCol(string Name, string UdtName, string Type, bool Identity = false, bool Serial = false, string Sequence = null,
        string CompareCast = null);

    // PostgreSQL reads a table's whole shape in one query; a rule lookup (DisableRules) is the only other read.
    private static IDbCommand CreatePostgreSqlShapeCommand(PgCol[] columns, (string Name, string UdtName)[] unsupported = null, string[] rules = null)
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteReader().Returns(_ => cmd.CommandText != null && cmd.CommandText.Contains("pg_rules")
            ? StringRowsReader(rules ?? [])
            : PostgreSqlShapeReader(columns, unsupported ?? []));
        return cmd;
    }

    private static IDataReader StringRowsReader(string[] rows)
    {
        var reader = Substitute.For<IDataReader>();
        var i = -1;
        reader.Read().Returns(_ => ++i < rows.Length);
        reader.GetString(0).Returns(_ => rows[i]);
        return reader;
    }

    private static IDataReader PostgreSqlShapeReader(PgCol[] columns, (string Name, string UdtName)[] unsupported)
    {
        var rows = columns.Select((c, n) => (c.Name, c.UdtName, c.Type, c.Identity, Fed: c.Identity || c.Serial, c.Sequence, c.CompareCast, Unsupported: false, Ordinal: n + 1))
            .Concat(unsupported.Select((u, n) => (u.Name, u.UdtName, Type: u.UdtName, Identity: false, Fed: false, Sequence: (string)null, CompareCast: (string)null,
                                                  Unsupported: true, Ordinal: columns.Length + n + 1)))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();
        var reader = Substitute.For<IDataReader>();
        var i = -1;
        reader.Read().Returns(_ => ++i < rows.Length);
        reader.GetString(0).Returns(_ => rows[i].Name);
        reader.GetString(1).Returns(_ => rows[i].UdtName);
        foreach (var column in new[] { 2, 3, 4, 5 }) reader.IsDBNull(column).Returns(true);
        reader.GetString(6).Returns("YES");
        reader.GetValue(7).Returns(_ => rows[i].Identity);
        reader.GetString(8).Returns(_ => rows[i].Type);
        reader.GetValue(9).Returns(_ => rows[i].Unsupported);
        reader.GetValue(10).Returns(true);
        reader.IsDBNull(11).Returns(false);
        reader.GetValue(11).Returns(_ => rows[i].Fed);
        reader.IsDBNull(12).Returns(_ => rows[i].Sequence == null);
        reader.GetString(12).Returns(_ => rows[i].Sequence);
        reader.GetValue(13).Returns(_ => rows[i].Identity);
        reader.GetValue(14).Returns(_ => rows[i].Ordinal);
        reader.IsDBNull(15).Returns(_ => rows[i].CompareCast == null);
        reader.GetString(15).Returns(_ => rows[i].CompareCast);
        return reader;
    }

    #endregion

    #region Helper Methods - MySQL Mock

    private record MySqlColumnDef(
        string Name, string DataType, long? CharMaxLen,
        long? NumPrec, long? NumScale, int? DtPrec,
        string ColType, string Extra, string GenExpr);

    private static IDbCommand CreateMySqlMockCommand(MySqlColumnDef[] columns)
    {
        var cmd = Substitute.For<IDbCommand>();

        // Return a fresh reader on each ExecuteReader() call so fragment methods
        // that each query independently all get the full column set. The MariaDB JSON-column
        // detection issues a CHECK_CONSTRAINTS lookup before the column read; feed it an empty
        // reader so the mock's column defs (which set DATA_TYPE='json' directly) drive JSON-ness.
        cmd.ExecuteReader().Returns(ci =>
            cmd.CommandText != null && cmd.CommandText.Contains("CHECK_CONSTRAINTS")
                ? CreateEmptyMockReader()
                : CreateMySqlMockReader(columns));
        return cmd;
    }

    private static IDataReader CreateEmptyMockReader()
    {
        var reader = Substitute.For<IDataReader>();
        reader.Read().Returns(false);
        return reader;
    }

    private static IDataReader CreateMySqlMockReader(MySqlColumnDef[] columns)
    {
        var reader = Substitute.For<IDataReader>();
        var currentIndex = -1;
        reader.Read().Returns(ci =>
        {
            currentIndex++;
            return currentIndex < columns.Length;
        });

        reader.GetOrdinal("COLUMN_NAME").Returns(0);
        reader.GetOrdinal("DATA_TYPE").Returns(1);
        reader.GetOrdinal("CHARACTER_MAXIMUM_LENGTH").Returns(2);
        reader.GetOrdinal("NUMERIC_PRECISION").Returns(3);
        reader.GetOrdinal("NUMERIC_SCALE").Returns(4);
        reader.GetOrdinal("DATETIME_PRECISION").Returns(5);
        reader.GetOrdinal("COLUMN_TYPE").Returns(6);
        reader.GetOrdinal("EXTRA").Returns(7);
        reader.GetOrdinal("GENERATION_EXPRESSION").Returns(8);

        reader.GetString(0).Returns(ci => columns[currentIndex].Name);
        reader.GetString(1).Returns(ci => columns[currentIndex].DataType);
        reader.IsDBNull(2).Returns(ci => !columns[currentIndex].CharMaxLen.HasValue);
        reader.GetInt64(2).Returns(ci => columns[currentIndex].CharMaxLen ?? 0);
        reader.IsDBNull(3).Returns(ci => !columns[currentIndex].NumPrec.HasValue);
        reader.GetInt64(3).Returns(ci => columns[currentIndex].NumPrec ?? 0);
        reader.IsDBNull(4).Returns(ci => !columns[currentIndex].NumScale.HasValue);
        reader.GetInt64(4).Returns(ci => columns[currentIndex].NumScale ?? 0);
        reader.IsDBNull(5).Returns(ci => !columns[currentIndex].DtPrec.HasValue);
        reader.GetInt32(5).Returns(ci => columns[currentIndex].DtPrec ?? 0);
        reader.GetString(6).Returns(ci => columns[currentIndex].ColType);
        reader.GetString(7).Returns(ci => columns[currentIndex].Extra);
        reader.IsDBNull(8).Returns(ci => columns[currentIndex].GenExpr == null);
        reader.GetString(8).Returns(ci => columns[currentIndex].GenExpr ?? "");

        return reader;
    }

    #endregion

    #region Unsupported Type Exclusion Tests

    [Test]
    public void BuildMergeScript_PostgreSQL_DoesNotExcludePointOrPolygon()
    {
        // The unsupported type filter constant should not contain point or polygon
        // (they overlap with PostGIS geometry types and must remain supported)
        var filter = MergeScriptHelper.PostgreSqlUnsupportedTypeFilter;

        // Extract just the NOT IN list from the filter
        var notInMatch = System.Text.RegularExpressions.Regex.Match(filter, @"NOT IN \([^)]+\)");
        Assert.That(notInMatch.Success, Is.True, "Expected a NOT IN clause in the filter");

        var notInList = notInMatch.Value;
        Assert.That(notInList, Does.Not.Contain("'point'"),
            "point should not be excluded — it overlaps with PostGIS");
        Assert.That(notInList, Does.Not.Contain("'polygon'"),
            "polygon should not be excluded — it overlaps with PostGIS");
    }

    [Test]
    public void SqlServerUnsupportedTypeFilter_ContainsAllExpectedTypes()
    {
        var filter = MergeScriptHelper.SqlServerUnsupportedTypeFilter;
        Assert.That(filter, Does.Contain("'sql_variant'"));
        Assert.That(filter, Does.Contain("'rowversion'"));
        Assert.That(filter, Does.Contain("'timestamp'"));
    }

    [Test]
    public void PostgreSqlUnsupportedTypeFilter_ContainsAllExpectedTypes()
    {
        var filter = MergeScriptHelper.PostgreSqlUnsupportedTypeFilter;
        Assert.That(filter, Does.Contain("'tsvector'"));
        Assert.That(filter, Does.Contain("'tsquery'"));
        Assert.That(filter, Does.Contain("'money'"));
        Assert.That(filter, Does.Contain("'box'"));
        Assert.That(filter, Does.Contain("'circle'"));
        Assert.That(filter, Does.Contain("'line'"));
        Assert.That(filter, Does.Contain("'lseg'"));
        Assert.That(filter, Does.Contain("'path'"));
        Assert.That(filter, Does.Contain("t.typtype = 'c'")); // composite type check
    }

    [Test]
    public void PostgreSqlUnsupportedTypeFilter_DoesNotContainPointOrPolygon()
    {
        var filter = MergeScriptHelper.PostgreSqlUnsupportedTypeFilter;
        Assert.That(filter, Does.Not.Contain("'point'"));
        Assert.That(filter, Does.Not.Contain("'polygon'"));
    }

    #endregion

    #region Unsupported Column Comments Tests

    [Test]
    public void GetUnsupportedColumnComments_MySQL_ReturnsEmpty()
    {
        var cmd = Substitute.For<IDbCommand>();

        var result = MergeScriptHelper.GetUnsupportedColumnComments(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void BuildMergeScript_SqlServer_WithUnsupportedColumns_PrependsComments()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")], unsupported: [("VariantCol", "sql_variant")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.StartWith("-- Column [VariantCol] skipped: sql_variant is not supported for data delivery"));
        Assert.That(result, Does.Contain("MERGE INTO"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_NoUnsupportedColumns_DoesNotPrependComments()
    {
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Not.StartWith("--"));
        Assert.That(result, Does.Contain("MERGE INTO"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithUnsupportedColumns_PrependsComments()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")], unsupported: [("search_vector", "tsvector")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.StartWith("-- Column \"search_vector\" skipped: tsvector is not supported for data delivery"));
        Assert.That(result, Does.Contain("MERGE INTO"));
    }

    #endregion

    #region MySQL Fragment Method Tests

    [Test]
    public void GetJsonColumnDefinitions_MySQL_ReturnsJsonTableColumns()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10, 0, null, "int", "", null),
            new("name", "varchar", 100, null, null, null, "varchar(100)", "", null),
            new("location", "geometry", null, null, null, null, "geometry", "", null),
        });

        var result = MergeScriptHelper.GetJsonColumnDefinitions(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Does.Contain("`id` INT PATH '$.id'"));
        Assert.That(result, Does.Contain("`name` VARCHAR(100) CHARACTER SET utf8mb4 PATH '$.name'"));
        Assert.That(result, Does.Contain("`location` LONGTEXT CHARACTER SET utf8mb4 PATH '$.location'"));
    }

    [Test]
    public void GetJsonSelectColumns_MySQL_ReturnsSelectExpressions()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10, 0, null, "int", "", null),
            new("location", "geometry", null, null, null, null, "geometry", "", null),
            new("data", "blob", 65535, null, null, null, "blob", "", null),
        });

        var result = MergeScriptHelper.GetJsonSelectColumns(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Does.Contain("`id`"));
        Assert.That(result, Does.Contain("ST_GeomFromText(`location`, COALESCE(`location.STSrid`, 0))"));
        Assert.That(result, Does.Contain("FROM_BASE64(`data`)"));
    }

    [Test]
    public void GetInsertColumns_MySQL_ReturnsBacktickQuotedList()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10, 0, null, "int", "", null),
            new("name", "varchar", 100, null, null, null, "varchar(100)", "", null),
        });

        var result = MergeScriptHelper.GetInsertColumns(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Is.EqualTo("`id`, `name`"));
    }

    [Test]
    public void GetUpdateColumns_MySQL_ReturnsColumnNames()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10, 0, null, "int", "", null),
            new("name", "varchar", 100, null, null, null, "varchar(100)", "", null),
        });

        var result = MergeScriptHelper.GetUpdateColumns(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Does.Contain("`id`"));
        Assert.That(result, Does.Contain("`name`"));
    }

    // The update list is plain names: whether a column is JSON is in its column info, not a prefix on its name.
    [Test]
    public void GetUpdateColumns_MySQL_ListsAJsonColumnByName()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10, 0, null, "int", "", null),
            new("metadata", "json", null, null, null, null, "json", "", null),
        });

        var result = MergeScriptHelper.GetUpdateColumns(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Is.EqualTo("`id`,`metadata`"));
    }

    #endregion

    #region MySQL GetMatchColumns Tests

    [Test]
    public void GetMatchColumns_MySQL_SingleKey()
    {
        var result = MergeScriptHelper.GetMatchColumns(Platform.MySQL, "id");
        Assert.That(result, Is.EqualTo("`Source`.`id` = `Target`.`id`"));
    }

    [Test]
    public void GetMatchColumns_MySQL_CompositeKey()
    {
        var result = MergeScriptHelper.GetMatchColumns(Platform.MySQL, "id,code");
        Assert.That(result, Does.Contain("`Source`.`id` = `Target`.`id`"));
        Assert.That(result, Does.Contain("`Source`.`code` = `Target`.`code`"));
        Assert.That(result, Does.Contain(" AND "));
    }

    [Test]
    public void GetMatchColumns_MySQL_NullableKey()
    {
        var result = MergeScriptHelper.GetMatchColumns(Platform.MySQL, "*nullable_col");
        Assert.That(result, Does.Contain("`Source`.`nullable_col` = `Target`.`nullable_col`"));
        Assert.That(result, Does.Contain("`Source`.`nullable_col` IS NULL AND `Target`.`nullable_col` IS NULL"));
    }

    [Test]
    public void GetMatchColumns_MySQL_TrimsBackticks()
    {
        var result = MergeScriptHelper.GetMatchColumns(Platform.MySQL, "`id`");
        Assert.That(result, Is.EqualTo("`Source`.`id` = `Target`.`id`"));
    }

    [Test]
    public void GetMatchColumns_MySQL_MixedKeys()
    {
        var result = MergeScriptHelper.GetMatchColumns(Platform.MySQL, "id,*nullable_col");
        Assert.That(result, Does.Contain("`Source`.`id` = `Target`.`id`"));
        Assert.That(result, Does.Contain("(`Source`.`nullable_col` = `Target`.`nullable_col` OR (`Source`.`nullable_col` IS NULL AND `Target`.`nullable_col` IS NULL))"));
    }

    #endregion

    #region BuildMergeScript - destSchemaOverride (DataTongs schema-template extraction, slice 7)

    // The destSchemaOverride parameter exists for the DataTongs schema-template extraction
    // path: the caller knows the SOURCE schema (used to query INFORMATION_SCHEMA on the
    // source database) but wants destination-side refs in the emitted body to carry a
    // *different* identifier — typically the {{SchemaName}} engine token. Catalog probes
    // continue to use schemaOrDb so the source metadata lookup still resolves; destination
    // refs (MERGE INTO, ALTER TABLE, IDENTITY_INSERT, SETVAL, content-file token) use the
    // override. SchemaQuench's DataDeliveryProcessor leaves the override null and behavior
    // is identical to today.

    [Test]
    public void BuildMergeScript_SqlServer_DestSchemaOverride_RewritesDestinationRefs()
    {
        // Covers MERGE INTO, ALTER TABLE DISABLE/ENABLE TRIGGER. Identity-insert lives in its
        // own test below because tokenizeScripts:true sets jsonKeys=null which short-circuits
        // the IDENTITY-column-in-keys check away — that combination is the realistic schema-
        // template case but it suppresses IDENTITY_INSERT regardless of override.
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "tenant_seed", "Customers", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: true,
            tokenizeScripts: true, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: "{{SchemaName}}");

        Assert.That(result, Does.Contain("MERGE INTO [{{SchemaName}}].[Customers] AS Target"),
            "MERGE INTO destination must use the override, not the source schema.");
        Assert.That(result, Does.Contain("ALTER TABLE [{{SchemaName}}].[Customers] DISABLE TRIGGER ALL"));
        Assert.That(result, Does.Contain("ALTER TABLE [{{SchemaName}}].[Customers] ENABLE TRIGGER ALL"));
        Assert.That(result, Does.Not.Contain("[tenant_seed].[Customers]"),
            "Source-schema literal must not appear in any destination ref when override is set.");
    }

    [Test]
    public void BuildMergeScript_SqlServer_DestSchemaOverride_IdentityInsertUsesOverride()
    {
        // tokenizeScripts:false populates jsonKeys from tableData; an identity key column
        // present in jsonKeys then enables the IDENTITY_INSERT block. Verify it uses the
        // override.
        var cmd = CreateSqlServerShapeCommand([new("Id", "int", Identity: true)], flags: 1);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "tenant_seed", "Customers", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: "{{SchemaName}}");

        Assert.That(result, Does.Contain("SET IDENTITY_INSERT [{{SchemaName}}].[Customers] ON"));
        Assert.That(result, Does.Contain("SET IDENTITY_INSERT [{{SchemaName}}].[Customers] OFF"));
        Assert.That(result, Does.Not.Contain("[tenant_seed].[Customers]"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_DestSchemaOverride_ContentTokenUnqualified()
    {
        // The content-file token must reference the unqualified content filename in
        // schema-template mode — DataTongs writes the .tabledata file with an unqualified
        // name (Customers.tabledata), so the token in the merge body must match.
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "tenant_seed", "Customers", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: "{{SchemaName}}");

        Assert.That(result, Does.Contain("{{Customers.tabledata}}"),
            "Content-file token must be unqualified (no source schema) when override is set.");
        Assert.That(result, Does.Not.Contain("{{tenant_seed.Customers.tabledata}}"),
            "Schema-qualified content-file token must not appear when override is set.");
    }

    [Test]
    public void BuildMergeScript_SqlServer_DestSchemaOverride_CatalogQueriesStillUseSourceSchema()
    {
        // Catalog probes (GetJsonSelectColumns, NeedsIdentityInsert, GetJsonColumnDefinitions,
        // GetInsertColumns, GetUpdateColumns) must continue to use the actual source schema —
        // that's where the table physically exists. Only the EMITTED script body switches to
        // the override.
        var cmd = Substitute.For<IDbCommand>();

        // Sequence: cliff -> unsupported(null) -> cliff -> jsonSelectCols -> needsIdentity -> cliff ->
        // jsonColDefs -> cliff -> insertCols. The 0 entries answer the cliff self-detection
        // (not-below-cliff, so the modern STRING_AGG path is taken).
        var sequence = new Queue<object>(new object[] { 0, null, 0, "[Id]", false, 0, "           [Id] INT", 0, "        [Id]" });
        cmd.ExecuteScalar().Returns(_ => sequence.Count > 0 ? sequence.Dequeue() : null);
        var bound = CaptureBoundParameters(cmd);

        MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "tenant_seed", "Customers", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: "{{SchemaName}}");

        // Catalog probes bind the SOURCE schema as a parameter; the emitted-script override token
        // must never reach the source-metadata queries.
        Assert.That(bound.Any(p => Equals(p.Value, "tenant_seed")), Is.True,
            "Catalog probes must use the SOURCE schema for INFORMATION_SCHEMA lookups, not the override.");
        Assert.That(bound.Any(p => Equals(p.Value, "{{SchemaName}}")), Is.False,
            "Catalog probes must not pass the engine token through to source metadata queries.");
    }

    [Test]
    public void BuildMergeScript_SqlServer_DestSchemaOverride_NullKeepsTodayBehavior()
    {
        // Regression guard: omitting destSchemaOverride or passing null must produce
        // exactly the same output as before slice 7 (literal source schema everywhere).
        var cmd = CreateSqlServerShapeCommand([new("Id", "int")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "Users", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: null);

        Assert.That(result, Does.Contain("MERGE INTO [dbo].[Users] AS Target"));
        Assert.That(result, Does.Contain("{{dbo.Users.tabledata}}"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_DestSchemaOverride_RewritesDestinationRefs()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer", Identity: true, Sequence: "tenant_seed.customers_id_seq")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "tenant_seed", "customers", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: true,
            tokenizeScripts: true, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: "{{SchemaName}}");

        Assert.That(result, Does.Contain("\"{{SchemaName}}\".\"customers\""),
            "PostgreSQL destination refs must use the override in place of the source schema.");
        Assert.That(result, Does.Not.Contain("\"tenant_seed\".\"customers\""),
            "Source-schema literal must not appear in destination refs when override is set.");
        Assert.That(result, Does.Contain("{{customers.tabledata}}"),
            "PostgreSQL content-file token must also be unqualified when override is set.");
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_DestSchemaOverride_TriggerAndSetvalUseOverride()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer", Identity: true, Sequence: "tenant_seed.customers_id_seq")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "tenant_seed", "customers", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: true,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: "{{SchemaName}}");

        Assert.That(result, Does.Contain("ALTER TABLE \"{{SchemaName}}\".\"customers\""),
            "PostgreSQL ALTER TABLE (trigger disable/enable) must use the destination override.");
        // SETVAL block reads MAX(col) from destination table — must use override.
        Assert.That(result, Does.Contain("FROM \"{{SchemaName}}\".\"customers\""),
            "SETVAL block FROM clause must use the destination override.");
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_DestSchemaOverride_NullKeepsTodayBehavior()
    {
        var cmd = CreatePostgreSqlShapeCommand([new("id", "int4", "integer")]);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: null);

        Assert.That(result, Does.Contain("\"public\".\"test_table\""));
        Assert.That(result, Does.Contain("{{public.test_table.tabledata}}"));
    }

    // The destination qualifier on MySQL: null keeps the database the catalog was read from (delivery builds its script
    // in the target); "" names the table alone, so a script written now writes into whatever database it is deployed
    // to; a name qualifies with that database.
    [TestCase(null, "`testdb`.`testtable`", TestName = "BuildMergeScript_MySQL_NoOverride_KeepsTheCatalogDatabase")]
    [TestCase("", "INTO `testtable`", TestName = "BuildMergeScript_MySQL_EmptyOverride_NamesTheTableAlone")]
    [TestCase("other", "`other`.`testtable`", TestName = "BuildMergeScript_MySQL_Override_QualifiesWithIt")]
    public void BuildMergeScript_MySQL_DestinationQualifier(string destOverride, string expected)
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "`id`",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: destOverride);

        Assert.That(result, Does.Contain(expected));
        if (destOverride != null)
            Assert.That(result, Does.Not.Contain("`testdb`."), "no reference may keep the catalog database: " + result);
    }

    #endregion

    // MariaDB 10.2's JSON parser refuses an escaped surrogate pair, so on 10.2 it is written as the character itself.
    [TestCase(1002, @"[{""n"":""a😀b""}]", "[{\"n\":\"a\U0001F600b\"}]", TestName = "SurrogateEscapes_10_2_Decoded")]
    [TestCase(1002, @"[{""n"":""😀""}]", "[{\"n\":\"\U0001F600\"}]", TestName = "SurrogateEscapes_10_2_UpperHexDecoded")]
    [TestCase(1002, @"[{""n"":""\\ud83d\\ude00""}]", @"[{""n"":""\\ud83d\\ude00""}]", TestName = "SurrogateEscapes_10_2_EscapedBackslashIsText")]
    [TestCase(1002, @"[{""n"":""\\😀""}]", "[{\"n\":\"\\\\\U0001F600\"}]", TestName = "SurrogateEscapes_10_2_AfterAnEscapedBackslash")]
    [TestCase(1002, @"[{""n"":""\ud83d""}]", @"[{""n"":""\ud83d""}]", TestName = "SurrogateEscapes_10_2_LoneSurrogateUnchanged")]
    [TestCase(1002, @"[{""n"":""é""}]", @"[{""n"":""é""}]", TestName = "SurrogateEscapes_10_2_BmpEscapeUnchanged")]
    [TestCase(1003, @"[{""n"":""😀""}]", @"[{""n"":""😀""}]", TestName = "SurrogateEscapes_10_3_Unchanged")]
    [TestCase(800, @"[{""n"":""😀""}]", @"[{""n"":""😀""}]", TestName = "SurrogateEscapes_MySql_Unchanged")]
    public void DecodeSurrogateEscapesMySql_OnlyOnMariaDb10_2(int versionNum, string payload, string expected)
        => Assert.That(MergeScriptHelper.DecodeSurrogateEscapesMySql(payload, versionNum), Is.EqualTo(expected));

    [TestCase("8.0.11", true)]
    [TestCase("8.0.11-log", true)]
    [TestCase("8.0.12", true)]
    [TestCase("8.0.20", true)]
    [TestCase("8.0.21", false)]
    [TestCase("8.0.36", false)]
    [TestCase("10.6.5-MariaDB", false)]
    [TestCase(null, false)]
    public void JsonTableMisreadsScalarsMySql_ByServerVersion(string serverVersion, bool expected)
    {
        Assert.That(MergeScriptHelper.JsonTableMisreadsScalarsMySql(serverVersion), Is.EqualTo(expected));
    }

    // The server is asked only on 8.0, where a patch decides it.
    [TestCase(507)]
    [TestCase(804)]
    [TestCase(1006)]
    [TestCase(0)]
    public void JsonTableMisreadsScalarsMySql_AsksNothingOffTheEightZeroLine(int versionNum)
    {
        var cmd = Substitute.For<System.Data.IDbCommand>();
        Assert.That(MergeScriptHelper.JsonTableMisreadsScalarsMySql(cmd, versionNum), Is.False);
        cmd.DidNotReceive().ExecuteScalar();
    }

    // Paired with the integration test that delivers nulls, 2^31 and 2^64-1: on the affected patches every column is read as
    // JSON and a JSON null becomes SQL NULL; a JSON column stays a document.
    [Test]
    public void BuildJsonRowSourceMySql_ReadsScalarsAsJson_OnlyWhereJsonTableMisreadsThem()
    {
        var cols = new List<MergeScriptHelper.MySqlColumnInfo>
        {
            new() { Name = "u", DataType = "bigint", ColumnType = "bigint unsigned" },
            new() { Name = "j", DataType = "json", ColumnType = "json", IsJson = true },
            new() { Name = "t", DataType = "varchar", ColumnType = "varchar(10)", CharMaxLength = 10 },
            new() { Name = "y", DataType = "year", ColumnType = "year" },
        };
        var misread = MergeScriptHelper.BuildJsonRowSourceMySql(cols, 800, jsonTableMisreadsScalars: true);
        var plain = MergeScriptHelper.BuildJsonRowSourceMySql(cols, 800);
        Assert.Multiple(() =>
        {
            Assert.That(misread, Does.Contain("`u` JSON PATH '$.u'").And.Contain("`j` JSON PATH '$.j'"));
            Assert.That(misread, Does.Contain("IF(JSON_TYPE(_ss_jt.`t`) = 'NULL', NULL, JSON_UNQUOTE(_ss_jt.`t`)) AS `t`"));
            Assert.That(misread, Does.Contain("IF(JSON_TYPE(_ss_jt.`u`) = 'NULL', NULL, IF(JSON_TYPE(_ss_jt.`u`) = 'BOOLEAN'"));
            Assert.That(misread, Does.Contain("NULL, CAST(IF(JSON_TYPE(_ss_jt.`y`) = 'BOOLEAN'"));
            Assert.That(misread, Does.Contain("_ss_jt.`j` AS `j`").And.EndWith(") AS jt"));
            Assert.That(plain, Does.Contain("`u` BIGINT UNSIGNED PATH '$.u'").And.Not.Contain("_ss_jt"));
        });
    }

    // A script generated on a server whose JSON_TABLE reads scalars correctly is delivered to one that misreads them: the
    // marked typed clause is rebuilt in the read-as-JSON form from the target table's columns, only the columns it declared.
    [Test]
    public void RewriteTypedRowSourcesMySql_RebuildsAMarkedClause_InTheReadAsJsonForm()
    {
        var cols = new List<MergeScriptHelper.MySqlColumnInfo>
        {
            new() { Name = "id", DataType = "int", ColumnType = "int" },
            new() { Name = "note", DataType = "varchar", ColumnType = "varchar(20)", CharMaxLength = 20 },
            new() { Name = "y", DataType = "year", ColumnType = "year" },
        };
        var target = cols.Append(new MergeScriptHelper.MySqlColumnInfo { Name = "added_later", DataType = "int", ColumnType = "int" }).ToList();
        const string table = "odd*/name";
        var typed = MergeScriptHelper.BuildJsonRowSourceMySql(cols, 800);
        var script = "INSERT INTO `t` (`id`) SELECT jt.`id` FROM " + MergeScriptHelper.TypedRowSourceOpenMySql +
                     Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(table)) + "*/" + typed + MergeScriptHelper.TypedRowSourceCloseMySql + ";";
        string asked = null;

        var rewritten = MergeScriptHelper.RewriteTypedRowSourcesMySql(script, t => { asked = t; return target; });

        Assert.Multiple(() =>
        {
            Assert.That(asked, Is.EqualTo(table), "the marker names the table the clause reads into");
            Assert.That(rewritten, Is.EqualTo("INSERT INTO `t` (`id`) SELECT jt.`id` FROM " +
                                              MergeScriptHelper.BuildJsonRowSourceMySql(cols, 800, jsonTableMisreadsScalars: true) + ";"),
                "the typed clause becomes the read-as-JSON form, over the clause's own columns only");
            Assert.That(MergeScriptHelper.RewriteTypedRowSourcesMySql("SELECT 1", _ => throw new AssertionException("asked")), Is.EqualTo("SELECT 1"));
        });
    }

    // Paired with the integration tests that deliver booleans and YEAR 0: MariaDB 10.2-10.5 shreds raw JSON and reads it
    // as a typed JSON_TABLE column would, keeping SchemaSmith_JsonScalarStr for text so 4-byte characters survive.
    [Test]
    public void BuildJsonRowSourceMySql_MariaDbShred_ReadsBooleansAndYearsAsNumbers()
    {
        var cols = new List<MergeScriptHelper.MySqlColumnInfo>
        {
            new() { Name = "i", DataType = "int", ColumnType = "int" },
            new() { Name = "j", DataType = "json", ColumnType = "json", IsJson = true },
            new() { Name = "t", DataType = "varchar", ColumnType = "varchar(10)", CharMaxLength = 10 },
            new() { Name = "y", DataType = "year", ColumnType = "year" },
        };
        var shred = MergeScriptHelper.BuildJsonRowSourceMySql(cols, 1002);
        Assert.Multiple(() =>
        {
            Assert.That(shred, Does.Contain("WITH RECURSIVE _ss_seq").And.Contain(") AS _ss_jt"));
            Assert.That(shred, Does.Contain("SchemaSmith_JsonScalarStr(_ss_jt.`t`) AS `t`"));
            Assert.That(shred, Does.Contain("IF(JSON_TYPE(_ss_jt.`i`) = 'BOOLEAN', IF(JSON_UNQUOTE(_ss_jt.`i`) = 'true', '1', '0'), SchemaSmith_JsonScalarStr(_ss_jt.`i`)) AS `i`"));
            Assert.That(shred, Does.Contain("CAST(IF(JSON_TYPE(_ss_jt.`y`) = 'BOOLEAN'"));
            Assert.That(shred, Does.Contain("_ss_jt.`j` AS `j`").And.EndWith(") AS jt"));
        });
    }

    // MySQL went 5.7, 8.x, 9.x, then calendar years from 26: 26.7 reads 2607. Only MariaDB 10.2-10.5 lacks JSON_TABLE.
    [TestCase(0, true)]
    [TestCase(507, false)]
    [TestCase(800, true)]
    [TestCase(904, true)]
    [TestCase(1002, false)]
    [TestCase(1005, false)]
    [TestCase(1006, true)]
    [TestCase(1300, true)]
    [TestCase(2600, true)]
    [TestCase(2607, true)]
    public void MySqlFamilyHasJsonTable_ByVersionNum(int versionNum, bool expected)
        => Assert.That(MergeScriptHelper.MySqlFamilyHasJsonTable(versionNum), Is.EqualTo(expected));

    // MariaDB's JSON_TABLE kept string escapes literally until 10.6.20, 10.11.10, 11.2.6, 11.4.4 and 11.6.2 (MDEV-27412).
    [TestCase("10.5.29-MariaDB", false, TestName = "JsonTableMisreadsStrings_10_5_HasNoJsonTable")]
    [TestCase("10.6.8-MariaDB", true, TestName = "JsonTableMisreadsStrings_10_6_8")]
    [TestCase("10.6.19-MariaDB-log", true, TestName = "JsonTableMisreadsStrings_10_6_19")]
    [TestCase("10.6.20-MariaDB", false, TestName = "JsonTableMisreadsStrings_10_6_20")]
    [TestCase("10.9.8-MariaDB", true, TestName = "JsonTableMisreadsStrings_10_9")]
    [TestCase("10.11.9-MariaDB", true, TestName = "JsonTableMisreadsStrings_10_11_9")]
    [TestCase("10.11.10-MariaDB", false, TestName = "JsonTableMisreadsStrings_10_11_10")]
    [TestCase("11.1.6-MariaDB", true, TestName = "JsonTableMisreadsStrings_11_1")]
    [TestCase("11.2.5-MariaDB", true, TestName = "JsonTableMisreadsStrings_11_2_5")]
    [TestCase("11.2.6-MariaDB", false, TestName = "JsonTableMisreadsStrings_11_2_6")]
    [TestCase("11.3.2-MariaDB", true, TestName = "JsonTableMisreadsStrings_11_3")]
    [TestCase("11.4.3-MariaDB-ubu2404", true, TestName = "JsonTableMisreadsStrings_11_4_3")]
    [TestCase("11.4.4-MariaDB-ubu2404", false, TestName = "JsonTableMisreadsStrings_11_4_4")]
    [TestCase("11.5.2-MariaDB", true, TestName = "JsonTableMisreadsStrings_11_5")]
    [TestCase("11.6.1-MariaDB", true, TestName = "JsonTableMisreadsStrings_11_6_1")]
    [TestCase("11.6.2-MariaDB", false, TestName = "JsonTableMisreadsStrings_11_6_2")]
    [TestCase("11.8.3-MariaDB", false, TestName = "JsonTableMisreadsStrings_11_8")]
    [TestCase("8.0.36", false, TestName = "JsonTableMisreadsStrings_MySql")]
    public void MariaDbJsonTableMisreadsStrings_ByVersion(string version, bool expected)
        => Assert.That(MergeScriptHelper.MariaDbJsonTableMisreadsStrings(version), Is.EqualTo(expected));

    [Test]
    public void QuoteRowNumbers_QuotesOnlyTheNamedRowKeys()
    {
        var keys = new HashSet<string> { "d", "e" };
        Assert.That(MergeScriptHelper.QuoteRowNumbers(
                @"[{""d"":1.25,""e"":-3E+2,""n"":4.5,""j"":{""d"":7.5},""s"":""d"",""x"":null},{""d"":null, ""e"" : 12}]", keys),
            Is.EqualTo(@"[{""d"":""1.25"",""e"":""-3E+2"",""n"":4.5,""j"":{""d"":7.5},""s"":""d"",""x"":null},{""d"":null, ""e"" : ""12""}]"));
    }

    [Test]
    public void QuoteRowNumbers_LeavesStringsThatLookLikeKeysAlone()
    {
        const string json = @"[{""s"":""\""d\"":1.5"",""d"":""already""}]";
        Assert.That(MergeScriptHelper.QuoteRowNumbers(json, new HashSet<string> { "d" }), Is.EqualTo(json));
    }

    [Test]
    public void QuoteRowNumbers_NoKeysOrNullIsUnchanged()
    {
        Assert.That(MergeScriptHelper.QuoteRowNumbers(@"[{""d"":1.5}]", new HashSet<string>()), Is.EqualTo(@"[{""d"":1.5}]"));
        Assert.That(MergeScriptHelper.QuoteRowNumbers(null, new HashSet<string> { "d" }), Is.Null);
    }

    [TestCase(1002, true)]
    [TestCase(1300, true)]
    [TestCase(904, false)]
    [TestCase(2607, false)]   // MySQL 26.7, not MariaDB
    public void IsMariaDbVersionNum_ByVersionNum(int versionNum, bool expected)
        => Assert.That(MergeScriptHelper.IsMariaDbVersionNum(versionNum), Is.EqualTo(expected));

    // The deferred self-reference pass inserts only the rows whose key is missing. With no key column known it has
    // nothing to match on, and an empty match list produced WHERE NOT EXISTS (... WHERE ), a syntax error.
    [TestCase(null)]
    [TestCase("")]
    [TestCase("`unknown`")]
    public void BuildMissingRowsFilterMySql_WithNoKnownKey_IsNull(string keyColumns)
    {
        var columns = new List<MergeScriptHelper.MySqlColumnInfo> { new() { Name = "id", DataType = "int", ColumnType = "int" } };
        Assert.That(MergeScriptHelper.BuildMissingRowsFilterMySql("db", "t", columns, keyColumns), Is.Null);
    }

    [Test]
    public void BuildMissingRowsFilterMySql_MatchesOnTheKey()
    {
        var columns = new List<MergeScriptHelper.MySqlColumnInfo>
        {
            new() { Name = "id", DataType = "int", ColumnType = "int" },
            new() { Name = "parent_id", DataType = "int", ColumnType = "int" }
        };
        var filter = MergeScriptHelper.BuildMissingRowsFilterMySql("db", "t", columns, "`id`");
        Assert.That(filter, Does.StartWith("WHERE NOT EXISTS (SELECT 1 FROM `db`.`t` _ss_live WHERE _ss_live.`id` <=> "));
        Assert.That(filter, Does.Not.Contain("parent_id"));
    }
}
