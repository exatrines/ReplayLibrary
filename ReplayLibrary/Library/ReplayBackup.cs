namespace ReplayLibrary.Library;

/// <summary>One-time copy of the replay tree, then a library copy every seven days.</summary>
internal static class ReplayBackup
{
    private const int LibraryRetrySeconds = 60 * 60;

    private static int _state;
    private static int _failed;
    private static int _needsSave;
    private static long _libraryRetryUnix;
    private static string _status = string.Empty;

    internal static bool IsRunning => Volatile.Read(ref _state) == 1;

    internal static string Status => _status;

    internal static void StartIfNeeded()
    {
        if (C.ReplayBackupCompleted || IsRunning || Volatile.Read(ref _failed) != 0)
            return;

        if (!Directory.Exists(ReplayPaths.ReplayDirectory()))
            return;

        Start();
    }

    internal static void StartLibraryIfDue()
    {
        if (!C.ReplayBackupCompleted || IsRunning)
            return;

        var library = ReplayPaths.LibraryDirectory();
        if (!Directory.Exists(library))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < Volatile.Read(ref _libraryRetryUnix))
            return;

        var elapsed = now - C.LibraryBackupUnixSeconds;
        if (C.LibraryBackupUnixSeconds != 0 && elapsed < 7 * 24 * 60 * 60)
            return;

        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return;

        var destination = ReplayPaths.LibraryBackupDirectory(DateTime.Now);
        _status = "Backing up library...";
        Task.Run(() => RunLibrary(library, destination));
    }

    internal static bool CanMutateReplay()
    {
        if (IsRunning)
            return false;

        if (C.ReplayBackupCompleted)
            return true;

        if (!Directory.Exists(ReplayPaths.ReplayDirectory()))
            return true;

        if (Volatile.Read(ref _failed) != 0)
            return false;

        if (!IsRunning)
            Start();

        return false;
    }

    internal static string? MutationBlock()
    {
        if (CanMutateReplay())
            return null;

        return string.IsNullOrEmpty(Status) ? "Replay backup is still running." : Status;
    }

    internal static void FlushSave()
    {
        if (Interlocked.Exchange(ref _needsSave, 0) == 1)
            C.Save();
    }

    private static void Start()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return;

        var source = ReplayPaths.ReplayDirectory();
        var destination = ReplayPaths.BackupDirectory(DateTime.Now);
        _status = "Backing up replay...";
        Task.Run(() => Run(source, destination));
    }

    private static void Run(string source, string destination)
    {
        try
        {
            if (!Directory.Exists(source))
            {
                Finish(success: false, "Replay folder was missing", permanentFailure: true);
                return;
            }

            var bytes = DirectorySize(source);
            var root = Path.GetPathRoot(destination);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < bytes)
                {
                    Finish(success: false, "Not enough free disk space to back up replay", permanentFailure: true);
                    return;
                }
            }

            CopyTree(source, destination);
            C.ReplayBackupCompleted = true;
            Volatile.Write(ref _needsSave, 1);
            Finish(success: true, "Replay backup saved", permanentFailure: true);
            PluginServices.Log.Information("Replay backup saved to {Path}", destination);
        }
        catch (Exception e)
        {
            TryDelete(destination);
            PluginServices.Log.Error(e, "Replay backup failed");
            Finish(success: false, "Replay backup failed", permanentFailure: true);
        }
    }

    private static void RunLibrary(string source, string destination)
    {
        try
        {
            if (!Directory.Exists(source))
            {
                Finish(success: false, "Library folder was missing", permanentFailure: false);
                return;
            }

            var bytes = DirectorySize(source);
            var root = Path.GetPathRoot(destination);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < bytes)
                {
                    Finish(success: false, "Not enough free disk space to back up the library", permanentFailure: false);
                    return;
                }
            }

            CopyTree(source, destination);
            PruneLibraryBackups();
            C.LibraryBackupUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Volatile.Write(ref _needsSave, 1);
            Finish(success: true, "Library backup saved", permanentFailure: false);
            PluginServices.Log.Information("Library backup saved to {Path}", destination);
        }
        catch (Exception e)
        {
            TryDelete(destination);
            PluginServices.Log.Error(e, "Library backup failed");
            Finish(success: false, "Library backup failed", permanentFailure: false);
        }
    }

    private static void PruneLibraryBackups()
    {
        var parent = Path.GetDirectoryName(ReplayPaths.ReplayDirectory());
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return;

        var extras = Directory.EnumerateDirectories(parent, ReplayPaths.LibraryBackupPrefix + "*")
            .OrderByDescending(path => path)
            .Skip(3);
        foreach (var extra in extras)
            TryDelete(extra);
    }

    private static void Finish(bool success, string status, bool permanentFailure)
    {
        _status = status;
        if (!success && permanentFailure)
            Volatile.Write(ref _failed, 1);
        if (!success && !permanentFailure)
            Volatile.Write(ref _libraryRetryUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + LibraryRetrySeconds);
        Volatile.Write(ref _state, 0);
    }

    private static long DirectorySize(string source)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
        }

        return total;
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void TryDelete(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to remove an incomplete replay backup");
        }
    }
}
