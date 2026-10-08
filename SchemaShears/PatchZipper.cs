// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.IO.Compression;
using Schema.Isolators;

namespace SchemaShears;

public static class PatchZipper
{
    public static string ZipPathFor(string outputPath) =>
        outputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";

    public static void RequireNoExistingZip(string outputPath)
    {
        var zipPath = ZipPathFor(outputPath);
        if (FileWrapper.GetFromFactory().Exists(zipPath))
            throw new PatchBuildException($"Zip already exists: '{zipPath}'.");
    }

    public static string Zip(string outputPath)
    {
        RequireNoExistingZip(outputPath);
        var zipPath = ZipPathFor(outputPath);

        ZipFile.CreateFromDirectory(outputPath, zipPath);
        return zipPath;
    }
}
