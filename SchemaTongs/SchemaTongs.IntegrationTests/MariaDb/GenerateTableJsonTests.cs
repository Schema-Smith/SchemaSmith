// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain.MariaDb;
using Schema.Domain;
using SchemaTongs.IntegrationTests.Shared;

namespace SchemaTongs.IntegrationTests.MariaDb;

[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class GenerateTableJsonTests : GenerateTableJsonSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string ConfigPrefix => "MariaDB";

    // MariaDB keeps integer display widths and reports the default FK action as RESTRICT.
    protected override string ExpectedIntegerType(string canonical) => canonical switch
    {
        "tinyint" => "tinyint(4)",
        "smallint" => "smallint(6)",
        "mediumint" => "mediumint(9)",
        "int" => "int(11)",
        "bigint" => "bigint(20)",
        _ => canonical
    };

    protected override string ExpectedDefaultFkAction => "RESTRICT";

    /// <summary>
    /// A system-versioned table must EXTRACT. Before this, it was silently omitted from the package --
    /// no error, no warning -- because the extraction filters accepted only TABLE_TYPE = 'BASE TABLE'
    /// and MariaDB reports such a table as 'SYSTEM VERSIONED'.
    /// <para>Both authoring forms are covered deliberately. The implicit form
    /// (<c>WITH SYSTEM VERSIONING</c>, no declared period columns) exposes nothing but its TABLE_TYPE --
    /// no PERIODS row, no period columns, empty CREATE_OPTIONS -- so any detection keyed on periods or
    /// columns sees it as an ordinary table. Testing only the explicit form would pass while the
    /// commoner form stayed broken.</para>
    /// <para>MariaDB-only: MySQL has no system versioning at any version, which is why this lives here
    /// rather than in the shared fixture.</para>
    /// </summary>
    [Test]
    public void ShouldExtractSystemVersionedTables_WithExplicitPeriodColumnsByTheirRole()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // System versioning arrives in 10.3; the supported floor is 10.2, where the CREATE below is a
        // syntax error rather than a degrade, so there is no state to exercise.
        if (ServerVersionNum < 1003)
            Assert.Ignore($"MariaDB {ServerVersionNum} predates system-versioned tables (10.3).");

        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`ImplicitVersioned` (
    `Id` INT NOT NULL PRIMARY KEY, `Val` VARCHAR(20) NULL
) ENGINE=InnoDB WITH SYSTEM VERSIONING;

CREATE TABLE `{_integrationDb}`.`ExplicitVersioned` (
    `Id` INT NOT NULL PRIMARY KEY, `Val` VARCHAR(20) NULL,
    `RowStart` TIMESTAMP(6) GENERATED ALWAYS AS ROW START,
    `RowEnd` TIMESTAMP(6) GENERATED ALWAYS AS ROW END,
    PERIOD FOR SYSTEM_TIME(`RowStart`, `RowEnd`)
) ENGINE=InnoDB WITH SYSTEM VERSIONING;

CREATE TABLE `{_integrationDb}`.`NotVersioned` (`Id` INT NOT NULL PRIMARY KEY) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();

        var implicitJson = GenerateTableJson(cmd, _integrationDb, "ImplicitVersioned");
        var explicitJson = GenerateTableJson(cmd, _integrationDb, "ExplicitVersioned");
        var plainJson = GenerateTableJson(cmd, _integrationDb, "NotVersioned");

        // Asserted before deserializing: an omitted table yields empty/no JSON, and a deserialize
        // failure there would report as a parse error rather than as "the table was dropped".
        Assert.Multiple(() =>
        {
            Assert.That(implicitJson, Is.Not.Null.And.Not.Empty,
                "The implicit form extracted to nothing -- this is the silent omission the fix exists to stop.");
            Assert.That(explicitJson, Is.Not.Null.And.Not.Empty,
                "The explicit form extracted to nothing -- this is the silent omission the fix exists to stop.");
        });

        var implicitTable = (MariaDbTable)PlatformDeserializer.DeserializeTable(implicitJson, Platform);
        var explicitTable = (MariaDbTable)PlatformDeserializer.DeserializeTable(explicitJson, Platform);
        var plainTable = (MariaDbTable)PlatformDeserializer.DeserializeTable(plainJson, Platform);

        Assert.Multiple(() =>
        {
            Assert.That(implicitTable.IsSystemVersioned, Is.True,
                "TABLE_TYPE is the only signal the implicit form gives, and it must be enough.");
            Assert.That(explicitTable.IsSystemVersioned, Is.True);
            Assert.That(plainTable.IsSystemVersioned, Is.False,
                "An ordinary table must not be reported as versioned.");
            Assert.That(plainJson, Does.Not.Contain("IsSystemVersioned"),
                "An ordinary table's package must omit the key entirely rather than carry it as false.");

            // The engine owns these columns, so they are written with their role rather than as ordinary or generated
            // columns: an ordinary one would have the deploy try to manage it, and a generated one would carry the
            // pseudo-expression ROW START. The implicit form's hidden pair is not written at all.
            var explicitColumns = explicitTable.Columns.OfType<MariaDbColumn>().ToDictionary(c => c.Name.Replace("`", ""));
            Assert.That(explicitColumns.Keys, Is.EquivalentTo(new[] { "Id", "Val", "RowStart", "RowEnd" }));
            Assert.That($"{explicitColumns["RowStart"].GeneratedAlways}|{explicitColumns["RowEnd"].GeneratedAlways}", Is.EqualTo("RowStart|RowEnd"),
                "each period column with its role");
            Assert.That(explicitColumns["RowStart"].GenerationExpression ?? explicitColumns["RowEnd"].GenerationExpression, Is.Null,
                "and not as a generated column");
            Assert.That(implicitTable.Columns.Select(c => c.Name.Replace("`", "")), Is.EquivalentTo(new[] { "Id", "Val" }),
                "the implicit pair is the engine's and is not written");
        });

        conn.Close();
    }

    /// <summary>
    /// Application-time periods extract, and a system-versioned table does not acquire a phantom one.
    /// <para>That second half is the trap. MariaDB lists <c>SYSTEM_TIME</c> in
    /// <c>INFORMATION_SCHEMA.PERIODS</c> alongside real application periods on an explicitly-versioned
    /// table, so a naive read gives such a table a period whose columns extraction already excludes --
    /// a package declaring the same state twice, in two shapes that can then disagree.</para>
    /// <para><b>Version-gated on 11.4, not on 10.4.3, and the difference is the feature's sharpest
    /// edge.</b> Periods themselves arrive in 10.4.3, but the catalog that reports them does not land
    /// until 11.4. Between those releases a period can exist and nothing can be asked about it, so this
    /// skips rather than pretending the blind spot is a pass.</para>
    /// </summary>
    [Test]
    public void ShouldExtractApplicationTimePeriods_AndNotInventOneForSystemVersioning()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        if (ServerVersionNum < 1104)
            Assert.Ignore(
                $"MariaDB {ServerVersionNum} has no INFORMATION_SCHEMA.PERIODS (11.4+). Application-time "
                + "periods arrive in 10.4.3, so on 10.4.3-11.3 the state can exist and no catalog can "
                + "report it -- a documented blind spot, not something this test can cover.");

        cmd.CommandText = $@"
