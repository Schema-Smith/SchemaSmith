// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using Schema.Domain;
using Schema.Isolators;
using Schema.Configuration;
using Schema.Utility;

namespace SchemaTongs;

public static class Program
{
    public static void Main(string[] args)
    {
        CommandLineParser.HandleCommonSwitches("SchemaTongs", ToolSpecificSwitches);

        AppDomain.CurrentDomain.UnhandledException += UnhandledException;
        LogFactory.LogInitializer = ConfigHelper.ConfigureLog4Net;
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("SchemaTongs", LogFactory.GetLogger("ProgressLog").Info);
        CommandLineParser.WarnOnUnrecognizedArguments(KnownArguments, LogFactory.GetLogger("ProgressLog").Warn);
        SettingsContract.WarnOnUnrecognizedKeys(config, SettingsTool.SchemaTongs, LogFactory.GetLogger("ProgressLog").Warn);

        if (CommandLineParser.ContainsSwitch("WriteSchemasOnly"))
        {
            WriteSchemasOnly();
            return;
        }

        var platform = ResolvePlatform();

        var tongs = new SchemaTongs(platform);
        tongs.PreFlightSourceVersion();   // fail fast on an unsupported source before kindling
        tongs.CastTemplate();
        // Matches SchemaQuench's partial-failure convention (exit 2): a table extraction failure no
        // longer aborts the run, so a clean exit code must not be the only signal a caller checks —
        // the package on disk can be short tables that failed and were skipped (see SchemaTongs.Failed).
        LogBackup.BackupLogsAndExit("SchemaTongs", tongs.Failed ? 2 : 0);
    }

    public static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogBackup.UnhandledExceptionLogger("SchemaTongs", e);
    }

    /// <summary>
    /// Regenerates the .json-schemas/*.schema validation files for an existing product
    /// without connecting to a database. Reads the platform from Product.json and writes
    /// the schemas on the fly from the current C# domain types.
    /// </summary>
    internal static void WriteSchemasOnly()
    {
        var config = FactoryContainer.ResolveOrCreate<Microsoft.Extensions.Configuration.IConfigurationRoot>();
        var productPath = config[SettingsKeys.ProductKeys.Path] ?? ".";
        var productFile = Path.Combine(productPath, "Product.json");

        if (!FileWrapper.GetFromFactory().Exists(productFile))
            throw new Exception($"Product.json not found at '{productFile}'. Set 'Product:Path' to the product directory.");

        var product = Product.LoadForDisplay(productFile);
        Console.WriteLine($"Regenerating .json-schemas for {product.Name} ({product.Platform})...");
        RepositoryHelper.WriteSchemaFiles(productPath, product.Platform,
            warning => Console.WriteLine($"WARNING: {warning}"));
        Console.WriteLine("Done.");
    }

    internal static Platform ResolvePlatform()
    {
        var config = FactoryContainer.ResolveOrCreate<Microsoft.Extensions.Configuration.IConfigurationRoot>();
        var platformValue = config[SettingsKeys.Target.Platform] ?? config[SettingsKeys.Source.Platform];
        if (string.IsNullOrWhiteSpace(platformValue))
            throw new Exception("Platform is required. Set 'Target:Platform' or 'Source:Platform' in SchemaTongs.settings.json.");

        return PlatformExtensions.ParsePlatform(platformValue);
    }

    private static readonly string[] KnownArguments = { "WriteSchemasOnly" };

    private static void ToolSpecificSwitches()
    {
        Console.WriteLine("  --WriteSchemasOnly               Regenerate .json-schemas for an existing product without connecting to a database.");
    }
}
