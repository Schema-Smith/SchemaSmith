// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;

namespace Schema.Utility;

/// <summary>
/// A line a tool prints itself rather than through its logs. Under LogFormat json standard output carries nothing but
/// JSON log lines, so the line goes to standard error instead.
/// </summary>
public static class ConsoleOutput
{
    public static void Line(string text = "")
    {
        if (ConfigHelper.JsonLogs) Console.Error.WriteLine(text);
        else Console.WriteLine(text);
    }
}
