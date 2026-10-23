// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Unit tests for the previewOnly fan-out path: when <c>previewOnly == true</c>,
/// <see cref="ProductQuench.EnumerateWorkUnitsForTemplate"/> must discover targets and
/// flag would-create units WITHOUT calling <see cref="ProductQuench.ProvisionDatabaseViaAdminConnection"/>.
/// </summary>
[TestFixture]
public class PreviewProvisioningTests
{
    [Test]
    public void PreviewOnly_MissingDbWithCreateIfMissing_DoesNotProvision_AndFlagsWouldCreate()
    {
        WithPreviewProbe("db_missing", createIfMissing: true, dbExists: false, (pq, template) =>
        {
            var units = pq.CallEnumerate(template, previewOnly: true);
            Assert.That(pq.ProvisionCalls, Is.Empty);                       // never provisioned
            Assert.That(units.Single().WouldCreateDatabase, Is.True);       // reported as would-create
        });
    }

    [Test]
    public void PreviewOnly_MissingDbWithCreateIfMissingFalse_SkipsUnit()
    {
        WithPreviewProbe("db_missing", createIfMissing: false, dbExists: false, (pq, template) =>
        {
            var units = pq.CallEnumerate(template, previewOnly: true);
            Assert.Multiple(() =>
            {
                Assert.That(pq.ProvisionCalls, Is.Empty);
                Assert.That(units, Is.Empty, "Missing DB + CreateIfMissing:false must still skip in preview mode.");
            });
        });
    }

    [Test]
    public void PreviewOnly_ExistingDb_DoesNotFlagWouldCreate()
    {
        WithPreviewProbe("db_exists", createIfMissing: true, dbExists: true, (pq, template) =>
        {
            var units = pq.CallEnumerate(template, previewOnly: true);
            Assert.Multiple(() =>
            {
                Assert.That(pq.ProvisionCalls, Is.Empty);
                Assert.That(units.Single().WouldCreateDatabase, Is.False, "Pre-existing DB must not be flagged would-create.");
            });
        });
    }

    [Test]
    public void NonPreview_MissingDbWithCreateIfMissing_DoesProvision_AndDoesNotFlagWouldCreate()
    {
        // Non-preview behavior must be byte-for-byte unchanged. A real run, not WhatIf: under WhatIf nothing is created,
        // which the next test covers.
        WithPreviewProbe("db_missing", createIfMissing: true, dbExists: false, whatIf: false, body: (pq, template) =>
        {
            var units = pq.CallEnumerate(template, previewOnly: false);
            Assert.Multiple(() =>
            {
                Assert.That(pq.ProvisionCalls, Is.EqualTo(new[] { "db_missing" }),
                    "Non-preview must provision the missing DB.");
                Assert.That(units.Single().WouldCreateDatabase, Is.False,
                    "Actually-provisioned DB must NOT carry WouldCreateDatabase=true.");
            });
        });
    }

    // #550: a WhatIf run only reports the database it would create, so its unit must not try to open it.
    [Test]
    public void WhatIf_MissingDbWithCreateIfMissing_FlagsWouldCreate_SoTheUnitDoesNotOpenIt()
    {
        WithPreviewProbe("db_missing", createIfMissing: true, dbExists: false, whatIf: true, body: (pq, template) =>
        {
            var units = pq.CallEnumerate(template, previewOnly: false);
            Assert.That(units.Single().WouldCreateDatabase, Is.True,
                "under WhatIf the database is only reported, so it does not exist for the unit to open");
        });
    }

    // #562: a database CreateIfMissing could not create had no work unit, so the summary's failures and targets, and the
    // console's failure manifest, said nothing about the PartialFailure.
    [Test]
    public void ADatabaseThatCouldNotBeCreated_IsAFailedTarget_AndAFailure()
    {
        WithPreviewProbe("db_denied", createIfMissing: true, dbExists: false, whatIf: false, provisionThrows: true, body: (pq, template) =>
        {
            var units = pq.CallEnumerate(template, previewOnly: false);
            Assert.Multiple(() =>
            {
                Assert.That(units, Is.Empty, "nothing is deployed to a database that does not exist");
                var target = pq.TargetResults.SingleOrDefault(t => t.Database == "db_denied");
                Assert.That(target?.Outcome, Is.EqualTo(SchemaQuench.Reporting.TargetOutcome.Failed), "targets[] lists it as failed");
                var failure = pq.FailureRecords.SingleOrDefault(f => f.ScopeKey.Contains("db_denied"));
                Assert.That(failure?.Error, Does.Contain("CREATE DATABASE permission denied"), "failures[] carries the provisioning message");
            });
        });
    }

