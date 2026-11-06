// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

namespace SchemaTongs.UnitTests;

/// <summary>
/// Unit tests for the SQL-body rewriter that collapses source-schema-qualified
/// identifiers to <c>{{SchemaName}}</c> when extracting a schema template
/// (design §7.3, plan §10.2). Cross-schema references and quoted string literals
/// are preserved; unqualified identifiers are flagged for the audit warning
/// but not rewritten (design §7.4).
/// </summary>
[TestFixture]
public class SqlBodyRewriterTests
{
    private const string SourceSchema = "tenant_seed";

    private static SqlBodyRewriterResult Rewrite(string body, string sourceSchema = SourceSchema)
        => SqlBodyRewriter.Rewrite(body, sourceSchema);

    #region §7.3 — Source-schema-qualified reference rewriting

    [Test]
    public void Bracketed_SourceSchemaQualified_Rewritten()
    {
        var result = Rewrite("SELECT * FROM [tenant_seed].[Customers]");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM {{SchemaName}}.[Customers]"));
    }

    // An apostrophe in a comment opened a fake string literal running to the next quote, and the seed-schema references
    // inside that span were left pointing at the seed schema, so the tenant's module read the seed's tables.
    [Test]
    public void AnApostropheInAComment_DoesNotHideTheReferencesAfterIt()
    {
        var result = Rewrite("-- don't read the seed\nSELECT * FROM tenant_seed.Customers WHERE Name = 'x'\n/* it's fine */ SELECT * FROM [tenant_seed].[Orders]");
        Assert.That(result.Body, Is.EqualTo("-- don't read the seed\nSELECT * FROM {{SchemaName}}.Customers WHERE Name = 'x'\n/* it's fine */ SELECT * FROM {{SchemaName}}.[Orders]"));
    }

    [Test]
    public void Unbracketed_SourceSchemaQualified_Rewritten()
    {
        var result = Rewrite("SELECT * FROM tenant_seed.Customers");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM {{SchemaName}}.Customers"));
    }

