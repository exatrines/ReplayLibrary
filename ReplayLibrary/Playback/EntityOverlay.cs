using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ReplayLibrary.Playback;

/// <summary>Hides player, pet, NPC, and enemy models and draws dummy markers in their place.</summary>
internal sealed unsafe class EntityOverlay
{
    private const int ReadyFrames = 5;
    private const int ObjectSlotCount = 819;
    private const uint InvalidOwnerId = 0xE0000000;
    private const uint LabelOutline = 0xE0000000;
    private const string NamePlateAddon = "NamePlate";
    private const VisibilityFlags HiddenRenderMask = VisibilityFlags.Model | VisibilityFlags.Nameplate;

    private readonly HashSet<ushort> _hidden = [];
    private readonly HashSet<ushort> _hiddenScratch = [];
    private readonly HashSet<ushort> _dummies = [];
    private readonly HashSet<uint> _playerIds = [];
    private string[]? _jobAbbreviations;
    private int _readyFrames;
    private bool _applying;
    private ushort _namePlateId;
    private bool _namePlateHidden;

    internal void Update()
    {
        if (!PluginServices.ClientState.IsLoggedIn)
        {
            Restore();
            return;
        }

        if (!WantsApply())
        {
            Restore();
            return;
        }

        if (!CanApply())
        {
            _readyFrames = 0;
            if (_applying)
                Restore();
            return;
        }

        if (!_applying)
        {
            _readyFrames++;
            if (_readyFrames < ReadyFrames)
                return;
        }

        _applying = true;
        HideModels();
        HideNamePlates();
    }

    internal void Draw()
    {
        if (!_applying || _dummies.Count == 0)
            return;

        var manager = GameObjectManager.Instance();
        if (manager == null)
            return;

        var gui = PluginServices.GameGui;
        var draw = ImGui.GetBackgroundDrawList();
        var size = Math.Clamp(C.DummySize, 0.5f, 4f);
        for (var i = 0; i < ObjectSlotCount; i++)
        {
            var native = manager->Objects.IndexSorted[i].Value;
            if (native == null || !_dummies.Contains(native->ObjectIndex))
                continue;

            DrawDummy(draw, gui, native, size);
        }
    }

    internal void Restore()
    {
        _readyFrames = 0;
        _applying = false;
        RestoreModels();
        RestoreNamePlates();
    }

    private static bool CanApply() =>
        IsLocalPlayerDrawable() && (!PlaybackService.IsPlaying || IsReplayUiReady());

    private void HideModels()
    {
        var manager = GameObjectManager.Instance();
        if (manager == null)
            return;

        _playerIds.Clear();
        for (var i = 0; i < ObjectSlotCount; i++)
        {
            var native = manager->Objects.IndexSorted[i].Value;
            if (native != null && native->ObjectKind == ObjectKind.Pc)
                _playerIds.Add(native->EntityId);
        }

        _hiddenScratch.Clear();
        _dummies.Clear();
        for (var i = 0; i < ObjectSlotCount; i++)
        {
            var native = manager->Objects.IndexSorted[i].Value;
            if (native == null)
                continue;

            var mode = ModeFor(native);
            if (!HidesModel(mode))
            {
                if (_hidden.Contains(native->ObjectIndex))
                    SetHidden(native, false);
                continue;
            }

            if (!IsDrawn(native) && !_hidden.Contains(native->ObjectIndex))
                continue;

            SetHidden(native, true);
            _hiddenScratch.Add(native->ObjectIndex);
            if (mode == EntityDisplayMode.ReplaceToDummy)
                _dummies.Add(native->ObjectIndex);
        }

        _hidden.Clear();
        foreach (var index in _hiddenScratch)
            _hidden.Add(index);
    }

    private void RestoreModels()
    {
        _dummies.Clear();
        if (_hidden.Count == 0)
            return;

        var manager = GameObjectManager.Instance();
        if (manager != null)
        {
            for (var i = 0; i < ObjectSlotCount; i++)
            {
                var native = manager->Objects.IndexSorted[i].Value;
                if (native != null && _hidden.Contains(native->ObjectIndex))
                    SetHidden(native, false);
            }
        }

        _hidden.Clear();
    }

    private void HideNamePlates()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
            return;

        var addon = manager->GetAddonByName(NamePlateAddon);
        if (addon == null)
            return;

        if (addon->IsVisible)
        {
            manager->SetAddonVisibility(addon->Id, false);
            _namePlateId = addon->Id;
        }

