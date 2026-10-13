// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections;

namespace Schema.Isolators;

public class EnvironmentWrapper : IEnvironment
{
    public void Exit(int exitCode)
    {
        Environment.Exit(exitCode);
    }

    public string CommandLine { get; } = Environment.CommandLine;

    public string GetEnvironmentVariable(string variable)
    {
        return Environment.GetEnvironmentVariable(variable);
    }

    public IDictionary GetEnvironmentVariables()
    {
        return Environment.GetEnvironmentVariables();
    }

    public string GetFolderPath(Environment.SpecialFolder folder)
    {
        return Environment.GetFolderPath(folder);
    }

    public static IEnvironment GetFromFactory()
    {
        return FactoryContainer.ResolveOrCreate<IEnvironment, EnvironmentWrapper>();
    }
}
