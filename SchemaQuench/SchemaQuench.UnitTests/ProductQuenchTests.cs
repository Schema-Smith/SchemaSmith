// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Isolators;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class ProductQuenchTests
{
    #region Platform Dispatch - Init Database

    [Test]
    public void GetInitDatabase_SqlServer_ReturnsMaster()
    {
        Assert.That(ProductQuench.GetInitDatabase(Platform.SqlServer), Is.EqualTo("master"));
    }

    [Test]
    public void GetInitDatabase_PostgreSQL_ReturnsPostgres()
    {
        Assert.That(ProductQuench.GetInitDatabase(Platform.PostgreSQL), Is.EqualTo("postgres"));
    }

    [Test]
    public void GetInitDatabase_MySQL_ReturnsInformationSchema()
    {
        Assert.That(ProductQuench.GetInitDatabase(Platform.MySQL), Is.EqualTo("information_schema"));
    }

    [Test]
    public void GetInitDatabase_InvalidPlatform_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductQuench.GetInitDatabase((Platform)999));
    }

    #endregion

    #region Platform Dispatch - Server ID Query

    [Test]
    public void GetServerIdQuery_SqlServer_ReturnsServerName()
    {
        Assert.That(ProductQuench.GetServerIdQuery(Platform.SqlServer), Is.EqualTo("SELECT @@SERVERNAME"));
    }

    [Test]
    public void GetServerIdQuery_PostgreSQL_ReturnsInetServerAddr()
    {
        Assert.That(ProductQuench.GetServerIdQuery(Platform.PostgreSQL), Is.EqualTo("SELECT inet_server_addr();"));
    }

    [Test]
    public void GetServerIdQuery_MySQL_ReturnsHostname()
    {
        Assert.That(ProductQuench.GetServerIdQuery(Platform.MySQL), Is.EqualTo("SELECT @@hostname"));
    }

    [Test]
    public void GetServerIdQuery_InvalidPlatform_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductQuench.GetServerIdQuery((Platform)999));
    }

    #endregion

    #region SpecialTokenTags Tests

    [Test]
    public void SpecialTokenTags_ContainsMaterializedViewSchema()
    {
        Assert.That(ProductQuench.SpecialTokenTags, Does.Contain("MaterializedViewSchema_"));
    }

    [Test]
    public void SpecialTokenTags_ContainsAllExpectedTags()
    {
        Assert.That(ProductQuench.SpecialTokenTags, Is.EquivalentTo(
            new[]
            {
                "TableSchema_", "ObjectScripts_", "QueryTokens_", "MaterializedViewSchema_", "IndexedViewSchema_",
                "TableXml_", "MaterializedViewXml_", "IndexedViewXml_"
            }));
    }

    [Test]
    public void SpecialTokenTags_ContainsIndexedViewSchema()
    {
        Assert.That(ProductQuench.SpecialTokenTags, Does.Contain("IndexedViewSchema_"));
    }

    #endregion

    #region ReferencesSpecialToken Tests

    [Test]
    public void ReferencesSpecialToken_CrossTemplateTableSchemaToken_IsDetected()
    {
        var script = new SqlScript();
        script.RemainingTokens.Add("TableSchema_App");

        Assert.That(ProductQuench.ReferencesSpecialToken(script), Is.True);
    }

    [Test]
    public void ReferencesSpecialToken_EachSpecialTag_IsDetected()
    {
        foreach (var tag in ProductQuench.SpecialTokenTags)
        {
            var script = new SqlScript();
            script.RemainingTokens.Add($"{tag}SomeTemplate");

            Assert.That(ProductQuench.ReferencesSpecialToken(script), Is.True,
                $"Token {tag}SomeTemplate should be detected as a special cross-template token");
        }
    }

    [Test]
    public void ReferencesSpecialToken_CaseInsensitiveTag_IsDetected()
    {
        var script = new SqlScript();
        script.RemainingTokens.Add("tableschema_App");

        Assert.That(ProductQuench.ReferencesSpecialToken(script), Is.True);
    }

    [Test]
    public void ReferencesSpecialToken_OrdinaryUnresolvedToken_IsNotDetected()
    {
        var script = new SqlScript();
        script.RemainingTokens.Add("SomeOtherToken");

        Assert.That(ProductQuench.ReferencesSpecialToken(script), Is.False);
    }

    [Test]
    public void ReferencesSpecialToken_NoRemainingTokens_IsNotDetected()
    {
        Assert.That(ProductQuench.ReferencesSpecialToken(new SqlScript()), Is.False);
    }

    #endregion

    #region Product Script Server Routing Tests

    [Test]
    public void QuenchProductScriptsWithCheckpoint_OpensCommandAgainstEachServerByName()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            var schemaPackagePath = "Product";
            var productPath = Path.Combine(schemaPackagePath, "Product.json");
            var file = Substitute.For<IFile>();
            var directory = Substitute.For<IDirectory>();

            file.Exists(schemaPackagePath).Returns(false);
            directory.Exists(schemaPackagePath).Returns(true);
            file.Exists(productPath).Returns(true);
            file.ReadAllText(productPath).Returns("""
                                                  {
                                                    "Name": "TestProduct",
                                                    "Platform": "SqlServer",
                                                    "ScriptFolders": []
                                                  }
                                                  """);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = schemaPackagePath,
                    ["Target:Server"] = "primary-server",
                    ["Target:SecondaryServers"] = "secondary-server",
                    ["MaxThreads"] = "1",
                    ["WhatIfONLY"] = "true"
                })
                .Build();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IFile>(file);
            FactoryContainer.Register<IDirectory>(directory);

            try
            {
                var quench = new RecordingProductQuench();
                var folders = new List<ProductFolder> { new() { ServerToQuench = ServerToQuench.Both } };

                quench.QuenchProductScriptsWithCheckpoint(folders, "Before Product", true);

                // The SQL Server fan-out opens one command per server, each against its OWN server
                // name — the secondary's command must target the secondary, not be re-routed to the
                // primary. MaxThreads=1 keeps the fan-out serial so the recording list stays single-threaded.
                Assert.That(quench.CommandServers, Is.EquivalentTo(new[] { "primary-server", "secondary-server" }));
            }
            finally
            {
                FactoryContainer.Clear();
            }
        }
    }

    private sealed class RecordingProductQuench : ProductQuench
    {
        public List<string> CommandServers { get; } = [];

        internal override IDbCommand GetCommand(string server)
        {
            CommandServers.Add(server);
            var command = Substitute.For<IDbCommand>();
            command.Connection.Returns(Substitute.For<IDbConnection>());
            // Answer the server-version-floor pre-flight (a real SQL Server reports its major version);
            // all other queries keep returning null so unrelated behavior is unchanged.
            command.ExecuteScalar().Returns(_ =>
                (command.CommandText ?? "").Contains("ProductVersion") ? (object)"16.0.1000.6" : null);
            return command;
        }
    }

    #endregion

    #region BuildSpecialTokens Tests

    [Test]
    public void BuildSpecialTokens_IncludesMaterializedViewSchemaToken()
    {
        var template = new Template { Name = "Core" };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens.ContainsKey("MaterializedViewSchema_Core"), Is.True);
    }

    [Test]
    public void BuildSpecialTokens_MaterializedViewSchema_DefaultsToEmptyArray()
    {
        var template = new Template { Name = "Core" };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["MaterializedViewSchema_Core"], Is.EqualTo("[]"));
    }

    [Test]
    public void BuildSpecialTokens_MaterializedViewSchema_StoresRawValue_NotPreEscaped()
    {
        // #301: special-token values are stored RAW. Quote-escaping happens at substitution time
        // (SqlScript.TokenReplace, context-aware), so pre-escaping here would double-escape a token
        // embedded in a string literal.
        var template = new Template { Name = "Core", MaterializedViewSchema = "[{\"Name\":\"test's view\"}]" };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["MaterializedViewSchema_Core"], Is.EqualTo("[{\"Name\":\"test's view\"}]"));
    }

    [Test]
    public void CrossTemplateToken_InStringLiteral_ProducesSingleLevelEscaping()
    {
        // #301: a cross-template token embedded in a SQL string literal must have its single quotes
        // escaped exactly once for that literal — not left raw (unclosed-quote error), not doubled.
        var template = new Template { Name = "App", TableSchema = "[{\"Default\":\"'X'\"}]" };
        var tokens = ProductQuench.BuildSpecialTokens(template);

        var script = new SqlScript { Name = "t", FilePath = "t.sql" };
        script.Batches.Add("EXEC Foo '{{TableSchema_App}}'");
        script.ReplaceQueryTokens(tokens.ToList());

        Assert.That(script.Batches[0], Is.EqualTo("EXEC Foo '[{\"Default\":\"''X''\"}]'"),
            $"Actual: {script.Batches[0]}");
    }

    [Test]
    public void BuildSpecialTokens_IncludesAllTokenTypes()
    {
        var template = new Template { Name = "MyTemplate" };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens.Keys, Does.Contain("TableSchema_MyTemplate"));
        Assert.That(tokens.Keys, Does.Contain("ObjectScripts_MyTemplate"));
        Assert.That(tokens.Keys, Does.Contain("QueryTokens_MyTemplate"));
        Assert.That(tokens.Keys, Does.Contain("MaterializedViewSchema_MyTemplate"));
        Assert.That(tokens.Keys, Does.Contain("IndexedViewSchema_MyTemplate"));
        // A2 cross-template XML twins
        Assert.That(tokens.Keys, Does.Contain("TableXml_MyTemplate"));
        Assert.That(tokens.Keys, Does.Contain("MaterializedViewXml_MyTemplate"));
        Assert.That(tokens.Keys, Does.Contain("IndexedViewXml_MyTemplate"));
        Assert.That(tokens, Has.Count.EqualTo(8));
    }

    [Test]
    public void BuildSpecialTokens_XmlTwins_AreWellFormedAndRawNotEscaped()
    {
        var template = new Template
        {
            Name = "App",
            TableSchema = "[{\"Name\":\"[Orders]\",\"Note\":\"a'b\"}]"
        };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        var xml = tokens["TableXml_App"];
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => System.Xml.Linq.XElement.Parse(xml));
            Assert.That(xml, Does.StartWith("<Tables"));
            Assert.That(xml, Does.Contain("[Orders]"));
            // Stored RAW like the JSON siblings — TokenReplace escapes context-aware (#301 guard).
            Assert.That(xml, Does.Contain("a'b"));
            Assert.That(xml, Does.Not.Contain("a''b"));
            // Empty view sets still produce well-formed roots.
            Assert.That(tokens["MaterializedViewXml_App"], Does.StartWith("<MaterializedViews"));
            Assert.That(tokens["IndexedViewXml_App"], Does.StartWith("<IndexedViews"));
        });
    }

    [Test]
    public void BuildSpecialTokens_IncludesIndexedViewSchemaToken()
    {
        var template = new Template { Name = "Core" };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens.ContainsKey("IndexedViewSchema_Core"), Is.True);
    }

    [Test]
    public void BuildSpecialTokens_IndexedViewSchema_DefaultsToEmptyArray()
    {
        var template = new Template { Name = "Core" };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["IndexedViewSchema_Core"], Is.EqualTo("[]"));
    }

    [Test]
    public void BuildSpecialTokens_IndexedViewSchema_StoresRawValue_NotPreEscaped()
    {
        // #301: stored raw; escaping happens at substitution (see MaterializedView test above).
        var template = new Template { Name = "Core", IndexedViewSchema = "[{\"Name\":\"test's view\"}]" };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["IndexedViewSchema_Core"], Is.EqualTo("[{\"Name\":\"test's view\"}]"));
    }

    // I8: cross-template snapshots of schema-template content (TableSchema_/
    // MaterializedViewSchema_/IndexedViewSchema_) must NOT contain the literal
    // {{SchemaName}} token — once embedded in a regular template's script, the
    // token would never be substituted at runtime and would corrupt DDL.
    [Test]
    public void BuildSpecialTokens_SchemaTemplate_TableSchema_ReplacesSchemaNameToken()
    {
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            TableSchema = "[{\"Schema\":\"{{SchemaName}}\",\"Name\":\"Customers\"}]"
        };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["TableSchema_TenantBody"], Does.Not.Contain("{{SchemaName}}"));
        Assert.That(tokens["TableSchema_TenantBody"], Does.Contain("<per-iteration>"));
    }

    [Test]
    public void BuildSpecialTokens_SchemaTemplate_MaterializedViewSchema_ReplacesSchemaNameToken()
    {
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            MaterializedViewSchema = "[{\"Schema\":\"{{SchemaName}}\",\"Name\":\"OrderSummary\"}]"
        };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["MaterializedViewSchema_TenantBody"], Does.Not.Contain("{{SchemaName}}"));
        Assert.That(tokens["MaterializedViewSchema_TenantBody"], Does.Contain("<per-iteration>"));
    }

    [Test]
    public void BuildSpecialTokens_SchemaTemplate_IndexedViewSchema_ReplacesSchemaNameToken()
    {
        var template = new Template
        {
            Name = "TenantBody",
            SchemaIdentificationScript = "SELECT 'tenant_a'",
            IndexedViewSchema = "[{\"Schema\":\"{{SchemaName}}\",\"Name\":\"vw_Orders\"}]"
        };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["IndexedViewSchema_TenantBody"], Does.Not.Contain("{{SchemaName}}"));
        Assert.That(tokens["IndexedViewSchema_TenantBody"], Does.Contain("<per-iteration>"));
    }

    [Test]
    public void BuildSpecialTokens_RegularTemplate_LeavesContentUnchanged()
    {
        // No SchemaIdentificationScript → not a schema template, so {{SchemaName}} is not
        // expected to appear; but defensively confirm the path doesn't munge regular content.
        var template = new Template
        {
            Name = "Core",
            TableSchema = "[{\"Schema\":\"dbo\",\"Name\":\"Customers\"}]"
        };

        var tokens = ProductQuench.BuildSpecialTokens(template);

        Assert.That(tokens["TableSchema_Core"], Is.EqualTo("[{\"Schema\":\"dbo\",\"Name\":\"Customers\"}]"));
    }

    #endregion

    #region ReadFilterArray (Slice 5)

    [Test]
    public void ReadFilterArray_NullSection_ReturnsEmpty()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();

        Assert.That(ProductQuench.ReadFilterArray(config, "Target:Templates"), Is.Empty);
    }

    [Test]
    public void ReadFilterArray_WhitespaceOnlyValues_AreFilteredOut()
    {
        // Stale config slots from prior tests come back as null or "" via the in-memory provider.
        // Whitespace-only values are equivalent — none should reach the filter.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Target:Templates:0"] = "",
                ["Target:Templates:1"] = "   ",
                ["Target:Templates:2"] = null
            })
            .Build();

        Assert.That(ProductQuench.ReadFilterArray(config, "Target:Templates"), Is.Empty);
    }

    [Test]
    public void ReadFilterArray_TrimsLeadingAndTrailingWhitespace()
    {
        // A user-supplied `" tenant_acme "` in their settings file must normalize to `"tenant_acme"`
        // so it matches the discovered universe (which never carries surrounding whitespace).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Target:Schemas:0"] = " tenant_acme",
                ["Target:Schemas:1"] = "tenant_globex ",
                ["Target:Schemas:2"] = "  tenant_beta  "
            })
            .Build();

        Assert.That(ProductQuench.ReadFilterArray(config, "Target:Schemas"),
            Is.EqualTo(new[] { "tenant_acme", "tenant_globex", "tenant_beta" }));
    }

    #endregion

    #region ReadTemplateTargets (Slice 2 — Issue #257)

    [Test]
    public void ReadTemplateTargets_ParsesAllAxesAndCreateIfMissing()
    {
        // Per-template override map mirrors the same .NET config array-keys shape as
        // Target.Templates / Target.Databases / Target.Schemas, scoped one level deeper under the
        // template name. CreateIfMissing is a bool with default false when absent. PerTenantDB
        // intentionally omits CreateIfMissing in this fixture to exercise the default-false path.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Target:TemplateTargets:TenantSchema:Schemas:0"] = "acme",
                ["Target:TemplateTargets:TenantSchema:Schemas:1"] = "globex",
                ["Target:TemplateTargets:TenantSchema:CreateIfMissing"] = "true",
                ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_a",
                ["Target:TemplateTargets:PerTenantDB:Databases:1"] = "tenant_b"
            }!)
            .Build();

        var targets = ProductQuench.ReadTemplateTargets(config);

        Assert.Multiple(() =>
        {
            Assert.That(targets.Keys, Is.EquivalentTo(new[] { "TenantSchema", "PerTenantDB" }));
            Assert.That(targets["TenantSchema"].Schemas, Is.EqualTo(new[] { "acme", "globex" }));
            Assert.That(targets["TenantSchema"].Databases, Is.Empty);
            Assert.That(targets["TenantSchema"].CreateIfMissing, Is.True);
            Assert.That(targets["PerTenantDB"].Databases, Is.EqualTo(new[] { "tenant_a", "tenant_b" }));
            Assert.That(targets["PerTenantDB"].Schemas, Is.Empty);
            Assert.That(targets["PerTenantDB"].CreateIfMissing, Is.False);
        });
    }

    [Test]
    public void ReadTemplateTargets_AbsentSection_ReturnsEmpty()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();

        Assert.That(ProductQuench.ReadTemplateTargets(config), Is.Empty);
    }

    [Test]
    public void ReadTemplateTargets_KeyLookupIsCaseInsensitive()
    {
        // Validators + override consumers all match template names with OrdinalIgnoreCase.
        // The dictionary returned by ReadTemplateTargets carries that comparer so a settings
        // file that mixes "TenantSchema" / "tenantschema" / "TENANTSCHEMA" still resolves to
        // the same per-template entry.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Target:TemplateTargets:TenantSchema:Schemas:0"] = "acme"
            }!)
            .Build();

        var targets = ProductQuench.ReadTemplateTargets(config);

        Assert.Multiple(() =>
        {
            Assert.That(targets.ContainsKey("TenantSchema"), Is.True);
            Assert.That(targets.ContainsKey("tenantschema"), Is.True);
            Assert.That(targets.ContainsKey("TENANTSCHEMA"), Is.True);
        });
    }

    [Test]
    public void ReadTemplateTargets_EmptyCreateIfMissingValue_SurvivesAsPhantomSurvivor_ForValidator()
    {
        // The phantom-entry skip in ReadTemplateTargets exists to defend against in-memory
        // provider residue (key path still present after a fixture nulls the values mid-run —
        // distinct from the user-authored config a real run would carry). The skip must only
        // fire when the CreateIfMissing KEY is genuinely absent — NOT when its value is empty.
        // A user writing `CreateIfMissing: ""` in JSON should reach the validator and trip rule
        // 3 ("entry must declare at least one of Databases or Schemas"), not be silently dropped
        // as test residue.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                // Entry has empty Databases / Schemas AND an explicit empty CreateIfMissing.
                // This shape MUST survive into the validator's input — the entry expresses
                // user intent (the empty CreateIfMissing key was deliberately set), even
                // though it's invalid per rule 3.
                ["Target:TemplateTargets:Tenant:CreateIfMissing"] = ""
            }!)
            .Build();

        var targets = ProductQuench.ReadTemplateTargets(config);

        Assert.Multiple(() =>
        {
            Assert.That(targets.Keys, Does.Contain("Tenant"),
                "User-authored entry with empty CreateIfMissing value must NOT be silently dropped " +
                "by the phantom-entry skip — the skip is for test-residue empty entries, not " +
                "user-authored invalid ones.");
            Assert.That(targets["Tenant"].HasNoTargets, Is.True);
            // CreateIfMissing parses to default false when the raw value is empty.
            Assert.That(targets["Tenant"].CreateIfMissing, Is.False);
        });
    }

    [Test]
    public void ReadTemplateTargets_WhitespaceOnlyEntries_AreFiltered()
    {
        // Stray null / whitespace slots (e.g., a test that nulled "Target:TemplateTargets:T:Schemas:1"
        // mid-run) must not survive into the override list — they would surface as misleading
        // unknown-name errors downstream when the validator compares the override against the
        // discovered universe.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Target:TemplateTargets:TenantSchema:Schemas:0"] = "  acme  ",
                ["Target:TemplateTargets:TenantSchema:Schemas:1"] = "",
                ["Target:TemplateTargets:TenantSchema:Schemas:2"] = "   ",
                ["Target:TemplateTargets:TenantSchema:Schemas:3"] = "globex"
            }!)
            .Build();

        var targets = ProductQuench.ReadTemplateTargets(config);

        Assert.That(targets["TenantSchema"].Schemas, Is.EqualTo(new[] { "acme", "globex" }));
    }

    #endregion

    #region Schema-Template Work-Unit Enumeration (Slice 3)

    [Test]
    public void EnumerateWorkUnits_RegularTemplate_OnePerDatabase()
    {
        // Regular templates produce one work unit per (server, db) pair with empty SchemaName.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppA", "AppB", "AppC" };

            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };
            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(3));
                Assert.That(units.Select(u => u.DatabaseName), Is.EquivalentTo(new[] { "AppA", "AppB", "AppC" }));
                Assert.That(units.All(u => u.SchemaName == ""), Is.True);
                Assert.That(units.All(u => u.Server == "primary"), Is.True);
                Assert.That(units.All(u => u.TemplateName == "Core"), Is.True);
            });
        });
    }

    [Test]
    public void EnumerateWorkUnits_SchemaTemplate_OnePerSchemaPerDb()
    {
        // Schema templates: each (server, db) hits SchemaDiscovery and produces one work unit per
        // discovered schema. The plan's worked example: 2 DBs × 4 schemas = 8 units.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "Tenants_East", "Tenants_West" };
            quench.SchemaDiscoveryResults[("primary", "Tenants_East")] =
                new List<string> { "tenant_acme", "tenant_globex", "tenant_initech", "tenant_umbrella" };
            quench.SchemaDiscoveryResults[("primary", "Tenants_West")] =
                new List<string> { "tenant_acme", "tenant_globex", "tenant_initech", "tenant_umbrella" };

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(8));
                Assert.That(units.Where(u => u.DatabaseName == "Tenants_East").Select(u => u.SchemaName),
                    Is.EquivalentTo(new[] { "tenant_acme", "tenant_globex", "tenant_initech", "tenant_umbrella" }));
                Assert.That(units.All(u => u.TemplateName == "TenantBody"), Is.True);
            });
        });
    }

    [Test]
    public void EnumerateWorkUnits_RegularThreeDatabases_ThreeWorkUnits()
    {
        // §3.4 plan case: Regular template + 3 DBs → 3 work units.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppA", "AppB", "AppC" };

            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.That(units, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void EnumerateWorkUnits_SchemaTemplate_DifferentSchemasPerDb()
    {
        // Schema sets need not be uniform across DBs — the discovery query may return per-DB sets.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppA", "AppB" };
            quench.SchemaDiscoveryResults[("primary", "AppA")] = new List<string> { "tenant_x", "tenant_y" };
            quench.SchemaDiscoveryResults[("primary", "AppB")] = new List<string> { "tenant_z" };

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(3));
                Assert.That(units.Single(u => u.DatabaseName == "AppB").SchemaName, Is.EqualTo("tenant_z"));
            });
        });
    }

    [Test]
    public void EnumerateWorkUnits_SchemaTemplate_NoSchemasReturned_NoUnitsForThatDb()
    {
        // A DB whose discovery script returns zero schemas contributes zero work units. Slice 3 with
        // a Required template + 0 schemas → empty work-unit list → required-empty error.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppEmpty" };
            quench.SchemaDiscoveryResults[("primary", "AppEmpty")] = new List<string>();

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.That(units, Is.Empty);
        });
    }

    [Test]
    public void QuenchTemplate_RequiredEmpty_RegularTemplate_SetsUpdateFailed()
    {
        // Required + zero work units → error logged + _updateFailed set. Regular-template flavor:
        // zero DBs returned by DatabaseIdentificationScript.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = System.Array.Empty<string>();

            var template = new Template
            {
                Name = "Core",
                RequireAtLeastOneTarget = true,
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            quench.InvokeQuenchTemplate(template);

            Assert.That(quench.UpdateFailed, Is.True);
            Assert.That(quench.LogBackupCalled, Is.True);
        });
    }

    [Test]
    public void QuenchTemplate_RequiredEmpty_SchemaTemplate_ZeroSchemas_SetsUpdateFailed()
    {
        // Required + schema template + DBs present but zero schemas discovered → still empty work
        // list → required-empty error fires. Mirrors the design's "empty across all servers" rule.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppA" };
            quench.SchemaDiscoveryResults[("primary", "AppA")] = new List<string>();

            var template = new Template
            {
                Name = "TenantBody",
                RequireAtLeastOneTarget = true,
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            quench.InvokeQuenchTemplate(template);

            Assert.That(quench.UpdateFailed, Is.True);
        });
    }

    [Test]
    public void QuenchTemplate_OptionalEmpty_DoesNotFail()
    {
        // Optional templates with zero work units skip silently — no error, no _updateFailed.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = System.Array.Empty<string>();

            var template = new Template
            {
                Name = "Optional",
                RequireAtLeastOneTarget = false,
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            quench.InvokeQuenchTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(quench.UpdateFailed, Is.False);
                Assert.That(quench.LogBackupCalled, Is.False);
            });
        });
    }

    [Test]
    public void LogSchemaTemplateFields_SchemaTemplate_EchoesFields()
    {
        // Per §3.6, templates with SchemaIdentificationScript echo their schema-fan-out config to
        // the progress log at template-start. Regular templates skip the echo. All four echoed
        // fields (SchemaIdentificationScript, CreateSchemaIfMissing, AllowParallel,
        // ContinueOnSchemaFailure) are now honored by the slice-4 engine; no stub annotation suffix.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT 1",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas",
                CreateSchemaIfMissing = true,
                AllowParallel = false,
                ContinueOnSchemaFailure = false
            };

            quench.InvokeLogSchemaTemplateFieldsIfSet(template);

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.Some.Contains("SchemaIdentificationScript:"));
                Assert.That(quench.ProgressLogLines, Has.Some.Contains("CreateSchemaIfMissing: True"));
                Assert.That(quench.ProgressLogLines, Has.None.Contains("not yet honored"));
                Assert.That(quench.ProgressLogLines, Has.Some.Contains("AllowParallel: False"));
                Assert.That(quench.ProgressLogLines, Has.Some.Contains("ContinueOnSchemaFailure: False"));
            });
        });
    }

    [Test]
    public void LogSchemaTemplateFields_RegularTemplate_AllDefaults_LogsNothing()
    {
        // Regular template + ContinueOnDatabaseFailure at default → no echo at all.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT 1",
                ContinueOnDatabaseFailure = true
            };

            quench.InvokeLogSchemaTemplateFieldsIfSet(template);

            Assert.That(quench.ProgressLogLines, Is.Empty);
        });
    }

    [Test]
    public void LogSchemaTemplateFields_ContinueOnDatabaseFailureFalse_EchoesIt()
    {
        // ContinueOnDatabaseFailure applies to ALL templates (regular + schema); echoed only when
        // non-default (false). Slice 4 now wires it to actual failure routing — no stub suffix.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT 1",
                ContinueOnDatabaseFailure = false
            };

            quench.InvokeLogSchemaTemplateFieldsIfSet(template);

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.Some.Contains("ContinueOnDatabaseFailure: False"));
                Assert.That(quench.ProgressLogLines, Has.None.Contains("not yet honored"));
            });
        });
    }

    [Test]
    public void LogSchemaTemplateFields_AllowParallel_NoStubAnnotation()
    {
        // AllowParallel IS consulted by slice-3 dispatch (single-thread vs MaxThreads-bounded pool).
        // It must NOT carry the "(not yet honored — slice 4)" suffix.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT 1",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas",
                AllowParallel = true
            };

            quench.InvokeLogSchemaTemplateFieldsIfSet(template);

            var allowParallelLines = quench.ProgressLogLines
                .Where(line => line.Contains("AllowParallel:"))
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(allowParallelLines, Is.Not.Empty);
                Assert.That(allowParallelLines, Has.None.Contains("not yet honored"));
            });
        });
    }

    [Test]
    public void LogSchemaTemplateFields_SchemaIdentificationScript_NoStubAnnotation()
    {
        // SchemaIdentificationScript IS load-bearing in slice 3 (schema discovery runs against it).
        // It must NOT carry the "(not yet honored — slice 4)" suffix.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT 1",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            quench.InvokeLogSchemaTemplateFieldsIfSet(template);

            var schemaIdLines = quench.ProgressLogLines
                .Where(line => line.Contains("SchemaIdentificationScript:"))
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(schemaIdLines, Is.Not.Empty);
                Assert.That(schemaIdLines, Has.None.Contains("not yet honored"));
            });
        });
    }

    [Test]
    public void DispatchWorkUnits_TemplateOrderPreserved_AllUnitsCompleteBeforeNextTemplate()
    {
        // Each template is dispatched via its own WorkUnitDispatcher.Run() — a synchronous, blocking
        // call. As long as QuenchTemplate iterates templates in TemplateOrder sequence (today's
        // behavior), template N+1's dispatch can't start until template N's dispatcher returns. This
        // test verifies that invariant at the dispatch layer: a single dispatcher's Run() completes
        // all its work before returning.
        var sequenceLog = new System.Collections.Concurrent.ConcurrentQueue<(string Template, string Phase)>();
        var allUnits = new List<WorkUnit>
        {
            new("s", "db1", "Shared", ""),
            new("s", "db2", "Shared", ""),
            new("s", "db3", "Shared", "")
        };

        var dispatcher = new WorkUnitDispatcher(allUnits, maxThreads: 3, new Dictionary<string, bool>(),
            unit =>
            {
                sequenceLog.Enqueue((unit.TemplateName, "start"));
                System.Threading.Thread.Sleep(20);
                sequenceLog.Enqueue((unit.TemplateName, "end"));
            });
        dispatcher.Run();

        // After Run() returns, every unit's end must have been observed. Subsequent template's
        // Run() can only start after this point — that's the TemplateOrder guarantee.
        var ends = sequenceLog.Count(e => e.Phase == "end");
        Assert.That(ends, Is.EqualTo(3), "All 3 units must have completed before Run() returns.");
    }

    [Test]
    public void EnumerateWorkUnits_ReservedSchemaName_ContinueMode_SkipsBadDbAndContinues()
    {
        // SchemaDiscovery throws on reserved names ('dbo', 'public', etc. — design §5.4).
        // With ContinueOnDatabaseFailure=true (default), a per-DB discovery failure trips
        // _updateFailed but does NOT abort enumeration — the loop continues to the next DB.
        WithMinimalSqlServerProductQuench(quench =>
        {
            // AppBad fails first; with continue=true, AppGood is still enumerated.
            quench.IdentifiedDatabases["primary"] = new[] { "AppBad", "AppGood" };
            quench.SchemaDiscoveryFailures[("primary", "AppBad")] =
                new System.InvalidOperationException("reserved schema name 'dbo'");
            quench.SchemaDiscoveryResults[("primary", "AppGood")] = new List<string> { "tenant_x" };

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas",
                ContinueOnDatabaseFailure = true  // default; explicit for clarity
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(1),
                    "AppBad skipped; AppGood's unit still enumerated in continue mode.");
                Assert.That(units.Single().DatabaseName, Is.EqualTo("AppGood"));
                Assert.That(quench.UpdateFailed, Is.True, "_updateFailed set even in continue mode.");
            });
        });
    }

    [Test]
    public void EnumerateWorkUnits_ReservedSchemaName_AbortMode_StopsEnumeration()
    {
        // With ContinueOnSchemaFailure=false on a schema template, a per-DB discovery failure
        // aborts enumeration for the rest of this server — AppGood is never touched. (The
        // per-template-scope contract: schema-template failures, including discovery, honor
        // ContinueOnSchemaFailure, not ContinueOnDatabaseFailure.)
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppBad", "AppGood" };
            quench.SchemaDiscoveryFailures[("primary", "AppBad")] =
                new System.InvalidOperationException("reserved schema name 'dbo'");
            quench.SchemaDiscoveryResults[("primary", "AppGood")] = new List<string> { "tenant_x" };

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas",
                ContinueOnSchemaFailure = false
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Is.Empty,
                    "AppBad's discovery failure aborts enumeration in abort mode; AppGood is never enumerated.");
                Assert.That(quench.UpdateFailed, Is.True);
            });
        });
    }

    [Test]
    public void EnumerateWorkUnits_SchemaTemplate_PrimaryDiscoveryFailureInAbortMode_StopsBeforeSecondaryServer()
    {
        // Regression guard for #247. With ServerToQuench=Both and ContinueOnSchemaFailure=false
        // on a schema template, a primary-server schema-discovery failure must abort enumeration
        // BEFORE secondary's databases are touched. Pre-fix the outer server loop checked
        // !template.ContinueOnDatabaseFailure (true by default), so primary's abort-mode failure
        // did NOT break the outer loop and secondary's work units snuck into the dispatched list.
        // The fix swaps the predicate to ShouldAbortOnFailure(template) so the outer loop honors
        // the type-aware abort scope (ContinueOnSchemaFailure for schema templates).
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppA" };
            quench.IdentifiedDatabases["secondary"] = new[] { "AppA" };
            quench.SchemaDiscoveryFailures[("primary", "AppA")] =
                new System.InvalidOperationException("reserved schema name 'dbo'");
            // Secondary's discovery would succeed if reached — proves the fix is enumeration-level,
            // not "secondary just happened to have nothing to enumerate."
            quench.SchemaDiscoveryResults[("secondary", "AppA")] = new List<string> { "tenant_x" };

            var template = new SqlServerTemplate
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas",
                ContinueOnSchemaFailure = false,
                ServerToQuench = ServerToQuench.Both
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Is.Empty,
                    "Primary's discovery failure must abort enumeration before secondary is touched. " +
                    "Pre-#247-fix this returned 1 unit (secondary.AppA.tenant_x) and let the dispatcher " +
                    "run schema-template work on secondary despite ContinueOnSchemaFailure=false.");
                Assert.That(quench.UpdateFailed, Is.True);
            });
        }, secondaryServers: "secondary");
    }

    [Test]
    public void EnumerateWorkUnits_ReservedSchemaName_LaterDb_PreservesEarlierUnits()
    {
        // With ContinueOnDatabaseFailure=true (default): AppGood's unit is produced; AppBad's
        // failure is skipped via continue; no more DBs. The earlier unit is preserved and
        // _updateFailed is set. With continue=true the unit IS dispatched (vs slice-3 where
        // _updateFailed caused the dispatcher to skip everything — that was an inconsistency
        // the slice-4 routing resolves).
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppGood", "AppBad" };
            quench.SchemaDiscoveryResults[("primary", "AppGood")] = new List<string> { "tenant_x" };
            quench.SchemaDiscoveryFailures[("primary", "AppBad")] =
                new System.InvalidOperationException("reserved schema name 'dbo'");

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas",
                ContinueOnDatabaseFailure = true  // default; AppBad's failure is skipped
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(1),
                    "AppGood's unit is preserved; AppBad's failure is skipped in continue mode.");
                Assert.That(units.Single().DatabaseName, Is.EqualTo("AppGood"));
                Assert.That(quench.UpdateFailed, Is.True, "_updateFailed set even in continue mode.");
            });
        });
    }

    /// <summary>
    /// Regression guard for the dispatch-guard bug: when ContinueOnDatabaseFailure=true and one DB's
    /// schema discovery fails, <c>_updateFailed</c> is set during enumeration but valid work units
    /// from the surviving DB are still added to the list. Before the fix the dispatch condition was
    /// <c>workUnits.Count > 0 &amp;&amp; !_updateFailed</c>, which evaluated false and silently
    /// dropped the valid units. The fix drops <c>&amp;&amp; !_updateFailed</c> so the dispatcher
    /// runs whenever there are units regardless of enumeration-time failures. This test MUST FAIL
    /// against the pre-fix code (no units dispatched) and pass after the fix (one unit dispatched).
    /// </summary>
    [Test]
    public void QuenchTemplate_ContinueOnDbFailure_ValidUnitsDispatchedDespiteEnumerationFailure()
    {
        // Two DBs: AppBad's schema discovery fails; AppGood produces one valid unit.
        // With ContinueOnDatabaseFailure=true, _updateFailed is set during enumeration but
        // AppGood's work unit is still in the list. The dispatcher must still run.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppBad", "AppGood" };
            quench.SchemaDiscoveryFailures[("primary", "AppBad")] =
                new System.InvalidOperationException("reserved schema name 'dbo'");
            quench.SchemaDiscoveryResults[("primary", "AppGood")] = new List<string> { "tenant_x" };

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas",
                ContinueOnDatabaseFailure = true,
                ContinueOnSchemaFailure = true
            };

            quench.InvokeQuenchTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(quench.DispatchedWorkUnits, Has.Count.EqualTo(1),
                    "AppGood's valid unit must reach the dispatcher even though _updateFailed was set " +
                    "during AppBad's enumeration failure. Pre-fix this was 0.");
                Assert.That(quench.DispatchedWorkUnits.Single().DatabaseName, Is.EqualTo("AppGood"));
                Assert.That(quench.UpdateFailed, Is.True,
                    "_updateFailed stays set so the non-zero exit path fires after dispatch.");
            });
        });
    }

    [Test]
    public void EnumerateWorkUnits_ServerConnectionFailure_AbortsAndFlagsUpdateFailed()
    {
        // Server-level enumeration failure (unreachable server, bad DatabaseIdentificationScript)
        // must be caught, logged, and surface as _updateFailed=true. With only one server in the
        // list and no work units produced, the result is empty regardless of ContinueOnDatabaseFailure.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.FailGetCommandFor.Add("primary");

            var template = new Template
            {
                Name = "TenantBody",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Is.Empty, "Server enumeration failed; no work units produced.");
                Assert.That(quench.UpdateFailed, Is.True);
                Assert.That(quench.ProgressLogLines,
                    Has.Some.Contains("Database enumeration FAILED"),
                    "Server-level failure must be logged so ops can see it without trawling the error log.");
            });
        });
    }

    #endregion

    #region TemplateTargets Enumeration Override (Slice 2 — Issue #257)

    [Test]
    public void EnumerateWorkUnitsForTemplate_OverrideReplacesSchemaDiscovery()
    {
        // Schemas override + no Databases override: database discovery still runs via the
        // DatabaseIdentificationScript, but the per-DB SchemaDiscovery call is bypassed.
        // Assertion: DiscoverSchemas was NOT called even once.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "ordering_b" };
            // Intentionally populate SchemaDiscoveryResults so a regression that accidentally
            // calls DiscoverSchemas would produce wrong-shape units rather than empties.
            quench.SchemaDiscoveryResults[("primary", "ordering_b")] = new List<string> { "should_be_ignored" };

            var template = new Template
            {
                Name = "TenantSchema",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units.Select(u => (u.DatabaseName, u.SchemaName)),
                    Is.EquivalentTo(new[] { ("ordering_b", "acme"), ("ordering_b", "globex") }));
                Assert.That(quench.DiscoverSchemasCallCount, Is.Zero,
                    "Override REPLACES SchemaDiscovery — the discovery surface must not be touched.");
                Assert.That(units.All(u => u.SchemaSource == "TemplateTargets:TenantSchema:Schemas"), Is.True);
                Assert.That(units.All(u => u.DatabaseSource == "DatabaseIdentificationScript"), Is.True);
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:TenantSchema:Schemas:0"] = "acme",
            ["Target:TemplateTargets:TenantSchema:Schemas:1"] = "globex"
        });
    }

    [Test]
    public void EnumerateWorkUnitsForTemplate_OverrideProducesCrossProductForBothAxes()
    {
        // Both axes overridden: cross-product (2 × 2 = 4 units). Neither DiscoverSchemas nor
        // any database-discovery GetCommand should fire — GetCommandCallCount stays zero.
        WithMinimalSqlServerProductQuench(quench =>
        {
            // Populate discovery sources so a regression that accidentally calls them would
            // produce different DB/schema names than the override.
            quench.IdentifiedDatabases["primary"] = new[] { "wrong_db" };
            quench.SchemaDiscoveryResults[("primary", "ring1")] = new List<string> { "wrong_schema" };
            // The DB-axis gate checks existence for override-sourced DBs. Mark the overridden
            // DBs as existing so this test exercises the pure enumeration-override path with no
            // CreateIfMissing branch involved.
            quench.ExistingDatabases.Add(("primary", "ring1"));
            quench.ExistingDatabases.Add(("primary", "ring2"));

            var template = new Template
            {
                Name = "TenantStack",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units.Select(u => (u.DatabaseName, u.SchemaName)), Is.EquivalentTo(new[]
                {
                    ("ring1", "alpha"), ("ring1", "beta"),
                    ("ring2", "alpha"), ("ring2", "beta")
                }));
                Assert.That(quench.GetCommandCallCount, Is.Zero,
                    "Both axes overridden — no server connection should be opened for discovery.");
                Assert.That(quench.DiscoverSchemasCallCount, Is.Zero,
                    "Schema override REPLACES SchemaDiscovery.");
                Assert.That(units.All(u => u.DatabaseSource == "TemplateTargets:TenantStack:Databases"), Is.True);
                Assert.That(units.All(u => u.SchemaSource == "TemplateTargets:TenantStack:Schemas"), Is.True);
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:TenantStack:Databases:0"] = "ring1",
            ["Target:TemplateTargets:TenantStack:Databases:1"] = "ring2",
            ["Target:TemplateTargets:TenantStack:Schemas:0"] = "alpha",
            ["Target:TemplateTargets:TenantStack:Schemas:1"] = "beta"
        });
    }

    [Test]
    public void EnumerateWorkUnitsForTemplate_NoOverride_UsesIdentificationScripts()
    {
        // No TemplateTargets entry: existing identification-script behavior is preserved bit-for-bit.
        // DatabaseSource / SchemaSource reflect that.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "ordering_b" };
            quench.SchemaDiscoveryResults[("primary", "ordering_b")] = new List<string> { "acme", "globex" };

            var template = new Template
            {
                Name = "TenantSchema",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(2));
                Assert.That(quench.DiscoverSchemasCallCount, Is.EqualTo(1),
                    "No override → SchemaDiscovery must run once per (server, db).");
                Assert.That(units.All(u => u.DatabaseSource == "DatabaseIdentificationScript"), Is.True);
                Assert.That(units.All(u => u.SchemaSource == "SchemaIdentificationScript"), Is.True);
            });
        });
    }

    [Test]
    public void EnumerateWorkUnitsForTemplate_NoOverride_RegularTemplate_SourceMatchesRegular()
    {
        // Regular (non-schema) template: SchemaSource carries the "(regular template)" sentinel
        // so a log reader can disambiguate "no schema fan-out" from "schema fan-out via script."
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppA", "AppB" };

            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(2));
                Assert.That(units.All(u => u.SchemaName == ""), Is.True);
                Assert.That(units.All(u => u.DatabaseSource == "DatabaseIdentificationScript"), Is.True);
                Assert.That(units.All(u => u.SchemaSource == "(regular template)"), Is.True);
            });
        });
    }

    [Test]
    public void LogWorkUnitSources_UniformTemplate_CollapsesToOneLineThatStillNamesTheSource()
    {
        // The rollup exists to keep a 1000-tenant run readable, but collapsing to one line is only
        // half the requirement: the line has to still answer "did an override participate?". A rollup
        // that emitted a bare count would pass a line-count assertion and be useless.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var workUnits = new List<WorkUnit>
            {
                new("primary", "tenant_a", "TenantSchema", "acme")
                {
                    DatabaseSource = "DatabaseIdentificationScript",
                    SchemaSource = "TemplateTargets:TenantSchema:Schemas"
                },
                new("primary", "tenant_b", "TenantSchema", "acme")
                {
                    DatabaseSource = "DatabaseIdentificationScript",
                    SchemaSource = "TemplateTargets:TenantSchema:Schemas"
                },
                new("primary", "tenant_c", "TenantSchema", "acme")
                {
                    DatabaseSource = "DatabaseIdentificationScript",
                    SchemaSource = "TemplateTargets:TenantSchema:Schemas"
                }
            };

            quench.ProgressLogLines.Clear();
            quench.LogWorkUnitSources(workUnits);

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.Count.EqualTo(1),
                    "three units sharing one source pair are one template's worth of disclosure, not three");
                Assert.That(quench.ProgressLogLines[0], Does.Contain("TenantSchema")
                    .And.Contain("3 units")
                    .And.Contain("DatabaseIdentificationScript")
                    .And.Contain("TemplateTargets:TenantSchema:Schemas"),
                    "the collapsed line must still name both sources -- a bare count answers nothing");
            });
        });
    }

    [Test]
    public void LogWorkUnitSources_OneDeviatingUnit_IsNamedIndividuallyNotAveragedAway()
    {
        // The anti-flattening guard, and the assertion most likely to catch a regression that
        // "passes" by summarising everything. One overridden tenant among many is the whole reason
        // an operator reads this disclosure at all.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var workUnits = new List<WorkUnit>
            {
                new("primary", "tenant_a", "TenantSchema", "acme")
                {
                    DatabaseSource = "DatabaseIdentificationScript",
                    SchemaSource = "SchemaIdentificationScript"
                },
                new("primary", "tenant_b", "TenantSchema", "acme")
                {
                    DatabaseSource = "DatabaseIdentificationScript",
                    SchemaSource = "SchemaIdentificationScript"
                },
                new("primary", "odd_one_out", "TenantSchema", "acme")
                {
                    DatabaseSource = "TemplateTargets:TenantSchema:Databases",
                    SchemaSource = "SchemaIdentificationScript"
                }
            };

            quench.ProgressLogLines.Clear();
            quench.LogWorkUnitSources(workUnits);

            Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                s.Contains("[primary].[odd_one_out]") &&
                s.Contains("TemplateTargets:TenantSchema:Databases")),
                "the single deviating unit must be named, not hidden behind the majority's count -- "
                + "otherwise the disclosure reports the opposite of what happened.\n"
                + string.Join("\n", quench.ProgressLogLines));
        });
    }

    [Test]
    public void LogWorkUnitSources_SeparateTemplates_EachGetTheirOwnRollup()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            var workUnits = new List<WorkUnit>
            {
                new("primary", "ordering_b", "TenantSchema", "acme")
                {
                    DatabaseSource = "DatabaseIdentificationScript",
                    SchemaSource = "TemplateTargets:TenantSchema:Schemas"
                },
                new("primary", "AppA", "Core", "")
                {
                    DatabaseSource = "TemplateTargets:Core:Databases",
                    SchemaSource = "(regular template)"
                }
            };

            quench.ProgressLogLines.Clear();
            quench.LogWorkUnitSources(workUnits);

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.Count.EqualTo(2),
                    "two templates, two rollups");
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                    s.Contains("'TenantSchema'") && s.Contains("TemplateTargets:TenantSchema:Schemas")));
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                    s.Contains("'Core'") && s.Contains("TemplateTargets:Core:Databases")));
            });
        });
    }

    [Test]
    public void QuenchProduct_ValidatorRejectsBadConfig_AbortsBeforeAnyEnumeration()
    {
        // Pre-flight validator failure (e.g., TemplateTargets entry naming an unknown template)
        // must abort the run before EnumerateWorkUnitsForTemplate is reached. The recording
        // quench's DiscoverSchemasCallCount staying at zero is the proof that no per-template
        // discovery happened.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "AppA" };

            var ex = Assert.Throws<TemplateTargetValidationException>(
                () => quench.QuenchProduct(suppressKindlingForTesting: true));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("FooBar"),
                    "The diagnostic must name the offending entry.");
                Assert.That(quench.DiscoverSchemasCallCount, Is.Zero,
                    "Validator must abort before any template enumeration runs.");
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:FooBar:Schemas:0"] = "x"
        });
    }

    [Test]
    public void EnumerateWorkUnitsForTemplate_DatabasesOverrideOnly_SchemaDiscoveryStillRuns()
    {
        // Each axis decides independently: Databases overridden, Schemas not. The override
        // supplies the databases; SchemaDiscovery still runs per overridden DB.
        WithMinimalSqlServerProductQuench(quench =>
        {
            // The discovery script source should not produce these names — that proves the
            // override took effect.
            quench.IdentifiedDatabases["primary"] = new[] { "wrong_db" };
            quench.SchemaDiscoveryResults[("primary", "tenant_a")] = new List<string> { "s1" };
            quench.SchemaDiscoveryResults[("primary", "tenant_b")] = new List<string> { "s2" };
            // DB-axis gate requires existence for override-sourced DBs. Mark both as existing
            // so this test exercises the pure enumeration-override path with no CreateIfMissing
            // branch involved.
            quench.ExistingDatabases.Add(("primary", "tenant_a"));
            quench.ExistingDatabases.Add(("primary", "tenant_b"));

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units.Select(u => (u.DatabaseName, u.SchemaName)),
                    Is.EquivalentTo(new[] { ("tenant_a", "s1"), ("tenant_b", "s2") }));
                Assert.That(units.All(u => u.DatabaseSource == "TemplateTargets:PerTenantDB:Databases"), Is.True);
                Assert.That(units.All(u => u.SchemaSource == "SchemaIdentificationScript"), Is.True);
                Assert.That(quench.DiscoverSchemasCallCount, Is.EqualTo(2),
                    "Schema-axis discovery runs once per overridden DB (no schema override).");
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_a",
            ["Target:TemplateTargets:PerTenantDB:Databases:1"] = "tenant_b"
        });
    }

    #endregion

    #region TemplateTargets Provisioning Flags (Slice 3 — Issue #257)

    [Test]
    public void EnumerateWorkUnitsForTemplate_SchemaOverride_CarriesSchemaFromOverrideTrue()
    {
        // SchemaFromOverride is set true on every unit whose schema came from a
        // TemplateTargets:<T>:Schemas override. Drives the override-vs-discovery branch in
        // DatabaseQuench.EnsureSchemaExists — discovery-sourced units keep today's strict
        // template-level CreateSchemaIfMissing behavior.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "ordering_b" };

            var template = new Template
            {
                Name = "TenantSchema",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.That(units.All(u => u.SchemaFromOverride), Is.True);
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:TenantSchema:Schemas:0"] = "acme"
        });
    }

    [Test]
    public void EnumerateWorkUnitsForTemplate_SchemaOverrideWithCreateIfMissing_CarriesProvisionFlag()
    {
        // CreateIfMissing: true on the override surfaces as ProvisionSchemaIfMissing = true on
        // every unit. DatabaseQuench delegates to SchemaProvisioner on units carrying this flag
        // when their schema is missing on target.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "ordering_b" };

            var template = new Template
            {
                Name = "TenantSchema",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.That(units.All(u => u.ProvisionSchemaIfMissing), Is.True);
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:TenantSchema:Schemas:0"] = "acme",
            ["Target:TemplateTargets:TenantSchema:CreateIfMissing"] = "true"
        });
    }

    [Test]
    public void EnumerateWorkUnitsForTemplate_SchemaOverrideWithoutCreateIfMissing_LeavesProvisionFlagFalse()
    {
        // CreateIfMissing absent / false (default) → ProvisionSchemaIfMissing stays false.
        // Override-sourced units with this combo SKIP missing schemas (no error, no provisioning).
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "ordering_b" };

            var template = new Template
            {
                Name = "TenantSchema",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.That(units.All(u => u.SchemaFromOverride && !u.ProvisionSchemaIfMissing), Is.True);
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:TenantSchema:Schemas:0"] = "acme"
        });
    }

    [Test]
    public void EnumerateWorkUnitsForTemplate_NoOverride_BothProvisioningFlagsFalse()
    {
        // Discovery-sourced units never carry the slice-3 override flags — today's behavior
        // is preserved bit-for-bit.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "ordering_b" };
            quench.SchemaDiscoveryResults[("primary", "ordering_b")] = new List<string> { "acme" };

            var template = new Template
            {
                Name = "TenantSchema",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                SchemaIdentificationScript = "SELECT schema_name FROM sys.schemas"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.That(units.All(u => !u.SchemaFromOverride && !u.ProvisionSchemaIfMissing), Is.True);
        });
    }

    #endregion

    #region TemplateTargets DB-Axis Provisioning (Slice 4 — Issue #257)

    [Test]
    public void EnumerateWorkUnits_DatabaseOverrideWithCreateIfMissing_ProvisionsMissingDbsBeforeDispatch()
    {
        // Two DBs in the override, only one pre-exists. CreateIfMissing: true → the missing one
        // is provisioned through the admin connection BEFORE any per-DB work units are enqueued.
        // Both DBs end up in the work-unit list afterwards.
        WithMinimalSqlServerProductQuench(quench =>
        {
            // Only tenant_a exists on the server today. tenant_b does NOT — and the override
            // says CreateIfMissing: true, so the gate must provision it.
            quench.ExistingDatabases.Add(("primary", "tenant_a"));

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units.Select(u => u.DatabaseName), Is.EquivalentTo(new[] { "tenant_a", "tenant_b" }));
                Assert.That(quench.ProvisionedDatabases, Is.EquivalentTo(new[] { ("primary", "tenant_b") }),
                    "CreateIfMissing: true must provision the missing DB before adding its work units.");
                Assert.That(units.All(u => u.DatabaseFromOverride), Is.True);
                Assert.That(units.All(u => u.ProvisionDatabaseIfMissing), Is.True);
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_a",
            ["Target:TemplateTargets:PerTenantDB:Databases:1"] = "tenant_b",
            ["Target:TemplateTargets:PerTenantDB:CreateIfMissing"] = "true"
        });
    }

    [Test]
    public void EnumerateWorkUnits_DatabaseOverrideWithoutCreateIfMissing_SkipsMissingDbsAndOmitsWorkUnits()
    {
        // CreateIfMissing: false (default) — missing DBs are SKIPPED with an info log and their
        // work units are NOT enqueued. Existing DBs deploy normally.
        WithMinimalSqlServerProductQuench(quench =>
        {
            // tenant_a exists; tenant_skipped does NOT. CreateIfMissing absent → defaults false.
            quench.ExistingDatabases.Add(("primary", "tenant_a"));

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units.Select(u => u.DatabaseName), Is.EquivalentTo(new[] { "tenant_a" }),
                    "Skip-missing must REMOVE work units for missing DBs from the enumeration.");
                Assert.That(quench.ProvisionedDatabases, Is.Empty,
                    "CreateIfMissing: false must NOT silently upgrade to a provision.");
                // Skip log line names the missing DB + the CreateIfMissing-is-false reason.
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                    s.Contains("Database 'tenant_skipped'") &&
                    s.Contains("CreateIfMissing is false") &&
                    s.Contains("skipping all iterations for this server-database pair")));
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_a",
            ["Target:TemplateTargets:PerTenantDB:Databases:1"] = "tenant_skipped"
        });
    }

    [Test]
    public void EnumerateWorkUnits_DatabaseOverride_DiscoveryDbs_BypassDbAxisGate()
    {
        // Discovery-sourced DBs (no Databases override) must NOT go through the DB-axis gate
        // — DatabaseExistsOnServer must not be called, and no skip log fires for the discovered
        // DBs (the engine assumes whatever the identification script returned IS reachable).
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = new[] { "ordering_b" };

            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(1));
                Assert.That(units[0].DatabaseFromOverride, Is.False,
                    "Discovery-sourced units must carry DatabaseFromOverride = false.");
                Assert.That(units[0].ProvisionDatabaseIfMissing, Is.False);
                Assert.That(quench.ProvisionedDatabases, Is.Empty,
                    "Discovery-sourced DBs must bypass the DB-axis gate entirely.");
            });
        });
    }

    [Test]
    public void EnumerateWorkUnits_DatabaseOverride_ProvisioningFailure_TripsUpdateFailed()
    {
        // Permission denial during CREATE DATABASE bubbles up as TemplateTargetProvisioningException;
        // the gate must log the actionable diagnostic + trip _updateFailed + omit the failing DB's
        // work units. Other DBs in the override still deploy.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.ExistingDatabases.Add(("primary", "tenant_a"));
            // tenant_b is missing AND the simulated admin user lacks CREATE DATABASE permission.
            quench.ProvisioningFailures.Add(("primary", "tenant_b"));

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                ContinueOnDatabaseFailure = true   // see one DB succeed even when its sibling errors
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units.Select(u => u.DatabaseName), Is.EquivalentTo(new[] { "tenant_a" }),
                    "The DB whose provisioning failed must not produce work units.");
                Assert.That(quench.UpdateFailed, Is.True,
                    "Provisioning failure must trip _updateFailed so the exit-code path engages.");
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                    s.Contains("Database provisioning FAILED") &&
                    s.Contains("tenant_b") &&
                    s.Contains("CREATE DATABASE permission")));
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_a",
            ["Target:TemplateTargets:PerTenantDB:Databases:1"] = "tenant_b",
            ["Target:TemplateTargets:PerTenantDB:CreateIfMissing"] = "true"
        });
    }

    [Test]
    public void EnumerateWorkUnits_DatabaseOverride_GenericProvisioningException_TripsUpdateFailed()
    {
        // The gate's provisioning catch must accept ANY exception, not just
        // TemplateTargetProvisioningException. Admin-connection failures during the actual
        // ProvisionDatabaseViaAdminConnection call (e.g., login denied, transient network blip)
        // surface as plain Exception types; the catch routes them through the same
        // continue-on-failure diagnostic as the wrapped provisioning failures.
        WithMinimalSqlServerProductQuench(quench =>
        {
            // Pre-existing existence check works (tenant_a) so we isolate the provisioning failure.
            quench.ExistingDatabases.Add(("primary", "tenant_a"));
            quench.GenericProvisioningFailures.Add(("primary", "tenant_b"));

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                ContinueOnDatabaseFailure = true
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units.Select(u => u.DatabaseName), Is.EquivalentTo(new[] { "tenant_a" }),
                    "The DB whose provisioning hit a generic exception must not produce work units.");
                Assert.That(quench.UpdateFailed, Is.True);
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                    s.Contains("Database provisioning FAILED") &&
                    s.Contains("tenant_b")));
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_a",
            ["Target:TemplateTargets:PerTenantDB:Databases:1"] = "tenant_b",
            ["Target:TemplateTargets:PerTenantDB:CreateIfMissing"] = "true"
        });
    }

    [Test]
    public void EnumerateWorkUnits_DatabaseOverride_ExistenceCheckFailure_TripsUpdateFailed()
    {
        // Admin-connection failure during the existence check (e.g., login denied to master) is
        // a server-level failure: surface a clear diagnostic, trip _updateFailed, omit the DB.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.ExistenceCheckFailures.Add(("primary", "tenant_b"));

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases",
                ContinueOnDatabaseFailure = true
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Is.Empty);
                Assert.That(quench.UpdateFailed, Is.True);
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                    s.Contains("Database existence check FAILED") &&
                    s.Contains("tenant_b")));
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_b"
        });
    }

    // ----- Fleet: IdentificationDatabase (enumeration DB override) -------------------------------

    [Test]
    public void ResolveIdentificationDatabase_Unset_ReturnsNull()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            Assert.That(quench.InvokeResolveIdentificationDatabase(new Template()), Is.Null);
            Assert.That(quench.InvokeResolveIdentificationDatabase(new Template { IdentificationDatabase = "   " }), Is.Null);
        });
    }

    [Test]
    public void ResolveIdentificationDatabase_Literal_ReturnedUnchanged()
    {
        WithMinimalSqlServerProductQuench(quench =>
            Assert.That(quench.InvokeResolveIdentificationDatabase(new Template { IdentificationDatabase = "RegistryDb" }),
                Is.EqualTo("RegistryDb")));
    }

    [Test]
    public void ResolveIdentificationDatabase_Token_ResolvedFromTemplateTokens()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            var template = new Template
            {
                IdentificationDatabase = "{{ControlDb}}",
                NonQueryTokens = new Dictionary<string, string> { { "ControlDb", "Registry_Prod" } }
            };
            Assert.That(quench.InvokeResolveIdentificationDatabase(template), Is.EqualTo("Registry_Prod"));
        });
    }

    [Test]
    public void ComposeIdentificationConnectionString_NoOverride_TargetsIdentificationDb()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            var built = quench.InvokeComposeIdentificationConnectionString("primary",
                new Template { IdentificationDatabase = "RegistryDb" });
            Assert.That(built, Does.Contain("primary"));
            Assert.That(built, Does.Contain("Initial Catalog=RegistryDb").Or.Contain("Database=RegistryDb"),
                $"Enumeration connection must target the IdentificationDatabase (not the init DB). Got: {built}");
        });
    }

    [Test]
    public void ComposeIdentificationConnectionString_OverrideOnPrimary_RetargetsToIdentificationDb()
    {
        // The --ConnectionString override on the primary is re-targeted to the IdentificationDatabase
        // for the enumeration command — NOT the override's own Database=, and NOT the init DB (the
        // admin/provisioning path keeps the init DB; that is asserted by the ComposeAdminDb tests).
        var env = Substitute.For<IEnvironment>();
        env.CommandLine.Returns(
            "schemaquench.exe --ConnectionString=\"Server=primary;Database=tenant_a;User Id=sa;Password=secret\"");
        WithMinimalSqlServerProductQuench(quench =>
        {
            var built = quench.InvokeComposeIdentificationConnectionString("primary",
                new Template { IdentificationDatabase = "RegistryDb" });
            Assert.That(built, Does.Contain("Initial Catalog=RegistryDb").Or.Contain("Database=RegistryDb"),
                $"Override must be re-targeted to the IdentificationDatabase. Got: {built}");
            Assert.That(built, Does.Not.Contain("=tenant_a"),
                "The override's original Database=tenant_a must be replaced.");
        }, environment: env);
    }

    [Test]
    public void ComposeAdminDbConnectionString_OverrideOnPrimary_RetargetsToInitDb()
    {
        // When --ConnectionString is supplied AND the server is the primary, GetCommandForAdminDb
        // must re-target the override to the platform's init DB rather than honoring the
        // override's Database= verbatim. PG in particular REQUIRES the admin DB (postgres) —
        // CREATE DATABASE cannot target a non-admin DB and cannot run inside a transaction.
        // SQL Server / MySQL tolerated the prior broken behavior, but the correct contract is
        // to retarget on every engine.
        //
        // Set the IEnvironment.CommandLine to inject --ConnectionString=...; CommandLineParser
        // reads from EnvironmentWrapper.GetFromFactory().CommandLine.
        var env = Substitute.For<IEnvironment>();
        env.CommandLine.Returns(
            "schemaquench.exe --ConnectionString=\"Server=primary;Database=tenant_a;User Id=sa;Password=secret\"");
        WithMinimalSqlServerProductQuench(quench =>
        {
            var built = quench.InvokeComposeAdminDbConnectionString("primary");

            Assert.That(built, Does.Contain("Initial Catalog=master")
                    .Or.Contain("Database=master"),
                $"Override must be re-targeted to 'master' (SQL Server init DB), not the override's " +
                $"original Database=tenant_a target. Got: {built}");
            Assert.That(built, Does.Not.Contain("=tenant_a"),
                "Original override's Database=tenant_a must be replaced.");
        }, environment: env);
    }

    [Test]
    public void ComposeAdminDbConnectionString_NoOverride_BuildsFromConfig()
    {
        // Belt-and-suspenders: when no --ConnectionString is in effect, the configured-target
        // path must build the connection string from Target:Server / User / Password / Port /
        // ConnectionProperties and point at the platform's init DB.
        WithMinimalSqlServerProductQuench(quench =>
        {
            var built = quench.InvokeComposeAdminDbConnectionString("primary");

            Assert.That(built, Does.Contain("primary").And.Contain("master"),
                $"Configured-target path must target the platform init DB (master). Got: {built}");
        });
    }

    [Test]
    public void ComposeAdminDbConnectionString_OverrideOnSecondary_DoesNotApplyOverride()
    {
        // The override branch is gated on (server == primary). A secondary server must NOT
        // inherit the override — secondaries get their connection from Target:* config.
        var env = Substitute.For<IEnvironment>();
        env.CommandLine.Returns(
            "schemaquench.exe --ConnectionString=\"Server=primary;Database=tenant_a;User Id=sa;Password=secret\"");
        WithMinimalSqlServerProductQuench(quench =>
        {
            var built = quench.InvokeComposeAdminDbConnectionString("secondary");

            // Secondary did not inherit the override's User Id=sa marker; it came from the configured
            // path which has no User/Password set in this minimal harness.
            Assert.That(built, Does.Not.Contain("User Id=sa"),
                $"Override must not bleed to secondary servers. Got: {built}");
        }, secondaryServers: "secondary", environment: env);
    }

    [Test]
    public void DatabaseAxisProvisioningGate_WhatIfMissingDb_LogsWouldCreateAndEnqueuesUnits()
    {
        // Provisioner-isolation WhatIf test exists in SchemaProvisionerTests; this is the gate /
        // ProductQuench boundary equivalent — when WhatIf is active and a Databases-override
        // target is missing AND CreateIfMissing: true, the gate must engage the WhatIf path (no
        // real DDL) AND still enqueue the work units so the dispatcher honors WhatIf for the rest
        // of the deployment too (consistent with WhatIf's "describe what would happen" contract).
        WithMinimalSqlServerProductQuench(quench =>
        {
            // tenant_phantom does NOT exist on target — populate nothing in ExistingDatabases
            // for ("primary", "tenant_phantom").

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                // WhatIf log line emitted in the per-engine-quoted form.
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s =>
                    s.Contains("[WhatIf] Would create database") &&
                    s.Contains("tenant_phantom")),
                    "Gate must engage SchemaProvisioner's WhatIf branch when WhatIfONLY is true.");
                // Work units still enqueued — WhatIf == "describe what would happen", including
                // the deployment that would land inside the just-provisioned DB.
                Assert.That(units, Has.Some.Matches<WorkUnit>(u =>
                    u.DatabaseName == "tenant_phantom" && u.ProvisionDatabaseIfMissing));
                // The "real" creation log line did NOT fire.
                Assert.That(quench.ProgressLogLines, Has.None.Matches<string>(s =>
                    s.Contains("  Creating database") && !s.Contains("[WhatIf]")));
            });
        }, extraConfig: new Dictionary<string, string>
        {
            // WhatIfONLY is already "true" in the base helper config — make it explicit here so
            // future readers see the gate intent.
            ["WhatIfONLY"] = "true",
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_phantom",
            ["Target:TemplateTargets:PerTenantDB:CreateIfMissing"] = "true"
        });
    }

    [Test]
    public void EnumerateWorkUnits_DatabaseOverride_PreExistingDb_DoesNotProvision()
    {
        // Idempotent path: when the DB already exists, the gate must return true without
        // provisioning — the existence check covers it. CreateIfMissing: true on a pre-existing
        // DB is a no-op.
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.ExistingDatabases.Add(("primary", "tenant_a"));

            var template = new Template
            {
                Name = "PerTenantDB",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };

            var units = quench.EnumerateWorkUnitsForTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(units, Has.Count.EqualTo(1));
                Assert.That(units[0].DatabaseName, Is.EqualTo("tenant_a"));
                Assert.That(quench.ProvisionedDatabases, Is.Empty,
                    "Pre-existing DB must NOT trigger provisioning even with CreateIfMissing: true.");
            });
        }, extraConfig: new Dictionary<string, string>
        {
            ["Target:TemplateTargets:PerTenantDB:Databases:0"] = "tenant_a",
            ["Target:TemplateTargets:PerTenantDB:CreateIfMissing"] = "true"
        });
    }

    #endregion

    #region Product folder ShouldApplyExpression gating (#260)

    [Test]
    public void GateProductFolders_KeepsTrueAndBlank_DropsFalse()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            var keep = new ProductFolder { FolderPath = "keep", ShouldApplyExpression = "KEEP" };
            var skip = new ProductFolder { FolderPath = "skip", ShouldApplyExpression = "SKIP" };
            var plain = new ProductFolder { FolderPath = "plain" };
            var command = Substitute.For<IDbCommand>();
            command.ExecuteScalar().Returns(_ => command.CommandText.Contains("KEEP") ? (object)1 : 0);

            var survivors = quench.GateProductFolders(command, new[] { keep, skip, plain });

            Assert.That(survivors.Select(f => f.FolderPath), Is.EqualTo(new[] { "keep", "plain" }));
        });
    }

    [Test]
    public void GateProductFolders_ExpressionThrows_PropagatesFailClosed()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            var bad = new ProductFolder { FolderPath = "bad", ShouldApplyExpression = "BROKEN" };
            var command = Substitute.For<IDbCommand>();
            command.ExecuteScalar().Returns(_ => throw new Exception("syntax error"));

            Assert.Throws<Exception>(() => quench.GateProductFolders(command, new[] { bad }));
        });
    }

    // ---- Product-scope gate token resolution (B6b: the third gate site, aligned with N2) ----

    [Test]
    public void GateProductFolders_NoGatedFolders_NeverQueriesTheServer()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            var plain = new ProductFolder { FolderPath = "plain" };
            var command = Substitute.For<IDbCommand>();

            var survivors = quench.GateProductFolders(command, new[] { plain });

            Assert.That(survivors.Select(f => f.FolderPath), Is.EqualTo(new[] { "plain" }));
            command.DidNotReceive().ExecuteScalar();
        });
    }

    [Test]
    public void GateProductFolders_ResolvesServerMajorVersionToken_BeforeEvaluating()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            // Product-folder gates run at product scope — before any database is selected — but the
            // server connection is already open, so {{ServerMajorVersion}} must resolve there (B6b).
            var versioned = new ProductFolder { FolderPath = "versioned", ShouldApplyExpression = "{{ServerMajorVersion}} >= 15" };
            var command = Substitute.For<IDbCommand>();
            string gateCommandText = null;
            command.ExecuteScalar().Returns(_ =>
            {
                if (command.CommandText.Contains("ProductVersion", StringComparison.OrdinalIgnoreCase))
                    return "15.0.2000.5";
                gateCommandText = command.CommandText;
                return 1;
            });

            var survivors = quench.GateProductFolders(command, new[] { versioned });

            Assert.That(survivors.Select(f => f.FolderPath), Is.EqualTo(new[] { "versioned" }),
                "The gate must evaluate the resolved comparison, not reach the server as literal token text.");
            Assert.That(gateCommandText, Does.Contain("15 >= 15"));
            Assert.That(gateCommandText, Does.Not.Contain("{{ServerMajorVersion}}"));
        });
    }

    [Test]
    public void GateProductFolders_LeavesUnresolvableCompatibilityLevelTokenLiteral_SoTheServerErrorsLoudly()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            // {{CompatibilityLevel}} is a database property; no database is selected at product scope,
            // so it must NOT be silently rewritten into a wrong-but-plausible comparison (e.g. "0 >= 130")
            // — that would skip the folder without telling anyone. It stays literal and reaches the
            // server as-is, which is today's loud-failure behaviour (decision recorded in the B6b report).
            var folder = new ProductFolder { FolderPath = "compat-gated", ShouldApplyExpression = "{{CompatibilityLevel}} >= 130" };
            var command = Substitute.For<IDbCommand>();
            string gateCommandText = null;
            command.ExecuteScalar().Returns(_ =>
            {
                gateCommandText = command.CommandText;
                throw new Exception("Incorrect syntax near '{'.");
            });

            Assert.Throws<Exception>(() => quench.GateProductFolders(command, new[] { folder }));

            Assert.That(gateCommandText, Does.Contain("{{CompatibilityLevel}}"),
                "An unresolvable token must reach the server as literal text, not be replaced with a wrong value.");
            Assert.That(quench.ProgressLogLines, Has.Some.Contains("compat-gated"));
            Assert.That(quench.ProgressLogLines, Has.Some.Contains("ShouldApplyExpression failed"));
        });
    }

    #endregion

    #region Log Hygiene — token scrubbing (#244)

    [Test]
    public void LogProductInfo_ScrubsSensitiveTokenValue()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.LoadedProduct.ScriptTokens["DbPassword"] = "supersecretvalue";

            quench.InvokeLogProductInfo();

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.None.Contains("supersecretvalue"));
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s => s.TrimStart() == "DbPassword: ***"));
            });
        });
    }

    [Test]
    public void LogProductInfo_LogsNonSensitiveTokenVerbatim()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.LoadedProduct.ScriptTokens["MainDB"] = "AppDatabase";

            quench.InvokeLogProductInfo();

            Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s => s.TrimStart() == "MainDB: AppDatabase"));
        });
    }

    [Test]
    public void LogProductInfo_LogTokensFalse_SuppressesEntireSection()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.LoadedProduct.ScriptTokens["DbPassword"] = "supersecretvalue";

            quench.InvokeLogProductInfo();

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.Some.Contains("suppressed via LogHygiene.LogTokens=false"));
                // No token names AND no token values leak when the whole section is suppressed.
                Assert.That(quench.ProgressLogLines, Has.None.Contains("supersecretvalue"));
                Assert.That(quench.ProgressLogLines, Has.None.Contains("DbPassword"));
            });
        }, extraConfig: new Dictionary<string, string> { ["LogHygiene:LogTokens"] = "false" });
    }

    [Test]
    public void LogProductInfo_ScrubTokens_OptInForUnmatchedName()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.LoadedProduct.ScriptTokens["Handshake"] = "opaquevalue";

            quench.InvokeLogProductInfo();

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.None.Contains("opaquevalue"));
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s => s.TrimStart() == "Handshake: ***"));
            });
        }, extraConfig: new Dictionary<string, string> { ["LogHygiene:ScrubTokens:0"] = "Handshake" });
    }

    [Test]
    public void LogProductInfo_AllowTokens_OptOutOfDefaultPattern()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.LoadedProduct.ScriptTokens["PublicToken"] = "shareable";

            quench.InvokeLogProductInfo();

            Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s => s.TrimStart() == "PublicToken: shareable"));
        }, extraConfig: new Dictionary<string, string> { ["LogHygiene:AllowTokens:0"] = "PublicToken" });
    }

    [Test]
    public void QuenchTemplate_ScrubsSensitiveTemplateTokenValue()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            // Optional template + zero discovered DBs → no failure; token logging happens up front.
            quench.IdentifiedDatabases["primary"] = System.Array.Empty<string>();
            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };
            template.LoggableTokens["ApiKey"] = "supersecretkey";

            quench.InvokeQuenchTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.None.Contains("supersecretkey"));
                Assert.That(quench.ProgressLogLines, Has.Some.Matches<string>(s => s.TrimStart() == "ApiKey: ***"));
            });
        });
    }

    [Test]
    public void QuenchTemplate_LogTokensFalse_SuppressesTemplateTokenSection()
    {
        WithMinimalSqlServerProductQuench(quench =>
        {
            quench.IdentifiedDatabases["primary"] = System.Array.Empty<string>();
            var template = new Template
            {
                Name = "Core",
                Product = quench.LoadedProduct,
                DatabaseIdentificationScript = "SELECT name FROM sys.databases"
            };
            template.LoggableTokens["ApiKey"] = "supersecretkey";

            quench.InvokeQuenchTemplate(template);

            Assert.Multiple(() =>
            {
                Assert.That(quench.ProgressLogLines, Has.Some.Contains("suppressed via LogHygiene.LogTokens=false"));
                Assert.That(quench.ProgressLogLines, Has.None.Contains("supersecretkey"));
                Assert.That(quench.ProgressLogLines, Has.None.Contains("ApiKey"));
            });
        }, extraConfig: new Dictionary<string, string> { ["LogHygiene:LogTokens"] = "false" });
    }

    #endregion

    #region Schema-Template Work-Unit Enumeration (Slice 3) — helpers shared with slice 2

    /// <summary>
    /// Hand-builds a minimal SQL Server ProductQuench (no real connections, no Product.json on disk)
    /// inside the FactoryContainer lock and runs <paramref name="body"/> against the recorded test
    /// double. Keeps every schema-template work-unit test self-contained: build → assert → tear down.
    /// </summary>
    private static void WithMinimalSqlServerProductQuench(
        System.Action<RecordingWorkUnitProductQuench> body,
        string secondaryServers = null,
        IReadOnlyDictionary<string, string> extraConfig = null,
        IEnvironment environment = null)
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
                                                    "Name": "TestProduct",
                                                    "Platform": "SqlServer",
                                                    "ScriptFolders": []
                                                  }
                                                  """);

            var configValues = new Dictionary<string, string>
            {
                ["SchemaPackagePath"] = schemaPackagePath,
                ["Target:Server"] = "primary",
                ["WhatIfONLY"] = "true"
            };
            if (!string.IsNullOrEmpty(secondaryServers))
                configValues["Target:SecondaryServers"] = secondaryServers;
            if (extraConfig != null)
                foreach (var (k, v) in extraConfig)
                    configValues[k] = v;
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IFile>(file);
            FactoryContainer.Register<IDirectory>(directory);

            // CRITICAL: prevent LogBackup.BackupLogsAndExit's Environment.Exit(n) from killing the
            // test host. The QuenchTemplate failure paths route through BackupLogsAndExit, which
            // calls Environment.Exit unless IEnvironment is mocked. Without this substitute the
            // first required-empty test brings down the whole nunit host. Tests can pass a
            // pre-configured environment (e.g., to inject --ConnectionString into CommandLine)
            // via the optional environment parameter.
            FactoryContainer.Register<IEnvironment>(environment ?? Substitute.For<IEnvironment>());

            // Substitute a capture-friendly logger BEFORE constructing ProductQuench so its
            // _progressLog field (captured in the constructor) points at the fake.
            var progressLog = Substitute.For<log4net.ILog>();
            var progressLogLines = new List<string>();
            progressLog.When(l => l.Info(Arg.Any<object>()))
                .Do(ci => progressLogLines.Add($"{ci.Arg<object>()}"));
            progressLog.When(l => l.Error(Arg.Any<object>()))
                .Do(ci => progressLogLines.Add($"ERROR: {ci.Arg<object>()}"));
            progressLog.When(l => l.Warn(Arg.Any<object>()))
                .Do(ci => progressLogLines.Add($"WARN: {ci.Arg<object>()}"));
            Schema.Utility.LogFactory.Register("ProgressLog", progressLog);
            Schema.Utility.LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

            try
            {
                var quench = new RecordingWorkUnitProductQuench(progressLogLines);
                body(quench);
            }
            finally
            {
                FactoryContainer.Clear();
                Schema.Utility.LogFactory.Clear();
            }
        }
    }

    /// <summary>
    /// ProductQuench test double for slice-3 work-unit enumeration. Stubs out the live-connection
    /// surface (<see cref="ProductQuench.GetCommand"/>, <see cref="ProductQuench.DiscoverSchemas"/>)
    /// with in-memory result tables, captures the progress-log output for inspection, and exposes
    /// the private <c>_updateFailed</c> flag via reflection so tests can assert on it.
    /// </summary>
    private sealed class RecordingWorkUnitProductQuench : ProductQuench
    {
        public Dictionary<string, string[]> IdentifiedDatabases { get; } = new();
        public Dictionary<(string Server, string Db), List<string>> SchemaDiscoveryResults { get; } = new();
        public Dictionary<(string Server, string Db), System.Exception> SchemaDiscoveryFailures { get; } = new();
        public HashSet<string> FailGetCommandFor { get; } = new();
        public List<string> ProgressLogLines { get; }
        public bool LogBackupCalled { get; private set; }

        // DB-axis seams. ExistingDatabases is the in-memory truth set for the
        // DatabaseExistsOnServer check; ProvisionedDatabases records what production code asked
        // to provision so tests assert on the call without standing up an admin connection.
        public HashSet<(string Server, string Db)> ExistingDatabases { get; } = new();
        public List<(string Server, string Db)> ProvisionedDatabases { get; } = new();
        public HashSet<(string Server, string Db)> ProvisioningFailures { get; } = new();
        // Simulates a non-wrapped exception bubbling up from ProvisionDatabaseViaAdminConnection
        // (e.g., admin-connection login denial) — distinct from ProvisioningFailures, which throws
        // the wrapped TemplateTargetProvisioningException path that the original narrow catch
        // already handled.
        public HashSet<(string Server, string Db)> GenericProvisioningFailures { get; } = new();
        public HashSet<(string Server, string Db)> ExistenceCheckFailures { get; } = new();

        // Call tracking for the override-vs-discovery tests. Production code that bypasses
        // discovery via TemplateTargets must NOT touch these surfaces — assertions on the recorded
        // counts catch a regression that accidentally re-invokes discovery after applying the override.
        public int GetCommandCallCount { get; private set; }
        public int DiscoverSchemasCallCount { get; private set; }

        public RecordingWorkUnitProductQuench(List<string> progressLogLines)
        {
            ProgressLogLines = progressLogLines;
        }

        internal override IDbCommand GetCommand(string server)
        {
            GetCommandCallCount++;
            if (FailGetCommandFor.Contains(server))
                throw new System.Exception($"Unable to connect to {server} (simulated)");
            var dbs = IdentifiedDatabases.TryGetValue(server, out var list) ? list : System.Array.Empty<string>();
            return MakeReaderCommand(dbs);
        }

        internal override List<string> DiscoverSchemas(string server, string databaseName, Template template)
        {
            DiscoverSchemasCallCount++;
            if (SchemaDiscoveryFailures.TryGetValue((server, databaseName), out var ex))
                throw ex;
            return SchemaDiscoveryResults.TryGetValue((server, databaseName), out var list)
                ? new List<string>(list)
                : new List<string>();
        }

        // DB-axis existence + provisioning seams. Tests populate ExistingDatabases to control
        // whether DatabaseAxisProvisioningGate sees the DB as present; provisioning failures are
        // simulated by adding to ProvisioningFailures.
        internal override bool DatabaseExistsOnServer(string server, string databaseName)
        {
            if (ExistenceCheckFailures.Contains((server, databaseName)))
                throw new System.Exception($"Existence check failed for [{server}].[{databaseName}] (simulated)");
            return ExistingDatabases.Contains((server, databaseName));
        }

        internal override void ProvisionDatabaseViaAdminConnection(string server, string databaseName)
        {
            if (ProvisioningFailures.Contains((server, databaseName)))
                throw new TemplateTargetProvisioningException(
                    $"Failed to provision database '{databaseName}'. CREATE DATABASE permission denied (simulated)",
                    new System.Exception("simulated"));
            if (GenericProvisioningFailures.Contains((server, databaseName)))
                throw new System.Exception(
                    $"Unable to connect to {server} with user simulated (simulated admin-connection failure)");
            // Mirror the SchemaProvisioner's per-engine WhatIf vs real-create log shape so tests
            // can assert on the gate's WhatIf engagement at the production-equivalent boundary.
            // SQL Server quoting is the only case the recorder runs through; other engines flow
            // through the same gate but their tests live in the integration suite.
            if (IsWhatIfOnly)
                Schema.Utility.LogFactory.GetLogger("ProgressLog").Info(
                    $"  [WhatIf] Would create database [{databaseName}] (CreateIfMissing: true)");
            else
                Schema.Utility.LogFactory.GetLogger("ProgressLog").Info(
                    $"  Creating database [{databaseName}] (CreateIfMissing: true)");
            ProvisionedDatabases.Add((server, databaseName));
            // Make the gate see the DB as existing immediately after provisioning so the caller
            // proceeds with schema discovery / work-unit enqueueing. Under WhatIf in production,
            // the DB stays missing on the server; but the gate uses the post-provision return as
            // a signal to enqueue work units (the dispatcher won't execute real DDL under WhatIf
            // either), so the same "DB-exists-from-now-on" recording is correct for both paths.
            ExistingDatabases.Add((server, databaseName));
        }

        // Capture, don't dispatch. Slice-3 enumeration tests only need to assert on the work
        // unit list shape — running the dispatcher would require a live DatabaseQuench.Execute()
        // and a real connection, neither of which is in scope at this layer.
        public List<WorkUnit> DispatchedWorkUnits { get; } = new();

        internal override void DispatchWorkUnits(Template template, List<WorkUnit> workUnits, bool suppressKindling)
        {
            DispatchedWorkUnits.AddRange(workUnits);
        }

        public bool UpdateFailed
        {
            get
            {
                var fi = typeof(ProductQuench).GetField("_updateFailed",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return (bool)fi!.GetValue(this);
            }
        }

        public void InvokeQuenchTemplate(Template template)
        {
            try
            {
                var method = typeof(ProductQuench).GetMethod("QuenchTemplate",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                method!.Invoke(this, new object[] { template, true });
            }
            catch (System.Reflection.TargetInvocationException tie) when (
                tie.InnerException is System.Exception { Message: var m } && m.Contains("BackupLogsAndExit"))
            {
                // LogBackup.BackupLogsAndExit calls Environment.Exit in production; in tests the
                // file-backup mock no-ops but might still throw. Either way the test observes the
                // failure through UpdateFailed.
                LogBackupCalled = true;
            }

            // The check at the bottom of QuenchTemplate calls LogBackup.BackupLogsAndExit when
            // _updateFailed is true. Under tests it's a no-op; surface the equivalent observation
            // by reading _updateFailed directly.
            LogBackupCalled = LogBackupCalled || UpdateFailed;
        }

        public void InvokeLogSchemaTemplateFieldsIfSet(Template template)
        {
            ProgressLogLines.Clear();
            var method = typeof(ProductQuench).GetMethod("LogSchemaTemplateFieldsIfSet",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            method!.Invoke(this, new object[] { template });
        }

        public void InvokeLogProductInfo()
        {
            ProgressLogLines.Clear();
            var method = typeof(ProductQuench).GetMethod("LogProductInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            method!.Invoke(this, null);
        }

        // Expose the admin-DB connection-string composition for direct assertion.
        // ComposeAdminDbConnectionString is internal virtual on the base — production code
        // calls it from GetCommandForAdminDb; tests reach it through this thin invoker.
        public string InvokeComposeAdminDbConnectionString(string server)
        {
            var method = typeof(ProductQuench).GetMethod("ComposeAdminDbConnectionString",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (string)method!.Invoke(this, new object[] { server })!;
        }

        // Fleet IdentificationDatabase seams (internal on the base, visible to this assembly).
        public string InvokeResolveIdentificationDatabase(Template template) => ResolveIdentificationDatabase(template);

        public string InvokeComposeIdentificationConnectionString(string server, Template template) =>
            ComposeIdentificationConnectionString(server, template);

        private static IDbCommand MakeReaderCommand(string[] rows)
        {
            var command = Substitute.For<IDbCommand>();
            command.Connection.Returns(Substitute.For<IDbConnection>());
            command.ExecuteReader().Returns(_ => new InMemoryRowReader(rows));
            // Answer the server-version-floor pre-flight (a real SQL Server reports its major version);
            // all other scalar queries keep returning null so unrelated behavior is unchanged.
            command.ExecuteScalar().Returns(_ =>
                (command.CommandText ?? "").Contains("ProductVersion") ? (object)"16.0.1000.6" : null);
            return command;
        }
    }

    /// <summary>
    /// Single-column <see cref="IDataReader"/> stub that yields the supplied string values in order.
    /// Mirrors the shape of what <c>SELECT name FROM sys.databases</c> would produce — one column,
    /// N rows.
    /// </summary>
    private sealed class InMemoryRowReader : IDataReader
    {
        private readonly string[] _rows;
        private int _idx = -1;

        public InMemoryRowReader(string[] rows) { _rows = rows ?? System.Array.Empty<string>(); }

        public bool Read() { _idx++; return _idx < _rows.Length; }

        public object this[int i] => _rows[_idx];
        public object this[string name] => _rows[_idx];

        public void Dispose() { }
        public void Close() { }
        public bool NextResult() => false;
        public int Depth => 0;
        public bool IsClosed => false;
        public int RecordsAffected => -1;
        public int FieldCount => 1;

        public string GetName(int i) => "name";
        public string GetDataTypeName(int i) => "VARCHAR";
        public System.Type GetFieldType(int i) => typeof(string);
        public object GetValue(int i) => _rows[_idx];
        public int GetValues(object[] values) { values[0] = _rows[_idx]; return 1; }
        public int GetOrdinal(string name) => 0;
        public bool GetBoolean(int i) => throw new System.NotSupportedException();
        public byte GetByte(int i) => throw new System.NotSupportedException();
        public long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length) => 0;
        public char GetChar(int i) => throw new System.NotSupportedException();
        public long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length) => 0;
        public System.Guid GetGuid(int i) => throw new System.NotSupportedException();
        public short GetInt16(int i) => throw new System.NotSupportedException();
        public int GetInt32(int i) => throw new System.NotSupportedException();
        public long GetInt64(int i) => throw new System.NotSupportedException();
        public float GetFloat(int i) => throw new System.NotSupportedException();
        public double GetDouble(int i) => throw new System.NotSupportedException();
        public string GetString(int i) => _rows[_idx];
        public decimal GetDecimal(int i) => throw new System.NotSupportedException();
        public System.DateTime GetDateTime(int i) => throw new System.NotSupportedException();
        public IDataReader GetData(int i) => throw new System.NotSupportedException();
        public bool IsDBNull(int i) => _rows[_idx] == null;
        public System.Data.DataTable GetSchemaTable() => null;
    }

    #endregion

    #region DropTablesRemovedFromProduct cascade (env + product + template, defaultValue: true)

    [Test]
    public void DropTablesRemoved_AllAbsent_DefaultTrue()
    {
        // Env unset, product null, template null -> default true.
        Assert.That(ProductQuench.ResolveCascadedFlag(null, null, null, defaultValue: true), Is.True);
    }

    [Test]
    public void DropTablesRemoved_ProductFalse_EnvAbsent_False()
    {
        // The SchemaShears patch case: product vetoes the default-true ambient.
        Assert.That(ProductQuench.ResolveCascadedFlag(null, false, null, defaultValue: true), Is.False);
    }

    [Test]
    public void DropTablesRemoved_EnvFalse_ProductTrue_False()
    {
        // Env false wins regardless of product (false dominates).
        Assert.That(ProductQuench.ResolveCascadedFlag(false, true, null, defaultValue: true), Is.False);
    }

    [Test]
    public void DropTablesRemoved_BothFalse_False()
    {
        Assert.That(ProductQuench.ResolveCascadedFlag(false, false, null, defaultValue: true), Is.False);
    }

    [Test]
    public void DropTablesRemoved_TemplateFalse_OverridesProductTrue_False()
    {
        // Template-level false suppresses even when env+product say true.
        Assert.That(ProductQuench.ResolveCascadedFlag(null, true, false, defaultValue: true), Is.False);
    }

    #endregion

    #region DropColumnsRemovedFromProduct cascade (env + product + template, defaultValue: true)

    [Test]
    public void DropColumnsRemoved_AllAbsent_DefaultTrue()
    {
        // Env unset, product null, template null -> default true (drop).
        Assert.That(ProductQuench.ResolveCascadedFlag(null, null, null, defaultValue: true), Is.True);
    }

    [Test]
    public void DropColumnsRemoved_TemplateFalse_ResolvesToFalse()
    {
        // Template flag false -> resolved false.
        Assert.That(ProductQuench.ResolveCascadedFlag(null, null, false, defaultValue: true), Is.False);
    }

    [Test]
    public void DropColumnsRemoved_EnvFalse_ResolvesToFalse()
    {
        // Env false + product/template null -> false.
        Assert.That(ProductQuench.ResolveCascadedFlag(false, null, null, defaultValue: true), Is.False);
    }

    #endregion

    #region DropForeignKeysRemovedFromProduct cascade (env + product + template, defaultValue: true)

    [Test]
    public void DropForeignKeysRemoved_TemplateFalse_ResolvesToFalse()
    {
        // Template false suppresses the default-true ambient — the SchemaShears patch case.
        Assert.That(ProductQuench.ResolveCascadedFlag(null, null, false, defaultValue: true), Is.False);
    }

    [Test]
    public void DropForeignKeysRemoved_EnvFalse_ResolvesToFalse()
    {
        // Env false with product/template absent -> false (env dominates).
        Assert.That(ProductQuench.ResolveCascadedFlag(false, null, null, defaultValue: true), Is.False);
    }

    [Test]
    public void DropForeignKeysRemoved_AllAbsent_DefaultTrue()
    {
        // No env, no product flag, no template flag -> default true (drop by default).
        Assert.That(ProductQuench.ResolveCascadedFlag(null, null, null, defaultValue: true), Is.True);
    }

    #endregion

    #region ResolveCascadedFlag + ConfigBool

    [Test] public void Cascade_AllAbsent_ReturnsDefault_True()  => Assert.That(ProductQuench.ResolveCascadedFlag(null,null,null,true), Is.True);
    [Test] public void Cascade_AllAbsent_ReturnsDefault_False() => Assert.That(ProductQuench.ResolveCascadedFlag(null,null,null,false), Is.False);
    [Test] public void Cascade_EnvFalse_ProductTrue_StaysFalse() => Assert.That(ProductQuench.ResolveCascadedFlag(false,true,null,true), Is.False);
    [Test] public void Cascade_EnvTrue_ProductFalse_False()      => Assert.That(ProductQuench.ResolveCascadedFlag(true,false,null,false), Is.False);
    [Test] public void Cascade_DefaultFalse_EnvTrue_Enables()    => Assert.That(ProductQuench.ResolveCascadedFlag(true,null,null,false), Is.True);
    [Test] public void Cascade_TemplateFalse_Tightens()          => Assert.That(ProductQuench.ResolveCascadedFlag(null,null,false,true), Is.False);
    [Test] public void Cascade_TemplateTrue_CannotBeatAncestorFalse() => Assert.That(ProductQuench.ResolveCascadedFlag(false,null,true,false), Is.False);
    [Test] public void ConfigBool_Absent_Null()  { var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>()).Build(); Assert.That(ProductQuench.ConfigBool(c,"X"), Is.Null); }
    [Test] public void ConfigBool_False_False()   { var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>{["X"]="false"}).Build(); Assert.That(ProductQuench.ConfigBool(c,"X"), Is.False); }
    [Test] public void ConfigBool_True_True()     { var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>{["X"]="true"}).Build(); Assert.That(ProductQuench.ConfigBool(c,"X"), Is.True); }

    // Env-level no-drop protection tier (env PreventDrop) config resolution (#270 Slice E).
    private static IConfigurationRoot Cfg(params (string Key, string Value)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(p => p.Key, p => p.Value)).Build();

    [Test] public void ProtectedMode_Off_WhenAbsent()      => Assert.That(ProductQuench.ProtectedModeEnabled(Cfg()), Is.False);
    [Test] public void ProtectedMode_On_WhenTrue()         => Assert.That(ProductQuench.ProtectedModeEnabled(Cfg(("PreventDrop","true"))), Is.True);
    [Test] public void ProtectedMode_Off_WhenFalse()       => Assert.That(ProductQuench.ProtectedModeEnabled(Cfg(("PreventDrop","false"))), Is.False);
    [Test] public void ProtectedMode_Off_WhenUnparseable() => Assert.That(ProductQuench.ProtectedModeEnabled(Cfg(("PreventDrop","banana"))), Is.False);

    #endregion

    #region ResolveCascadedPolicy (most-specific-wins, WHOLE object) + env-tier composition

    // Deliberately NOT the semantics of ResolveCascadedFlag above: no veto, no combining. The nearest
    // non-null level is returned entire and the levels below it are never consulted.
    private static RebuildPolicy Rebuild(string mode, int? threshold = null, bool onOrderMismatch = false) =>
        new() { Mode = mode, Threshold = threshold, OnOrderMismatch = onOrderMismatch };

    [Test]
    public void RebuildPolicy_TableWinsOverEveryHigherLevel()
    {
        var table = Rebuild("ALWAYS");
        Assert.That(ProductQuench.ResolveCascadedPolicy(Rebuild("NEVER"), Rebuild("THRESHOLD", 5), Rebuild("NEVER"), table),
            Is.SameAs(table));
    }

    [Test]
    public void RebuildPolicy_TemplateWinsWhenTableIsNull()
    {
        var template = Rebuild("THRESHOLD", 2);
        Assert.That(ProductQuench.ResolveCascadedPolicy(Rebuild("NEVER"), Rebuild("ALWAYS"), template, null),
            Is.SameAs(template));
    }

    [Test]
    public void RebuildPolicy_ProductWinsWhenTableAndTemplateAreNull()
    {
        var product = Rebuild("ALWAYS");
        Assert.That(ProductQuench.ResolveCascadedPolicy(Rebuild("NEVER"), product, null, null), Is.SameAs(product));
    }

    [Test]
    public void RebuildPolicy_EnvIsTheLastResortBeforeTheDefault()
    {
        var env = Rebuild("THRESHOLD", 9);
        Assert.That(ProductQuench.ResolveCascadedPolicy(env, null, null, null), Is.SameAs(env));
    }

    [Test]
    public void RebuildPolicy_AllLevelsNull_YieldsNever()
    {
        var resolved = ProductQuench.ResolveCascadedPolicy(null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.Not.Null, "callers must never have to null-check the resolved policy");
            Assert.That(resolved.Mode, Is.EqualTo("NEVER"));
            Assert.That(resolved.Threshold, Is.Null);
            Assert.That(resolved.OnOrderMismatch, Is.False);
        });
    }

    [Test]
    public void RebuildPolicy_TableDoesNotInheritAHigherLevelsThreshold()
    {
        // THE case this resolver exists to get right. The product asks for THRESHOLD 5; the table
        // overrides with ALWAYS and nothing else. If the levels blended field-by-field the table would
        // silently acquire Threshold 5 -- a trigger its author never wrote. It takes its policy WHOLE.
        var resolved = ProductQuench.ResolveCascadedPolicy(
            env: null, product: Rebuild("THRESHOLD", 5), template: null, table: Rebuild("ALWAYS"));

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Mode, Is.EqualTo("ALWAYS"));
            Assert.That(resolved.Threshold, Is.Null,
                "the product's Threshold must NOT leak into a table that declared its own policy");
        });
    }

    [Test]
    public void RebuildPolicy_TableDoesNotInheritAHigherLevelsOnOrderMismatch()
    {
        var resolved = ProductQuench.ResolveCascadedPolicy(
            env: null, product: Rebuild("NEVER", onOrderMismatch: true), template: null, table: Rebuild("ALWAYS"));

        Assert.That(resolved.OnOrderMismatch, Is.False,
            "composition is WITHIN a level -- an order-mismatch trigger does not cross the cascade");
    }

    [Test]
    public void EnvRebuildPolicy_AbsentWhenNoKeyIsSet()
    {
        // An all-defaults object here would shadow a product or template policy that the operator never
        // meant to override, so "nothing configured" has to stay null rather than become NEVER.
        Assert.That(ProductQuench.ReadEnvRebuildPolicy(Cfg()), Is.Null);
    }

    [Test]
    public void EnvRebuildPolicy_ComposesTheThreeKeysIntoOnePolicy()
    {
        var policy = ProductQuench.ReadEnvRebuildPolicy(Cfg(
            ("RebuildPolicyMode", "threshold"),
            ("RebuildPolicyThreshold", "4"),
            ("RebuildPolicyOnOrderMismatch", "true")));

        Assert.Multiple(() =>
        {
            Assert.That(policy.Mode, Is.EqualTo("THRESHOLD"), "the mode is upper-cased to the authored form");
            Assert.That(policy.Threshold, Is.EqualTo(4));
            Assert.That(policy.OnOrderMismatch, Is.True);
        });
    }

    [Test]
    public void EnvRebuildPolicy_OnOrderMismatchAloneStillProducesAPolicy()
    {
        var policy = ProductQuench.ReadEnvRebuildPolicy(Cfg(("RebuildPolicyOnOrderMismatch", "true")));

        Assert.Multiple(() =>
        {
            Assert.That(policy, Is.Not.Null);
            Assert.That(policy.Mode, Is.EqualTo("NEVER"), "an unset mode stays at the default");
            Assert.That(policy.OnOrderMismatch, Is.True);
        });
    }

    [Test]
    public void EnvRebuildPolicy_UnparseableThresholdLeavesItUnset()
    {
        // Left null so SS-TBL-001's shape ("THRESHOLD with no threshold") is what surfaces, rather than
        // a silently invented number.
        var policy = ProductQuench.ReadEnvRebuildPolicy(Cfg(
            ("RebuildPolicyMode", "THRESHOLD"), ("RebuildPolicyThreshold", "banana")));

        Assert.That(policy.Threshold, Is.Null);
    }

    #endregion
}
