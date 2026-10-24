// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Microsoft.Extensions.Configuration;
using Schema.Configuration;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench;

/// <summary>
/// The debug SQL files are opt-in (#576): without <c>WriteDebugSql</c> none is written, and a failure says how to get one.
/// </summary>
internal static class DebugSql
{
    internal static bool Enabled =>
        CommandLineParser.ContainsSwitch(SettingsKeys.WriteDebugSql) ||
        string.Equals(FactoryContainer.Resolve<IConfigurationRoot>()?[SettingsKeys.WriteDebugSql], "true",
            StringComparison.OrdinalIgnoreCase);

    internal static string ArtifactDirectory
    {
        get
        {
            var configured = FactoryContainer.Resolve<IConfigurationRoot>()?[SettingsKeys.ArtifactPath];
            return string.IsNullOrWhiteSpace(configured) ? System.IO.Directory.GetCurrentDirectory() : configured;
        }
    }

    internal static string HowToGetIt(string directory) =>
        $"Rerun with --WriteDebugSql to write the resolved SQL of this step to {directory}.";
}