        _namePlateHidden = true;
    }

    private void RestoreNamePlates()
    {
        if (!_namePlateHidden)
            return;

        var manager = RaptureAtkUnitManager.Instance();
        if (manager != null && _namePlateId != 0)
            manager->SetAddonVisibility(_namePlateId, true);

        _namePlateId = 0;
        _namePlateHidden = false;
    }

    private void DrawDummy(ImDrawListPtr draw, IGameGui gui, GameObject* native, float size)
    {
        var position = DrawPosition(native);
        if (!gui.WorldToScreen(position, out var feet))
            return;

        var height = native->GetHeight();
        if (height < 0.4f)
            height = 2f;

        var worldRadius = height * 0.12f * size;
        if (native->ObjectKind != ObjectKind.Pc)
            worldRadius *= 0.65f;

        var radius = ScreenRadius(gui, position, worldRadius);
        if (radius < 1.5f)
            radius = 1.5f;

        var color = ColorFor(native);
        var headWorld = position with { Y = position.Y + height };
        if (gui.WorldToScreen(headWorld, out var head))
        {
            draw.AddLine(feet, head, color, Math.Max(1.5f, radius * 0.18f));
            draw.AddCircleFilled(head, radius * 0.55f, color);
            DrawLabel(draw, native, head, radius, color);
        }

        draw.AddCircleFilled(feet, radius, color);
        draw.AddCircle(feet, radius, 0xE0FFFFFF, 12, 1.25f);
    }

    private void DrawLabel(ImDrawListPtr draw, GameObject* native, Vector2 head, float radius, uint color)
    {
        var text = LabelFor(native);
        if (string.IsNullOrEmpty(text))
            return;

        var size = ImGui.CalcTextSize(text);
        var pos = new Vector2(head.X - size.X * 0.5f, head.Y - radius - size.Y - 2f);
        var fill = color | 0xFF000000;
        draw.AddText(pos + new Vector2(1f, 0f), LabelOutline, text);
        draw.AddText(pos + new Vector2(-1f, 0f), LabelOutline, text);
        draw.AddText(pos + new Vector2(0f, 1f), LabelOutline, text);
        draw.AddText(pos + new Vector2(0f, -1f), LabelOutline, text);
        draw.AddText(pos, fill, text);
    }

    private string? LabelFor(GameObject* native)
    {
        if (IsEnemy(native) || IsNpc(native))
        {
            var name = native->GetName().ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        if (native->ObjectKind != ObjectKind.Pc || C.PlayerDummyLabel == PlayerDummyLabelMode.None)
            return null;

        var jobId = ((Character*)native)->ClassJob;
        if (C.PlayerDummyLabel == PlayerDummyLabelMode.Job)
            return JobAbbreviation(jobId);

        return RoleName(RoleForJob(jobId));
    }

    private string JobAbbreviation(byte jobId)
    {
        var abbreviations = JobAbbreviations();
        if (jobId < abbreviations.Length && !string.IsNullOrEmpty(abbreviations[jobId]))
            return abbreviations[jobId];

        return RoleName(RoleForJob(jobId));
    }

    private string[] JobAbbreviations()
    {
        if (_jobAbbreviations != null)
            return _jobAbbreviations;

        var names = new string[64];
        var sheet = PluginServices.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English);
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || row.RowId >= names.Length)
                    continue;

                var abbreviation = row.Abbreviation.ToString();
                if (!string.IsNullOrWhiteSpace(abbreviation))
                    names[row.RowId] = abbreviation;
            }
        }

        _jobAbbreviations = names;
        return names;
    }

    private EntityDisplayMode ModeFor(GameObject* native)
    {
        if (native->ObjectKind == ObjectKind.Pc)
            return C.PlayerMode;

        if (IsPlayerOwnedPet(native))
            return C.PetMode;

        if (IsEnemy(native))
            return C.EnemyMode;

        if (IsNpc(native))
            return C.NpcMode;

        return EntityDisplayMode.DoNothing;
    }

    private bool IsPlayerOwnedPet(GameObject* native)
    {
        if (native->ObjectKind != ObjectKind.BattleNpc)
            return false;

        var sub = native->BattleNpcSubKind;
        if (sub is not (BattleNpcSubKind.Pet or BattleNpcSubKind.Buddy))
            return false;

        var ownerId = native->OwnerId;
        if (ownerId == 0 || ownerId == InvalidOwnerId)
            return false;

        return _playerIds.Contains(ownerId);
    }

    private static bool IsNpc(GameObject* native) =>
        native->ObjectKind == ObjectKind.EventNpc
        || (native->ObjectKind == ObjectKind.BattleNpc
            && native->BattleNpcSubKind == BattleNpcSubKind.NpcPartyMember);

    private static bool IsEnemy(GameObject* native) =>
        native->ObjectKind == ObjectKind.BattleNpc
        && native->BattleNpcSubKind is BattleNpcSubKind.Combatant or BattleNpcSubKind.BNpcPart;

    private static uint ColorFor(GameObject* native)
    {
        if (IsEnemy(native))
            return ImGui.ColorConvertFloat4ToU32(C.DummyColorEnemy);

        if (IsNpc(native))
            return ImGui.ColorConvertFloat4ToU32(C.DummyColorNpc);

        if (native->ObjectKind != ObjectKind.Pc)
            return ImGui.ColorConvertFloat4ToU32(C.DummyColorPet);

        return RoleForJob(((Character*)native)->ClassJob) switch
        {
            DummyRole.Tank => ImGui.ColorConvertFloat4ToU32(C.DummyColorTank),
            DummyRole.Healer => ImGui.ColorConvertFloat4ToU32(C.DummyColorHealer),
            DummyRole.Crafter => ImGui.ColorConvertFloat4ToU32(C.DummyColorCrafter),
            DummyRole.Gatherer => ImGui.ColorConvertFloat4ToU32(C.DummyColorGatherer),
            _ => ImGui.ColorConvertFloat4ToU32(C.DummyColorDps),
        };
    }

    private static string RoleName(DummyRole role) => role switch
    {
        DummyRole.Tank => LibraryText.Get("overlay.role.tank"),
        DummyRole.Healer => LibraryText.Get("overlay.role.healer"),
        DummyRole.Crafter => LibraryText.Get("overlay.role.crafter"),
        DummyRole.Gatherer => LibraryText.Get("overlay.role.gatherer"),
        _ => LibraryText.Get("overlay.role.dps"),
    };

    private static DummyRole RoleForJob(byte jobId) => jobId switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => DummyRole.Tank,
        6 or 24 or 28 or 33 or 40 => DummyRole.Healer,
        >= 8 and <= 15 => DummyRole.Crafter,
        >= 16 and <= 18 => DummyRole.Gatherer,
        _ => DummyRole.Dps,
    };

    private static void SetHidden(GameObject* native, bool hide)
    {
        if (native->IsCharacter())
        {
            native->RenderFlags &= ~VisibilityFlags.Model;
            native->RenderFlags &= ~VisibilityFlags.Nameplate;
            var character = (Character*)native;
            if (hide)
                character->Alpha = 0f;
            else if (character->Alpha == 0f)
                character->Alpha = 1f;
            return;
        }

        if (hide)
        {
            native->RenderFlags |= VisibilityFlags.Model;
            native->RenderFlags &= ~VisibilityFlags.Nameplate;
            return;
        }

        native->RenderFlags &= ~HiddenRenderMask;
    }

    private static bool WantsApply() =>
        PlaybackService.IsPlaying
        && (HidesModel(C.PlayerMode)
            || HidesModel(C.PetMode)
            || HidesModel(C.NpcMode)
            || HidesModel(C.EnemyMode));

    private static bool HidesModel(EntityDisplayMode mode) =>
        mode is EntityDisplayMode.Hide or EntityDisplayMode.ReplaceToDummy;

    private static bool IsDrawn(GameObject* native) =>
        native->DrawObject != null;

    private static bool IsLocalPlayerDrawable()
    {
        var manager = GameObjectManager.Instance();
        if (manager == null)
            return false;

        var local = manager->Objects.IndexSorted[0].Value;
        return local != null && local->ObjectKind == ObjectKind.Pc && local->DrawObject != null;
    }

    private static bool IsReplayUiReady()
    {
        var addon = (AtkUnitBase*)PluginServices.GameGui.GetAddonByName("ContentsReplayPlayer", 1).Address;
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private static Vector3 DrawPosition(GameObject* native)
    {
        var drawObject = native->DrawObject;
        if (drawObject != null)
        {
            var drawn = drawObject->Position;
            return new Vector3(drawn.X, drawn.Y, drawn.Z);
        }

        var fallback = native->Position;
        return new Vector3(fallback.X, fallback.Y, fallback.Z);
    }

    private static float ScreenRadius(IGameGui gui, Vector3 origin, float worldRadius)
    {
        if (!gui.WorldToScreen(origin, out var center))
            return 0f;

        var radius = 0f;
        if (gui.WorldToScreen(origin + new Vector3(worldRadius, 0f, 0f), out var alongX))
            radius = Math.Max(radius, Vector2.Distance(center, alongX));
        if (gui.WorldToScreen(origin + new Vector3(0f, worldRadius, 0f), out var alongY))
            radius = Math.Max(radius, Vector2.Distance(center, alongY));
        if (gui.WorldToScreen(origin + new Vector3(0f, 0f, worldRadius), out var alongZ))
            radius = Math.Max(radius, Vector2.Distance(center, alongZ));
        return radius;
    }

    private enum DummyRole
    {
        Tank,
        Healer,
        Dps,
        Crafter,
        Gatherer,
    }
}
