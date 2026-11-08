using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using TerritoryUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace ReplayLibrary.Playback;

/// <summary>Copies a library replay into a native slot, and applies speed and chapter once playback starts.</summary>
internal static unsafe class PlaybackService
{
    private const int HeaderVector = 0x5B0;
    private const int AgentSelectionDirty = 42;
    private const int AgentSelectedSlot = 44;

    private static bool _displayResolved;
    private static delegate* unmanaged<nint, void> _displaySelected;
    private static bool _requestPlaybackResolved;
    private static delegate* unmanaged<ContentsReplayManager*, byte, byte> _requestPlayback;
    private static bool _setChapterResolved;
    private static delegate* unmanaged<ContentsReplayManager*, byte, byte> _setChapter;
    private static bool _wasPlaying;
    private static float? _speedOverride;
    private static int? _loadedSlot;
    private static string? _loadedPath;

    internal static bool InInn
    {
        get
        {
            var row = PluginServices.DataManager.GetExcelSheet<TerritoryType>()
                .GetRowOrDefault(PluginServices.ClientState.TerritoryType);
            return row != null && row.Value.TerritoryIntendedUse.RowId == (uint)TerritoryUse.Inn;
        }
    }

    internal static bool IsPlaying
    {
        get
        {
            var manager = ContentsReplayManager.Instance();
            return manager != null && manager->PlaybackControls.HasFlag(ContentsReplayPlaybackControl.InPlayback);
        }
    }

    internal static float PlaybackSpeed
    {
        get
        {
            var manager = ContentsReplayManager.Instance();
            return manager == null ? 1f : manager->PlaybackSpeed;
        }
    }

    internal static void UpdatePlayback()
    {
        var manager = ContentsReplayManager.Instance();
        var playing = manager != null && manager->PlaybackControls.HasFlag(ContentsReplayPlaybackControl.InPlayback);
        if (!playing)
        {
            if (_wasPlaying)
                PlaybackReader.Clear();
            _wasPlaying = false;
            _speedOverride = null;
            return;
        }

        if (!_wasPlaying)
        {
            _wasPlaying = true;
            _speedOverride = null;
            PlaybackReader.LoadCurrent();
        }

        if (_speedOverride is float speed)
            manager->PlaybackSpeed = speed;
        else if (manager->PlaybackSpeed <= 0)
        {
            manager->PlaybackSpeed = 1;
            manager->PlaybackControls &= ~ContentsReplayPlaybackControl.Paused;
        }

        var address = PluginServices.Condition.Address;
        if (address != 0)
            *(bool*)(address + (int)ConditionFlag.OccupiedInCutSceneEvent) = false;
    }

    internal static bool Play(string libraryPath, int slot = 0)
    {
        if (!File.Exists(libraryPath) || slot < 0 || slot >= ReplaySlots.SlotCount)
            return false;

        if (Library.ReplayBackup.MutationBlock() is string status)
        {
            PluginServices.ChatGui.PrintError(status);
            return false;
        }

        var contentId = PluginServices.PlayerState.ContentId;
        if (contentId == 0)
        {
            PluginServices.ChatGui.PrintError("Log in before playing a replay.");
            return false;
        }

        var destination = ReplaySlots.PathFor(slot);
        try
        {
            Directory.CreateDirectory(ReplayPaths.ReplayDirectory());
            var sameFile = string.Equals(
                Path.GetFullPath(libraryPath),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase);
            if (!sameFile)
                File.Copy(libraryPath, destination, overwrite: true);
            ReplayFile.WriteLocalContentId(destination, contentId);
            ReplayFile.ClaimViewpoint(destination, contentId);
            PublishSlot(slot, destination);
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to copy replay into slot {Slot}", slot);
            PluginServices.ChatGui.PrintError("Failed to copy the replay into a slot.");
            return false;
        }

        _speedOverride = null;
        _loadedSlot = slot;
        _loadedPath = libraryPath;
        PlaybackReader.Watch(destination);
        PluginServices.ChatGui.Print($"Loaded the replay into slot {slot + 1}.");
        return true;
    }

