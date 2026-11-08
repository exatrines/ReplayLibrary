using System.Text;

namespace ReplayLibrary.Game;

/// <summary>On-disk replay header. Layout matches ContentsReplayManager.FFXIVREPLAYHeader.</summary>
internal static class ReplayFile
{
    internal const int HeaderSize = 0x68;
    internal const int ChapterSize = 772;
    internal const int DataOffset = HeaderSize + ChapterSize;
    internal const int SegmentHeader = 12;
    internal const int ReplayLengthOffset = 72;
    internal const int ContentFinderConditionOffset = 0x20;
    internal const int TimestampOffset = 0x14;
    internal const int GameBuildOffset = 0x10;
    internal const int LocalContentIdOffset = 0x30;
    internal const int JobsOffset = 0x38;
    internal const int ViewpointOffset = 0x40;
    internal const int FlagsOffset = 0x28;
    internal const byte ClearedFlag = 0x04;
    internal const string Magic = "FFXIVREPLAY";

    internal readonly record struct Header(
        uint GameBuildRevision,
        uint Timestamp,
        ushort ContentFinderConditionId,
        ulong LocalContentId,
        long Length,
        ulong Jobs,
        byte Viewpoint,
        bool Cleared);

    internal static bool TryRead(string path, out Header header)
    {
        header = default;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < HeaderSize)
                return false;

            var bytes = new byte[HeaderSize];
            if (stream.Read(bytes, 0, HeaderSize) != HeaderSize)
                return false;

            var magic = Encoding.ASCII.GetString(bytes, 0, Magic.Length);
            if (!magic.Equals(Magic, StringComparison.Ordinal))
                return false;

            header = new Header(
                BitConverter.ToUInt32(bytes, GameBuildOffset),
                BitConverter.ToUInt32(bytes, TimestampOffset),
                BitConverter.ToUInt16(bytes, ContentFinderConditionOffset),
                BitConverter.ToUInt64(bytes, LocalContentIdOffset),
                stream.Length,
                BitConverter.ToUInt64(bytes, JobsOffset),
                bytes[ViewpointOffset],
                (bytes[FlagsOffset] & ClearedFlag) != 0);
            return true;
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to read replay header {Path}", path);
            return false;
        }
    }

    internal static void WriteLocalContentId(string path, ulong contentId)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = LocalContentIdOffset;
        stream.Write(BitConverter.GetBytes(contentId));
    }

    internal static void ClaimViewpoint(string path, ulong contentId)
    {
        var local = (uint)contentId;
        if (local == 0)
            return;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to claim replay viewpoint {Path}", path);
            return;
        }

        if (bytes.Length < DataOffset + 4 || bytes[ViewpointOffset] >= 8)
            return;

        var end = DataOffset + (int)BitConverter.ToUInt32(bytes, ReplayLengthOffset);
        if (end > bytes.Length)
            return;

        var contentAt = bytes[ViewpointOffset] * 456 + 48;
        var recorded = ViewpointContentId(bytes, end, contentAt);
        if (recorded == 0 || recorded == local)
            return;

        var pos = DataOffset;
        var changed = false;
        while (pos + SegmentHeader <= end)
        {
            var opcode = BitConverter.ToUInt16(bytes, pos);
            var length = BitConverter.ToUInt16(bytes, pos + 2);
            var payload = pos + SegmentHeader;
            if (payload + length > end)
                return;

            if (opcode == 743 && length == 3672 && BitConverter.ToUInt32(bytes, payload + contentAt) == recorded)
            {
                BitConverter.GetBytes(local).CopyTo(bytes, payload + contentAt);
                changed = true;
            }
            else if (opcode == 380 && length == 664 && BitConverter.ToUInt32(bytes, payload + 8) == recorded)
            {
                BitConverter.GetBytes(local).CopyTo(bytes, payload + 8);
                changed = true;
            }

            pos += SegmentHeader + length;
        }

        if (!changed)
            return;

        try
        {
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception e)
        {
            PluginServices.Log.Error(e, "Failed to claim replay viewpoint {Path}", path);
        }
    }

    private static uint ViewpointContentId(byte[] bytes, int end, int contentAt)
    {
        var pos = DataOffset;
        while (pos + SegmentHeader <= end)
        {
            var opcode = BitConverter.ToUInt16(bytes, pos);
            var length = BitConverter.ToUInt16(bytes, pos + 2);
            var payload = pos + SegmentHeader;
            if (payload + length > end)
                return 0;

            if (opcode == 743 && length == 3672)
            {
                var id = BitConverter.ToUInt32(bytes, payload + contentAt);
                if (id != 0)
                    return id;
            }

            pos += SegmentHeader + length;
        }

        return 0;
    }

    internal static string SlotName(ulong contentId, int slot) =>
        $"FFXIV_{contentId:X16}_{slot:D3}.dat";

    internal static bool IsLoadFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length != 30 || !name.StartsWith("FFXIV_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name[22] != '_')
            return false;

        for (var index = 6; index < 22; index++)
        {
            if (!Uri.IsHexDigit(name[index]))
                return false;
        }

        return char.IsDigit(name[23]) && char.IsDigit(name[24]) && char.IsDigit(name[25]);
    }
}
