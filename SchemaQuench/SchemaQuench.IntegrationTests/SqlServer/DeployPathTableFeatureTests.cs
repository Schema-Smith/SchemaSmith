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
    private bool _nextDeployFails;

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

            // 2. A column change rotates to an instance that includes the new column.
            deploy(CdcTable(enableCdc: true, extraColumn: true));
            Assert.That(NewestInstanceColumns(cmd), Is.EqualTo(new[] { "A", "B", "Id", "Twice" }),
                "a column change must rotate to a capture instance that includes the new column");
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "rotation keeps the old instance for the operator to drain");

            // 3. Leaving EnableCDC out leaves the table's CDC as it is: no disable, no rotation. It used to be disabled,
            //    losing the change table and its history, which nothing could rebuild.
            deploy(CdcTable(enableCdc: null, extraColumn: true));
            Assert.That(IsTrackedByCdc(cmd), Is.True, "an unset EnableCDC must leave the table's CDC alone");
            Assert.That(InstanceCount(cmd), Is.EqualTo(2), "an unset EnableCDC must not rotate the capture instance");

            // 4. Only an explicit false turns it off.
            deploy(CdcTable(enableCdc: false, extraColumn: true));
            Assert.That(IsTrackedByCdc(cmd), Is.False, "EnableCDC false must disable CDC on the table");
        });
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
        });
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
        }, policy: policy);
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
        }, policy: policy);
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
        });
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

    private void RunScenario(string prefix, string setupDatabase, Action<Action<string>, string, IDbCommand> body,
                             string templateExtra = "", bool expectFailure = false, string policy = "warn", bool xmlIngest = false)
    {
        var db = prefix + "_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
                ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master",
                    config["Target:User"], config["Target:Password"], config["Target:Port"], connProps));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var savedPath = config["SchemaPackagePath"];
            var savedEncoding = config["Target:CompatEncoding"];

            try
            {
                cmd.CommandText = $"CREATE DATABASE [{db}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                if (setupDatabase != null) ExecuteWithDeadlockRetry(cmd, setupDatabase);
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, policy: policy);
                config["SchemaPackagePath"] = tempDir;
                // The encoding is chosen during version detection, which SkipKindlingForge also skips.
                if (xmlIngest) config["Target:CompatEncoding"] = "legacy";

                body(tableJson =>
                {
                    WritePackage(tempDir, db, tableJson, templateExtra, _betweenScript, _beforeScript);
                    _environment.ClearReceivedCalls();
                    Program.Main(xmlIngest ? [] : ["SkipKindlingForge"]);
                    var fails = expectFailure || _nextDeployFails;
                    _nextDeployFails = false;
                    if (fails) _environment.Received().Exit(Arg.Is<int>(code => code != 0));
                    else _environment.DidNotReceive().Exit(Arg.Is<int>(code => code != 0));
                }, db, cmd);
            }
            finally
            {
                config["SchemaPackagePath"] = savedPath;
                config["Target:CompatEncoding"] = savedEncoding;
                try
                {
                    conn.ChangeDatabase("master");
                    cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN "
                                      + $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
                    cmd.ExecuteNonQuery();
                }
                catch (DbException) { /* best-effort cleanup */ }
                catch (InvalidOperationException) { /* connection already unusable */ }
                conn.Close();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
    }

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
                                     string beforeScript = null)
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
        _nextDeployFails = false;
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }
}
