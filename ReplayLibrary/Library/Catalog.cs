using Lumina.Excel.Sheets;
using ReplayLibrary.Recording;

namespace ReplayLibrary.Library;

/// <summary>Lists the library, files loose replays, and applies delete and trim.</summary>
internal static partial class Catalog
{
    internal readonly record struct Entry(
        string ContentTypeName,
        uint ContentTypeId,
        uint ContentId,
        string FolderName,
        string Path,
        uint Timestamp,
        string Version,
        uint SourceBuild,
        ulong Jobs,
        byte Viewpoint,
        bool Anonymized,
        bool Cleared,
        bool Protected);

    private static readonly object FileGate = new();
    private static readonly Dictionary<uint, (uint Type, uint Patch)?> PlacementCache = new();
    private static long _nextMaintainUnix;
    private static int _maintainBusy;
    private static int _maintainAgain;
    private static MaintainScan? _maintainScan;

    private sealed class MaintainScan
    {
        internal required List<StoredReplay> Stored { get; init; }
        internal required List<DeletedFile> Deleted { get; init; }
    }

    private readonly record struct DeletedFile(string Path, DateTime Arrived, long Length);

    internal static IReadOnlyList<Entry> List(uint preferredBuild)
    {
        var typeNames = new Dictionary<uint, string>();
        var entries = new List<Entry>();
        AddLibrary(ReplayPaths.LibraryDirectory(), entries, typeNames);
        Disambiguate(entries);
        return MarkProtected(VisibleCopies(entries, preferredBuild));
    }

    private static void AddLibrary(string root, List<Entry> entries, Dictionary<uint, string> typeNames)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            if (IsLegacyPart(file) || IsSplitFile(file))
                continue;

