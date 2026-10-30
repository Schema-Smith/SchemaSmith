// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer
{
    // UnsupportedFeaturePolicy bakes the resolved policy ('warn' default | 'fail') into its body at KINDLE
    // time from Target:UnsupportedFeaturePolicy (the SS-2008 floor dropped the 2016+ SESSION_CONTEXT
    // transport, unavailable on a genuine pre-2016 binary). Any value other than an explicit 'fail'
    // resolves to the safe 'warn' default.
    [TestFixture]
    [Category("SqlServer")]
    public class UnsupportedFeaturePolicyTests : BakedKindleTestBase
    {
        [Test]
        public void UnsupportedFeaturePolicy_DefaultsToWarn_WhenNothingBaked()
        {
            // _mainDb is kindled by FixtureSetup with the default policy ('warn').
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "SELECT SchemaSmith.UnsupportedFeaturePolicy()";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("warn"));
        }

        [Test]
        public void UnsupportedFeaturePolicy_ReturnsFail_WhenKindledWithFail()
        {
            using var conn = KindleScratchDatabase("PolicyFailBake", policy: "fail");
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "SELECT SchemaSmith.UnsupportedFeaturePolicy()";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("fail"));
        }

        [Test]
        public void UnsupportedFeaturePolicy_ResolvesToWarn_ForAnyNonFailValue()
        {
            // The function's defensive CASE resolves anything other than an explicit 'fail' to 'warn' even if
            // an un-normalized value were ever baked (the C# caller normalizes to 'warn'/'fail' beforehand).
            using var conn = KindleScratchDatabase("PolicyBogusBake", policy: "bogus");
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "SELECT SchemaSmith.UnsupportedFeaturePolicy()";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("warn"));
        }

        // ---------------------------------------------------------------------------------------------------
        // Temporal (SYSTEM_VERSIONING / PERIOD FOR SYSTEM_TIME) — SQL Server 2016 (major 13). Below the floor
        // the turn-on emit (MissingIndexesAndConstraintsQuench) is suppressed, so a declared temporal table
        // deploys as a plain table (warn, default) or aborts (fail) instead of hard-failing on the 2016-only
        // DDL. The scratch DB bakes fn_ServerMajorVersion() = 10 (SQL 2008 R2) to force the < 13 branch on the
        // modern container — the SS analogue of PostgreSQL's schemasmith.version_override.
        // ---------------------------------------------------------------------------------------------------

        private const string TemporalObjectType = "temporal (SQL Server 2016)";

        private static string TemporalTableJson(string tableName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "IsTemporal": true,
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false, "PrimaryKey": true},
        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": false}
    ]
}
""";

        private static int TableTemporalType(IDbCommand cmd, string tableName)
        {
            cmd.CommandText = $"SELECT CAST(OBJECTPROPERTY(OBJECT_ID('dbo.{tableName}'), 'TableTemporalType') AS INT)";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static int DowngradeRowCount(IDbCommand cmd, string objectType, string objectName)
        {
            cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit
                                 WHERE ActionType = 'downgraded'
                                   AND ObjectType = '{objectType}'
                                   AND ObjectName = '{objectName}'";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // warn (default): a table declared temporal on a < 2016 target is created as a plain (non-temporal)
        // table, the SYSTEM_VERSIONING/PERIOD emit suppressed, and a downgrade manifest row names the table.
        [Test]
        public void Temporal_BelowSql2016_WarnPolicy_DeploysNonTemporal_AndRecordsDowngrade()
        {
            var tableName = $"WarnTemporal_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("TemporalWarnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, TemporalTableJson(tableName), productName: tableName),
                "a temporal table must degrade (emit suppressed) below SQL Server 2016, not hard-fail on SYSTEM_VERSIONING");

            cmd.CommandText = $"SELECT OBJECT_ID('dbo.{tableName}')";
            Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value), "the table must still be created");
            Assert.That(TableTemporalType(cmd, tableName), Is.EqualTo(0), "the table must be plain (non-temporal) below 2016");
            Assert.That(DowngradeRowCount(cmd, TemporalObjectType, $"[dbo].[{tableName}]"), Is.EqualTo(1),
                "a downgrade manifest row must name the table that lost temporal tracking");
        }

        // No phantom churn: a second quench of the same temporal-declared table on a < 2016 target must not
        // error and must leave the table plain (the turn-on stays suppressed; nothing re-detects it modified).
        [Test]
        public void Temporal_BelowSql2016_WarnPolicy_SecondQuench_StaysNonTemporal()
        {
            var tableName = $"NoChurnTemporal_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("TemporalChurnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, TemporalTableJson(tableName), productName: tableName);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, TemporalTableJson(tableName), productName: tableName),
                "a repeat quench below 2016 must stay idempotent");
            Assert.That(TableTemporalType(cmd, tableName), Is.EqualTo(0), "the table must remain plain after a second quench");
        }

        // fail (opt-in): a < 2016 target with a declared temporal table aborts with a clear "requires SQL
        // Server 2016" message rather than silently degrading.
        [Test]
        public void Temporal_BelowSql2016_FailPolicy_AbortsWithRequiresSql2016()
        {
            var tableName = $"FailTemporal_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("TemporalFailBake", serverMajorVersion: 10, policy: "fail");
            using var cmd = conn.CreateCommand();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, TemporalTableJson(tableName), productName: tableName));
            Assert.That(ex!.Message, Does.Contain("requires SQL Server 2016"),
                "the fail policy must abort naming the required version");
        }

        // ---------------------------------------------------------------------------------------------------
        // Dynamic data masking (MASKED WITH) — SQL Server 2016 (major 13). Below the floor the column emit
        // (CREATE + ALTER paths) is suppressed and the modified-column detection ignores the mask diff, so a
        // masked column deploys unmasked (warn) or the quench aborts (fail) instead of hard-failing on the
        // 2016-only clause.
        // ---------------------------------------------------------------------------------------------------

        private const string DataMaskingObjectType = "data masking (SQL Server 2016)";

        private static string MaskedTableJson(string tableName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Email]", "DataType": "NVARCHAR(200)", "Nullable": false, "DataMaskFunction": "email()"}
    ]
}
""";

        private static int MaskedColumnCount(IDbCommand cmd, string tableName)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.masked_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // warn (default): a masked column on a < 2016 target is created unmasked, the MASKED WITH emit
        // suppressed, and a downgrade manifest row names the column.
        [Test]
        public void DataMasking_BelowSql2016_WarnPolicy_DeploysUnmasked_AndRecordsDowngrade()
        {
            var tableName = $"WarnMask_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("MaskWarnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, MaskedTableJson(tableName), productName: tableName),
                "a masked column must degrade (emit suppressed) below SQL Server 2016, not hard-fail on MASKED WITH");

            cmd.CommandText = $"SELECT OBJECT_ID('dbo.{tableName}')";
            Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value), "the table must still be created");
            Assert.That(MaskedColumnCount(cmd, tableName), Is.EqualTo(0), "no column may be masked below 2016");
            Assert.That(DowngradeRowCount(cmd, DataMaskingObjectType, $"[dbo].[{tableName}].[Email]"), Is.EqualTo(1),
                "a downgrade manifest row must name the column that lost masking");
        }

        // No phantom churn: a second quench of the same masked-declared column on a < 2016 target must not
        // error and must leave the column unmasked (the mask diff is ignored in modified-column detection).
        [Test]
        public void DataMasking_BelowSql2016_WarnPolicy_SecondQuench_StaysUnmasked()
        {
            var tableName = $"NoChurnMask_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("MaskChurnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, MaskedTableJson(tableName), productName: tableName);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, MaskedTableJson(tableName), productName: tableName),
                "a repeat quench below 2016 must stay idempotent");
            Assert.That(MaskedColumnCount(cmd, tableName), Is.EqualTo(0), "the column must remain unmasked after a second quench");
        }

        // fail (opt-in): a < 2016 target with a declared masked column aborts with "requires SQL Server 2016".
        [Test]
        public void DataMasking_BelowSql2016_FailPolicy_AbortsWithRequiresSql2016()
        {
            var tableName = $"FailMask_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("MaskFailBake", serverMajorVersion: 10, policy: "fail");
            using var cmd = conn.CreateCommand();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, MaskedTableJson(tableName), productName: tableName));
            Assert.That(ex!.Message, Does.Contain("requires SQL Server 2016"),
                "the fail policy must abort naming the required version");
        }

        // ---------------------------------------------------------------------------------------------------
        // Always Encrypted (ENCRYPTED WITH) — SQL Server 2016 (major 13). Below the floor the column emit is
        // suppressed and the encryption diff is ignored in modified-column detection (so no swap-guard trip),
        // so an encrypted column deploys plaintext (warn) or the quench aborts (fail). The below-13 case never
        // references the CEK, so the throwaway kindle DB needs no Always Encrypted key infrastructure.
        // ---------------------------------------------------------------------------------------------------

        private const string AlwaysEncryptedObjectType = "Always Encrypted (SQL Server 2016)";

        private static string EncryptedTableJson(string tableName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[SSN]", "DataType": "NVARCHAR(11)", "Nullable": false,
         "EncryptionType": "DETERMINISTIC", "EncryptionKey": "[TestCEK]", "EncryptionAlgorithm": "AEAD_AES_256_CBC_HMAC_SHA_256"}
    ]
}
""";

        private static int EncryptedColumnCount(IDbCommand cmd, string tableName)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND encryption_type IS NOT NULL";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // warn (default): an encrypted column on a < 2016 target is created plaintext, the ENCRYPTED WITH emit
        // suppressed (the CEK is never referenced), and a downgrade manifest row names the column.
        [Test]
        public void AlwaysEncrypted_BelowSql2016_WarnPolicy_DeploysPlaintext_AndRecordsDowngrade()
        {
            var tableName = $"WarnEnc_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("EncWarnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, EncryptedTableJson(tableName), productName: tableName),
                "an encrypted column must degrade (emit suppressed) below SQL Server 2016, not hard-fail on ENCRYPTED WITH");

            cmd.CommandText = $"SELECT OBJECT_ID('dbo.{tableName}')";
            Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value), "the table must still be created");
            Assert.That(EncryptedColumnCount(cmd, tableName), Is.EqualTo(0), "no column may be encrypted below 2016");
            Assert.That(DowngradeRowCount(cmd, AlwaysEncryptedObjectType, $"[dbo].[{tableName}].[SSN]"), Is.EqualTo(1),
                "a downgrade manifest row must name the column that lost encryption");
        }

        // No phantom churn / no swap-guard trip: a second quench of the same encrypted-declared column on a
        // < 2016 target must not error and must leave the column plaintext (the encryption diff is ignored in
        // modified-column detection, so MustSwapColumn is never set and the AE fail-closed guard is not reached).
        [Test]
        public void AlwaysEncrypted_BelowSql2016_WarnPolicy_SecondQuench_StaysPlaintext()
        {
            var tableName = $"NoChurnEnc_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("EncChurnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, EncryptedTableJson(tableName), productName: tableName);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, EncryptedTableJson(tableName), productName: tableName),
                "a repeat quench below 2016 must stay idempotent (no swap-guard trip)");
            Assert.That(EncryptedColumnCount(cmd, tableName), Is.EqualTo(0), "the column must remain plaintext after a second quench");
        }

        // fail (opt-in): a < 2016 target with a declared encrypted column aborts with "requires SQL Server 2016".
        [Test]
        public void AlwaysEncrypted_BelowSql2016_FailPolicy_AbortsWithRequiresSql2016()
        {
            var tableName = $"FailEnc_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("EncFailBake", serverMajorVersion: 10, policy: "fail");
            using var cmd = conn.CreateCommand();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, EncryptedTableJson(tableName), productName: tableName));
            Assert.That(ex!.Message, Does.Contain("requires SQL Server 2016"),
                "the fail policy must abort naming the required version");
        }

        // ---------------------------------------------------------------------------------------------------
        // Columnstore indexes — NONCLUSTERED requires SQL Server 2012 (major 11), CLUSTERED requires 2014
        // (major 12). Below its intro version a columnstore index cannot exist, so it is dropped from the
        // working set entirely (not just clause-suppressed): the table deploys without the index (warn) or the
        // quench aborts (fail). Baking major 11 proves the nonclustered/clustered split -- an NCCI is created
        // while a CCI still degrades.
        // ---------------------------------------------------------------------------------------------------

        private const string ColumnStoreObjectType = "columnstore index (SQL Server 2012/2014)";

        private static string ClusteredColumnStoreJson(string tableName, string indexName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": false}
    ],
    "Indexes": [
        {"Name": "[{{indexName}}]", "Clustered": true, "ColumnStore": true, "PrimaryKey": false, "Unique": false}
    ]
}
""";

        private static string NonclusteredColumnStoreJson(string tableName, string indexName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": false}
    ],
    "Indexes": [
        {"Name": "[{{indexName}}]", "Clustered": false, "ColumnStore": true, "PrimaryKey": false, "Unique": false, "IncludeColumns": "[Val]"}
    ]
}
""";

        // sys.indexes.type: 5 = clustered columnstore, 6 = nonclustered columnstore.
        private static int ColumnStoreIndexCount(IDbCommand cmd, string tableName)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [type] IN (5, 6)";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // warn (default): a clustered columnstore index on a < 2014 target is skipped (the table deploys as a
        // rowstore heap) and a downgrade manifest row names the index.
        [Test]
        public void ColumnStore_BelowSql2014_WarnPolicy_SkipsIndex_AndRecordsDowngrade()
        {
            var tableName = $"WarnCci_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"cci_{tableName}";
            using var conn = KindleScratchDatabase("CciWarnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, ClusteredColumnStoreJson(tableName, indexName), productName: tableName),
                "a columnstore index must degrade (skipped) below its intro version, not hard-fail on COLUMNSTORE");

            cmd.CommandText = $"SELECT OBJECT_ID('dbo.{tableName}')";
            Assert.That(cmd.ExecuteScalar(), Is.Not.EqualTo(DBNull.Value), "the table must still be created");
            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(0), "no columnstore index may exist below the floor");
            Assert.That(DowngradeRowCount(cmd, ColumnStoreObjectType, $"[dbo].[{tableName}].[{indexName}]"), Is.EqualTo(1),
                "a downgrade manifest row must name the skipped columnstore index");
        }

        // No phantom churn: a second quench of the same columnstore-declared table on a below-floor target must
        // not error and must still have no columnstore index (it was dropped from the working set, not left
        // "missing" every run).
        [Test]
        public void ColumnStore_BelowSql2014_WarnPolicy_SecondQuench_StaysSkipped()
        {
            var tableName = $"NoChurnCci_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"cci_{tableName}";
            using var conn = KindleScratchDatabase("CciChurnBake", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, ClusteredColumnStoreJson(tableName, indexName), productName: tableName);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, ClusteredColumnStoreJson(tableName, indexName), productName: tableName),
                "a repeat quench below the floor must stay idempotent");
            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(0), "the table must remain columnstore-free after a second quench");
        }

        // fail (opt-in): a below-floor target with a declared columnstore index aborts naming the required version.
        [Test]
        public void ColumnStore_BelowSql2014_FailPolicy_Aborts()
        {
            var tableName = $"FailCci_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"cci_{tableName}";
            using var conn = KindleScratchDatabase("CciFailBake", serverMajorVersion: 10, policy: "fail");
            using var cmd = conn.CreateCommand();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, ClusteredColumnStoreJson(tableName, indexName), productName: tableName));
            Assert.That(ex!.Message, Does.Contain("Columnstore indexes require SQL Server 2012"),
                "the fail policy must abort naming the required version");
        }

        // A NONCLUSTERED columnstore exists from 2012, but on 2012 and 2014 it makes its table read-only, so creating it
        // would change what the application can do. It is skipped there and created from 2016 (major 13). This replaces
        // a test that asserted creation at major 11, which pinned the read-only outcome.
        private const string NcciReadOnlyObjectType = "nonclustered columnstore index (writable from SQL Server 2016)";

        [TestCase(11)]
        [TestCase(12)]
        public void ColumnStore_Nonclustered_At2012And2014_IsSkipped_BecauseItWouldMakeTheTableReadOnly(int major)
        {
            var tableName = $"Ncci{major}_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"ncci_{tableName}";
            using var conn = KindleScratchDatabase("NcciBake", serverMajorVersion: major, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, NonclusteredColumnStoreJson(tableName, indexName), productName: tableName);

            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(0));
            Assert.That(DowngradeRowCount(cmd, NcciReadOnlyObjectType, $"[dbo].[{tableName}].[{indexName}]"), Is.EqualTo(1));
        }

        // The read-only rule only stops SchemaSmith creating the index. One already on the table was made on purpose on
        // this server; dropping it from the working set would let a later pass remove it.
        [Test]
        public void ColumnStore_Nonclustered_At2014_AlreadyOnTheTable_IsKept()
        {
            var tableName = $"NcciKeep_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"ncci_{tableName}";
            using var conn = KindleScratchDatabase("NcciBake", serverMajorVersion: 12, policy: "warn");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE dbo.[{tableName}] (Id INT NOT NULL, Val NVARCHAR(100) NOT NULL); " +
                              $"CREATE NONCLUSTERED COLUMNSTORE INDEX [{indexName}] ON dbo.[{tableName}] (Val)";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, NonclusteredColumnStoreJson(tableName, indexName), productName: tableName);

            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(1));
            Assert.That(DowngradeRowCount(cmd, NcciReadOnlyObjectType, $"[dbo].[{tableName}].[{indexName}]"), Is.EqualTo(0));
        }

        // A columnstore index reports COLUMNSTORE compression, and a declaration that names none means exactly that. The
        // compression fix-up compared it with the declared default NONE and tried to rebuild the index to NONE, which the
        // engine refuses, so every redeploy of a table with a columnstore index failed.
        [TestCase(false)]
        [TestCase(true)]
        public void ColumnStore_AtSql2016_RedeploysCleanly(bool clustered)
        {
            var tableName = $"CsTwice_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"cs_{tableName}";
            var json = clustered ? ClusteredColumnStoreJson(tableName, indexName) : NonclusteredColumnStoreJson(tableName, indexName);
            using var conn = KindleScratchDatabase("NcciBake", serverMajorVersion: 13, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, json, productName: tableName);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: tableName));
            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(1));
        }

        [Test]
        public void ColumnStore_Nonclustered_AtSql2016_IsCreated()
        {
            var tableName = $"Ncci13_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"ncci_{tableName}";
            using var conn = KindleScratchDatabase("NcciBake", serverMajorVersion: 13, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, NonclusteredColumnStoreJson(tableName, indexName), productName: tableName);

            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(1));
        }

        // A CLUSTERED columnstore exists from 2014, but 2014 refuses one beside any rowstore index (35304). The rowstore
        // indexes carry keys and uniqueness, so the columnstore is the one skipped.
        private const string CciBesideRowstoreObjectType = "clustered columnstore beside rowstore indexes (SQL Server 2016)";

        private static string ClusteredColumnStoreBesideRowstoreJson(string tableName, string indexName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": false}
    ],
    "Indexes": [
        {"Name": "[{{indexName}}]", "Clustered": true, "ColumnStore": true, "PrimaryKey": false, "Unique": false},
        {"Name": "[ux_{{tableName}}]", "Clustered": false, "Unique": true, "IndexColumns": "[Id]"}
    ]
}
""";

        [Test]
        public void ColumnStore_Clustered_AtSql2014_Alone_IsCreated()
        {
            var tableName = $"Cci12_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("CciBake12", serverMajorVersion: 12, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, ClusteredColumnStoreJson(tableName, $"cci_{tableName}"), productName: tableName);

            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(1));
        }

        [Test]
        public void ColumnStore_Clustered_AtSql2014_BesideARowstoreIndex_IsSkipped_AndTheRowstoreIndexStays()
        {
            var tableName = $"CciNc12_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"cci_{tableName}";
            using var conn = KindleScratchDatabase("CciBake12", serverMajorVersion: 12, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, ClusteredColumnStoreBesideRowstoreJson(tableName, indexName), productName: tableName);

            var columnStores = ColumnStoreIndexCount(cmd, tableName);
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND name = 'ux_{tableName}'";
            var rowstore = Convert.ToInt32(cmd.ExecuteScalar());
            var downgrades = DowngradeRowCount(cmd, CciBesideRowstoreObjectType, $"[dbo].[{tableName}].[{indexName}]");
            Assert.Multiple(() =>
            {
                Assert.That(columnStores, Is.EqualTo(0));
                Assert.That(rowstore, Is.EqualTo(1), "the rowstore index must be created");
                Assert.That(downgrades, Is.EqualTo(1));
            });
        }

        // ---------------------------------------------------------------------------------------------------
        // Full-text STATISTICAL_SEMANTICS needs a registered semantic language statistics database; without
        // one SQL Server refuses the whole full-text index (41209). The demo container has full-text and no semantic
        // database, so this is the real refusal, not a simulation.
        // ---------------------------------------------------------------------------------------------------

        // ---------------------------------------------------------------------------------------------------
        // Without Full-Text Search installed (the stock Linux container images) SQL Server refuses every full-text
        // index. Every CI leg installs it, so the missing service is simulated through the CONTEXT_INFO override,
        // extended with an 'SSFT' marker
        // that SchemaSmith.fn_FullTextInstalled reads; the catalog named below does not exist, so any attempt to
        // build the index would fail.
        // ---------------------------------------------------------------------------------------------------

        private const string NoFullTextObjectType = "full-text index (Full-Text Search not installed)";

        // A degrade's line names what it applied to; a list too long for one message is cut, and says so.
        [Test]
        public void ADowngradeLine_NamesItsObjects_AndCutsAListTooLongForOneMessage()
        {
            using var conn = KindleScratchDatabase("NoFtsBake", policy: "warn");
            var messages = new System.Collections.Generic.List<string>();
            ((Microsoft.Data.SqlClient.SqlConnection)conn).InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().Select(x => x.Message));
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "EXEC SchemaSmith.PrintDowngrade N'  Thing skipped (downgraded)', N'[dbo].[A], [dbo].[B]'; " +
                              "EXEC SchemaSmith.PrintDowngrade N'  Many skipped (downgraded)', @l";
            var list = cmd.CreateParameter();
            list.ParameterName = "@l";
            list.Value = string.Join(", ", System.Linq.Enumerable.Range(1, 400).Select(i => $"[dbo].[Table{i:000}]"));
            cmd.Parameters.Add(list);
            cmd.ExecuteNonQuery();

            Assert.Multiple(() =>
            {
                Assert.That(messages, Does.Contain("  Thing skipped (downgraded): [dbo].[A], [dbo].[B]"));
                Assert.That(messages.Last(), Does.StartWith("  Many skipped (downgraded): [dbo].[Table001]").And.EndWith(", ... (the summary lists them all)"));
                Assert.That(messages.Last().Length, Is.LessThan(2048), "one message");
            });
        }

        // A full-text index the table already has is left alone without Full-Text Search: only one that would have to be
        // created is degraded. Degrading every declared index made an existing one read as removed, and dropped it.
        [Test]
        public void AnExistingFullTextIndex_WithoutFullTextSearch_IsKept_AndNotRecordedAsDowngraded()
        {
            var tableName = $"KeepFts_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("NoFtsBake", policy: "warn");
            using var cmd = conn.CreateCommand();
            var json = PlainFullTextJson(tableName).Replace("[ss_no_such_cat]", "[ss_keep_cat]");
            try
            {
                cmd.CommandText = "IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE [name] = 'ss_keep_cat') CREATE FULLTEXT CATALOG [ss_keep_cat]";
                cmd.ExecuteNonQuery();
                RunTableQuenchProc(cmd, json, productName: tableName);
                Assert.That(FullTextIndexCount(cmd, tableName), Is.EqualTo(1), "setup: the index exists");

                SimulateNoFullTextSearch(cmd);
                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: tableName));

                Assert.Multiple(() =>
                {
                    Assert.That(FullTextIndexCount(cmd, tableName), Is.EqualTo(1), "the existing full-text index is kept");
                    Assert.That(DowngradeRowCount(cmd, NoFullTextObjectType, $"[dbo].[{tableName}]"), Is.Zero, "nothing was skipped");
                });
            }
            finally
            {
                cmd.CommandText = "SET CONTEXT_INFO 0x0";
                cmd.ExecuteNonQuery();
            }
        }

        private static void SimulateNoFullTextSearch(IDbCommand cmd)
        {
            cmd.CommandText = "DECLARE @c VARBINARY(128) = 0x53534F56 + CONVERT(BINARY(4), CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))) + " +
                              "0x5353454400 + 0x5353465400; SET CONTEXT_INFO @c";
            cmd.ExecuteNonQuery();
        }

        private static string PlainFullTextJson(string tableName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Body]", "DataType": "NVARCHAR(MAX)", "Nullable": true}
    ],
    "Indexes": [
        {"Name": "[PK_{{tableName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]"}
    ],
    "FullTextIndex": {"FullTextCatalog": "[ss_no_such_cat]", "KeyIndex": "[PK_{{tableName}}]", "Columns": "[Body]"}
}
""";

        private static int FullTextIndexCount(IDbCommand cmd, string tableName)
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FullTextIndex_WithoutFullTextSearch_DeploysTheTableWithoutIt_AndRecordsTheDowngrade(bool indexOnly)
        {
            var tableName = $"NoFts_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("NoFtsBake", policy: "warn");
            using var cmd = conn.CreateCommand();
            try
            {
                if (indexOnly)
                {
                    cmd.CommandText = $"CREATE TABLE dbo.[{tableName}] (Id INT NOT NULL CONSTRAINT [PK_{tableName}] PRIMARY KEY CLUSTERED, Body NVARCHAR(MAX) NULL)";
                    cmd.ExecuteNonQuery();
                }
                SimulateNoFullTextSearch(cmd);

                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, PlainFullTextJson(tableName), indexOnly: indexOnly, productName: tableName));

                cmd.CommandText = $"SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
                var columns = Convert.ToInt32(cmd.ExecuteScalar());
                Assert.Multiple(() =>
                {
                    Assert.That(columns, Is.EqualTo(2), "the table deploys");
                    Assert.That(FullTextIndexCount(cmd, tableName), Is.Zero, "without its full-text index");
                    Assert.That(DowngradeRowCount(cmd, NoFullTextObjectType, $"[dbo].[{tableName}]"), Is.EqualTo(1));
                });
            }
            finally
            {
                cmd.CommandText = "SET CONTEXT_INFO 0x0";
                cmd.ExecuteNonQuery();
            }
        }

        [Test]
        public void FullTextIndex_WithoutFullTextSearch_UnderFail_RefusesBeforeCreatingTheTable()
        {
            var tableName = $"NoFtsF_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("NoFtsFailBake", policy: "fail");
            using var cmd = conn.CreateCommand();
            try
            {
                SimulateNoFullTextSearch(cmd);

                var ex = Assert.Catch(() => RunTableQuenchProc(cmd, PlainFullTextJson(tableName), productName: tableName));

                cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('dbo.{tableName}') IS NULL THEN 0 ELSE 1 END";
                var created = Convert.ToInt32(cmd.ExecuteScalar());
                Assert.Multiple(() =>
                {
                    Assert.That(ex!.Message, Does.Contain("Full-Text Search").And.Contain($"[dbo].[{tableName}]"));
                    Assert.That(created, Is.Zero, "refused before the table is created");
                });
            }
            finally
            {
                cmd.CommandText = "SET CONTEXT_INFO 0x0";
                cmd.ExecuteNonQuery();
            }
        }

        private const string SemanticsObjectType = "full-text statistical semantics (no semantic database)";

        private static string SemanticFullTextJson(string tableName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Body]", "DataType": "NVARCHAR(MAX)", "Nullable": true}
    ],
    "Indexes": [
        {"Name": "[PK_{{tableName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]"}
    ],
    "FullTextIndex": {"FullTextCatalog": "[ss_semantics_cat]", "KeyIndex": "[PK_{{tableName}}]", "Columns": "[Body] LANGUAGE 1033 STATISTICAL_SEMANTICS"}
}
""";

        private static void EnsureFullTextCatalog(IDbCommand cmd)
        {
            cmd.CommandText = "IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 0 SELECT -1 " +
                              "ELSE IF EXISTS (SELECT 1 FROM sys.fulltext_semantic_language_statistics_database) SELECT -2 " +
                              "ELSE BEGIN IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ss_semantics_cat') " +
                              "CREATE FULLTEXT CATALOG ss_semantics_cat; SELECT 0 END";
            var state = Convert.ToInt32(cmd.ExecuteScalar());
            if (state == -1) Assert.Ignore("Full-text search is not installed on this server.");
            if (state == -2) Assert.Ignore("A semantic language statistics database is registered on this server.");
        }

        [Test]
        public void StatisticalSemantics_WithoutASemanticDatabase_CreatesTheFullTextIndexWithoutIt_AndRecordsTheDowngrade()
        {
            var tableName = $"Sem_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("SemBake", policy: "warn");
            using var cmd = conn.CreateCommand();
            EnsureFullTextCatalog(cmd);

            RunTableQuenchProc(cmd, SemanticFullTextJson(tableName), productName: tableName);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, SemanticFullTextJson(tableName), productName: tableName));

            cmd.CommandText = $"SELECT COUNT(*) FROM sys.fulltext_index_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
            var columns = Convert.ToInt32(cmd.ExecuteScalar());
            var downgrades = DowngradeRowCount(cmd, SemanticsObjectType, $"[dbo].[{tableName}]");
            Assert.Multiple(() =>
            {
                Assert.That(columns, Is.EqualTo(1), "the full-text index must exist, without statistical semantics");
                Assert.That(downgrades, Is.EqualTo(2), "once per quench");
            });
        }

        // The --IndexOnly path parses full-text indexes itself, after its other index degrades, so it calls the
        // full-text degrade separately.
        [Test]
        public void StatisticalSemantics_WithoutASemanticDatabase_OnTheIndexOnlyPath_CreatesTheFullTextIndexWithoutIt()
        {
            var tableName = $"SemIo_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("SemBake", policy: "warn");
            using var cmd = conn.CreateCommand();
            EnsureFullTextCatalog(cmd);
            cmd.CommandText = $"CREATE TABLE dbo.[{tableName}] (Id INT NOT NULL CONSTRAINT [PK_{tableName}] PRIMARY KEY CLUSTERED, Body NVARCHAR(MAX) NULL)";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, SemanticFullTextJson(tableName), indexOnly: true, productName: tableName);

            cmd.CommandText = $"SELECT COUNT(*) FROM sys.fulltext_index_columns WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
            var columns = Convert.ToInt32(cmd.ExecuteScalar());
            var downgrades = DowngradeRowCount(cmd, SemanticsObjectType, $"[dbo].[{tableName}]");
            Assert.Multiple(() =>
            {
                Assert.That(columns, Is.EqualTo(1));
                Assert.That(downgrades, Is.EqualTo(1));
            });
        }

        [Test]
        public void StatisticalSemantics_WithoutASemanticDatabase_UnderFail_Aborts()
        {
            var tableName = $"SemFail_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("SemFailBake", policy: "fail");
            using var cmd = conn.CreateCommand();
            EnsureFullTextCatalog(cmd);

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, SemanticFullTextJson(tableName), productName: tableName));
            // Not the engine's own 41209, which also names the semantic database: the refusal has to come before the table.
            Assert.That(ex!.Message, Does.Contain("STATISTICAL_SEMANTICS requires a registered semantic language statistics database"));
            cmd.CommandText = $"SELECT OBJECT_ID('dbo.{tableName}')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value), "a refused deploy must not create the table");
        }

        // ---------------------------------------------------------------------------------------------------
        // Edition. Below 2016 SP1 (13.0.4001), compression and columnstore need Enterprise or Developer
        // edition: Express 2008 R2-2014 refuse them with 7738 and 35315. The demo container is Developer, so the
        // edition is simulated through the CONTEXT_INFO override fn_ServerMajorVersion reads, extended with an
        // 'SSED' marker that SchemaSmith.fn_EnterpriseFeaturesUnavailable reads.
        // ---------------------------------------------------------------------------------------------------

        private const string CompressionEditionObjectType = "data compression (Enterprise edition below SQL Server 2016 SP1)";
        private const string ColumnStoreEditionObjectType = "columnstore index (Enterprise edition below SQL Server 2016 SP1)";

        private static void SimulateEditionWithoutEnterpriseFeatures(IDbCommand cmd, int major)
        {
            cmd.CommandText = $"DECLARE @c VARBINARY(128) = 0x53534F56 + CONVERT(BINARY(4), {major}) + 0x5353454401; SET CONTEXT_INFO @c";
            cmd.ExecuteNonQuery();
        }

        private static string CompressedTableJson(string tableName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "CompressionType": "PAGE",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Val]", "DataType": "NVARCHAR(100)", "Nullable": false}
    ],
    "Indexes": [
        {"Name": "[ix_{{tableName}}]", "Clustered": false, "IndexColumns": "[Val]", "CompressionType": "ROW"}
    ]
}
""";

        private static string CompressionOf(IDbCommand cmd, string tableName, int indexId)
        {
            cmd.CommandText = $"SELECT MAX(data_compression_desc) FROM sys.partitions WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND index_id = {indexId}";
            return cmd.ExecuteScalar()?.ToString();
        }

        [Test]
        public void Compression_OnAnEditionWithoutIt_DeploysUncompressed_RecordsTheDowngrade_AndStaysQuietOnRedeploy()
        {
            var tableName = $"EdComp_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("EdBake13", serverMajorVersion: 13, policy: "warn");
            using var cmd = conn.CreateCommand();
            SimulateEditionWithoutEnterpriseFeatures(cmd, 13);

            RunTableQuenchProc(cmd, CompressedTableJson(tableName), productName: tableName);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, CompressedTableJson(tableName), productName: tableName));

            cmd.CommandText = $"SELECT index_id FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND name = 'ix_{tableName}'";
            var ixId = Convert.ToInt32(cmd.ExecuteScalar());
            var heap = CompressionOf(cmd, tableName, 0);
            var index = CompressionOf(cmd, tableName, ixId);
            var tableRows = DowngradeRowCount(cmd, CompressionEditionObjectType, $"[dbo].[{tableName}]");
            var indexRows = DowngradeRowCount(cmd, CompressionEditionObjectType, $"[dbo].[{tableName}].[ix_{tableName}]");
            Assert.Multiple(() =>
            {
                Assert.That(heap, Is.EqualTo("NONE"));
                Assert.That(index, Is.EqualTo("NONE"));
                Assert.That(tableRows, Is.EqualTo(2), "once per quench");
                Assert.That(indexRows, Is.EqualTo(2), "once per quench");
            });
        }

        private static string CompressedSpatialJson(string tableName) => $$"""
{
    "Schema": "[dbo]",
    "Name": "[{{tableName}}]",
    "Columns": [
        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
        {"Name": "[Location]", "DataType": "GEOGRAPHY", "Nullable": true}
    ],
    "Indexes": [ {"Name": "[PK_{{tableName}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]"} ],
    "SpatialIndexes": [ {"Name": "[sx_{{tableName}}]", "Column": "[Location]", "Tessellation": "GEOGRAPHY_GRID", "CompressionType": "PAGE"} ]
}
""";

        private static string SpatialCompressionOf(IDbCommand cmd, string tableName)
        {
            cmd.CommandText = $"SELECT MAX(p.data_compression_desc) FROM sys.spatial_indexes i JOIN sys.internal_tables it ON it.parent_object_id = i.[object_id] AND it.parent_minor_id = i.index_id " +
                              $"JOIN sys.partitions p ON p.[object_id] = it.[object_id] AND p.index_id = 1 WHERE i.[object_id] = OBJECT_ID('dbo.{tableName}')";
            return cmd.ExecuteScalar() as string ?? "(no spatial index)";
        }

        // CREATE SPATIAL INDEX takes DATA_COMPRESSION from SQL Server 2012, on every edition (2008 R2 refuses it as syntax).
        private const string SpatialCompressionObjectType = "spatial index compression (SQL Server 2012)";

        [TestCase(false)]
        [TestCase(true)]
        public void SpatialCompression_Below2012_IsCreatedUncompressed_RecordsTheDowngrade_AndStaysQuietOnRedeploy(bool indexOnly)
        {
            var tableName = $"V10Spx_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("Bake10", serverMajorVersion: 10, policy: "warn");
            using var cmd = conn.CreateCommand();

            if (indexOnly) RunTableQuenchProc(cmd, CompressedSpatialJson(tableName).Replace("\"CompressionType\": \"PAGE\"", "\"CellsPerObject\": 16"), productName: tableName);
            RunTableQuenchProc(cmd, CompressedSpatialJson(tableName), indexOnly: indexOnly, productName: tableName);
            RunTableQuenchProc(cmd, CompressedSpatialJson(tableName), indexOnly: indexOnly, productName: tableName);

            var compression = SpatialCompressionOf(cmd, tableName);
            var downgrades = DowngradeRowCount(cmd, SpatialCompressionObjectType, $"[dbo].[{tableName}].[sx_{tableName}]");
            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'index' AND ObjectName LIKE '%sx_{tableName}%' AND ActionType IN ('created', 'dropped')";
            var changes = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(compression, Is.EqualTo("NONE"), "the index is created, uncompressed");
                Assert.That(downgrades, Is.EqualTo(2), "once per quench");
                Assert.That(changes, Is.EqualTo(1), "created once; the redeploy does not re-create it");
            });
        }

        [Test]
        public void SpatialCompression_Below2012_UnderFail_IsRefusedByName()
        {
            var tableName = $"V10SpxF_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("FailBake10", serverMajorVersion: 10, policy: "fail");
            using var cmd = conn.CreateCommand();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, CompressedSpatialJson(tableName), productName: tableName));
            Assert.That(ex!.Message, Does.Contain("Spatial index compression requires SQL Server 2012").And.Contain($"sx_{tableName}"));
        }

        [Test]
        public void SpatialCompression_IsKept_OnAnEditionWithoutRowstoreCompression()
        {
            var tableName = $"EdSpx_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("EdBake13", serverMajorVersion: 13, policy: "warn");
            using var cmd = conn.CreateCommand();
            SimulateEditionWithoutEnterpriseFeatures(cmd, 13);

            RunTableQuenchProc(cmd, CompressedSpatialJson(tableName), productName: tableName);
            Assert.Multiple(() =>
            {
                Assert.That(SpatialCompressionOf(cmd, tableName), Is.EqualTo("PAGE"), "2012-2016 Express compress a spatial index");
                Assert.That(DowngradeRowCount(cmd, SpatialCompressionObjectType, $"[dbo].[{tableName}].[sx_{tableName}]"), Is.Zero);
            });
        }

        [Test]
        public void ColumnStore_OnAnEditionWithoutIt_IsSkipped_AndRecordsTheDowngrade()
        {
            var tableName = $"EdNcci_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"ncci_{tableName}";
            using var conn = KindleScratchDatabase("EdBake13", serverMajorVersion: 13, policy: "warn");
            using var cmd = conn.CreateCommand();
            SimulateEditionWithoutEnterpriseFeatures(cmd, 13);

            RunTableQuenchProc(cmd, NonclusteredColumnStoreJson(tableName, indexName), productName: tableName);

            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(0));
            Assert.That(DowngradeRowCount(cmd, ColumnStoreEditionObjectType, $"[dbo].[{tableName}].[{indexName}]"), Is.EqualTo(1));
        }

        [Test]
        public void Compression_OnAnEditionWithoutIt_UnderFail_Aborts()
        {
            var tableName = $"EdFail_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("EdFailBake13", serverMajorVersion: 13, policy: "fail");
            using var cmd = conn.CreateCommand();
            SimulateEditionWithoutEnterpriseFeatures(cmd, 13);

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, CompressedTableJson(tableName), productName: tableName));
            Assert.That(ex!.Message, Does.Contain("Enterprise"));
            cmd.CommandText = $"SELECT OBJECT_ID('dbo.{tableName}')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value), "a refused deploy must not create the table");
        }

        [Test]
        public void Compression_OnAnEditionWithIt_IsApplied()
        {
            var tableName = $"EdOk_{Guid.NewGuid().ToString("N")[..8]}";
            using var conn = KindleScratchDatabase("EdBake13", serverMajorVersion: 13, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, CompressedTableJson(tableName), productName: tableName);

            Assert.That(CompressionOf(cmd, tableName, 0), Is.EqualTo("PAGE"));
        }

        // The clustered/nonclustered split: a CLUSTERED columnstore needs major 12 (2014), so baking 11 must
        // still degrade it even though a nonclustered one would be created at the same version.
        [Test]
        public void ColumnStore_Clustered_AtSql2012_StillDegrades()
        {
            var tableName = $"Cci11_{Guid.NewGuid().ToString("N")[..8]}";
            var indexName = $"cci_{tableName}";
            using var conn = KindleScratchDatabase("CciBake11", serverMajorVersion: 11, policy: "warn");
            using var cmd = conn.CreateCommand();

            RunTableQuenchProc(cmd, ClusteredColumnStoreJson(tableName, indexName), productName: tableName);

            Assert.That(ColumnStoreIndexCount(cmd, tableName), Is.EqualTo(0), "a clustered columnstore must not be created below major 12");
            Assert.That(DowngradeRowCount(cmd, ColumnStoreObjectType, $"[dbo].[{tableName}].[{indexName}]"), Is.EqualTo(1),
                "a clustered columnstore must degrade at major 11 (needs 12)");
        }

        // ---------------------------------------------------------------------------------------------------
        // XML ingest path (compat-100 / below the OPENJSON cliff) — CI coverage. A genuine pre-2016 target
        // deploys through ParseTableXml / IndexOnlyXmlQuench, so the degrade must fire on that path too. Kindle
        // the scratch DB with IngestEncoding.Xml + baked major 10 on the MODERN container to exercise the exact
        // below-cliff code path in CI (the genuine-binary GenuineSql2008EmitGuardCertTests are the [Explicit]
        // release-time recheck — a real old binary's catalog shape is only reproduced there, not by baking).
        // ---------------------------------------------------------------------------------------------------

        private static string AllFeaturesJson(string temporal, string cols, string cci) => $$"""
