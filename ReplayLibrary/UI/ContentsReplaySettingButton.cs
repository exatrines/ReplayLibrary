using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ReplayLibrary.UI;

/// <summary>Button on the left of the ContentsReplaySetting close control. Clicking opens the library.</summary>
internal static unsafe class ContentsReplaySettingButton
{
    private const int CloseButtonIndex = 2;

    internal static void Draw(MainWindow window)
    {
        if (PluginServices.GameGui.GameUiHidden)
            return;

        var addon = (AtkUnitBase*)PluginServices.GameGui.GetAddonByName("ContentsReplaySetting", 1).Address;
        if (addon == null || !addon->IsVisible)
            return;

        var windowNode = addon->WindowNode;
        if (windowNode == null || windowNode->Component == null)
            return;

        var component = (AtkComponentWindow*)windowNode->Component;
        var nodeIds = component->NodeIds;
        if (nodeIds.Length <= CloseButtonIndex)
            return;

        var close = component->GetNodeById((uint)nodeIds[CloseButtonIndex]);
        if (close == null || !close->IsVisible())
            return;

        var scaleX = close->GetScaleX();
        var scaleY = close->GetScaleY();
        var closeWidth = close->GetWidth() * scaleX;
        var closeHeight = close->GetHeight() * scaleY;
        if (closeWidth <= 0 || closeHeight <= 0)
            return;

        using var font = MirageUi.PushFont(MirageUi.FontSize.Default);
        var label = LibraryText.Get("contentsReplay.open");
        var buttonSize = new Vector2(MirageUi.ButtonWidth(label), closeHeight);
        var gap = 4f * scaleX;
        var viewport = ImGui.GetMainViewport();
        var pos = viewport.Pos + new Vector2(close->ScreenX - gap - buttonSize.X, close->ScreenY);

        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(buttonSize);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoBackground;
        if (ImGui.Begin("##replayLibraryContentsReplay", flags)
            && MirageUi.PrimaryButton(label, width: buttonSize.X, height: closeHeight, id: "contents-replay-open"))
            window.OpenLibrary();

        ImGui.End();
        ImGui.PopStyleVar(2);
    }
}
