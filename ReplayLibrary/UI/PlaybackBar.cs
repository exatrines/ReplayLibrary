using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ReplayLibrary.Playback;

namespace ReplayLibrary.UI;

/// <summary>Playback controls. Pinned, they sit on the game replay bar. Unpinned, they are a Mirage window.</summary>
internal static unsafe class PlaybackBar
{
    private const int RestartDelayMs = 12000;
    private const string EndPopup = "playback-end";
    private static readonly float[] Presets = [0.5f, 1f, 2f, 5f, 10f, 20f];

    internal static readonly PlaybackWindow Floating = new();

    private enum Page
    {
        Settings,
        Camera,
        Characters,
    }

    private static bool _expanded = true;
    private static Page _page = Page.Settings;
    private static Vector2 _endPopupPos;
    private static bool _seekHold;
    private static int _seekPreview;
    private static float _topHeight = 48f;
    private static float _footerHeight = 96f;

    internal static void Prepare() =>
        Floating.IsOpen = !C.PinPlaybackBar && TryDock(out _, out _);

    internal static void DrawDocked()
    {
        if (!C.PinPlaybackBar || !TryDock(out var dock, out var width))
            return;

        var colors = MirageTheme.ResolveAppliedColors();
        var height = PageHeight();
        var viewport = ImGui.GetMainViewport();
        using (MirageTheme.PushCustom(colors))
        {
            ImGui.SetNextWindowPos(dock, ImGuiCond.Always, new Vector2(0f, 1f));
            ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
            ImGui.SetNextWindowViewport(viewport.ID);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, colors.PageBg);
            if (ImGui.Begin(
                    "Playback###replaylibrary-playback-dock",
                    ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoNav))
            {
                MirageWindowHeader.DrawBackdrop();
                DrawPage(ImDrawFlags.RoundCornersAll, height);
            }

            ImGui.End();
            ImGui.PopStyleColor();
        }
    }

    internal static float PageHeight()
    {
        var scale = MirageWindowHeader.BarHeight / 36f;
        var pane = _expanded ? PaneHeight(MirageUi.ResolveControlHeight()) : 0f;
        return _topHeight + pane + _footerHeight + 10f * scale * 2f;
    }

    private static float PaneHeight(float button)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        return button * 4f + spacing * 7f;
    }

    internal static void DrawPage(ImDrawFlags corners, float pageHeight)
    {
        MirageWindowHeader.FillContentPage(corners);
        var scale = MirageWindowHeader.BarHeight / 36f;
        var pad = new Vector2(12f, 10f) * scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, pad);
        ImGui.BeginChild(
            "##playback-page",
            new Vector2(ImGui.GetWindowSize().X, MathF.Max(1f, pageHeight)),
            false,
            ImGuiWindowFlags.AlwaysUseWindowPadding
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse);
        DrawBody(PlaybackReader.Stalled());
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    internal static bool TryDock(out Vector2 dock, out float width)
    {
        dock = default;
        width = 0f;
        if (!PlaybackService.IsPlaying || HiddenByGame())
            return false;

        var addon = (AtkUnitBase*)PluginServices.GameGui.GetAddonByName("ContentsReplayPlayer", 1).Address;
        if (addon == null || addon->RootNode == null)
            return false;

        var scale = addon->Scale;
        var pad = scale * 8f;
        width = MathF.Max(120f, addon->RootNode->GetWidth() * scale - pad * 2f);
        dock = new Vector2(addon->X + pad, addon->Y + pad) + ImGui.GetMainViewport().Pos;
        return true;
    }

    private static void DrawBody(bool stalled)
    {
        var origin = ImGui.GetCursorPos();
        var button = MirageUi.ResolveControlHeight();
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var width = ImGui.GetContentRegionAvail().X;
        var pane = _expanded
            ? MathF.Max(1f, ImGui.GetContentRegionAvail().Y - _footerHeight - _topHeight)
            : 0f;
        var top = DrawTopBar(button);
        _topHeight = top;

        if (_expanded)
            DrawSettingsPane(origin, top, button, gap, width, pane, stalled);

        DrawFooter(new Vector2(origin.X, origin.Y + top + pane));
    }

    private static void DrawSettingsPane(
        Vector2 origin,
        float top,
        float button,
        float gap,
        float width,
        float pane,
        bool stalled)
    {
        const float line = 1f;
        var contentX = origin.X + button + gap + line + gap;
        ImGui.SetCursorPos(new Vector2(origin.X + button + gap, origin.Y + top));
        DrawVerticalSeparator(pane);

        ImGui.SetCursorPos(new Vector2(contentX, origin.Y + top));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild(
            "##playback-main",
            new Vector2(MathF.Max(1f, width - (contentX - origin.X)), pane),
            false);
        DrawMain(stalled);
        ImGui.EndChild();
        ImGui.PopStyleVar();

        ImGui.SetCursorPos(new Vector2(origin.X, origin.Y + top));
        DrawRail();
    }

    private static void DrawFooter(Vector2 cursor)
    {
        ImGui.SetCursorPos(cursor);
        var y = ImGui.GetCursorPosY();
        if (_expanded)
            MirageUi.PaddedSeparator();
        DrawTime();
        DrawSpeed();
        _footerHeight = MathF.Max(1f, ImGui.GetCursorPosY() - y);
    }

    private static float DrawTopBar(float button)
    {
        var start = ImGui.GetCursorPosY();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        if (TopButton(FontAwesomeIcon.Cog, "playback-gear", _expanded ? "Hide settings" : "Show settings", _expanded))
            _expanded = !_expanded;
        ImGui.SameLine(0f, spacing);
        DrawVerticalSeparator(button);
        ImGui.SameLine(0f, spacing);
        if (TopButton(FontAwesomeIcon.Users, "playback-gpose", "Group pose"))
            PlaybackHooks.EnterGroupPose();
        ImGui.SameLine();
        if (TopButton(FontAwesomeIcon.Crosshairs, "playback-idle", "Idle camera on the focus target"))
            PlaybackHooks.EnterIdleCamera();
        ImGui.SameLine();
        DrawWaymarks();

        var endX = ImGui.GetContentRegionMax().X - button * 2f - spacing;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), endX));
        DrawPin();
        ImGui.SameLine();
        if (TopButton(FontAwesomeIcon.FastForward, "playback-end", "Skip to the end"))
            ImGui.OpenPopup(EndPopup);
        _endPopupPos = new Vector2(ImGui.GetItemRectMax().X, ImGui.GetItemRectMin().Y);
        DrawEndPopup();
        DrawFullSeparator();
        return MathF.Max(1f, ImGui.GetCursorPosY() - start);
    }

    private static void DrawMain(bool stalled)
    {
        if (!PlaybackReader.IsChapterLoading)
        {
            switch (_page)
            {
                case Page.Camera:
                    CameraLockPanel.Draw();
                    break;
                case Page.Characters:
                    EntityOverlayPanel.Draw();
                    break;
                default:
                    DrawSettings();
                    break;
            }
        }

        if (stalled && MirageUi.PrimaryButton("Unstuck", id: "playback-unstuck"))
            PlaybackReader.SkipPacket();
    }

    private static void DrawRail()
    {
        if (RailButton(FontAwesomeIcon.Play, "playback-settings", "Playback settings", _page == Page.Settings))
            _page = Page.Settings;
        if (RailButton(
                FontAwesomeIcon.Camera,
                "playback-camera",
                LibraryText.Get("camera.lock"),
                _page == Page.Camera || C.LockCamera))
            _page = Page.Camera;
        if (RailButton(
                FontAwesomeIcon.UserFriends,
                "playback-characters",
                LibraryText.Get("overlay.title"),
                _page == Page.Characters))
            _page = Page.Characters;
    }

    private static void DrawWaymarks()
    {
        var shown = PlaybackHooks.WaymarksVisible;
        if (TopButton(
                FontAwesomeIcon.MapMarker,
                "playback-waymarks",
                shown ? "Hide waymarks" : "Show waymarks",
                shown)
            && PlaybackHooks.ToggleWaymarks())
        {
            C.ShowWaymarks = PlaybackHooks.WaymarksVisible;
            C.Save();
        }
    }

    private static void DrawPin()
    {
        var pinned = C.PinPlaybackBar;
        if (!TopButton(
                FontAwesomeIcon.Thumbtack,
                "playback-pin",
                LibraryText.Get(pinned ? "playback.pin.detach" : "playback.pin.follow"),
                pinned))
            return;

        C.PinPlaybackBar = !pinned;
        if (pinned)
            Floating.PlaceAtReplayBar();

        C.Save();
    }

    private static bool TopButton(FontAwesomeIcon icon, string id, string tooltip, bool accent = false) =>
        ToolButton(icon, id, tooltip, accent, MirageTooltipPosition.Below);

    private static bool RailButton(FontAwesomeIcon icon, string id, string tooltip, bool accent = false) =>
        ToolButton(icon, id, tooltip, accent, MirageTooltipPosition.Right);

    private static bool ToolButton(
        FontAwesomeIcon icon,
        string id,
        string tooltip,
        bool accent,
        MirageTooltipPosition tooltipPosition) =>
        MirageUi.IconButton(
            icon,
            id: id,
            tooltip: tooltip,
            tooltipPosition: tooltipPosition,
            color: accent ? MirageUi.GetColor(MirageUi.Color.Accent) : null);

    private static void DrawVerticalSeparator(float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var inset = 4f;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(pos.X, pos.Y + inset),
            new Vector2(pos.X, pos.Y + height - inset),
            ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(1f, height));
    }

    private static void DrawFullSeparator()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        ImGui.Dummy(new Vector2(1f, spacing));
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(
            pos,
            new Vector2(pos.X + width, pos.Y),
            ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(MathF.Max(1f, width), 1f));
    }

    private static void DrawEndPopup()
    {
        if (!ImGui.IsPopupOpen(EndPopup))
            return;

        var gap = ImGui.GetStyle().ItemSpacing.Y;
        ImGui.SetNextWindowPos(new Vector2(_endPopupPos.X, _endPopupPos.Y - gap), ImGuiCond.Always, new Vector2(1f, 1f));
        if (!ImGui.BeginPopup(EndPopup))
            return;

        if (MirageUi.PrimaryButton("Skip to the end", id: "playback-end-go"))
        {
            PlaybackReader.SkipToEnd();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawSettings()
    {
        var hideName = C.HideOwnName;
        if (MirageUi.Checkbox("Hide own name. Applies when the replay starts.", ref hideName))
        {
            C.HideOwnName = hideName;
            PlaybackHooks.SetHideName(hideName);
            C.Save();
        }

        var quick = C.QuickChapterLoad;
        if (MirageUi.Checkbox("Quick chapter load", ref quick))
        {
            C.QuickChapterLoad = quick;
            C.Save();
        }

        var jump = C.JumpToTime;
        if (MirageUi.Checkbox("Right click jumps to the time", ref jump))
        {
            C.JumpToTime = jump;
            C.Save();
        }

        MirageUi.Tooltip("Jumping skips packets, so the scene can look wrong.");

        var rate = (int)MathF.Round(C.SeekRate);
        if (MirageUi.DragInt("Loading speed", ref rate, speed: 10f, min: 100, max: 2000, id: "loading-speed"))
        {
            C.SeekRate = rate;
            C.Save();
        }

        MirageUi.Tooltip("Raising this can break fights that change the arena.");

        var showSpeeds = C.ShowSpeedButtons;
        if (MirageUi.Checkbox("Show speed buttons", ref showSpeeds))
        {
            C.ShowSpeedButtons = showSpeeds;
            C.Save();
        }

        var preset = C.SpeedPreset;
        if (MirageUi.DragFloat("Speed preset", ref preset, speed: 0.05f, min: 0.05f, max: 60f, format: "%.2f", id: "speed-preset"))
        {
            C.SpeedPreset = preset;
            C.Save();
        }
    }

    private static void DrawTime()
    {
        if (!PlaybackReader.TryPull(out var start, out var end, out var position))
            return;

        var max = Math.Max(start + 1, end - RestartDelayMs);
        var shown = _seekHold ? _seekPreview : Math.Clamp(position, start, max);
        var label = Clock(shown - start);
        ImGui.BeginDisabled(PlaybackReader.IsChapterLoading);
        if (MirageUi.SliderInt("", ref shown, start, max, id: "playback-time", format: label))
        {
            _seekHold = true;
            _seekPreview = shown;
        }

        if (ImGui.IsItemDeactivated() && _seekHold)
        {
            PlaybackReader.Seek((uint)_seekPreview);
            _seekHold = false;
        }

        if (ImGui.IsItemHovered())
        {
            var bar = Math.Max(1f, ImGui.GetItemRectSize().X);
            var ratio = Math.Clamp((ImGui.GetMousePos().X - ImGui.GetItemRectMin().X) / bar, 0f, 1f);
            var ms = start + (int)((max - start) * ratio);
            ImGui.SetTooltip(Clock(ms - start));
            if (C.JumpToTime && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                PlaybackReader.Jump((uint)ms);
        }

        ImGui.EndDisabled();
    }

    private static void DrawSpeed()
    {
        var speed = PlaybackService.PlaybackSpeed;
        var shown = speed;
        if (MirageUi.DragFloat("", ref shown, speed: 0.05f, min: 0.05f, max: 60f, format: "%.2fx", id: "playback-speed")
            && Math.Abs(shown - speed) > 0.001f)
            PlaybackService.SetSpeed(shown);

        if (!C.ShowSpeedButtons)
            return;

        var count = Presets.Length + 1;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var inner = MathF.Max(count, MathF.Round(ImGui.GetContentRegionAvail().X - spacing * (count - 1)));
        var width = MathF.Floor(inner / count);
        var extra = (int)(inner - width * count);
        for (var i = 0; i < count; i++)
        {
            if (i != 0)
                ImGui.SameLine(0f, spacing);
            var preset = i < Presets.Length ? Presets[i] : C.SpeedPreset;
            var id = i < Presets.Length ? $"speed-{i}" : "speed-custom";
            DrawPreset(preset, id, width + (i < extra ? 1f : 0f));
        }
    }

    private static void DrawPreset(float speed, string id, float width)
    {
        var label = SpeedText(speed);
        var height = MirageUi.ResolveControlHeight();
        if (MirageUi.PrimaryButton("", width: width, id: id, height: height))
            PlaybackService.SetSpeed(Math.Abs(PlaybackService.PlaybackSpeed - speed) < 0.001f ? 1f : speed);

        var origin = LabelOrigin(ImGui.GetItemRectMin(), ImGui.GetItemRectSize(), label);
        ImGui.GetWindowDrawList().AddText(origin, ImGui.GetColorU32(ImGuiCol.Text), label);
    }

    // Center the painted glyphs. The font advance box sits off the ink, so "1x" looks shifted in a narrow button.
    private static Vector2 LabelOrigin(Vector2 boxMin, Vector2 boxSize, string label)
    {
        var font = ImGui.GetFont();
        var scale = ImGui.GetFontSize() / font.FontSize;
        var left = float.MaxValue;
        var right = float.MinValue;
        var top = float.MaxValue;
        var bottom = float.MinValue;
        for (var i = 0; i < label.Length; i++)
        {
            var glyph = font.FindGlyph(label[i]);
            if (glyph == null)
                continue;

            var pen = i == 0 ? 0f : ImGui.CalcTextSize(label[..i]).X;
            left = MathF.Min(left, pen + glyph->X0 * scale);
            right = MathF.Max(right, pen + glyph->X1 * scale);
            top = MathF.Min(top, glyph->Y0 * scale);
            bottom = MathF.Max(bottom, glyph->Y1 * scale);
        }

        if (left > right)
            return boxMin + (boxSize - ImGui.CalcTextSize(label)) * 0.5f;

        var origin = boxMin + boxSize * 0.5f - new Vector2(left + right, top + bottom) * 0.5f;
        return new Vector2(
            MathF.Round(origin.X, MidpointRounding.AwayFromZero),
            MathF.Round(origin.Y, MidpointRounding.AwayFromZero));
    }

    private static bool HiddenByGame()
    {
        if (PluginServices.GameGui.GameUiHidden || PluginServices.Condition[ConditionFlag.WatchingCutscene])
            return true;

        return PluginServices.GameGui.GetAddonByName("TalkSubtitle", 1).Address != 0;
    }

    private static string Clock(int ms)
    {
        if (ms < 0)
            ms = 0;

        var span = TimeSpan.FromMilliseconds(ms);
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string SpeedText(float speed) =>
        speed == 1f ? "1x" : speed.ToString("0.##") + "x";
}

/// <summary>Detached playback controls, drawn with the Mirage window header.</summary>
internal sealed class PlaybackWindow : Window
{
    private bool _collapsed;
    private bool _placed;
    private bool _ready;
    private IDisposable? _theme;

    internal PlaybackWindow() : base("Playback###replaylibrary-playback")
    {
        RespectCloseHotkey = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = false;
        Flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav;
    }

    internal void PlaceAtReplayBar() => _placed = false;

    public override void PreDraw()
    {
        _ready = false;
        if (!PlaybackBar.TryDock(out var dock, out var width))
            return;

        var height = _collapsed
            ? MirageWindowHeader.BarHeight
            : MirageWindowHeader.BarHeight + PlaybackBar.PageHeight();
        if (!_placed)
        {
            Position = new Vector2(dock.X, dock.Y - height);
            PositionCondition = ImGuiCond.Always;
            _placed = true;
        }

        var size = new Vector2(width, height);
        Size = size;
        SizeCondition = ImGuiCond.Always;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = size,
            MaximumSize = size,
        };
        _theme = MirageTheme.PushCustom(MirageTheme.ResolveAppliedColors());
        _ready = true;
    }

    public override void Draw()
    {
        if (!_ready)
            return;

        var page = string.Empty;
        MirageWindowHeader.Draw(
            this,
            LibraryText.Get("playback.window"),
            ref _collapsed,
            0f,
            ref page,
            chrome: MirageWindowHeader.Chrome.Menu | MirageWindowHeader.Chrome.Collapse);
        if (_collapsed)
        {
            MirageWindowHeader.DragClientArea(this);
            return;
        }

        ImGui.SetCursorPos(new Vector2(0f, MirageWindowHeader.BarHeight));
        PlaybackBar.DrawPage(ImDrawFlags.RoundCornersBottom, PlaybackBar.PageHeight());
        MirageWindowHeader.DragClientArea(this);
    }

    public override void PostDraw()
    {
        if (!_ready)
            return;

        MirageWindowCapture.Copy();
        _theme?.Dispose();
        _theme = null;
        _ready = false;
    }
}
