// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Data-type synonyms must not churn (parity with PostgreSQL, which has had this since its
/// parse step).
///
/// <para>Every engine accepts synonyms and the catalog reports the BASE name whatever the package
/// declared — a column authored <c>INTEGER</c> comes back <c>int</c>. Without normalization the
/// comparison saw a difference that was not one and drop/recreated the column on EVERY quench: not a
/// cosmetic diff, an unnecessary table rewrite on every deploy, forever.</para>
///
/// <para><b>Asserted at the outcome.</b> Not "the helper returns X for Y" — declare the
/// column with the synonym, deploy twice, and require the column to be untouched. The mapping table
/// itself was measured against both engines rather than assumed, so a test that re-asserted the table
/// would only be checking my arithmetic against itself.</para>
/// </summary>
[Category("Integration")]
public abstract class DataTypeSynonymChurnSharedTests : BaseTableQuenchTests
{
    // Left column is what a user writes; the comment is what the engine reports back. Verified on
    // MySQL 8.0 and MariaDB 11.4 -- identical on both, modulo the integer display width MariaDB adds
    // and StripIntDisplayWidth already handles.
    [TestCase("INTEGER", TestName = "Synonym_INTEGER_DoesNotChurn")]              // -> int
    [TestCase("DEC(10,2)", TestName = "Synonym_DEC_DoesNotChurn")]                // -> decimal(10,2)
    [TestCase("NUMERIC(10,2)", TestName = "Synonym_NUMERIC_DoesNotChurn")]        // -> decimal(10,2)
    [TestCase("FIXED(10,2)", TestName = "Synonym_FIXED_DoesNotChurn")]            // -> decimal(10,2)
    [TestCase("BOOL", TestName = "Synonym_BOOL_DoesNotChurn")]                    // -> tinyint(1)
    [TestCase("BOOLEAN", TestName = "Synonym_BOOLEAN_DoesNotChurn")]              // -> tinyint(1)
    [TestCase("CHARACTER VARYING(50)", TestName = "Synonym_CHARACTER_VARYING_DoesNotChurn")] // -> varchar(50)
    [TestCase("CHARACTER(10)", TestName = "Synonym_CHARACTER_DoesNotChurn")]      // -> char(10)
    [TestCase("ENUM('a ','b')", TestName = "Enum_TrailingSpaceMember_DoesNotChurn")] // -> enum('a','b')
    [TestCase("SET('x ','it''s ')", TestName = "Set_TrailingSpaceMember_DoesNotChurn")] // -> set('x','it''s')
    [TestCase("VARCHAR(10) BINARY", TestName = "Attribute_VARCHAR_BINARY_DoesNotChurn")] // -> varchar(10) <charset>_bin
    [TestCase("CHAR(5) BINARY", TestName = "Attribute_CHAR_BINARY_DoesNotChurn")]
    [TestCase("TEXT BINARY", TestName = "Attribute_TEXT_BINARY_DoesNotChurn")]
    [TestCase("ENUM('a','b') BINARY", TestName = "Attribute_ENUM_BINARY_DoesNotChurn")]
    [TestCase("REAL", TestName = "Synonym_REAL_DoesNotChurn")]                 // -> double (float under REAL_AS_FLOAT)
    [TestCase("DOUBLE PRECISION", TestName = "Synonym_DOUBLE_PRECISION_DoesNotChurn")] // -> double
    [TestCase("FLOAT4", TestName = "Synonym_FLOAT4_DoesNotChurn")]             // -> float
    [TestCase("FLOAT8", TestName = "Synonym_FLOAT8_DoesNotChurn")]             // -> double
    [TestCase("INT1", TestName = "Synonym_INT1_DoesNotChurn")]                 // -> tinyint
    [TestCase("INT2", TestName = "Synonym_INT2_DoesNotChurn")]                 // -> smallint
    [TestCase("INT3", TestName = "Synonym_INT3_DoesNotChurn")]                 // -> mediumint
    [TestCase("MIDDLEINT", TestName = "Synonym_MIDDLEINT_DoesNotChurn")]       // -> mediumint
    [TestCase("INT4", TestName = "Synonym_INT4_DoesNotChurn")]                 // -> int
    [TestCase("INT8", TestName = "Synonym_INT8_DoesNotChurn")]                 // -> bigint
    [TestCase("INT4(5)", TestName = "Synonym_INT4_5_DoesNotChurn")]            // -> int(5) on MariaDB
    [TestCase("LONG", TestName = "Synonym_LONG_DoesNotChurn")]                 // -> mediumtext
    [TestCase("LONG VARCHAR", TestName = "Synonym_LONG_VARCHAR_DoesNotChurn")] // -> mediumtext
    [TestCase("LONG VARBINARY", TestName = "Synonym_LONG_VARBINARY_DoesNotChurn")] // -> mediumblob
    [TestCase("DECIMAL", TestName = "Synonym_DECIMAL_DoesNotChurn")]           // -> decimal(10,0)
    [TestCase("NUMERIC", TestName = "Synonym_NUMERIC_DoesNotChurn")]           // -> decimal(10,0)
    [TestCase("DEC", TestName = "Synonym_bare_DEC_DoesNotChurn")]              // -> decimal(10,0)
    [TestCase("FIXED", TestName = "Synonym_bare_FIXED_DoesNotChurn")]          // -> decimal(10,0)
    // A length after a space is not a bare type: it was given the default length too, a syntax error on deploy.
    [TestCase("DECIMAL (10,2)", TestName = "Spaced_DECIMAL_DoesNotChurn")]    // -> decimal(10,2)
    [TestCase("DEC (10,2)", TestName = "Spaced_DEC_DoesNotChurn")]            // -> decimal(10,2)
    [TestCase("CHAR (10)", TestName = "Spaced_CHAR_DoesNotChurn")]            // -> char(10)
    [TestCase("BINARY (4)", TestName = "Spaced_BINARY_DoesNotChurn")]         // -> binary(4)
    [TestCase("BIT (3)", TestName = "Spaced_BIT_DoesNotChurn")]               // -> bit(3)
    [TestCase("DECIMAL UNSIGNED", TestName = "Synonym_DECIMAL_UNSIGNED_DoesNotChurn")] // -> decimal(10,0) unsigned
    [TestCase("DEC UNSIGNED", TestName = "Synonym_DEC_UNSIGNED_DoesNotChurn")] // -> decimal(10,0) unsigned
    [TestCase("FIXED UNSIGNED", TestName = "Synonym_FIXED_UNSIGNED_DoesNotChurn")] // -> decimal(10,0) unsigned
    [TestCase("NUMERIC UNSIGNED", TestName = "Synonym_NUMERIC_UNSIGNED_DoesNotChurn")] // -> decimal(10,0) unsigned
    [TestCase("CHAR", TestName = "Synonym_CHAR_DoesNotChurn")]                 // -> char(1)
    [TestCase("BIT", TestName = "Synonym_BIT_DoesNotChurn")]                   // -> bit(1)
    [TestCase("BINARY", TestName = "Synonym_BINARY_DoesNotChurn")]             // -> binary(1)
    [TestCase("NVARCHAR(10)", TestName = "Synonym_NVARCHAR_10_DoesNotChurn")]  // -> varchar(10) utf8mb3
    [TestCase("NCHAR(5)", TestName = "Synonym_NCHAR_5_DoesNotChurn")]          // -> char(5) utf8mb3
    [TestCase("NATIONAL VARCHAR(10)", TestName = "Synonym_NATIONAL_VARCHAR_10_DoesNotChurn")] // -> varchar(10) utf8mb3
    [TestCase("NATIONAL CHAR(3)", TestName = "Synonym_NATIONAL_CHAR_3_DoesNotChurn")] // -> char(3) utf8mb3
    [TestCase("NCHAR VARCHAR(4)", TestName = "Synonym_NCHAR_VARCHAR_4_DoesNotChurn")] // -> varchar(4) utf8mb3
    [TestCase("GEOMETRYCOLLECTION", TestName = "Synonym_GEOMETRYCOLLECTION_DoesNotChurn")] // -> geomcollection on MySQL 8
    public void ColumnDeclaredWithASynonym_IsNotRewrittenOnRedeploy(string synonym)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"SynProduct_{uid}";
        var table = $"SynTable_{uid}";
        var defs = TableWithColumnType(table, synonym);

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            var deployed = GetColumnDataType(cmd, _mainDb, table, "Val");
            Assert.That(deployed, Is.Not.EqualTo("UNKNOWN"), "Setup: the column must deploy.");

