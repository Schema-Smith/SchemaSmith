// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Identifier safety (#603 and its siblings) on PostgreSQL: every name holds a quote, a percent sign, a double quote, a dot,
/// a backslash, a comma, spaces and a non-Latin letter, and one column is exactly 63 bytes, the most PostgreSQL keeps. The
/// deploy has to create, report, preview, rename, drop, extract and redeploy them under exactly that name. One ordered
/// chain records each step's failures and goes on, so one red run lists everything wrong.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[NonParallelizable]
public class IdentifierSafetyTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    // P'r%o\d, written for the JSON the fixture puts it in.
    protected override string ProductName => "P'r%o\\\\d";

    private const string H = "a'b%c\"d.e\\f,g h é";
    private const string Schema = H;
    private const string Table = H, Child = H + "2", Gone = H + "3";
    private const string Key = "k" + H, Col = H, ChildKey = "k" + H, ChildRef = H + "r";
    private static readonly string Long = Fill("L" + H, 63);
    private const string Pk = H + "_pk", Ix = H + "_ix", Ck = H + "_ck", CkLike = H + "_like", Fk = H + "_fk", St = H + "_st";
    private const string Enum = H + "_en", Domain = H + "_do", DomainCheck = H + "_dock", Sequence = H + "_sq", View = H + "_mv", ViewIndex = H + "_mvix";
    private const string TableDescription = "has \\d+ and 'quote' and 100%";

    // Exactly <paramref name="bytes"/> bytes of UTF-8: é is two.
    private static string Fill(string start, int bytes) => start + new string('x', bytes - Encoding.UTF8.GetByteCount(start));

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string L(string value) => "'" + value.Replace("'", "''") + "'";
    private static string J(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [OneTimeSetUp]
    public void StartClean() => DropEverything(recreate: true);

    [OneTimeTearDown]
    public void DropTheHostileSchema() => DropEverything(recreate: false);

    // Nothing is left behind: the suite shares the database, and another fixture must not meet these names.
    private void DropEverything(bool recreate) =>
        Exec($"DROP SCHEMA IF EXISTS {Q(Schema)} CASCADE;" + (recreate ? $" CREATE SCHEMA {Q(Schema)};" : ""));

    private string ParentJson(string col = Col, string colOldName = null, string ix = Ix, bool withCheck = true, string longType = "integer",
        string likePattern = "%x%") => $$"""
        { "Schema": "{{J(Schema)}}", "Name": "{{J(Table)}}", "Description": "{{J(TableDescription)}}",
          "Columns": [ { "Name": "{{J(Key)}}", "DataType": "integer", "Nullable": false },
                       { "Name": "{{J(col)}}", "DataType": "integer", "Nullable": true, "Description": "{{J("col \\d+ 'x'")}}"{{(colOldName == null ? "" : $", \"OldName\": \"{J(colOldName)}\"")}} },
                       { "Name": "{{J(Long)}}", "DataType": "{{longType}}", "Nullable": true },
                       { "Name": "{{J(Enum)}}c", "DataType": "{{J(Q(Schema) + "." + Q(Enum))}}", "Nullable": true },
                       { "Name": "{{J(Domain)}}c", "DataType": "{{J(Q(Schema) + "." + Q(Domain))}}", "Nullable": true } ],
          "Indexes": [ { "Name": "{{J(Pk)}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{J(Q(Key))}}" },
                       { "Name": "{{J(ix)}}", "IndexColumns": "{{J(Q(col))}}" },
                       { "Name": "{{J(Long)}}", "IndexColumns": "{{J(Q(Long))}}" } ],
          "CheckConstraints": [ {{(withCheck ? $"{{ \"Name\": \"{J(Ck)}\", \"Expression\": \"{J(Q(Key) + " >= 0")}\" }}, " : "")}}
                                { "Name": "{{J(CkLike)}}", "Expression": "{{J($"'a''b' LIKE '{likePattern}'")}}" } ],
          "Statistics": [ { "Name": "{{J(St)}}", "StatisticsColumns": "{{J(Q(col) + ", " + Q(Key))}}" } ] }
        """;

    private static string ChildJson(string name = Child, string oldName = null, bool withForeignKey = true) => $$"""
        { "Schema": "{{J(Schema)}}", "Name": "{{J(name)}}"{{(oldName == null ? "" : $", \"OldName\": \"{J(oldName)}\"")}},
          "Columns": [ { "Name": "{{J(ChildKey)}}", "DataType": "integer", "Nullable": false },
                       { "Name": "{{J(ChildRef)}}", "DataType": "integer", "Nullable": true } ],
          "Indexes": [ { "Name": "{{J(name + "_pk")}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{J(Q(ChildKey))}}" } ]
          {{(withForeignKey ? $", \"ForeignKeys\": [ {{ \"Name\": \"{J(Fk)}\", \"Columns\": \"{J(Q(ChildRef))}\", \"RelatedTableSchema\": \"{J(Schema)}\", \"RelatedTable\": \"{J(Table)}\", \"RelatedColumns\": \"{J(Q(Key))}\" }} ]" : "")}} }
        """;

    private static string GoneJson() => $$"""
        { "Schema": "{{J(Schema)}}", "Name": "{{J(Gone)}}",
          "Columns": [ { "Name": "{{J(Key)}}", "DataType": "integer", "Nullable": false } ],
          "Indexes": [ { "Name": "{{J(Gone + "_pk")}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{J(Q(Key))}}" } ] }
        """;

    private void WriteTypes()
    {
        WriteFile("Enum Types", "en.json", $$"""{ "Schema": "{{J(Schema)}}", "Name": "{{J(Enum)}}", "Values": [ "{{J(H)}}", "ok" ] }""");
        WriteFile("Domain Types", "do.json", $$"""
            { "Schema": "{{J(Schema)}}", "Name": "{{J(Domain)}}", "DataType": "integer",
              "CheckConstraints": [ { "Name": "{{J(DomainCheck)}}", "Expression": "VALUE >= 0" } ] }
            """);
        WriteFile("Sequences", "sq.json", $$"""{ "Schema": "{{J(Schema)}}", "Name": "{{J(Sequence)}}", "Start": 1, "Increment": 1 }""");
        WriteFile("Materialized Views", "mv.json", $$"""
            { "Schema": "{{J(Schema)}}", "Name": "{{J(View)}}", "Definition": "{{J("SELECT count(*) AS n FROM " + Q(Schema) + "." + Q(Table))}}",
              "Indexes": [ { "Name": "{{J(ViewIndex)}}", "IndexColumns": "n" } ] }
            """);
    }

    private void Package()
    {
        WritePackage("", "Enum Types", "Domain Types", "Sequences", "Materialized Views");
        WriteTypes();
    }

    private void WriteBase()
    {
        Package();
        WriteFile("Tables", "parent.json", ParentJson());
        WriteFile("Tables", "child.json", ChildJson());
        WriteFile("Tables", "gone.json", GoneJson());
    }

    // Renames of a column and a table; a check and a foreign key dropped; a check rewritten; a type changed; a table removed.
    private void WriteChangeSet()
    {
        Package();
        WriteFile("Tables", "parent.json", ParentJson(col: Col + "n", colOldName: Col, ix: Ix + "n", withCheck: false, longType: "bigint", likePattern: "%y%"));
        WriteFile("Tables", "child.json", ChildJson(Child + "n", oldName: Child, withForeignKey: false));
    }

    private static readonly Dictionary<string, string> AllowTheChanges = new() { ["Target:DestructiveChangePolicy"] = "allow" };

    private string InSchema => $"(SELECT oid FROM pg_namespace WHERE nspname = {L(Schema)})";

    private bool Exists(string sql) => Count(sql) > 0;

    private bool RelationExists(string name, string kinds) =>
        Exists($"SELECT count(*) FROM pg_class WHERE relnamespace = {InSchema} AND relname = {L(name)} AND relkind = ANY(string_to_array({L(kinds)}, ','))");

    private bool ColumnExists(string table, string column) =>
        Exists($"SELECT count(*) FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid WHERE c.relnamespace = {InSchema} AND c.relname = {L(table)} AND a.attname = {L(column)} AND NOT a.attisdropped");

    private bool ConstraintExists(string name, string type) =>
        Exists($"SELECT count(*) FROM pg_constraint WHERE connamespace = {InSchema} AND conname = {L(name)} AND contype = {L(type)}");

    // Every relation, constraint and type of the schema by oid: a drop and re-create the audit does not mention changes it.
    private string Snapshot() => Scalar($"""
        SELECT string_agg(k, E'\n' ORDER BY k) FROM (
          SELECT 'r:' || relname || '|' || oid || '|' || relfilenode AS k FROM pg_class WHERE relnamespace = {InSchema}
          UNION ALL SELECT 'c:' || conname || '|' || oid FROM pg_constraint WHERE connamespace = {InSchema}
          UNION ALL SELECT 't:' || typname || '|' || oid FROM pg_type WHERE typnamespace = {InSchema} AND typtype IN ('e', 'd')) x
        """)?.ToString();

    private string Description(string table, string column = null) => Scalar(column == null
        ? $"SELECT obj_description((SELECT oid FROM pg_class WHERE relnamespace = {InSchema} AND relname = {L(table)}), 'pg_class')"
        : $"SELECT col_description((SELECT oid FROM pg_class WHERE relnamespace = {InSchema} AND relname = {L(table)}), (SELECT attnum FROM pg_attribute WHERE attrelid = (SELECT oid FROM pg_class WHERE relnamespace = {InSchema} AND relname = {L(table)}) AND attname = {L(column)}))")?.ToString();

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
            catch (NUnit.Framework.AssertionException e)
            {
                Record(name, e);
            }
            catch (System.Data.Common.DbException e)
            {
                Record(name, e);
            }
            catch (Schema.Utility.RunFailedException e)
            {
                Record(name, e);
            }
            catch (Schema.Domain.PackageException e)
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
        Expect(RelationExists(Table, "r") && RelationExists(Child, "r") && RelationExists(Gone, "r"), "the tables");
        Expect(ColumnExists(Table, Key) && ColumnExists(Table, Col) && ColumnExists(Table, Long), "the columns, the 63-byte one included");
        Expect(RelationExists(Pk, "i") && RelationExists(Ix, "i") && RelationExists(Long, "i"), "the indexes, the 63-byte one included");
        Expect(ConstraintExists(Ck, "c") && ConstraintExists(CkLike, "c"), "the checks");
        Expect(ConstraintExists(Fk, "f"), "the foreign key");
        Expect(Exists($"SELECT count(*) FROM pg_statistic_ext WHERE stxnamespace = {InSchema} AND stxname = {L(St)}"), "the statistics");
        Expect(Exists($"SELECT count(*) FROM pg_type WHERE typnamespace = {InSchema} AND typname = {L(Enum)} AND typtype = 'e'"), "the enum type");
        Expect(Exists($"SELECT count(*) FROM pg_enum e JOIN pg_type t ON t.oid = e.enumtypid WHERE t.typname = {L(Enum)} AND e.enumlabel = {L(H)}"), "the enum value");
        Expect(ConstraintExists(DomainCheck, "c"), "the domain and its check");
        Expect(RelationExists(Sequence, "S"), "the sequence");
        Expect(RelationExists(View, "m") && RelationExists(ViewIndex, "i"), "the materialized view and its index");
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
            var json = Scalar($"SELECT \"SchemaSmith\".\"GenerateTableJSON\"({L(Schema)}, {L(table)})")?.ToString();
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
        Expect(RelationExists(Ix + "n", "i") && !RelationExists(Ix, "i"), "the index replaced");
        Expect(RelationExists(Child + "n", "r") && !RelationExists(Child, "r"), "the table renamed");
        Expect(!RelationExists(Gone, "r"), "the removed table dropped");
        Expect(!ConstraintExists(Ck, "c"), "the check dropped");
        Expect(!ConstraintExists(Fk, "f"), "the foreign key dropped");
        Expect(Scalar($"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE connamespace = {InSchema} AND conname = {L(CkLike)}")?.ToString()?.Contains("%y%") == true,
            "the changed check re-created with its new pattern");
        ExpectEqual(Scalar($"SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid WHERE c.relnamespace = {InSchema} AND c.relname = {L(Table)} AND a.attname = {L(Long)}")?.ToString(),
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

    // The temp-table cleanup run before every query token cast each schema name in the database to regnamespace, so a
    // schema holding a double quote anywhere in the database broke every query token there (42602, invalid name
    // syntax). It reads the session's temp schema by oid now, and still clears the session's temp tables.
    [Test]
    public void AQueryToken_Resolves_WhereASchemaNameHoldsADoubleQuote()
    {
        const string other = "tok\"en";
        Exec($"DROP SCHEMA IF EXISTS {Q(other)} CASCADE; CREATE SCHEMA {Q(other)}; CREATE TABLE {Q(other)}.t (i int)");
        try
        {
            Exec("CREATE TEMPORARY TABLE schemasmith_token_probe (i int)");
            var tokens = new Dictionary<string, string> { ["Answer"] = global::Schema.Utility.TokenHelper.QueryTag + "SELECT 'yes'" };
            using var command = Connection.CreateCommand();
            global::Schema.Utility.TokenHelper.ResolveQueryTokens(tokens, [], command, ".", Platform.PostgreSQL);
            Assert.Multiple(() =>
            {
                Assert.That(tokens["Answer"], Is.EqualTo("yes"));
                Assert.That(Count("SELECT count(*) FROM pg_class WHERE relname = 'schemasmith_token_probe' AND relnamespace = pg_my_temp_schema()"), Is.Zero,
                    "the session's temp tables are still cleared");
            });
        }
        finally
        {
            Exec($"DROP SCHEMA IF EXISTS {Q(other)} CASCADE");
        }
    }
}