[
  {"Schema": "[dbo]", "Name": "[{{temporal}}]", "IsTemporal": true,
   "Columns": [{"Name": "[Id]", "DataType": "INT", "Nullable": false, "PrimaryKey": true}, {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": false}]},
  {"Schema": "[dbo]", "Name": "[{{cols}}]",
   "Columns": [{"Name": "[Id]", "DataType": "INT", "Nullable": false},
               {"Name": "[Email]", "DataType": "NVARCHAR(100)", "Nullable": false, "DataMaskFunction": "email()"},
               {"Name": "[SSN]", "DataType": "NVARCHAR(11)", "Nullable": false, "EncryptionType": "DETERMINISTIC", "EncryptionKey": "[TestCEK]", "EncryptionAlgorithm": "AEAD_AES_256_CBC_HMAC_SHA_256"}]},
  {"Schema": "[dbo]", "Name": "[{{cci}}]",
   "Columns": [{"Name": "[Id]", "DataType": "INT", "Nullable": false}, {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": false}],
   "Indexes": [{"Name": "[cci_{{cci}}]", "Clustered": true, "ColumnStore": true, "PrimaryKey": false, "Unique": false}]}
]
""";

        private static void DeployXml(IDbCommand cmd, string tablesJson, string productName)
        {
            var xml = ModelXmlSerializer.ToIngestXml(tablesJson, "Tables", "Table");
            cmd.CommandTimeout = 300;
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{productName}', @TableDefinitions = @xml, " +
                              "@WhatIf = 0, @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
            var p = cmd.CreateParameter();
            p.ParameterName = "@xml";
            p.Value = xml;
            p.DbType = DbType.String;
            cmd.Parameters.Add(p);
            TransientRetry.Run(() => cmd.ExecuteNonQuery());
            cmd.Parameters.Clear();
        }

        // warn (default) on the XML ingest path: all four features degrade — the table deploys plain, columns
        // unmasked/plaintext, no columnstore — and one downgrade manifest row per feature is recorded.
        [Test]
        public void XmlEncoding_BelowSql2016_WarnPolicy_AllFeaturesDegradeCleanly()
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            string temporal = $"XmlT_{id}", cols = $"XmlC_{id}", cci = $"XmlI_{id}";
            using var conn = KindleScratchDatabase("XmlWarnBake", serverMajorVersion: 10, policy: "warn", encoding: IngestEncoding.Xml);
            using var cmd = conn.CreateCommand();

            Assert.DoesNotThrow(() => DeployXml(cmd, AllFeaturesJson(temporal, cols, cci), "XmlWarn"),
                "all four features must degrade cleanly on the XML ingest path below 2016");

            Assert.That(TableTemporalType(cmd, temporal), Is.EqualTo(0), "temporal turn-on suppressed on the XML path");
            Assert.That(MaskedColumnCount(cmd, cols), Is.EqualTo(0), "no masking on the XML path below 2016");
            Assert.That(EncryptedColumnCount(cmd, cols), Is.EqualTo(0), "no encryption on the XML path below 2016");
            Assert.That(ColumnStoreIndexCount(cmd, cci), Is.EqualTo(0), "no columnstore on the XML path below the floor");
            Assert.That(DowngradeRowCount(cmd, TemporalObjectType, $"[dbo].[{temporal}]"), Is.EqualTo(1), "temporal downgrade row (XML path)");
            Assert.That(DowngradeRowCount(cmd, DataMaskingObjectType, $"[dbo].[{cols}].[Email]"), Is.EqualTo(1), "masking downgrade row (XML path)");
            Assert.That(DowngradeRowCount(cmd, AlwaysEncryptedObjectType, $"[dbo].[{cols}].[SSN]"), Is.EqualTo(1), "AE downgrade row (XML path)");
            Assert.That(DowngradeRowCount(cmd, ColumnStoreObjectType, $"[dbo].[{cci}].[cci_{cci}]"), Is.EqualTo(1), "columnstore downgrade row (XML path)");
        }

        // fail (opt-in) on the XML ingest path: the quench aborts naming the required version.
        [Test]
        public void XmlEncoding_BelowSql2016_FailPolicy_Aborts()
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            using var conn = KindleScratchDatabase("XmlFailBake", serverMajorVersion: 10, policy: "fail", encoding: IngestEncoding.Xml);
            using var cmd = conn.CreateCommand();

            var ex = Assert.Catch(() => DeployXml(cmd, AllFeaturesJson($"XmlFT_{id}", $"XmlFC_{id}", $"XmlFI_{id}"), "XmlFail"));
            Assert.That(ex!.Message, Does.Contain("requires SQL Server"),
                "the fail policy must abort on the XML ingest path too");
        }
    }
}
