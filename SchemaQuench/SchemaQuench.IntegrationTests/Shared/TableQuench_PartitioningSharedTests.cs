// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using MySqlConnector;
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
            var json = cmd.ExecuteScalar()?.ToString() ?? "";

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
            var json = cmd.ExecuteScalar()?.ToString() ?? "";

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

    private static string WithHashPartitioning(string table, int count) => $$"""
[
  {
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "varchar(50)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ],
    "Partitioning": { "Method": "HASH", "Expression": "Id", "PartitionCount": {{count}} }
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

    private int LivePartitionCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.PARTITIONS
 WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND PARTITION_NAME IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