    // The same for a database whose existence could not be checked: the check failing is not "it is missing".
    [Test]
    public void ADatabaseWhoseExistenceCheckFailed_IsAFailedTarget_AndAFailure()
    {
        WithPreviewProbe("db_unreadable", createIfMissing: true, dbExists: false, whatIf: false, existsThrows: true, body: (pq, template) =>
        {
            var units = pq.CallEnumerate(template, previewOnly: false);
            Assert.Multiple(() =>
            {
                Assert.That(units, Is.Empty, "nothing is deployed to a database that could not be checked");
                Assert.That(pq.ProvisionCalls, Is.Empty, "and nothing tries to create it");
                var target = pq.TargetResults.SingleOrDefault(t => t.Database == "db_unreadable");
                Assert.That(target?.Outcome, Is.EqualTo(SchemaQuench.Reporting.TargetOutcome.Failed), "targets[] lists it as failed");
                var failure = pq.FailureRecords.SingleOrDefault(f => f.ScopeKey.Contains("db_unreadable"));
                Assert.That(failure?.Error, Does.Contain("VIEW ANY DATABASE permission denied"), "failures[] carries the engine's message");
            });

            // The console's failure manifest renders the same records, so it names the database too.
            var failureLog = Substitute.For<log4net.ILog>();
            Schema.Utility.LogFactory.Register("FailureLog", failureLog);
            pq.EmitFailureRollup();
            failureLog.Received().Info(Arg.Is<object>(m => m.ToString().Contains("db_unreadable")));
        });
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal SQL Server ProductQuench harness (mirrors the pattern in
    /// <see cref="ProductQuenchTests"/>), constructs a <see cref="PreviewProbe"/> inside the
    /// <see cref="FactoryContainer"/> lock (so the base constructor resolves config / product
    /// correctly), and runs <paramref name="body"/> with the probe and the template. Tears down
    /// after the body returns.
    /// </summary>
    private static void WithPreviewProbe(
        string databaseName, bool createIfMissing, bool dbExists,
        System.Action<PreviewProbe, Template> body, bool whatIf = true, bool provisionThrows = false, bool existsThrows = false)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            Schema.Utility.LogFactory.Clear();

            const string schemaPackagePath = "Product";
            var productPath = Path.Combine(schemaPackagePath, "Product.json");
            var file = Substitute.For<IFile>();
            var directory = Substitute.For<IDirectory>();

            file.Exists(schemaPackagePath).Returns(false);
            directory.Exists(schemaPackagePath).Returns(true);
            file.Exists(productPath).Returns(true);
            file.ReadAllText(productPath).Returns("""
                {
                  "Name": "PreviewTestProduct",
                  "Platform": "SqlServer",
                  "ScriptFolders": []
                }
                """);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = schemaPackagePath,
                    ["Target:Server"] = "primary",
                    ["WhatIfONLY"] = whatIf ? "true" : "false",
                    ["Target:TemplateTargets:PreviewTemplate:Databases:0"] = databaseName,
                    ["Target:TemplateTargets:PreviewTemplate:CreateIfMissing"] = createIfMissing ? "true" : "false"
                })
                .Build();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IFile>(file);
            FactoryContainer.Register<IDirectory>(directory);
            FactoryContainer.Register<IEnvironment>(Substitute.For<IEnvironment>());

            Schema.Utility.LogFactory.Register("ProgressLog", Substitute.For<log4net.ILog>());
            Schema.Utility.LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

            try
            {
                var probe = new PreviewProbe(dbExists) { ProvisionThrows = provisionThrows, ExistsThrows = existsThrows };
                var template = new Template
                {
                    Name = "PreviewTemplate",
                    Product = probe.LoadedProduct,
                    DatabaseIdentificationScript = "SELECT name FROM sys.databases"
                };
                body(probe, template);
            }
            finally
            {
                FactoryContainer.Clear();
                Schema.Utility.LogFactory.Clear();
            }
        }
    }

    /// <summary>
    /// Minimal ProductQuench subclass for preview-provisioning tests. Stubs
    /// <see cref="ProductQuench.DatabaseExistsOnServer"/> and
    /// <see cref="ProductQuench.ProvisionDatabaseViaAdminConnection"/> to avoid live connections,
    /// and exposes <see cref="CallEnumerate"/> to reach the production fan-out under test.
    /// </summary>
    private sealed class PreviewProbe : ProductQuench
    {
        private readonly bool _dbExists;
        public List<string> ProvisionCalls { get; } = [];
        public bool ProvisionThrows { get; init; }
        public bool ExistsThrows { get; init; }

        public PreviewProbe(bool dbExists = false) { _dbExists = dbExists; }

        public List<WorkUnit> CallEnumerate(Template template, bool previewOnly)
            => EnumerateWorkUnitsForTemplate(template, previewOnly);

        internal override bool DatabaseExistsOnServer(string server, string databaseName) =>
            ExistsThrows ? throw new System.InvalidOperationException("VIEW ANY DATABASE permission denied in database 'master'.") : _dbExists;

        internal override void ProvisionDatabaseViaAdminConnection(string server, string databaseName)
        {
            ProvisionCalls.Add(databaseName);
            if (ProvisionThrows) throw new System.InvalidOperationException("CREATE DATABASE permission denied in database 'master'.");
        }
    }
}
