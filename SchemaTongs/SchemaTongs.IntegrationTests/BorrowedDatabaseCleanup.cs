// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

// The root namespace of every test in this assembly, so this SetUpFixture wraps all of them.
namespace SchemaTongs.IntegrationTests;

/// <summary>
/// This suite runs on the Schema suite's per-engine FixtureSetup databases, which are created lazily on first use, and
/// only the Schema suite's own teardown drops them. A process-exit handler is registered too, but the test host can be
/// stopped before it finishes, so the databases are dropped here, once every test in this assembly has run.
/// </summary>
[SetUpFixture]
public class BorrowedDatabaseCleanup
{
    [OneTimeTearDown]
    public void DropBorrowedDatabases()
    {
        Schema.IntegrationTests.SqlServer.FixtureSetup.CleanupIfInitialized();
        Schema.IntegrationTests.PostgreSQL.FixtureSetup.CleanupIfInitialized();
        Schema.IntegrationTests.MySQL.FixtureSetup.CleanupIfInitialized();
        Schema.IntegrationTests.MariaDb.FixtureSetup.CleanupIfInitialized();
    }
}
