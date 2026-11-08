using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ReplayLibrary.Playback;

/// <summary>Playback hooks: packet source, chapter load, cutscenes, names, and waymarks.</summary>
internal sealed unsafe class PlaybackHooks : IDisposable
{
    private const int WaymarkOffset = 72;
    private const uint ReplayObjectId = 0xE0000000;
    private const ushort RsvOpcode = 61441;
    private const ushort RsfOpcode = 61442;

    private static PlaybackHooks? _current;

    private readonly CodePatch? _fastForward;
    private readonly CodePatch? _packetCap;
    private readonly CodePatch? _unlimited;
    private readonly CodePatch? _hideName;
    private readonly CodePatch? _segmentPatch;
    private readonly Hook<SegmentDelegate>? _segment;
    private readonly Hook<UpdateDelegate>? _update;
    private readonly Hook<ChapterDelegate>? _chapter;
    private readonly Hook<ReplayDelegate>? _replay;
    private readonly Hook<PacketDelegate>? _rsv;
    private readonly Hook<PacketDelegate>? _rsf;
    private readonly Hook<CommandDelegate>? _command;
    private readonly Hook<EventDelegate>? _eventBegin;
    private readonly Hook<FormatDelegate>? _format;
    private readonly delegate* unmanaged<ContentsReplayManager*, uint, ushort, byte*, ulong, byte> _write;
    private readonly delegate* unmanaged<CharacterManager*, int, void> _deleteNpc;
    private byte* _waymarks;

    private delegate nint SegmentDelegate(ContentsReplayManager* manager);
    private delegate void UpdateDelegate(ContentsReplayManager* manager);
    private delegate void ChapterDelegate(ContentsReplayManager* manager, byte chapter);
    private delegate byte ReplayDelegate(ContentsReplayManager* manager, byte* segment, byte* data);
    private delegate byte PacketDelegate(byte* data);
    private delegate byte CommandDelegate(uint trigger, int a, int b, int c, int d);
    private delegate nint EventDelegate(nint a1, nint a2);
    private delegate nint FormatDelegate(nint module, uint row, int index, uint hours, uint minutes, uint seconds, uint extra);

    internal PlaybackHooks()
    {
        _current = this;
        _fastForward = CodePatch.TryCreate("0F 83 ?? ?? ?? ?? 41 0F B7 46 02 4D 8D 46 0C", Nops(6));
        _packetCap = CodePatch.TryCreate("41 FF C7 48 39 43 38", Nops(3));
        _packetCap?.SetEnabled(true);
        _unlimited = CodePatch.TryCreate("0F 87 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 33 F6", Nops(6));
        _hideName = CodePatch.TryCreate("75 ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? F6 05", Nops(2));
        _hideName?.SetEnabled(C.HideOwnName);

        var segmentSite = Find("48 39 43 38 0F 83 ?? ?? ?? ?? 48 8B 4B 30");
        var login = Find("40 53 48 83 EC 20 F6 81 ?? ?? ?? ?? ?? 48 8B D9 0F 85 ?? ?? ?? ?? F6 81 ?? ?? ?? ?? ??");
        if (segmentSite != 0 && login != 0 && ResumeMatches(segmentSite))
        {
            _segmentPatch = CodePatch.TryAt(segmentSite, SegmentRedirect(segmentSite, login));
            _segment = Hook<SegmentDelegate>(login, OnSegment);
        }

        _update = Hook<UpdateDelegate>(Follow("E8 ?? ?? ?? ?? F6 83 ?? ?? ?? ?? 04 74 38 F6 83 ?? ?? ?? ?? 01"), OnUpdate);
        _chapter = Hook<ChapterDelegate>(
            "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 30 48 8B F1 0F B6 EA",
            OnChapter);
        _replay = Hook<ReplayDelegate>(Follow("E8 ?? ?? ?? ?? 80 BB ?? ?? ?? ?? ?? 77 9A"), OnReplay);
        _rsv = Hook<PacketDelegate>("44 8B 09 4C 8D 41 34", OnRsv);
        _rsf = Hook<PacketDelegate>("48 8B 11 4C 8D 41 08", OnRsf);
        _command = Hook<CommandDelegate>(Follow("E8 ?? ?? ?? ?? EB 5B 48 8B 47 08"), OnCommand);
        _eventBegin = Hook<EventDelegate>(
            "40 53 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 59 08",
            OnEventBegin);
        _format = Hook<FormatDelegate>(Follow("E8 ?? ?? ?? ?? 48 8B D0 8D 4F 0E"), OnFormat);
        _write = (delegate* unmanaged<ContentsReplayManager*, uint, ushort, byte*, ulong, byte>)Follow("E8 ?? ?? ?? ?? 84 C0 74 60 33 C0");
        _deleteNpc = (delegate* unmanaged<CharacterManager*, int, void>)Follow("E8 ?? ?? ?? ?? 33 D2 48 8D 47 50");
        _waymarks = WaymarkByte();

        _update?.Enable();
        _chapter?.Enable();
        _replay?.Enable();
        _rsv?.Enable();
        _rsf?.Enable();
        _command?.Enable();
        _eventBegin?.Enable();
        _format?.Enable();

        PluginServices.Toast.Toast += OnToast;
    }

