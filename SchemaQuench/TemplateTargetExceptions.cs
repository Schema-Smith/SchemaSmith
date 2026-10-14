// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Utility;

namespace SchemaQuench;

/// <summary>
/// Thrown when a <c>Target.TemplateTargets</c> configuration violates one of the
/// fail-fast rules (design §5). The message is the user-facing diagnostic.
/// </summary>
public class TemplateTargetValidationException : RunFailedException
{
    public TemplateTargetValidationException(string message) : base(message) { }
}

/// <summary>
/// Thrown when <c>TemplateTargets</c> declarative provisioning fails at runtime — typically
/// a permission denial on <c>CREATE DATABASE</c> / <c>CREATE SCHEMA</c>, or an admin-connection
/// failure when retargeting to <c>master</c> / <c>postgres</c> / <c>information_schema</c>
/// (design §6, #257). The message carries an actionable diagnostic naming the target object
/// + the missing privilege; the underlying engine exception is preserved as
/// <see cref="Exception.InnerException"/> so the root cause stays attached.
/// </summary>
public class TemplateTargetProvisioningException : Exception
{
    public TemplateTargetProvisioningException(string message, Exception innerException)
        : base(message, innerException) { }
}
