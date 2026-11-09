// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
[NonParallelizable]
public class JsonLayoutTests
{
    private static readonly DateTime At = new(2026, 11, 9, 14, 3, 22, 123, DateTimeKind.Utc);

    private static LoggingEvent Event(string logger, Level level, string message, Exception error = null) =>
        new(new LoggingEventData { LoggerName = logger, Level = level, Message = message, TimeStampUtc = At, ExceptionString = error?.ToString() });

    // The text as written: JObject.Parse would read the timestamp as a date and print it back in the local culture.
    private static JObject Parse(string text) =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(text, new Newtonsoft.Json.JsonSerializerSettings { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })!;

    private static JObject Line(LoggingEvent e)
    {
        var text = JsonLayout.Render(e);
        Assert.That(text, Does.Not.Contain("\n"), "one line per event");
        return Parse(text);
    }

    [Test]
    public void TheAlwaysPresentFields()
    {
        RunContext.Start("SchemaQuench");
        var line = Line(Event("ProgressLog", Level.Info, "Begin Quench of Northwind"));

        Assert.That(line.Properties().Select(p => p.Name), Is.EqualTo(new[] { "timestamp", "level", "message", "tool", "runId", "logger" }));
        Assert.That((string)line["timestamp"], Is.EqualTo("2026-11-09T14:03:22.123Z"));
        Assert.That((string)line["level"], Is.EqualTo("info"));
        Assert.That((string)line["message"], Is.EqualTo("Begin Quench of Northwind"));
        Assert.That((string)line["tool"], Is.EqualTo("SchemaQuench"));
        Assert.That((string)line["runId"], Is.EqualTo(RunContext.Id.ToString()));
        Assert.That((string)line["logger"], Is.EqualTo("progress"));
    }

    [TestCase("ErrorLog", "error")]
    [TestCase("FailureLog", "failure")]
    [TestCase("SomethingElse", "SomethingElse")]
    public void TheLoggerName(string logger, string expected)
    {
        Assert.That((string)Line(Event(logger, Level.Warn, "x"))["logger"], Is.EqualTo(expected));
    }

    [TestCase("DEBUG", "debug")]
    [TestCase("WARN", "warn")]
    [TestCase("ERROR", "error")]
    [TestCase("FATAL", "error")]
    public void TheLevel(string level, string expected)
    {
        var l = LogManager.GetRepository(typeof(JsonLayoutTests).Assembly).LevelMap[level];
        Assert.That((string)Line(Event("ProgressLog", l, "x"))["level"], Is.EqualTo(expected));
    }

    [Test]
    public void AMultiLineQuotedNonAsciiMessage_IsOneValidLine()
    {
        const string message = "Msg 102, \"near\" 'x'\r\nCREATE TABLE \\t Ünïcödé\u0001 end";

        Assert.That((string)Line(Event("ErrorLog", Level.Error, message))["message"], Is.EqualTo(message));
    }

    [Test]
    public void AnException_IsTheErrorField()
    {
        var line = Line(Event("ErrorLog", Level.Error, "failed", new InvalidOperationException("boom")));

        Assert.That((string)line["error"], Does.Contain("InvalidOperationException").And.Contain("boom"));
    }

    [Test]
    public void TheTargetScope_IsFields_AndItsPrefixLeavesTheMessage()
    {
        using (LogScope.Begin("sql01", "Billing_0042", "tenant_0042", "Tenant", "[sql01].[Billing_0042] [Schema: tenant_0042]"))
        {
            var line = Line(Event("ProgressLog", Level.Info, "[sql01].[Billing_0042] [Schema: tenant_0042] Quenching tables"));

            Assert.That((string)line["message"], Is.EqualTo("Quenching tables"));
            Assert.That((string)line["server"], Is.EqualTo("sql01"));
            Assert.That((string)line["database"], Is.EqualTo("Billing_0042"));
            Assert.That((string)line["schema"], Is.EqualTo("tenant_0042"));
            Assert.That((string)line["template"], Is.EqualTo("Tenant"));
            // A message that does not start with the scope's prefix is left whole.
            Assert.That((string)Line(Event("ProgressLog", Level.Info, "  [Billing_0042] Resuming"))["message"], Is.EqualTo("  [Billing_0042] Resuming"));
        }

        var after = Line(Event("ProgressLog", Level.Info, "[sql01].[Billing_0042] done"));
        Assert.That(after.ContainsKey("server"), Is.False, "the scope ends with its unit");
        Assert.That((string)after["message"], Is.EqualTo("[sql01].[Billing_0042] done"));
    }

