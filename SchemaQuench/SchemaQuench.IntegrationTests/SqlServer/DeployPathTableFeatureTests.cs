// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Table-level features that live in their own procedure, deployed through SchemaQuench's REAL path
/// (<c>Program.Main</c> → <c>DatabaseQuench</c>), not the <c>SchemaSmith.TableQuench</c> SQL wrapper.
/// <para>Every other test of these features calls that wrapper, and the product never does: it runs each table
/// step itself. A step added only to the wrapper therefore passed every test while a real deploy never ran it —
/// CDC (#423), table Change Tracking and FILESTREAM columns (#424), and the below-floor degrade (#425). These
/// tests assert what a user gets from a deploy, so a step the product stops calling fails here.</para>
/// </summary>
[Category("SqlServer")]
[TestFixture]
public class DeployPathTableFeatureTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private string _betweenScript;
    private string _beforeScript;
    private string _prerequisiteScript;
    private string _securityPolicyScript;
    private bool _nextDeployFails;

    // A new database costs CREATE DATABASE, sp_cdc_enable_db, a full kindle and a cold plan compile on the first
    // deploy -- most of a scenario. Scenarios differ only in the database setup, the policy and the ingest encoding the
    // forge was kindled with, so one database per combination is reused, and each scenario starts by removing what
    // the previous one left (ResetProbe).
    private readonly Dictionary<(string Setup, string Policy, bool XmlIngest), string> _sharedDatabases = new();

    [OneTimeTearDown]
    public void DropSharedDatabases()
    {
        if (_sharedDatabases.Count == 0) return;
        using var conn = OpenMaster();
        using var cmd = conn.CreateCommand();
        foreach (var db in _sharedDatabases.Values) DropDatabase(cmd, db);
        _sharedDatabases.Clear();
    }

    [Test]
    public void ACdcTable_IsEnabledRotatedAndDisabled_ByARealDeploy()
    {
        RunScenario("DeployCdc", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            // 1. A new table: CDC is enabled after every column exists, computed column included (#420, #423).
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(IsTrackedByCdc(cmd), Is.True, "a new EnableCDC table must be tracked after a real deploy");
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "Id", "Twice" }),
                "the capture instance must cover every declared column, computed ones included");
            Assert.That(CdcChanges(), Is.EqualTo(new[] { "created" }), "the summary must record CDC being enabled");

            // 2. A column change rotates to an instance that includes the new column.
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "B", "Id", "Twice" }),
                "a column change must rotate to a capture instance that includes the new column");
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "rotation keeps the old instance for the operator to drain");
            Assert.That(CdcChanges(), Is.EqualTo(new[] { "modified" }), "the summary must record the rotation");

            // 3. Leaving EnableCDC out leaves the table's CDC as it is: no disable, no rotation. It used to be disabled,
            //    losing the change table and its history, which nothing could rebuild.
            deploy(CdcTable(enableCdc: null, extraColumn: true));
            Assert.That(IsTrackedByCdc(cmd), Is.True, "an unset EnableCDC must leave the table's CDC alone");
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "an unset EnableCDC must not rotate the capture instance");

            // 4. Only an explicit false turns it off.
            deploy(CdcTable(enableCdc: false, extraColumn: true));
            Assert.That(IsTrackedByCdc(cmd), Is.False, "EnableCDC false must disable CDC on the table");
            // One row per capture instance: the rotation left two, and each is disabled.
            Assert.That(CdcChanges(), Is.EqualTo(new[] { "dropped", "dropped" }), "the summary must record CDC being disabled");
        });
    }

    // The CDC change rows the last deploy's summary recorded for the probe table.
    private static List<string> CdcChanges()
    {
        var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
        return ((JArray)summary.SelectToken("objectChanges.details") ?? [])
            .Where(d => (string)d["objectType"] == "changeDataCapture" && ((string)d["objectName"]).Contains("DeployProbe"))
            .Select(d => (string)d["action"]).ToList();
    }

    // #426. SQL Server defaults @supports_net_changes to ON once the table has a primary key, and since the #420 move a
    // new table's key exists when CDC is enabled -- so leaving it to the engine flipped every new table to ON. Unset keeps
    // the pre-2.7 result, OFF; declaring it rotates, because an instance cannot change it in place.
    [Test]
    public void NetChanges_AreOffWhenUnset_AndADeclarationRotatesToThem()
    {
        RunScenario("DeployNet", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.False, "an unset CdcSupportsNetChanges must not get SQL Server's ON default");

            deploy(CdcTable(enableCdc: true, extraColumn: false, netChanges: true));
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "a declared value the instance lacks rotates to a new instance");
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "and the new instance has it");
        });
    }

    [Test]
    public void ARotation_KeepsTheInstancesNetChanges_WhenUnset()
    {
        RunScenario("DeployNetKeep", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            // OFF on a table WITH a primary key -- the one case where "keep" and SQL Server's own default (ON once a key
            // exists) disagree. Starting from ON would pass whether or not the rotation carried the value.
            deploy(CdcTable(enableCdc: true, extraColumn: false, netChanges: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.False);

            // The declaration goes away and a column is added: the column rotates, and unset keeps what the table had.
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(InstanceCount(cmd), Is.EqualTo(2));
            Assert.That(NewestInstanceNetChanges(cmd), Is.False, "a rotation must keep net changes it was not told to change, as it keeps its filegroup");
        });
    }

    [Test]
    public void ATemplateDefault_SetsNetChanges_ForATableThatDeclaresNone()
    {
        RunScenario("DeployNetTpl", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "the template's CdcSupportsNetChanges applies to a table that declares none");
        }, templateExtra: ", \"CdcSupportsNetChanges\": true");
    }

    [Test]
    public void NetChangesWithoutAPrimaryKey_IsRefusedByName()
    {
        RunScenario("DeployNetNoPk", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false, netChanges: true, primaryKey: false));
            // Refused by our own message, before sp_cdc_enable_table could fail on it mid-run. A failure is logged to the
            // error log, so the text is looked for across both logs rather than assuming which one carries it.
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("sets CdcSupportsNetChanges true") && m.Contains("declares no primary key")), Is.True,
                "the deploy must refuse by name, not fail inside sp_cdc_enable_table");
        }, expectFailure: true);
    }

    // "RelatedTable": "sales.Customer" was placed whole behind the default schema ([dbo].[sales.Customer]) and the deploy
    // failed. A full deploy, not the procedure alone, because the prefix is read when the package loads.
    [Test]
    public void AForeignKeyWithASchemaQualifiedRelatedTable_ReferencesThatSchema()
    {
        const string setup = "EXEC('CREATE SCHEMA sales'); CREATE TABLE sales.Customer ([Id] INT NOT NULL CONSTRAINT [PK_Customer] PRIMARY KEY);";
        RunScenario("DeployFkSchema", setup, (deploy, db, cmd) =>
        {
            deploy("""
                { "Schema": "[dbo]", "Name": "[DeployProbe]",
                  "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                               { "Name": "[CustomerId]", "DataType": "INT", "Nullable": true } ],
                  "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
                  "ForeignKeys": [ { "Name": "[FK_DeployProbe_Customer]", "Columns": "[CustomerId]", "RelatedTable": "sales.Customer", "RelatedColumns": "[Id]" } ] }
                """);
            cmd.CommandText = "SELECT OBJECT_SCHEMA_NAME(referenced_object_id) + '.' + OBJECT_NAME(referenced_object_id) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('dbo.DeployProbe')";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("sales.Customer"));
        }, freshDatabase: true);
    }

    // The shape SchemaTongs writes to Prerequisites/, an Objects folder: that pass runs ahead of table creation, and the
    // script is create-only, so the second deploy finds the keys and changes nothing.
    [Test]
    public void AnAlwaysEncryptedTable_DeploysToAnEmptyDatabase_WithItsKeysFromAPrerequisiteScript()
    {
        RunScenario("DeployAeKeys", null, (deploy, db, cmd) =>
        {
            _prerequisiteScript = """
                IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE [name] = N'CMK_Deploy')
                  EXEC(N'CREATE COLUMN MASTER KEY [CMK_Deploy] WITH (KEY_STORE_PROVIDER_NAME = N''MSSQL_CERTIFICATE_STORE'', KEY_PATH = N''CurrentUser/My/0123456789ABCDEF'')')
                IF NOT EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE [name] = N'CEK_Deploy')
                  EXEC(N'CREATE COLUMN ENCRYPTION KEY [CEK_Deploy] WITH VALUES (COLUMN_MASTER_KEY = [CMK_Deploy], ALGORITHM = ''RSA_OAEP'', ENCRYPTED_VALUE = 0x016E0000016300)')
                """;
            const string table = """
                { "Schema": "[dbo]", "Name": "[DeployProbe]",
                  "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                               { "Name": "[Ssn]", "DataType": "CHAR(11)", "Nullable": true, "Collation": "Latin1_General_BIN2",
                                 "EncryptionType": "DETERMINISTIC", "EncryptionKey": "CEK_Deploy", "EncryptionAlgorithm": "AEAD_AES_256_CBC_HMAC_SHA_256" } ],
                  "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }
                """;
            deploy(table);
            deploy(table);
            cmd.CommandText = "SELECT k.[name] COLLATE DATABASE_DEFAULT + ':' + c.encryption_type_desc COLLATE DATABASE_DEFAULT FROM sys.columns c " +
                              "JOIN sys.column_encryption_keys k ON k.column_encryption_key_id = c.column_encryption_key_id WHERE c.[object_id] = OBJECT_ID('dbo.DeployProbe')";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("CEK_Deploy:DETERMINISTIC"));
        }, freshDatabase: true);
    }

    // SecurityPolicies is an after-tables folder: a policy names its table, which the same deploy has just created.
    [Test]
    public void ASecurityPolicy_IsCreatedAfterTheTableItProtects_ByARealDeploy()
    {
        const string setup = "EXEC('CREATE SCHEMA sec'); EXEC('CREATE FUNCTION sec.fn_rep(@Rep AS SYSNAME) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS ok WHERE @Rep = USER_NAME()');";
        RunScenario("DeployRls", setup, (deploy, db, cmd) =>
        {
            _securityPolicyScript = """
                IF OBJECT_ID(N'[sec].[ProbePolicy]') IS NULL
                  EXEC(N'CREATE SECURITY POLICY [sec].[ProbePolicy] ADD FILTER PREDICATE [sec].[fn_rep]([SalesRep]) ON [dbo].[DeployProbe] WITH (STATE = ON, SCHEMABINDING = ON)')
                """;
            const string table = """
                { "Schema": "[dbo]", "Name": "[DeployProbe]",
                  "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[SalesRep]", "DataType": "SYSNAME", "Nullable": false } ],
                  "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }
                """;
            deploy(table);
            deploy(table);
            cmd.CommandText = "SELECT OBJECT_NAME(target_object_id) FROM sys.security_predicates WHERE [object_id] = OBJECT_ID(N'sec.ProbePolicy')";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("DeployProbe"));
        }, freshDatabase: true);
    }

    [Test]
    public void ADeclaredDisabledIndex_IsDisabledByARealDeploy()
    {
        RunScenario("DeployDisabledIndex", null, (deploy, db, cmd) =>
        {
            deploy("""
                { "Schema": "[dbo]", "Name": "[DeployProbe]",
                  "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Code]", "DataType": "INT", "Nullable": true } ],
                  "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                               { "Name": "[IX_DeployProbe_Code]", "IndexColumns": "[Code]", "Disabled": true } ] }
                """);
            cmd.CommandText = "SELECT is_disabled FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.DeployProbe') AND [name] = 'IX_DeployProbe_Code'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the declared Disabled state reaches the database");
        });
    }

    [Test]
    public void AnEdgeConstraint_IsCreatedByARealDeploy()
    {
        const string setup = "CREATE TABLE dbo.ProbePerson ([Id] INT NOT NULL) AS NODE; CREATE TABLE dbo.ProbeCity ([Id] INT NOT NULL) AS NODE;";
        RunScenario("DeployEdgeConstraint", setup, (deploy, db, cmd) =>
        {
            deploy("""
                { "Schema": "[dbo]", "Name": "[DeployProbe]", "GraphType": "Edge",
                  "Columns": [ { "Name": "[Since]", "DataType": "DATE", "Nullable": true } ],
                  "EdgeConstraints": [ { "Name": "[EC_DeployProbe]", "Clauses": [ { "FromTable": "[dbo].[ProbePerson]", "ToTable": "[dbo].[ProbeCity]" } ], "DeleteAction": "CASCADE" } ] }
                """);
            cmd.CommandText = "SELECT [name] COLLATE DATABASE_DEFAULT + ':' + delete_referential_action_desc COLLATE DATABASE_DEFAULT FROM sys.edge_constraints WHERE parent_object_id = OBJECT_ID('dbo.DeployProbe')";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("EC_DeployProbe:CASCADE"), "the declared edge constraint reaches the database");
        }, freshDatabase: true);
    }

    [Test]
    public void CdcDeclaredOnADatabaseWithoutCdc_IsRecordedAsDowngraded_ByARealDeploy()
    {
        RunScenario("DeployCdcOff", setupDatabase: null, (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(IsTrackedByCdc(cmd), Is.False);
            // Asserted on the log, not on SchemaSmith.ChangeAudit: a real deploy reads its session's audit rows into the
            // deployment summary and then deletes them, so the table is empty afterwards whether or not the degrade ran.
            _progressLog.Received().Info(Arg.Is<string>(m => m.Contains("CDC skipped: not enabled on this database")));
        }, expectIncomplete: true);
    }

    // Under 'fail' the refusal must come before anything is created. The degrade runs in the ingest batch ahead of
    // MissingTableAndColumnQuench, and a RAISERROR does not stop a batch, so a refusal the batch ignores still creates
    // the table and only then reports the failure.
    [Test]
    public void CdcDeclaredOnADatabaseWithoutCdc_UnderFail_RefusesBeforeCreatingAnything()
    {
        RunScenario("DeployCdcFail", setupDatabase: null, (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("Change Data Capture requires CDC enabled on the database")), Is.True,
                "the deploy must refuse by the degrade's own message");
            cmd.CommandText = "SELECT OBJECT_ID('dbo.DeployProbe')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value), "a refused deploy must not have created the table");
        }, expectFailure: true, policy: "fail");
    }

    // #432: a Before script is where a package turns CDC on for a new or restored database, and the degrade used to
    // judge the database before that slot ran -- so the first deploy recorded the tables as downgraded and tracked none.
    [TestCase("warn")]
    [TestCase("fail")]
    public void CdcEnabledByABeforeScript_TracksTheTable_OnTheFirstDeploy(string policy)
    {
        RunScenario("DeployCdcBefore", setupDatabase: null, (deploy, db, cmd) =>
        {
            _beforeScript = "IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1) EXEC sys.sp_cdc_enable_db";
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(IsTrackedByCdc(cmd), Is.True, "the first deploy must track the table once the Before script has enabled CDC");
            _progressLog.DidNotReceive().Info(Arg.Is<string>(m => m.Contains("CDC skipped")));
        }, policy: policy, freshDatabase: true);
    }

    [TestCase("warn")]
    [TestCase("fail")]
    public void ChangeTrackingEnabledByABeforeScript_TracksTheTable_OnTheFirstDeploy(string policy)
    {
        RunScenario("DeployCtBefore", setupDatabase: null, (deploy, db, cmd) =>
        {
            _beforeScript = "IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID()) "
                            + "ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 1 DAYS)";
            deploy("""
                { "Schema": "[dbo]", "Name": "[DeployProbe]", "EnableChangeTracking": true,
                  "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
                  "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }
                """);
            cmd.CommandText = "SELECT COUNT(*) FROM sys.change_tracking_tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                "the first deploy must track the table once the Before script has enabled Change Tracking");
            _progressLog.DidNotReceive().Info(Arg.Is<string>(m => m.Contains("Change Tracking skipped")));
        }, policy: policy, freshDatabase: true);
    }

    // Deferring the judgement past the Before slot must not lose it: a Before script that leaves CDC off still
    // degrades under 'warn' and still refuses under 'fail'.
    [Test]
    public void CdcStillOffAfterABeforeScript_IsRecordedAsDowngraded()
    {
        RunScenario("DeployCdcBeforeOff", setupDatabase: null, (deploy, db, cmd) =>
        {
            _beforeScript = "SELECT 1";
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(IsTrackedByCdc(cmd), Is.False);
            _progressLog.Received().Info(Arg.Is<string>(m => m.Contains("CDC skipped: not enabled on this database")));
        }, expectIncomplete: true);
    }

    [Test]
    public void CdcStillOffAfterABeforeScript_UnderFail_IsRefused()
    {
        RunScenario("DeployCdcBeforeFail", setupDatabase: null, (deploy, db, cmd) =>
        {
            _beforeScript = "SELECT 1";
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("Change Data Capture requires CDC enabled on the database")), Is.True,
                "the deploy must refuse by the degrade's own message");
        }, expectFailure: true, policy: "fail");
    }

    // A column added by a run that then fails before the table-features step must still reach the capture instance
    // on the next run. The column work is done by then, so nothing in the next run's column diff says to rotate.
    [Test]
    public void AColumnChange_StillRotates_WhenTheRunThatMadeItFailedBeforeCdc()
    {
        RunScenario("DeployCdcResume", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));

            _betweenScript = "RAISERROR('between-scripts failure', 16, 1)";
            _nextDeployFails = true;
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            _betweenScript = null;

            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "B", "Id", "Twice" }),
                "the column the failed run added must be captured once a later run completes");
        });
    }

    // The same, through --ResumeQuench. A resume skips the completed ModifiedTables step, which is where a column change
    // used to be turned into a rotation, and starts a new session, so nothing it decided survives.
    [Test]
    public void AColumnChange_StillRotates_WhenTheFailedRunIsResumed()
    {
        var checkpointDir = Path.Join(Path.GetTempPath(), $"DeployCdcResumeCkpt_{Guid.NewGuid():N}");
        RunScenario("DeployCdcResumeCk", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));

            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var savedDir = config["CheckpointDirectory"];
            config["CheckpointDirectory"] = checkpointDir;
            try
            {
                _environment.CommandLine.Returns("--SkipKindlingForge");
                _betweenScript = "RAISERROR('between-scripts failure', 16, 1)";
                _nextDeployFails = true;
                deploy(CdcTable(enableCdc: true, extraColumn: true));
                _betweenScript = null;
                Assert.That(Directory.Exists(checkpointDir) && Directory.EnumerateFileSystemEntries(checkpointDir).Any(), Is.True,
                    "precondition: the failed run left a checkpoint to resume from");

                _environment.CommandLine.Returns("--SkipKindlingForge --ResumeQuench");
                deploy(CdcTable(enableCdc: true, extraColumn: true));
            }
            finally
            {
                config["CheckpointDirectory"] = savedDir;
                // Program.Main registers a checkpoint manager for that folder and nothing unregisters it, so the next test
                // in this process would write checkpoints into the folder deleted below.
                FactoryContainer.Unregister<Schema.Checkpointing.ICheckpointing>();
                _environment.CommandLine.Returns("");
                try { if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true); }
                catch (IOException) { /* a held handle must not fail the test */ }
            }
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "B", "Id", "Twice" }),
                "the column the failed run added must be captured once the resumed run completes");
        });
    }

    // #427. At the two-instance limit a new column is refused before it is added: refused any later, the column already
    // exists, and once the operator follows the message and frees a slot, nothing in the next run's column diff says to
    // rotate. The recovery the message describes must end with the column captured.
    [Test]
    public void ANewColumnAtTheInstanceLimit_IsRefusedBeforeItIsAdded_AndTheRecoveryCapturesIt()
    {
        RunScenario("DeployCdcCeil", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "precondition: both capture-instance slots are in use");

            _nextDeployFails = true;
            deploy(CdcTable(enableCdc: true, extraColumn: true, extraColumnC: true));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("CDC capture-instance limit reached")), Is.True, "refused by the limit's own message");
            cmd.CommandText = "SELECT COL_LENGTH('dbo.DeployProbe', 'C')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value), "a refused deploy must not have added the column");

            ExecuteWithDeadlockRetry(cmd, "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'DeployProbe', @capture_instance = N'dbo_DeployProbe'");
            deploy(CdcTable(enableCdc: true, extraColumn: true, extraColumnC: true));
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "B", "C", "Id", "Twice" }),
                "once a slot is free the next deploy must add the column and capture it");
        });
    }

    // #426, unset. Earlier versions enabled CDC before a new table's primary key existed, so SQL Server's default gave
    // a new table net changes OFF and a keyed existing table ON. Unset keeps both.
    [Test]
    public void NetChanges_WhenUnset_FollowSqlServersDefault_ForAnExistingKeyedTable()
    {
        RunScenario("DeployNetExisting", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: false, extraColumn: false));
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "enabling CDC on an existing keyed table keeps what earlier versions did: ON");
        });
    }

    // Net changes can identify rows by a unique index instead of a primary key. SchemaSmith cannot declare that index,
    // so a rotation that keeps net changes has to keep the index too, or sp_cdc_enable_table fails mid-run.
    [Test]
    public void ARotation_KeepsTheUniqueIndexNetChangesUse()
    {
        RunScenario("DeployNetUix", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: false, extraColumn: false, primaryKey: false, uniqueIndex: true));
            ExecuteWithDeadlockRetry(cmd, "EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'DeployProbe', "
                                          + "@role_name = NULL, @supports_net_changes = 1, @index_name = N'IX_DeployProbe'");
            deploy(CdcTable(enableCdc: true, extraColumn: true, primaryKey: false, uniqueIndex: true));
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "the column change rotates");
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "and the new instance keeps net changes on the same index");
        });
    }

    // CdcIndexName is passed as @index_name, so net changes can identify rows by a unique index on a table with no
    // primary key -- a declaration that was refused before it existed. The XML ingest parses it separately.
    [TestCase(false)]
    [TestCase(true)]
    public void CdcIndexName_LetsNetChangesIdentifyRowsByAUniqueIndex_WithNoPrimaryKey(bool xmlIngest)
    {
        RunScenario("DeployCdcIdx", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false, netChanges: true, primaryKey: false, uniqueIndex: true,
                            cdcIndexName: "[IX_DeployProbe]"));
            Assert.That(NewestInstanceIndex(cmd), Is.EqualTo("IX_DeployProbe"), "the instance must identify rows by the declared index");
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "with net changes on");
        }, xmlIngest: xmlIngest);
    }

    // A CdcIndexName the newest instance does not use rotates, after the index exists: here it is declared in the same
    // deploy. Once rotated, the next deploy finds nothing to change.
    [Test]
    public void CdcIndexName_ChangedToAnotherIndex_RotatesOntoIt_AndThenConverges()
    {
        RunScenario("DeployCdcIdxRot", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(NewestInstanceIndex(cmd), Is.EqualTo("PK_DeployProbe"), "precondition: SQL Server chose the primary key");

            var declared = CdcTable(enableCdc: true, extraColumn: false, altUniqueIndexColumn: "[Id]", cdcIndexName: "[UX_DeployProbe]");
            deploy(declared);
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "a declared index the instance does not use rotates to a new instance");
            Assert.That(NewestInstanceIndex(cmd), Is.EqualTo("UX_DeployProbe"), "and the new instance uses it");

            deploy(declared);
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "once it is used, the next deploy must not rotate again");
        });
    }

    [Test]
    public void CdcIndexName_ChangedWhileBothInstancesAreInUse_IsRefusedBeforeAnythingChanges()
    {
        RunScenario("DeployCdcIdxCeil", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "precondition: both capture-instance slots are in use");

            _nextDeployFails = true;
            deploy(CdcTable(enableCdc: true, extraColumn: true, altUniqueIndexColumn: "[Id]", cdcIndexName: "[UX_DeployProbe]"));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("CDC capture-instance limit reached") && m.Contains("CdcIndexName")), Is.True,
                "refused by the limit's own message");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.DeployProbe') AND [name] = 'UX_DeployProbe'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "refused before the index was created");
        });
    }

    [TestCase("[UX_Missing]", null, "which is not one of its declared indexes")]
    [TestCase("[UX_DeployProbe]", "[A]", "which has a nullable key column")]
    public void CdcIndexName_ThatSqlServerWouldRefuse_IsRefusedByName_BeforeCdcIsEnabled(string cdcIndexName, string altColumn, string problem)
    {
        RunScenario("DeployCdcIdxBad", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false, altUniqueIndexColumn: altColumn, cdcIndexName: cdcIndexName));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("names CdcIndexName " + cdcIndexName.Trim('[', ']')) && m.Contains(problem)), Is.True,
                "the deploy must refuse by name, not fail inside sp_cdc_enable_table");
            Assert.That(IsTrackedByCdc(cmd), Is.False, "refused before CDC was enabled");
        }, expectFailure: true);
    }

    // SQL Server will not drop an index a capture instance uses, but only says so when the DROP runs, part-way through
    // the index work. It does allow a rename, leaving the instance naming an index that is gone and free to be dropped.
    // Both are refused before any index is touched.
    [TestCase("rename")]
    [TestCase("change")]
    public void AnIndexACaptureInstanceUses_IsRefusedBeforeItIsDroppedOrRenamed(string what)
    {
        RunScenario("DeployCdcIdxKeep", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));

            _nextDeployFails = true;
            deploy(what == "rename"
                ? CdcTable(enableCdc: true, extraColumn: false, primaryKeyName: "[PK_DeployProbe_Renamed]")
                : CdcTable(enableCdc: true, extraColumn: false, primaryKeyColumns: "[Id] DESC"));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("CDC identifies rows by an index this deploy would drop or rename")
                                        && m.Contains($"[PK_DeployProbe] ({(what == "rename" ? "renamed" : "dropped")}, named by dbo_DeployProbe)")), Is.True,
                "refused by its own message, naming the index, what would happen to it, and the capture instance");
            if (what == "rename")
                Assert.That(logged.Any(m => m.Contains("[PK_DeployProbe] (dropped")), Is.False, "a rename is not a drop");
            cmd.CommandText = "SELECT is_descending_key FROM sys.index_columns ic JOIN sys.indexes i ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id "
                              + "WHERE i.[object_id] = OBJECT_ID('dbo.DeployProbe') AND i.[name] = 'PK_DeployProbe'";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(false), "the index must be left as it was, under its own name");
        });
    }

    // An index-only template renames and drops indexes in its own procedure, so it needs the same refusal.
    [TestCase(false)]
    [TestCase(true)]
    public void AnIndexACaptureInstanceUses_IsRefusedByAnIndexOnlyTemplate(bool xmlIngest)
    {
        RunScenario("DeployCdcIdxOnly", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            cmd.CommandText = """
                CREATE TABLE dbo.DeployProbe (Id INT NOT NULL CONSTRAINT PK_DeployProbe PRIMARY KEY, A INT NULL);
                EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'DeployProbe', @role_name = NULL;
                """;
            cmd.ExecuteNonQuery();

            _nextDeployFails = true;
            deploy(CdcTable(enableCdc: null, extraColumn: false, primaryKeyName: "[PK_DeployProbe_Renamed]"));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("CDC identifies rows by an index this deploy would drop or rename")
                                        && m.Contains("[PK_DeployProbe] (renamed, named by dbo_DeployProbe)")), Is.True,
                "refused by its own message before the index-only deploy renames the index");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.DeployProbe') AND [name] = 'PK_DeployProbe'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the index must keep its name");
        }, templateExtra: ", \"IndexOnlyTableQuenches\": true", xmlIngest: xmlIngest);
    }

    // A new clustered index displaces the existing one whatever its name, even when the package no longer declares it
    // and is not set to drop it -- so the clustered index a capture instance uses is refused there too.
    [Test]
    public void ANewClusteredIndex_DisplacingTheOneACaptureInstanceUses_IsRefusedFirst()
    {
        RunScenario("DeployCdcIdxCx", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));

            _nextDeployFails = true;
            deploy(CdcTable(enableCdc: true, extraColumn: false, primaryKey: false, clusteredIndexColumn: "[A]",
                            tableExtra: "\"DropIndexesRemovedFromProduct\": false,"));
            var logged = _progressLog.ReceivedCalls().Concat(_errorLog.ReceivedCalls())
                .Select(c => c.GetArguments().FirstOrDefault()?.ToString() ?? "");
            Assert.That(logged.Any(m => m.Contains("[PK_DeployProbe] (dropped, named by dbo_DeployProbe)")), Is.True,
                "refused by its own message before the clustered index is displaced");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.DeployProbe') AND [name] = 'PK_DeployProbe'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the primary key must still be there");
        });
    }

    // The XML ingest (compatibility level below 130) is a separate batch with its own copy of every preflight step.
    [Test]
    public void ACdcTable_OnTheXmlIngestPath_GetsTheTemplateDefaultAndRotates()
    {
        RunScenario("DeployCdcXml", setupDatabase: "EXEC sys.sp_cdc_enable_db", (deploy, db, cmd) =>
        {
            deploy(CdcTable(enableCdc: true, extraColumn: false));
            Assert.That(NewestInstanceNetChanges(cmd), Is.True, "the template default applies on the XML path too");
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "B", "Id", "Twice" }));
            _progressLog.Received().Info(Arg.Is<string>(m => m.Contains("forced by Target:CompatEncoding")));
        }, templateExtra: ", \"CdcSupportsNetChanges\": true", xmlIngest: true);
    }

    // A deploy that dies part-way leaves its session's ChangeAudit rows behind, and SQL Server recycles session ids, so
    // the next deploy to get that id read them as its own and put them in its summary. Seeding every likely id makes
    // the reuse certain.
    [Test]
    public void ChangeAuditRowsLeftByAnotherSession_AreNotReportedAsThisDeploysChanges()
    {
        RunScenario("DeployStaleAudit", setupDatabase: null, (deploy, db, cmd) =>
        {
            cmd.CommandText = "INSERT SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) "
                              + "SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'table', 'dbo.PhantomFromAnotherSession', 'created' "
                              + "FROM sys.all_columns a CROSS JOIN sys.all_columns b";
            cmd.ExecuteNonQuery();

            deploy(CdcTable(enableCdc: null, extraColumn: false));

            var summary = JObject.Parse(File.ReadAllText(Path.Join(ConfigHelper.ResolveLogPath(), "SchemaQuench - Summary.json")));
            var reported = ((JArray)summary.SelectToken("objectChanges.details") ?? [])
                .Select(d => (string)d["objectName"]).ToList();
            Assert.That(reported, Has.Some.Contains("DeployProbe"), "precondition: the summary carries this deploy's own changes");
            Assert.That(reported, Has.None.Contains("PhantomFromAnotherSession"),
                "rows another session left behind must not be reported as this deploy's changes");
        });
    }

    [Test]
    public void AChangeTrackingTable_IsTracked_ByARealDeploy()
    {
        RunScenario("DeployCt", setupDatabase: "ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 1 DAYS)", (deploy, db, cmd) =>
        {
            deploy("""
                { "Schema": "[dbo]", "Name": "[DeployProbe]", "EnableChangeTracking": true, "TrackColumnsUpdated": true,
                  "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[A]", "DataType": "INT", "Nullable": true } ],
                  "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }
                """);
            cmd.CommandText = "SELECT is_track_columns_updated_on FROM sys.change_tracking_tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe')";
            var result = cmd.ExecuteScalar();
            Assert.That(result, Is.Not.Null.And.Not.EqualTo(DBNull.Value), "an EnableChangeTracking table must be tracked after a real deploy");
            Assert.That(Convert.ToBoolean(result), Is.True, "TrackColumnsUpdated must be applied");
        });
    }

    // A system-versioned table whose primary-key column changes: SQL Server will not drop the key while versioning is
    // on (13557), so the deploy failed with "Could not drop constraint". Versioning is now suspended around the rebuild
    // and resumed with the SAME history table -- a custom-named one, so a resume by the default name would show -- and
    // the history rows written before the change must still be there, under the new collation.
    [Test]
    public void ATemporalTablesPrimaryKeyColumnChange_KeepsVersioningAndItsHistory()
    {
        RunScenario("DeployTemporalPk", setupDatabase: null, (deploy, db, cmd) =>
        {
            deploy(TemporalTable("Latin1_General_CS_AS"));
            cmd.CommandText = "INSERT dbo.DeployProbe (Code, Val) VALUES ('a', 1); UPDATE dbo.DeployProbe SET Val = 2;";
            cmd.ExecuteNonQuery();

            deploy(TemporalTable("Latin1_General_CI_AS"));

            cmd.CommandText = "SELECT h.[name] FROM sys.tables t JOIN sys.tables h ON h.[object_id] = t.history_table_id WHERE t.[object_id] = OBJECT_ID('dbo.DeployProbe') AND t.temporal_type = 2";
            Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("DeployProbe_Archive"), "versioning must be back on, with the same history table");
            cmd.CommandText = "SELECT COUNT(*) FROM dbo.DeployProbe_Archive WHERE Val = 1";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the history written before the change must survive");
            cmd.CommandText = "SELECT STRING_AGG(OBJECT_NAME([object_id]) + ':' + collation_name, ',') WITHIN GROUP (ORDER BY OBJECT_NAME([object_id])) "
                              + "FROM sys.columns WHERE [name] = 'Code' AND [object_id] IN (OBJECT_ID('dbo.DeployProbe'), OBJECT_ID('dbo.DeployProbe_Archive'))";
            Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("DeployProbe:Latin1_General_CI_AS,DeployProbe_Archive:Latin1_General_CI_AS"));
            cmd.CommandText = "SELECT COUNT(*) FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.DeployProbe') AND [name] = 'SchemaSmith_SuspendedHistory'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "the suspension marker must be removed once versioning is back on");
        });
    }

    private static string TemporalTable(string collation) => $$"""
        { "Schema": "[dbo]", "Name": "[DeployProbe]", "IsTemporal": true, "HistoryTableName": "[DeployProbe_Archive]",
          "Columns": [ { "Name": "[Code]", "DataType": "VARCHAR(20)", "Nullable": false, "Collation": "{{collation}}" },
                       { "Name": "[Val]", "DataType": "INT", "Nullable": true } ],
          "Indexes": [ { "Name": "[PK_DeployProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Code]" } ] }
        """;

    private static string CdcTable(bool? enableCdc, bool extraColumn, bool? netChanges = null, bool primaryKey = true,
                                   bool extraColumnC = false, bool uniqueIndex = false, string cdcIndexName = null,
                                   string altUniqueIndexColumn = null, string primaryKeyName = "[PK_DeployProbe]",
                                   string primaryKeyColumns = "[Id]", string clusteredIndexColumn = null,
                                   string tableExtra = "") => $$"""
        { "Schema": "[dbo]", "Name": "[DeployProbe]", {{tableExtra}} {{(enableCdc is { } cdc ? $"\"EnableCDC\": {(cdc ? "true" : "false")}," : "")}}
          {{(netChanges is { } nc ? $"\"CdcSupportsNetChanges\": {(nc ? "true" : "false")}," : "")}}
          {{(cdcIndexName != null ? $"\"CdcIndexName\": \"{cdcIndexName}\"," : "")}}
          "Columns": [
            { "Name": "[Id]", "DataType": "INT", "Nullable": false },
            { "Name": "[A]", "DataType": "INT", "Nullable": true },
            {{(extraColumn ? """{ "Name": "[B]", "DataType": "INT", "Nullable": true },""" : "")}}
            {{(extraColumnC ? """{ "Name": "[C]", "DataType": "INT", "Nullable": true },""" : "")}}
            { "Name": "[Twice]", "DataType": "INT", "Nullable": true, "ComputedExpression": "[A] * 2" }
          ],
          "Indexes": [ {{(clusteredIndexColumn != null ? $"{{ \"Name\": \"[CX_DeployProbe]\", \"IndexColumns\": \"{clusteredIndexColumn}\", \"Clustered\": true }}," : "")}}
                       {{(altUniqueIndexColumn != null ? $"{{ \"Name\": \"[UX_DeployProbe]\", \"IndexColumns\": \"{altUniqueIndexColumn}\", \"Unique\": true }}," : "")}}
                       {{(primaryKey
              ? $"{{ \"Name\": \"{primaryKeyName}\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"{primaryKeyColumns}\" }}"
              : uniqueIndex
                  ? """{ "Name": "[IX_DeployProbe]", "IndexColumns": "[Id]", "Unique": true }"""
                  : """{ "Name": "[IX_DeployProbe]", "IndexColumns": "[Id]" }""")}} ] }
        """;

    // freshDatabase: the scenario reconfigures the database itself (a Before script that enables CDC or Change
    // Tracking), so a shared one would carry that into the next scenario.
    private void RunScenario(string prefix, string setupDatabase, Action<Action<string>, string, IDbCommand> body,
                             string templateExtra = "", bool expectFailure = false, string policy = "warn", bool xmlIngest = false,
                             bool expectIncomplete = false, bool freshDatabase = false)
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        var key = (setupDatabase ?? "", policy, xmlIngest);

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            using var conn = OpenMaster();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var savedPath = config["SchemaPackagePath"];
            var savedEncoding = config["Target:CompatEncoding"];
            var shared = !freshDatabase && _sharedDatabases.ContainsKey(key);
            var db = shared ? _sharedDatabases[key] : prefix + "_" + Guid.NewGuid().ToString("N")[..12];

            try
            {
                if (shared)
                {
                    conn.ChangeDatabase(db);
                    ResetProbe(cmd);
                }
                else
                {
                    cmd.CommandText = $"CREATE DATABASE [{db}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};";
                    cmd.ExecuteNonQuery();
                    if (!freshDatabase) _sharedDatabases[key] = db;
                    conn.ChangeDatabase(db);
                    if (setupDatabase != null) ExecuteWithDeadlockRetry(cmd, setupDatabase);
                    ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, policy: policy);
                }
                config["SchemaPackagePath"] = tempDir;
                // The encoding is chosen during version detection, which SkipKindlingForge also skips.
                if (xmlIngest) config["Target:CompatEncoding"] = "legacy";

                body(tableJson =>
                {
                    WritePackage(tempDir, db, tableJson, templateExtra, _betweenScript, _beforeScript, _prerequisiteScript, _securityPolicyScript);
                    _environment.ClearReceivedCalls();
                    var reportBase = Path.Join(tempDir, "summary");
                    if (expectIncomplete) _environment.CommandLine.Returns($"SchemaQuench.exe --report:{reportBase}");
                    Program.Main(xmlIngest ? [] : ["SkipKindlingForge"]);
                    if (expectIncomplete) _environment.CommandLine.Returns("");
                    var fails = expectFailure || _nextDeployFails;
                    _nextDeployFails = false;
                    if (fails) _environment.Received().Exit(Arg.Is<int>(code => code != 0));
                    else if (expectIncomplete)
                    {
                        _environment.Received(1).Exit(1);
                        _environment.DidNotReceive().Exit(Arg.Is<int>(code => code > 1));
                        var summary = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(reportBase + ".json"));
                        Assert.That(summary.SelectToken("run.outcome")?.ToString(), Is.EqualTo("Incomplete"),
                            "a run that downgraded a feature must say Incomplete in its summary, not only in its exit code");
                    }
                    else _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));
                }, db, cmd);
            }
            finally
            {
                config["SchemaPackagePath"] = savedPath;
                config["Target:CompatEncoding"] = savedEncoding;
                if (freshDatabase) DropDatabase(cmd, db);
                conn.Close();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
    }

    private static IDbConnection OpenMaster()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
            ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
                config["Target:User"], config["Target:Password"], config["Target:Port"], connProps));
        conn.Open();
        return conn;
    }

    private static void DropDatabase(IDbCommand cmd, string db)
    {
        try
        {
            cmd.Connection!.ChangeDatabase("master");
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
        }
        // Cleanup must not fail the test, but a database it leaves behind keeps running (a CDC capture job), so say so.
        catch (DbException e) { TestContext.Progress.WriteLine($"WARNING: test database {db} was not dropped: {e.Message}"); }
        catch (InvalidOperationException e) { TestContext.Progress.WriteLine($"WARNING: test database {db} was not dropped: {e.Message}"); }
    }

    // Everything a scenario can leave in a shared database: the probe table with its capture instances, Change Tracking
    // and history table, and SchemaSmith's own per-object state, so the next scenario deploys as if into a new one.
    // Run at the start rather than the end, so a scenario that failed half-way cannot leak into the next.
    private static void ResetProbe(IDbCommand cmd) => ExecuteWithDeadlockRetry(cmd, """
        IF EXISTS (SELECT 1 FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe') AND is_tracked_by_cdc = 1)
            EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'DeployProbe', @capture_instance = N'all';
        IF EXISTS (SELECT 1 FROM sys.change_tracking_tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe'))
            ALTER TABLE dbo.DeployProbe DISABLE CHANGE_TRACKING;
        IF EXISTS (SELECT 1 FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe') AND temporal_type = 2)
            ALTER TABLE dbo.DeployProbe SET (SYSTEM_VERSIONING = OFF);
        IF OBJECT_ID('dbo.DeployProbe') IS NOT NULL DROP TABLE dbo.DeployProbe;
        IF OBJECT_ID('dbo.DeployProbe_Archive') IS NOT NULL DROP TABLE dbo.DeployProbe_Archive;
        DELETE SchemaSmith.ChangeAudit;
        DELETE SchemaSmith.ExpressionMap;
        DELETE SchemaSmith.ProductOwnership;
        DELETE SchemaSmith.CompletedMigrationScripts;
        """);

    // sp_cdc_enable_db writes database-wide replication metadata and can be chosen as a deadlock victim against a
    // sibling fixture's DDL; retrying is what the CDC fixture does for the same reason.
    private static void ExecuteWithDeadlockRetry(IDbCommand cmd, string sql)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return;
            }
            catch (DbException e) when (e.Message.Contains("deadlock victim", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(1000);
            }
        }
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool IsTrackedByCdc(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT is_tracked_by_cdc FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.DeployProbe')";
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static bool NewestInstanceNetChanges(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT TOP 1 supports_net_changes FROM cdc.change_tables
                            WHERE source_object_id = OBJECT_ID('dbo.DeployProbe') ORDER BY create_date DESC, [object_id] DESC";
        return Convert.ToBoolean(cmd.ExecuteScalar());
    }

    private static string NewestInstanceIndex(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT TOP 1 index_name FROM cdc.change_tables
                            WHERE source_object_id = OBJECT_ID('dbo.DeployProbe') ORDER BY create_date DESC, [object_id] DESC";
        return cmd.ExecuteScalar() as string;
    }

    private static int InstanceCount(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM cdc.change_tables WHERE source_object_id = OBJECT_ID('dbo.DeployProbe')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string[] NewestInstanceColumns(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT cc.column_name FROM cdc.captured_columns cc
                            WHERE cc.object_id = (SELECT TOP 1 ct.object_id FROM cdc.change_tables ct
                                                   WHERE ct.source_object_id = OBJECT_ID('dbo.DeployProbe')
                                                   ORDER BY ct.create_date DESC, ct.object_id DESC)
                            ORDER BY cc.column_name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static void WritePackage(string dir, string db, string tableJson, string templateExtra = "", string betweenScript = null,
                                     string beforeScript = null, string prerequisiteScript = null, string securityPolicyScript = null)
    {
        var tables = Path.Join(dir, "Templates", "Main", "Tables");
        Directory.CreateDirectory(tables);
        var between = Path.Join(dir, "Templates", "Main", "Between");
        if (Directory.Exists(between)) Directory.Delete(between, true);
        var folders = "";
        if (betweenScript != null)
        {
            Directory.CreateDirectory(between);
            File.WriteAllText(Path.Join(between, "Fail.sql"), betweenScript);
            folders = "{ \"FolderPath\": \"Between\", \"QuenchSlot\": \"BetweenTablesAndKeys\" }";
        }
        var before = Path.Join(dir, "Templates", "Main", "Before");
        if (Directory.Exists(before)) Directory.Delete(before, true);
        if (beforeScript != null)
        {
            Directory.CreateDirectory(before);
            File.WriteAllText(Path.Join(before, "Prepare.sql"), beforeScript);
            folders += (folders.Length > 0 ? ", " : "") + "{ \"FolderPath\": \"Before\", \"QuenchSlot\": \"Before\" }";
        }
        var prerequisites = Path.Join(dir, "Templates", "Main", "Prerequisites");
        if (Directory.Exists(prerequisites)) Directory.Delete(prerequisites, true);
        if (prerequisiteScript != null)
        {
            Directory.CreateDirectory(prerequisites);
            File.WriteAllText(Path.Join(prerequisites, "AlwaysEncryptedKeys.sql"), prerequisiteScript);
            folders = "{ \"FolderPath\": \"Prerequisites\", \"QuenchSlot\": \"Objects\" }" + (folders.Length > 0 ? ", " : "") + folders;
        }
        var policies = Path.Join(dir, "Templates", "Main", "SecurityPolicies");
        if (Directory.Exists(policies)) Directory.Delete(policies, true);
        if (securityPolicyScript != null)
        {
            Directory.CreateDirectory(policies);
            File.WriteAllText(Path.Join(policies, "sec.ProbePolicy.sql"), securityPolicyScript);
            folders += (folders.Length > 0 ? ", " : "") + "{ \"FolderPath\": \"SecurityPolicies\", \"QuenchSlot\": \"AfterTablesObjects\", \"ObjectType\": \"SecurityPolicies\" }";
        }
        File.WriteAllText(Path.Join(dir, "Product.json"),
            "{ \"Name\": \"DeployPathProbe\", \"ValidationScript\": \"SELECT CAST(1 AS BIT)\", \"TemplateOrder\": [\"Main\"], "
            + "\"ScriptTokens\": {}, \"ScriptFolders\": [], \"Platform\": \"SqlServer\" }");
        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"),
            "{ \"Name\": \"Main\", \"DatabaseIdentificationScript\": "
            + $"\"SELECT [name] FROM sys.databases WHERE [name] = '{db}'\", \"ScriptFolders\": [{folders}]{templateExtra} }}");
        File.WriteAllText(Path.Join(tables, "dbo.DeployProbe.json"), tableJson);
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        _betweenScript = null;
        _beforeScript = null;
        _prerequisiteScript = null;
        _securityPolicyScript = null;
        _nextDeployFails = false;
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }
}
