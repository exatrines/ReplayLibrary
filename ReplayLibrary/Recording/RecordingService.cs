using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace ReplayLibrary.Recording;

/// <summary>Starts a duty recording with the client, then files allowed content types.</summary>
internal sealed unsafe class RecordingService : IDisposable
{
    private const int ArchiveWindowTicks = 600;
    private const int NextSaveSlotOffset = 0x5AF;
    private const string ZoneInSignature = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 49 8B F8 8B F2 48 8B D9 E8 ?? ?? ?? ?? F6 83";
    private const string InitializeSignature = "40 55 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 27 F6 81";
    private const string BeginSignature = "E8 ?? ?? ?? ?? 48 8B 5C 24 ?? 48 83 C4 20 41 5C";

    private delegate void ZoneInDelegate(ContentsReplayManager* manager, uint objectId, nint packet);
    private delegate void InitializeDelegate(ContentsReplayManager* manager);

    private readonly RecordPatches _patches = new();
    private readonly Hook<ZoneInDelegate>? _zoneIn;
    private readonly Hook<InitializeDelegate>? _initialize;

    private bool _wasRecording;
    private int _archiveDelay;
    private DateTime _startedUtc;
    private bool _beginResolved;
    private delegate* unmanaged<ContentsReplayManager*, byte, void> _begin;

    internal RecordingService()
    {
        _zoneIn = Hook<ZoneInDelegate>(ZoneInSignature, OnZoneIn);
        _initialize = Hook<InitializeDelegate>(InitializeSignature, OnInitialize);
    }

    internal void Update() => UpdateArchive();

    public void Dispose()
    {
        _zoneIn?.Dispose();
        _initialize?.Dispose();
        _patches.Dispose();
    }

    private void OnZoneIn(ContentsReplayManager* manager, uint objectId, nint packet)
    {
        _zoneIn!.Original(manager, objectId, packet);
        if (manager == null || ((byte)manager->Status & 1) == 0)
            return;

        if (manager->ZoneInitPacket.ContentFinderConditionId == 0)
            return;

        if (manager->Status.HasFlag(ContentsReplayStatus.RecordingPackets))
            return;

        OnInitialize(manager);
    }

    private void OnInitialize(ContentsReplayManager* manager)
    {
        if (manager == null || _initialize == null)
            return;

        PrepareSaveSlot(manager);
        _initialize.Original(manager);
        if (!manager->Status.HasFlag(ContentsReplayStatus.RecordingPackets))
            return;

        var begin = BeginRecording();
        if (begin != null)
            begin(manager, 1);

        manager->Header.LocalContentId = 0;
    }

    private static void PrepareSaveSlot(ContentsReplayManager* manager)
    {
        var slot = *((byte*)manager + NextSaveSlotOffset);
        if (slot > 2)
            *((byte*)manager + NextSaveSlotOffset) = 0;
    }

    private void UpdateArchive()
    {
        var recording = IsRecording();
        if (recording && !_wasRecording)
        {
            _wasRecording = true;
            _startedUtc = DateTime.UtcNow;
            _archiveDelay = 0;
        }
        else if (!recording && _wasRecording)
        {
            _wasRecording = false;
            _archiveDelay = ArchiveWindowTicks;
        }

        if (_archiveDelay <= 0)
            return;

        if (!Library.ReplayBackup.CanMutateReplay())
            return;

        _archiveDelay--;
        if (ArchiveFinishedSlot())
        {
            _archiveDelay = 0;
            return;
        }

        if (_archiveDelay == 0)
            PluginServices.Log.Warning("Recording ended but no new slot file was found");
    }

    private bool ArchiveFinishedSlot()
    {
        var file = FindNewSlot();
        if (file == null || !ReplayFile.TryRead(file.FullName, out var header))
            return false;

        if (!AcceptedContent(header, out var contentId, out _, out var name))
            return true;

        var stored = Library.Catalog.Import(file.FullName, contentId, name);
        if (stored == null)
            return false;

        var label = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(file.Name)
            : name;
        PluginServices.ChatGui.Print($"Saved the replay to the library: {label}");
        return true;
    }

    private static bool AcceptedContent(ReplayFile.Header header, out ushort contentId, out uint contentType, out string name)
    {
        contentId = header.ContentFinderConditionId;
        contentType = 0;
        name = string.Empty;
        if (contentId == 0)
            return false;

        var row = PluginServices.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(contentId);
        if (row == null)
            return false;

        contentType = row.Value.ContentType.RowId;
        name = row.Value.Name.ToString();
        return C.Allows(contentType);
    }

    private FileInfo? FindNewSlot()
    {
        var since = _startedUtc.AddSeconds(-5);
        FileInfo? newest = null;
        foreach (var directory in new[] { ReplayPaths.ReplayDirectory(), ReplayPaths.AutorenamedDirectory() })
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.dat"))
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < since)
                    continue;

                if (newest == null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    newest = info;
            }
        }

        return newest;
    }

    private static bool IsRecording()
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null)
            return false;

        var status = manager->Status;
        return status.HasFlag(ContentsReplayStatus.RecordingPackets)
            || status.HasFlag(ContentsReplayStatus.ContentStarted);
    }

    private delegate* unmanaged<ContentsReplayManager*, byte, void> BeginRecording()
    {
        if (_beginResolved)
            return _begin;

        _beginResolved = true;
        try
        {
            var site = PluginServices.SigScanner.ScanText(BeginSignature);
            if (Marshal.ReadByte(site) == 0xE8)
            {
                var relative = Marshal.ReadInt32(site, 1);
                site += 5 + relative;
            }

            _begin = (delegate* unmanaged<ContentsReplayManager*, byte, void>)site;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Recording start function was not found");
        }

        return _begin;
    }

    private static Hook<T>? Hook<T>(string signature, T detour) where T : Delegate
    {
        try
        {
            var address = PluginServices.SigScanner.ScanText(signature);
            var hook = PluginServices.GameInterop.HookFromAddress<T>(address, detour);
            hook.Enable();
            return hook;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Recording hook was not found: {Signature}", signature);
            return null;
        }
    }
}