            // A rewrite is what churn looks like here, and it is invisible in the column's type --
            // the type is right either way. Watch the audit instead: a second deploy of an unchanged
            // declaration must record no column change at all.
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a column declared {synonym} was altered on a redeploy of an unchanged declaration -- "
                + $"the engine reports it as {deployed}, and without synonym normalization the compare "
                + "reads that as a type change every single time");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // MY-V3: MySQL stores a bare VECTOR as vector(2048), so it was altered on every deploy. MariaDB refuses a VECTOR
    // without its dimension, so there only the dimensioned form exists.
    [TestCase("VECTOR(3)")]
    [TestCase("VECTOR")]
    public void AVectorColumn_IsNotRewrittenOnRedeploy(string declared)
    {
        using (var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT VERSION()";
            var version = cmd.ExecuteScalar()?.ToString() ?? "";
            var isMariaDb = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
            var numbers = version.Split('-')[0].Split('.');
            var major = int.Parse(numbers[0]);
            var minor = int.Parse(numbers[1]);
            if (isMariaDb ? major < 11 || (major == 11 && minor < 7) : major < 9)
                Assert.Ignore("VECTOR is MySQL 9.0 / MariaDB 11.7.");
            if (isMariaDb && declared == "VECTOR")
                Assert.Ignore("MariaDB refuses a VECTOR without its dimension.");
        }
        ColumnDeclaredWithASynonym_IsNotRewrittenOnRedeploy(declared);
    }

    // Members are trimmed with their quoting intact. A backslash-escaped quote is part of the member: reading it as the
    // member's end lost everything after it, and the column's DDL then failed to parse.
    [TestCase(@"ENUM('it\'s ','b')", @"ENUM('it\'s','b')")]
    [TestCase(@"ENUM('a\\ ','b')", @"ENUM('a\\','b')")]
    [TestCase("SET('x ','it''s ')", "SET('x','it''s')")]
    public void TrimEnumMembers_KeepsEscapedQuotesInsideTheirMember(string declared, string expected)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SchemaSmith_TrimEnumMembers(@p)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p";
        p.Value = declared;
        cmd.Parameters.Add(p);

        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(expected));
    }

    // A national type implies utf8mb3, so folding NVARCHAR to VARCHAR must keep that: a latin1 column declared NVARCHAR is
    // a real difference, and comparing it equal would be a missed change, worse than the churn the fold removes.
    [Test]
    public void ANationalTypeOverALatin1Column_IsStillChanged()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"NatTable_{uid}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $"CREATE TABLE `{_mainDb}`.`{table}` (`Id` int NOT NULL PRIMARY KEY, `Val` varchar(10) CHARACTER SET latin1 NULL)";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, TableWithColumnType(table, "NVARCHAR(10)"), productName: $"NatProduct_{uid}");

            cmd.CommandText = $"SELECT CHARACTER_SET_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'Val'";
            // MySQL before 8.0.30 and MariaDB before 10.6.1 name the same character set utf8.
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.AnyOf("utf8mb3", "utf8"), "the latin1 column must take the national character set");
        }
        finally
        {
            DropTable(cmd, table);
        }
    }

    // MySQL 8.0.30 and MariaDB 10.6.1 report utf8 as utf8mb3, collations included (utf8mb3_general_ci); earlier
    // servers report utf8_general_ci. A package written in either form must not be re-applied on the other kind.
    [TestCase("utf8", "utf8_general_ci", TestName = "Utf8Names_DoNotChurn")]
    [TestCase("utf8mb3", "utf8mb3_general_ci", TestName = "Utf8mb3Names_DoNotChurn")]
    public void Utf8CharsetAndCollationNames_DoNotChurn(string charset, string collation)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"Utf8Table_{uid}";
        var defs = $$"""
[
  {
    "Name": "{{table}}", "CharacterSet": "{{charset}}", "Collation": "{{collation}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "varchar(10)", "Nullable": true, "CharacterSet": "{{charset}}", "Collation": "{{collation}}" }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ]
  }
]
""";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, defs, productName: $"Utf8Product_{uid}");
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: $"Utf8Product_{uid}");

            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '{table}%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), $"a table and column declared {charset}/{collation} were changed on a redeploy");
        }
        finally
        {
            DropTable(cmd, table);
        }
    }

    // Index spellings the engine stores in another form must not be rebuilt on every deploy.
    [TestCase("varchar(10)", "`Val`", "HASH", false, null, TestName = "Index_HashOnInnoDB_DoesNotChurn")]
    [TestCase("varchar(10)", "`Val`(10)", "BTREE", false, null, TestName = "Index_FullLengthPrefix_DoesNotChurn")]
    // Column names match without regard to case on every server, so the key part may name the column in another case.
    [TestCase("varchar(10)", "`val`(10)", "BTREE", false, null, TestName = "Index_FullLengthPrefix_ColumnInAnotherCase_DoesNotChurn")]
    [TestCase("varchar(10)", "`val`", "BTREE", false, null, TestName = "Index_ColumnInAnotherCase_DoesNotChurn")]
    [TestCase("point", "`Val`", "BTREE", false, "MySQL", TestName = "Index_BtreeOnGeometry_DoesNotChurn")]
    // MySQL 5.7 stores a BTREE key on a POINT as prefix 25, the whole value, so a package extracted there may say so.
    [TestCase("point", "`Val`(25)", "BTREE", false, "MySQL", TestName = "Index_BtreeOnGeometry_WithItsImplicitPrefix_DoesNotChurn")]
    [TestCase("text", "`Val`", "BTREE", true, "MariaDb", TestName = "Index_UniqueOnText_DoesNotChurn")]
    public void IndexDeclaredInAnotherSpelling_IsNotRebuiltOnRedeploy(string columnType, string indexColumns, string indexType, bool unique, string onlyOn)
    {
        if (onlyOn != null && Platform.ToString() != onlyOn) Assert.Ignore($"Only meaningful on {onlyOn}.");
        if (indexColumns.EndsWith("(25)") && columnType == "point")
        {
            using var probe = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            probe.Open();
            using var versionCmd = probe.CreateCommand();
            versionCmd.CommandText = "SELECT VERSION()";
            if (Schema.Utility.VersionHelper.ParsePatchComparable(versionCmd.ExecuteScalar()?.ToString(), Platform) >= 80000)
                Assert.Ignore("Only MySQL 5.7 gives a POINT key an implicit prefix.");
        }
        if (unique && columnType == "text")
        {
            using var probe = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            probe.Open();
            using var versionCmd = probe.CreateCommand();
            versionCmd.CommandText = "SELECT VERSION()";
            if (Schema.Utility.VersionHelper.ParsePatchComparable(versionCmd.ExecuteScalar()?.ToString(), Platform) < 100400)
                Assert.Ignore("A unique key over a whole TEXT column needs MariaDB 10.4's hash enforcement; 10.2 and 10.3 refuse it (1170).");
        }
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"IdxTable_{uid}";
        var nullable = columnType == "point" ? "false" : "true";
        var defs = $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "{{columnType}}", "Nullable": {{nullable}} }
    ],
    "Indexes": [
      { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" },
      { "Name": "ix_val", "Unique": {{(unique ? "true" : "false")}}, "IndexType": "{{indexType}}", "IndexColumns": "{{indexColumns}}" }
    ]
  }
]
""";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, defs, productName: $"IdxProduct_{uid}");
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: $"IdxProduct_{uid}");

            cmd.CommandText = $"SELECT GROUP_CONCAT(CONCAT(ObjectType, ' ', ObjectName, ' ', ActionType)) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '{table}%'";
            Assert.That(cmd.ExecuteScalar()?.ToString() ?? "", Is.Empty, $"an index declared {indexType} on {indexColumns} was changed on a redeploy");
        }
        finally
        {
            DropTable(cmd, table);
        }
    }

    // On a MEMORY table BTREE and HASH are different structures, so a declared change of kind is applied -- the full
    // deploy's index detector never compared the kind, so it was not -- and then left alone.
    [TestCase("HASH", "HASH", TestName = "MemoryIndex_BtreeDeclaredHash_IsChanged")]
    public void AMemoryTableIndexKind_IsApplied(string declared, string expected)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"MemTable_{uid}";
        var kind = declared == null ? "" : $", \"IndexType\": \"{declared}\"";
        var defs = $$"""
