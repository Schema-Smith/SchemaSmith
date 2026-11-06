// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Identifier safety (#603 and its siblings) on MySQL and MariaDB. The schema is the database, so the names that hold every
/// character the engine allows are the tables', columns', indexes', keys' and checks' -- a quote, a percent sign, a
/// backtick, a dot, a backslash, a comma, spaces and a non-Latin letter, in lower case because the lower_case_table_names
/// legs fold upper case -- and one column and one index are 64 characters. Comments hold \d, a doubled backslash and a
/// line break: the MySQL scripts doubled quotes but not backslashes (A9), so such a comment came back changed. The same
/// ordered chain as the other engines', recording each step's failures.
/// </summary>
[NonParallelizable]
public abstract class IdentifierSafetyMySqlFamilySharedTests : ObjectScriptDeployFixture
{
    // P'r%o\d, written for the JSON the fixture puts it in.
    protected sealed override string ProductName => "P'r%o\\\\d";

    private const string H = "a'b%c`d.e\\f,g h é";
    // Every table starts with it, so the fixture finds its own in the shared database.
    private const string Prefix = "ids" + H;
    private const string Table = Prefix, Child = Prefix + "2", Gone = Prefix + "3";
    private const string Key = "k" + H, Col = H, Text = "t" + H, Defaulted = "d" + H, ChildKey = "k" + H, ChildRef = H + "r";
    private static readonly string Long = ("l" + H).PadRight(64, 'x');
    private const string Ix = H + "_ix", Ck = H + "_ck", CkLike = H + "_like", Fk = H + "_fk", Ft = H + "_ft", KeyIx = H + "_kx";
    private static readonly string LongIx = ("i" + H).PadRight(64, 'x');
    private const string TableComment = "has \\d+ and 'quote' and 100% and \\\\ and a\nbreak";
    private const string ColumnComment = "col \\d+ 'x'";
    private const string IndexComment = "ix \\d+ \\\\ 'y'";
    // The SQL literal for the value  x\d'q  : a backslash and a quote, each written escaped.
    private const string DefaultLiteral = "'x\\\\d''q'";

