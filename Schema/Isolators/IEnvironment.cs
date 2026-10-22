// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections;

namespace Schema.Isolators;

public interface IEnvironment
{
    void Exit(int exitCode);

    string CommandLine { get; }

    string GetEnvironmentVariable(string variable);
    IDictionary GetEnvironmentVariables();
    string GetFolderPath(Environment.SpecialFolder folder);
}
