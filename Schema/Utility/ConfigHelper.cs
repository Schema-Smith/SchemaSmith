// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using log4net;
using log4net.Config;
using Microsoft.Extensions.Configuration;
using Schema.Isolators;

namespace Schema.Utility;

public static class ConfigHelper
{
    public static void ConfigureLog4Net()
    {
        var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly());
        GlobalContext.Properties["LogPath"] = ResolveLogPath();
        try
        {
            using var configStream = ResourceLoader.Load("Log4Net.config").ToStream();
            XmlConfigurator.Configure(logRepository, configStream);
        }
        catch
        {
            XmlConfigurator.Configure(logRepository); // use default config if not embedded
        }

        // Every logger, not the root: the tools' loggers are non-additive, so a root appender would see nothing.
        var counter = new WarningCounter();
        foreach (var logger in logRepository.GetCurrentLoggers())
            ((log4net.Repository.Hierarchy.Logger)logger).AddAppender(counter);
    }

    public static string ResolveLogPath()
    {
        var toolDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var raw = CommandLineParser.ValueOfSwitch("LogPath", null) ?? toolDir;
        // Path.GetFullPath resolves a relative path against the process CWD (the invocation
        // directory) and leaves an absolute path unchanged — so the log4net property and the
        // log backup step below always agree on one absolute directory (#331).
        return Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // NOTE: No Platform constant — unified tools read platform from Product.Platform

    /// <summary>
    /// A folder setting, trimmed, with a blank value meaning the current directory. Untrimmed, a value of spaces made
    /// SchemaTongs write into a folder named " " (which Windows tools cannot open) and DataTongs throw on it.
    /// </summary>
    public static string PathSetting(IConfiguration config, string key) =>
        config?[key]?.Trim() is { Length: > 0 } path ? path : ".";

    // Keyed by the configuration it was built for, so a configuration a test registers directly never inherits one.
    private static readonly ConditionalWeakTable<IConfigurationRoot, string> MissingSettingsFiles = new();

    /// <summary>
    /// Stops the run on settings it cannot use, before anything connects: a <c>--ConfigFile</c> that does not exist.
    /// A <c>LogHygiene</c> block the tools cannot read is a warning, naming the key, since the run can go ahead.
    /// </summary>
    public static void CheckStartupSettings(IConfigurationRoot config, Action<string> warn)
    {
        if (config != null && MissingSettingsFiles.TryGetValue(config, out var missing))
            throw new RunFailedException($"Settings file not found: {missing}. Nothing was run. A --ConfigFile path is read relative to the current directory.");

        foreach (var problem in LogHygieneOptions.Problems(config))
            warn?.Invoke(problem);
    }

    public static IConfigurationRoot GetAppSettingsAndUserSecrets(string app, Action<string> logLine)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            if (config != null) return config;

            // Config bootstrap must read the REAL current directory, not a mockable isolator:
            // tests register a mock IDirectory for other components, and a mock's null
            // GetCurrentDirectory() would break ConfigurationBuilder.SetBasePath here.
            var basePath = Directory.GetCurrentDirectory();
            var explicitFile = CommandLineParser.ValueOfSwitch("ConfigFile", null);
            var settingsFile = explicitFile ?? $"{app}.settings.json";
            var builder = new ConfigurationBuilder()
                .SetBasePath(basePath);

            // The default file falls back to the tool's own folder (test runners may not set CWD to the output
            // directory). A file named with --ConfigFile never does: reading a same-named file from somewhere else
            // would run with settings nobody asked for.
            string missingFile = null;
            if (!File.Exists(Path.GetFullPath(settingsFile, basePath)))
            {
                var appBasePath = AppContext.BaseDirectory;
                if (explicitFile != null) missingFile = Path.GetFullPath(settingsFile, basePath);
                else if (File.Exists(Path.Join(appBasePath, settingsFile))) builder.SetBasePath(appBasePath);
            }

            builder.AddJsonFile(settingsFile, optional: true)
#if DEBUG
                .AddUserSecrets(Assembly.GetCallingAssembly(), optional: true)
#endif
                .AddEnvironmentVariables("SmithySettings_")
                .AddInMemoryCollection(CommandLineParser.ConfigOverrides);

            config = builder.Build();
            if (missingFile != null) MissingSettingsFiles.AddOrUpdate(config, missingFile);
            FactoryContainer.Register(config);
            logLine?.Invoke(app);

            ConfigurationLogger.LogCommandLine(config, logLine);
            ConfigurationLogger.LogConfiguration(config, logLine);

            return config;
        }
    }
}