    private static string B(string name) => "`" + name.Replace("`", "``") + "`";
    private static string L(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    private static string J(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    [OneTimeSetUp]
    public void StartClean() => DropTheTables();

    [OneTimeTearDown]
    public void LeaveNothing() => DropTheTables();

    private void DropTheTables()
    {
        var tables = new List<string>();
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = {L(MainDb)} AND TABLE_NAME LIKE {L(Prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%")}";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        if (tables.Count > 0)
            Exec("SET FOREIGN_KEY_CHECKS = 0; " + string.Concat(tables.Select(t => $"DROP TABLE IF EXISTS {B(MainDb)}.{B(t)}; ")) + "SET FOREIGN_KEY_CHECKS = 1;");
    }

    private string ParentJson(string col = Col, string colOldName = null, string ix = Ix, bool withCheck = true, string longType = "INT",
        string likePattern = "%x%", bool withLongIndex = true, string keyIx = KeyIx) => $$"""
        { "Name": "{{J(Table)}}", "Comment": "{{J(TableComment)}}",
          "Columns": [ { "Name": "{{J(Key)}}", "DataType": "INT", "Nullable": false },
                       { "Name": "{{J(col)}}", "DataType": "INT", "Nullable": true, "Comment": "{{J(ColumnComment)}}"{{(colOldName == null ? "" : $", \"OldName\": \"{J(colOldName)}\"")}} },
                       { "Name": "{{J(Long)}}", "DataType": "{{longType}}", "Nullable": true },
                       { "Name": "{{J(Text)}}", "DataType": "TEXT", "Nullable": true },
                       { "Name": "{{J(Defaulted)}}", "DataType": "VARCHAR(40)", "Nullable": true, "Default": "{{J(DefaultLiteral)}}" } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{J(B(Key))}}" },
                       { "Name": "{{J(ix)}}", "IndexColumns": "{{J(B(col))}}", "Comment": "{{J(IndexComment)}}" },
                       { "Name": "{{J(keyIx)}}", "IndexColumns": "{{J(B(Key))}}" }{{(withLongIndex ? $", {{ \"Name\": \"{J(LongIx)}\", \"IndexColumns\": \"{J(B(Long))}\" }}" : "")}} ],
          "FullTextIndexes": [ { "Name": "{{J(Ft)}}", "Columns": "{{J(B(Text))}}", "Comment": "{{J(IndexComment)}}" } ],
          "CheckConstraints": [ {{(withCheck ? $"{{ \"Name\": \"{J(Ck)}\", \"Expression\": \"{J(B(Key) + " >= 0")}\" }}, " : "")}}
                                { "Name": "{{J(CkLike)}}", "Expression": "{{J($"'a''b' like '{likePattern}'")}}" } ] }
        """;

    private static string ChildJson(string name = Child, string oldName = null, bool withForeignKey = true) => $$"""
        { "Name": "{{J(name)}}"{{(oldName == null ? "" : $", \"OldName\": \"{J(oldName)}\"")}},
          "Columns": [ { "Name": "{{J(ChildKey)}}", "DataType": "INT", "Nullable": false },
                       { "Name": "{{J(ChildRef)}}", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{J(B(ChildKey))}}" } ]
          {{(withForeignKey ? $", \"ForeignKeys\": [ {{ \"Name\": \"{J(Fk)}\", \"Columns\": \"{J(B(ChildRef))}\", \"RelatedTable\": \"{J(Table)}\", \"RelatedColumns\": \"{J(B(Key))}\" }} ]" : "")}} }
        """;

    private static string GoneJson() => $$"""
        { "Name": "{{J(Gone)}}",
          "Columns": [ { "Name": "{{J(Key)}}", "DataType": "INT", "Nullable": false } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{J(B(Key))}}" } ] }
        """;

    private void WriteBase()
    {
        WritePackage(RunOnceFolders, "Before Scripts");
        WriteFile("Before Scripts", RunOnce, "SELECT 1");
        WriteFile("Tables", "parent.json", ParentJson());
        WriteFile("Tables", "child.json", ChildJson());
        WriteFile("Tables", "gone.json", GoneJson());
    }

    // Renames of a column, an index and a table; a check, a foreign key and an index dropped; a check rewritten; a type changed; a table removed.
    private void WriteChangeSet()
    {
        WritePackage(RunOnceFolders, "Before Scripts");
        WriteFile("Before Scripts", RunOnce, "SELECT 1");
        WriteFile("Tables", "parent.json", ParentJson(col: Col + "n", colOldName: Col, ix: Ix + "n", withCheck: false, longType: "BIGINT", likePattern: "%y%", withLongIndex: false, keyIx: KeyIx + "r"));
        WriteFile("Tables", "child.json", ChildJson(Child + "n", oldName: Child, withForeignKey: false));
    }

    // A run-once script: its completion record carries the product name and its path, so both must round-trip.
    private const string RunOnce = "o'n%c,e é Ω.sql";
    private const string RunOnceFolders = """{ "FolderPath": "Before Scripts", "QuenchSlot": "Before" }""";
    private string RunOnceRecord() => Text1("SELECT CONCAT(COUNT(*), '|', MIN(ProductName)) FROM SchemaSmith_CompletedMigrationScripts WHERE ScriptPath LIKE '%Ω%'");

    private static readonly Dictionary<string, string> AllowTheChanges = new() { ["Target:DestructiveChangePolicy"] = "allow" };

    private string InDb => $"TABLE_SCHEMA = {L(MainDb)}";

    private bool Exists(string sql) => Count(sql) > 0;

    private bool TableExists(string table) => Exists($"SELECT COUNT(*) FROM information_schema.TABLES WHERE {InDb} AND TABLE_NAME = {L(table)}");

    private bool ColumnExists(string table, string column) =>
        Exists($"SELECT COUNT(*) FROM information_schema.COLUMNS WHERE {InDb} AND TABLE_NAME = {L(table)} AND COLUMN_NAME = {L(column)}");

    private bool IndexExists(string table, string index) =>
        Exists($"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE {InDb} AND TABLE_NAME = {L(table)} AND INDEX_NAME = {L(index)}");

    private bool ConstraintExists(string table, string name, string type) =>
        Exists($"SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = {L(MainDb)} AND TABLE_NAME = {L(table)} AND CONSTRAINT_NAME = {L(name)} AND CONSTRAINT_TYPE = {L(type)}");

    private string Text1(string sql) => Scalar(sql)?.ToString();

    // The fixture's tables, columns and indexes with their comments, types and creation times: a rebuild or a comment
    // that came back changed moves it.
    private string Snapshot()
    {
        var parts = new List<string>();
        foreach (var sql in new[]
                 {
                     $"SELECT TABLE_NAME, CREATE_TIME, TABLE_COMMENT FROM information_schema.TABLES WHERE {InDb} AND TABLE_NAME LIKE 'ids%' ORDER BY TABLE_NAME",
                     $"SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, COLUMN_COMMENT FROM information_schema.COLUMNS WHERE {InDb} AND TABLE_NAME LIKE 'ids%' ORDER BY TABLE_NAME, COLUMN_NAME",
                     $"SELECT TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, INDEX_COMMENT FROM information_schema.STATISTICS WHERE {InDb} AND TABLE_NAME LIKE 'ids%' ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX",
                 })
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new StringBuilder();
                for (var i = 0; i < reader.FieldCount; i++) row.Append(reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString()).Append('|');
                parts.Add(row.ToString());
            }
        }
        return string.Join("\n", parts);
    }

    private string Extract(string table)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = $"CALL {B(MainDb)}.SchemaSmith_GenerateTableJSON({L(MainDb)}, {L(table)})";
        using var reader = command.ExecuteReader();
        var json = new StringBuilder();
        while (reader.Read()) json.Append(reader[0]);
        return json.ToString();
    }

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

    private void Record(string name, Exception e) =>
        _failures.Add($"{name}: threw {string.Join(" <- ", Causes(e).Select(c => $"{c.GetType().Name}: {Flat(c.Message)}"))}");

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
            catch (AssertionException e)
            {
                Record(name, e);
            }
            catch (DbException e)
            {
                Record(name, e);
            }
            catch (RunFailedException e)
            {
                Record(name, e);
            }
            catch (PackageException e)
            {
                Record(name, e);
            }
            catch (InvalidOperationException e)
            {
                Record(name, e);
            }
        }
        if (_failures.Count > 0) Assert.Fail($"{_failures.Count} failure(s):" + string.Concat(_failures.Select(f => Environment.NewLine + "  - " + f)));
    }

    private void DeployCreatesEveryObjectUnderItsExactName()
    {
        WriteBase();
        var run = Deploy(settings: AllowTheChanges);
        ExpectEqual(RunOnceRecord(), "1|" + ProductName.Replace(@"\\", @"\"), "the run-once script recorded under its product");
        Expect(TableExists(Table) && TableExists(Child) && TableExists(Gone), "the tables");
        Expect(ColumnExists(Table, Key) && ColumnExists(Table, Col) && ColumnExists(Table, Long), "the columns, the 64-character one included");
        Expect(IndexExists(Table, Ix) && IndexExists(Table, LongIx) && IndexExists(Table, Ft), "the indexes, the 64-character and FULLTEXT ones included");
        Expect(ConstraintExists(Table, Ck, "CHECK") && ConstraintExists(Table, CkLike, "CHECK"), "the checks");
        Expect(ConstraintExists(Child, Fk, "FOREIGN KEY"), "the foreign key");
        ExpectEqual(Text1($"SELECT TABLE_COMMENT FROM information_schema.TABLES WHERE {InDb} AND TABLE_NAME = {L(Table)}"), TableComment, "the table's comment");
        ExpectEqual(Text1($"SELECT COLUMN_COMMENT FROM information_schema.COLUMNS WHERE {InDb} AND TABLE_NAME = {L(Table)} AND COLUMN_NAME = {L(Col)}"), ColumnComment, "the column's comment");
        ExpectEqual(Text1($"SELECT INDEX_COMMENT FROM information_schema.STATISTICS WHERE {InDb} AND TABLE_NAME = {L(Table)} AND INDEX_NAME = {L(Ix)}"), IndexComment, "the index's comment");
        ExpectEqual(Text1($"SELECT INDEX_COMMENT FROM information_schema.STATISTICS WHERE {InDb} AND TABLE_NAME = {L(Table)} AND INDEX_NAME = {L(Ft)}"), IndexComment, "the FULLTEXT index's comment");
        // MySQL reports a string default as its value, MariaDB as the literal that produces it.
        ExpectEqual(Text1($"SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS WHERE {InDb} AND TABLE_NAME = {L(Table)} AND COLUMN_NAME = {L(Defaulted)}"),
            GetType().Namespace!.EndsWith("MariaDb") ? DefaultLiteral : @"x\d'q", "the column's default");
        ExpectNone(Mangled(run), "progress lines that name the table");
        ExpectNone(run.Audit.Where(r => r.Action == "created").Select(r => r.ObjectName).Where(n => n.Contains("f,g h") && !n.Contains("a'b%c")), "audit names");
    }

    private void RedeployingUnchangedChangesNothing()
    {
        WriteBase();
        var before = Snapshot();
        var run = Deploy(settings: AllowTheChanges);
        ExpectEqual(RunOnceRecord(), "1|" + ProductName.Replace(@"\\", @"\"), "the run-once script found, not run and recorded again");
        ExpectNone(Changes(run), "audited changes");
        ExpectEqual(Snapshot(), before, "every object as it was");
        ExpectNone(Mangled(run), "progress lines that name the table");
    }

    private void ExtractingAndRedeployingChangesNothing()
    {
        WriteBase();
        foreach (var (table, file) in new[] { (Table, "parent.json"), (Child, "child.json"), (Gone, "gone.json") })
        {
            var json = Extract(table);
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
        ExpectNone(Mangled(run), "progress lines that name the table");
    }

    private void ApplyingTheChangeSetUsesTheExactNames()
    {
        WriteChangeSet();
        var run = Deploy(dropRemovedTables: true, settings: AllowTheChanges);
        Expect(ColumnExists(Table, Col + "n") && !ColumnExists(Table, Col), "the column renamed");
        Expect(IndexExists(Table, Ix + "n") && !IndexExists(Table, Ix), "the index replaced");
        // Same columns, new name: renamed in place, which needs its ownership row found under a name holding a dot.
        Expect(IndexExists(Table, KeyIx + "r") && !IndexExists(Table, KeyIx), "the index renamed");
        ExpectNone(Changes(run).Where(c => c.Contains(KeyIx)), "the renamed index rebuilt");
        // Its table and name together run past 64 characters, the width of a single identifier.
        Expect(!IndexExists(Table, LongIx), "the index with the longest name dropped");
        Expect(TableExists(Child + "n") && !TableExists(Child), "the table renamed");
        Expect(!TableExists(Gone), "the removed table dropped");
        Expect(!ConstraintExists(Table, Ck, "CHECK"), "the check dropped");
        Expect(!ConstraintExists(Child + "n", Fk, "FOREIGN KEY"), "the foreign key dropped");
        Expect(Text1($"SELECT CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = {L(MainDb)} AND CONSTRAINT_NAME = {L(CkLike)}")?.Contains("%y%") == true,
            "the changed check re-created with its new pattern");
        ExpectEqual(Text1($"SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE {InDb} AND TABLE_NAME = {L(Table)} AND COLUMN_NAME = {L(Long)}"), "bigint", "the type changed");
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
