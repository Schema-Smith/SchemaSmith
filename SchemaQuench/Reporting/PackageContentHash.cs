// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Schema.Isolators;

namespace SchemaQuench.Reporting;

public sealed record PackageIdentity(string ContentHash, int FileCount);

/// <summary>
/// The package's identity is its content: two machines, a Windows and a Linux checkout, and a folder and its zip all hash
/// the same package to the same value. Files are taken in ordinal order of their '/'-separated relative path; each
/// contributes its path, a zero byte, its bytes with CRLF written as LF, and a zero byte. Anything under a directory whose
/// name starts with '.', '.sqlerror' files and an in-package log directory are files the tools or editors write, not
/// package content, so they are left out.
/// </summary>
public static class PackageContentHash
{
    public static PackageIdentity ForFolder(string productRoot, string logDirectory = null)
    {
        try
        {
            var directory = DirectoryWrapper.GetFromFactory();
            if (string.IsNullOrEmpty(productRoot) || !directory.Exists(productRoot)) return null;
            var root = Path.GetFullPath(productRoot);
            var logs = string.IsNullOrEmpty(logDirectory) ? null : RelativeIfInside(root, Path.GetFullPath(logDirectory));
            var file = FileWrapper.GetFromFactory();
            var files = directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => (Relative: Path.GetRelativePath(root, path).Replace('\\', '/'), Full: path))
                .Where(f => Included(f.Relative) && (logs == null || !f.Relative.StartsWith(logs + "/", PathComparison)))
                .Select(f => (f.Relative, (Func<Stream>)(() => file.OpenRead(f.Full))));
            return Hash(files);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PackageIdentity ForZip(IEnumerable<IZipEntry> entries)
    {
        try
        {
            var files = entries
                .Select(e => (Relative: e.FullName.Replace('\\', '/').TrimStart('/'), Entry: e))
                .Where(f => !f.Relative.EndsWith('/') && Included(f.Relative))
                .Select(f => (f.Relative, (Func<Stream>)f.Entry.Open));
            return Hash(files);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string RelativeIfInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative.TrimEnd('/');
    }

    private static bool Included(string relativePath) =>
        !relativePath.Split('/').Any(segment => segment.StartsWith('.'))
        && !relativePath.EndsWith(".sqlerror", StringComparison.OrdinalIgnoreCase);

    private static PackageIdentity Hash(IEnumerable<(string Relative, Func<Stream> Open)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        foreach (var (relative, open) in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using (var stream = open()) AppendWithLfLineEnds(hash, stream);
            hash.AppendData([0]);
            count++;
        }
        return new PackageIdentity("sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()), count);
    }

    // Streams the file so a package's large data files never sit in memory whole. A CR is held back until the next byte
    // shows whether it ends a line, which may be in the next read.
    private static void AppendWithLfLineEnds(IncrementalHash hash, Stream stream)
    {
        const byte cr = 13, lf = 10;
        var input = new byte[81920];
        var output = new byte[input.Length + 1];
        var heldCr = false;
        int read;
        while ((read = stream.Read(input, 0, input.Length)) > 0)
        {
            var length = 0;
            for (var i = 0; i < read; i++)
            {
                var b = input[i];
                if (heldCr && b != lf) output[length++] = cr;
                heldCr = b == cr;
                if (!heldCr) output[length++] = b;
            }
            hash.AppendData(output, 0, length);
        }
        if (heldCr) hash.AppendData([cr]);
    }
}
