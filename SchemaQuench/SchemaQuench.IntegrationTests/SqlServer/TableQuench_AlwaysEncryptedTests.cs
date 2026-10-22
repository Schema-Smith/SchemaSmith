// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Always Encrypted (AE) certification — what TableQuench actually does for encrypted columns.
///
/// CERTIFIED FINDINGS (2026-06-27, live SQL Server container, standard NON-enclave deployment).
/// Every claim below was reproduced against the container; the swap path lives in
/// Schema/Scripts/SqlServer/SchemaSmith.ModifiedTableQuench.sql (the @v_SwapColumnScript
/// LIKE '%ENCRYPTED WITH%' block, ~lines 702-711).
///
/// HARNESS CONTROL (passes): a static DETERMINISTIC column, populated through an
/// AE-enabled (Column Encryption Setting=Enabled), PARAMETERIZED insert and read back through an
/// AE-enabled connection, decrypts to the original. The harness is sound, so the failures below
/// are the product, not the test.
///
/// WHAT WORKS:
///   - New encrypted column on an EMPTY table via quench: column is created with the declared
///     CEK / ENCRYPTION_TYPE / algorithm (verified in sys.columns). Certified by
///     ShouldCreateNewEncryptedColumnOnEmptyTable below.
///   - DDL containing ENCRYPTED WITH executes fine over an AE-enabled connection on this engine
///     version — it does NOT parse-error. (Refutes the prior POC note; certified by
///     ShouldExecuteEncryptedWithDdlOverAeEnabledConnection.)
///
/// WHAT DOES NOT WORK — encryption CHANGES on a POPULATED table (re-type, re-key, or initial
/// encryption of plaintext). The quench raises a deliberate, explicit fail-closed guard error
/// (RAISERROR severity 16) naming the offending column and pointing to the Before/After rebuild
/// workaround. The column is left UNTOUCHED.
///
/// WHY IT CANNOT WORK (the fundamental limit on standard non-enclave servers):
///   - A NOT NULL encrypted column cannot be ADDed to a non-empty table (general SQL rule; no
///     DEFAULT is allowed on encrypted columns to escape it).
///   - A NULLABLE encrypted column CAN be added to a non-empty table on this engine version,
///     but the swap's data copy would be a SERVER-SIDE "UPDATE [temp] = [original]", and when
///     the two columns have different encryption settings the server rejects it:
///        "Operand type clash: nvarchar(50) encrypted with (encryption_type = 'DETERMINISTIC'...)
///         is incompatible with nvarchar(50) encrypted with (encryption_type = 'RANDOMIZED'...)."
///     The server holds no Column Master Key and cannot decrypt/re-encrypt; re-encryption requires
///     secure enclaves, which a standard deployment does not have.
///
/// NET: on a standard (non-enclave) server, an AE encryption CHANGE on populated data is
/// impossible to perform inside the quench. The guard (ModifiedTableQuench.sql, the ENCRYPTED WITH
/// sub-branch) raises immediately — before any DDL or data copy — naming the column and instructing
/// the operator to use a Before/After full-table rebuild. Certified by
/// ShouldBlockEncryptionTypeChange_OnPopulatedColumn and
/// ShouldBlockInitialEncryption_OnPopulatedColumn below.
///
/// WORKAROUND for users — full table rebuild via Before/After migration scripts:
///   Before: create the new table with the target (encrypted) schema; INSERT INTO new SELECT * FROM
///     old over a "Column Encryption Setting=Enabled" connection (the ADO.NET driver does the
///     decrypt/re-encrypt client-side); drop FKs/indexes/constraints on old; drop old; sp_rename new.
///   After:  recreate FKs/indexes/constraints/extended properties.
///   This is the same full-rebuild approach SSMS uses for Always Encrypted changes; run it in a
///   maintenance window (referential integrity is briefly broken).
/// </summary>
[TestFixture]
[Category("SqlServer")]
[NonParallelizable]
public class TableQuench_AlwaysEncryptedTests : BaseTableQuenchTests
{
    // AE-enabled connection targeting the integration main DB: the ADO.NET driver encrypts
    // parameters on insert and decrypts on read. Required for any data round-trip verification.
    private string AeConnectionString() =>
        new SqlConnectionStringBuilder(_connectionString)
        {
            InitialCatalog = _mainDb,
            ColumnEncryptionSetting = SqlConnectionColumnEncryptionSetting.Enabled
        }.ToString();

