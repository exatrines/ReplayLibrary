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
    private const int SeekDeltaOffset = 0x728;
    private const int LoadingOffset = 0x720;
    private const int SelectedChapterOffset = 0x734;
    private const int SlotOffset = 0x458;
    private const int SegmentHeader = 12;

    private static byte[]? _bytes;
    private static GCHandle _handle;
    private static uint _dataLength;
    private static string? _pendingPath;
    private static uint _seekStop;
    private static byte _quickTarget;
    private static byte _sectionEnd;
    private static int _loading;
    private static bool _waymarksApplied;
    private static float _lastPosition;
    private static long _movedAt = Environment.TickCount64;

    internal static bool HasBuffer => _bytes != null;

    internal static void Watch(string path) => _pendingPath = path;

    internal static void LoadCurrent()
    {
        Clear();
        var path = _pendingPath;
        if (string.IsNullOrEmpty(path))
        {
            var manager = ContentsReplayManager.Instance();
            if (manager == null)
                return;

            var slot = *((byte*)manager + SlotOffset);
            if (slot > 2)
                slot = 0;
            path = ReplaySlots.PathFor(slot);
        }

        if (!File.Exists(path))
            return;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < DataStart + SegmentHeader)
                return;

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var length = BitConverter.ToUInt32(bytes, ReplayLengthOffset);
            if (length < SegmentHeader || DataStart + length > bytes.Length)
                return;

            _bytes = bytes;
            _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
            _dataLength = length;
            _waymarksApplied = false;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Could not read the playing replay");
            Clear();
        }
    }

    internal static void Clear()
    {
        PlaybackHooks.SetFastForward(false);
        PlaybackHooks.SetUnlimited(false);
        if (_handle.IsAllocated)
            _handle.Free();
        _bytes = null;
        _dataLength = 0;
        _seekStop = 0;
        _quickTarget = 0;
        _sectionEnd = 0;
        _loading = 0;
        _lastPosition = 0;
        _movedAt = Environment.TickCount64;
    }

    internal static byte* CurrentSegment(ContentsReplayManager* manager)
    {
        if (_bytes == null || manager == null)
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

        var segment = (byte*)_handle.AddrOfPinnedObject() + DataStart + cursor;
        var size = *(ushort*)(segment + 2);
        if (cursor + SegmentHeader + size > _dataLength)
            return null;

        return segment;
    }

    internal static void AfterPlaybackUpdate(ContentsReplayManager* manager)
    {
        if (manager == null || _bytes == null)
            return;

        *(long*)((byte*)manager + DataOffset) = 0;
        *(int*)((byte*)manager + DataLoadTypeOffset) = 0;
        ApplyWaymarks(manager);
        if (_quickTarget >= 2 && ChapterMs(manager, _sectionEnd) <= SeekMs(manager))
            ContinueQuickLoad(manager);
    }

    internal static void ArmFastForward()
    {
        if (_seekStop != 0)
            PlaybackHooks.SetFastForward(true);
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
        if (manager == null || _bytes == null || IsLoading(manager))
            return;

        if (!TryFind(ms, out var offset, out var segmentMs))
            return;

        var chapter = ChapterAtTime(manager, ms);
        var current = ChapterAtTime(manager, (uint)Math.Max(SeekMs(manager), 0));
        _seekStop = offset;
        if (SeekMs(manager) < (int)segmentMs && chapter == current)
            PlaybackHooks.RefreshChapter(manager, (byte)chapter);
        else
            PlaybackService.JumpToChapter(chapter);
    }

    internal static void Jump(uint ms)
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null || _bytes == null || !TryFind(ms, out var offset, out var segmentMs))
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

        _seekStop = 0;
        PlaybackHooks.SetFastForward(false);
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

        positionMs = SeekMs(manager);
        var start = ChapterAtTime(manager, (uint)positionMs);
        start = Previous(manager, start, FFXIVREPLAYChapterType.StartRestart);
        var end = Next(manager, start, FFXIVREPLAYChapterType.StartRestart);
        startMs = (int)ChapterMs(manager, start);
        endMs = end > start ? (int)ChapterMs(manager, end) : (int)manager->Header.TotalMs;
        if (endMs <= startMs)
            endMs = (int)manager->Header.TotalMs;
        positionMs = Math.Clamp(positionMs, startMs, endMs);
        return endMs > startMs;
    }

    internal static bool Stalled()
    {
        var manager = ContentsReplayManager.Instance();
        if (manager == null)
            return false;

        if (*(int*)((byte*)manager + LoadingOffset) != 0)
            return false;

        var paused = manager->PlaybackControls.HasFlag(ContentsReplayPlaybackControl.Paused);
        var position = manager->PositionMs;
        var now = Environment.TickCount64;
        if (paused || Math.Abs(position - _lastPosition) > 0.001f)
        {
            _lastPosition = position;
            _movedAt = now;
            return false;
        }

        return now - _movedAt > 3000;
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
                countdown = Math.Min(cutscene + 2, manager->NumChapters - 1);
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

    private static void ApplyWaymarks(ContentsReplayManager* manager)
    {
        var loading = *(int*)((byte*)manager + LoadingOffset);
        if (!_waymarksApplied && _loading != 0 && loading == 0 && !C.ShowWaymarks && PlaybackHooks.WaymarksVisible)
            PlaybackHooks.ToggleWaymarks();
        if (loading == 0)
            _waymarksApplied = true;
        _loading = loading;
    }

    private static bool TryFind(uint ms, out uint offset, out uint segmentMs)
    {
        offset = 0;
        segmentMs = 0;
        if (_bytes == null)
            return false;

        var data = (byte*)_handle.AddrOfPinnedObject() + DataStart;
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
        var chapter = 0;
        var count = manager->NumChapters;
        for (var i = 0; i < count; i++)
        {
            if (manager->Chapters[i].PositionMs <= ms)
                chapter = i;
        }

        return chapter;
    }

    private static int Previous(ContentsReplayManager* manager, int from, FFXIVREPLAYChapterType type)
    {
        for (var i = Math.Min(from, manager->NumChapters - 1); i >= 0; i--)
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