CREATE TABLE `{_integrationDb}`.`PeriodOnly` (
    `Id` INT NOT NULL PRIMARY KEY,
    `ValidFrom` DATE NOT NULL, `ValidTo` DATE NOT NULL,
    PERIOD FOR `Validity`(`ValidFrom`, `ValidTo`)
) ENGINE=InnoDB;

CREATE TABLE `{_integrationDb}`.`VersionedWithExplicitPeriodCols` (
    `Id` INT NOT NULL PRIMARY KEY,
    `RowStart` TIMESTAMP(6) GENERATED ALWAYS AS ROW START,
    `RowEnd` TIMESTAMP(6) GENERATED ALWAYS AS ROW END,
    PERIOD FOR SYSTEM_TIME(`RowStart`, `RowEnd`)
) ENGINE=InnoDB WITH SYSTEM VERSIONING;

CREATE TABLE `{_integrationDb}`.`OrdinaryTable` (`Id` INT NOT NULL PRIMARY KEY) ENGINE=InnoDB;
";
        cmd.ExecuteNonQuery();

        var periodJson = GenerateTableJson(cmd, _integrationDb, "PeriodOnly");
        var versionedJson = GenerateTableJson(cmd, _integrationDb, "VersionedWithExplicitPeriodCols");
        var plainJson = GenerateTableJson(cmd, _integrationDb, "OrdinaryTable");

        var periodTable = (MariaDbTable)PlatformDeserializer.DeserializeTable(periodJson, Platform);
        var versionedTable = (MariaDbTable)PlatformDeserializer.DeserializeTable(versionedJson, Platform);
        var plainTable = (MariaDbTable)PlatformDeserializer.DeserializeTable(plainJson, Platform);

        Assert.Multiple(() =>
        {
            Assert.That(periodTable.Periods, Has.Count.EqualTo(1),
                "The declared application-time period must survive extraction.");
            var p = periodTable.Periods[0];
            Assert.That(p.Name, Is.EqualTo("Validity"));
            Assert.That(p.StartColumn, Is.EqualTo("ValidFrom"));
            Assert.That(p.EndColumn, Is.EqualTo("ValidTo"),
                "Start and end must not be transposed -- the interval would silently invert.");
            Assert.That(periodTable.CheckConstraints.Select(c => c.Name), Does.Not.Contain("Validity"),
                "the engine's own check for the period (ValidFrom < ValidTo) is the period's, not a declared check");

            Assert.That(versionedTable.Periods, Is.Empty,
                "SYSTEM_TIME is listed in PERIODS for an explicitly-versioned table, but it is already "
                + "described by IsSystemVersioned and its columns are excluded. Surfacing it here as well "
                + "would have the package declare the same state twice.");
            Assert.That(versionedTable.IsSystemVersioned, Is.True,
                "...and the versioning itself must still be reported, or the exclusion has thrown the "
                + "state away instead of relocating it.");

            Assert.That(plainTable.Periods, Is.Empty);
            Assert.That(plainJson, Does.Not.Contain("\"Periods\""),
                "A table with no periods must omit the key entirely rather than carry an empty array. "
                + "Matched on the quoted key: a bare \"Periods\" substring also matches a table NAME that "
                + "happens to contain it, which is how this assertion first failed against a table called NoPeriods.");
        });

        conn.Close();
    }


}
