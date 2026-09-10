// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Isolators;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Per-iteration <c>{{SchemaName}}</c> substitution over the serialized model payloads.
///
/// <para>Under a schema template, <c>SchemaDefaultResolver</c> resolves every schema-qualified
/// object's <c>Schema</c> to the literal <c>{{SchemaName}}</c> token, and the payload JSON is
/// serialized carrying that token verbatim. Each iteration then substitutes its own tenant schema in.
/// A payload that is resolved but never substituted is strictly worse than one that was never
/// resolved: instead of falling back to <c>public</c>, the engine receives the literal token as a
/// schema name.</para>
///
/// <para>This asserts the payloads as a SET rather than one at a time. The defect these cover was a
/// single missing line in a block of six near-identical assignments -- the shape where a per-item
/// test passes for every item somebody remembered to write one for.</para>
/// </summary>
[TestFixture]
public class DatabaseQuenchSchemaTemplatePayloadTests
{
    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    private static DatabaseQuench SchemaTemplateQuench(Template template, string schemaName = "tenant_a") =>
        new("srv", new Product { Name = "P", Platform = Platform.PostgreSQL }, template, "db",
            schemaName, false, "false", false, "false", "false", "false", "false", "false", "false",
            "false", "false", true, false, null);

    private static Template SchemaTemplateWithDeclarativeTypes()
    {
        var template = new Template
        {
            Name = "tenant_body",
            SchemaIdentificationScript = "SELECT 'tenant_a' AS schema_name"
        };
        template.Tables.Add(new PostgreSqlTable { Name = "customers" });
        template.MaterializedViews.Add(new PostgreSqlMaterializedView { Name = "mv_active" });
        template.EnumTypes.Add(new PostgreSqlEnumType { Name = "order_status" });
        template.DomainTypes.Add(new PostgreSqlDomainType { Name = "email_address", DataType = "text" });
        template.Sequences.Add(new PostgreSqlSequence { Name = "order_seq" });
        template.Product = new Product { Name = "P", Platform = Platform.PostgreSQL };

        // What Template.Load does: resolve first, then serialize, so each payload carries the token.
        SchemaDefaultResolver.Resolve(template);
        template.TableSchema = Newtonsoft.Json.Linq.JArray.FromObject(template.Tables).ToString();
        template.MaterializedViewSchema = Newtonsoft.Json.Linq.JArray.FromObject(template.MaterializedViews).ToString();
        template.EnumTypeSchema = Newtonsoft.Json.Linq.JArray.FromObject(template.EnumTypes).ToString();
        template.DomainTypeSchema = Newtonsoft.Json.Linq.JArray.FromObject(template.DomainTypes).ToString();
        template.SequenceSchema = Newtonsoft.Json.Linq.JArray.FromObject(template.Sequences).ToString();
        return template;
    }

    [Test]
    public void PrepareIterationContent_SchemaTemplate_SubstitutesTheTokenInEveryModelPayload()
    {
        var template = SchemaTemplateWithDeclarativeTypes();
        var quench = SchemaTemplateQuench(template);

        quench.PrepareIterationContent();

        var payloads = new (string Name, string Json)[]
        {
            ("tables", quench.IterationTableSchema),
            ("materialized views", quench.IterationMaterializedViewSchema),
            ("enum types", quench.IterationEnumTypeSchema),
            ("domain types", quench.IterationDomainTypeSchema),
            ("sequences", quench.IterationSequenceSchema),
        };

        Assert.Multiple(() =>
        {
            foreach (var (name, json) in payloads)
            {
                Assert.That(json, Does.Not.Contain("{{SchemaName}}"),
                    $"the {name} payload still carries the unsubstituted token, so the engine is handed "
                    + "\"{{SchemaName}}\" as a literal schema name -- worse than the null it would have had "
                    + "before the resolver ran, because null at least fell back to public");
                Assert.That(json, Does.Contain("tenant_a"),
                    $"the {name} payload must name this iteration's tenant schema");
            }
        });
    }

    // A regular template resolves to the platform default instead, and must never acquire the token.
    [Test]
    public void PrepareIterationContent_RegularTemplate_PayloadsCarryThePlatformDefault()
    {
        var template = new Template { Name = "plain" };
        template.DomainTypes.Add(new PostgreSqlDomainType { Name = "email_address", DataType = "text" });
        template.Product = new Product { Name = "P", Platform = Platform.PostgreSQL };
        SchemaDefaultResolver.Resolve(template);
        template.DomainTypeSchema = Newtonsoft.Json.Linq.JArray.FromObject(template.DomainTypes).ToString();

        var quench = SchemaTemplateQuench(template, schemaName: "");
        quench.PrepareIterationContent();

        Assert.Multiple(() =>
        {
            Assert.That(quench.IterationDomainTypeSchema, Does.Contain("public"));
            Assert.That(quench.IterationDomainTypeSchema, Does.Not.Contain("{{SchemaName}}"));
        });
    }
}