            var directoryName = Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty;
            entries.Add(Describe(file, directoryName, typeNames));
        }
    }

    private static Entry Describe(string file, string directoryName, Dictionary<uint, string> typeNames)
    {
        uint timestamp = 0;
        ushort contentId = 0;
        uint contentType = 0;
        var version = string.Empty;
        ulong jobs = 0;
        byte viewpoint = 0;
        var cleared = false;
        if (ReplayFile.TryRead(file, out var header))
        {
            timestamp = header.Timestamp;
            contentId = header.ContentFinderConditionId;
            version = header.GameBuildRevision == 0 ? string.Empty : header.GameBuildRevision.ToString();
            jobs = header.Jobs;
            viewpoint = header.Viewpoint;
            cleared = header.Cleared;
        }

        var mark = ReadFileMark(file);
        var contentName = string.Empty;
        if (contentId != 0)
        {
            var lookedUp = Lookup(contentId);
            contentType = lookedUp.ContentType;
            contentName = lookedUp.Name ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(contentName))
            contentName = mark.Content;
        if (string.IsNullOrWhiteSpace(contentName) && !uint.TryParse(directoryName, out _))
            contentName = directoryName;
        if (string.IsNullOrWhiteSpace(contentName))
            contentName = "content";

        return new Entry(
            ContentTypeName(contentType, typeNames),
            contentType,
            contentId,
            contentName,
            file,
            timestamp,
            version,
            mark.SourceBuild,
            jobs,
            viewpoint,
            mark.Anonymized,
            cleared,
            false);
    }

    private static bool IsSplitFile(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var mark = stem.LastIndexOf("_s", StringComparison.Ordinal);
        if (mark <= 0 || !IsDigits(stem[(mark + 2)..]))
            return false;

        var version = stem.LastIndexOf("_v", StringComparison.Ordinal);
        return version >= 0 && version < mark;
    }

    private static void RemoveSplitParts(string sourcePath)
    {
        if (IsSplitFile(sourcePath))
            return;

        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
            return;

        var prefix = Path.GetFileNameWithoutExtension(sourcePath) + "_s";
        foreach (var file in Directory.EnumerateFiles(directory, "*.dat"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !IsSplitFile(file))
                continue;

            try
            {
                File.Delete(file);
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to remove split replay {Path}", file);
            }
        }
    }

    private static void Disambiguate(List<Entry> entries)
    {
        var idsByName = new Dictionary<string, HashSet<uint>>();
        foreach (var entry in entries)
        {
            if (!idsByName.TryGetValue(entry.FolderName, out var ids))
            {
                ids = [];
                idsByName[entry.FolderName] = ids;
            }

            ids.Add(entry.ContentId);
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.ContentId == 0 || idsByName[entry.FolderName].Count < 2)
                continue;

            entries[index] = entry with { FolderName = $"{entry.FolderName}_{entry.ContentId}" };
        }
    }

    private static List<Entry> VisibleCopies(List<Entry> entries, uint preferredBuild)
    {
        var preferred = preferredBuild.ToString();
        var result = new List<Entry>();
        foreach (var group in entries.GroupBy(Key))
        {
            if (group.First().Timestamp == 0)
            {
                result.AddRange(group);
                continue;
            }

            var current = group.Where(entry => entry.Version == preferred).ToList();
            var shown = current.Count > 0 ? current : group.ToList();
            var anonymized = shown.Where(entry => entry.Anonymized).ToList();
            result.AddRange(anonymized.Count > 0 ? anonymized : shown);
        }

        return result;
    }

    private static string Key(Entry entry) =>
        entry.ContentId != 0
            ? $"{entry.ContentId}:{entry.Timestamp}"
            : $"{entry.FolderName}:{entry.Timestamp}";

    private static List<Entry> MarkProtected(List<Entry> entries)
    {
        var kept = KeptPaths();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            entries[index] = entry with
            {
                Protected = kept.Contains(Path.GetFullPath(entry.Path)),
            };
        }

        return entries;
    }

    private static string ContentTypeName(uint contentType, Dictionary<uint, string> names)
    {
        if (contentType == 0)
            return "Other";
        if (names.TryGetValue(contentType, out var cached))
            return cached;

        var row = PluginServices.DataManager.GetExcelSheet<ContentType>().GetRowOrDefault(contentType);
        var name = ContentTypeLabel(contentType, row?.Name.ToString());
        if (string.IsNullOrWhiteSpace(name))
            name = contentType.ToString();
        names[contentType] = name;
        return name;
    }

    internal static string ContentTypeLabel(uint contentTypeId, string? sheetName)
    {
        if (contentTypeId != 40)
            return sheetName ?? string.Empty;

        return LibraryText.Get("content.crucible");
    }

    private readonly record struct LookedUpContent(string? Name, uint ContentType);

    private static LookedUpContent Lookup(ushort contentId)
    {
        var row = PluginServices.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(contentId);
        if (row == null)
            return new LookedUpContent(null, 0);

        return new LookedUpContent(row.Value.Name.ToString(), row.Value.ContentType.RowId);
    }

    internal static bool IsInLibrary(string path) =>
        IsInside(path, ReplayPaths.LibraryDirectory());

    private static bool IsInside(string path, string root)
    {
        var folder = Path.GetFullPath(root);
        var full = Path.GetFullPath(path);
        return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindOriginal(uint sourceBuild, uint timestamp, uint contentId) =>
        FindOriginalIn(ReplayPaths.LibraryDirectory(), sourceBuild, timestamp, contentId)
        ?? FindOriginalIn(ReplayPaths.DeletedDirectory(), sourceBuild, timestamp, contentId);

    private static string? FindOriginalIn(string folder, uint sourceBuild, uint timestamp, uint contentId)
    {
        if (!Directory.Exists(folder))
            return null;

        foreach (var file in Directory.EnumerateFiles(folder, "*.dat", SearchOption.AllDirectories))
        {
            if (!ReplayFile.TryRead(file, out var header))
                continue;
            if (header.GameBuildRevision != sourceBuild || header.Timestamp != timestamp)
                continue;
            if (contentId != 0 && header.ContentFinderConditionId != contentId)
                continue;
            return file;
        }

        return null;
    }

    internal static bool LibraryHasSame(string path) =>
        ReplayFile.TryRead(path, out var header) && FindIdentical(path, header) != null;

    internal static bool MigrateLegacy()
    {
        if (!ReplayBackup.CanMutateReplay())
            return false;

        var changed = false;
        changed |= Relocate(ReplayPaths.LegacyDeletedDirectory(), ReplayPaths.DeletedDirectory());
        changed |= Relocate(ReplayPaths.LegacyLibraryDirectory(), ReplayPaths.LibraryDirectory());
        changed |= Relocate(ReplayPaths.LegacyArchiveDirectory(), ReplayPaths.LibraryDirectory());
        changed |= Relocate(ReplayPaths.LegacyRootLibraryDirectory(), ReplayPaths.LibraryDirectory());
        return changed;
    }

    internal static int FileLoose()
    {
        if (!ReplayBackup.CanMutateReplay() || RecordingService.IsRecording())
            return 0;

        MigrateLegacy();
        MigrateLedger();
        var replay = ReplayPaths.ReplayDirectory();
        if (!Directory.Exists(replay))
            return 0;

        Directory.CreateDirectory(ReplayPaths.LibraryDirectory());

        var stored = StoredKeys();
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(replay, "*.dat", SearchOption.AllDirectories).ToList())
        {
            if (SkipLoose(file) || !ReplayFile.TryRead(file, out var header))
                continue;

            var key = (header.GameBuildRevision, header.Timestamp, header.Length);
            if (stored.TryGetValue(key, out var existing) && existing.Exists(path => SameBytes(file, path)))
            {
                TryDeleteSource(file);
                continue;
            }

            var destination = NextFreePath(ReplayPaths.LibraryDirectory(), FiledStem(header, file));
            try
            {
                if (!PlaceInLibrary(file, destination))
                    continue;
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to file replay {Path}", file);
                continue;
            }

            if (!stored.TryGetValue(key, out existing))
            {
                existing = [];
                stored[key] = existing;
            }

            existing.Add(destination);
            moved++;
        }

        return moved;
    }

    private static bool SkipLoose(string path) =>
        ReplayFile.IsLoadFile(path)
        || IsLegacyPart(path)
        || IsSplitFile(path)
        || IsInLibrary(path)
        || IsInside(path, ReplayPaths.DeletedDirectory())
        || IsInside(path, ReplayPaths.LegacyDeletedDirectory());

    private static string FiledStem(ReplayFile.Header header, string path)
    {
        var mark = ReadFileMark(path);
        var content = header.ContentFinderConditionId != 0 ? Lookup(header.ContentFinderConditionId).Name : null;
        if (string.IsNullOrWhiteSpace(content))
            content = mark.Content;
        if (string.IsNullOrWhiteSpace(content))
            content = header.ContentFinderConditionId != 0 ? header.ContentFinderConditionId.ToString() : "content";

        return FileStem(header.Timestamp, content, header.GameBuildRevision, mark.SourceBuild, mark.Anonymized);
    }

    private static Dictionary<(uint Build, uint Timestamp, long Length), List<string>> StoredKeys()
    {
        var stored = new Dictionary<(uint, uint, long), List<string>>();
        foreach (var file in ReadStored(ReplayPaths.LibraryDirectory()))
        {
            var key = (file.Header.GameBuildRevision, file.Header.Timestamp, file.Header.Length);
            if (!stored.TryGetValue(key, out var paths))
            {
                paths = [];
                stored[key] = paths;
            }

            paths.Add(file.Path);
        }

        return stored;
    }

    private static bool Relocate(string source, string destination)
    {
        if (!Directory.Exists(source))
            return false;

        var moved = false;
        foreach (var file in Directory.EnumerateFiles(source, "*.dat", SearchOption.AllDirectories).ToList())
        {
            if (ReplayFile.IsLoadFile(file) || IsSplitFile(file))
                continue;

            Directory.CreateDirectory(destination);
            var target = NextFreePath(destination, Path.GetFileNameWithoutExtension(file));
            try
            {
                if (IsInLibrary(target))
                {
                    if (!PlaceInLibrary(file, target))
                        continue;
                }
                else
                {
                    lock (FileGate)
                    {
                        if (!File.Exists(file))
                            continue;

                        File.Move(file, target);
                        DeleteSidecar(file);
                    }
                }

                moved = true;
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to move legacy replay {Path}", file);
            }
        }

        return moved;
    }

    internal static string? Import(string sourcePath, ushort contentId, string contentName)
    {
        if (!ReplayFile.TryRead(sourcePath, out var header))
            return null;

        if (header.ContentFinderConditionId != 0)
            contentId = header.ContentFinderConditionId;

        if (contentId != 0 && string.IsNullOrWhiteSpace(contentName))
            contentName = Lookup(contentId).Name ?? contentId.ToString();

        if (string.IsNullOrWhiteSpace(contentName))
            contentName = "content";

        var existing = FindIdentical(sourcePath, header);
        if (existing != null)
        {
            TryDeleteSource(sourcePath);
            return existing;
        }

        MigrateLedger();
        var folder = ReplayPaths.LibraryDirectory();
        Directory.CreateDirectory(folder);
        var fileName = FileStem(header.Timestamp, contentName, header.GameBuildRevision);
        var destination = NextFreePath(folder, fileName);
        try
        {
            if (!PlaceInLibrary(sourcePath, destination))
                return null;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to store replay {Path}", sourcePath);
            return null;
        }

        MaintainLibrary(force: true);
        return destination;
    }

    internal static string? Adopt(string sourcePath)
    {
        if (IsInLibrary(sourcePath))
            return sourcePath;

        if (!ReplayFile.TryRead(sourcePath, out var header))
            return null;

        return Import(sourcePath, header.ContentFinderConditionId, string.Empty);
    }

    private static string? FindIdentical(string sourcePath, ReplayFile.Header header)
    {
        var root = ReplayPaths.LibraryDirectory();
        if (!Directory.Exists(root))
            return null;

        foreach (var file in Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            if (SamePath(file, sourcePath) || IsLegacyPart(file))
                continue;
            if (!ReplayFile.TryRead(file, out var existing))
                continue;
            if (existing.GameBuildRevision != header.GameBuildRevision
                || existing.Timestamp != header.Timestamp
                || existing.Length != header.Length)
                continue;
            if (SameBytes(sourcePath, file))
                return file;
        }

        return null;
    }

    private static bool SameBytes(string left, string right)
    {
        const int Size = 81920;
        try
        {
            using var a = File.Open(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var b = File.Open(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (a.Length != b.Length)
                return false;

            var leftBytes = new byte[Size];
            var rightBytes = new byte[Size];
            while (true)
            {
                var readLeft = a.Read(leftBytes, 0, Size);
                var readRight = b.Read(rightBytes, 0, Size);
                if (readLeft != readRight)
                    return false;
                if (readLeft == 0)
                    return true;
                if (!leftBytes.AsSpan(0, readLeft).SequenceEqual(rightBytes.AsSpan(0, readRight)))
                    return false;
            }
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to compare replays {Left} and {Right}", left, right);
            return false;
        }
    }

    private static void TryDeleteSource(string sourcePath)
    {
        try
        {
            File.Delete(sourcePath);
            DeleteSidecar(sourcePath);
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to remove a duplicate replay {Path}", sourcePath);
        }
    }

    private readonly record struct StoredReplay(string Path, ReplayFile.Header Header);

    private static List<StoredReplay> ReadStored(string root)
    {
        var files = new List<StoredReplay>();
        if (!Directory.Exists(root))
            return files;

        foreach (var file in Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            if (IsLegacyPart(file) || IsSplitFile(file) || !ReplayFile.TryRead(file, out var header))
                continue;

            files.Add(new StoredReplay(file, header));
        }

        return files;
    }

    internal static int Delete(IReadOnlyList<string> paths)
    {
        var deleted = ReplayPaths.DeletedDirectory();
        Directory.CreateDirectory(deleted);

        var count = 0;
        foreach (var path in paths)
        {
            if (!IsInLibrary(path))
                continue;

            try
            {
                lock (FileGate)
                {
                    if (MoveToDeleted(path, deleted))
                        count++;
                }
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to delete replay {Path}", path);
            }
        }

        return count;
    }

    private static bool MoveToDeleted(string path, string deleted)
    {
        if (!File.Exists(path))
            return false;

        var stem = Path.GetFileNameWithoutExtension(path);
        var destination = Path.Combine(deleted, stem + ".dat");
        if (SamePath(destination, path))
            return false;
        if (File.Exists(destination))
            destination = NextFreePath(deleted, stem);

        File.Move(path, destination);
        RemoveSplitParts(path);
        File.SetCreationTimeUtc(destination, DateTime.UtcNow);
        ForgetLibraryEntry(path);
        DeleteSidecar(path);
        DeleteSidecar(destination);
        return true;
    }

    private static bool RemoveReplay(string path)
    {
        var removed = false;
        if (File.Exists(path))
        {
            File.Delete(path);
            removed = true;
        }

        ForgetLibraryEntry(path);
        if (DeleteSidecar(path))
            removed = true;

        if (removed)
            RemoveSplitParts(path);

        return removed;
    }

    private static bool PlaceInLibrary(string source, string destination)
    {
        lock (FileGate)
        {
            if (!File.Exists(source))
                return false;

            File.Move(source, destination);
            MoveSidecar(source, destination);
        }

        NoteLibraryEntry(destination);
        return true;
    }

    private static void MoveSidecar(string source, string destination)
    {
        var meta = Path.ChangeExtension(source, ".json");
        if (!File.Exists(meta))
            return;

        File.Move(meta, Path.ChangeExtension(destination, ".json"), overwrite: true);
    }

    private static bool DeleteSidecar(string path)
    {
        var meta = Path.ChangeExtension(path, ".json");
        if (!File.Exists(meta))
            return false;

        File.Delete(meta);
        return true;
    }

    internal static bool MaintainLibrary(bool force = false)
    {
        var changed = false;
        MaintainScan? scan;
        lock (FileGate)
        {
            scan = _maintainScan;
            _maintainScan = null;
        }

        if (scan != null)
        {
            if (!ReplayBackup.CanMutateReplay())
            {
                lock (FileGate)
                    _maintainScan ??= scan;
            }
            else
            {
                MigrateLedger();
                RefreshLibraryLedger();
                changed = ArchiveAgedReplays(scan.Stored);
                if (TrimDeleted(scan.Deleted))
                    changed = true;
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!force && now < Volatile.Read(ref _nextMaintainUnix))
            return changed;

        if (!ReplayBackup.CanMutateReplay())
            return changed;

        if (Interlocked.CompareExchange(ref _maintainBusy, 1, 0) != 0)
        {
            if (force)
                Volatile.Write(ref _maintainAgain, 1);
            return changed;
        }

        Volatile.Write(ref _nextMaintainUnix, now + 60);
        Task.Run(ScanLibrary);
        return changed;
    }

    private static void ScanLibrary()
    {
        try
        {
            var scan = new MaintainScan
            {
                Stored = DeletableFiles(),
                Deleted = ReadDeleted(),
            };
            lock (FileGate)
                _maintainScan = scan;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to read the library");
        }
        finally
        {
            Volatile.Write(ref _maintainBusy, 0);
            if (Interlocked.Exchange(ref _maintainAgain, 0) == 1)
                Volatile.Write(ref _nextMaintainUnix, 0);
        }
    }

    private static bool ArchiveAgedReplays(List<StoredReplay> stored)
    {
        var days = C.DeletedKeepDays;
        if (days <= 0)
            return false;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        var library = new List<StoredReplay>();
        foreach (var file in stored)
        {
            if (IsInLibrary(file.Path))
                library.Add(file);
        }

        var kept = KeptPaths(library);
        var deleted = ReplayPaths.DeletedDirectory();
        var changed = false;
        foreach (var file in stored)
        {
            var age = DeleteAnchor(file.Path, file.Header.Timestamp);
            if (age == 0 || age > cutoff)
                continue;
            if (IsInLibrary(file.Path) && kept.Contains(Path.GetFullPath(file.Path)))
                continue;

            try
            {
                Directory.CreateDirectory(deleted);
                lock (FileGate)
                {
                    if (MoveToDeleted(file.Path, deleted))
                        changed = true;
                }
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to archive replay {Path}", file.Path);
            }
        }

        return changed;
    }

    private static long DeleteAnchor(string path, uint recorded)
    {
        var entered = LibraryEntered(path);
        if (entered > 0)
            return entered;

        if (IsInLibrary(path))
            return recorded;

        try
        {
            var created = new DateTimeOffset(File.GetCreationTimeUtc(path)).ToUnixTimeSeconds();
            if (created > 0)
                return created;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to read the file time {Path}", path);
        }

        return recorded;
    }

    private static List<DeletedFile> ReadDeleted()
    {
        var files = new List<DeletedFile>();
        var deleted = ReplayPaths.DeletedDirectory();
        if (!Directory.Exists(deleted))
            return files;

        foreach (var path in Directory.EnumerateFiles(deleted, "*.dat"))
        {
            if (IsLegacyPart(path) || IsSplitFile(path) || !ReplayFile.TryRead(path, out var header))
                continue;

            files.Add(new DeletedFile(path, File.GetCreationTimeUtc(path), header.Length));
        }

        return files;
    }

    private static bool TrimDeleted(List<DeletedFile> files)
    {
        var max = C.MaxLibraryGigabytes;
        if (max <= 0 || files.Count == 0)
            return false;

        var budget = (long)max * 1024 * 1024 * 1024;
        long total = 0;
        foreach (var file in files)
            total += file.Length;

        if (total <= budget)
            return false;

        var ordered = files.ToList();
        ordered.Sort(static (left, right) =>
        {
            var time = left.Arrived.CompareTo(right.Arrived);
            return time != 0
                ? time
                : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        });

        var changed = false;
        foreach (var file in ordered)
        {
            if (total <= budget)
                break;

            try
            {
                lock (FileGate)
                {
                    if (!RemoveReplay(file.Path))
                        continue;
                }

                total -= file.Length;
                changed = true;
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to remove deleted replay {Path}", file.Path);
                break;
            }
        }

        return changed;
    }

    private static HashSet<string> KeptPaths(List<StoredReplay>? libraryFiles = null)
    {
        var files = libraryFiles ?? ReadStored(ReplayPaths.LibraryDirectory());

        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in files.GroupBy(ContentKey))
        {
            var id = group.First().Header.ContentFinderConditionId;
            var count = Math.Max(0, C.DefaultKeepCount);
            if (id != 0 && TryPlace(id, out var found))
                count = C.ResolveKeep(id, found.Type, found.Patch);
            else if (id != 0 && C.TryCount(KeepCount.Content, 0, 0, id, out var content))
                count = content;

            if (count <= 0)
                continue;

            var members = group.ToList();
            var ranked = new List<StoredReplay>();
            foreach (var file in members)
            {
                if (HasCurrentConversion(file, members) || AnonymizedCopyShown(file, members))
                    continue;
                ranked.Add(file);
            }

            ranked.Sort(CompareLock);
            var taken = Math.Min(count, ranked.Count);
            for (var index = 0; index < taken; index++)
                kept.Add(Path.GetFullPath(ranked[index].Path));
        }

        return kept;
    }

    private static bool HasCurrentConversion(StoredReplay file, List<StoredReplay> group)
    {
        var sourceBuild = file.Header.GameBuildRevision;
        if (sourceBuild == 0 || sourceBuild == GameBuild.Current || file.Header.Timestamp == 0)
            return false;

        foreach (var other in group)
        {
            if (other.Header.Timestamp != file.Header.Timestamp)
                continue;
            if (other.Header.GameBuildRevision != GameBuild.Current)
                continue;
            if (ReadFileMark(other.Path).SourceBuild != sourceBuild)
                continue;
            return true;
        }

        return false;
    }

    private static bool AnonymizedCopyShown(StoredReplay file, List<StoredReplay> group)
    {
        if (file.Header.Timestamp == 0 || ReadFileMark(file.Path).Anonymized)
            return false;

        var current = false;
        var anonymized = false;
        var currentAnonymized = false;
        foreach (var other in group)
        {
            if (other.Header.Timestamp != file.Header.Timestamp)
                continue;

            var marked = ReadFileMark(other.Path).Anonymized;
            var samePatch = other.Header.GameBuildRevision == GameBuild.Current;
            current |= samePatch;
            anonymized |= marked;
            currentAnonymized |= samePatch && marked;
        }

        return current ? currentAnonymized : anonymized;
    }

    private static int CompareLock(StoredReplay left, StoredReplay right)
    {
        var tier = LockTier(left).CompareTo(LockTier(right));
        if (tier != 0)
            return tier;

        var time = right.Header.Timestamp.CompareTo(left.Header.Timestamp);
        return time != 0
            ? time
            : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static int LockTier(StoredReplay file)
    {
        if (!file.Header.Cleared)
            return 2;

        return file.Header.GameBuildRevision == GameBuild.Current ? 0 : 1;
    }

    private static bool TryPlace(uint contentId, out (uint Type, uint Patch) found)
    {
        lock (PlacementCache)
        {
            if (PlacementCache.TryGetValue(contentId, out var cached))
            {
                found = cached.GetValueOrDefault();
                return cached.HasValue;
            }
        }

        var row = PluginServices.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(contentId);
        lock (PlacementCache)
        {
            if (row == null)
            {
                PlacementCache[contentId] = null;
                found = default;
                return false;
            }

            found = (row.Value.ContentType.RowId, row.Value.RequiredExVersion.RowId);
            PlacementCache[contentId] = found;
            return true;
        }
    }

    private static string ContentKey(StoredReplay file)
    {
        if (file.Header.ContentFinderConditionId != 0)
            return file.Header.ContentFinderConditionId.ToString();

        return "0:" + ReadFileMark(file.Path).Content;
    }

    private static List<StoredReplay> DeletableFiles()
    {
        var files = ReadStored(ReplayPaths.LibraryDirectory());
        var replay = ReplayPaths.ReplayDirectory();
        if (!Directory.Exists(replay))
            return files;

        foreach (var path in Directory.EnumerateFiles(replay, "*.dat", SearchOption.AllDirectories))
        {
            if (IsInside(path, ReplayPaths.DeletedDirectory()) || IsInLibrary(path))
                continue;
            if (ReplayFile.IsLoadFile(path) || IsLegacyPart(path) || IsSplitFile(path) || !ReplayFile.TryRead(path, out var header))
                continue;

            files.Add(new StoredReplay(path, header));
        }

        return files;
    }

    private static string FileStem(uint timestamp, string contentName, uint build) =>
        FileStem(timestamp, contentName, build, 0, false);

    private static string FileStem(uint timestamp, string contentName, uint build, uint sourceBuild, bool anonymized)
    {
        var stamp = timestamp == 0
            ? DateTime.Now.ToString("yyyyMMddHHmmss")
            : DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime.ToString("yyyyMMddHHmmss");
        var safe = FileContentName(contentName);
        var stem = string.IsNullOrEmpty(safe) ? stamp : $"{stamp}_{safe}";
        stem += "_v" + build;
        if (sourceBuild != 0 && sourceBuild != build)
            stem += "_c" + sourceBuild;
        if (anonymized)
            stem += "_a";
        return stem;
    }

    private static string FileContentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];
        var length = 0;
        foreach (var ch in name.Trim())
        {
            if (invalid.Contains(ch) || char.IsControl(ch))
                continue;
            buffer[length++] = ch;
        }

        var safe = new string(buffer, 0, length).Trim().TrimEnd('.');
        if (safe.Length > 80)
            safe = safe[..80].TrimEnd();
        return safe;
    }

    private static string NextFreePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName + ".dat");
        var suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{fileName}-{suffix}.dat");
            suffix++;
        }

        return path;
    }

    private readonly record struct FileMark(uint SourceBuild, bool Anonymized, string Content);

    private static FileMark ReadFileMark(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (TryParseStem(stem, out var mark))
            return mark;

        var collision = stem.LastIndexOf('-');
        if (collision > 0 && IsDigits(stem[(collision + 1)..]) && TryParseStem(stem[..collision], out mark))
            return mark;

        return new FileMark(0, false, string.Empty);
    }

    private static bool TryParseStem(string stem, out FileMark mark)
    {
        mark = default;
        var version = stem.LastIndexOf("_v", StringComparison.Ordinal);
        if (version < 0 || !TryTakeDigits(stem[(version + 2)..], out _, out var rest))
            return false;

        uint sourceBuild = 0;
        if (rest.StartsWith("_c", StringComparison.Ordinal))
        {
            if (!TryTakeDigits(rest[2..], out var sourceDigits, out rest) || !uint.TryParse(sourceDigits, out sourceBuild))
                return false;
        }

        var anonymized = false;
        if (rest.StartsWith("_a", StringComparison.Ordinal))
        {
            anonymized = true;
            rest = rest[2..];
        }

        if (rest.Length != 0)
            return false;

        var content = string.Empty;
        if (version > 14 && stem[14] == '_' && IsDigits(stem[..14]))
            content = stem[15..version];

        mark = new FileMark(sourceBuild, anonymized, content);
        return true;
    }

    private static bool TryTakeDigits(string text, out string digits, out string rest)
    {
        var count = 0;
        while (count < text.Length && char.IsDigit(text[count]))
            count++;

        if (count == 0)
        {
            digits = string.Empty;
            rest = text;
            return false;
        }

        digits = text[..count];
        rest = text[count..];
        return true;
    }

    internal static string CopyLabel(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var dash = stem.LastIndexOf('-');
        if (dash <= 0)
            return string.Empty;

        var suffix = stem[(dash + 1)..];
        return IsDigits(suffix) ? stem[dash..] : string.Empty;
    }

    private static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsDigit);

    private static bool IsLegacyPart(string path)
    {
        var name = Path.GetFileName(path);
        var marker = name.LastIndexOf(".c", StringComparison.Ordinal);
        if (marker < 0 || !name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = name[(marker + 2)..^4];
        return digits.Length > 0 && uint.TryParse(digits, out _);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

}
