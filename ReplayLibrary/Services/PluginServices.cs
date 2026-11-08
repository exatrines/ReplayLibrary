namespace ReplayLibrary.Services;

/// <summary>Dalamud services injected at plugin startup.</summary>
internal static class PluginServices
{
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    internal static ICommandManager CommandManager { get; private set; } = null!;
    internal static IFramework Framework { get; private set; } = null!;
    internal static IClientState ClientState { get; private set; } = null!;
    internal static ICondition Condition { get; private set; } = null!;
    internal static IDataManager DataManager { get; private set; } = null!;
    internal static IChatGui ChatGui { get; private set; } = null!;
    internal static IPluginLog Log { get; private set; } = null!;
    internal static IPlayerState PlayerState { get; private set; } = null!;
    internal static ISigScanner SigScanner { get; private set; } = null!;
    internal static IGameGui GameGui { get; private set; } = null!;
    internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    internal static IGameConfig GameConfig { get; private set; } = null!;
    internal static IToastGui Toast { get; private set; } = null!;
    internal static ITargetManager TargetManager { get; private set; } = null!;

    internal static void Init(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IDataManager dataManager,
        IChatGui chatGui,
        IPluginLog log,
        IPlayerState playerState,
        ISigScanner sigScanner,
        IGameGui gameGui,
        IGameInteropProvider gameInterop,
        IGameConfig gameConfig,
        IToastGui toast,
        ITargetManager targetManager)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        Framework = framework;
        ClientState = clientState;
        Condition = condition;
        DataManager = dataManager;
        ChatGui = chatGui;
        Log = log;
        PlayerState = playerState;
        SigScanner = sigScanner;
        GameGui = gameGui;
        GameInterop = gameInterop;
        GameConfig = gameConfig;
        Toast = toast;
        TargetManager = targetManager;
    }
}
