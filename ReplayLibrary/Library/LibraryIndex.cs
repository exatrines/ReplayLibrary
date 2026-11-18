using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReplayLibrary.Library;

/// <summary>One catalog for the library. Per-file json is migrated once, then removed.</summary>
internal static partial class Catalog
{
    private const string LedgerFileName = "library.json";

    private static int _ledgerReady;

    private sealed class LedgerRow
    {
        public string Name = "";
        public long Entered;
        public bool Anonymized;
        public uint SourceBuild;
        public ulong Jobs;
        public bool Cleared;
        public uint Timestamp;
        public int ContentId;
        public uint Build;
        public long Length;
        public int Viewpoint;
        public long Size;
        public long Written;
    }

    internal static void MigrateLedger()
    {
        if (Volatile.Read(ref _ledgerReady) != 0 || !ReplayBackup.CanMutateReplay())
            return;

        lock (FileGate)
        {
            if (_ledgerReady != 0)
                return;

            var folder = ReplayPaths.LibraryDirectory();
            Directory.CreateDirectory(folder);
            var created = !File.Exists(LedgerPath());
            if (created && !SaveLedger(BuildLedger()))
                return;

            var removed = DeletePerFileJson(folder);
            removed += DeletePerFileJson(ReplayPaths.DeletedDirectory());
            if (created || removed > 0)
                PluginServices.Log.Information("Library catalog is library.json ({Count} per-file files removed)", removed);

            _ledgerReady = 1;
        }
    }

    internal static void CopyEntered(string source, string? output)
    {
        long recorded = ReplayFile.TryRead(source, out var header) ? header.Timestamp : 0;
        if (!UpdateLedger(rows =>
        {
            var changed = false;
            var entered = recorded;
            var sourceRow = FindRow(rows, source);
            if (sourceRow >= 0)
                entered = rows[sourceRow].Entered;
            else if (File.Exists(source) && IsInLibrary(source))
            {
                entered = Stamp(entered);
                rows.Add(Capture(source, entered));
                changed = true;
            }

            if (string.IsNullOrEmpty(output) || !File.Exists(output) || !IsInLibrary(output) || FindRow(rows, output) >= 0)
                return changed;

            rows.Add(Capture(output, Stamp(entered)));
            return true;
        }))
            return;

        if (IsInLibrary(source))
            DeleteSidecar(source);
        if (!string.IsNullOrEmpty(output) && IsInLibrary(output))
            DeleteSidecar(output);
    }

    private static void NoteLibraryEntry(string? path, long entered = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !IsInLibrary(path))
            return;

        if (!UpdateLedger(rows =>
        {
            if (FindRow(rows, path) >= 0)
                return false;

            rows.Add(Capture(path, Stamp(entered)));
            return true;
        }))
            return;

