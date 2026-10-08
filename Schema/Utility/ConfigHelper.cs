// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Reflection;
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
            var settingsFile = CommandLineParser.ValueOfSwitch("ConfigFile", null) ?? $"{app}.settings.json";
            var builder = new ConfigurationBuilder()
                .SetBasePath(basePath);

            // Check AppContext.BaseDirectory as fallback (test runners may not set CWD to the output directory)
            var appBasePath = AppContext.BaseDirectory;
            if (!File.Exists(Path.Join(basePath, settingsFile)) && File.Exists(Path.Join(appBasePath, settingsFile)))
                builder.SetBasePath(appBasePath);

            builder.AddJsonFile(settingsFile, optional: true)
#if DEBUG
                .AddUserSecrets(Assembly.GetCallingAssembly(), optional: true)
#endif
                .AddEnvironmentVariables("SmithySettings_")
                .AddInMemoryCollection(CommandLineParser.ConfigOverrides);

            config = builder.Build();
            FactoryContainer.Register(config);
            logLine?.Invoke(app);

            ConfigurationLogger.LogCommandLine(config, logLine);
            ConfigurationLogger.LogConfiguration(config, logLine);

            return config;
        }
    }
}
