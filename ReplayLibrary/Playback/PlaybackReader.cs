using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ReplayLibrary.Playback;

/// <summary>In-memory copy of the playing replay, used to seek and to feed packets.</summary>
internal static unsafe class PlaybackReader
{
    private const int DataStart = 876;
    private const int ReplayLengthOffset = 72;
    private const int TotalMsOffset = 24;
    private const int OverallOffset = 0x38;
    private const int DataOffset = 0x30;
    private const int DataLoadTypeOffset = 0x434;
    private const int ActiveHeaderOffset = 0x48;
    private const int SeekDeltaOffset = 0x728;
    private const int SelectedChapterOffset = 0x734;
    private const int SlotOffset = 0x458;
    private const int FileStreamOffset = 0x8;
    private const int SavedHeadersOffset = 0x5B0;
    private const int SegmentHeader = 12;

    private static byte* _block;
    private static uint _dataLength;
    private static string? _pendingPath;
    private static string? _selectedPath;
    private static string? _loadedPath;
    private static bool _courseReady;
    private static uint _seekStop;
    private static byte _quickTarget;
    private static byte _sectionEnd;

    internal static bool HasBuffer => _block != null;

    internal static void Watch(string path)
    {
        _pendingPath = path;
        if (Read(path, false))
            Remember(path);
    }

    internal static void SelectPlayback(string? path) => _selectedPath = path;

    internal static void ReloadStartedPlayback(ContentsReplayManager* manager)
    {
        var path = string.IsNullOrEmpty(_selectedPath)
            ? ReplaySlots.PathFor(*((byte*)manager + SlotOffset))
            : _selectedPath;
        FreeBlock();
        LoadFromDisk(path);
    }

