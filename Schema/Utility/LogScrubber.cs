// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using System.Text.RegularExpressions;

namespace Schema.Utility;

/// <summary>
/// Pure, stateless scrubbing of sensitive values before they reach any log surface (settings echo,
/// token logging, future CLI-argument logging, resolved-script artifacts). Behaviour is driven by a
/// baked-in default sensitive-name pattern set plus per-tool <see cref="LogHygieneOptions"/>.
/// </summary>
public static class LogScrubber
{
    public const string Mask = "***";

    // Contains-match substrings (case-insensitive). Stored without glob asterisks; the asterisk
    // syntax users write in ScrubPatterns ("*Salt*") is purely cosmetic and normalised away.
    private static readonly string[] DefaultSensitivePatterns =
        ["Password", "Pwd", "Secret", "ApiKey", "Token", "ConnectionString", "Credential"];

    // A password, and the key or keyword that introduces it. The keys: a connection string's Password= or Pwd=, the
    // same word in T-SQL (PASSWORD = N'...'), a credential's SECRET =, and any key ending in one of them (OLD_PASSWORD =,
    // PGPASSWORD=, MYSQL_PWD=). A key must end at the '=', so "MyPasswordHint=" is not one. The keywords take no '=' and
    // match only when a quote follows, since IDENTIFIED BY RANDOM PASSWORD and PASSWORD NULL carry no secret:
    // PostgreSQL's PASSWORD '...', and the MySQL family's IDENTIFIED [WITH plugin] BY [PASSWORD] '...' and REPLACE '...'.
    // The value forms that can hold a ';' come first: a JSON-escaped \"...\", "..." and '...' (optionally N'...') with
    // a doubled quote as an escape, {...} with }} as one, and a quoted value wrapped in a call, PASSWORD('...'). A quote
    // that never closes runs to the end of the text: a truncated value is still all secret, so failing open would log
    // its tail. An unquoted value stops at ';' or a line break, so a stack trace after it survives -- or, after a key
    // that ends a longer word (a shell or token assignment), at whitespace too, so the next assignment survives. Only
    // spaces and tabs may surround '=', so an empty value at a line end masks nothing more.
    private static readonly Regex ConnectionStringSecret =
        new(@"(?<key>(?<prefixed>(?<=\w))?(?:password|pwd|secret)[ \t]*="
            + @"|\bpassword[ \t]+(?=N?['""])"
            + @"|\bidentified(?:[ \t]+with[ \t]+\w+)?[ \t]+by(?:[ \t]+password)?[ \t]+(?=['""])"
            + @"|\breplace[ \t]+(?=['""]))[ \t]*"
            + @"(?:\\""(?:(?!\\"")[\s\S])*(?:\\"")?"
            + @"|""(?:[^""]|"""")*""?"
            + @"|N?'(?:[^']|'')*'?"
            + @"|\{(?:[^}]|\}\})*\}?"
            + @"|\w+\([ \t]*N?'(?:[^']|'')*'?[ \t]*\)?"
            + @"|(?(prefixed)[^\s;]*|[^;\r\n]*))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // A credential in a URL's userinfo component -- scheme://user:password@host. Not covered by the
    // keyword pattern above, so "?password=x" in a value masked while "user:pass@host" earlier in the
    // SAME value did not. Written against the general URL shape rather than one connection-string
    // dialect, because this class of string turns up as a webhook or an API endpoint at least as often
    // as a database URI.
    //
    // Four things the shape has to get right, each of which is a test:
    //   * the (?=@) lookahead is what separates "user:password@host" from "host:8080/path" -- without
    //     it every port number in every logged URL is masked;
    //   * [^\s@/?#]* cannot cross a path separator, so "…/a:b/mail@example.com" is left alone rather
    //     than having everything between the colon and a much later @ destroyed;
    //   * the username is preserved. It is not a secret, and it is most of what makes a scrubbed log
    //     still diagnosable.
    //   * the scheme is tried only where a run of scheme characters starts, and is consumed atomically. Tried
    //     from every position, a long word with no "://" cost quadratic time: a 5 MB line never finished.
    private static readonly Regex UrlUserInfoSecret =
        new(@"(?<prefix>(?<![a-z0-9+.\-])(?>[a-z][a-z0-9+.\-]*)://[^\s:/?#@]+:)[^\s@/?#]*(?=@)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);


    public static bool ShouldScrubName(string name, LogHygieneOptions options)
    {
        if (string.IsNullOrEmpty(name)) return false;
        options ??= LogHygieneOptions.Default;

        if (options.AllowTokens.Contains(name)) return false;
        if (options.ScrubTokens.Contains(name)) return true;

        return DefaultSensitivePatterns
            .Concat(options.ScrubPatterns.Select(p => p.Trim('*')).Where(p => p.Length > 0))
            .Any(name.ContainsIgnoringCase);
    }

    public static string ScrubConnectionStringSubfields(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var masked = ConnectionStringSecret.Replace(value, m => $"{m.Groups["key"].Value}{Mask}");
        return UrlUserInfoSecret.Replace(masked, m => $"{m.Groups["prefix"].Value}{Mask}");
    }

    public static string ScrubTokenValue(string name, string value, LogHygieneOptions options)
    {
        options ??= LogHygieneOptions.Default;
        return ShouldScrubName(name, options) ? Mask : ScrubConnectionStringSubfields(value);
    }
}
