using System.Runtime.InteropServices;

namespace ReplayLibrary.Game;

/// <summary>Reversible byte patch at a scanned address.</summary>
internal sealed class CodePatch : IDisposable
{
    private readonly nint _address;
    private readonly byte[] _original;
    private readonly byte[] _patched;
    private bool _enabled;

    private CodePatch(nint address, byte[] patched)
    {
        _address = address;
        _patched = patched;
        _original = new byte[patched.Length];
        Marshal.Copy(address, _original, 0, patched.Length);
    }

    internal static CodePatch? TryCreate(string signature, byte[] patched)
    {
        try
        {
            var address = PluginServices.SigScanner.ScanText(signature);
            return address == 0 ? null : new CodePatch(address, patched);
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Code patch was not found: {Signature}", signature);
            return null;
        }
    }

    internal static CodePatch? TryAt(nint address, byte[] patched)
    {
        if (address == 0)
            return null;

        return new CodePatch(address, patched);
    }

    internal void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        Write(enabled ? _patched : _original);
        _enabled = enabled;
    }

    public void Dispose() => SetEnabled(false);

    private void Write(byte[] bytes)
    {
        if (!VirtualProtect(_address, (nuint)bytes.Length, 0x40, out var old))
            return;

        Marshal.Copy(bytes, 0, _address, bytes.Length);
        VirtualProtect(_address, (nuint)bytes.Length, old, out _);
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
}
