// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// Under UnsupportedFeaturePolicy warn, a PostgreSQL deploy says on the console what it skipped, as the other engines do;
/// only the formatter was tested, not that a deploy prints its lines. A VIRTUAL generated column is degraded below
/// PostgreSQL 18, so this runs on the floor legs and is ignored on a server that holds one.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class DowngradeLogDeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.PostgreSQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("postgres");
    protected override string ConfigPrefix => "PostgreSQL";
    protected override string ProductName => "DowngradeLogProduct";

    private void CleanUp() =>
        Exec("DROP TABLE IF EXISTS public.downgrade_log_probe; DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = 'DowngradeLogProduct'");

    [Test]
    public void AVirtualGeneratedColumnBelow18_IsReportedAsSkipped()
    {
        if (Convert.ToInt32(Scalar("SELECT current_setting('server_version_num')::int")) >= 180000)
            Assert.Ignore("PostgreSQL 18 holds a VIRTUAL generated column, so nothing is degraded.");
        CleanUp();
        try
        {
            WritePackage("");
            WriteFile("Tables", "public.downgrade_log_probe.json", """
                { "Schema": "public", "Name": "downgrade_log_probe",
                  "Columns": [ { "Name": "a", "DataType": "integer", "Nullable": false },
                               { "Name": "b", "DataType": "integer", "Generated": "ALWAYS", "GenerationExpression": "a * 2", "Virtual": true } ] }
                """);

            var run = Deploy();

            Assert.That(run.Log, Has.Some.Contains("Skipping ").And.Contains("(downgraded)"), string.Join("\n", run.Log));
        }
        finally
        {
            CleanUp();
        }
    }
}
