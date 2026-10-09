// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Domain;
using Schema.Isolators;
using Schema.Configuration;
using Schema.Utility;
using Microsoft.Extensions.Configuration;

namespace DataTongs;

public static class Program
{
    public static void Main(string[] args)
    {
        CommandLineParser.HandleCommonSwitches("DataTongs", ToolSpecificSwitches);

        AppDomain.CurrentDomain.UnhandledException += UnhandledException;
        LogFactory.LogInitializer = ConfigHelper.ConfigureLog4Net;
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("DataTongs", LogFactory.GetLogger("ProgressLog").Info);
        CommandLineParser.WarnOnUnrecognizedArguments(KnownArguments, LogFactory.GetLogger("ProgressLog").Warn);
        ConfigHelper.CheckStartupSettings(config, LogFactory.GetLogger("ProgressLog").Warn);
        SettingsContract.WarnOnUnrecognizedKeys(config, SettingsTool.DataTongs, LogFactory.GetLogger("ProgressLog").Warn);

        var platform = ResolvePlatform();

        var tongs = new DataTongs(platform);
        tongs.CastData();
        LogBackup.BackupLogsAndExit("DataTongs", tongs.ExitCode);
    }

    public static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogBackup.UnhandledExceptionLogger("DataTongs", e);
    }

    internal static Platform ResolvePlatform()
    {
        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        var platformValue = config[SettingsKeys.Source.Platform] ?? config[SettingsKeys.Target.Platform];
        if (string.IsNullOrWhiteSpace(platformValue))
            throw new Exception("Platform is required. Set 'Source:Platform' or 'Target:Platform' in DataTongs.settings.json.");

        return PlatformExtensions.ParsePlatform(platformValue);
    }

    private static readonly string[] KnownArguments = { "ConfigureDataDelivery" };

    private static void ToolSpecificSwitches()
    {
        Console.WriteLine("  --ConfigureDataDelivery          Write data delivery configuration into table JSON files after extraction.");
        Console.WriteLine("  --TemplatePath:<path>            Template root (containing Tables/) used by --ConfigureDataDelivery.");
        Console.WriteLine("  --DeliveryEncoding:<encoding>    Content-file encoding for extracted data: Json (default) | Xml. Both work on every source engine.");
    }
}
