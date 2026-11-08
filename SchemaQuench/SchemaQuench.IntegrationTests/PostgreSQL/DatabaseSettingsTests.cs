// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class DatabaseSettingsTests : DatabaseSettingsSharedTests
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";

    protected override string Kind => "Parameter";
    protected override string Name => "work_mem";
    protected override string ValueA => "8MB";
    protected override string ValueB => "16MB";
    protected override string EquivalentSpellingOfA => "8192kB";
    protected override (string Name, string Value, string Action) Refused => ("shared_buffers", "256MB", "unfixable");

    // The database's own setting, as stored: ALTER DATABASE keeps the spelling it was given.
    protected override string ReadCurrent() => Scalar("""
        SELECT substr(x, 10) FROM pg_db_role_setting r CROSS JOIN LATERAL unnest(r.setconfig) AS x
         WHERE r.setrole = 0 AND r.setdatabase = (SELECT oid FROM pg_database WHERE datname = current_database()) AND x LIKE 'work_mem=%'
        """)?.ToString();

    protected override string SetSql(string value) => $"ALTER DATABASE \"{MainDb}\" SET work_mem = '{value}'";
    protected override void Restore() => Exec($"ALTER DATABASE \"{MainDb}\" RESET work_mem");

    // With no setting of its own the database runs at the server's value. The deploy's session reports its own: here the
    // lock_timeout the run sets from Target:LockTimeoutSeconds, so comparing that would call a database without the
    // declared value one that has it.
    [Test]
    public void TheRunsOwnSessionValue_DoesNotPassForTheDatabases()
    {
        Exec($"ALTER DATABASE \"{MainDb}\" RESET lock_timeout");
        try
        {
            Package(One("30s", fix: true, "Error", name: "lock_timeout"));

            var run = Deploy(settings: new System.Collections.Generic.Dictionary<string, string> { ["Target:LockTimeoutSeconds"] = "30" });

            Assert.Multiple(() =>
            {
                Assert.That(SettingRows(run), Is.EqualTo(new[] { "Parameter lock_timeout changed" }), Lines(run));
                Assert.That(Scalar("""
                    SELECT substr(x, 14) FROM pg_db_role_setting r CROSS JOIN LATERAL unnest(r.setconfig) AS x
                     WHERE r.setrole = 0 AND r.setdatabase = (SELECT oid FROM pg_database WHERE datname = current_database()) AND x LIKE 'lock_timeout=%'
                    """)?.ToString(), Is.EqualTo("30s"), "the database holds the value itself now");
            });
        }
        finally
        {
            Exec($"ALTER DATABASE \"{MainDb}\" RESET lock_timeout");
        }
    }
}