    private const string Cek = "[TestCEK]";
    private const string Algo = "AEAD_AES_256_CBC_HMAC_SHA_256";

    /// <summary>
    /// HARNESS CONTROL — proves the AE round-trip works before any swap scenario is judged.
    /// Static DETERMINISTIC column, parameterized encrypted insert, AE-enabled read-back.
    /// </summary>
    [Test]
    public void HarnessControl_StaticEncryptedColumn_RoundTripsThroughAeConnection()
    {
        using var conn = new SqlConnection(AeConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeControlRoundTrip";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = $@"
                CREATE TABLE dbo.AeControlRoundTrip (
                  Id INT NOT NULL,
                  Secret NVARCHAR(50) COLLATE Latin1_General_BIN2
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = {Cek}, ENCRYPTION_TYPE = DETERMINISTIC,
                                    ALGORITHM = '{Algo}') NOT NULL)";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "INSERT dbo.AeControlRoundTrip (Id, Secret) VALUES (@id, @secret)";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = 1 });
            cmd.Parameters.Add(new SqlParameter("@secret", SqlDbType.NVarChar, 50) { Value = "TopSecret" });
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();

            cmd.CommandText = "SELECT Secret FROM dbo.AeControlRoundTrip WHERE Id = 1";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("TopSecret"),
                "AE harness control: encrypted value must round-trip through the driver");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeControlRoundTrip";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Certifies the prior POC claim is false on this engine: ENCRYPTED WITH DDL does NOT
    /// parse-error when run over an AE-enabled connection.
    /// </summary>
    [Test]
    public void ShouldExecuteEncryptedWithDdlOverAeEnabledConnection()
    {
        using var conn = new SqlConnection(AeConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeDdlOverAe";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = $@"
                CREATE TABLE dbo.AeDdlOverAe (
                  Id INT NOT NULL,
                  Secret NVARCHAR(50) COLLATE Latin1_General_BIN2
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = {Cek}, ENCRYPTION_TYPE = RANDOMIZED,
                                    ALGORITHM = '{Algo}') NULL)";
            Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(),
                "ENCRYPTED WITH DDL must not parse-error over an AE-enabled connection");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeDdlOverAe";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// SCENARIO A (works): quench that adds a new ENCRYPTED column to an EMPTY table creates it
    /// with the declared CEK / type / algorithm, and the column round-trips encrypted data.
    /// </summary>
    [Test]
    public void ShouldCreateNewEncryptedColumnOnEmptyTable()
    {
        using var conn = new SqlConnection(AeConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeNewColEmpty";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = "CREATE TABLE dbo.AeNewColEmpty (Id INT NOT NULL)";
            cmd.ExecuteNonQuery();

            var json = """
                {
                    "Schema": "[dbo]",
                    "Name": "[AeNewColEmpty]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Secret]", "DataType": "NVARCHAR(50)", "Nullable": false, "Collation": "Latin1_General_BIN2",
                         "EncryptionType": "DETERMINISTIC", "EncryptionKey": "[TestCEK]", "EncryptionAlgorithm": "AEAD_AES_256_CBC_HMAC_SHA_256"}
                    ]
                }
                """;
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = @"SELECT encryption_type_desc, encryption_algorithm_name
                                FROM sys.columns WHERE object_id = OBJECT_ID('dbo.AeNewColEmpty') AND name = 'Secret'";
            using (var r = cmd.ExecuteReader())
            {
                Assert.That(r.Read(), Is.True, "encrypted column should exist");
                Assert.That(r.GetString(0), Is.EqualTo("DETERMINISTIC"));
                Assert.That(r.GetString(1), Is.EqualTo(Algo));
            }

            // And it must actually round-trip encrypted data.
            cmd.CommandText = "INSERT dbo.AeNewColEmpty (Id, Secret) VALUES (@id, @secret)";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = 1 });
            cmd.Parameters.Add(new SqlParameter("@secret", SqlDbType.NVarChar, 50) { Value = "NewColSecret" });
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();

