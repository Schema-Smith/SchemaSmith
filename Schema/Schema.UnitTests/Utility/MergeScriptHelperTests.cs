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
        var rows = Newtonsoft.Json.Linq.JArray.Parse(BuildPayload(rowCount));
        var columns = new List<MergeScriptHelper.MySqlColumnInfo>
        {
            new() { Name = "Id", DataType = "int" }
        };
        var sql = MergeScriptHelper.BuildChunkedMergeMySql("db", "t", "`Id`, `Name`", "jt.`Id`, jt.`Name`",
            "(SELECT 1) AS jt", "`Id`, `Name`", "`Id`", rows, columns,
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

    [Test]
    public void BuildChunkedMergeMySql_WithoutDelete_EmitsNoKeyTable()
    {
        var noDeleteChunks = 2;
        var rows = Newtonsoft.Json.Linq.JArray.Parse(BuildPayload(MergeScriptHelper.MariaDbShredChunkRows * noDeleteChunks));
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
    // Asserted at the outcome per Rule 32: whether the probe runs at all, and which aggregation the built
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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id],[Name]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [Name] NVARCHAR(100)",
            insertCols: "        [Id],\r\n        [Name]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id],[Name]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [Name] NVARCHAR(100)",
            insertCols: "        [Id],\r\n        [Name]",
            updateCols: "[Name]");

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id],[Name]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [Name] NVARCHAR(100)",
            insertCols: "        [Id],\r\n        [Name]",
            updateCols: "[Name]");

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id],[Name]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [Name] NVARCHAR(100)",
            insertCols: "        [Id],\r\n        [Name]",
            updateCols: "[Name]");

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: true, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: "Target.[Type] = 'Active'");

        Assert.That(result, Does.Contain("AND (Target.[Type] = 'Active')"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_WithDisableTriggers_IncludesTriggerStatements()
    {
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: true,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id],[Name]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [Name] NVARCHAR(100)",
            insertCols: "        [Id],\r\n        [Name]",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Name\":\"O'Brien\"}]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("O''Brien"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_NullableKeyColumn_GeneratesIsNullComparison()
    {
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "*[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("IS NULL AND Target.[Id] IS NULL"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_TrimsBracketsFromSchemaAndTable()
    {
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[LookupID],[Code]",
            needsIdentity: false,
            jsonColDefs: "           [LookupID] INT,\r\n           [Code] NVARCHAR(32)",
            insertCols: "        [LookupID],\r\n        [Code]",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",(elem ->> 'name')::varchar(100) AS \"name\"",
            insertCols: "        \"id\",\r\n        \"name\"",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[{\"id\":1}]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("MERGE INTO"));
        Assert.That(result, Does.Contain("\"public\".\"test_table\""));
        Assert.That(result, Does.Contain("JSON_ARRAY_ELEMENTS"));
        Assert.That(result, Does.Contain("WHEN NOT MATCHED"));
        Assert.That(result, Does.Contain("INSERT"));
        Assert.That(result, Does.Contain("DO $$"));
        Assert.That(result, Does.Contain("END $$ LANGUAGE plpgsql"));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_InsertUpdate_ContainsMatchedClause()
    {
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",(elem ->> 'name')::varchar(100) AS \"name\"",
            insertCols: "        \"id\",\r\n        \"name\"",
            updateCols: "\"name\"");

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: "\"id\"");

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'Id')::int4 AS \"Id\"",
            insertCols: "        \"Id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'Id')::int4 AS \"Id\"",
            insertCols: "        \"Id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'Id')::int4 AS \"Id\"",
            insertCols: "        \"Id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'Id')::int4 AS \"Id\"",
            insertCols: "        \"Id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: "id=public.test_table_id_seq=SYSTEM",
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null);

        Assert.That(result, Does.Contain("{{public.test_table.tabledata}}"));
    }

    [Test]
    public void BuildMergeScript_PostgreSql_WithTokenize_ContentFileTokenParameter_IsEmbeddedVerbatim()
    {
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlXmlMockCommand(
            unsupportedComments: null, identAndSeq: null,
            updateCols: "\"Name\"", insertCols: "        \"Id\",\r\n        \"Name\"");

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
        var cmd = CreatePostgreSqlXmlMockCommand(
            unsupportedComments: null, identAndSeq: null,
            updateCols: "\"Name\"", insertCols: "        \"Id\",\r\n        \"Name\"");

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
        var cmd = CreatePostgreSqlXmlMockCommand(
            unsupportedComments: null, identAndSeq: null,
            updateCols: null, insertCols: "        \"Id\",\r\n        \"Geom\"",
            metadataColumns:
            [
                ("Id", "integer", "int4", "pg_catalog", null, false),
                ("Geom", "USER-DEFINED", "geometry", "public", null, true)
            ]);

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
        var cmd = CreatePostgreSqlXmlMockCommand(
            unsupportedComments: null, identAndSeq: null,
            updateCols: null, insertCols: "        \"Id\",\r\n        \"Data\"",
            metadataColumns:
            [
                ("Id", "integer", "int4", "pg_catalog", null, false),
                ("Data", "bytea", "bytea", "pg_catalog", null, true)
            ]);

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
    public void BuildMergeScript_PostgreSql_XmlEncoding_ArrayColumn_UsesStringToArrayWithSameDelimiter()
    {
        var cmd = CreatePostgreSqlXmlMockCommand(
            unsupportedComments: null, identAndSeq: null,
            updateCols: null, insertCols: "        \"Id\",\r\n        \"Tags\"",
            metadataColumns:
            [
                ("Id", "integer", "int4", "pg_catalog", null, false),
                ("Tags", "ARRAY", "_int4", "pg_catalog", null, true)
            ]);

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
                Does.Contain("STRING_TO_ARRAY(\"x\".\"Tags\", '*,*', '*NULL_VALUE_REPRESENTATION*')::_int4"),
                "Same function, delimiter, and NULL sentinel GetJsonColumnDefinitionsPostgreSql applies to the JSON row source.");
        });
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
        Assert.That(result, Does.Contain("`vc_col` VARCHAR(100) PATH '$.vc_col'"));
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
        Assert.That(result, Does.Contain("`data` TEXT PATH '$.data'"));
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
                cmd = CreateSqlServerXmlMockCommand(
                    unsupportedComments: null,
                    insertCols: "        [id],\n        [name]");
                schemaOrDb = "dbo";
                keyColumns = "[id]";
                break;
            case Platform.PostgreSQL:
                cmd = CreatePostgreSqlXmlMockCommand(
                    unsupportedComments: null, identAndSeq: null, updateCols: null,
                    insertCols: "        \"id\",\n        \"name\"");
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
        var sqlCmd = CreateSqlServerMockCommand("[Id]", false, "           [Id] INT", "        [Id]", null);
        var sqlResult = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, sqlCmd,
            "dbo", "T", "[{\"v\":\"it's\"}]", "[Id]", false, false, false, false, null);
        Assert.That(sqlResult, Does.Contain("it''s"));

        // PostgreSQL
        var pgCmd = CreatePostgreSqlMockCommand(null, "(elem ->> 'id')::int4 AS \"id\"", "        \"id\"", null);
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
        var cmd = Substitute.For<IDbCommand>();

        // Set up ExecuteReader to return rule names on first call (rules query),
        // then empty on subsequent calls
        var readerCallCount = 0;
        var ruleNames = new[] { "rule_insert_p1", "rule_insert_p2" };
        var ruleIndex = -1;

        cmd.ExecuteReader().Returns(ci =>
        {
            readerCallCount++;
            var reader = Substitute.For<IDataReader>();
            if (readerCallCount == 1)
            {
                // Rules query — return rule names
                reader.Read().Returns(_ => { ruleIndex++; return ruleIndex < ruleNames.Length; });
                reader.GetString(0).Returns(_ => ruleNames[ruleIndex]);
            }
            else
            {
                // Subsequent readers — return no rows
                reader.Read().Returns(false);
            }
            return reader;
        });

        // Set up ExecuteScalar sequence for PostgreSQL merge helper
        var scalarSequence = new object[]
        {
            null,  // GetUnsupportedColumnComments
            null,  // GetIdentityColumnAndSequence
            "(elem ->> 'id')::int4 AS \"id\"", // GetJsonColumnDefinitions
            "        \"id\""  // GetInsertColumns
        };
        var scalarIndex = 0;
        cmd.ExecuteScalar().Returns(_ => scalarIndex < scalarSequence.Length ? scalarSequence[scalarIndex++] : null);

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
        Assert.That(result.IndexOf("DISABLE RULE"), Is.LessThan(result.IndexOf("DO $$")));
        // Enable statements should be outside the DO $$ block (after it)
        Assert.That(result.IndexOf("ENABLE RULE"), Is.GreaterThan(result.IndexOf("END $$ LANGUAGE plpgsql")));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithUpdateDescendents_OmitsOnlyKeyword()
    {
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            updateDescendents: false);

        Assert.That(result, Does.Contain("ONLY "));
    }

    [TestCase("geometry", true)]
    [TestCase("geography", true)]
    [TestCase("point", true)]
    [TestCase("linestring", true)]
    [TestCase("polygon", true)]
    [TestCase("multipoint", true)]
    [TestCase("multilinestring", true)]
    [TestCase("multipolygon", true)]
    [TestCase("geometrycollection", true)]
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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",ST_GeomFromText(elem ->> 'location') AS \"location\"",
            insertCols: "        \"id\",\r\n        \"location\"",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("ST_GeomFromText(elem ->> 'location') AS \"location\""));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_ByteaType_UsesDecodeBase64()
    {
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",decode(elem ->> 'photo', 'base64') AS \"photo\"",
            insertCols: "        \"id\",\r\n        \"photo\"",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("decode(elem ->> 'photo', 'base64') AS \"photo\""));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_WithGeometryUpdate_UsesStandardEquality()
    {
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",ST_GeomFromText(elem ->> 'location') AS \"location\"",
            insertCols: "        \"id\",\r\n        \"location\"",
            updateCols: "\"location\"");

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",(elem ->> 'demographics')::xml AS \"demographics\"",
            insertCols: "        \"id\",\r\n        \"demographics\"",
            updateCols: "\"demographics\"",
            xmlCols: "demographics");

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",(elem ->> 'metadata')::json AS \"metadata\"",
            insertCols: "        \"id\",\r\n        \"metadata\"",
            updateCols: "\"metadata\"",
            jsonTypeCols: new Dictionary<string, string> { { "metadata", "json" } });

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",(elem ->> 'settings')::jsonb AS \"settings\"",
            insertCols: "        \"id\",\r\n        \"settings\"",
            updateCols: "\"settings\"",
            jsonTypeCols: new Dictionary<string, string> { { "settings", "jsonb" } });

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",(elem ->> 'name')::varchar(100) AS \"name\"",
            insertCols: "        \"id\",\r\n        \"name\"",
            updateCols: "\"name\"",
            jsonTypeCols: new Dictionary<string, string>());

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
    public void BuildMergeScript_SqlServer_GeometryColumn_UsesGeometryPrefix()
    {
        // GEOMETRY columns get G[ prefix just like GEOGRAPHY
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "geometry::STGeomFromText([Shape], [Shape.STSrid]) AS [Shape],[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [Shape] NVARCHAR(4000), [Shape.STSrid] INT",
            insertCols: "        [Id],\r\n        [Shape]",
            updateCols: "G[Shape]");

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // G[ prefix triggers .ToString() comparison
        Assert.That(result, Does.Contain("Target.[Shape].ToString()) <> (Source.[Shape].ToString()"));
        // SET clause strips prefix
        Assert.That(result, Does.Contain("[Shape] = Source.[Shape]"));
    }

    [Test]
    public void BuildMergeScript_SqlServer_GeometrySelectColumns_UsesGeometryType()
    {
        // Verify the SQL query for JSON select uses geometry::STGeomFromText
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "geometry::STGeomFromText([Shape], [Shape.STSrid]) AS [Shape]",
            needsIdentity: false,
            jsonColDefs: "           [Shape] NVARCHAR(4000), [Shape.STSrid] INT",
            insertCols: "        [Shape]",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(result, Does.Contain("geometry::STGeomFromText([Shape], [Shape.STSrid]) AS [Shape]"));
    }

    [Test]
    public void GetJsonSelectColumnsSqlServer_QueryContainsGeometryAndGeography()
    {
        // Verify the SQL query text checks for both GEOGRAPHY and GEOMETRY
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Id]");

        // Use BuildMergeScript to trigger the SQL generation internally
        var cmd2 = CreateSqlServerMockCommand("[Id]", false, "           [Id] INT", "        [Id]", null);
        MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd2,
            "dbo", "TestTable", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // Inspect the generated SQL queries. The first call sets up GetJsonSelectColumns
        // which should contain IN ('GEOGRAPHY', 'GEOMETRY')
        var commandTexts = cmd2.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "set_CommandText")
            .Select(c => c.GetArguments()[0]?.ToString())
            .Where(t => t == null || !t.Contains("compatibility_level")) // B1: drop the cliff-detect probe
            .ToList();

        // Second query is GetJsonSelectColumns (first is GetUnsupportedColumnComments) - should contain both types
        Assert.That(commandTexts[1], Does.Contain("'GEOGRAPHY'"));
        Assert.That(commandTexts[1], Does.Contain("'GEOMETRY'"));
    }

    [Test]
    public void GetUpdateColumnsSqlServer_QueryContainsGeometryAndGeography()
    {
        var cmd = CreateSqlServerMockCommand("[Id]", false, "           [Id] INT", "        [Id]", "[Name]");
        MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        var commandTexts = cmd.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "set_CommandText")
            .Select(c => c.GetArguments()[0]?.ToString())
            .Where(t => t == null || !t.Contains("compatibility_level")) // B1: drop the cliff-detect probe
            .ToList();

        // Fifth query is GetUpdateColumns - should check for both GEOGRAPHY and GEOMETRY
        var updateQuery = commandTexts[4];
        Assert.That(updateQuery, Does.Contain("'GEOGRAPHY'"));
        Assert.That(updateQuery, Does.Contain("'GEOMETRY'"));
    }

    [Test]
    public void GetJsonColumnDefinitionsSqlServer_QueryContainsGeometryReplacement()
    {
        var cmd = CreateSqlServerMockCommand("[Id]", false, "           [Id] INT", "        [Id]", null);
        MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        var commandTexts = cmd.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "set_CommandText")
            .Select(c => c.GetArguments()[0]?.ToString())
            .Where(t => t == null || !t.Contains("compatibility_level")) // B1: drop the cliff-detect probe
            .ToList();

        // Fourth query is GetJsonColumnDefinitions - should contain GEOMETRY replacement
        var jsonColDefsQuery = commandTexts[3];
        Assert.That(jsonColDefsQuery, Does.Contain("'GEOMETRY'"));
        Assert.That(jsonColDefsQuery, Does.Contain("'GEOGRAPHY'"));
        // STSrid companion for both types
        Assert.That(jsonColDefsQuery, Does.Contain("IN ('GEOGRAPHY', 'GEOMETRY')"));
    }

    #endregion

    #region SQL Server DATETIMEOFFSET Support Tests

    [Test]
    public void BuildMergeScript_SqlServer_DateTimeOffsetColumn_UsesDPrefix()
    {
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id],[EventTime]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [EventTime] NVARCHAR(50)",
            insertCols: "        [Id],\r\n        [EventTime]",
            updateCols: "D[EventTime]");

        var result = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1}]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // D[ prefix triggers CAST to NVARCHAR(50) comparison
        Assert.That(result, Does.Contain("CAST(Target.[EventTime] AS NVARCHAR(50))) <> (CAST(Source.[EventTime] AS NVARCHAR(50))"));
        // SET clause strips prefix
        Assert.That(result, Does.Contain("[EventTime] = Source.[EventTime]"));
    }

    [Test]
    public void GetUpdateColumnsSqlServer_QueryContainsDateTimeOffset()
    {
        var cmd = CreateSqlServerMockCommand("[Id]", false, "           [Id] INT", "        [Id]", "[Name]");
        MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        var commandTexts = cmd.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "set_CommandText")
            .Select(c => c.GetArguments()[0]?.ToString())
            .Where(t => t == null || !t.Contains("compatibility_level")) // B1: drop the cliff-detect probe
            .ToList();

        var updateQuery = commandTexts[4];
        Assert.That(updateQuery, Does.Contain("'DATETIMEOFFSET'"));
        Assert.That(updateQuery, Does.Contain("'D'"));
    }

    [Test]
    public void GetJsonColumnDefinitionsSqlServer_QueryContainsDateTimeOffsetReplacement()
    {
        var cmd = CreateSqlServerMockCommand("[Id]", false, "           [Id] INT", "        [Id]", null);
        MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[]", "[Id]",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        var commandTexts = cmd.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "set_CommandText")
            .Select(c => c.GetArguments()[0]?.ToString())
            .Where(t => t == null || !t.Contains("compatibility_level")) // B1: drop the cliff-detect probe
            .ToList();

        var jsonColDefsQuery = commandTexts[3];
        Assert.That(jsonColDefsQuery, Does.Contain("'DATETIMEOFFSET'"));
        Assert.That(jsonColDefsQuery, Does.Contain("'NVARCHAR(50)'"));
    }

    #endregion

    #region Helper Methods - SQL Server Mock

    /// <summary>
    /// Creates a SQL Server mock command. Call order for BuildMergeScript:
    /// 1. GetUnsupportedColumnComments, 2. GetJsonSelectColumns, 3. NeedsIdentityInsert,
    /// 4. IdentityColumnInJsonKeys (only if NeedsIdentityInsert=true), 5. GetJsonColumnDefinitions,
    /// 6. GetUpdateColumns (only if mergeUpdate=true), 7. GetInsertColumns
    /// </summary>
    private static IDbCommand CreateSqlServerMockCommand(
        string jsonSelectCols, bool needsIdentity, string jsonColDefs,
        string insertCols, string updateCols, string unsupportedComments = null)
    {
        var cmd = Substitute.For<IDbCommand>();
        // Every STRING_AGG-based builder self-detects the cliff with an extra ExecuteScalar first;
        // 0 (not-below-cliff) selects the modern STRING_AGG path, matching this mock's assertions.
        // GetJsonSelectColumns and GetJsonColumnDefinitions probe too — they previously called
        // STRING_AGG unconditionally, which is what broke data delivery on SQL Server 2016 (#393).
        var sequence = new List<object>
        {
            0,                    // cliff-check for GetUnsupportedColumnComments
            unsupportedComments,  // 1. GetUnsupportedColumnComments
            0,                    // cliff-check for GetJsonSelectColumns
            jsonSelectCols,       // 2. GetJsonSelectColumns
            needsIdentity         // 3. NeedsIdentityInsert
        };
        if (needsIdentity)
            sequence.Add(true);   // 4. IdentityColumnInJsonKeysSqlServer (assume identity column is in jsonKeys for unit-test mocks)
        sequence.Add(0);          // cliff-check for GetJsonColumnDefinitions
        sequence.Add(jsonColDefs);// GetJsonColumnDefinitions
        if (updateCols != null)
        {
            sequence.Add(0);          // cliff-check for GetUpdateColumns
            sequence.Add(updateCols); // GetUpdateColumns (only if mergeUpdate)
        }
        sequence.Add(0);          // cliff-check for GetInsertColumns
        sequence.Add(insertCols); // GetInsertColumns

        var callCount = 0;
        cmd.ExecuteScalar().Returns(ci =>
        {
            var idx = callCount++;
            return idx < sequence.Count ? sequence[idx] : null;
        });
        return cmd;
    }

    /// <summary>
    /// SQL Server mock command for the XML row-source path (isXml=true). Unlike CreateSqlServerMockCommand,
    /// the XML path reads column metadata via GetColumnMetadataSqlServer (ExecuteReader) instead of the
    /// STRING_AGG-based GetJsonSelectColumnsSqlServer, so it needs its own ExecuteReader stub. ExecuteScalar
    /// order (mergeUpdate=false, needsIdentity=false, matching every call site here): 1. cliff-check for
    /// GetUnsupportedColumnComments, 2. GetUnsupportedColumnComments, 3. NeedsIdentityInsertSqlServer,
    /// 4. cliff-check for GetInsertColumns, 5. GetInsertColumns. ExecuteReader: GetColumnMetadataSqlServer
    /// (called once, from BuildXmlShredSelectColumnsSqlServer) returns a fixed Id int / Name varchar(100) shape.
    /// </summary>
    private static IDbCommand CreateSqlServerXmlMockCommand(string unsupportedComments, string insertCols)
    {
        var cmd = Substitute.For<IDbCommand>();
        var sequence = new List<object>
        {
            0,                    // cliff-check for GetUnsupportedColumnComments
            unsupportedComments,  // GetUnsupportedColumnComments
            false,                // NeedsIdentityInsertSqlServer
            0,                    // cliff-check for GetInsertColumns
            insertCols            // GetInsertColumns
        };

        var callCount = 0;
        cmd.ExecuteScalar().Returns(ci =>
        {
            var idx = callCount++;
            return idx < sequence.Count ? sequence[idx] : null;
        });

        cmd.ExecuteReader().Returns(ci => CreateSqlServerColumnMetadataReader());
        return cmd;
    }

    private static IDataReader CreateSqlServerColumnMetadataReader(
        (string Name, string DataType, string UserType, int? MaxLen, bool IsGeometry, bool IsBinary, bool IsXml)[] columns = null)
    {
        columns ??= new (string Name, string DataType, string UserType, int? MaxLen, bool IsGeometry, bool IsBinary, bool IsXml)[]
        {
            ("id", "int", "INT", null, false, false, false),
            ("name", "varchar", "VARCHAR", 100, false, false, false)
        };

        var reader = Substitute.For<IDataReader>();
        var idx = -1;
        reader.Read().Returns(ci => { idx++; return idx < columns.Length; });
        reader.GetString(0).Returns(ci => columns[idx].Name);
        reader.GetString(1).Returns(ci => columns[idx].DataType);
        reader.GetString(2).Returns(ci => columns[idx].UserType);
        reader.IsDBNull(3).Returns(ci => columns[idx].MaxLen is null);
        reader.GetValue(3).Returns(ci => (object)columns[idx].MaxLen ?? DBNull.Value);
        reader.IsDBNull(4).Returns(true);
        reader.GetValue(4).Returns(DBNull.Value);
        reader.IsDBNull(5).Returns(true);
        reader.GetValue(5).Returns(DBNull.Value);
        reader.IsDBNull(6).Returns(true);
        reader.GetValue(6).Returns(DBNull.Value);
        reader.GetString(7).Returns("YES");
        reader.GetValue(8).Returns(false);
        reader.GetValue(9).Returns(false);
        reader.GetValue(10).Returns(ci => columns[idx].IsGeometry);
        reader.GetValue(11).Returns(ci => columns[idx].IsBinary);
        reader.GetValue(12).Returns(ci => columns[idx].IsXml);
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

    /// <summary>
    /// Creates a PostgreSQL mock command. Call order for BuildMergeScript:
    /// 1. GetUnsupportedColumnComments, 2. GetIdentityColumnAndSequence, 3. GetJsonColumnDefinitions,
    /// 4. GetUpdateColumns (only if mergeUpdate=true), 5. GetXmlColumns (if mergeUpdate),
    /// 6. GetJsonColumnsPostgreSql (ExecuteReader, if mergeUpdate), 7. GetInsertColumns
    /// </summary>
    private static IDbCommand CreatePostgreSqlMockCommand(
        string identAndSeq, string jsonColDefs, string insertCols, string updateCols,
        string xmlCols = null, Dictionary<string, string> jsonTypeCols = null,
        string unsupportedComments = null)
    {
        var cmd = Substitute.For<IDbCommand>();
        var sequence = new List<object>
        {
            unsupportedComments, // 1. GetUnsupportedColumnComments
            identAndSeq,        // 2. GetIdentityColumnAndSequence
            jsonColDefs          // 3. GetJsonColumnDefinitions
        };
        if (updateCols != null)
        {
            sequence.Add(updateCols); // 4. GetUpdateColumns (only if mergeUpdate)
            sequence.Add(xmlCols);    // 5. GetXmlColumns (only if mergeUpdate)
            // 6. GetJsonColumnsPostgreSql uses ExecuteReader (handled below)
        }
        sequence.Add(insertCols);     // GetInsertColumns

        var callCount = 0;
        cmd.ExecuteScalar().Returns(ci =>
        {
            var idx = callCount++;
            return idx < sequence.Count ? sequence[idx] : null;
        });

        // Set up ExecuteReader for GetJsonColumnsPostgreSql (only called when mergeUpdate)
        var jsonTypeEntries = jsonTypeCols ?? new Dictionary<string, string>();
        var jsonTypeList = jsonTypeEntries.ToList();
        var readerIndex = -1;
        var reader = Substitute.For<IDataReader>();
        reader.Read().Returns(ci =>
        {
            readerIndex++;
            return readerIndex < jsonTypeList.Count;
        });
        reader.GetString(0).Returns(ci => jsonTypeList[readerIndex].Key);
        reader.GetString(1).Returns(ci => jsonTypeList[readerIndex].Value);
        cmd.ExecuteReader().Returns(reader);

        return cmd;
    }

    /// <summary>
    /// PostgreSQL mock command for the XML row-source path. Unlike CreatePostgreSqlMockCommand, the XML
    /// path reads column metadata via GetColumnMetadataPostgreSql (ExecuteReader) instead of the
    /// STRING_AGG-based jsonColumns query, so ExecuteReader is routed by CommandText rather than fed a
    /// single fixed reader. ExecuteScalar order: 1. GetUnsupportedColumnComments,
    /// 2. GetIdentityColumnAndSequence, 3. GetUpdateColumns, 4. GetXmlColumns (both mergeUpdate-only),
    /// 5. GetInsertColumns. ExecuteReader: GetColumnMetadataPostgreSql (its pg_attribute/attidentity join
    /// distinguishes it) returns metadataColumns (defaults to a fixed Id int4 / Name varchar(100) shape —
    /// see CreatePostgreSqlColumnMetadataReader); GetJsonColumnsPostgreSql (mergeUpdate-only) returns no
    /// rows (no json/jsonb-typed target columns).
    /// </summary>
    private static IDbCommand CreatePostgreSqlXmlMockCommand(
        string unsupportedComments, string identAndSeq, string updateCols, string insertCols,
        (string Name, string DataType, string UdtName, string UdtSchema, int? MaxLen, bool Nullable)[] metadataColumns = null)
    {
        var cmd = Substitute.For<IDbCommand>();
        var sequence = new List<object> { unsupportedComments, identAndSeq };
        if (updateCols != null)
        {
            sequence.Add(updateCols);
            sequence.Add(null); // GetXmlColumnsPostgreSql: no xml-typed target columns
        }
        sequence.Add(insertCols);

        var callCount = 0;
        cmd.ExecuteScalar().Returns(ci =>
        {
            var idx = callCount++;
            return idx < sequence.Count ? sequence[idx] : null;
        });

        cmd.ExecuteReader().Returns(ci =>
            cmd.CommandText != null && cmd.CommandText.Contains("attidentity")
                ? CreatePostgreSqlColumnMetadataReader(metadataColumns)
                : CreateEmptyMockReader());

        return cmd;
    }

    private static IDataReader CreatePostgreSqlColumnMetadataReader(
        (string Name, string DataType, string UdtName, string UdtSchema, int? MaxLen, bool Nullable)[] columns = null)
    {
        columns ??= new (string Name, string DataType, string UdtName, string UdtSchema, int? MaxLen, bool Nullable)[]
        {
            ("Id", "integer", "int4", "pg_catalog", null, false),
            ("Name", "character varying", "varchar", "pg_catalog", 100, true)
        };

        var reader = Substitute.For<IDataReader>();
        var idx = -1;
        reader.Read().Returns(ci => { idx++; return idx < columns.Length; });
        reader.GetString(0).Returns(ci => columns[idx].Name);
        reader.GetString(1).Returns(ci => columns[idx].DataType);
        reader.GetString(2).Returns(ci => columns[idx].UdtName);
        reader.GetString(3).Returns(ci => columns[idx].UdtSchema);
        reader.IsDBNull(4).Returns(ci => columns[idx].MaxLen is null);
        reader.GetValue(4).Returns(ci => (object)columns[idx].MaxLen ?? DBNull.Value);
        reader.IsDBNull(5).Returns(true);
        reader.GetValue(5).Returns(DBNull.Value);
        reader.IsDBNull(6).Returns(true);
        reader.GetValue(6).Returns(DBNull.Value);
        reader.IsDBNull(7).Returns(true);
        reader.GetValue(7).Returns(DBNull.Value);
        reader.GetString(8).Returns(ci => columns[idx].Nullable ? "YES" : "NO");
        reader.GetValue(9).Returns(false);
        reader.GetValue(10).Returns(false);
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
    public void BuildMergeScript_SqlServer_QueriesContainUnsupportedTypeFilter()
    {
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id],[Name]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT,\r\n           [Name] NVARCHAR(100)",
            insertCols: "        [Id],\r\n        [Name]",
            updateCols: "[Name]");

        MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd,
            "dbo", "TestTable", "[{\"Id\":1,\"Name\":\"test\"}]", "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        // Verify that the unsupported type filter was included in queries
        var allQueries = cmd.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "set_CommandText")
            .Select(c => c.GetArguments()[0]?.ToString())
            .Where(t => t == null || !t.Contains("compatibility_level")) // B1: drop the cliff-detect probe
            .ToList();

        // GetJsonSelectColumns, GetJsonColumnDefinitions, GetInsertColumns, GetUpdateColumns should all have the filter
        var filteredQueries = allQueries.Where(q => q != null && q.Contains("sql_variant")).ToList();
        Assert.That(filteredQueries.Count, Is.GreaterThanOrEqualTo(4),
            "Expected at least 4 queries (select, json defs, insert, update) to include sql_variant filter");

        // Also check that timestamp and rowversion are excluded
        var timestampQueries = allQueries.Where(q => q != null && q.Contains("'timestamp'")).ToList();
        Assert.That(timestampQueries.Count, Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void BuildMergeScript_PostgreSQL_QueriesContainUnsupportedTypeFilter()
    {
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\",(elem ->> 'name')::varchar(100) AS \"name\"",
            insertCols: "        \"id\",\r\n        \"name\"",
            updateCols: "\"name\"");

        MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[{\"id\":1,\"name\":\"test\"}]", "\"id\"",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        var allQueries = cmd.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "set_CommandText")
            .Select(c => c.GetArguments()[0]?.ToString())
            .Where(t => t == null || !t.Contains("compatibility_level")) // B1: drop the cliff-detect probe
            .ToList();

        // GetJsonColumnDefinitions, GetInsertColumns, GetUpdateColumns should all have the filter
        var filteredQueries = allQueries.Where(q => q != null && q.Contains("tsvector")).ToList();
        Assert.That(filteredQueries.Count, Is.GreaterThanOrEqualTo(3),
            "Expected at least 3 queries (json defs, insert, update) to include tsvector filter");

        // Check that money, box, circle, line, lseg, path are also excluded
        var moneyQueries = allQueries.Where(q => q != null && q.Contains("'money'")).ToList();
        Assert.That(moneyQueries.Count, Is.GreaterThanOrEqualTo(3));

        // Check composite type exclusion
        var compositeQueries = allQueries.Where(q => q != null && q.Contains("t.typtype = 'c'")).ToList();
        Assert.That(compositeQueries.Count, Is.GreaterThanOrEqualTo(3));
    }

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
    public void GetUnsupportedColumnComments_SqlServer_ReturnsCommentLines()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("-- Column [VariantCol] skipped: sql_variant is not supported for data delivery\r\n-- Column [VersionCol] skipped: rowversion is not supported for data delivery");

        var result = MergeScriptHelper.GetUnsupportedColumnComments(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.That(result, Does.Contain("-- Column [VariantCol] skipped: sql_variant is not supported for data delivery"));
        Assert.That(result, Does.Contain("-- Column [VersionCol] skipped: rowversion is not supported for data delivery"));
    }

    [Test]
    public void GetUnsupportedColumnComments_SqlServer_NoUnsupportedColumns_ReturnsEmpty()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(null);

        var result = MergeScriptHelper.GetUnsupportedColumnComments(Platform.SqlServer, cmd, "dbo", "TestTable");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetUnsupportedColumnComments_PostgreSQL_ReturnsCommentLines()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("-- Column \"search_vector\" skipped: tsvector is not supported for data delivery\n-- Column \"price\" skipped: money is not supported for data delivery");

        var result = MergeScriptHelper.GetUnsupportedColumnComments(Platform.PostgreSQL, cmd, "public", "test_table");

        Assert.That(result, Does.Contain("tsvector is not supported for data delivery"));
        Assert.That(result, Does.Contain("money is not supported for data delivery"));
    }

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
        var comments = "-- Column [VariantCol] skipped: sql_variant is not supported for data delivery";
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null,
            unsupportedComments: comments);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null,
            unsupportedComments: null);

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
        var comments = "-- Column \"search_vector\" skipped: tsvector is not supported for data delivery";
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null,
            unsupportedComments: comments);

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
        Assert.That(result, Does.Contain("`name` VARCHAR(100) PATH '$.name'"));
        Assert.That(result, Does.Contain("`location` TEXT PATH '$.location'"));
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

    [Test]
    public void GetUpdateColumns_MySQL_MarksJsonColumnsWithPrefix()
    {
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10, 0, null, "int", "", null),
            new("metadata", "json", null, null, null, null, "json", "", null),
        });

        var result = MergeScriptHelper.GetUpdateColumns(Platform.MySQL, cmd, "testdb", "test_table");

        Assert.That(result, Does.Contain("`id`"));
        Assert.That(result, Does.Contain("J[`metadata`]"));
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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: true,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreateSqlServerMockCommand(
            jsonSelectCols: "[Id]",
            needsIdentity: false,
            jsonColDefs: "           [Id] INT",
            insertCols: "        [Id]",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: "id=tenant_seed.customers_id_seq=SYSTEM",
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: "id=tenant_seed.customers_id_seq=SYSTEM",
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

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
        var cmd = CreatePostgreSqlMockCommand(
            identAndSeq: null,
            jsonColDefs: "(elem ->> 'id')::int4 AS \"id\"",
            insertCols: "        \"id\"",
            updateCols: null);

        var result = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
            "public", "test_table", "[]", "\"id\"",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: true, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: null);

        Assert.That(result, Does.Contain("\"public\".\"test_table\""));
        Assert.That(result, Does.Contain("{{public.test_table.tabledata}}"));
    }

    [Test]
    public void BuildMergeScript_MySQL_DestSchemaOverride_IgnoredOrThrows()
    {
        // MySQL has no schema-template fan-out (no schema-inside-database concept), so the
        // override is meaningless on MySQL. Two acceptable behaviors:
        //   (a) silently ignore the override (the caller upstream is supposed to gate this
        //       on platform, but defense in depth never hurts), or
        //   (b) throw an explicit ArgumentException.
        // We pick (a) for symmetry with MergeScriptHelper's existing tolerance of edge
        // platform combinations, and to keep the call sites simple. The destination ref
        // stays on the source database name on MySQL.
        var cmd = CreateMySqlMockCommand(new MySqlColumnDef[]
        {
            new("id", "int", null, 10L, 0L, null, "int", "", null)
        });

        var result = MergeScriptHelper.BuildMergeScript(Platform.MySQL, cmd,
            "testdb", "testtable", "[]", "`id`",
            mergeUpdate: false, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null,
            disableRules: false, updateDescendents: true,
            destSchemaOverride: "{{SchemaName}}");

        Assert.That(result, Does.Contain("`testdb`.`testtable`"),
            "MySQL must keep using the source database name; the override is a no-op.");
        Assert.That(result, Does.Not.Contain("{{SchemaName}}"),
            "MySQL output must not leak the engine token into the script body.");
    }

    #endregion

}
