using ReplayLibrary.Game;

namespace ReplayLibrary.Recording;

/// <summary>Byte patches for duty recording. One of them hides the ready-check prompt.</summary>
internal sealed class RecordPatches : IDisposable
{
    private static readonly byte[] SkipCheck = [0xEB, 0x25];
    private static readonly byte[] AllowContent = [0x90, 0x90, 0x90, 0x90, 0x90, 0x90];
    private static readonly byte[] HideReadyToast =
    [
        0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
        0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
    ];

    private readonly CodePatch? _alwaysRecord;
    private readonly CodePatch? _replayableCheck;
    private readonly CodePatch? _readyToast;
    private bool _enabled;

    internal RecordPatches()
    {
        _alwaysRecord = CodePatch.TryCreate("24 06 3C 02 75 29", SkipCheck);
        _replayableCheck = CodePatch.TryCreate("F6 40 3C 02 74 04 B0 01 EB 02 32 C0", AllowContent);
        _readyToast = CodePatch.TryCreate("BA CB 07 00 00 48 8B CF E8", HideReadyToast);
        SetEnabled(true);
    }

    internal void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        _alwaysRecord?.SetEnabled(enabled);
        _replayableCheck?.SetEnabled(enabled);
        _readyToast?.SetEnabled(enabled);
    }

    public void Dispose() => SetEnabled(false);
}
