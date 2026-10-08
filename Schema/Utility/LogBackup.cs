// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using Schema.Isolators;

namespace Schema.Utility;

public static class LogBackup
{
    public static void BackupLogsAndExit(string appName, int exitCode = 0)
    {
        var backupDir = "UNKNOWN";
        try
        {
            var directory = DirectoryWrapper.GetFromFactory();
            var file = FileWrapper.GetFromFactory();
            var ext = 0;

            var cwd = ConfigHelper.ResolveLogPath();

            // Two runs from one install race here: CreateDirectory is idempotent, so both settle on the
            // same App.0001 and the loser's Copy hits a destination that already exists. That threw into
            // the catch below and exited 4 -- a run that had just printed PASS reported as a failure, which
            // CI parallelism makes routine. On a collision, take the next index and try again. A directory an
            // earlier run left behind is not a collision and does not use up an attempt: counting those meant that
            // once fifty backups existed, no run archived its logs again.
            const int maxCollisions = 50;
            const int maxIndex = 9999;
            var collisions = 0;
            var copied = false;
            while (!copied && collisions < maxCollisions && ext < maxIndex)
            {
                backupDir = Path.Join(cwd, $"{appName}.{$"{++ext}".PadLeft(4, '0')}");
                if (directory.Exists(backupDir)) continue;

                directory.CreateDirectory(backupDir);
                try
                {
                    foreach (var logFile in directory.GetFiles(cwd, $"{appName} - *.log", SearchOption.TopDirectoryOnly))
                        file.Copy(logFile, Path.Join(backupDir, Path.GetFileName(logFile)));

                    // Deployment summary report (#243, E4e): archive the always-on Summary.json/.md
                    // alongside the run's logs. Harmless on tools that never write them (SchemaTongs,
                    // DataTongs) -- GetFiles simply matches nothing.
                    foreach (var summaryFile in directory.GetFiles(cwd, $"{appName} - Summary.*", SearchOption.TopDirectoryOnly))
                        file.Copy(summaryFile, Path.Join(backupDir, Path.GetFileName(summaryFile)));

                    copied = true;
                }
                catch (IOException)
                {
                    // Another run got here first. Leave its files alone and try the next directory.
                    collisions++;
                }
            }

            // Archiving logs is a convenience. Failing to do it must not overwrite the outcome the run
            // actually reached -- that is what turned a passing run into an exit-4 failure.
            if (!copied)
            {
                Console.WriteLine("");
                Console.WriteLine($"UNABLE TO BACKUP LOG FILES TO {backupDir} -- the run's own result stands.");
            }

            EnvironmentWrapper.GetFromFactory().Exit(exitCode);
        }
        catch (Exception e)
        {
            Console.WriteLine("");
            Console.WriteLine($"UNABLE TO BACKUP LOG FILES TO {backupDir}");
            Console.WriteLine(e);
            EnvironmentWrapper.GetFromFactory().Exit(4);
        }
    }

    /// <summary>
    /// Ends a run that failed for a reason the user can fix: the message on the console, the detail in the error log,
    /// and exit 2. An unhandled exception, by contrast, is a defect and exits 3.
    /// </summary>
    public static void FailedRunExit(string appName, RunFailedException e)
    {
        LogFactory.GetLogger("ProgressLog").Error($"FAILED: {e.Message}");
        LogFactory.GetLogger("ErrorLog").Error(e.Message, e);
        BackupLogsAndExit(appName, 2);
    }

    public static void UnhandledExceptionLogger(string appName, UnhandledExceptionEventArgs e)
    {
        LogFactory.GetLogger("ProgressLog").Error($"EXCEPTION - See the error log:\r\n{e.ExceptionObject}");
        LogFactory.GetLogger("ErrorLog").Error(e.ExceptionObject);

        BackupLogsAndExit(appName, 3);
    }
}
