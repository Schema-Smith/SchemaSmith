// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// <c>AnsiPadding: false</c> on a char, varchar, binary or varbinary column. SQL Server fixes a column's padding when the
/// column is created (from the session's ANSI_PADDING) and every ALTER COLUMN pads it again whatever the session says,
/// so an unpadded column can only be made by CREATE TABLE or ADD under ANSI_PADDING OFF. Each test asserts the outcome
/// a user sees -- trailing blanks kept or trimmed -- as well as the catalog flag.
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_AnsiPaddingTests : BaseTableQuenchTests
{
    [Test]
    public void NewTable_GetsEachColumnsDeclaredPadding()
    {
        var table = Unique("PadNew");
        WithConnection(cmd =>
        {
            RunTableQuenchProc(cmd, Table(table,
                Col("Off", "VARCHAR(10)", padding: false),
                Col("OffBin", "VARBINARY(10)", padding: false),
                Col("OffChar", "CHAR(5)", padding: false, nullable: false, @default: "'x'"),
                Col("Omitted", "VARCHAR(10)"),
                Col("On", "VARCHAR(10)", padding: true),
                Col("Wide", "NVARCHAR(10)")));

            Assert.That(IsPadded(cmd, table, "Off"), Is.False);
            Assert.That(IsPadded(cmd, table, "OffBin"), Is.False);
            Assert.That(IsPadded(cmd, table, "OffChar"), Is.False);
            Assert.That(IsPadded(cmd, table, "Omitted"), Is.True, "an omitted AnsiPadding creates the column padded, as before");
            Assert.That(IsPadded(cmd, table, "On"), Is.True);
            Assert.That(IsPadded(cmd, table, "Wide"), Is.True);
            Assert.That(Scalar(cmd, $"SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY column_id) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]')"),
                Is.EqualTo("Off,OffBin,OffChar,Omitted,On,Wide"), "the table must be created in declared order, not repaired afterwards by swapping columns to the end");
            Assert.That(StoredLength(cmd, table, "Off", "'ab  '"), Is.EqualTo(2), "an unpadded varchar trims trailing blanks");
            Assert.That(StoredLength(cmd, table, "Omitted", "'ab  '"), Is.EqualTo(4), "a padded varchar keeps them");
        }, table);
    }

    [Test]
    public void NewColumnOnAnExistingTable_GetsItsDeclaredPadding()
    {
        var table = Unique("PadAdd");
        WithConnection(cmd =>
        {
            RunTableQuenchProc(cmd, Table(table, Col("Id", "INT", nullable: false)));
            RunTableQuenchProc(cmd, Table(table, Col("Id", "INT", nullable: false),
                Col("Off", "VARCHAR(10)", padding: false), Col("Omitted", "VARCHAR(10)")));

            Assert.That(IsPadded(cmd, table, "Off"), Is.False);
            Assert.That(IsPadded(cmd, table, "Omitted"), Is.True);
        }, table);
    }

    [Test]
    public void PaddedColumnDeclaredUnpadded_IsReplacedKeepingItsDataDefaultAndIndex()
    {
        var table = Unique("PadToOff");
        WithConnection(cmd =>
        {
            Exec(cmd, $"CREATE TABLE dbo.[{table}] (Id INT NOT NULL, Val VARCHAR(10) NOT NULL CONSTRAINT [DF_{table}_Val] DEFAULT 'd')");
            Exec(cmd, $"CREATE INDEX [IX_{table}_Val] ON dbo.[{table}] (Val)");
            Exec(cmd, $"INSERT dbo.[{table}] (Id, Val) VALUES (1, 'keep')");

            RunTableQuenchProc(cmd, Table(table,
                [Col("Id", "INT", nullable: false), Col("Val", "VARCHAR(10)", padding: false, nullable: false, @default: "'d'")],
                indexes: $$"""{ "Name": "[IX_{{table}}_Val]", "IndexColumns": "[Val]" }"""));

            Assert.That(IsPadded(cmd, table, "Val"), Is.False);
            Assert.That(Scalar(cmd, $"SELECT Val FROM dbo.[{table}] WHERE Id = 1"), Is.EqualTo("keep"));
            Assert.That(Scalar(cmd, $"SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = 'Val'"), Is.EqualTo(false));
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('dbo.[{table}]')"), Is.EqualTo(1));
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = 'IX_{table}_Val'"), Is.EqualTo(1));
        }, table);
    }

    [Test]
    public void UnpaddedColumnWithAnotherChange_StaysUnpadded()
    {
        var table = Unique("PadKeep");
        WithConnection(cmd =>
        {
            Exec(cmd, $"EXEC('SET ANSI_PADDING OFF; CREATE TABLE dbo.[{table}] (Id INT NOT NULL, Val VARCHAR(10) NULL)')");
            Exec(cmd, $"INSERT dbo.[{table}] (Id, Val) VALUES (1, 'keep')");

            RunTableQuenchProc(cmd, Table(table, Col("Id", "INT", nullable: false), Col("Val", "VARCHAR(20)", padding: false, nullable: false)));

            Assert.That(IsPadded(cmd, table, "Val"), Is.False, "ALTER COLUMN pads a column, so the change must not go through it");
            Assert.That(Scalar(cmd, $"SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = 'Val'"), Is.EqualTo((short)20));
            Assert.That(Scalar(cmd, $"SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = 'Val'"), Is.EqualTo(false));
            Assert.That(Scalar(cmd, $"SELECT Val FROM dbo.[{table}] WHERE Id = 1"), Is.EqualTo("keep"));
        }, table);
    }

    [Test]
    public void UnpaddedColumnDeclaredPadded_IsPadded()
    {
        var table = Unique("PadToOn");
        WithConnection(cmd =>
        {
            Exec(cmd, $"EXEC('SET ANSI_PADDING OFF; CREATE TABLE dbo.[{table}] (Id INT NOT NULL, Val VARCHAR(10) NULL)')");

            RunTableQuenchProc(cmd, Table(table, Col("Id", "INT", nullable: false), Col("Val", "VARCHAR(10)", padding: true)));

            Assert.That(IsPadded(cmd, table, "Val"), Is.True);
        }, table);
    }

    // Packages extracted before AnsiPadding existed omit it. Deploying one must not re-pad a legacy unpadded column.
    [Test]
    public void UnpaddedColumnWithPaddingOmitted_IsLeftAlone()
    {
        var table = Unique("PadOmit");
        WithConnection(cmd =>
        {
            Exec(cmd, $"EXEC('SET ANSI_PADDING OFF; CREATE TABLE dbo.[{table}] (Id INT NOT NULL, Val VARCHAR(10) NULL)')");
            ClearChangeAudit(cmd);

            RunTableQuenchProc(cmd, Table(table, Col("Id", "INT", nullable: false), Col("Val", "VARCHAR(10)")));

            Assert.That(IsPadded(cmd, table, "Val"), Is.False);
            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0));
        }, table);
    }

    [TestCase("VARCHAR(10)", true, TestName = "AnsiPadding_UnpaddedVarchar_DoesNotChurn")]
    [TestCase("BINARY(4)", true, TestName = "AnsiPadding_UnpaddedBinary_DoesNotChurn")]
    [TestCase("VARCHAR(10)", false, TestName = "AnsiPadding_ExplicitlyPadded_DoesNotChurn")]
    [TestCase("NVARCHAR(10)", true, TestName = "AnsiPadding_FalseOnNvarchar_IsIgnored")]
    [TestCase("INT", true, TestName = "AnsiPadding_FalseOnInt_IsIgnored")]
    public void UnchangedDeclaration_IsNotRewrittenOnRedeploy(string dataType, bool unpadded)
    {
        var table = Unique("PadChurn");
        WithConnection(cmd =>
        {
            var defs = Table(table, Col("Id", "INT", nullable: false), Col("Val", dataType, padding: !unpadded));
            RunTableQuenchProc(cmd, defs);
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs);

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0), $"{dataType} with AnsiPadding {!unpadded} was altered on a redeploy");
        }, table);
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    private static string Col(string name, string type, bool? padding = null, bool nullable = true, string @default = null) =>
        $"{{ \"Name\": \"[{name}]\", \"DataType\": \"{type}\", \"Nullable\": {(nullable ? "true" : "false")}" +
        (padding.HasValue ? $", \"AnsiPadding\": {(padding.Value ? "true" : "false")}" : "") +
        (@default != null ? $", \"Default\": \"{@default}\"" : "") + " }";

    private static string Table(string table, params string[] columns) => Table(table, columns, indexes: null);

    private static string Table(string table, string[] columns, string indexes) =>
        $"[ {{ \"Schema\": \"[dbo]\", \"Name\": \"[{table}]\", \"Columns\": [ {string.Join(", ", columns)} ]" +
        (indexes != null ? $", \"Indexes\": [ {indexes} ]" : "") + " } ]";

    private void RunTableQuenchProc(IDbCommand cmd, string json) => base.RunTableQuenchProc(cmd, json, productName: "AnsiPaddingTests");

    private void WithConnection(Action<IDbCommand> body, string table)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try { body(cmd); }
        finally { Exec(cmd, $"IF OBJECT_ID('dbo.[{table}]') IS NOT NULL DROP TABLE dbo.[{table}]"); }
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static bool? IsPadded(IDbCommand cmd, string table, string column) =>
        Scalar(cmd, $"SELECT is_ansi_padded FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = '{column}'") as bool?;

    private static int StoredLength(IDbCommand cmd, string table, string column, string literal)
    {
        Exec(cmd, $"DELETE dbo.[{table}]; INSERT dbo.[{table}] ([{column}]) VALUES ({literal})");
        return Convert.ToInt32(Scalar(cmd, $"SELECT DATALENGTH([{column}]) FROM dbo.[{table}]"));
    }

    private static void ClearChangeAudit(IDbCommand cmd) => Exec(cmd, "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");

    private static int ColumnChangesRecorded(IDbCommand cmd, string table) =>
        Convert.ToInt32(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'column' AND ObjectName LIKE '%{table}%'"));
}
