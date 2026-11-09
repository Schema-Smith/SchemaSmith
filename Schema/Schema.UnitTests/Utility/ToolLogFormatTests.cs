// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using log4net;
using log4net.Config;
using log4net.Repository.Hierarchy;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// Each tool's own log configuration, switched to LogFormat json: every line any of its loggers writes, to its log files
/// and to the console, is a JSON object, and lines the tool prints itself go to standard error.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ToolLogFormatTests
{
    private string _logDir;

    [SetUp]
    public void SetUp() => _logDir = Path.Join(Path.GetTempPath(), $"toollogs-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        ConfigHelper.ApplyLogFormat(LogManager.CreateRepository($"reset-{Guid.NewGuid():N}"), null, null);
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Join(dir.FullName, "SchemaSmith.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static JObject Parse(string line) =>
        JsonConvert.DeserializeObject<JObject>(line, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;

    [TestCase("SchemaQuench")]
    [TestCase("SchemaTongs")]
    [TestCase("DataTongs")]
    [TestCase("SchemaShears")]
    public void EveryLineOfTheToolsLogs_IsJson(string tool)
    {
        var configFile = Path.Join(RepoRoot(), tool, "Log4Net.config");
        var loggers = XDocument.Load(configFile).Descendants("logger").Select(l => (string)l.Attribute("name")).ToList();
        Assert.That(loggers, Does.Contain("ProgressLog"), $"{tool}'s log configuration");

        Directory.CreateDirectory(_logDir);
        GlobalContext.Properties["LogPath"] = _logDir;
        var repository = (Hierarchy)LogManager.CreateRepository($"{tool}-{Guid.NewGuid():N}");
        XmlConfigurator.Configure(repository, new FileInfo(configFile));
        RunContext.Start(tool);
        var stdout = new StringWriter();
        var old = Console.Out;
        Console.SetOut(stdout);
        try
        {
            ConfigHelper.ApplyLogFormat(repository, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["LogFormat"] = "json" }).Build(), null);
            foreach (var name in loggers)
            {
                var log = LogManager.GetLogger(repository.Name, name);
                log.Info($"{name} info");
                log.Error($"{name} error\nsecond line", new InvalidOperationException("boom"));
            }
        }
        finally
        {
            Console.SetOut(old);
            repository.Shutdown();
        }

        var files = Directory.GetFiles(_logDir);
        Assert.That(files, Is.Not.Empty, "log files written");
        var lines = files.SelectMany(File.ReadAllLines).Concat(stdout.ToString().Split('\n'))
            .Where(l => l.Length > 0).ToList();
        Assert.That(lines, Has.Count.GreaterThanOrEqualTo(loggers.Count * 2));
        foreach (var line in lines)
        {
            var json = Parse(line);
            Assert.That((string)json["tool"], Is.EqualTo(tool), line);
            Assert.That((string)json["runId"], Is.EqualTo(RunContext.Id.ToString()), line);
        }
    }

    [Test]
    public void ALineTheToolPrintsItself_GoesToStandardErrorUnderJson_AndToStandardOutputOtherwise()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (oldOut, oldError) = (Console.Out, Console.Error);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            ConsoleOutput.Line("plain run");
            ConfigHelper.ApplyLogFormat(LogManager.CreateRepository($"json-{Guid.NewGuid():N}"), new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["LogFormat"] = "json" }).Build(), null);
            ConsoleOutput.Line("json run");
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }

        Assert.That(stdout.ToString(), Does.Contain("plain run").And.Not.Contain("json run"));
        Assert.That(stderr.ToString(), Does.Contain("json run"));
    }
}
