// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;

namespace Schema.Utility;

/// <summary>
/// The run this process is doing: one id shared by every log line, the deployment summary and the checkpoints the run
/// writes. Each tool's Main starts a new one, so a test process that runs Main many times gets one id per run.
/// </summary>
public static class RunContext
{
    public static Guid Id { get; private set; } = Guid.NewGuid();

    public static string Tool { get; private set; } = "";

    public static void Start(string tool)
    {
        Id = Guid.NewGuid();
        Tool = tool ?? "";
    }
}
