namespace ReplayLibrary.Game;

/// <summary>The three native replay slots in the replay root.</summary>
internal static class ReplaySlots
{
    internal const int SlotCount = 3;

    internal static string PathFor(int slot) =>
        Path.Combine(ReplayPaths.ReplayDirectory(), ReplayFile.SlotName(PluginServices.PlayerState.ContentId, slot));
}
