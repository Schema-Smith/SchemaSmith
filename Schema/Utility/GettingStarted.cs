// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Schema.Isolators;

namespace Schema.Utility;

/// <summary>A setting a first run needs. A null <see cref="Meaning"/> counts as configuration but is not listed.</summary>
public record FirstRunSetting(string Key, string Meaning);

/// <summary>
/// What a tool does when it is run with no arguments on an install nobody has configured: explain how to configure it
/// instead of attempting a run that can only fail. A bare run is the normal mode -- it runs from the settings file --
/// so this applies only when nothing at all is configured, and anything a user has set leads to a normal run.
/// </summary>
public static class GettingStarted
{
    private const string EnvironmentPrefix = "SmithySettings_";

    /// <summary>
    /// Shows the getting-started screen and exits when the run has no arguments, no <c>SmithySettings_</c> environment
    /// variable, and either a settings file whose first-run settings are all blank (exit 0) or no settings file in the
    /// current directory or the tool's folder (exit 2). Returns whether it did.
    /// </summary>
    public static bool ShowIfUnconfigured(string app, string summary, IReadOnlyList<FirstRunSetting> settings, string[] args) =>
        ShowIfUnconfigured(app, summary, settings, args, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    internal static bool ShowIfUnconfigured(string app, string summary, IReadOnlyList<FirstRunSetting> settings, string[] args,
        string currentDirectory, string toolDirectory)
    {
        // A configuration already registered was supplied by the host (a test, an embedding tool), not by a file.
        if (args.Length > 0 || FactoryContainer.Resolve<IConfigurationRoot>() != null || HasEnvironmentSettings()) return false;

        var fileName = $"{app}.settings.json";
        var candidates = new[] { Path.Join(currentDirectory, fileName), Path.Join(toolDirectory, fileName) }
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var settingsFile = candidates.FirstOrDefault(File.Exists);

        if (settingsFile == null)
        {
            WriteHeader(app, summary);
            Console.WriteLine("No settings file was found, so nothing was run. Looked for:");
            foreach (var candidate in candidates) Console.WriteLine($"  {candidate}");
            Console.WriteLine();
            WriteHowToConfigure(app, settings, $"Put a {fileName} in one of those places with these settings:");
            EnvironmentWrapper.GetFromFactory().Exit(2);
            return true;
        }

        if (!AllBlank(settingsFile, settings)) return false;

        WriteHeader(app, summary);
        Console.WriteLine("Nothing is configured yet, so nothing was run.");
        Console.WriteLine();
        WriteHowToConfigure(app, settings, $"Fill in these settings in {settingsFile}:");
        EnvironmentWrapper.GetFromFactory().Exit(0);
        return true;
    }

    private static bool HasEnvironmentSettings() =>
        (EnvironmentWrapper.GetFromFactory().GetEnvironmentVariables()?.Keys.Cast<object>() ?? [])
            .Any(k => k.ToString()!.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase));

    // A file the configuration reader cannot parse is left to the normal run, which reports it properly.
    private static bool AllBlank(string settingsFile, IReadOnlyList<FirstRunSetting> settings)
    {
        IConfigurationRoot config;
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(settingsFile)!)
                .AddJsonFile(Path.GetFileName(settingsFile), optional: false)
                .Build();
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        return settings.All(s => string.IsNullOrWhiteSpace(config[s.Key]));
    }

    private static void WriteHeader(string app, string summary)
    {
        Console.WriteLine($"{app} {CommandLineParser.ToolVersion()} -- {summary}.");
        Console.WriteLine();
    }

    private static void WriteHowToConfigure(string app, IReadOnlyList<FirstRunSetting> settings, string lead)
    {
        var listed = settings.Where(s => s.Meaning != null).ToList();
        Console.WriteLine(lead);
        var width = listed.Max(s => s.Key.Length);
        foreach (var setting in listed)
            Console.WriteLine($"  {setting.Key.PadRight(width)}  {setting.Meaning}");
        Console.WriteLine();
        Console.WriteLine($"Or set them as environment variables ({EnvironmentPrefix}{listed[0].Key.Replace(":", "__")}=...),");
        Console.WriteLine("or name your own settings file with --ConfigFile:<path>.");
        Console.WriteLine();
        Console.WriteLine($"{app} --help lists every option. Reference: " +
                          $"https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/reference/{app.ToLowerInvariant()}.md");
    }
}
