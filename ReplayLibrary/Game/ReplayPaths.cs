using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace ReplayLibrary.Game;

/// <summary>Replay directory from the client user path, with the standard documents folder as fallback.</summary>
internal static unsafe class ReplayPaths
{
    internal const string LibraryFolderName = "replaylibrary";
    internal const string DeletedFolderName = "deleted";
    internal const string LegacyLibraryFolderName = "library";
    internal const string LegacyDeletedFolderName = "library-deleted";
    internal const string LegacyArchiveFolderName = "library-archive";
    internal const string AutorenamedFolderName = "autorenamed";

    internal static string ReplayDirectory()
    {
        var userPath = ReadUserPath();
        if (!string.IsNullOrWhiteSpace(userPath))
            return Path.Combine(userPath, "replay");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "FINAL FANTASY XIV - A Realm Reborn",
            "replay");
    }

    internal static string UserDirectory() =>
        Path.GetDirectoryName(ReplayDirectory()) ?? ReplayDirectory();

    internal static string LibraryDirectory() =>
        Path.Combine(ReplayDirectory(), LibraryFolderName);

    internal static string LegacyRootLibraryDirectory() =>
        Path.Combine(UserDirectory(), LibraryFolderName);

    internal static string DeletedDirectory() =>
        Path.Combine(ReplayDirectory(), DeletedFolderName);

    internal static string LegacyLibraryDirectory() =>
        Path.Combine(ReplayDirectory(), LegacyLibraryFolderName);

    internal static string LegacyDeletedDirectory() =>
        Path.Combine(ReplayDirectory(), LegacyDeletedFolderName);

    internal static string LegacyArchiveDirectory() =>
        Path.Combine(ReplayDirectory(), LegacyArchiveFolderName);

    internal static string AutorenamedDirectory() =>
        Path.Combine(ReplayDirectory(), AutorenamedFolderName);

    internal const string LibraryBackupPrefix = "replay-library-backup-";

    internal static string BackupDirectory(DateTime localTime) =>
        Path.Combine(
            UserDirectory(),
            "replay-backup-" + localTime.ToString("yyyyMMdd-HHmmss"));

    internal static string LibraryBackupDirectory(DateTime localTime) =>
        Path.Combine(
            UserDirectory(),
            LibraryBackupPrefix + localTime.ToString("yyyyMMdd-HHmmss"));

    private static string? ReadUserPath()
    {
        try
        {
            var framework = Framework.Instance();
            if (framework == null)
                return null;

            var path = framework->UserPathString;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to read the game user path");
            return null;
        }
    }
}
