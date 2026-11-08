using ReplayLibrary.Game;

namespace ReplayLibrary.Playback;

/// <summary>Duty recorder playback patches. The content check is ignored, and the zone fade is shortened.</summary>
internal sealed class PlaybackPatches : IDisposable
{
    private static readonly byte[] AllowContent = [0x90, 0x90, 0x90, 0x90, 0x90, 0x90];
    private static readonly byte[] FadeOut = [0x44, 0x8D, 0x07, 0x90];
    private static readonly byte[] FadeIn = [0x44, 0x8D, 0x42, 0x01];

    private readonly CodePatch? _selectedSlot;
    private readonly CodePatch? _slotList;
    private readonly CodePatch? _fadeOut;
    private readonly CodePatch? _fadeIn;

    internal PlaybackPatches()
    {
        _selectedSlot = CodePatch.TryCreate("F6 40 7C 02 74 04 B0 01 EB 02 32 C0 40 84 FF", AllowContent);
        _slotList = CodePatch.TryCreate("F6 40 7C 02 74 04 B0 01 EB 02 32 C0 40 84 ED", AllowContent);
        _fadeOut = CodePatch.TryCreate("44 8D 47 0A 33 D2", FadeOut);
        _fadeIn = CodePatch.TryCreate("44 8D 42 0A 41 FF 92 ?? ?? 00 00 48 8B 5C 24", FadeIn);
        _selectedSlot?.SetEnabled(true);
        _slotList?.SetEnabled(true);
        _fadeOut?.SetEnabled(true);
        _fadeIn?.SetEnabled(true);
    }

    public void Dispose()
    {
        _selectedSlot?.Dispose();
        _slotList?.Dispose();
        _fadeOut?.Dispose();
        _fadeIn?.Dispose();
    }
}