    internal static bool WaymarksVisible => _current?._waymarks != null && (*_current._waymarks & 2) == 0;

    internal static void SetFastForward(bool enabled) => _current?._fastForward?.SetEnabled(enabled);

    internal static void SetUnlimited(bool enabled) => _current?._unlimited?.SetEnabled(enabled);

    internal static void SetHideName(bool enabled) => _current?._hideName?.SetEnabled(enabled);

    internal static bool ToggleWaymarks()
    {
        if (_current?._waymarks == null)
            return false;

        *_current._waymarks ^= 2;
        return true;
    }

    internal static void RefreshChapter(ContentsReplayManager* manager, byte chapter) =>
        _current?._chapter?.Original(manager, chapter);

    internal static void RemoveBattleNpcs()
    {
        if (_current == null || _current._deleteNpc == null)
            return;

        var characters = CharacterManager.Instance();
        if (characters == null)
            return;

        var list = characters->BattleCharas;
        for (var i = 0; i < 100 && i < list.Length; i++)
        {
            var battle = list[i].Value;
            if (battle != null && battle->ObjectKind == ObjectKind.BattleNpc)
                _current._deleteNpc(characters, i);
        }
    }

    internal static void EnterGroupPose()
    {
        var ui = UIModule.Instance();
        if (ui != null)
            ui->EnterGPose();
    }

    internal static void EnterIdleCamera()
    {
        var ui = UIModule.Instance();
        if (ui == null)
            return;

        var focus = PluginServices.TargetManager.FocusTarget;
        var id = focus?.GameObjectId ?? ReplayObjectId;
        ui->EnterIdleCam(0, id);
    }

    public void Dispose()
    {
        PluginServices.Toast.Toast -= OnToast;
        _format?.Dispose();
        _eventBegin?.Dispose();
        _command?.Dispose();
        _rsf?.Dispose();
        _rsv?.Dispose();
        _replay?.Dispose();
        _chapter?.Dispose();
        _update?.Dispose();
        _segment?.Dispose();
        _segmentPatch?.Dispose();
        _hideName?.Dispose();
        _unlimited?.Dispose();
        _packetCap?.Dispose();
        _fastForward?.Dispose();
        if (_current == this)
            _current = null;
    }

    private void OnUpdate(ContentsReplayManager* manager)
    {
        var feed = PlaybackReader.HasBuffer && _segment != null && _segmentPatch != null;
        if (feed)
        {
            _segment!.Enable();
            _segmentPatch!.SetEnabled(true);
            PlaybackReader.ArmFastForward();
        }

        try
        {
            _update!.Original(manager);
        }
        finally
        {
            if (feed)
            {
                _segmentPatch!.SetEnabled(false);
                _segment!.Disable();
            }
        }

        PlaybackReader.AfterPlaybackUpdate(manager);
    }

    private nint OnSegment(ContentsReplayManager* manager) => (nint)PlaybackReader.CurrentSegment(manager);

    private void OnChapter(ContentsReplayManager* manager, byte chapter)
    {
        _chapter!.Original(manager, chapter);
        PlaybackReader.OnChapter(manager, chapter);
    }

    private byte OnReplay(ContentsReplayManager* manager, byte* segment, byte* data)
    {
        var opcode = *(ushort*)segment;
        if (opcode == RsvOpcode && _rsv != null)
        {
            _rsv.Original(data);
            return 1;
        }

        if (opcode == RsfOpcode && _rsf != null)
        {
            _rsf.Original(data);
            return 1;
        }

        return _replay!.Original(manager, segment, data);
    }

    private byte OnRsv(byte* data)
    {
        WriteRecorded(RsvOpcode, data, *(int*)data + 52);
        return _rsv!.Original(data);
    }

    private byte OnRsf(byte* data)
    {
        WriteRecorded(RsfOpcode, data, 72);
        return _rsf!.Original(data);
    }