    [Test]
    public void ARegularTemplate_HasNoSchemaField()
    {
        using (LogScope.Begin("sql01", "Main", null, "Main", "[sql01].[Main]"))
            Assert.That(Line(Event("ProgressLog", Level.Info, "[sql01].[Main] x")).ContainsKey("schema"), Is.False);
    }

    private static (Hierarchy Repository, string File) Repository(string name)
    {
        var repository = (Hierarchy)LogManager.CreateRepository($"{name}-{Guid.NewGuid():N}");
        var file = Path.Join(Path.GetTempPath(), $"jsonlayout-{Guid.NewGuid():N}.log");
        var pattern = new PatternLayout("%date - %message%newline");
        pattern.ActivateOptions();
        var console = new ManagedColoredConsoleAppender { Name = "console", Layout = pattern };
        console.ActivateOptions();
        var fileAppender = new FileAppender { Name = "ProgressLog", File = file, AppendToFile = false, Layout = pattern };
        fileAppender.ActivateOptions();
        var logger = (Logger)repository.GetLogger("ProgressLog");
        logger.AddAppender(console);
        logger.AddAppender(fileAppender);
        logger.Level = Level.All;
        repository.Configured = true;
        return (repository, file);
    }

    private static IConfiguration Config(string format) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["LogFormat"] = format }).Build();

    [Test]
    public void JsonFormat_EveryFileAndConsoleLineIsJson()
    {
        var (repository, file) = Repository("json");
        var stdout = new StringWriter();
        var old = Console.Out;
        Console.SetOut(stdout);
        try
        {
            ConfigHelper.ApplyLogFormat(repository, Config("json"), _ => { });
            // A coloured console writes colour codes on a terminal, which a JSON reader cannot take.
            Assert.That(repository.GetAppenders().OfType<ManagedColoredConsoleAppender>(), Is.Empty);
            var log = LogManager.GetLogger(repository.Name, "ProgressLog");
            log.Info("first");
            log.Warn("second\nline");
        }
        finally
        {
            Console.SetOut(old);
            repository.Shutdown();
        }

        var fileLines = File.ReadAllLines(file);
        File.Delete(file);
        var consoleLines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(fileLines, Has.Length.EqualTo(2));
        Assert.That(consoleLines, Has.Length.EqualTo(2));
        Assert.That(fileLines.Concat(consoleLines).Select(l => (string)Parse(l)["message"]), Is.EqualTo(new[] { "first", "second\nline", "first", "second\nline" }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("text")]
    [TestCase("TEXT")]
    public void TextFormat_LeavesTheLayoutsAsTheyWere(string format)
    {
        var (repository, file) = Repository("text");
        try
        {
            ConfigHelper.ApplyLogFormat(repository, Config(format), _ => { });
            LogManager.GetLogger(repository.Name, "ProgressLog").Info("plain");
        }
        finally
        {
            repository.Shutdown();
        }

        var lines = File.ReadAllLines(file);
        File.Delete(file);
        Assert.That(lines.Single(), Does.Match(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3} - plain$"));
    }

    [Test]
    public void AnUnknownFormat_Warns_AndStaysText()
    {
        var (repository, file) = Repository("unknown");
        var warnings = new List<string>();
        try
        {
            ConfigHelper.ApplyLogFormat(repository, Config("xml"), warnings.Add);
            LogManager.GetLogger(repository.Name, "ProgressLog").Info("plain");
        }
        finally
        {
            repository.Shutdown();
        }

        var lines = File.ReadAllLines(file);
        File.Delete(file);
        Assert.That(lines.Single(), Does.EndWith(" - plain"));
        Assert.That(warnings.Single(), Does.Contain("LogFormat").And.Contain("xml"));
    }
}