        DeleteSidecar(path);
    }

    private static void ForgetLibraryEntry(string path)
    {
        UpdateLedger(rows => rows.RemoveAll(row => SameLibraryName(row.Name, LibraryName(path))) > 0);
    }

    private static long LibraryEntered(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(LedgerPath()))
            return 0;

        lock (FileGate)
        {
            var rows = LoadLedger();
            if (rows == null)
                return 0;

            var index = FindRow(rows, path);
            return index >= 0 ? rows[index].Entered : 0;
        }
    }

    private static void RefreshLibraryLedger()
    {
        var folder = ReplayPaths.LibraryDirectory();
        if (!Directory.Exists(folder))
            return;

        UpdateLedger(rows =>
        {
            var dirty = false;
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                var full = Path.GetFullPath(Path.Combine(folder, rows[i].Name));
                if (!File.Exists(full))
                {
                    PluginServices.Log.Error("Library file listed in the catalog is missing {Path}", full);
                    rows.RemoveAt(i);
                    dirty = true;
                    continue;
                }

                var info = new FileInfo(full);
                if (info.Length == rows[i].Size && info.LastWriteTimeUtc.Ticks == rows[i].Written)
                    continue;
                if (!ReplayFile.TryRead(full, out var header))
                    continue;

                WriteHeader(rows[i], header);
                rows[i].Size = info.Length;
                rows[i].Written = info.LastWriteTimeUtc.Ticks;
                dirty = true;
            }

            foreach (var file in ReadStored(folder))
            {
                if (FindRow(rows, file.Path) >= 0)
                    continue;

                rows.Add(Capture(file.Path, file.Header.Timestamp));
                dirty = true;
            }

            return dirty;
        });
    }

    private static bool UpdateLedger(Func<List<LedgerRow>, bool> change)
    {
        if (!File.Exists(LedgerPath()))
            return false;

        lock (FileGate)
        {
            var rows = LoadLedger();
            if (rows == null)
                return false;
            if (!change(rows))
                return true;

            return SaveLedger(rows);
        }
    }

    private static List<LedgerRow> BuildLedger()
    {
        var rows = new List<LedgerRow>();
        foreach (var file in ReadStored(ReplayPaths.LibraryDirectory()))
            rows.Add(Capture(file.Path, SidecarEntered(file.Path, file.Header.Timestamp)));

        return rows;
    }

    private static LedgerRow Capture(string path, long entered)
    {
        var info = new FileInfo(path);
        var mark = ReadFileMark(path);
        var row = new LedgerRow
        {
            Name = LibraryName(path),
            Entered = entered,
            Anonymized = mark.Anonymized,
            SourceBuild = mark.SourceBuild,
            Size = info.Exists ? info.Length : 0,
            Written = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
        };

        if (ReplayFile.TryRead(path, out var header))
            WriteHeader(row, header);

        return row;
    }

    private static void WriteHeader(LedgerRow row, ReplayFile.Header header)
    {
        row.Timestamp = header.Timestamp;
        row.ContentId = header.ContentFinderConditionId;
        row.Build = header.GameBuildRevision;
        row.Length = header.Length;
        row.Jobs = header.Jobs;
        row.Viewpoint = header.Viewpoint;
        row.Cleared = header.Cleared;
    }

    private static long Stamp(long entered) =>
        entered > 0 ? entered : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static long SidecarEntered(string path, uint recorded)
    {
        var meta = Path.ChangeExtension(path, ".json");
        if (!File.Exists(meta))
            return recorded;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(meta)) is JsonObject obj
                && obj["deleteFrom"] is JsonValue value
                && value.TryGetValue<long>(out var from)
                && from > 0)
                return from;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to read the delete timer {Path}", path);
        }

        return recorded;
    }

    private static List<LedgerRow>? LoadLedger()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(LedgerPath())) is not JsonObject root
                || root["files"] is not JsonArray files)
            {
                PluginServices.Log.Error("Library catalog has no file list");
                return null;
            }

            var rows = new List<LedgerRow>();
            foreach (var node in files)
            {
                if (node is not JsonObject obj)
                    continue;

                var name = obj["name"]?.ToString();
                if (string.IsNullOrEmpty(name))
                    continue;

                rows.Add(ReadRow(name, obj));
            }

            return rows;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to read the library catalog");
            return null;
        }
    }

    private static bool SaveLedger(List<LedgerRow> rows)
    {
        var path = LedgerPath();
        var temp = path + ".tmp";
        try
        {
            var files = new JsonArray();
            foreach (var row in rows)
                files.Add(WriteRow(row));

            File.WriteAllText(temp, new JsonObject { ["files"] = files }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to write the library catalog");
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception cleanup)
            {
                PluginServices.Log.Error(cleanup, "Failed to remove the catalog temp file");
            }

            return false;
        }
    }

    private static int DeletePerFileJson(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), LedgerFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception e)
            {
                PluginServices.Log.Error(e, "Failed to delete replay metadata {Path}", file);
            }
        }

        return removed;
    }

    private static int FindRow(List<LedgerRow> rows, string path)
    {
        var name = LibraryName(path);
        return rows.FindIndex(row => SameLibraryName(row.Name, name));
    }

    private static string LedgerPath() => Path.Combine(ReplayPaths.LibraryDirectory(), LedgerFileName);

    private static string LibraryName(string path)
    {
        var relative = Path.GetRelativePath(ReplayPaths.LibraryDirectory(), path);
        return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static bool SameLibraryName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static LedgerRow ReadRow(string name, JsonObject obj) => new()
    {
        Name = name,
        Entered = Long(obj, "entered"),
        Anonymized = Flag(obj, "anonymized"),
        SourceBuild = UInt(obj, "sourceBuild"),
        Jobs = ULong(obj, "jobs"),
        Cleared = Flag(obj, "cleared"),
        Timestamp = UInt(obj, "timestamp"),
        ContentId = (int)Long(obj, "contentId"),
        Build = UInt(obj, "build"),
        Length = Long(obj, "length"),
        Viewpoint = (int)Long(obj, "viewpoint"),
        Size = Long(obj, "size"),
        Written = Long(obj, "written"),
    };

    private static JsonObject WriteRow(LedgerRow row) => new()
    {
        ["name"] = row.Name,
        ["entered"] = row.Entered,
        ["anonymized"] = row.Anonymized,
        ["sourceBuild"] = row.SourceBuild,
        ["jobs"] = row.Jobs.ToString(),
        ["cleared"] = row.Cleared,
        ["timestamp"] = row.Timestamp,
        ["contentId"] = row.ContentId,
        ["build"] = row.Build,
        ["length"] = row.Length,
        ["viewpoint"] = row.Viewpoint,
        ["size"] = row.Size,
        ["written"] = row.Written,
    };

    private static bool Flag(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static long Long(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private static uint UInt(JsonObject obj, string name)
    {
        var number = Long(obj, name);
        return number < 0 || number > uint.MaxValue ? 0 : (uint)number;
    }

    private static ulong ULong(JsonObject obj, string name) =>
        ulong.TryParse(obj[name]?.ToString(), out var number) ? number : 0;
}