    internal static bool StartLoaded()
    {
        if (_loadedSlot is not int slot)
        {
            PluginServices.ChatGui.PrintError("Load a replay first.");
            return false;
        }

        if (IsPlaying)
            return true;

        if (!InInn)
        {
            PluginServices.ChatGui.PrintError("Not in an inn.");
            return false;
        }

        if (!StartPlayback((byte)slot))
        {
            PluginServices.ChatGui.PrintError("Could not play the replay.");
            return false;
        }

        PluginServices.ChatGui.Print($"Playing the replay in slot {slot + 1}.");
        return true;
    }

    internal static bool Start(string libraryPath, int slot = 0)
    {
        var loaded = _loadedSlot == slot
            && _loadedPath != null
            && string.Equals(_loadedPath, libraryPath, StringComparison.OrdinalIgnoreCase);
        if (!loaded && !Play(libraryPath, slot))
            return false;

        return StartLoaded();
    }

    internal static void SetSpeed(float speed)
    {
        _speedOverride = speed;
        var manager = ContentsReplayManager.Instance();
        if (manager != null && manager->PlaybackControls.HasFlag(ContentsReplayPlaybackControl.InPlayback))
            manager->PlaybackSpeed = speed;
    }

    internal static void JumpToChapter(int index)
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null || index < 0 || index >= manager->NumChapters)
            return;

        var set = SetChapter();
        if (set == null)
        {
            PluginServices.ChatGui.PrintError("Could not jump to that chapter.");
            return;
        }

        set(manager, (byte)index);
    }

    private static void PublishSlot(int slot, string path)
    {
        var header = new byte[ReplayFile.HeaderSize];
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Read(header, 0, header.Length) != header.Length)
                return;
        }

        var manager = ContentsReplayManager.Instance();
        var headers = manager == null ? 0 : *(nint*)((nint)manager + HeaderVector);
        if (headers != 0)
            Marshal.Copy(header, 0, headers + slot * ReplayFile.HeaderSize, header.Length);
        else
            PluginServices.Log.Warning("Saved replay headers were not found");

        var addon = PluginServices.GameGui.GetAddonByName("ContentsReplaySetting", 1);
        var agent = addon.Address == 0 ? 0 : PluginServices.GameGui.FindAgentInterface(addon).Address;
        if (agent == 0)
            return;

        var display = DisplaySelected();
        if (display == null)
            return;

        *(byte*)(agent + AgentSelectedSlot) = (byte)slot;
        *(byte*)(agent + AgentSelectionDirty) = 1;
        display(agent);
    }

    private static delegate* unmanaged<nint, void> DisplaySelected()
    {
        if (_displayResolved)
            return _displaySelected;

        _displayResolved = true;
        try
        {
            var site = PluginServices.SigScanner.ScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 39 6E 34");
            if (Marshal.ReadByte(site) == 0xE8)
            {
                var relative = Marshal.ReadInt32(site, 1);
                site += 5 + relative;
            }

            _displaySelected = (delegate* unmanaged<nint, void>)site;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Duty recorder refresh was not found");
        }

        return _displaySelected;
    }

    private static bool StartPlayback(byte slot)
    {
        var start = RequestPlayback();
        var manager = ContentsReplayManager.Instance();
        if (start == null || manager == null)
            return false;

        return start(manager, slot) != 0;
    }

    private static delegate* unmanaged<ContentsReplayManager*, byte, byte> RequestPlayback()
    {
        if (_requestPlaybackResolved)
            return _requestPlayback;

        _requestPlaybackResolved = true;
        try
        {
            _requestPlayback = (delegate* unmanaged<ContentsReplayManager*, byte, byte>)PluginServices.SigScanner.ScanText(
                "48 89 5C 24 08 57 48 83 EC 30 F6 81 ?? ?? ?? ?? 04");
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Replay start function was not found");
        }

        return _requestPlayback;
    }

    private static delegate* unmanaged<ContentsReplayManager*, byte, byte> SetChapter()
    {
        if (_setChapterResolved)
            return _setChapter;

        _setChapterResolved = true;
        try
        {
            var site = PluginServices.SigScanner.ScanText("E8 ?? ?? ?? ?? 84 C0 E9 ?? ?? ?? ?? 48 8D 4F 10");
            if (Marshal.ReadByte(site) == 0xE8)
            {
                var relative = Marshal.ReadInt32(site, 1);
                site += 5 + relative;
            }

            _setChapter = (delegate* unmanaged<ContentsReplayManager*, byte, byte>)site;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Replay chapter function was not found");
        }

        return _setChapter;
    }

}
