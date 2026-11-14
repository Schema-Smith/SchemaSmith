// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Validates the slice-2 migration of SchemaSmith.CompletedMigrationScripts: idempotent ALTER
/// adds template_name and schema_name columns; legacy blank-template rows match same-template
/// lookups (permissive); legacy blank-schema rows do NOT shadow per-tenant lookups (strict).
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class MigrationTrackingBackwardCompatTests
{
    private IDbConnection _connection = null!;
    private IDbCommand _command = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _command = _connection.CreateCommand();

        // Reset the table to LEGACY (pre-extension) shape so the upgrade path is exercised.
        DropAndRecreateLegacyTable();
    }

    [TearDown]
    public void TearDown()
    {
        // Restore the table to current shape so subsequent tests inherit the same state
        // as a fresh fixture setup.
        try
        {
            _command.CommandText = "IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts') IS NOT NULL DROP TABLE SchemaSmith.CompletedMigrationScripts";
            _command.ExecuteNonQuery();
            // force: version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);
        }
        finally
        {
            _command.Dispose();
            _connection.Close();
            _connection.Dispose();
        }
    }

    private void DropAndRecreateLegacyTable()
    {
        _command.CommandText = @"
            IF OBJECT_ID('SchemaSmith.CompletedMigrationScripts') IS NOT NULL
                DROP TABLE SchemaSmith.CompletedMigrationScripts;
            CREATE TABLE SchemaSmith.CompletedMigrationScripts (
                ScriptPath  VARCHAR(800) NOT NULL,
                ProductName VARCHAR(100) NOT NULL,
                QuenchSlot  VARCHAR(30)  NOT NULL,
                QuenchDate  DATETIME     NOT NULL DEFAULT GETDATE(),
                CONSTRAINT PK_CompletedMigrationScripts PRIMARY KEY CLUSTERED (ScriptPath, ProductName, QuenchSlot)
            );";
        _command.ExecuteNonQuery();
    }

    [Test]
    public void KindleTheForge_LegacyTableMissingColumns_AddsTemplateNameAndSchemaName()
    {
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts (ScriptPath, ProductName, QuenchSlot)
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before');";
        _command.ExecuteNonQuery();

        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);

        _command.CommandText = @"
            SELECT name FROM sys.columns
            WHERE object_id = OBJECT_ID('SchemaSmith.CompletedMigrationScripts')
              AND name IN ('template_name', 'schema_name')
            ORDER BY name;";
        using var reader = _command.ExecuteReader();
        var found = new System.Collections.Generic.List<string>();
        while (reader.Read()) found.Add(reader.GetString(0));
        reader.Close();
        Assert.That(found, Is.EquivalentTo(new[] { "schema_name", "template_name" }));
    }

    [Test]
    public void KindleTheForge_LegacyVarcharTable_KeepsItsRows_AndStoresANameOutsideTheCodePage()
    {
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts (ScriptPath, ProductName, QuenchSlot)
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before');";
        _command.ExecuteNonQuery();

        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);

        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts (ScriptPath, ProductName, QuenchSlot, template_name, schema_name)
            VALUES (N'Before Scripts/Ω.sql', N'Ω Product', 'Before', N'Main', N'');
            SELECT CONCAT(COUNT(*), '|',
                          SUM(CASE WHEN ScriptPath = N'Before Scripts/Ω.sql' AND ProductName = N'Ω Product' THEN 1 ELSE 0 END), '|',
                          SUM(CASE WHEN ScriptPath = 'Before Scripts/Migration_001.sql' AND ProductName = 'Demo' THEN 1 ELSE 0 END))
            FROM SchemaSmith.CompletedMigrationScripts;";
        Assert.That(_command.ExecuteScalar()?.ToString(), Is.EqualTo("2|1|1"),
            "rows | the name outside the code page, stored whole | the legacy row, kept");
    }

    [Test]
    public void KindleTheForge_LegacyRow_HasBlankTemplateNameAndSchemaName_AfterUpgrade()
    {
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts (ScriptPath, ProductName, QuenchSlot)
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before');";
        _command.ExecuteNonQuery();

        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);

        _command.CommandText = @"
            SELECT template_name, schema_name FROM SchemaSmith.CompletedMigrationScripts
            WHERE ScriptPath = 'Before Scripts/Migration_001.sql';";
        using var reader = _command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo(""));
        Assert.That(reader.GetString(1), Is.EqualTo(""));
    }

    [Test]
    public void PermissiveTemplateLookup_LegacyRow_MatchesNewSameTemplateRead()
    {
        // Pre-extension migration script for the same template, no schema concept yet.
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts (ScriptPath, ProductName, QuenchSlot)
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before');";
        _command.ExecuteNonQuery();

        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);

        // New-style permissive lookup for the same template at no schema scope.
        _command.CommandText = @"
            SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts
            WHERE ProductName = 'Demo'
              AND QuenchSlot = 'Before'
              AND ScriptPath = 'Before Scripts/Migration_001.sql'
              AND template_name IN ('', 'Shared')
              AND schema_name = '';";
        var count = (int)_command.ExecuteScalar();
        Assert.That(count, Is.EqualTo(1), "Legacy template_name='' must match permissive IN ('', @template).");
    }

    [Test]
    public void StrictSchemaLookup_LegacyBlankSchemaRow_DoesNotShadowNewTenantLookup()
    {
        // Pre-extension legacy row (no schema concept).
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts (ScriptPath, ProductName, QuenchSlot)
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before');";
        _command.ExecuteNonQuery();

        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);

        // A new per-tenant lookup for 'tenant_acme'.
        _command.CommandText = @"
            SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts
            WHERE ProductName = 'Demo'
              AND QuenchSlot = 'Before'
              AND ScriptPath = 'Before Scripts/Migration_001.sql'
              AND template_name IN ('', 'TenantBody')
              AND schema_name = 'tenant_acme';";
        var count = (int)_command.ExecuteScalar();
        Assert.That(count, Is.EqualTo(0),
            "Strict schema_name = @schema must NOT match a legacy blank-schema row when looking up a tenant.");
    }

    [Test]
    public void NewWrites_PopulateActualTemplateAndSchemaValues()
    {
        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);

        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts
                (ScriptPath, ProductName, QuenchSlot, template_name, schema_name)
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before', 'TenantBody', 'tenant_acme');";
        _command.ExecuteNonQuery();

        _command.CommandText = @"
            SELECT template_name, schema_name FROM SchemaSmith.CompletedMigrationScripts
            WHERE ScriptPath = 'Before Scripts/Migration_001.sql' AND template_name = 'TenantBody';";
        using var reader = _command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("TenantBody"));
        Assert.That(reader.GetString(1), Is.EqualTo("tenant_acme"));
    }

    [Test]
    public void EagerMigrate_ClaimsLegacyBlankRow_WhenScriptIsOnDiskForCurrentTemplate()
    {
        // Transitional slice-2 mechanic: when a permissive SELECT matches a legacy
        // blank-template row AND the row's ScriptPath is in the current template's on-disk
        // script set, an UPDATE claims the row by setting template_name to the current
        // template. Future prune passes can then clean the row up correctly.
        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts
                ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name])
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before', '', '');";
        _command.ExecuteNonQuery();

        // Simulate the eager-migrate UPDATE that DatabaseQuench would emit when 'Main'
        // sees the legacy row's ScriptPath in its on-disk script list.
        _command.CommandText = @"
            UPDATE SchemaSmith.CompletedMigrationScripts
            SET [template_name] = 'Main'
            WHERE [ProductName] = 'Demo' AND [QuenchSlot] = 'Before'
              AND [template_name] = '' AND [schema_name] = ''
              AND [ScriptPath] IN ('Before Scripts/Migration_001.sql');";
        _command.ExecuteNonQuery();

        _command.CommandText = @"
            SELECT template_name FROM SchemaSmith.CompletedMigrationScripts
            WHERE ScriptPath = 'Before Scripts/Migration_001.sql';";
        Assert.That(_command.ExecuteScalar()?.ToString(), Is.EqualTo("Main"),
            "Eager-migrate must have set template_name to the claiming template.");

        // After claim, a subsequent strict DELETE (the prune path) for Main now matches the row.
        _command.CommandText = @"
            DELETE SchemaSmith.CompletedMigrationScripts
            WHERE [ProductName] = 'Demo' AND [QuenchSlot] = 'Before'
              AND [ScriptPath] = 'Before Scripts/Migration_001.sql'
              AND [template_name] = 'Main' AND [schema_name] = '';";
        _command.ExecuteNonQuery();

        _command.CommandText = @"
            SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts
            WHERE ScriptPath = 'Before Scripts/Migration_001.sql';";
        Assert.That((int)_command.ExecuteScalar(), Is.EqualTo(0),
            "Once claimed, a future prune can clean the row up correctly.");
    }

    [Test]
    public void DeleteIsStrictOnTemplateName_LegacyBlankTemplateRow_NotSharedAcrossTemplates()
    {
        // Regression: a pre-extension product with templates A and B that both have a script
        // with the same filename (e.g. Migration_001.sql) had ONE legacy tracking row with
        // template_name=''. Both A's and B's permissive SELECTs match it. If A's PruneObsolete
        // step DELETEd permissively on template_name IN ('', 'A'), it would nuke the row B
        // still needs. Strict DELETE on template_name = @template prevents this.
        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts
                ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name])
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before', '', '');";
        _command.ExecuteNonQuery();

        // Simulate template A's prune: DELETE with template_name = 'A' (the new strict form).
        _command.CommandText = @"
            DELETE SchemaSmith.CompletedMigrationScripts
            WHERE [ProductName] = 'Demo' AND [QuenchSlot] = 'Before'
              AND [ScriptPath] = 'Before Scripts/Migration_001.sql'
              AND [template_name] = 'A'
              AND [schema_name] = '';";
        _command.ExecuteNonQuery();

        // Assert: the legacy blank-template row is preserved (template B still relies on it).
        _command.CommandText = @"
            SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts
            WHERE ScriptPath = 'Before Scripts/Migration_001.sql' AND template_name = '';";
        Assert.That((int)_command.ExecuteScalar(), Is.EqualTo(1),
            "Strict DELETE on template_name must NOT touch legacy blank-template rows owned by no specific template.");
    }

    [Test]
    public void KindleTheForge_RunTwice_RemainsIdempotent_NoDuplicateColumns()
    {
        _command.CommandText = @"
            INSERT INTO SchemaSmith.CompletedMigrationScripts (ScriptPath, ProductName, QuenchSlot)
            VALUES ('Before Scripts/Migration_001.sql', 'Demo', 'Before');";
        _command.ExecuteNonQuery();

        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        ForgeKindler.KindleTheForge(_command, Platform.SqlServer, forceReKindle: true);
        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(_command, Platform.SqlServer));

        _command.CommandText = @"
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID('SchemaSmith.CompletedMigrationScripts')
              AND name IN ('template_name', 'schema_name');";
        Assert.That((int)_command.ExecuteScalar(), Is.EqualTo(2));

        _command.CommandText = @"
            SELECT COUNT(*) FROM SchemaSmith.CompletedMigrationScripts
            WHERE ScriptPath = 'Before Scripts/Migration_001.sql';";
        Assert.That((int)_command.ExecuteScalar(), Is.EqualTo(1),
            "Legacy row must be preserved across re-kindling, not duplicated.");
    }
}