            cmd.CommandText = "SELECT Secret FROM dbo.AeNewColEmpty WHERE Id = 1";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("NewColSecret"));
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeNewColEmpty";
            cmd.ExecuteNonQuery();
        }
    }

    private static string AeTable(string table, bool nullable = true, string key = "[TestCEK]") => $$"""
        { "Schema": "[dbo]", "Name": "[{{table}}]",
          "Columns": [ {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                       {"Name": "[Secret]", "DataType": "NVARCHAR(50)", "Nullable": {{(nullable ? "true" : "false")}}, "Collation": "Latin1_General_BIN2",
                        "EncryptionType": "DETERMINISTIC", "EncryptionKey": "{{key}}", "EncryptionAlgorithm": "AEAD_AES_256_CBC_HMAC_SHA_256"} ],
          "Indexes": [ {"Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]"} ] }
        """;

    private void WithAeTable(string table, System.Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{table}";
        cmd.ExecuteNonQuery();
        try { body(cmd); }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    // The deploy read the database a key lives in (NULL for a local key) and compared it with the declared key's name,
    // so every encrypted column looked changed and every redeploy failed on the ALTER COLUMN that followed.
    [Test]
    public void AnUnchangedEncryptedColumn_RedeploysWithoutTouchingIt()
    {
        WithAeTable("AeRedeploy", cmd =>
        {
            RunTableQuenchProc(cmd, AeTable("AeRedeploy"));
            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, AeTable("AeRedeploy"));
            cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%AeRedeploy%'";
            Assert.That(System.Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "a redeploy changes nothing");
        });
    }

    // ALTER COLUMN takes ENCRYPTED WITH before NULL / NOT NULL; the deploy wrote it after, a syntax error.
    [Test]
    public void ANullabilityChange_OnAnEncryptedColumn_IsApplied()
    {
        WithAeTable("AeNullability", cmd =>
        {
            RunTableQuenchProc(cmd, AeTable("AeNullability"));
            RunTableQuenchProc(cmd, AeTable("AeNullability", nullable: false));
            cmd.CommandText = "SELECT CAST(is_nullable AS INT) * 10 + CASE WHEN encryption_type_desc = 'DETERMINISTIC' THEN 1 ELSE 0 END FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.AeNullability') AND [name] = 'Secret'";
            Assert.That(System.Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "NOT NULL now, and still encrypted");
        });
    }

    // Another key is an encryption change the server cannot make: refused by name, like a change of type.
    [Test]
    public void AnotherKey_OnAnEncryptedColumn_IsRefusedByName()
    {
        WithAeTable("AeRekey", cmd =>
        {
            cmd.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE [name] = N'TestCEK_Other')
                  CREATE COLUMN ENCRYPTION KEY [TestCEK_Other] WITH VALUES (COLUMN_MASTER_KEY = [TestCMK], ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x016E0000016300)
                """;
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, AeTable("AeRekey"));
            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, AeTable("AeRekey", key: "[TestCEK_Other]")));
            Assert.That(ex!.Message, Does.Contain("in-place encryption change on [dbo].[AeRekey].[Secret]").And.Contain("Before/After"), "the guard, before any DDL");
            cmd.CommandText = "SELECT k.[name] FROM sys.columns c JOIN sys.column_encryption_keys k ON k.column_encryption_key_id = c.column_encryption_key_id WHERE c.[object_id] = OBJECT_ID('dbo.AeRekey') AND c.[name] = 'Secret'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("TestCEK"), "the column is left as it is");
        });
    }

    // Decrypting is the same server-side impossibility in the other direction: refused by name, the column left as it is.
    [Test]
    public void RemovingEncryption_FromAnEncryptedColumn_IsRefusedByName()
    {
        WithAeTable("AeDecrypt", cmd =>
        {
            RunTableQuenchProc(cmd, AeTable("AeDecrypt"));
            var plain = AeTable("AeDecrypt").Replace("\"EncryptionType\": \"DETERMINISTIC\", ", "");
            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, plain));
            Assert.That(ex!.Message, Does.Contain("in-place encryption change on [dbo].[AeDecrypt].[Secret]").And.Contain("Before/After"), "the guard, before any DDL");
            cmd.CommandText = "SELECT encryption_type_desc FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.AeDecrypt') AND [name] = 'Secret'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("DETERMINISTIC"), "the column is left as it is");
        });
    }

    [Test]
    public void ShouldNotAlterSwappedColumnsRedundantly()
    {
        // The "Alter Modified Columns" section must skip rows where MustSwapColumn = 1.
        // Without this fix, columns handled by the swap cursor would ALSO get an ALTER COLUMN
        // which is a no-op for identity removal but fails for encrypted columns.
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE dbo.AESwapSkipTest (Id INT IDENTITY(1,1) NOT NULL, Val NVARCHAR(50) NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT dbo.AESwapSkipTest (Val) VALUES ('test')";
        cmd.ExecuteNonQuery();

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[AESwapSkipTest]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(50)"}
                ]
            }
            """;
        RunTableQuenchProc(cmd, json);

        Assert.That(GetColumnDataType(cmd, "AESwapSkipTest", "Id"), Is.EqualTo("INT"));
        cmd.CommandText = "SELECT Val FROM dbo.AESwapSkipTest";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("test"));

        cmd.CommandText = "DROP TABLE dbo.AESwapSkipTest";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void GenerateTableJson_AeColumn_MapsKeyAndAlgorithmCorrectly()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            CREATE TABLE dbo.AeRoundTrip (
              Id INT NOT NULL,
              Secret NVARCHAR(50) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = [TestCEK], ENCRYPTION_TYPE = DETERMINISTIC,
                                ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL)";
        cmd.ExecuteNonQuery();

        try
        {
            cmd.CommandText = "EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = 'AeRoundTrip'";
            var json = ReadAllLines(cmd);

            Assert.That(json, Does.Contain("\"EncryptionType\": \"DETERMINISTIC\""));
            Assert.That(json, Does.Contain("\"EncryptionKey\": \"[TestCEK]\""));
            Assert.That(json, Does.Contain("\"EncryptionAlgorithm\": \"AEAD_AES_256_CBC_HMAC_SHA_256\""));
        }
        finally
        {
            cmd.CommandText = "DROP TABLE dbo.AeRoundTrip";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    /// <summary>
    /// SCENARIO B (blocked): changing encryption type (DETERMINISTIC -> RANDOMIZED) on a POPULATED
    /// column raises a fail-closed guard error naming the column and pointing to the rebuild path.
    /// The column is left UNTOUCHED (original value still decrypts).
    /// </summary>
    [Test]
    public void ShouldBlockEncryptionTypeChange_OnPopulatedColumn()
    {
        using var conn = new SqlConnection(AeConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockTypeChange";
        cmd.ExecuteNonQuery();
        try
        {
            // Create table with DETERMINISTIC column
            cmd.CommandText = $@"
                CREATE TABLE dbo.AeBlockTypeChange (
                  Id INT NOT NULL,
                  Secret NVARCHAR(50) COLLATE Latin1_General_BIN2
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = {Cek}, ENCRYPTION_TYPE = DETERMINISTIC,
                                    ALGORITHM = '{Algo}') NOT NULL)";
            cmd.ExecuteNonQuery();

            // Seed a row so the table is populated
            cmd.CommandText = "INSERT dbo.AeBlockTypeChange (Id, Secret) VALUES (@id, @secret)";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = 1 });
            cmd.Parameters.Add(new SqlParameter("@secret", SqlDbType.NVarChar, 50) { Value = "DetSecret" });
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();

            // Quench: change DETERMINISTIC -> RANDOMIZED (AE encryption type change on populated column)
            var json = $$$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[AeBlockTypeChange]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Secret]", "DataType": "NVARCHAR(50)", "Nullable": false, "Collation": "Latin1_General_BIN2",
                         "EncryptionType": "RANDOMIZED", "EncryptionKey": "[TestCEK]", "EncryptionAlgorithm": "{{{Algo}}}"}
                    ]
                }
                """;

            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json));
            Assert.That(ex!.Message, Does.Contain("Always Encrypted").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("Secret").IgnoreCase,
                "guard message must name the offending column");
            Assert.That(ex.Message, Does.Contain("rebuild").IgnoreCase,
                "guard message must point to the Before/After rebuild workaround");
            Assert.That(ex.Message, Does.Contain("[dbo].[AeBlockTypeChange]"),
                "guard message must render schema/table with single brackets, not double");

            // Column must be left UNTOUCHED — still DETERMINISTIC, original value still decrypts
            cmd.CommandText = "SELECT Secret FROM dbo.AeBlockTypeChange WHERE Id = 1";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("DetSecret"),
                "original encrypted value must survive the blocked quench attempt");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockTypeChange";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ShouldBlockEncryptionTypeChange_BeforeAnyOtherTableIsTouched()
    {
        // #511: the guard used to fire from inside the column swap, after the indexes over every changing column in the
        // deploy had been dropped. A plain table sharing the deploy keeps its key and its column.
        using var conn = new SqlConnection(AeConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockOrder; DROP TABLE IF EXISTS dbo.AeBlockOrderPlain";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = $@"
                CREATE TABLE dbo.AeBlockOrder (
                  Id INT NOT NULL,
                  Secret NVARCHAR(50) COLLATE Latin1_General_BIN2
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = {Cek}, ENCRYPTION_TYPE = DETERMINISTIC,
                                    ALGORITHM = '{Algo}') NOT NULL);
                CREATE TABLE dbo.AeBlockOrderPlain (Id INT NOT NULL CONSTRAINT PK_AeBlockOrderPlain PRIMARY KEY, Val NVARCHAR(10) NULL)";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT dbo.AeBlockOrder (Id, Secret) VALUES (@id, @secret)";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = 1 });
            cmd.Parameters.Add(new SqlParameter("@secret", SqlDbType.NVarChar, 50) { Value = "DetSecret" });
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();

            var json = $$$"""
                [
                  {
                    "Schema": "[dbo]",
                    "Name": "[AeBlockOrder]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Secret]", "DataType": "NVARCHAR(50)", "Nullable": false, "Collation": "Latin1_General_BIN2",
                         "EncryptionType": "RANDOMIZED", "EncryptionKey": "[TestCEK]", "EncryptionAlgorithm": "{{{Algo}}}"}
                    ]
                  },
                  {
                    "Schema": "[dbo]",
                    "Name": "[AeBlockOrderPlain]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "BIGINT", "Nullable": false},
                        {"Name": "[Val]", "DataType": "NVARCHAR(10)", "Nullable": true}
                    ],
                    "Indexes": [{"Name": "[PK_AeBlockOrderPlain]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]"}]
                  }
                ]
                """;

            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json));
            cmd.CommandText = "SELECT CONVERT(VARCHAR(1), (SELECT COUNT(*) FROM sys.key_constraints WHERE [name] = 'PK_AeBlockOrderPlain')) + ',' + (SELECT TYPE_NAME(system_type_id) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.AeBlockOrderPlain') AND [name] = 'Id')";
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("Always Encrypted").IgnoreCase);
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo("1,int"), "the plain table's key and column are untouched: the guard fired before the first drop");
            });
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockOrder; DROP TABLE IF EXISTS dbo.AeBlockOrderPlain";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// SCENARIO C (blocked): initial encryption of a populated PLAINTEXT column raises the same
    /// fail-closed guard error. The column is left plaintext and UNTOUCHED.
    /// </summary>
    [Test]
    public void ShouldBlockInitialEncryption_OnPopulatedColumn()
    {
        using var conn = new SqlConnection(_connectionString + ";Initial Catalog=" + _mainDb);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockInitialEncrypt";
        cmd.ExecuteNonQuery();
        try
        {
            // Create table with a plaintext column
            cmd.CommandText = @"
                CREATE TABLE dbo.AeBlockInitialEncrypt (
                  Id INT NOT NULL,
                  Secret NVARCHAR(50) NOT NULL)";
            cmd.ExecuteNonQuery();

            // Seed a row so the table is populated
            cmd.CommandText = "INSERT dbo.AeBlockInitialEncrypt (Id, Secret) VALUES (1, 'PlainSecret')";
            cmd.ExecuteNonQuery();

            // Quench: add DETERMINISTIC encryption to the plaintext column (initial encryption on populated data)
            var json = $$$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[AeBlockInitialEncrypt]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Secret]", "DataType": "NVARCHAR(50)", "Nullable": false,
                         "EncryptionType": "DETERMINISTIC", "EncryptionKey": "[TestCEK]", "EncryptionAlgorithm": "{{{Algo}}}"}
                    ]
                }
                """;

            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json));
            Assert.That(ex!.Message, Does.Contain("Always Encrypted").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("Secret").IgnoreCase,
                "guard message must name the offending column");
            Assert.That(ex.Message, Does.Contain("rebuild").IgnoreCase,
                "guard message must point to the Before/After rebuild workaround");
            Assert.That(ex.Message, Does.Contain("[dbo].[AeBlockInitialEncrypt]"),
                "guard message must render schema/table with single brackets, not double");

            // Column must be left UNTOUCHED — still plaintext, original value intact
            cmd.CommandText = "SELECT Secret FROM dbo.AeBlockInitialEncrypt WHERE Id = 1";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("PlainSecret"),
                "original plaintext value must survive the blocked quench attempt");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockInitialEncrypt";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// SCENARIO D (WhatIf): the fail-closed guard is unconditional — it fires in WhatIf mode too,
    /// so the impossibility is visible in previews before any DDL runs.
    /// </summary>
    [Test]
    public void ShouldBlockEncryptionTypeChange_InWhatIfMode()
    {
        using var conn = new SqlConnection(AeConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockTypeChangeWhatIf";
        cmd.ExecuteNonQuery();
        try
        {
            cmd.CommandText = $@"
                CREATE TABLE dbo.AeBlockTypeChangeWhatIf (
                  Id INT NOT NULL,
                  Secret NVARCHAR(50) COLLATE Latin1_General_BIN2
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = {Cek}, ENCRYPTION_TYPE = DETERMINISTIC,
                                    ALGORITHM = '{Algo}') NOT NULL)";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "INSERT dbo.AeBlockTypeChangeWhatIf (Id, Secret) VALUES (@id, @secret)";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = 1 });
            cmd.Parameters.Add(new SqlParameter("@secret", SqlDbType.NVarChar, 50) { Value = "WhatIfSecret" });
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();

            var json = $$$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[AeBlockTypeChangeWhatIf]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Secret]", "DataType": "NVARCHAR(50)", "Nullable": false, "Collation": "Latin1_General_BIN2",
                         "EncryptionType": "RANDOMIZED", "EncryptionKey": "[TestCEK]", "EncryptionAlgorithm": "{{{Algo}}}"}
                    ]
                }
                """;

            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, json, whatIf: true));
            Assert.That(ex!.Message, Does.Contain("Always Encrypted").IgnoreCase,
                "guard must fire in WhatIf mode");
            Assert.That(ex.Message, Does.Contain("[dbo].[AeBlockTypeChangeWhatIf]"),
                "WhatIf guard message must render schema/table with single brackets");

            // Column is untouched — still DETERMINISTIC, original value still decrypts
            cmd.CommandText = "SELECT Secret FROM dbo.AeBlockTypeChangeWhatIf WHERE Id = 1";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("WhatIfSecret"),
                "original encrypted value must survive the WhatIf guard");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS dbo.AeBlockTypeChangeWhatIf";
            cmd.ExecuteNonQuery();
        }
    }

    private static string ReadAllLines(IDbCommand cmd)
    {
        var sb = new StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sb.Append(reader.GetString(0));
        return sb.ToString();
    }
}