    [Test]
    public void DoubleQuoted_SourceSchemaQualified_Rewritten()
    {
        var result = Rewrite("SELECT * FROM \"tenant_seed\".\"Customers\"");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM {{SchemaName}}.\"Customers\""));
    }

    [Test]
    public void MultipleSourceReferences_AllRewritten()
    {
        var result = Rewrite(
            "INSERT INTO [tenant_seed].[Orders] SELECT * FROM tenant_seed.OrderStaging");
        Assert.That(result.Body, Is.EqualTo(
            "INSERT INTO {{SchemaName}}.[Orders] SELECT * FROM {{SchemaName}}.OrderStaging"));
    }

    [Test]
    public void MixedBracketedSchemaBareObject_Rewritten()
    {
        // Issue #272: the SSMS view-designer form — bracketed schema, bare object.
        var result = Rewrite("SELECT * FROM [tenant_seed].Customers");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM {{SchemaName}}.Customers"));
    }

    [Test]
    public void MixedBareSchemaBracketedObject_Rewritten()
    {
        var result = Rewrite("SELECT * FROM tenant_seed.[Customers]");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM {{SchemaName}}.[Customers]"));
    }

    [Test]
    public void MixedForm_CrossSchema_StillPreserved()
    {
        // Guard: the relaxed matching must not start rewriting cross-schema refs.
        var result = Rewrite("SELECT * FROM [dbo].Countries");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM [dbo].Countries"));
    }

    #endregion

    #region §7.3 — Cross-schema preservation

    [Test]
    public void CrossSchemaReference_Preserved_Bracketed()
    {
        var result = Rewrite("SELECT * FROM [dbo].[Countries]");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM [dbo].[Countries]"));
    }

    [Test]
    public void CrossSchemaReference_Preserved_Unbracketed()
    {
        var result = Rewrite("SELECT * FROM dbo.Countries");
        Assert.That(result.Body, Is.EqualTo("SELECT * FROM dbo.Countries"));
    }

    [Test]
    public void CrossSchemaAndSource_OnlySourceRewritten()
    {
        var result = Rewrite(
            "SELECT c.* FROM [tenant_seed].[Customers] c JOIN [dbo].[Countries] cn ON c.CountryId = cn.Id");
        Assert.That(result.Body, Is.EqualTo(
            "SELECT c.* FROM {{SchemaName}}.[Customers] c JOIN [dbo].[Countries] cn ON c.CountryId = cn.Id"));
    }

    #endregion

    #region §7.3 — Schema-only reference (no .object) not rewritten

    [Test]
    public void SchemaOnlyBracketedReference_NotRewritten()
    {
        // A bare `[tenant_seed]` without a `.object` suffix is just a name — no rewrite.
        // GRANT EXECUTE ON SCHEMA::[tenant_seed] TO ... is a textbook case.
        const string sql = "GRANT EXECUTE ON SCHEMA::[tenant_seed] TO [SomeUser]";
        var result = Rewrite(sql);
        Assert.That(result.Body, Is.EqualTo(sql));
    }

    [Test]
    public void SchemaOnlyUnbracketedReference_NotRewritten()
    {
        const string sql = "ALTER SCHEMA tenant_seed TRANSFER dbo.SomeTable";
        var result = Rewrite(sql);
        // The `dbo.SomeTable` is cross-schema (preserved); the bare `tenant_seed` is a schema-only
        // reference — not a `<schema>.<obj>` form — and stays as-is.
        Assert.That(result.Body, Is.EqualTo(sql));
    }

    #endregion

    #region §7.4 — Unqualified-identifier audit

    [Test]
    public void Unqualified_FromClause_Identifier_RecordedWithLineNumber()
    {
        const string sql = "SELECT *\r\nFROM Customers";
        var result = Rewrite(sql);
        // Body unchanged (audit, not rewrite).
        Assert.That(result.Body, Is.EqualTo(sql));
        Assert.That(result.UnqualifiedReferenceLines, Is.Not.Null);
        Assert.That(result.UnqualifiedReferenceLines, Does.Contain(2),
            "Unqualified identifier on line 2 must be reported");
    }

    [Test]
    public void Unqualified_Join_Identifier_RecordedWithLineNumber()
    {
        const string sql = "SELECT *\r\nFROM [tenant_seed].[Customers] c\r\nJOIN Orders o ON o.CustomerId = c.Id";
        var result = Rewrite(sql);
        Assert.That(result.Body, Does.StartWith("SELECT *\r\nFROM {{SchemaName}}.[Customers] c"));
        Assert.That(result.UnqualifiedReferenceLines, Does.Contain(3));
    }

    [Test]
    public void Qualified_Reference_DoesNotProduceAuditEntry()
    {
        const string sql = "SELECT * FROM [tenant_seed].[Customers]";
        var result = Rewrite(sql);
        Assert.That(result.UnqualifiedReferenceLines, Is.Empty);
    }

    [Test]
    public void CrossSchemaReference_DoesNotProduceAuditEntry()
    {
        const string sql = "SELECT * FROM [dbo].[Countries]";
        var result = Rewrite(sql);
        Assert.That(result.UnqualifiedReferenceLines, Is.Empty);
    }

    #endregion

    #region §7.3 — String literal containing the source schema (regex-based known limitation)

    /// <summary>
    /// String literals containing the source schema name SHOULD ideally not be rewritten. The
    /// current rewriter is regex-based (see SqlBodyRewriter.cs header for the rationale) and
    /// recognises only standard SQL string literals delimited by single quotes (<c>'...'</c>).
    /// Identifiers inside single-quoted literals are left untouched.
    /// </summary>
    [Test]
    public void SourceSchema_InsideSingleQuotedStringLiteral_NotRewritten()
    {
        const string sql = "PRINT 'tenant_seed.Customers'";
        var result = Rewrite(sql);
        Assert.That(result.Body, Is.EqualTo(sql),
            "Source-schema names embedded in single-quoted string literals must not be rewritten");
    }

    /// <summary>
    /// Documented limitation of the regex-based rewriter: source-schema-qualified references
    /// embedded inside SQL Server <c>N'…'</c> Unicode literals are STILL not rewritten — same
    /// quote rules as a regular string literal. This test guards that behavior.
    /// </summary>
    [Test]
    public void SourceSchema_InsideUnicodeStringLiteral_NotRewritten()
    {
        const string sql = "PRINT N'tenant_seed.Customers'";
        var result = Rewrite(sql);
        Assert.That(result.Body, Is.EqualTo(sql));
    }

    /// <summary>
    /// A SQL string literal with a doubled single-quote escape (<c>''</c>) embedding the source
    /// schema name must still mask correctly — the doubled quote belongs to the literal and
    /// does not terminate it. The masking pattern <c>N?'(?:[^']|'')*'</c> recognises this, so
    /// the body must come back byte-equal and no unqualified-reference audit entry should fire.
    /// </summary>
    [Test]
    public void SourceSchema_InsideStringLiteralWithDoubledQuote_NotRewritten()
    {
        const string sql = "PRINT 'tenant_seed''s data'";
        var result = Rewrite(sql);
        Assert.That(result.Body, Is.EqualTo(sql),
            "Source-schema name inside a string literal with a doubled-quote escape must not be rewritten");
        Assert.That(result.UnqualifiedReferenceLines, Is.Empty,
            "String-literal content must not trigger the unqualified-reference audit");
    }

    #endregion

    #region Robustness

    [Test]
    public void Identifier_LookingLikeSourceSchema_NotRewrittenWhenItIsAColumnPart()
    {
        // `c.tenant_seed` is column access on alias `c`, not a schema-qualified reference.
        // The rewriter is single-token aware: `tenant_seed` is the right side of `c.`, not the left side of `.obj`.
        const string sql = "SELECT c.tenant_seed FROM SomeTable c";
        var result = Rewrite(sql);
        Assert.That(result.Body, Is.EqualTo(sql));
    }

    [Test]
    public void IdentifierWithSourceSchemaAsSubstring_NotRewritten()
    {
        // `tenant_seed_audit` happens to start with `tenant_seed` but is a distinct identifier.
        const string sql = "SELECT * FROM tenant_seed_audit.Log";
        var result = Rewrite(sql);
        Assert.That(result.Body, Is.EqualTo(sql));
    }

    [Test]
    public void EmptySourceSchema_NoRewrites()
    {
        const string sql = "SELECT * FROM [tenant_seed].[Customers]";
        var result = SqlBodyRewriter.Rewrite(sql, "");
        Assert.That(result.Body, Is.EqualTo(sql));
        Assert.That(result.UnqualifiedReferenceLines, Is.Empty);
    }

    [Test]
    public void NullSourceSchema_NoRewrites()
    {
        const string sql = "SELECT * FROM [tenant_seed].[Customers]";
        var result = SqlBodyRewriter.Rewrite(sql, null);
        Assert.That(result.Body, Is.EqualTo(sql));
        Assert.That(result.UnqualifiedReferenceLines, Is.Empty);
    }

    [Test]
    public void NullBody_ReturnsEmptyResult()
    {
        var result = SqlBodyRewriter.Rewrite(null, SourceSchema);
        Assert.That(result.Body, Is.Null.Or.Empty);
        Assert.That(result.UnqualifiedReferenceLines, Is.Empty);
    }

    #endregion
}
