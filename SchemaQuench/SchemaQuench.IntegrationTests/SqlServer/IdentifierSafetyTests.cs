// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Identifier safety (#603 and its siblings): every name holds every character SQL Server allows -- a quote, a percent
/// sign, the closing bracket, a dot, a backslash, a comma, spaces and a non-Latin letter -- and the deploy has to create,
/// report, preview, rename, drop, extract and redeploy them all under exactly that name. Each step asserts what a user
/// sees: the catalog, the change audit and the progress log, never a fragment of one line.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[NonParallelizable]
public class IdentifierSafetyTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    // P'r%o\d, written for the JSON the fixture puts it in.
    protected override string ProductName => "P'r%o\\\\d";

    private const string H = "a'b%c]d.e\\f,g h é";
    private const string Schema = H;
    private const string Table = H, Child = H + "2", Gone = H + "3";
    private const string Key = "k" + H, Col = H, ChildKey = "k" + H, ChildRef = H + "r";
    private static readonly string Long = Fill("L" + H, 128, endsWith: "\\");
    private const string Pk = H + "_pk", Ix = H + "_ix", Ck = H + "_ck", CkLike = H + "_like", Fk = H + "_fk", St = H + "_st";
    private static readonly string LongIx = Fill("I" + H, 128, endsWith: "x");
    private const string TableDescription = "has \\d+ and 'quote' and 100%";

    private static string Fill(string start, int length, string endsWith) => start + new string('x', length - start.Length - endsWith.Length) + endsWith;

    private static string B(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string N(string value) => "N'" + value.Replace("'", "''") + "'";
    private static string J(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // One dropped by the change set's removal of the table, so the drop path sees a hostile name too.
    [OneTimeSetUp]
    public void StartClean() => DropEverything();

    [OneTimeTearDown]
    public void DropTheHostileSchema() => DropEverything();

    private void DropEverything() => Exec($"""
        DECLARE @s SYSNAME = {N(Schema)}, @sql NVARCHAR(MAX) = N'';
        SELECT @sql += N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(f.name) + N';'
          FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id = f.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @s;
        SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
          FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @s;
        EXEC (@sql);
        DECLARE @q NVARCHAR(300) = QUOTENAME(@s);
        IF SCHEMA_ID(@s) IS NOT NULL EXEC (N'DROP SCHEMA ' + @q);
        EXEC (N'CREATE SCHEMA ' + @q);
        """);

    private string ParentJson(string col = Col, string colOldName = null, string ix = Ix, string ixOldName = null, bool withCheck = true, string longType = "INT") => $$"""
        { "Schema": "{{J(B(Schema))}}", "Name": "{{J(B(Table))}}", "Description": "{{J(TableDescription)}}",
          "Columns": [ { "Name": "{{J(B(Key))}}", "DataType": "INT", "Nullable": false },
                       { "Name": "{{J(B(col))}}", "DataType": "INT", "Nullable": true, "Description": "{{J("col \\d+ 'x'")}}"{{(colOldName == null ? "" : $", \"OldName\": \"{J(B(colOldName))}\"")}} },
                       { "Name": "{{J(B(Long))}}", "DataType": "{{longType}}", "Nullable": true } ],
          "Indexes": [ { "Name": "{{J(B(Pk))}}", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "{{J(B(Key))}}" },
                       { "Name": "{{J(B(ix))}}", "IndexColumns": "{{J(B(col))}}"{{(ixOldName == null ? "" : $", \"OldName\": \"{J(B(ixOldName))}\"")}} },
                       { "Name": "{{J(B(LongIx))}}", "IndexColumns": "{{J(B(Long))}}" } ],
          "CheckConstraints": [ {{(withCheck ? $"{{ \"Name\": \"{J(B(Ck))}\", \"Expression\": \"{J(B(col) + " > 0")}\" }}, " : "")}}
                                { "Name": "{{J(B(CkLike))}}", "Expression": "{{J("N'a''b' LIKE N'%x%'")}}" } ],
          "Statistics": [ { "Name": "{{J(B(St))}}", "Columns": "{{J(B(col))}}" } ] }
        """;

    private static string ChildJson(string name = Child, string oldName = null, bool withForeignKey = true) => $$"""
        { "Schema": "{{J(B(Schema))}}", "Name": "{{J(B(name))}}"{{(oldName == null ? "" : $", \"OldName\": \"{J(B(oldName))}\"")}},
          "Columns": [ { "Name": "{{J(B(ChildKey))}}", "DataType": "INT", "Nullable": false },
                       { "Name": "{{J(B(ChildRef))}}", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "{{J(B(name + "_pk"))}}", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "{{J(B(ChildKey))}}" } ]
          {{(withForeignKey ? $", \"ForeignKeys\": [ {{ \"Name\": \"{J(B(Fk))}\", \"Columns\": \"{J(B(ChildRef))}\", \"RelatedTableSchema\": \"{J(B(Schema))}\", \"RelatedTable\": \"{J(B(Table))}\", \"RelatedColumns\": \"{J(B(Key))}\" }} ]" : "")}} }
        """;

    private static string GoneJson() => $$"""
        { "Schema": "{{J(B(Schema))}}", "Name": "{{J(B(Gone))}}",
          "Columns": [ { "Name": "{{J(B(Key))}}", "DataType": "INT", "Nullable": false } ],
          "Indexes": [ { "Name": "{{J(B(Gone + "_pk"))}}", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "{{J(B(Key))}}" } ] }
        """;

    private void WriteBase()
    {
        WritePackage("");
        WriteFile("Tables", "parent.json", ParentJson());
        WriteFile("Tables", "child.json", ChildJson());
        WriteFile("Tables", "gone.json", GoneJson());
    }

    // Renames of a column and a table (SQL Server renames no index: a new name is a new index); a check and a foreign key
    // dropped; a type changed; a table removed.
    private void WriteChangeSet()
    {
        WritePackage("");
        WriteFile("Tables", "parent.json", ParentJson(col: Col + "n", colOldName: Col, ix: Ix + "n", withCheck: false, longType: "BIGINT"));
        WriteFile("Tables", "child.json", ChildJson(Child + "n", oldName: Child, withForeignKey: false));
    }

    private static readonly Dictionary<string, string> AllowTheChanges = new() { ["Target:DestructiveChangePolicy"] = "allow" };

    private bool Exists(string sql) => Count(sql) > 0;

    private string InSchema => $"SCHEMA_ID({N(Schema)})";

    private bool TableExists(string table) => Exists($"SELECT COUNT(*) FROM sys.tables WHERE schema_id = {InSchema} AND name = {N(table)}");

    private bool ColumnExists(string table, string column) =>
        Exists($"SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.schema_id = {InSchema} AND t.name = {N(table)} AND c.name = {N(column)}");

    private bool IndexExists(string table, string index) =>
        Exists($"SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.schema_id = {InSchema} AND t.name = {N(table)} AND i.name = {N(index)}");

    private bool ObjectNamed(string name, string type) =>
        Exists($"SELECT COUNT(*) FROM sys.objects WHERE schema_id = {InSchema} AND name = {N(name)} AND type = '{type}'");

    // Every object of the schema with when it last changed: a drop and re-create the audit does not mention moves it.
    private string Snapshot() => Scalar($"""
        SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), o.name + N'|' + CAST(o.object_id AS NVARCHAR(20)) + N'|' + CONVERT(NVARCHAR(30), o.modify_date, 126)), NCHAR(10))
                 WITHIN GROUP (ORDER BY o.name)
          FROM sys.objects o WHERE o.schema_id = {InSchema}
        """)?.ToString() + "\n" + Scalar($"""
        SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), t.name + N'.' + i.name + N'|' + CAST(i.index_id AS NVARCHAR(10))), NCHAR(10)) WITHIN GROUP (ORDER BY t.name, i.name)
          FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.schema_id = {InSchema} AND i.name IS NOT NULL
        """);

    private string Description(string table, string column = null) => Scalar(column == null
        ? $"SELECT CAST(value AS NVARCHAR(4000)) FROM sys.fn_listextendedproperty(N'MS_Description', N'SCHEMA', {N(Schema)}, N'TABLE', {N(table)}, NULL, NULL)"
        : $"SELECT CAST(value AS NVARCHAR(4000)) FROM sys.fn_listextendedproperty(N'MS_Description', N'SCHEMA', {N(Schema)}, N'TABLE', {N(table)}, N'COLUMN', {N(column)})")?.ToString();

    private static string[] Changes(DeployResult run) =>
        run.Audit.Where(r => r.Action is "created" or "modified" or "dropped" or "rebuilt").Select(r => $"{r.ObjectType} {r.ObjectName} {r.Action}").ToArray();

    // A progress line that names the table names it whole: a swallowed % or a doubled quote shows up here.
    private static string[] Mangled(DeployResult run) =>
        run.Log.Where(l => l != null && l.Contains("f,g h") && !l.Contains("a'b%c")).ToArray();

    private static string Flat(string message)
    {
        var flat = string.Join(" / ", message.Split((char)10).Select(l => l.Trim()).Where(l => l.Length > 0));
        return flat.Length > 1500 ? flat[..1500] : flat;
    }

    private static IEnumerable<Exception> Causes(Exception e)
    {
        for (; e != null; e = e.InnerException) yield return e;
    }

    private readonly List<string> _failures = new();
    private string _step;

    private void Expect(bool condition, string what)
    {
        if (!condition) _failures.Add($"{_step}: {what}");
    }

    private void ExpectEqual(string actual, string expected, string what)
    {
        if (actual != expected) _failures.Add($"{_step}: {what} -- expected [{expected}], was [{actual}]");
    }

    private void ExpectNone(IEnumerable<string> items, string what)
    {
        var list = items.ToList();
        if (list.Count > 0) _failures.Add($"{_step}: {what} -- {list.Count}: {string.Join(" | ", list.Take(5))}");
    }

    // One ordered chain, so the setup is paid once; a step that fails is recorded and the next still runs, so one red run
    // lists everything that is wrong.
    [Test]
    public void HostileNames_SurviveEveryStepOfADeploy()
    {
        foreach (var (name, step) in new (string, Action)[]
                 {
                     ("deploy", DeployCreatesEveryObjectUnderItsExactName), ("redeploy", RedeployingUnchangedChangesNothing),
                     ("extract", ExtractingAndRedeployingChangesNothing), ("preview", PreviewingAChangeSetChangesNothing),
                     ("apply", ApplyingTheChangeSetUsesTheExactNames), ("reapply", RedeployingTheChangeSetChangesNothing),
                 })
        {
            _step = name;
            try
            {
                step();
            }
            catch (Exception e)
            {
                _failures.Add($"{name}: threw {string.Join(" <- ", Causes(e).Select(c => $"{c.GetType().Name}: {Flat(c.Message)}"))}");
            }
        }
        if (_failures.Count > 0) Assert.Fail($"{_failures.Count} failure(s):" + string.Concat(_failures.Select(f => Environment.NewLine + "  - " + f)));
    }

    private void DeployCreatesEveryObjectUnderItsExactName()
    {
        WriteBase();
        var run = Deploy(settings: AllowTheChanges);
        Expect(TableExists(Table) && TableExists(Child) && TableExists(Gone), "the tables");
        Expect(ColumnExists(Table, Key) && ColumnExists(Table, Col) && ColumnExists(Table, Long), "the columns, the 128-character one included");
        Expect(IndexExists(Table, Pk) && IndexExists(Table, Ix) && IndexExists(Table, LongIx), "the indexes, the 128-character one included");
        Expect(ObjectNamed(Ck, "C") && ObjectNamed(CkLike, "C"), "the checks");
        Expect(ObjectNamed(Fk, "F"), "the foreign key");
        Expect(Exists($"SELECT COUNT(*) FROM sys.stats s JOIN sys.tables t ON t.object_id = s.object_id WHERE t.schema_id = {InSchema} AND t.name = {N(Table)} AND s.name = {N(St)}"),
            "the statistic");
        ExpectEqual(Description(Table), TableDescription, "the table's description");
        ExpectEqual(Description(Table, Col), "col \\d+ 'x'", "the column's description");
        ExpectNone(Mangled(run), "progress lines that name the table");
        ExpectNone(run.Audit.Where(r => r.Action == "created").Select(r => r.ObjectName).Where(n => n.Contains("f,g h") && !n.Contains("a'b%c")), "audit names");
    }

    private void RedeployingUnchangedChangesNothing()
    {
        WriteBase();
        var before = Snapshot();
        var run = Deploy(settings: AllowTheChanges);
        ExpectNone(Changes(run), "audited changes");
        ExpectEqual(Snapshot(), before, "every object as it was");
        ExpectNone(Mangled(run), "progress lines that name the table");
    }

    private void ExtractingAndRedeployingChangesNothing()
    {
        WriteBase();
        foreach (var (table, file) in new[] { (Table, "parent.json"), (Child, "child.json"), (Gone, "gone.json") })
        {
            var json = Scalar($"EXEC SchemaSmith.GenerateTableJSON @p_Schema = {N(Schema)}, @p_Table = {N(table)}")?.ToString();
            Expect(!string.IsNullOrEmpty(json), $"extraction of {table}");
            if (!string.IsNullOrEmpty(json)) File.WriteAllText(Path.Join(TemplateDir, "Tables", file), json);
        }
        var before = Snapshot();
        var run = Deploy(settings: AllowTheChanges);
        ExpectNone(Changes(run), "audited changes");
        ExpectEqual(Snapshot(), before, "every object as it was");
    }

    private void PreviewingAChangeSetChangesNothing()
    {
        WriteChangeSet();
        var before = Snapshot();
        var run = Deploy(whatIf: true, dropRemovedTables: true, settings: AllowTheChanges);
        ExpectEqual(Snapshot(), before, "nothing changed");
        Expect(run.Log.Any(l => l != null && l.Contains("N'a''b' LIKE N'%x%'", StringComparison.OrdinalIgnoreCase)), "the check prints unmangled");
        ExpectNone(run.Log.Where(l => l != null && l.Contains("%%")), "no doubled percent sign reaches the log");
    }

    private void ApplyingTheChangeSetUsesTheExactNames()
    {
        WriteChangeSet();
        var run = Deploy(dropRemovedTables: true, settings: AllowTheChanges);
        Expect(ColumnExists(Table, Col + "n") && !ColumnExists(Table, Col), "the column renamed");
        Expect(IndexExists(Table, Ix + "n") && !IndexExists(Table, Ix), "the index renamed");
        Expect(TableExists(Child + "n") && !TableExists(Child), "the table renamed");
        Expect(!TableExists(Gone), "the removed table dropped");
        Expect(!ObjectNamed(Ck, "C"), "the check dropped");
        Expect(!ObjectNamed(Fk, "F"), "the foreign key dropped");
        ExpectEqual(Scalar($"SELECT TYPE_NAME(c.system_type_id) FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.schema_id = {InSchema} AND t.name = {N(Table)} AND c.name = {N(Long)}")?.ToString(),
            "bigint", "the type changed");
        ExpectNone(Mangled(run), "progress lines that name the table");
    }

    private void RedeployingTheChangeSetChangesNothing()
    {
        WriteChangeSet();
        var before = Snapshot();
        var run = Deploy(dropRemovedTables: true, settings: AllowTheChanges);
        ExpectNone(Changes(run), "audited changes");
        ExpectEqual(Snapshot(), before, "every object as it was");
    }
}