    private void WriteRecorded(ushort opcode, byte* data, int length)
    {
        var manager = ContentsReplayManager.Instance();
        if (_write == null || manager == null || data == null || length is < 12 or > 8192)
            return;

        if (!manager->Status.HasFlag(ContentsReplayStatus.RecordingPackets))
            return;

        _write(manager, ReplayObjectId, opcode, data, (ulong)length);
    }

    private byte OnCommand(uint trigger, int a, int b, int c, int d)
    {
        if (!PlaybackService.IsPlaying || trigger is 201 or 1981)
            return _command!.Original(trigger, a, b, c, d);

        if (trigger == 314)
        {
            var address = PluginServices.Condition.Address;
            if (address != 0)
                *(bool*)(address + (int)ConditionFlag.WatchingCutscene) = a != 0;
        }

        return 0;
    }

    private nint OnEventBegin(nint a1, nint a2)
    {
        if (PlaybackService.IsPlaying && SkipContentCutscenes())
            return 0;

        return _eventBegin!.Original(a1, a2);
    }

    private nint OnFormat(nint module, uint row, int index, uint hours, uint minutes, uint seconds, uint extra)
    {
        var text = _format!.Original(module, row, index, hours, minutes, seconds, extra);
        if (row != 3079 || index is < 1 or > 64 || text == 0 || !PluginServices.PluginInterface.UiBuilder.ShouldModifyUi)
            return text;

        try
        {
            var manager = ContentsReplayManager.Instance();
            if (manager == null || index > manager->NumChapters)
                return text;

            var start = manager->Chapters[index - 1].PositionMs;
            var end = index < manager->NumChapters
                ? manager->Chapters[index].PositionMs
                : manager->Header.TotalMs;
            if (end < start)
                end = manager->Header.TotalMs;

            var span = TimeSpan.FromMilliseconds(end - start);
            var suffix = Encoding.UTF8.GetBytes($" ({(int)span.TotalMinutes:00}:{span.Seconds:00})");
            var length = 0;
            while (length < 80 && *(byte*)(text + length) != 0)
                length++;
            if (length >= 80)
                return text;

            Marshal.Copy(suffix, 0, text + length, suffix.Length);
            *(byte*)(text + length + suffix.Length) = 0;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Chapter length was not added");
        }

        return text;
    }

    private static void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        if (!isHandled && PlaybackService.IsPlaying && (PlaybackReader.IsChapterLoading || PlaybackService.PlaybackSpeed >= 5f))
            isHandled = true;
    }

    private static bool SkipContentCutscenes() =>
        PluginServices.GameConfig.TryGet(UiConfigOption.CutsceneSkipIsContents, out bool skip) && skip;

    private byte* WaymarkByte()
    {
        var site = Find("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 3B 48 8B 0D");
        if (site == 0)
            return null;

        var target = site + 7 + Marshal.ReadInt32(site, 3);
        return (byte*)(target + WaymarkOffset);
    }

    private static bool ResumeMatches(nint site) =>
        Marshal.ReadByte(site, 0x48) == 0x4D
        && Marshal.ReadByte(site, 0x49) == 0x85
        && Marshal.ReadByte(site, 0x4A) == 0xF6;

    private static byte[] SegmentRedirect(nint site, nint login)
    {
        var patch = new byte[14];
        patch[0] = 0x48;
        patch[1] = 0x8B;
        patch[2] = 0xCB;
        patch[3] = 0xE8;
        BitConverter.GetBytes((int)(login - (site + 8))).CopyTo(patch, 4);
        patch[8] = 0x4C;
        patch[9] = 0x8B;
        patch[10] = 0xF0;
        patch[11] = 0xEB;
        patch[12] = 0x3A;
        patch[13] = 0x90;
        return patch;
    }

    private static byte[] Nops(int count)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, (byte)0x90);
        return bytes;
    }

    private static nint Follow(string signature)
    {
        var site = Find(signature);
        if (site == 0 || Marshal.ReadByte(site) != 0xE8)
            return 0;

        return site + 5 + Marshal.ReadInt32(site, 1);
    }

    private static nint Find(string signature)
    {
        try
        {
            return PluginServices.SigScanner.ScanText(signature);
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Playback address was not found");
            return 0;
        }
    }

    private static Hook<T>? Hook<T>(nint address, T detour) where T : Delegate
    {
        if (address == 0)
            return null;

        try
        {
            return PluginServices.GameInterop.HookFromAddress(address, detour);
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Playback hook was not created");
            return null;
        }
    }

    private static Hook<T>? Hook<T>(string signature, T detour) where T : Delegate =>
        Hook(Find(signature), detour);
}
