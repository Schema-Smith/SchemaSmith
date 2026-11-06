using System;
// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class IdentifierTests
{
    [Test]
    public void EscapeDelimited_SqlServer_DoublesClosingBracket()
    {
        Assert.That(Identifier.EscapeDelimited("weird]name", Platform.SqlServer), Is.EqualTo("weird]]name"));
    }

    [Test]
    public void EscapeDelimited_PostgreSql_DoublesDoubleQuote()
    {
        Assert.That(Identifier.EscapeDelimited("weird\"name", Platform.PostgreSQL), Is.EqualTo("weird\"\"name"));
    }

    [Test]
    public void EscapeDelimited_MySql_DoublesBacktick()
    {
        Assert.That(Identifier.EscapeDelimited("weird`name", Platform.MySQL), Is.EqualTo("weird``name"));
    }

    [Test]
    public void EscapeDelimited_MariaDb_UsesMySqlBacktickRules()
    {
        Assert.That(Identifier.EscapeDelimited("weird`name", Platform.MariaDb), Is.EqualTo("weird``name"));
    }

    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    public void EscapeDelimited_NullOrEmpty_PassesThrough(Platform platform)
    {
        Assert.That(Identifier.EscapeDelimited(null, platform), Is.Null);
        Assert.That(Identifier.EscapeDelimited("", platform), Is.EqualTo(""));
    }

    [Test]
    public void EscapeDelimited_RoundTripsThroughUnwrap_SqlServer()
    {
        const string name = "a]b]]c";
        var wrapped = $"[{Identifier.EscapeDelimited(name, Platform.SqlServer)}]";
        Assert.That(Identifier.Unwrap(wrapped, Platform.SqlServer), Is.EqualTo(name));
    }

    [Test]
    public void Unwrap_SqlServer_StripsBracketDelimiters()
    {
        Assert.That(Identifier.Unwrap("[dbo]", Platform.SqlServer), Is.EqualTo("dbo"));
    }

    [Test]
    public void Unwrap_SqlServer_UnescapesDoubledClosingBrackets()
    {
        Assert.That(Identifier.Unwrap("[weird]]name]", Platform.SqlServer), Is.EqualTo("weird]name"));
    }

    [Test]
    public void Unwrap_SqlServer_LeavesUndelimitedAsIs()
    {
        Assert.That(Identifier.Unwrap("dbo", Platform.SqlServer), Is.EqualTo("dbo"));
    }

    [Test]
    public void Unwrap_SqlServer_LeavesUnmatchedDelimitersAsIs()
    {
        Assert.That(Identifier.Unwrap("[dbo", Platform.SqlServer), Is.EqualTo("[dbo"));
        Assert.That(Identifier.Unwrap("dbo]", Platform.SqlServer), Is.EqualTo("dbo]"));
    }

    [Test]
    public void Unwrap_PostgreSQL_StripsDoubleQuoteDelimiters()
    {
        Assert.That(Identifier.Unwrap("\"public\"", Platform.PostgreSQL), Is.EqualTo("public"));
    }

    [Test]
    public void Unwrap_PostgreSQL_UnescapesDoubledQuotes()
    {
        Assert.That(Identifier.Unwrap("\"weird\"\"name\"", Platform.PostgreSQL), Is.EqualTo("weird\"name"));
    }

    [Test]
    public void Unwrap_PostgreSQL_LeavesUndelimitedAsIs()
    {
        Assert.That(Identifier.Unwrap("public", Platform.PostgreSQL), Is.EqualTo("public"));
    }

    [Test]
    public void Unwrap_MySQL_DelegatesToBacktickUnquote()
    {
        Assert.That(Identifier.Unwrap("`my_db`", Platform.MySQL), Is.EqualTo("my_db"));
        Assert.That(Identifier.Unwrap("`my``db`", Platform.MySQL), Is.EqualTo("my`db"));
    }

    [Test]
    public void Unwrap_HandlesNullOrEmpty()
    {
        Assert.That(Identifier.Unwrap(null, Platform.SqlServer), Is.Null);
        Assert.That(Identifier.Unwrap("", Platform.PostgreSQL), Is.EqualTo(""));
    }

    [Test]
    public void Unwrap_PostgreSQL_QuoteWrappedSourceSchemaAndFkSchema_BothReduceToSameValue()
    {
        // Edge case: source schema configured as "tenant_seed" (with literal double quotes),
        // and FK RelatedTableSchema = "\"tenant_seed\"" (also quote-wrapped). Both sides must
        // unwrap to the bare identifier so the same-source comparison succeeds and the FK is
        // nulled. Tests the equivalence property: two differently-delimited forms of the same
        // name reduce to the same value after Unwrap.
        const string configuredWithQuotes = "\"tenant_seed\"";
        const string fkSchemaWithQuotes = "\"tenant_seed\"";
        var unwrappedConfig = Identifier.Unwrap(configuredWithQuotes, Platform.PostgreSQL);
        var unwrappedFk = Identifier.Unwrap(fkSchemaWithQuotes, Platform.PostgreSQL);
        Assert.That(unwrappedConfig, Is.EqualTo("tenant_seed"));
        Assert.That(unwrappedFk, Is.EqualTo("tenant_seed"));
        Assert.That(unwrappedConfig, Is.EqualTo(unwrappedFk),
            "Quote-wrapped source schema config and FK schema must unwrap to the same bare value");
    }

    // ---- SplitQualifiedName (#272: delimiter-aware schema/object split) ----

    // A doubled closing delimiter is the delimiter inside the name, not its end: the dot after it is still in the name.
    [TestCase("[a]]b.c]", "SqlServer", null, "a]b.c")]
    [TestCase("[s]]x].[a]]b.c]", "SqlServer", "s]x", "a]b.c")]
    [TestCase("\"a\"\"b.c\"", "PostgreSQL", null, "a\"b.c")]
    [TestCase("`a``b.c`", "MySQL", null, "a`b.c")]
    public void SplitQualifiedName_ADoubledDelimiterStaysInTheName(string value, string platform, string schema, string name)
    {
        var split = Identifier.SplitQualifiedName(value, Enum.Parse<Platform>(platform));
        Assert.Multiple(() =>
        {
            Assert.That(split.Schema, Is.EqualTo(schema));
            Assert.That(split.Name, Is.EqualTo(name));
        });
    }


    [Test]
    public void Split_SqlServer_FullyBracketed()
    {
        var (schema, name) = Identifier.SplitQualifiedName("[dbo].[Orders]", Platform.SqlServer);
        Assert.That(schema, Is.EqualTo("dbo"));
        Assert.That(name, Is.EqualTo("Orders"));
    }

    [Test]
    public void Split_SqlServer_Bare()
    {
        var (schema, name) = Identifier.SplitQualifiedName("dbo.Orders", Platform.SqlServer);
        Assert.That(schema, Is.EqualTo("dbo"));
        Assert.That(name, Is.EqualTo("Orders"));
    }

    [Test]
    public void Split_SqlServer_MixedBracketedSchemaBareObject()
    {
        var (schema, name) = Identifier.SplitQualifiedName("[dbo].Orders", Platform.SqlServer);
        Assert.That(schema, Is.EqualTo("dbo"));
        Assert.That(name, Is.EqualTo("Orders"));
    }

    [Test]
    public void Split_SqlServer_MixedBareSchemaBracketedObject()
    {
        var (schema, name) = Identifier.SplitQualifiedName("dbo.[Orders]", Platform.SqlServer);
        Assert.That(schema, Is.EqualTo("dbo"));
        Assert.That(name, Is.EqualTo("Orders"));
    }

    [Test]
    public void Split_SqlServer_DottedDelimitedSchema_NotMisSplit()
    {
        var (schema, name) = Identifier.SplitQualifiedName("[my.schema].[Orders]", Platform.SqlServer);
        Assert.That(schema, Is.EqualTo("my.schema"));
        Assert.That(name, Is.EqualTo("Orders"));
    }

    [Test]
    public void Split_PostgreSql_DottedDelimitedSchema_NotMisSplit()
    {
        var (schema, name) = Identifier.SplitQualifiedName("\"my.schema\".\"Orders\"", Platform.PostgreSQL);
        Assert.That(schema, Is.EqualTo("my.schema"));
        Assert.That(name, Is.EqualTo("Orders"));
    }

    [Test]
    public void Split_Unqualified_ReturnsNullSchema()
    {
        var (schema, name) = Identifier.SplitQualifiedName("Orders", Platform.SqlServer);
        Assert.That(schema, Is.Null);
        Assert.That(name, Is.EqualTo("Orders"));
    }

    [TestCase(Platform.SqlServer, "[a],[b]", new[] { "[a]", "[b]" })]
    [TestCase(Platform.SqlServer, "[a,b] , [c]]d,e]", new[] { "[a,b]", "[c]]d,e]" })]
    [TestCase(Platform.SqlServer, "*[k,1],G[g],[x] DESC", new[] { "*[k,1]", "G[g]", "[x] DESC" })]
    [TestCase(Platform.SqlServer, "CAST([a] AS DECIMAL(10,2)),'x,y',[b]", new[] { "CAST([a] AS DECIMAL(10,2))", "'x,y'", "[b]" })]
    [TestCase(Platform.PostgreSQL, "\"a,\"\"b\",\"c\"", new[] { "\"a,\"\"b\"", "\"c\"" })]
    [TestCase(Platform.MySQL, "`a,``b`,`c`", new[] { "`a,``b`", "`c`" })]
    [TestCase(Platform.SqlServer, "a, b,,", new[] { "a", "b" })]
    [TestCase(Platform.SqlServer, "", new string[0])]
    public void SplitDelimitedList_SplitsOnlyAtTheCommasBetweenItems(Platform platform, string list, string[] expected)
    {
        Assert.That(Identifier.SplitDelimitedList(list, platform), Is.EqualTo(expected));
    }
}