[
  {
    "Name": "{{table}}", "Engine": "MEMORY",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "int", "Nullable": true }
    ],
    "Indexes": [
      { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`", "IndexType": "HASH" },
      { "Name": "ix_val", "Unique": false, "IndexColumns": "`Val`"{{kind}} }
    ]
  }
]
""";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $"CREATE TABLE `{_mainDb}`.`{table}` (`Id` int NOT NULL, `Val` int NULL, PRIMARY KEY USING HASH (`Id`), KEY `ix_val` USING {(declared == null ? "HASH" : "BTREE")} (`Val`)) ENGINE=MEMORY";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, defs, productName: $"MemProduct_{uid}");
            cmd.CommandText = $"SELECT INDEX_TYPE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'ix_val'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(expected));

            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: $"MemProduct_{uid}");
            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '{table}%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "and then left alone");
        }
        finally
        {
            DropTable(cmd, table);
        }
    }

    // A foreign key made without a stated rule -- by hand or another tool, not by SchemaSmith, which always states one --
    // reads RESTRICT on MariaDB and MySQL 5.7 and NO ACTION on MySQL 8.0. In InnoDB the two are the same rule, so adopting
    // such a database must not drop and re-add the key.
    [TestCase(null, TestName = "ForeignKey_NoDeclaredRule_DoesNotChurn")]
    [TestCase("RESTRICT", TestName = "ForeignKey_Restrict_DoesNotChurn")]
    [TestCase("NO ACTION", TestName = "ForeignKey_NoAction_DoesNotChurn")]
    public void AForeignKeyRule_IsNotReappliedOnRedeploy(string rule)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var parent = $"FkParent_{uid}";
        var child = $"FkChild_{uid}";
        var rules = rule == null ? "" : $", \"DeleteAction\": \"{rule}\", \"UpdateAction\": \"{rule}\"";
        var defs = $$"""
[
  { "Name": "{{parent}}", "Columns": [ { "Name": "`Id`", "DataType": "int", "Nullable": false } ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ] },
  { "Name": "{{child}}", "Columns": [ { "Name": "`Id`", "DataType": "int", "Nullable": false }, { "Name": "`ParentId`", "DataType": "int", "Nullable": true } ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" }, { "Name": "ix_parent", "IndexColumns": "`ParentId`" } ],
    "ForeignKeys": [ { "Name": "fk_{{uid}}", "Columns": "`ParentId`", "RelatedTable": "{{parent}}", "RelatedColumns": "`Id`"{{rules}} } ] }
]
""";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $"CREATE TABLE `{_mainDb}`.`{parent}` (`Id` int NOT NULL PRIMARY KEY); "
                              + $"CREATE TABLE `{_mainDb}`.`{child}` (`Id` int NOT NULL PRIMARY KEY, `ParentId` int NULL, KEY `ix_parent` (`ParentId`), "
                              + $"CONSTRAINT `fk_{uid}` FOREIGN KEY (`ParentId`) REFERENCES `{parent}` (`Id`))";
            cmd.ExecuteNonQuery();
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: $"FkProduct_{uid}");

            cmd.CommandText = $"SELECT GROUP_CONCAT(CONCAT(ObjectType, ' ', ObjectName, ' ', ActionType)) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{uid}%'";
            Assert.That(cmd.ExecuteScalar()?.ToString() ?? "", Is.Empty, $"a foreign key made without a rule was rebuilt for a declared rule of {rule ?? "(none)"}");
        }
        finally
        {
            DropTable(cmd, child);
            DropTable(cmd, parent);
        }
    }

    // The BINARY attribute is a collation, so declaring it over a column that is not binary-collated is a real change.
    [Test]
    public void TheBinaryAttributeOverACaseInsensitiveColumn_IsStillChanged()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"BinTable_{uid}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $"CREATE TABLE `{_mainDb}`.`{table}` (`Id` int NOT NULL PRIMARY KEY, `Val` varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NULL) DEFAULT CHARSET=utf8mb4";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, TableWithColumnType(table, "VARCHAR(10) BINARY"), productName: $"BinProduct_{uid}");

            cmd.CommandText = $"SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'Val'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("utf8mb4_bin"));
        }
        finally
        {
            DropTable(cmd, table);
        }
    }

    // MariaDB 11.x has collations that belong to no one character set (uca1400_ai_ci); a column or table declared with
    // one is stored under the full name (utf8mb4_uca1400_ai_ci), so the declaration never matched it.
    [Test]
    public void ACharsetIndependentCollation_DoesNotChurn()
    {
        if (Platform.ToString() != "MariaDb") Assert.Ignore("Charset-independent collations are MariaDB's.");
        using var probeConn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        probeConn.Open();
        using (var probe = probeConn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM information_schema.COLLATIONS WHERE COLLATION_NAME = 'uca1400_ai_ci' AND CHARACTER_SET_NAME IS NULL";
            if (Convert.ToInt32(probe.ExecuteScalar()) == 0) Assert.Ignore("This MariaDB has no uca1400_ai_ci.");
        }
        var uid = Guid.NewGuid().ToString("N")[..8];
        var table = $"UcaTable_{uid}";
        var defs = $$"""
[
  {
    "Name": "{{table}}", "CharacterSet": "utf8mb4", "Collation": "uca1400_ai_ci",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "varchar(10)", "Nullable": true, "Collation": "uca1400_as_cs" }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ]
  }
]
""";
        using var cmd = probeConn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, defs, productName: $"UcaProduct_{uid}");
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: $"UcaProduct_{uid}");

            cmd.CommandText = $"SELECT GROUP_CONCAT(CONCAT(ObjectType, ' ', ObjectName, ' ', ActionType)) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '{table}%'";
            Assert.That(cmd.ExecuteScalar()?.ToString() ?? "", Is.Empty);
        }
        finally
        {
            DropTable(cmd, table);
        }
    }

    private static string TableWithColumnType(string table, string dataType) => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "{{dataType}}", "Nullable": true }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ]
  }
]
""";

    private void DropTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;";
        cmd.ExecuteNonQuery();
    }

    private void ClearChangeAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    private int ColumnChangesRecorded(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
 WHERE SessionId = CONNECTION_ID()
   AND ObjectType = 'column'
   AND ObjectName LIKE '{table}.%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
