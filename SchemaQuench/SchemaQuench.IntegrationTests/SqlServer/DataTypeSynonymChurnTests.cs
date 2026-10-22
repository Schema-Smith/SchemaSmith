// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Data-type synonyms must not churn (parity with PostgreSQL).
///
/// <para>SQL Server accepts synonyms and <c>sys.types</c> reports the base type whatever the package
/// declared, so a column authored <c>INTEGER</c> comes back <c>int</c> and the comparison read that as
/// a type change on EVERY quench — an unnecessary column rewrite on every deploy, forever. Only
/// <c>ROWVERSION</c> was mapped before, and inline in three separate places.</para>
///
/// <para><b>Asserted at the outcome</b> — declare with the synonym, deploy twice, and
/// require the column not to be rewritten. The mapping table itself was measured against SQL Server
/// 2022 rather than assumed, so re-asserting it here would only check my arithmetic against itself.</para>
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class DataTypeSynonymChurnTests : BaseTableQuenchTests
{
    // Left is what a user writes; the comment is what sys.types reports back.
    [TestCase("INTEGER", TestName = "Synonym_INTEGER_DoesNotChurn")]                        // -> int
    [TestCase("DEC(10,2)", TestName = "Synonym_DEC_DoesNotChurn")]                          // -> decimal
    [TestCase("CHARACTER VARYING(50)", TestName = "Synonym_CHARACTER_VARYING_DoesNotChurn")]// -> varchar
    [TestCase("NATIONAL CHARACTER(10)", TestName = "Synonym_NATIONAL_CHARACTER_DoesNotChurn")] // -> nchar
    [TestCase("BINARY VARYING(20)", TestName = "Synonym_BINARY_VARYING_DoesNotChurn")]      // -> varbinary
    [TestCase("DOUBLE PRECISION", TestName = "Synonym_DOUBLE_PRECISION_DoesNotChurn")]      // -> float
    // Kept as a guard on the fold: ROWVERSION was the ONE mapping SQL Server already had, spread over
    // three inline REPLACE calls. Folding them into the function must not lose it.
    [TestCase("ROWVERSION", TestName = "Synonym_ROWVERSION_StillDoesNotChurn")]             // -> timestamp
    [TestCase("CHAR VARYING(5)", TestName = "Synonym_CHAR_VARYING_DoesNotChurn")]          // -> varchar(5)
    [TestCase("NATIONAL CHAR(5)", TestName = "Synonym_NATIONAL_CHAR_DoesNotChurn")]        // -> nchar(5)
    [TestCase("NATIONAL CHAR VARYING(5)", TestName = "Synonym_NATIONAL_CHAR_VARYING_DoesNotChurn")] // -> nvarchar(5)
    [TestCase("NCHAR VARYING(5)", TestName = "Synonym_NCHAR_VARYING_DoesNotChurn")]        // -> nvarchar(5)
    [TestCase("NATIONAL TEXT", TestName = "Synonym_NATIONAL_TEXT_DoesNotChurn")]           // -> ntext
    // A length, precision or scale left to its default is reported spelled out.
    [TestCase("CHAR", TestName = "Default_CHAR_DoesNotChurn")]                             // -> char(1)
    [TestCase("VARCHAR", TestName = "Default_VARCHAR_DoesNotChurn")]                       // -> varchar(1)
    [TestCase("NCHAR", TestName = "Default_NCHAR_DoesNotChurn")]                           // -> nchar(1)
    [TestCase("NVARCHAR", TestName = "Default_NVARCHAR_DoesNotChurn")]                     // -> nvarchar(1)
    [TestCase("BINARY", TestName = "Default_BINARY_DoesNotChurn")]                         // -> binary(1)
    [TestCase("VARBINARY", TestName = "Default_VARBINARY_DoesNotChurn")]                   // -> varbinary(1)
    [TestCase("CHARACTER VARYING", TestName = "Default_CHARACTER_VARYING_DoesNotChurn")]   // -> varchar(1)
    [TestCase("DECIMAL", TestName = "Default_DECIMAL_DoesNotChurn")]                       // -> decimal(18,0)
    [TestCase("DECIMAL(10)", TestName = "Default_DECIMAL_Scale_DoesNotChurn")]             // -> decimal(10,0)
    [TestCase("NUMERIC", TestName = "Default_NUMERIC_DoesNotChurn")]                       // -> numeric(18,0)
    [TestCase("NUMERIC ( 12 )", TestName = "Default_NUMERIC_Scale_DoesNotChurn")]          // -> numeric(12,0)
    [TestCase("DEC(9)", TestName = "Default_DEC_Scale_DoesNotChurn")]                      // -> decimal(9,0)
    [TestCase("FLOAT(24)", TestName = "Float24_DoesNotChurn")]                             // -> real
    [TestCase("FLOAT(1)", TestName = "Float1_DoesNotChurn")]                               // -> real
    [TestCase("FLOAT(53)", TestName = "Float53_DoesNotChurn")]                             // -> float
    [TestCase("FLOAT(25)", TestName = "Float25_DoesNotChurn")]                             // -> float
    public void ColumnDeclaredWithASynonym_IsNotRewrittenOnRedeploy(string synonym)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"SynProduct_{uid}";
        var table = $"SynTable_{uid}";
        var defs = TableWithColumnType(table, synonym);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            var deployed = DeployedTypeName(cmd, table, "Val");
            Assert.That(deployed, Is.Not.Empty, "Setup: the column must deploy.");

            // The churn is invisible in the column's type -- the type is right either way. Watch the
            // audit: a second deploy of an unchanged declaration must record no column change at all.
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a column declared {synonym} was altered on a redeploy of an unchanged declaration -- "
                + $"sys.types reports it as {deployed}, and without synonym normalization the compare "
                + "reads that as a type change every single time");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // Typed XML: the catalog reports DOCUMENT through is_xml_document and never reports CONTENT, the default. Read
    // without it, a DOCUMENT column compared unequal to its own declaration and was altered on every deploy, and a
    // declaration spelling CONTENT out did the same.
    [TestCase("XML(DOCUMENT [dbo].[ChurnXsc])", TestName = "TypedXml_DOCUMENT_DoesNotChurn")]
    [TestCase("XML(CONTENT [dbo].[ChurnXsc])", TestName = "TypedXml_CONTENT_DoesNotChurn")]
    public void TypedXmlColumn_IsNotRewrittenOnRedeploy(string dataType)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"XmlProduct_{uid}";
        var table = $"XmlTable_{uid}";
        var defs = TableWithColumnType(table, dataType);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "IF NOT EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE [name] = 'ChurnXsc') "
                          + "EXEC('CREATE XML SCHEMA COLLECTION dbo.ChurnXsc AS N''<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
                          + "<xsd:element name=\"r\" type=\"xsd:string\"/></xsd:schema>''')";
        cmd.ExecuteNonQuery();

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a column declared {dataType} was altered on a redeploy of an unchanged declaration");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // An alias type whose name ends in CHAR was rendered with its base type's width (SHORTCHAR(10)), so it never
    // compared equal to its declaration -- and extracted that way, it could not be deployed at all.
    [Test]
    public void ColumnDeclaredWithAnAliasType_IsNotRewrittenOnRedeploy()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"AliasProduct_{uid}";
        var table = $"AliasTable_{uid}";
        var alias = $"Short{uid}Char";
        var defs = TableWithColumnType(table, alias);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE TYPE dbo.{alias} FROM varchar(10)";
        cmd.ExecuteNonQuery();

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a column declared with the alias type {alias} was altered on a redeploy of an unchanged declaration");
        }
        finally
        {
            DropTable(cmd, table);
            cmd.CommandText = $"DROP TYPE dbo.{alias}";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // An alias type whose name starts with a system type's (Varchar50, Char_YN) was read as that system type with
    // the rest of its name as trailing text, giving VARCHAR(1) 50: invalid DDL on every deploy.
    [TestCase("Varchar", "varchar(50)")]
    [TestCase("Char_", "char(1)")]
    [TestCase("Decimal", "decimal(10, 2)")]
    [TestCase("Binary", "binary(16)")]
    public void ColumnDeclaredWithAnAliasNamedLikeASystemType_DeploysAndIsNotRewritten(string prefix, string baseType)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"AliasPrefixProduct_{uid}";
        var table = $"AliasPrefixTable_{uid}";
        var alias = $"{prefix}0{uid}";
        var defs = TableWithColumnType(table, alias);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE TYPE dbo.{alias} FROM {baseType}";
        cmd.ExecuteNonQuery();

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            Assert.That(DeployedTypeName(cmd, table, "Val"), Is.EqualTo(alias), "the column must be created with the alias type");

            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: product);
            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a column declared with the alias type {alias} was altered on a redeploy of an unchanged declaration");
        }
        finally
        {
            DropTable(cmd, table);
            cmd.CommandText = $"DROP TYPE dbo.{alias}";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // #582: sysname is SQL Server's own alias for nvarchar(128) -- same storage -- but sys.types names it separately, so a
    // column built one way and declared the other was altered, and every index over it dropped and rebuilt. Legacy DDL
    // that used sysname, adopted into a package that says NVARCHAR(128), took that rebuild on its first deploy.
    [TestCase("SYSNAME", "NVARCHAR(128)", TestName = "Sysname_Live_DeclaredNvarchar128_IsNotAltered")]
    [TestCase("NVARCHAR(128)", "SYSNAME", TestName = "Nvarchar128_Live_DeclaredSysname_IsNotAltered")]
    [TestCase("NVARCHAR(128)", "sysname", TestName = "Nvarchar128_Live_DeclaredLowercaseSysname_IsNotAltered")]
    public void SysnameAndNvarchar128_CompareEqual_SoTheColumnAndItsIndexAreLeftAlone(string liveType, string declaredType)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"SysnameTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE TABLE [dbo].[{table}] ([Id] INT NOT NULL, [Val] {liveType} NOT NULL); "
                          + $"CREATE INDEX [IX_{table}_Val] ON [dbo].[{table}] ([Val]);";
        cmd.ExecuteNonQuery();

        try
        {
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, IndexedTable(table, declaredType, nullable: false), productName: $"SysnameProduct_{uid}");

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a {liveType} column declared {declaredType} was altered -- the two are the same type");
            Assert.That(IndexChangesRecorded(cmd, table), Is.EqualTo(0),
                $"the index over a {liveType} column declared {declaredType} was dropped and rebuilt");
            Assert.That(DeployedTypeName(cmd, table, "Val"), Is.EqualTo(liveType.StartsWith("SYS") ? "sysname" : "nvarchar"),
                "the column was re-typed: nothing should have touched it");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // The equality is the type's alone. sysname only defaults to NOT NULL, so a sysname NULL column declared NOT NULL is
    // still a real change, and must still be made.
    [Test]
    public void SysnameNull_DeclaredNvarchar128NotNull_IsStillAltered()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"SysnameNullTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"CREATE TABLE [dbo].[{table}] ([Id] INT NOT NULL, [Val] SYSNAME NULL);";
        cmd.ExecuteNonQuery();

        try
        {
            RunTableQuenchProc(cmd, IndexedTable(table, "NVARCHAR(128)", nullable: false), productName: $"SysnameNullProduct_{uid}");

            cmd.CommandText = $"SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('[dbo].[{table}]') AND name = 'Val'";
            Assert.That(Convert.ToBoolean(cmd.ExecuteScalar()), Is.False,
                "a nullable sysname column declared NOT NULL was left nullable -- the type compares equal, the nullability does not");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    private static string IndexedTable(string table, string dataType, bool nullable) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT", "Nullable": false },
      { "Name": "[Val]", "DataType": "{{dataType}}", "Nullable": {{(nullable ? "true" : "false")}} }
    ],
    "Indexes": [ { "Name": "[IX_{{table}}_Val]", "IndexColumns": "[Val]" } ]
  }
]
""";

    private int IndexChangesRecorded(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM SchemaSmith.ChangeAudit
 WHERE SessionId = @@SPID AND ObjectType = 'index' AND ObjectName LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string TableWithColumnType(string table, string dataType) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT", "Nullable": false },
      { "Name": "[Val]", "DataType": "{{dataType}}", "Nullable": true }
    ]
  }
]
""";

    private void DropTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"IF OBJECT_ID('[dbo].[{table}]') IS NOT NULL DROP TABLE [dbo].[{table}];";
        cmd.ExecuteNonQuery();
    }

    private string DeployedTypeName(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $@"
SELECT ISNULL((SELECT t.name FROM sys.columns c
                 JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID('[dbo].[{table}]') AND c.name = '{column}'), '')";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private void ClearChangeAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private int ColumnChangesRecorded(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM SchemaSmith.ChangeAudit
 WHERE SessionId = @@SPID AND ObjectType = 'column' AND ObjectName LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
