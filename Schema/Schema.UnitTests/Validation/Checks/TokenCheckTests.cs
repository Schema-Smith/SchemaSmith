// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

/// <summary>
/// Slice 2.3: text-level {{Token}} lint over the RAW package files, not the loaded domain model —
/// Template.Load resolves known tokens IN PLACE during load (Table.
/// ResolveScriptTokensInTableComponentScripts etc.), so a post-load scan can no longer see
/// definitions vs. references. Uses IFile/IDirectory isolator mocks to present a fake package
/// tree (no real disk, no DB), matching the convention Schema.UnitTests already uses for
/// Template.Load token-resolution tests.
/// </summary>
[TestFixture]
public class TokenCheckTests
{
    private const string PackagePath = @"C:\pkg";

    private IFile _mockFile;
    private IDirectory _mockDirectory;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        _mockFile = Substitute.For<IFile>();
        _mockDirectory = Substitute.For<IDirectory>();
        FactoryContainer.Register(_mockFile);
        FactoryContainer.Register(_mockDirectory);

        _mockDirectory.Exists(PackagePath).Returns(true);
        _mockDirectory.GetFiles(PackagePath, "*.sql", SearchOption.AllDirectories).Returns(System.Array.Empty<string>());
        _mockDirectory.GetFiles(PackagePath, "*.json", SearchOption.AllDirectories).Returns(System.Array.Empty<string>());
    }

    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    private static Product Product() => new() { Name = "Acme", Platform = Platform.SqlServer };

    private static ValidationContext Context() => new(Product(), new List<Template>(), PackagePath);

    private void SqlFiles(params string[] files) =>
        _mockDirectory.GetFiles(PackagePath, "*.sql", SearchOption.AllDirectories).Returns(files);

    private void JsonFiles(params string[] files) =>
        _mockDirectory.GetFiles(PackagePath, "*.json", SearchOption.AllDirectories).Returns(files);

    private void FileContent(string path, string content)
    {
        _mockFile.Exists(path).Returns(true);
        _mockFile.ReadAllText(path).Returns(content);
    }

    [Test]
    public void UndefinedTokenInColumnDefault_IsError()
    {
        var tableFile = Path.Combine(PackagePath, "Templates", "Main", "Tables", "Customer.json");
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Name"": ""Id"", ""Default"": ""{{Nope}}"" } ] }");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-TOK-001"));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Category, Is.EqualTo("Token"));
        Assert.That(findings[0].Location, Is.EqualTo(tableFile));
    }

    [Test]
    public void UndefinedTokenInDataDeliveryShouldApplyExpression_IsError()
    {
        // Locks existing coverage: TokenCheck's raw-text pass scans every .json file under the
        // package, including table files — so a {{token}} inside a DataDelivery gate is already
        // caught, same as any other JSON field. Guards against a future TokenCheck folder-handling
        // refactor silently dropping DataDelivery gates from the scan.
        var tableFile = Path.Join(PackagePath, "Templates", "Main", "Tables", "Customer.json");
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [], ""DataDelivery"": { ""ContentFile"": ""Customer.tabledata"", ""ShouldApplyExpression"": ""{{NotARealToken}}"" } }");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-TOK-001"));
        Assert.That(findings[0].Message, Does.Contain("NotARealToken"));
        Assert.That(findings[0].Location, Is.EqualTo(tableFile));
    }

    [Test]
    public void DefinedProductScriptToken_Referenced_NoFinding()
    {
        var productFile = Path.Combine(PackagePath, "Product.json");
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        JsonFiles(productFile);
        SqlFiles(scriptFile);
        FileContent(productFile, @"{ ""Name"": ""Acme"", ""ScriptTokens"": { ""Env"": ""prod"" } }");
        FileContent(scriptFile, "SELECT '{{Env}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void MalformedToken_UnmatchedBraces_IsError()
    {
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Bad.sql");
        SqlFiles(scriptFile);
        FileContent(scriptFile, "SELECT '{{Foo'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-TOK-002"));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Location, Is.EqualTo(scriptFile));
    }

    [Test]
    public void UnusedDefinedToken_IsWarning()
    {
        var productFile = Path.Combine(PackagePath, "Product.json");
        JsonFiles(productFile);
        FileContent(productFile, @"{ ""Name"": ""Acme"", ""ScriptTokens"": { ""Unused"": ""x"" } }");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-TOK-003"));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
    }

    // A defined token whose NAME the resolver accepts but TokenIdentifierPattern rejects must count as
    // referenced. The deploy substitutes {{My Token}} -- TokenHelper.GetTokensFromString takes any {{...}} under
    // 100 characters with no newline -- so reporting it "never referenced" (SS-TOK-003) is a false warning on a
    // package that works. The identifier filter exists ONLY to keep text that merely looks like a token (a
    // PostgreSQL array literal) out of UNDEFINED detection; its own comment says so. It was also narrowing the
    // REFERENCE set, which is the defect.
    [TestCase("My Token", TestName = "ReferencedToken_WithASpace_IsNotReportedUnused")]
    [TestCase("my-token", TestName = "ReferencedToken_WithAHyphen_IsNotReportedUnused")]
    [TestCase("[Region]", TestName = "ReferencedToken_WithBrackets_IsNotReportedUnused")]
    [TestCase("2ndPass", TestName = "ReferencedToken_WithALeadingDigit_IsNotReportedUnused")]
    public void ReferencedToken_OutsideTheIdentifierPattern_IsNotReportedUnused(string name)
    {
        var productFile = Path.Join(PackagePath, "Product.json");
        var scriptFile = Path.Join(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        JsonFiles(productFile);
        SqlFiles(scriptFile);
        FileContent(productFile, @"{ ""Name"": ""Acme"", ""ScriptTokens"": { """ + name + @""": ""x"" } }");
        FileContent(scriptFile, "SELECT '{{" + name + "}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings.Select(f => f.Code), Does.Not.Contain("SS-TOK-003"),
            $"'{name}' is referenced as {{{{{name}}}}} and the deploy substitutes it, so it is not unused");
        Assert.That(findings, Is.Empty, "a defined, referenced token is not a finding of any kind");
    }

    // The same name DEFINED but genuinely NOT referenced must still warn -- the fix widens what counts as a
    // reference, it must not silence the check.
    [Test]
    public void UnreferencedToken_OutsideTheIdentifierPattern_IsStillReportedUnused()
    {
        var productFile = Path.Join(PackagePath, "Product.json");
        JsonFiles(productFile);
        FileContent(productFile, @"{ ""Name"": ""Acme"", ""ScriptTokens"": { ""My Token"": ""x"" } }");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings.Select(f => f.Code), Does.Contain("SS-TOK-003"));
    }


    [Test]
    public void SchemaNameToken_NotFlagged()
    {
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        SqlFiles(scriptFile);
        FileContent(scriptFile, "SELECT '{{SchemaName}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void BuiltInProductNameToken_NotFlagged()
    {
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        SqlFiles(scriptFile);
        FileContent(scriptFile, "SELECT '{{ProductName}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void BuiltInModelPayloadXmlTwins_NotFlagged()
    {
        // A2: {{TableXml}} / {{MaterializedViewXml}} / {{IndexedViewXml}} are automatic tokens
        // (XML twins of the *Schema tokens) added at template load, so --Validate must recognise them.
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        SqlFiles(scriptFile);
        FileContent(scriptFile, "SELECT '{{TableXml}}', '{{MaterializedViewXml}}', '{{IndexedViewXml}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void BuiltInVersionAndCompatTokens_NotFlagged()
    {
        // A1: {{ServerMajorVersion}} / {{CompatibilityLevel}} are context tokens resolved per-target
        // in Phase B (post-connection), so --Validate must recognise them as defined.
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        SqlFiles(scriptFile);
        FileContent(scriptFile, "SELECT {{ServerMajorVersion}}, {{CompatibilityLevel}}");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void CleanPackage_NoFindings()
    {
        var productFile = Path.Combine(PackagePath, "Product.json");
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        var tableFile = Path.Combine(PackagePath, "Templates", "Main", "Tables", "Customer.json");
        JsonFiles(productFile, tableFile);
        SqlFiles(scriptFile);
        FileContent(productFile, @"{ ""Name"": ""Acme"", ""ScriptTokens"": { ""Env"": ""prod"" } }");
        FileContent(scriptFile, "SELECT '{{Env}}', '{{ProductName}}', '{{SchemaName}}'");
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Name"": ""Id"" } ] }");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    // ---- Extra coverage beyond the brief's mandatory list ----

    [Test]
    public void ExtensionsDerivedToken_ReferencedBareOrPrefixed_NotFlagged()
    {
        // Safety valve: Table.GetCustomTokens' prefixing ("Table."/"MaterializedView."/
        // "IndexedView.") depends on which object owns the Extensions block — this raw-text
        // check can't fully re-derive that, so both bare and every known-prefix form of an
        // Extensions-derived name must be treated as defined.
        var tableFile = Path.Combine(PackagePath, "Templates", "Main", "Tables", "Customer.json");
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        JsonFiles(tableFile);
        SqlFiles(scriptFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [], ""Extensions"": { ""Classification"": ""PII"" } }");
        FileContent(scriptFile, "SELECT '{{Classification}}', '{{Table.Classification}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void JsonSchemasDirectory_FilesExcludedFromScan()
    {
        var schemaFile = Path.Combine(PackagePath, ".json-schemas", "tables.sqlserver.schema");
        JsonFiles(schemaFile);
        FileContent(schemaFile, @"{ ""Description"": ""{{WhateverUndefinedToken}}"" }");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    // ---- Regression: #326 — a table named "Product" (MySQL-style file layout: no schema
    // prefix, so the table file is just Tables/Product.json) must not have its ScriptTokens
    // treated as product/template-level manifest tokens, purely on directory context. ----

    [Test]
    public void ScriptTokensInTableNamedProduct_NotRegisteredAsManifestTokens()
    {
        var tableFile = Path.Join(PackagePath, "Templates", "Main", "Tables", "Product.json");
        var scriptFile = Path.Join(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        JsonFiles(tableFile);
        SqlFiles(scriptFile);
        FileContent(tableFile, @"{ ""Name"": ""Product"", ""Columns"": [], ""ScriptTokens"": { ""TableLevelToken"": ""x"" } }");
        FileContent(scriptFile, "SELECT '{{TableLevelToken}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        // If Tables/Product.json's ScriptTokens were wrongly registered as manifest-level (the
        // bug), "TableLevelToken" would be considered defined and this reference would slip by
        // undetected instead of being flagged as undefined.
        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-TOK-001"));
        Assert.That(findings[0].Message, Does.Contain("TableLevelToken"));
    }

    // ---- Demo-QA-sweep false positives (repo_path/BranchName + Postgres xpath array literals) ----

    [Test]
    public void RepoPathToken_NotFlagged()
    {
        // Product.BranchNameFile defaults to "{{repo_path}}/.git/HEAD" (Schema/Domain/Product.cs:35) —
        // present verbatim in real demo Product.json files.
        var productFile = Path.Combine(PackagePath, "Product.json");
        JsonFiles(productFile);
        FileContent(productFile, @"{ ""Name"": ""Acme"", ""BranchNameFile"": ""{{repo_path}}/.git/HEAD"" }");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void BranchNameToken_NotFlagged()
    {
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Init.sql");
        SqlFiles(scriptFile);
        FileContent(scriptFile, "SELECT '{{BranchName}}'");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void PostgresXpathArrayLiteral_NotFlagged()
    {
        // Legitimate Postgres 2-D text-array literal syntax, not a token reference — the raw
        // "{{...}}" scan misreads it because the extracted content isn't a valid identifier.
        var scriptFile = Path.Combine(PackagePath, "Templates", "Main", "Before Scripts", "Xpath.sql");
        SqlFiles(scriptFile);
        FileContent(scriptFile, "SELECT xpath('//ns', doc, '{{ns,http://schemas.example.com/ns}}'::text[])");

        var findings = new TokenCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }
}
