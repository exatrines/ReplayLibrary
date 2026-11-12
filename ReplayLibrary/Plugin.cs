using ReplayLibrary.Playback;
using ReplayLibrary.Recording;
using ReplayLibrary.UI;
using Dalamud.Game;
using Dalamud.Game.Command;

namespace ReplayLibrary;

/// <summary>Content replay library. Recording and playback follow A Realm Recorded; storage is separate.</summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string Name = "Replay Library";

    private const string CommandName = "/lib";
    private const string ConfigCommandName = "/lib config";
    internal static Configuration C = null!;

    private readonly WindowSystem _windows;
    private readonly MainWindow _window;
    private readonly RecordingService _recording;
    private readonly PlaybackPatches _playback;
    private readonly PlaybackHooks _playbackHooks;
    private readonly CameraLockService _cameraLock = new();
    private readonly EntityOverlay _entities = new();

    public Plugin(
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
        ITextureProvider textureProvider,
        IGameGui gameGui,
        IGameInteropProvider gameInterop,
        IGameConfig gameConfig,
        IToastGui toast,
        ITargetManager targetManager)
    {
        PluginServices.Init(
            pluginInterface,
            commandManager,
            framework,
            clientState,
            condition,
            dataManager,
            chatGui,
            log,
            playerState,
            sigScanner,
            gameGui,
            gameInterop,
            gameConfig,
            toast,
            targetManager);

        C = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        C.Initialize(pluginInterface);
        _recording = new RecordingService();
        _playback = new PlaybackPatches();
        _playbackHooks = new PlaybackHooks();

        C.Themes ??= [];
        C.SelectedThemeId = MirageThemes.Bind(C.Themes, C.SelectedThemeId, null, SaveThemes);
        MirageUi.ConfigureTheme(() => MirageThemes.Current);
        MirageUi.BindLanguage(SaveLanguage);
        MirageUi.Init(pluginInterface, textureProvider, log);
        LibraryText.Register();
        ApplyUiLanguage();

        _window = new MainWindow();
        _windows = new WindowSystem(Name);
        _windows.AddWindow(_window);
        _windows.AddWindow(PlaybackBar.Floating);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleSettings;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the library window.",
            DisplayOrder = 0,
        });
        commandManager.AddHandler(ConfigCommandName, new CommandInfo((_, _) => ToggleSettings())
        {
            HelpMessage = "Open settings.",
            DisplayOrder = 1,
        });

        Library.ReplayBackup.StartIfNeeded();
        Library.ReplayBackup.StartLibraryIfDue();
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        PluginServices.Framework.Update -= OnFrameworkUpdate;
        PluginServices.PluginInterface.UiBuilder.Draw -= Draw;
        PluginServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettings;
        PluginServices.CommandManager.RemoveHandler(CommandName);
        PluginServices.CommandManager.RemoveHandler(ConfigCommandName);
        _entities.Restore();
        _cameraLock.Dispose();
        _playbackHooks.Dispose();
        _playback.Dispose();
        _recording.Dispose();
        _windows.RemoveAllWindows();
        _window.UnhookUi();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Library.ReplayBackup.FlushSave();
        var migrated = Library.Catalog.MigrateLegacy();
        Library.ReplayBackup.StartLibraryIfDue();
        var maintained = Library.Catalog.MaintainLibrary();
        if ((migrated || maintained) && _window.IsOpen)
            _window.Refresh();
        _recording.Update();
        PlaybackService.UpdatePlayback();
        _cameraLock.Update();
        _entities.Update();
    }

    private void Draw()
    {
        PlaybackBar.Prepare();
        _windows.Draw();
        ContentsReplaySettingButton.Draw(_window);
        PlaybackBar.DrawDocked();
        _entities.Draw();
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();
        if (string.Equals(arg, "config", StringComparison.OrdinalIgnoreCase))
            ToggleSettings();
        else
            ToggleMain();
    }

    private static void SaveThemes()
    {
        C.SelectedThemeId = MirageThemes.SelectedId;
        C.Save();
    }

    private static void SaveLanguage(string language)
    {
        C.UiLanguage = language;
        C.Save();
    }

    private static void ApplyUiLanguage()
    {
        var language = C.UiLanguage;
        if (string.IsNullOrEmpty(language))
        {
            language = PluginServices.ClientState.ClientLanguage == ClientLanguage.Japanese
                ? MirageUi.I18n.Japanese
                : MirageUi.I18n.English;
        }

        MirageUi.I18n.SetLanguage(language);
    }

    private void ToggleMain() => _window.TogglePage(MainWindow.LibraryPageId);

    private void ToggleSettings() => _window.TogglePage(MainWindow.SettingsPageId);
}
