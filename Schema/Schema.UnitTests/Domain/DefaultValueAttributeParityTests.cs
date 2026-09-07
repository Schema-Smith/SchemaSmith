// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Schema.Domain;

namespace Schema.UnitTests.Domain;

/// <summary>
/// A domain property that initialises itself to a non-default value MUST also carry a matching
/// <see cref="DefaultValueAttribute"/>, or load→save is not idempotent for packages that omit it.
/// <para><b>The mechanism.</b> <c>JsonHelper</c> serialises with <c>DefaultValueHandling.Ignore</c>, which
/// compares each value against its <em>declared</em> default. A property with a field initialiser but no
/// <c>[DefaultValue]</c> has no declared default to compare against, so the initialised value is always
/// written. A hand-authored package that omits the key gains it the first time any tool loads and saves the
/// file — <c>{"OnOrderMismatch": true}</c> becomes <c>{"Mode":"NEVER","OnOrderMismatch":true}</c>. The
/// defaults materialise at DESERIALISATION, so this is not an editor artifact: any save from any tool churns
/// the file.</para>
/// <para><b>Why a sweep and not a spot fix.</b> Two instances were found by the same consumer within one
/// branch — <c>RebuildPolicy.Mode</c>, then <c>PostgreSqlPolicy</c>'s Permissive/Command/Roles — which is
/// what a systemic gap looks like rather than a one-off. This test is the guard that keeps the set empty, so
/// the next one cannot be added silently.</para>
/// <para><b>Fix at the attribute, never consumer-side.</b> Collapsing an explicit <c>"NEVER"</c> to null on
/// save is the tempting workaround and it is wrong: <c>RebuildPolicy</c> is whole-object precedence, so a
/// table-level <c>{Mode:"NEVER"}</c> is a deliberate veto of an inherited <c>ALWAYS</c>. Dropping it makes
/// the table silently inherit the policy it was overriding — a real behaviour change traded for a cosmetic
/// one.</para>
/// </summary>
[TestFixture]
public class DefaultValueAttributeParityTests
{
    [Test]
    public void EveryInitialisedDomainProperty_DeclaresAMatchingDefaultValue()
    {
        var offenders = new List<string>();

        foreach (var type in DomainTypes())
        {
            object instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch
            {
                continue; // no usable parameterless ctor — nothing to observe
            }

            if (instance == null) continue;

            foreach (var prop in SerialisableProperties(type))
            {
                object value;
                try
                {
                    value = prop.GetValue(instance);
                }
                catch
                {
                    continue;
                }

                if (!IsInitialisedBeyondTypeDefault(prop.PropertyType, value)) continue;

                var declared = prop.GetCustomAttribute<DefaultValueAttribute>();
                if (declared == null)
                {
                    offenders.Add($"{type.Name}.{prop.Name} initialises to '{value}' but declares no [DefaultValue]");
                }
                else if (!Equals(declared.Value, value))
                {
                    offenders.Add($"{type.Name}.{prop.Name} initialises to '{value}' but [DefaultValue] says '{declared.Value}'");
                }
            }
        }

        Assert.That(offenders, Is.Empty,
            "These domain properties initialise themselves but declare no matching [DefaultValue], so "
            + "DefaultValueHandling.Ignore cannot strip them and every load->save adds the key to packages "
            + "that omitted it:\n  " + string.Join("\n  ", offenders.OrderBy(o => o)));
    }

    // Attributes are compile-time metadata for schema GENERATION, never package content -- their sentinel
    // initialisers (SchemaPropertyAttribute's NaN / -1 meaning "unset") are not serialisation defaults and
    // giving them [DefaultValue] would be meaningless.
    private static IEnumerable<Type> DomainTypes() =>
        typeof(RebuildPolicy).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace != null
                        && t.Namespace.StartsWith("Schema.Domain", StringComparison.Ordinal)
                        && !typeof(Attribute).IsAssignableFrom(t));

    // [JsonIgnore] properties never reach a package file, so they cannot churn one. Template's *Schema
    // properties are the case that matters here: they hold a JSON-array string built at deploy time to feed
    // a script token, and are deliberately excluded from serialisation.
    private static IEnumerable<PropertyInfo> SerialisableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() == null);

    // A collection initialised to empty is NOT this defect: JsonHelper's contract resolver decides whether an
    // empty collection is written, and an empty list carries no authored value that could be silently added
    // to a package's semantics. The defect being guarded is a SCALAR default materialising as a key.
    private static bool IsInitialisedBeyondTypeDefault(Type propertyType, object value)
    {
        if (value == null) return false;
        if (value is string s) return s.Length > 0;
        if (value is IEnumerable && value is not string) return false;

        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (!underlying.IsValueType) return false; // a non-null reference default is not a serialisation default

        var typeDefault = Activator.CreateInstance(underlying);
        return !Equals(value, typeDefault);
    }
}
