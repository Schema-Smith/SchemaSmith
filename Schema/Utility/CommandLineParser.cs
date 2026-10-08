// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Schema.Domain;
using Schema.Isolators;

namespace Schema.Utility;

public static class CommandLineParser
{
    public static List<string> Arguments
    {
        get
        {
            var commandLine = ForceLeadingSpace(EnvironmentWrapper.GetFromFactory().CommandLine);
            var result = new List<string>();
            var pos = 0;
            while (pos < commandLine.Length - 1)
            {
                var nextPos = FindNextUnquotedSpace(commandLine, pos);
                if (nextPos == -1)
                {
                    result.Add(commandLine.Substring(pos).Trim().Unquote());
                    break;
                }
                var arg = commandLine.Substring(pos, nextPos - pos).Trim().Unquote();
                if (arg != string.Empty)
                    result.Add(arg);
                pos = FindNextNonSpace(commandLine, nextPos);
            }

            return result;
        }
    }

    public static Dictionary<string, string> SwitchesAndValues
    {
        get
        {
            var result = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var argument in Arguments.Where(x => x.StartsWith("/") || x.StartsWith("-")))
            {
                // split into max 2 parts to handle values with embedded colon or equals
                var parts = argument.Split(new[] { ':', '=' }, 2);
                result[TrimKeyName(parts[0])] = parts.Length switch
                {
                    1 => string.Empty,
                    2 => parts[1],
                    _ => result[TrimKeyName(parts[0])]
                };
            }
            return result;
        }
    }

    /// <summary>
    /// Config overrides supplied on the command line. Any switch that carries a value —
    /// <c>--Key=value</c> or <c>--Key:value</c> — is an override. The value boundary is the first
    /// '=' if the switch has one, else the first ':'; the key is the text before it (with '__'
    /// translated to the config path separator ':'), the value is the remainder (trimmed of
    /// surrounding quotes/spaces). '__' nesting mirrors the SmithySettings_ environment-variable
    /// grammar, and because '=' wins over ':' a ':'-nested key can still carry a value
    /// (<c>--Target:Server=host</c>). A bare flag (no '=' or ':') is not an override. Reserved
    /// named switches (LogPath / ConfigFile / ConnectionString) also appear here but are inert —
    /// they are consumed via their own switch, never read from configuration.
    /// </summary>
    public static Dictionary<string, string> ConfigOverrides
    {
        get
        {
            var result = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var trimmed in Arguments.Where(x => x.StartsWith("/") || x.StartsWith("-")).Select(TrimKeyName))
            {
                // Value boundary: the first '=' if present, else the first ':'. '=' wins so a
                // ':'-nested key (e.g. Target:Server) can still carry an '='-delimited value.
                var sep = trimmed.IndexOf('=');
                if (sep < 0) sep = trimmed.IndexOf(':');
                if (sep < 0) continue; // a bare flag (no value) is not a config override
                var key = trimmed.Substring(0, sep).Replace("__", ":");
                if (key.Length == 0) continue;
                result[key] = trimmed.Substring(sep + 1).Trim('"', ' ');
            }
            return result;
        }
    }

    public static bool ContainsSwitch(string switchName)
    {
        return SwitchesAndValues.ContainsKey(switchName);
    }

    public static string ValueOfSwitch(string switchName, string defval = "")
    {
        return (ContainsSwitch(switchName) ? SwitchesAndValues[switchName]?.Trim('"', ' ') : defval);
    }

    public static int IntValueOfSwitch(string switchName, int defval = -1)
    {
        if (!int.TryParse(ValueOfSwitch(switchName), out var result))
            result = defval;
        return result;
    }

    /// <summary>
    /// Applies the <c>-Encrypt</c> / <c>-NoEncrypt</c> convenience switch to <paramref name="props"/>,
    /// writing the transport-security connection property appropriate to <paramref name="platform"/>
    /// (SQL Server <c>Encrypt</c>, PostgreSQL <c>SSL Mode</c>, MySQL/MariaDB <c>SslMode</c>). The flag
    /// wins over any value already sourced from ConnectionProperties. No switch leaves
    /// <paramref name="props"/> untouched; specifying both switches is an error.
    /// </summary>
    public static void ApplyTransportSecuritySwitch(Platform platform, Dictionary<string, string> props)
    {
        var on = ContainsSwitch("Encrypt");
        var off = ContainsSwitch("NoEncrypt");
        if (!on && !off) return;
        if (on && off)
            throw new Exception("Specify only one of -Encrypt / -NoEncrypt.");

        var (key, value) = platform.GetBasePlatform() switch
        {
            Platform.PostgreSQL => ("SSL Mode", on ? "Require" : "Disable"),
            Platform.MySQL => ("SslMode", on ? "Required" : "None"),
            _ => ("Encrypt", on ? "True" : "False")
        };
        props[key] = value;
    }

    private static readonly string[] CommonBareFlags =
        { "v", "ver", "version", "?", "h", "help", "Encrypt", "NoEncrypt", "debug", "verbose", "ExitNonZeroOnWarning" };

    /// <summary>
    /// Arguments this run will never read, in command-line order. Two shapes qualify, and both are
    /// silently inert without this check: a token that is not a switch at all — the usual cause is a
    /// switch value written with a space, since the ':'/'=' grammar leaves <c>--report ./out/x</c>
    /// holding no value and strands the path — and a bare switch that is not one of
    /// <paramref name="knownBareFlags"/>, which is how a misspelled <c>--TestConection</c> quietly
    /// becomes a full deployment. A switch that carries a value is always legitimate: any
    /// <c>--Key=value</c> / <c>--Key:value</c> is a configuration override, so no list can know it.
    /// Pass only genuinely valueless flags in <paramref name="knownArguments"/> — a value switch left
    /// out of it (LogPath, ConfigFile, report…) is then correctly reported when given bare. A tool
    /// that takes a positional command (SchemaQuench's <c>SkipKindlingForge</c>) lists it there too.
    /// </summary>
    public static List<string> UnrecognizedArguments(IEnumerable<string> knownArguments)
    {
        var known = new HashSet<string>(CommonBareFlags, StringComparer.InvariantCultureIgnoreCase);
        foreach (var flag in knownArguments ?? Enumerable.Empty<string>())
            known.Add(flag);

        return Arguments.Skip(1)
            .Where(arg => IsSwitch(arg)
                ? CarriesNoValue(arg) && !known.Contains(TrimKeyName(arg))
                : !known.Contains(arg))
            .ToList();
    }

    /// <summary>
    /// Stops the run on an argument the tool will never read. An argument the user typed and the tool did not
    /// understand is a reason to stop, not to warn: <c>--WhatIf</c> misspelled, or a value written with a space, used
    /// to be ignored, and the run went ahead, for real.
    /// </summary>
    public static void RefuseUnrecognizedArguments(IEnumerable<string> knownArguments)
    {
        var unread = UnrecognizedArguments(knownArguments);
        if (unread.Count == 0) return;

        throw new RunFailedException($"Not recognized: {string.Join(" ", unread)}. Nothing was run. Check the spelling "
                                     + "(--help lists the switches); a switch value attaches with ':' or '=', never a space "
                                     + "- e.g. --LogPath:./logs");
    }

    private static bool IsSwitch(string arg) => arg.StartsWith("/") || arg.StartsWith("-");

    private static bool CarriesNoValue(string arg) => arg.IndexOf('=') < 0 && arg.IndexOf(':') < 0;

    public static void HandleCommonSwitches(string app, Action toolSpecificSwitches = null)
    {
        if (ContainsSwitch("v") || ContainsSwitch("ver") || ContainsSwitch("version")) ShowVersionAndExit(app);
        if (ContainsSwitch("?") || ContainsSwitch("h") || ContainsSwitch("help")) ShowHelpAndExit(app, toolSpecificSwitches);
    }

    private static string ForceLeadingSpace(string commandLine)
    {
        if (!commandLine.StartsWith(" "))
            commandLine = " " + commandLine;
        return commandLine;
    }

    private static int FindNextNonSpace(string s, int startPos)
    {
        var curPos = startPos;
        while (s[curPos] == ' ' && curPos < s.Length - 1)
            curPos++;
        return curPos;
    }

    private static int FindNextUnquotedSpace(string s, int startPos)
    {
        var firstQuote = s.IndexOf('"', startPos);
        var nextQuote = s.IndexOf('"', firstQuote + 1);
        var nextSpace = s.IndexOf(' ', startPos);
        if (nextSpace == -1) return nextSpace;
        while (nextSpace > firstQuote && nextSpace < nextQuote)
            nextSpace = FindNextUnquotedSpace(s, nextQuote + 1);

        return nextSpace;
    }

    private static string TrimKeyName(string s)
    {
        return s.TrimStart('/').TrimStart('-');
    }

    internal static void ShowVersionAndExit(string app)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        Console.WriteLine($"{app} - Version: {version}");
        EnvironmentWrapper.GetFromFactory().Exit(0);
    }

    private static void ShowHelpAndExit(string app, Action toolSpecificSwitches = null)
    {
        Console.WriteLine($"{app}.exe [<command>]");
        Console.WriteLine("  --version                        Show the program version");
        Console.WriteLine("  --LogPath:<logpath>              Path to write logs and create backup directories. The default is the executable's directory.");
        Console.WriteLine("  --ConfigFile:<filepath>          Path and file name of the config file. The default is <toolname>.settings.json in the current path.");
        Console.WriteLine("  --ConnectionString:<connstr>     Override the connection string. Bypasses all connection settings in the config file.");
        Console.WriteLine("  --Encrypt | --NoEncrypt          Force transport encryption on/off (SQL Server Encrypt, PostgreSQL SSL Mode, MySQL/MariaDB SslMode). Wins over ConnectionProperties.");
        Console.WriteLine("  --<Key>=<value>                  Override any configuration option (also --<Key>:<value>; nest with '__', e.g. --Source__Server=host). Logged at startup; sensitive values scrubbed.");
        toolSpecificSwitches?.Invoke();
        Console.WriteLine("  --ExitNonZeroOnWarning           Exit 1 instead of 0 when the run logged a warning (also the ExitNonZeroOnWarning setting).");
        Console.WriteLine("  --help                           Show the command line options");
        EnvironmentWrapper.GetFromFactory().Exit(0);
    }
}
