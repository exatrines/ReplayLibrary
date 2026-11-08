using Lumina.Excel.Sheets;

namespace ReplayLibrary.Library;

/// <summary>Lists the library, files a finished recording, and applies delete and trim.</summary>
internal static class Catalog
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

    private static long _nextMaintainUnix;

    internal static IReadOnlyList<Entry> List(uint preferredBuild)
    {
        var typeNames = new Dictionary<uint, string>();
        var entries = new List<Entry>();
        AddLibrary(ReplayPaths.LibraryDirectory(), entries, typeNames);
        Disambiguate(entries);
        return MarkProtected(PreferBuild(entries, preferredBuild));
    }

    private static void AddLibrary(string root, List<Entry> entries, Dictionary<uint, string> typeNames)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            if (IsConvertedSibling(file) || IsSplitFile(file))
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

    private static List<Entry> PreferBuild(List<Entry> entries, uint preferredBuild)
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
            result.AddRange(current.Count > 0 ? current : group);
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

    internal static bool IsInLibrary(string path)
    {
        var library = Path.GetFullPath(ReplayPaths.LibraryDirectory());
        var full = Path.GetFullPath(path);
        return full.StartsWith(library + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindOriginal(uint sourceBuild, uint timestamp, uint contentId)
    {
        var folder = ReplayPaths.LibraryDirectory();
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
                File.Move(file, target);
                var meta = Path.ChangeExtension(file, ".json");
                if (File.Exists(meta))
                    File.Move(meta, Path.ChangeExtension(target, ".json"), overwrite: true);
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

        if (FindIdentical(sourcePath, header) != null)
        {
            TryDeleteSource(sourcePath);
            return null;
        }

        var folder = ReplayPaths.LibraryDirectory();
        Directory.CreateDirectory(folder);
        var fileName = FileStem(header.Timestamp, contentName, header.GameBuildRevision);
        var destination = NextFreePath(folder, fileName);
        try
        {
            File.Move(sourcePath, destination);
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

        var stored = FindIdentical(sourcePath, header);
        if (stored != null)
        {
            TryDeleteSource(sourcePath);
            return stored;
        }

        return Import(sourcePath, header.ContentFinderConditionId, string.Empty);
    }

    private static string? FindIdentical(string sourcePath, ReplayFile.Header header)
    {
        var root = ReplayPaths.LibraryDirectory();
        if (!Directory.Exists(root))
            return null;

        foreach (var file in Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            if (SamePath(file, sourcePath) || IsConvertedSibling(file))
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
            var meta = Path.ChangeExtension(sourcePath, ".json");
            if (File.Exists(meta))
                File.Delete(meta);
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to remove a duplicate replay {Path}", sourcePath);
        }
    }

    private readonly record struct StoredReplay(string Path, ReplayFile.Header Header, bool Anonymized);

    private static List<StoredReplay> ReadStored(string root)
    {
        var files = new List<StoredReplay>();
        if (!Directory.Exists(root))
            return files;

        foreach (var file in Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            if (IsConvertedSibling(file) || IsSplitFile(file) || !ReplayFile.TryRead(file, out var header))
                continue;

            files.Add(new StoredReplay(file, header, ReadFileMark(file).Anonymized));
        }

        return files;
    }

    internal static int Delete(IReadOnlyList<string> paths)
    {
        var deleted = ReplayPaths.DeletedDirectory();
        Directory.CreateDirectory(deleted);

        var count = 0;
        var kept = KeptPaths();
        foreach (var path in paths)
        {
            if (!IsInLibrary(path) || kept.Contains(Path.GetFullPath(path)))
                continue;

            try
            {
                if (MoveToDeleted(path, deleted))
                    count++;
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to delete replay {Path}", path);
            }
        }

        TrimDeleted();
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
        var meta = Path.ChangeExtension(path, ".json");
        var metaDestination = Path.ChangeExtension(destination, ".json");
        if (File.Exists(meta))
            File.Move(meta, metaDestination, overwrite: true);
        else if (File.Exists(metaDestination))
            File.Delete(metaDestination);
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

        var meta = Path.ChangeExtension(path, ".json");
        if (File.Exists(meta))
        {
            File.Delete(meta);
            removed = true;
        }

        if (removed)
            RemoveSplitParts(path);

        return removed;
    }

    internal static bool MaintainLibrary(bool force = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!force && now < _nextMaintainUnix)
            return false;

        if (!ReplayBackup.CanMutateReplay())
            return false;

        _nextMaintainUnix = now + 60;
        var changed = ArchiveAgedReplays();
        if (TrimDeleted())
            changed = true;
        return changed;
    }

    private static bool ArchiveAgedReplays()
    {
        var days = C.DeletedKeepDays;
        if (days <= 0)
            return false;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        var stored = DeletableFiles();
        var kept = KeptPaths(stored);
        var deleted = ReplayPaths.DeletedDirectory();
        var changed = false;
        foreach (var file in stored)
        {
            if (file.Header.Timestamp == 0 || file.Header.Timestamp > cutoff)
                continue;
            if (kept.Contains(Path.GetFullPath(file.Path)))
                continue;

            try
            {
                Directory.CreateDirectory(deleted);
                if (MoveToDeleted(file.Path, deleted))
                    changed = true;
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to archive replay {Path}", file.Path);
            }
        }

        return changed;
    }

    private static bool TrimDeleted()
    {
        var max = C.MaxLibraryGigabytes;
        if (max <= 0)
            return false;

        var deleted = ReplayPaths.DeletedDirectory();
        if (!Directory.Exists(deleted))
            return false;

        var budget = (long)max * 1024 * 1024 * 1024;
        var files = new List<(string Path, uint Timestamp, long Length)>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(deleted, "*.dat"))
        {
            if (IsConvertedSibling(path) || IsSplitFile(path) || !ReplayFile.TryRead(path, out var header))
                continue;

            files.Add((path, header.Timestamp, header.Length));
            total += header.Length;
        }

        if (total <= budget)
            return false;

        files.Sort(static (left, right) =>
        {
            var time = DeletedOrder(left.Timestamp).CompareTo(DeletedOrder(right.Timestamp));
            return time != 0
                ? time
                : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        });

        var changed = false;
        foreach (var file in files)
        {
            if (total <= budget)
                break;

            try
            {
                if (!RemoveReplay(file.Path))
                    continue;

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

    private static uint DeletedOrder(uint timestamp) => timestamp == 0 ? uint.MaxValue : timestamp;

    private static HashSet<string> KeptPaths(List<StoredReplay>? libraryFiles = null)
    {
        var place = ContentPlacement();

        var files = libraryFiles == null
            ? DeletableFiles()
            : [.. libraryFiles];

        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in files.GroupBy(ContentKey))
        {
            var id = group.First().Header.ContentFinderConditionId;
            var count = Math.Max(0, C.DefaultKeepCount);
            if (id != 0 && place.TryGetValue(id, out var found))
                count = C.ResolveKeep(id, found.Type, found.Patch);
            else if (id != 0 && C.TryCount(KeepCount.Content, 0, 0, id, out var content))
                count = content;

            if (count <= 0)
                continue;

            var taken = 0;
            foreach (var recording in Rank(group))
            {
                if (taken >= count)
                    break;

                foreach (var file in recording)
                    kept.Add(Path.GetFullPath(file.Path));
                taken++;
            }
        }

        return kept;
    }

    private static Dictionary<uint, (uint Type, uint Patch)> ContentPlacement()
    {
        var map = new Dictionary<uint, (uint Type, uint Patch)>();
        foreach (var row in PluginServices.DataManager.GetExcelSheet<ContentFinderCondition>())
        {
            if (row.RowId == 0)
                continue;

            map[row.RowId] = (row.ContentType.RowId, row.RequiredExVersion.RowId);
        }

        return map;
    }

    private static List<List<StoredReplay>> Rank(IEnumerable<StoredReplay> files)
    {
        var recordings = new List<List<StoredReplay>>();
        foreach (var file in files)
        {
            List<StoredReplay>? recording = null;
            if (file.Header.Timestamp != 0)
                recording = recordings.Find(row => row[0].Header.Timestamp == file.Header.Timestamp);
            if (recording == null)
            {
                recording = [];
                recordings.Add(recording);
            }

            recording.Add(file);
        }

        recordings.Sort(static (left, right) =>
        {
            var cleared = Cleared(right).CompareTo(Cleared(left));
            if (cleared != 0)
                return cleared;
            var anonymized = Anonymized(right).CompareTo(Anonymized(left));
            if (anonymized != 0)
                return anonymized;
            var time = right[0].Header.Timestamp.CompareTo(left[0].Header.Timestamp);
            if (time != 0)
                return time;
            var build = CurrentBuild(right).CompareTo(CurrentBuild(left));
            return build != 0
                ? build
                : string.Compare(BestPath(left), BestPath(right), StringComparison.OrdinalIgnoreCase);
        });
        return recordings;
    }

    private static bool Cleared(List<StoredReplay> files) => files.Exists(static file => file.Header.Cleared);

    private static bool Anonymized(List<StoredReplay> files) => files.Exists(static file => file.Anonymized);

    private static bool CurrentBuild(List<StoredReplay> files) =>
        files.Exists(static file => file.Header.GameBuildRevision == GameBuild.Current);

    private static string BestPath(List<StoredReplay> files)
    {
        var path = files[0].Path;
        for (var index = 1; index < files.Count; index++)
        {
            if (string.Compare(files[index].Path, path, StringComparison.OrdinalIgnoreCase) < 0)
                path = files[index].Path;
        }

        return path;
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

        var deleted = Path.GetFullPath(ReplayPaths.DeletedDirectory());
        var library = Path.GetFullPath(ReplayPaths.LibraryDirectory());
        foreach (var path in Directory.EnumerateFiles(replay, "*.dat", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(deleted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(library + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ReplayFile.IsLoadFile(path) || IsConvertedSibling(path) || IsSplitFile(path) || !ReplayFile.TryRead(path, out var header))
                continue;

            files.Add(new StoredReplay(path, header, ReadFileMark(path).Anonymized));
        }

        return files;
    }

    private static string FileStem(uint timestamp, string contentName, uint build)
    {
        var stamp = timestamp == 0
            ? DateTime.Now.ToString("yyyyMMddHHmmss")
            : DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime.ToString("yyyyMMddHHmmss");
        var safe = FileContentName(contentName);
        var stem = string.IsNullOrEmpty(safe) ? stamp : $"{stamp}_{safe}";
        return stem + "_v" + build;
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

    private static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsDigit);

    private static bool IsConvertedSibling(string path)
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
