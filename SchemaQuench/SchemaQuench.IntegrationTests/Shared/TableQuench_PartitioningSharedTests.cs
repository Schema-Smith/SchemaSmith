// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using MySqlConnector;
using Newtonsoft.Json.Linq;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

// MySQL / MariaDB partitioning (#partitioning, K3). ADOPT AND VERIFY: the partition definition is applied
// at CREATE, and thereafter compared against the live layout and REFUSED by name when the two disagree.
// ALTER TABLE ... PARTITION BY rewrites every row of the table, and a state-based diff cannot derive the
// SPLIT/MERGE intent behind a changed boundary -- it can only see that two layouts differ -- so nothing here
// ever emits it.
//
// Unlike SQL Server there is no partition-scheme object to point at: MySQL carries the whole definition in
// the table DDL, so the package has to carry it too. The POSTURE is identical even though the shape is not.
//
// THE COMPARISON NORMALIZES, and the supported floor is why. The engines disagree about how they report the
// partition expression back: MySQL 5.7 returns the text the user wrote (YEAR(dt)) while MySQL 8,
// MariaDB 10.2 and MariaDB 11.4 all return a rewritten form (year(`dt`)). A literal compare would refuse a
// package extracted on 5.7 and deployed to 8 -- a false alarm on an identical layout.
//
// Each test owns a UNIQUE product name so it is scoped to its own tables under parallel execution.
[Category("Integration")]
public abstract class TableQuench_PartitioningSharedTests : BaseTableQuenchTests
{
    // ---- create ---------------------------------------------------------------

