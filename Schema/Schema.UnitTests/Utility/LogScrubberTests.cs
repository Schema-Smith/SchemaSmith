// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class LogScrubberTests
{
    private static LogHygieneOptions Options(
        bool logTokens = true,
        IEnumerable<string> scrubTokens = null,
        IEnumerable<string> scrubPatterns = null,
        IEnumerable<string> allowTokens = null)
    {
        var options = new LogHygieneOptions { LogTokens = logTokens };
        if (scrubTokens != null) foreach (var t in scrubTokens) options.ScrubTokens.Add(t);
        if (scrubPatterns != null) options.ScrubPatterns.AddRange(scrubPatterns);
        if (allowTokens != null) foreach (var t in allowTokens) options.AllowTokens.Add(t);
        return options;
    }

    // ---- ShouldScrubName: default patterns ----

    [TestCase("DbPassword")]
    [TestCase("Password")]
    [TestCase("ConnectionPwd")]
    [TestCase("ClientSecret")]
    [TestCase("ApiKey")]
    [TestCase("AuthToken")]
    [TestCase("ConnectionString")]
    [TestCase("AwsCredential")]
    public void ShouldScrubName_MatchesDefaultSensitivePatterns(string name)
    {
        Assert.That(LogScrubber.ShouldScrubName(name, LogHygieneOptions.Default), Is.True);
    }

    [TestCase("PASSWORD")]
    [TestCase("dbpassword")]
    [TestCase("ApIkEy")]
    public void ShouldScrubName_IsCaseInsensitive(string name)
    {
        Assert.That(LogScrubber.ShouldScrubName(name, LogHygieneOptions.Default), Is.True);
    }

    [TestCase("ServerName")]
    [TestCase("MaxThreads")]
    [TestCase("MainDB")]
    [TestCase("")]
    public void ShouldScrubName_DoesNotMatchNonSensitiveNames(string name)
    {
        Assert.That(LogScrubber.ShouldScrubName(name, LogHygieneOptions.Default), Is.False);
    }

    // ---- ShouldScrubName: config knobs ----

    [Test]
    public void ShouldScrubName_ScrubTokens_OptInForUnmatchedName()
    {
        var options = Options(scrubTokens: ["Handshake"]);
        Assert.That(LogScrubber.ShouldScrubName("Handshake", options), Is.True);
    }

    [Test]
    public void ShouldScrubName_ScrubPatterns_OptInWithGlobStar()
    {
        var options = Options(scrubPatterns: ["*Salt*"]);
        Assert.That(LogScrubber.ShouldScrubName("SessionSalt", options), Is.True);
    }

    [Test]
    public void ShouldScrubName_BareGlobPattern_DoesNotScrubNonSensitiveNames()
    {
        // A bare "*" trims to an empty string; an unguarded contains-match would then match
        // every name. Suppress-all is LogTokens:false, not a wildcard pattern — a bare "*" is
        // a config mistake and must not silently scrub innocuous names.
        var options = Options(scrubPatterns: ["*"]);
        Assert.That(LogScrubber.ShouldScrubName("ServerName", options), Is.False);
    }

    [Test]
    public void ShouldScrubName_AllowTokens_OptOutOfDefaultPattern()
    {
        var options = Options(allowTokens: ["PublicToken"]);
        Assert.That(LogScrubber.ShouldScrubName("PublicToken", options), Is.False);
    }

    [Test]
    public void ShouldScrubName_AllowTokens_BeatsScrubTokens()
    {
        var options = Options(scrubTokens: ["Ambiguous"], allowTokens: ["Ambiguous"]);
        Assert.That(LogScrubber.ShouldScrubName("Ambiguous", options), Is.False);
    }

    [Test]
    public void ShouldScrubName_NullOptions_FallsBackToDefaults()
    {
        Assert.That(LogScrubber.ShouldScrubName("DbPassword", null), Is.True);
        Assert.That(LogScrubber.ShouldScrubName("ServerName", null), Is.False);
    }

    // ---- ScrubConnectionStringSubfields ----

    [Test]
    public void ScrubConnectionStringSubfields_StripsPassword()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("Server=db1;Password=secret;Database=app");
        Assert.That(result, Is.EqualTo("Server=db1;Password=***;Database=app"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_StripsPwd()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("Server=db1;Pwd=hunter2;Database=app");
        Assert.That(result, Is.EqualTo("Server=db1;Pwd=***;Database=app"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_IsCaseInsensitiveAndPreservesKeyCasing()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("Server=db1;PASSWORD=secret");
        Assert.That(result, Is.EqualTo("Server=db1;PASSWORD=***"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_HandlesPasswordAtEnd()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("Server=db1;Password=secret");
        Assert.That(result, Is.EqualTo("Server=db1;Password=***"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_StripsDoubleQuotedPasswordContainingSemicolon()
    {
        // ADO.NET allows quoting a value that itself contains ';'. A naive [^;]* stops at the
        // first inner ';' and leaks the tail of the password.
        var result = LogScrubber.ScrubConnectionStringSubfields("Server=db1;Password=\"p;q\";Database=app");
        Assert.That(result, Is.EqualTo("Server=db1;Password=***;Database=app"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_StripsSingleQuotedPasswordContainingSemicolon()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("Server=db1;Password='p;q';Database=app");
        Assert.That(result, Is.EqualTo("Server=db1;Password=***;Database=app"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_StripsBraceQuotedPwdContainingSemicolon()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("Server=db1;Pwd={a;b};Database=app");
        Assert.That(result, Is.EqualTo("Server=db1;Pwd=***;Database=app"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_PreservesNonSensitiveFields()
    {
        const string value = "Server=db1;Database=app;User Id=admin";
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(value), Is.EqualTo(value));
    }

    [Test]
    public void ScrubConnectionStringSubfields_DoesNotMatchPasswordSubstringInOtherKeys()
    {
        const string value = "Server=db1;MyPasswordHint=abc";
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(value), Is.EqualTo(value));
    }

    [Test]
    public void ScrubConnectionStringSubfields_NullOrEmptyReturnedAsIs()
    {
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(null), Is.Null);
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(""), Is.EqualTo(""));
    }

    // ---- ScrubTokenValue ----

    [Test]
    public void ScrubTokenValue_SensitiveName_MasksWholeValue()
    {
        Assert.That(LogScrubber.ScrubTokenValue("DbPassword", "secret123", LogHygieneOptions.Default), Is.EqualTo("***"));
    }

    [Test]
    public void ScrubTokenValue_NonSensitiveName_ReturnsValueVerbatim()
    {
        Assert.That(LogScrubber.ScrubTokenValue("MainDB", "TestMain", LogHygieneOptions.Default), Is.EqualTo("TestMain"));
    }

    [Test]
    public void ScrubTokenValue_NonSensitiveName_StillStripsEmbeddedConnectionStringPassword()
    {
        var result = LogScrubber.ScrubTokenValue("Dsn", "Server=db1;Password=secret", LogHygieneOptions.Default);
        Assert.That(result, Is.EqualTo("Server=db1;Password=***"));
    }

    [Test]
    public void ScrubTokenValue_AllowedName_StillStripsEmbeddedConnectionStringPassword()
    {
        var options = Options(allowTokens: ["Dsn"]);
        var result = LogScrubber.ScrubTokenValue("Dsn", "Server=db1;Password=secret", options);
        Assert.That(result, Is.EqualTo("Server=db1;Password=***"));
    }

    [Test]
    public void ScrubTokenValue_NullOptions_FallsBackToDefaults()
    {
        Assert.That(LogScrubber.ScrubTokenValue("ApiKey", "abc", null), Is.EqualTo("***"));
    }

    // ---- LogHygieneOptions.FromConfiguration ----

    [Test]
    public void FromConfiguration_AbsentBlock_DefaultsLogTokensTrueAndEmptyCollections()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            { "SomeOtherKey", "x" }
        }).Build();

        var options = LogHygieneOptions.FromConfiguration(config);

        Assert.Multiple(() =>
        {
            Assert.That(options.LogTokens, Is.True);
            Assert.That(options.ScrubTokens, Is.Empty);
            Assert.That(options.ScrubPatterns, Is.Empty);
            Assert.That(options.AllowTokens, Is.Empty);
        });
    }

    [Test]
    public void FromConfiguration_ReadsAllKnobs()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            { "LogHygiene:LogTokens", "false" },
            { "LogHygiene:ScrubTokens:0", "Handshake" },
            { "LogHygiene:ScrubTokens:1", "TenantSeed" },
            { "LogHygiene:ScrubPatterns:0", "*Salt*" },
            { "LogHygiene:AllowTokens:0", "PublicToken" }
        }).Build();

        var options = LogHygieneOptions.FromConfiguration(config);

        Assert.Multiple(() =>
        {
            Assert.That(options.LogTokens, Is.False);
            Assert.That(options.ScrubTokens, Does.Contain("Handshake"));
            Assert.That(options.ScrubTokens, Does.Contain("TenantSeed"));
            Assert.That(options.ScrubPatterns, Does.Contain("*Salt*"));
            Assert.That(options.AllowTokens, Does.Contain("PublicToken"));
        });
    }

    [Test]
    public void FromConfiguration_ScrubAndAllowTokensAreCaseInsensitive()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            { "LogHygiene:ScrubTokens:0", "Handshake" },
            { "LogHygiene:AllowTokens:0", "PublicToken" }
        }).Build();

        var options = LogHygieneOptions.FromConfiguration(config);

        Assert.Multiple(() =>
        {
            Assert.That(options.ScrubTokens.Contains("HANDSHAKE"), Is.True);
            Assert.That(options.AllowTokens.Contains("publictoken"), Is.True);
        });
    }

    // ---- URL userinfo credentials ----
    //
    // A credential in a URL's userinfo component was untouched: "?password=secret" in a string masked
    // while "user:pass@host" two characters earlier in the SAME string did not. Not reachable through
    // the shipped connection path -- ConnectionString.Build only ever emits discrete keyword=value
    // fields, and a raw --ConnectionString is masked whole by name before subfield scrubbing runs. What
    // IS reachable is any OTHER url-shaped value under an unremarkable name: a webhook, a custom LLM
    // endpoint, a URL inside a map-typed parameter.

    [Test]
    public void ScrubConnectionStringSubfields_MasksUrlUserinfoPassword()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("endpoint=https://svc:hunter2@api.example.com/v1");
        Assert.That(result, Does.Not.Contain("hunter2"),
            "a credential in a URL's userinfo component must not survive scrubbing");
        Assert.That(result, Does.Contain("https://svc:"),
            "the scheme and username are not secrets and are what make a scrubbed log diagnosable");
        Assert.That(result, Does.Contain("@api.example.com/v1"),
            "the host and path must survive -- masking the whole URL would make the log useless");
    }

    [Test]
    public void ScrubConnectionStringSubfields_MasksPostgresStyleUri()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields("postgresql://appuser:s3cr3t@db.internal:5432/orders");
        Assert.That(result, Does.Not.Contain("s3cr3t"));
        Assert.That(result, Does.Contain("@db.internal:5432/orders"),
            "a port after the host must not be mistaken for part of the credential");
    }

    [Test]
    public void ScrubConnectionStringSubfields_LeavesAPortAlone()
    {
        // The failure this guards against: "host:8080" looks exactly like "user:password" until you
        // notice there is no '@' after it. A pattern without that lookahead masks every port number.
        const string url = "http://build.example.com:8080/status";
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(url), Is.EqualTo(url),
            "a URL with a port and no credentials must be returned unchanged");
    }

    [Test]
    public void ScrubConnectionStringSubfields_LeavesUsernameOnlyUrlAlone()
    {
        const string url = "ftp://anonymous@files.example.com/pub";
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(url), Is.EqualTo(url),
            "userinfo with no password has no secret to mask, and masking the username would lose "
            + "information without protecting anything");
    }

    [Test]
    public void ScrubConnectionStringSubfields_DoesNotReachAcrossAPathToALaterAt()
    {
        // "a:b" here is a path segment, and the '@' belongs to a different part of the string. A greedy
        // pattern would mask everything between them and destroy the line.
        const string url = "https://example.com/a:b/mail@example.com";
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(url), Is.EqualTo(url),
            "the credential match must not span a path separator to find a later '@'");
    }

    [Test]
    public void ScrubConnectionStringSubfields_MasksBothAUserinfoAndAKeywordPasswordInOneValue()
    {
        var result = LogScrubber.ScrubConnectionStringSubfields(
            "Server=db1;Password=keywordsecret;Callback=https://hook:urlsecret@example.com");
        Assert.That(result, Does.Not.Contain("keywordsecret"));
        Assert.That(result, Does.Not.Contain("urlsecret"),
            "both forms must mask in the same pass -- the original gap was that one did and one did not, "
            + "in the very same string");
    }

    // ---- ScrubConnectionStringSubfields: line breaks, unclosed quotes, escaped quotes, cost ----

    // Masking ran to the next ';' across line breaks, so a stack trace logged after a connection string vanished.
    [Test]
    public void ScrubConnectionStringSubfields_UnquotedValue_StopsAtTheLineBreak()
    {
        var text = "Login failed for Server=db1;Password=hunter2\n   at Foo.Bar() in C:\\x\\Foo.cs:line 12\n   at Baz.Qux()";

        Assert.That(LogScrubber.ScrubConnectionStringSubfields(text),
            Is.EqualTo("Login failed for Server=db1;Password=***\n   at Foo.Bar() in C:\\x\\Foo.cs:line 12\n   at Baz.Qux()"));
    }

    [Test]
    public void ScrubConnectionStringSubfields_EmptyValueAtALineEnd_DoesNotMaskTheNextLine()
    {
        Assert.That(LogScrubber.ScrubConnectionStringSubfields("Server=db1;Password=\nnext line;tail"),
            Is.EqualTo("Server=db1;Password=***\nnext line;tail"));
    }

    // A value whose quote never closes is all secret. Matching it as unquoted stopped at its first ';' and logged the rest.
    [TestCase("Password=\"S12A;S12B")]
    [TestCase("Password='S12A;S12B")]
    [TestCase("Password={S12A;S12B")]
    [TestCase("Password=\\\"S12A;S12B")]
    public void ScrubConnectionStringSubfields_UnclosedQuote_MasksToTheEnd(string text)
    {
        var scrubbed = LogScrubber.ScrubConnectionStringSubfields(text);

        Assert.That(scrubbed, Does.Not.Contain("S12B"), scrubbed);
        Assert.That(scrubbed, Is.EqualTo("Password=***"));
    }

    // The escapes ADO.NET, ODBC, T-SQL and JSON use for a quote inside a quoted value. Treated as the closing quote,
    // each left the rest of the password in clear.
    [TestCase("Server=db;Password=\"a\"\"b;S2\";Database=app", "Server=db;Password=***;Database=app")]
    [TestCase("Password='a''b;S2';Database=app", "Password=***;Database=app")]
    [TestCase("Password={a}}b;S2};Database=app", "Password=***;Database=app")]
    [TestCase("ALTER LOGIN [x] WITH PASSWORD = 'it''s;secret'", "ALTER LOGIN [x] WITH PASSWORD =***")]
    [TestCase("CREATE LOGIN [x] WITH PASSWORD = N'S1;S2'", "CREATE LOGIN [x] WITH PASSWORD =***")]
    [TestCase("{\"cs\":\"Server=db;Password=\\\"S1;S2\\\"\"}", "{\"cs\":\"Server=db;Password=***\"}")]
    public void ScrubConnectionStringSubfields_EscapedQuoteInsideTheValue_MasksTheWholeValue(string text, string expected)
    {
        Assert.That(LogScrubber.ScrubConnectionStringSubfields(text), Is.EqualTo(expected));
    }

    // An unquoted password that merely starts with N is not an N'...' literal.
    [Test]
    public void ScrubConnectionStringSubfields_UnquotedValueStartingWithN_StopsAtTheSemicolon()
    {
        Assert.That(LogScrubber.ScrubConnectionStringSubfields("Password=Nabc;Database=app"),
            Is.EqualTo("Password=***;Database=app"));
    }

    // The URL pattern was retried from every position of a word with no "://": quadratic, and a 5 MB line never
    // finished. A dotted run gives the most starting positions a word boundary allows.
    [TestCase("0123456789abcdef")]
    [TestCase("a.")]
    public void ScrubConnectionStringSubfields_OnAMegabyteWithNoUrl_IsLinear(string unit)
    {
        var text = new System.Text.StringBuilder().Insert(0, unit, 1_048_576 / unit.Length).ToString();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var scrubbed = LogScrubber.ScrubConnectionStringSubfields(text);

        clock.Stop();
        Assert.That(scrubbed, Is.EqualTo(text));
        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(2), $"took {clock.Elapsed.TotalSeconds:F1}s");
    }
}