    internal static void ResumeClosedStream()
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null || !manager->PlaybackControls.HasFlag(ContentsReplayPlaybackControl.InPlayback))
            return;

        var stream = *(nint*)((byte*)manager + FileStreamOffset);
        if (stream == 0 || *(long*)stream != 0)
            return;

        if (!string.IsNullOrEmpty(C.LastLoadedReplay))
            LoadFromDisk(C.LastLoadedReplay);
    }

    internal static void ReleaseOnUnload()
    {
        var manager = ContentsReplayManager.Instance();
        if (manager != null && manager->PlaybackControls.HasFlag(ContentsReplayPlaybackControl.InPlayback))
        {
            manager->PlaybackControls |= ContentsReplayPlaybackControl.Paused;
            PluginServices.ChatGui.PrintError(LibraryText.Get("playback.unloaded"));
        }

        ApplySavedContentIds(0);
        FreeBlock();
        _dataLength = 0;
        _loadedPath = null;
        _courseReady = false;
        _seekStop = 0;
        _quickTarget = 0;
        _sectionEnd = 0;
    }

    internal static void ApplySavedContentIds(ulong contentId)
    {
        for (var slot = 0; slot < ReplaySlots.SlotCount; slot++)
        {
            var address = SavedHeaderAddress(slot);
            if (address == 0 || !HeaderValid(address))
                continue;

            *(ulong*)(address + ReplayFile.LocalContentIdOffset) = contentId;
        }
    }

    internal static byte[] ReadSavedHeader(int slot)
    {
        var bytes = new byte[ReplayFile.HeaderSize];
        var address = SavedHeaderAddress(slot);
        if (address != 0)
            Marshal.Copy(address, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static void WriteSavedHeader(int slot, byte[] header)
    {
        var address = SavedHeaderAddress(slot);
        if (address == 0 || header.Length < ReplayFile.HeaderSize)
            return;

        Marshal.Copy(header, 0, address, ReplayFile.HeaderSize);
    }

    internal static void PreparePlayback()
    {
        var path = ResolvePath();
        if (!string.IsNullOrEmpty(path))
            Read(path, false);
        if (_block == null || _courseReady)
            return;

        _seekStop = 0;
        _quickTarget = 0;
        _sectionEnd = 0;
        PlaybackHooks.SetFastForward(false);
        PlaybackHooks.SetUnlimited(false);
        PublishHeader();
        _courseReady = true;
    }

    internal static void Clear()
    {
        PlaybackHooks.SetFastForward(false);
        PlaybackHooks.SetUnlimited(false);
        if (_block != null)
        {
            Marshal.FreeHGlobal((nint)_block);
            _block = null;
        }

        _dataLength = 0;
        _loadedPath = null;
        _courseReady = false;
        _seekStop = 0;
        _quickTarget = 0;
        _sectionEnd = 0;
    }

    private static void PublishHeader()
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null || _block == null)
            return;

        Buffer.MemoryCopy(_block, (byte*)manager + ActiveHeaderOffset, DataStart, DataStart);
        *(int*)((byte*)manager + DataLoadTypeOffset) = 0;
    }

    private static bool LoadFromDisk(string path)
    {
        if (!Read(path, true))
            return false;

        PublishHeader();
        _courseReady = true;
        Remember(path);
        return true;
    }

    private static void Remember(string path)
    {
        if (string.Equals(C.LastLoadedReplay, path, StringComparison.OrdinalIgnoreCase))
            return;

        C.LastLoadedReplay = path;
        C.Save();
    }

    private static void FreeBlock()
    {
        if (_block == null)
            return;

        Marshal.FreeHGlobal((nint)_block);
        _block = null;
        _dataLength = 0;
        _loadedPath = null;
        _courseReady = false;
    }

    private static nint SavedHeaderAddress(int slot)
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null || slot < 0)
            return 0;

        var headers = *(nint*)((byte*)manager + SavedHeadersOffset);
        return headers == 0 ? 0 : headers + slot * ReplayFile.HeaderSize;
    }

    private static bool HeaderValid(nint address)
    {
        var magic = ReplayFile.Magic;
        for (var i = 0; i < magic.Length; i++)
        {
            if (*(byte*)(address + i) != (byte)magic[i])
                return false;
        }

        return true;
    }

    private static bool Read(string path, bool replace)
    {
        if (!replace && _block != null && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!replace && _block != null && PlaybackService.IsPlaying)
            return false;

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < DataStart + SegmentHeader)
                return false;

            var length = BitConverter.ToUInt32(bytes, ReplayLengthOffset);
            if (length < SegmentHeader || DataStart + length > bytes.Length)
                return false;

            var block = (byte*)Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, (nint)block, bytes.Length);
            var previous = _block;
            _dataLength = length;
            _loadedPath = path;
            _block = block;
            if (previous != null)
                Marshal.FreeHGlobal((nint)previous);
            return true;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Could not read the playing replay");
            return false;
        }
    }

    private static string? ResolvePath()
    {
        if (!string.IsNullOrEmpty(_pendingPath))
            return _pendingPath;

        var manager = ContentsReplayManager.Instance();
        if (manager == null)
            return null;

        var slot = *((byte*)manager + SlotOffset);
        if (slot > 2)
            slot = 0;
        return ReplaySlots.PathFor(slot);
    }

    internal static byte* CurrentSegment(ContentsReplayManager* manager)
    {
        if (_block == null || manager == null)
            return null;

        var offset = *(long*)((byte*)manager + OverallOffset);
        if (_seekStop != 0 && offset >= _seekStop)
        {
            _seekStop = 0;
            PlaybackHooks.SetFastForward(false);
        }

        if (offset < 0 || offset >= _dataLength)
            return null;

        var cursor = (uint)offset;
        if (cursor + SegmentHeader > _dataLength)
            return null;

        var rate = C.SeekRate;
        var delta = *(float*)((byte*)manager + SeekDeltaOffset);
        PlaybackHooks.SetUnlimited(rate > 100f && delta < rate);

        var segment = _block + DataStart + cursor;
        var size = *(ushort*)(segment + 2);
        if (cursor + SegmentHeader + size > _dataLength)
            return null;

        return segment;
    }

    internal static void AfterPlaybackUpdate(ContentsReplayManager* manager)
    {
        if (manager == null || _block == null)
            return;

        *(long*)((byte*)manager + DataOffset) = 0;
        *(int*)((byte*)manager + DataLoadTypeOffset) = 0;
        if (_quickTarget >= 2 && ChapterMs(manager, _sectionEnd) <= SeekMs(manager))
            ContinueQuickLoad(manager);
    }

    internal static void OnChapter(ContentsReplayManager* manager, byte chapter)
    {
        if (manager == null || !C.QuickChapterLoad || chapter == 0 || manager->NumChapters < 2)
            return;

        var current = ChapterAtTime(manager, (uint)Math.Max(SeekMs(manager), 0));
        if (current + 1 == chapter)
            return;

        _quickTarget = chapter;
        _sectionEnd = 0;
        ContinueQuickLoad(manager);
    }

    internal static void Seek(uint ms)
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null || _block == null || IsLoading(manager))
            return;

        if (!TryFind(ms, out var offset, out var segmentMs))
            return;

        var chapter = ChapterAtTime(manager, ms);
        var current = ChapterAtTime(manager, (uint)Math.Max(SeekMs(manager), 0));
        _seekStop = offset;
        PlaybackHooks.SetFastForward(true);
        if (SeekMs(manager) < (int)segmentMs && chapter == current)
            PlaybackHooks.RefreshChapter(manager, (byte)chapter);
        else
            PlaybackService.JumpToChapter(chapter);
    }

    internal static void Jump(uint ms)
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null || _block == null || !TryFind(ms, out var offset, out var segmentMs))
            return;

        *(long*)((byte*)manager + OverallOffset) = offset;
        manager->PositionMs = segmentMs / 1000f;
    }

    internal static void SkipPacket()
    {
        var manager = ContentsReplayManager.Instance();
        var segment = manager == null ? null : CurrentSegment(manager);
        if (manager == null || segment == null)
            return;

        var size = SegmentHeader + *(ushort*)(segment + 2);
        *(long*)((byte*)manager + OverallOffset) += size;
    }

    internal static void SkipToEnd()
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null)
            return;

        *(long*)((byte*)manager + OverallOffset) = long.MaxValue;
    }

    internal static bool TryPull(out int startMs, out int endMs, out int positionMs)
    {
        startMs = 0;
        endMs = 0;
        positionMs = 0;
        var manager = ContentsReplayManager.Instance();
        if (manager == null || manager->NumChapters == 0)
            return false;

        positionMs = Math.Max(SeekMs(manager), (int)ChapterMs(manager, 0));
        var start = ChapterAtTime(manager, (uint)Math.Max(SeekMs(manager), 0));
        start = Previous(manager, start, FFXIVREPLAYChapterType.StartRestart);
        var end = Next(manager, start, FFXIVREPLAYChapterType.StartRestart);
        startMs = (int)ChapterMs(manager, start);
        endMs = end > start ? (int)ChapterMs(manager, end) : (int)manager->Header.TotalMs;
        if (endMs <= startMs)
            endMs = (int)manager->Header.TotalMs;
        return endMs > startMs;
    }

    internal static bool IsChapterLoading
    {
        get
        {
            var manager = ContentsReplayManager.Instance();
            return manager != null && IsLoading(manager);
        }
    }

    private static void ContinueQuickLoad(ContentsReplayManager* manager)
    {
        if (_sectionEnd == 0)
        {
            PlaySection(manager, 0, 1);
            return;
        }

        var cutscene = Next(manager, _sectionEnd, FFXIVREPLAYChapterType.EventCutscene);
        if (cutscene != 0 && cutscene < _quickTarget - 1)
        {
            var countdown = Next(manager, cutscene, FFXIVREPLAYChapterType.Countdown);
            if (countdown == 0 || countdown > cutscene + 2)
                countdown = cutscene + 2;
            PlaySection(manager, cutscene, countdown);
            return;
        }

        PlaybackHooks.RemoveBattleNpcs();
        var start = Previous(manager, _quickTarget, FFXIVREPLAYChapterType.StartRestart);
        var ms = ChapterMs(manager, start);
        Jump(ms > 15000 ? ms - 15000 : 0);
        PlaySection(manager, 0, _quickTarget);
    }

    private static void PlaySection(ContentsReplayManager* manager, int from, int to)
    {
        if (from > 0 && *(long*)((byte*)manager + OverallOffset) < manager->Chapters[from].Offset)
            MoveTo(manager, from);

        _sectionEnd = (byte)to;
        if (_sectionEnd >= _quickTarget)
            _quickTarget = 0;
    }

    private static void MoveTo(ContentsReplayManager* manager, int index)
    {
        var chapter = manager->Chapters[index];
        *(long*)((byte*)manager + OverallOffset) = chapter.Offset;
        manager->PositionMs = chapter.PositionMs / 1000f;
    }

    private static bool TryFind(uint ms, out uint offset, out uint segmentMs)
    {
        offset = 0;
        segmentMs = 0;
        if (_block == null)
            return false;

        var data = _block + DataStart;
        uint cursor = 0;
        while (cursor + SegmentHeader <= _dataLength)
        {
            var size = SegmentHeader + *(ushort*)(data + cursor + 2);
            if (size < SegmentHeader || cursor + size > _dataLength)
                break;

            var at = *(uint*)(data + cursor + 4);
            if (at >= ms)
            {
                offset = cursor;
                segmentMs = at;
                return true;
            }

            cursor += (uint)size;
        }

        return false;
    }

    private static int ChapterAtTime(ContentsReplayManager* manager, uint ms)
    {
        var count = manager->NumChapters;
        for (var i = count - 1; i > 0; i--)
        {
            if (manager->Chapters[i].PositionMs <= ms)
                return i;
        }

        return 0;
    }

    private static int Previous(ContentsReplayManager* manager, int from, FFXIVREPLAYChapterType type)
    {
        for (var i = Math.Min(from, manager->NumChapters - 1); i > 0; i--)
        {
            if (manager->Chapters[i].Type == type)
                return i;
        }

        return 0;
    }

    private static int Next(ContentsReplayManager* manager, int from, FFXIVREPLAYChapterType type)
    {
        var count = manager->NumChapters;
        for (var i = from + 1; i < count; i++)
        {
            if (manager->Chapters[i].Type == type)
                return i;
        }

        return 0;
    }

    private static uint ChapterMs(ContentsReplayManager* manager, int index)
    {
        if (index < 0 || index >= manager->NumChapters)
            return 0;

        return manager->Chapters[index].PositionMs;
    }

    private static int SeekMs(ContentsReplayManager* manager) =>
        (int)MathF.Max(0, manager->PositionMs * 1000f);

    private static bool IsLoading(ContentsReplayManager* manager) =>
        *((byte*)manager + SelectedChapterOffset) < 64;
}