    [Test]
    public void TableQuench_DeclaredRangePartitioning_IsCreated()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartRangeProduct_{uid}";
        var table = $"PartRangeTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(LiveMethod(cmd, table), Is.EqualTo("RANGE"),
                    "a declared partitioning that does not reach the engine is the silent loss this closes");
                Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"),
                    "and the partitions must arrive IN ORDER -- RANGE boundaries have to ascend, so order "
                    + "is part of the definition rather than presentation");
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_DeclaredHashPartitioning_IsCreatedWithItsCount()
    {
        // HASH and KEY have no per-partition boundary -- they carry a COUNT instead, which is a different
        // shape of declaration and a separate emit path.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartHashProduct_{uid}";
        var table = $"PartHashTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithHashPartitioning(table, 4), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(LiveMethod(cmd, table), Is.EqualTo("HASH"));
                Assert.That(LivePartitionCount(cmd, table), Is.EqualTo(4),
                    "PartitionCount is the whole declaration for HASH; getting it wrong changes how rows "
                    + "are distributed with nothing to report it");
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // The count methods. LINEAR HASH and LINEAR KEY fell into the RANGE/LIST branch of the create emit, which writes a
    // partition list, so the CREATE carried an empty one and failed; and MariaDB extracted every count as a string, which
    // the package's schema refuses as an integer.
    [TestCase("HASH")]
    [TestCase("KEY")]
    [TestCase("LINEAR HASH")]
    [TestCase("LINEAR KEY")]
    public void TableQuench_CountPartitioning_IsCreatedWithItsCount_RedeploysAsANoOp_AndExtractsAsDeclared(string method)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartLinearProduct_{uid}";
        var table = $"PartLinearTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithHashPartitioning(table, 3, method), productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(LiveMethod(cmd, table), Is.EqualTo(method));
                Assert.That(LivePartitionCount(cmd, table), Is.EqualTo(3));
            });

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithHashPartitioning(table, 3, method), productName: product),
                "an unchanged LINEAR table redeploys cleanly");
            Assert.That(LivePartitionCount(cmd, table), Is.EqualTo(3), "and is untouched");

            cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_mainDb}', '{table}')";
            var json = TransientRetry.Run(() => cmd.ExecuteScalar())?.ToString() ?? "";
            var partitioning = json.Contains("Partitioning") ? json[json.IndexOf("\"Partitioning\"", StringComparison.Ordinal)..] : json;
            Assert.That(partitioning, Does.Contain($"\"Method\": \"{method}\"").And.Contain("\"PartitionCount\": 3"), partitioning);
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_RedeployingAPartitionedTable_IsANoOp()
    {
        // The case the normalization exists for. If the comparison were literal, this would fail on every
        // engine that rewrites the expression -- which is every engine except MySQL 5.7.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartIdemProduct_{uid}";
        var table = $"PartIdemTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product);

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product),
                "an unchanged partitioned table must redeploy cleanly");
            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"), "and be untouched");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_AnExpressionWrittenDifferentlyFromHowTheEngineReportsIt_StillMatches()
    {
        // Declared as "Id" with different spacing/case from whatever the engine echoes back. On MySQL 8 and
        // MariaDB the catalog returns `Id` (backticked); on MySQL 5.7 it returns the original text. Both
        // must compare equal to the declaration, or a package becomes engine-specific.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartNormProduct_{uid}";
        var table = $"PartNormTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product);

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithRangePartitioning(table, expression: " ID "), productName: product),
                "backticks, whitespace and case are normalized away before comparing -- otherwise the same "
                + "package would deploy on one engine and be refused on another");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // ---- refusals -------------------------------------------------------------

    [Test]
    public void TableQuench_ChangingTheDeclaredPartitioning_ThrowsAndChangesNothing()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartChangeProduct_{uid}";
        var table = $"PartChangeTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product);
            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"), "Setup: three range partitions.");

            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithHashPartitioning(table, 4), productName: product),
                "changing a live table's partitioning rewrites every row -- it must be refused, not applied");

            cmd.CommandText = "SELECT GROUP_CONCAT(Message SEPARATOR ' | ') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            var log = cmd.ExecuteScalar()?.ToString() ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(log, Does.Contain(table),
                    $"the run log must name the offending table -- MySQL's SIGNAL MESSAGE_TEXT is capped at "
                    + $"128 characters, so the detail goes there. Log: {log}");
                Assert.That(LiveMethod(cmd, table), Is.EqualTo("RANGE"),
                    "and NOTHING may have changed -- a half-applied repartition is worse than a refusal");
                Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"));
            });
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_DeclaringPartitioningOnAnExistingPlainTable_ThrowsAndChangesNothing()
    {
        // Adopting an existing table into partitioning is the same whole-table rewrite as changing it.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartAdoptProduct_{uid}";
        var table = $"PartAdoptTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithPlainTable(table), productName: product);
            Assert.That(LiveMethod(cmd, table), Is.Null, "Setup: table starts unpartitioned.");

            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product),
                "partitioning an existing table rewrites every row -- refuse rather than attempt it");
            Assert.That(LiveMethod(cmd, table), Is.Null, "and the table must still be unpartitioned");
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_APartitionedTableThePackageDoesNotMention_IsLeftAlone()
    {
        // The pre-existing DBA-partitioned table: a package that never mentions partitioning must keep
        // deploying against it untouched. An unset Partitioning means "SchemaSmith does not manage this
        // here" -- it is NOT a declaration that the table is unpartitioned. Getting this wrong would break
        // every package in the wild that happens to target a partitioned table.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartUnmanagedProduct_{uid}";
        var table = $"PartUnmanagedTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithPlainTable(table), productName: product);
            cmd.CommandText = $"ALTER TABLE `{_mainDb}`.`{table}` PARTITION BY RANGE (`Id`) "
                              + "(PARTITION p0 VALUES LESS THAN (100), PARTITION pmax VALUES LESS THAN MAXVALUE)";
            cmd.ExecuteNonQuery();
            Assert.That(LiveMethod(cmd, table), Is.EqualTo("RANGE"), "Setup: partitioned by hand.");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithPlainTable(table), productName: product),
                "a package that says nothing about partitioning must not be refused against a partitioned "
                + "table it never claimed to own");
            Assert.That(LiveMethod(cmd, table), Is.EqualTo("RANGE"), "and the partitioning must survive");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_NoPartitioningDeclared_IsCompletelyUnaffected()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartPlainProduct_{uid}";
        var table = $"PartPlainTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithPlainTable(table), productName: product);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithPlainTable(table), productName: product),
                "an ordinary table must still redeploy as a no-op");
            Assert.That(LiveMethod(cmd, table), Is.Null, "and stay unpartitioned");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_PartitioningRoundTripsThroughExtraction()
    {
        // Deploy declared, read it back through the same procedure SchemaTongs uses. Before this,
        // extraction said nothing about partitioning at all, so a partitioned table extracted as an
        // ordinary one -- cleanly, with a success message, and wrong.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartRoundProduct_{uid}";
        var table = $"PartRoundTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product);

            cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_mainDb}', '{table}')";
            var json = TransientRetry.Run(() => cmd.ExecuteScalar())?.ToString() ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("Partitioning"),
                    "the extracted package must carry the partitioning\n" + json);
                Assert.That(json, Does.Contain("RANGE"), json);
                // Order is the point: the boundaries must ascend, so p0 has to precede pmax in the output
                // or the package redeploys a definition the engine rejects.
                Assert.That(json.IndexOf("p0", StringComparison.Ordinal),
                    Is.LessThan(json.IndexOf("pmax", StringComparison.Ordinal)),
                    "partitions must extract in declared order, not alphabetically\n" + json);
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_AnUnpartitionedTable_ExtractsWithNoPartitioningKey()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartNoneProduct_{uid}";
        var table = $"PartNoneTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithPlainTable(table), productName: product);

            cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_mainDb}', '{table}')";
            var json = TransientRetry.Run(() => cmd.ExecuteScalar())?.ToString() ?? "";

            Assert.That(json, Does.Not.Contain("Partitioning"),
                "an ordinary table must gain no partitioning key -- otherwise every committed .json in "
                + "the wild churns on the next extraction\n" + json);
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // ---- partition list (K3 boundary maintenance) -----------------------------

    // The comparison above covers Method, Expression and -- for HASH/KEY -- the declared PartitionCount.
    // It did NOT cover the named Partitions array, so adding next year's boundary partition to the package
    // did nothing and said nothing: exit 0, no partition line in the log, table unchanged. RANGE without a
    // MAXVALUE catch-all then REJECTS the insert once the calendar reaches it ("Table has no partition for
    // value 2027"), so a team that adds the partition in December, sees a green deploy and moves on starts
    // failing writes on 1 January.
    //
    // Appending above the current maximum is the one partitioning change that should be APPLIED rather than
    // refused: ALTER TABLE ... ADD PARTITION moves no existing row. Everything else in the array -- removal,
    // a moved boundary, a partition inserted below the maximum -- redistributes or destroys rows and takes
    // the same refusal path the Expression case already uses. Silence was the one option that could not be
    // right.

    [Test]
    public void TableQuench_ADeclaredPartitionAboveTheDeployedMaximum_IsAdded()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartAddProduct_{uid}";
        var table = $"PartAddTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "100" }, { "Name": "p1", "Values": "200" } ]"""),
                productName: product);
            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1"), "Setup: two range partitions.");

            RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "100" }, { "Name": "p1", "Values": "200" }, { "Name": "p2", "Values": "300" } ]"""),
                productName: product);

            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,p2"),
                "appending a boundary partition above the current maximum moves no rows -- ADD PARTITION "
                + "is safe, and it is the common 'add next year' case the feature exists to serve");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_AddingAPartitionIsIdempotent()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartIdemProduct_{uid}";
        var table = $"PartIdemTable_{uid}";
        var defs = WithRangePartitions(table,
            """[ { "Name": "p0", "Values": "100" }, { "Name": "p1", "Values": "200" } ]""");

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            RunTableQuenchProc(cmd, defs, productName: product);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1"),
                "three passes over an unchanged declaration must add nothing -- a comparison that is not "
                + "stable would churn the table on every deploy, which is the defect class this must "
                + "not introduce while closing another");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_ARemovedPartition_IsRefusedByName()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartRemoveProduct_{uid}";
        var table = $"PartRemoveTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product);
            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"), "Setup: three partitions.");

            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "100" }, { "Name": "pmax", "Values": "MAXVALUE" } ]"""),
                productName: product),
                "dropping a RANGE partition destroys every row in it -- refuse, never apply");

            cmd.CommandText = "SELECT GROUP_CONCAT(Message SEPARATOR ' | ') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            var log = cmd.ExecuteScalar()?.ToString() ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(log, Does.Contain(table),
                    $"the run log must name the offending table -- SIGNAL MESSAGE_TEXT is capped at 128 "
                    + $"characters, so the detail goes there. Log: {log}");
                Assert.That(log, Does.Contain("p1"),
                    $"and it must name the PARTITION, not just the table -- 'something about partitioning "
                    + $"differs' is not actionable on a table with a dozen of them. Log: {log}");
                Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"),
                    "and NOTHING may have changed");
            });
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_AChangedPartitionBoundary_IsRefusedByName()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartBoundProduct_{uid}";
        var table = $"PartBoundTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "100" }, { "Name": "p1", "Values": "200" } ]"""),
                productName: product);

            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "150" }, { "Name": "p1", "Values": "200" } ]"""),
                productName: product),
                "moving a boundary redistributes rows between partitions, and a state diff cannot derive "
                + "the SPLIT/MERGE intent behind it -- it can only see that two layouts differ");

            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1"), "nothing may have changed");
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_APartitionInsertedBelowTheMaximum_IsRefusedByName()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartMidProduct_{uid}";
        var table = $"PartMidTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "100" }, { "Name": "p2", "Values": "300" } ]"""),
                productName: product);

            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            // p1 lands BETWEEN two deployed partitions -- a SPLIT of p2's range, not an append.
            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "100" }, { "Name": "p1", "Values": "200" }, { "Name": "p2", "Values": "300" } ]"""),
                productName: product),
                "a partition below the deployed maximum splits an existing range and moves its rows -- "
                + "ADD PARTITION cannot express it, so it is refused rather than silently skipped");

            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p2"), "nothing may have changed");
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // A MAXVALUE tail is the common shape, and nothing can be appended above it -- MySQL rejects
    // ADD PARTITION after a MAXVALUE partition. That is a REORGANIZE, not an append, so it refuses.
    [Test]
    public void TableQuench_AddingAPartitionAboveAMaxValueTail_IsRefusedByName()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartMaxProduct_{uid}";
        var table = $"PartMaxTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitioning(table), productName: product);
            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"), "Setup: MAXVALUE tail.");

            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithRangePartitions(table,
                """[ { "Name": "p0", "Values": "100" }, { "Name": "p1", "Values": "200" }, { "Name": "p2", "Values": "300" }, { "Name": "pmax", "Values": "MAXVALUE" } ]"""),
                productName: product),
                "nothing sits above MAXVALUE -- the engine rejects ADD PARTITION after it, so this is a "
                + "REORGANIZE and must be refused by name rather than attempted and failed");

            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p0,p1,pmax"), "nothing may have changed");
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // ---- subpartitions, named HASH/KEY partitions, comments, KEY ALGORITHM (D8 slice 2) ----------

    // A subpartitioned table has one catalog row per subpartition. Extraction wrote each as a partition of its own, with
    // repeated names and no SUBPARTITION BY, so the package could not be deployed.
    [Test]
    public void TableQuench_SubpartitionedTable_IsCreated_RedeploysAsANoOp_AndExtractsAsACount()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartSubProduct_{uid}";
        var table = $"PartSubTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithSubpartitions(table, 2), productName: product);
            Assert.That(LiveSubpartitionNames(cmd, table), Is.EqualTo("p0sp0,p0sp1,pmaxsp0,pmaxsp1"));

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithSubpartitions(table, 2), productName: product),
                "an unchanged subpartitioned table redeploys");

            var partitioning = ExtractPartitioning(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That((string)partitioning["Subpartitioning"]?["Method"], Is.EqualTo("HASH"), partitioning.ToString());
                Assert.That((int?)partitioning["Subpartitioning"]?["Count"], Is.EqualTo(2), partitioning.ToString());
                Assert.That(((JArray)partitioning["Partitions"]).Count, Is.EqualTo(2), "one entry per partition, not per subpartition");
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_AChangedSubpartitionCount_IsRefused()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartSubCountProduct_{uid}";
        var table = $"PartSubCountTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithSubpartitions(table, 2), productName: product);
            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithSubpartitions(table, 3), productName: product),
                "changing the subpartition count rewrites every row");
            Assert.That(LiveSubpartitionNames(cmd, table), Is.EqualTo("p0sp0,p0sp1,pmaxsp0,pmaxsp1"), "and nothing changed");
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // HASH and KEY partitions may carry names and comments of their own. Extraction wrote a count, so the names and the
    // comments were lost; and once named, adding one is not an append: every row's partition depends on the count.
    [Test]
    public void TableQuench_NamedHashPartitions_KeepTheirNamesAndComments_AndAddingOneIsRefused()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartNamedProduct_{uid}";
        var table = $"PartNamedTable_{uid}";
        const string two = """[ { "Name": "alpha", "Comment": "it's first" }, { "Name": "beta" } ]""";
        const string three = """[ { "Name": "alpha", "Comment": "it's first" }, { "Name": "beta" }, { "Name": "gamma" } ]""";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithNamedHashPartitions(table, two), productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("alpha,beta"));
                Assert.That(LivePartitionComment(cmd, table, "alpha"), Is.EqualTo("it's first"));
            });
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithNamedHashPartitions(table, two), productName: product));

            var partitioning = ExtractPartitioning(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(partitioning["PartitionCount"], Is.Null, partitioning.ToString());
                Assert.That(partitioning["Partitions"]?.Select(x => (string)x["Name"]), Is.EqualTo(new[] { "alpha", "beta" }));
                Assert.That((string)partitioning["Partitions"]?[0]?["Comment"], Is.EqualTo("it's first"));
                Assert.That(partitioning["Partitions"]?[0]?["Values"], Is.Null, "a HASH partition has no boundary");
            });

            var refused = Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithNamedHashPartitions(table, three), productName: product));
            Assert.That(refused!.Message, Does.Contain("Repartitioning rewrites every row and is refused"), "refused, not failed on a bad ALTER");
            cmd.CommandText = "SELECT GROUP_CONCAT(Message) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%rehashes them all%'";
            Assert.That(cmd.ExecuteScalar() as string, Does.Contain(table), "the run log names the table and why");
            Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("alpha,beta"), "and nothing was added");
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // The engine changes a partition's comment only by reorganizing the partition, which rewrites its rows.
    [Test]
    public void TableQuench_AChangedPartitionComment_IsReportedNotApplied()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartCommentProduct_{uid}";
        var table = $"PartCommentTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitions(table, """[ { "Name": "p0", "Values": "100", "Comment": "old" }, { "Name": "pmax", "Values": "MAXVALUE" } ]"""), productName: product);
            Assert.That(LivePartitionComment(cmd, table, "p0"), Is.EqualTo("old"));
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithRangePartitions(table, """[ { "Name": "p0", "Values": "100", "Comment": "new" }, { "Name": "pmax", "Values": "MAXVALUE" } ]"""), productName: product));

            cmd.CommandText = "SELECT GROUP_CONCAT(Message) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%reported, not applied%'";
            var report = cmd.ExecuteScalar() as string;
            Assert.Multiple(() =>
            {
                Assert.That(report, Is.Not.Null, "the difference is reported");
                Assert.That(report, Does.StartWith("  WARNING:").And.Contain("'new'").And.Contain("'old'"));
                Assert.That(LivePartitionComment(cmd, table, "p0"), Is.EqualTo("old"), "and not applied");
            });
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_KeyAlgorithmOne_IsCreatedWithIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartAlgoProduct_{uid}";
        var table = $"PartAlgoTable_{uid}";
        var package = WithHashPartitioning(table, 2, "KEY").Replace("\"PartitionCount\": 2", "\"PartitionCount\": 2, \"KeyAlgorithm\": 1");

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            Assert.That(package, Does.Contain("KeyAlgorithm"));
            RunTableQuenchProc(cmd, package, productName: product);
            cmd.CommandText = $"SHOW CREATE TABLE `{_mainDb}`.`{table}`";
            using var reader = cmd.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(Schema.Utility.MySqlKeyAlgorithm.Read(reader.GetString(1)), Is.EqualTo(1), reader.GetString(1));
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // An appended LIST partition was added with VALUES LESS THAN, which a LIST table refuses.
    [Test]
    public void TableQuench_ADeclaredListPartitionAppended_IsAdded()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartListAddProduct_{uid}";
        var table = $"PartListAddTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithListColumnsPartitions(table, "'east'"), productName: product);
            var withNorth = WithListColumnsPartitions(table, "'east'").Replace(
                "{ \"Name\": \"p_west\", \"Values\": \"'west'\" }",
                "{ \"Name\": \"p_west\", \"Values\": \"'west'\" }, { \"Name\": \"p_north\", \"Values\": \"'north'\", \"Comment\": \"added\" }");
            Assert.That(withNorth, Does.Contain("p_north"));
            RunTableQuenchProc(cmd, withNorth, productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(LivePartitionNames(cmd, table), Is.EqualTo("p_east,p_west,p_north"));
                Assert.That(LivePartitionComment(cmd, table, "p_north"), Is.EqualTo("added"));
            });
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    private JObject ExtractPartitioning(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_mainDb}', '{table}')";
        var json = TransientRetry.Run(() => cmd.ExecuteScalar())?.ToString() ?? "";
        return (JObject)JObject.Parse(json)["Partitioning"] ?? throw new AssertionException("no Partitioning in\n" + json);
    }

    // ---- package builders -----------------------------------------------------

    private static string WithPlainTable(string table) => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "varchar(50)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ]
  }
]
""";

    // MySQL requires every UNIQUE/PRIMARY key to contain every partitioning column, so the key is on Id.
    private static string WithRangePartitioning(string table, string expression = "Id") => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "varchar(50)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ],
    "Partitioning": {
      "Method": "RANGE",
      "Expression": "{{expression}}",
      "Partitions": [
        { "Name": "p0",   "Values": "100" },
        { "Name": "p1",   "Values": "200" },
        { "Name": "pmax", "Values": "MAXVALUE" }
      ]
    }
  }
]
""";

    private static string WithHashPartitioning(string table, int count, string method = "HASH") => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "varchar(50)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ],
    "Partitioning": { "Method": "{{method}}", "Expression": "Id", "PartitionCount": {{count}} }
  }
]
""";

    // LIST COLUMNS values are strings, and 'east' and 'EAST' route different rows. The values were compared upper-cased,
    // so a value changed only in case passed as unchanged and the package silently disagreed with the table.
    [Test]
    public void TableQuench_AListValueChangedOnlyInCase_IsRefusedByName()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartListProduct_{uid}";
        var table = $"PartListTable_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithListColumnsPartitions(table, "'east'"), productName: product);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithListColumnsPartitions(table, "'east'"), productName: product),
                "an unchanged list redeploys");

            Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, WithListColumnsPartitions(table, "'EAST'"), productName: product));
        }
        finally
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            DropTable(cmd, table);
        }
        conn.Close();
    }

    // The engine reports the keyword in capitals; a package spelling it in lowercase declares the same partition.
    [Test]
    public void TableQuench_ALowercaseMaxValue_RedeploysUnchanged()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"PartMaxProduct_{uid}";
        var table = $"PartMaxTable_{uid}";
        var partitions = """[ { "Name": "p0", "Values": "100" }, { "Name": "pmax", "Values": "maxvalue" } ]""";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRangePartitions(table, partitions), productName: product);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithRangePartitions(table, partitions), productName: product));
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    private static string WithListColumnsPartitions(string table, string eastValue) => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Region`", "DataType": "varchar(10)", "Nullable": false }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`,`Region`" } ],
    "Partitioning": {
      "Method": "LIST COLUMNS",
      "Expression": "Region",
      "Partitions": [ { "Name": "p_east", "Values": "{{eastValue}}" }, { "Name": "p_west", "Values": "'west'" } ]
    }
  }
]
""";

    // RANGE partitioning with a caller-supplied partition list, so a test can declare a layout differing
    // from the deployed one by exactly one partition. WithRangePartitioning above hard-codes p0/p1/pmax,
    // and a MAXVALUE tail cannot be appended to -- several tests below need a list without one.
    private static string WithRangePartitions(string table, string partitionsJson) => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "varchar(50)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ],
    "Partitioning": {
      "Method": "RANGE",
      "Expression": "Id",
      "Partitions": {{partitionsJson}}
    }
  }
]
""";

    // The key holds both columns: MySQL requires every unique key to include every partitioning column.
    private static string WithSubpartitions(string table, int count) => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Yr`",  "DataType": "int", "Nullable": false }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`,`Yr`" } ],
    "Partitioning": {
      "Method": "RANGE",
      "Expression": "Yr",
      "Subpartitioning": { "Method": "HASH", "Expression": "Id", "Count": {{count}} },
      "Partitions": [ { "Name": "p0", "Values": "2000" }, { "Name": "pmax", "Values": "MAXVALUE" } ]
    }
  }
]
""";

    private static string WithNamedHashPartitions(string table, string partitionsJson) => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [ { "Name": "`Id`",  "DataType": "int", "Nullable": false } ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ],
    "Partitioning": { "Method": "HASH", "Expression": "Id", "Partitions": {{partitionsJson}} }
  }
]
""";

    // ---- live-state readers ---------------------------------------------------

    private void DropTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;";
        cmd.ExecuteNonQuery();
    }

    // NULL when the table is not partitioned. A non-partitioned table yields ONE row here with every
    // partition column NULL rather than no rows at all, which is why this reads the value rather than
    // counting rows.
    private string LiveMethod(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT PARTITION_METHOD FROM INFORMATION_SCHEMA.PARTITIONS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'
 ORDER BY PARTITION_ORDINAL_POSITION LIMIT 1";
        return cmd.ExecuteScalar() as string;
    }

    private string LivePartitionNames(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT GROUP_CONCAT(PARTITION_NAME ORDER BY PARTITION_ORDINAL_POSITION)
  FROM INFORMATION_SCHEMA.PARTITIONS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND PARTITION_NAME IS NOT NULL";
        return cmd.ExecuteScalar() as string;
    }

    private string LiveSubpartitionNames(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT GROUP_CONCAT(SUBPARTITION_NAME ORDER BY PARTITION_ORDINAL_POSITION, SUBPARTITION_ORDINAL_POSITION)
  FROM INFORMATION_SCHEMA.PARTITIONS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND SUBPARTITION_NAME IS NOT NULL";
        return cmd.ExecuteScalar() as string;
    }

    private string LivePartitionComment(IDbCommand cmd, string table, string partition)
    {
        cmd.CommandText = $@"
SELECT MAX(PARTITION_COMMENT) FROM INFORMATION_SCHEMA.PARTITIONS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND PARTITION_NAME = '{partition}'";
        return cmd.ExecuteScalar() as string;
    }

    private int LivePartitionCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.PARTITIONS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND PARTITION_NAME IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
