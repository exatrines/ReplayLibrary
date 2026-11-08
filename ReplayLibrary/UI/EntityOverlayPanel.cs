namespace ReplayLibrary.UI;

/// <summary>Character hide and dummy settings on the playback bar.</summary>
internal static class EntityOverlayPanel
{
    private const float LabelWidth = 88f;

    internal static void Draw()
    {
        Mode("overlay.player", "overlay.player.hint", C.PlayerMode, value => C.PlayerMode = value);
        Mode("overlay.pets", "overlay.pets.hint", C.PetMode, value => C.PetMode = value);
        Mode("overlay.npc", "overlay.npc.hint", C.NpcMode, value => C.NpcMode = value);
        Mode("overlay.enemy", "overlay.enemy.hint", C.EnemyMode, value => C.EnemyMode = value);

        var size = C.DummySize;
        if (MirageUi.SliderFloat(
                LibraryText.Get("overlay.size"),
                ref size,
                0.5f,
                4f,
                "%.1f",
                "overlay-size",
                labelWidth: LabelWidth))
        {
            C.DummySize = size;
            C.Save();
        }

        var labels = new[]
        {
            LibraryText.Get("overlay.label.none"),
            LibraryText.Get("overlay.label.role"),
            LibraryText.Get("overlay.label.job"),
        };
        if (Pick("overlay.label", labels, (int)C.PlayerDummyLabel, out var label) && label != (int)C.PlayerDummyLabel)
        {
            C.PlayerDummyLabel = (PlayerDummyLabelMode)label;
            C.Save();
        }

        DrawColors();
    }

    private static void DrawColors()
    {
        var tank = C.DummyColorTank;
        var healer = C.DummyColorHealer;
        var dps = C.DummyColorDps;
        var crafter = C.DummyColorCrafter;
        var changed = Swatch("overlay.color.tank", ref tank);
        ImGui.SameLine();
        changed |= Swatch("overlay.color.healer", ref healer);
        ImGui.SameLine();
        changed |= Swatch("overlay.color.dps", ref dps);
        ImGui.SameLine();
        changed |= Swatch("overlay.color.crafter", ref crafter);

        var gatherer = C.DummyColorGatherer;
        var pet = C.DummyColorPet;
        var npc = C.DummyColorNpc;
        var enemy = C.DummyColorEnemy;
        changed |= Swatch("overlay.color.gatherer", ref gatherer);
        ImGui.SameLine();
        changed |= Swatch("overlay.color.pet", ref pet);
        ImGui.SameLine();
        changed |= Swatch("overlay.color.npc", ref npc);
        ImGui.SameLine();
        changed |= Swatch("overlay.color.enemy", ref enemy);
        if (!changed)
            return;

        C.DummyColorTank = tank;
        C.DummyColorHealer = healer;
        C.DummyColorDps = dps;
        C.DummyColorCrafter = crafter;
        C.DummyColorGatherer = gatherer;
        C.DummyColorPet = pet;
        C.DummyColorNpc = npc;
        C.DummyColorEnemy = enemy;
        C.Save();
    }

    private static bool Swatch(string key, ref Vector4 color)
    {
        ImGui.SetNextItemWidth(ImGui.GetFrameHeight());
        var changed = ImGui.ColorEdit4($"##{key}", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar);
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(LibraryText.Get(key));
        return changed;
    }

    private static void Mode(string labelKey, string hintKey, EntityDisplayMode current, System.Action<EntityDisplayMode> set)
    {
        var labels = new[]
        {
            LibraryText.Get("overlay.mode.nothing"),
            LibraryText.Get("overlay.mode.hide"),
            LibraryText.Get("overlay.mode.dummy"),
        };
        if (Pick(labelKey, labels, (int)current, out var next) && next != (int)current)
        {
            set((EntityDisplayMode)next);
            C.Save();
        }

        MirageUi.Tooltip(LibraryText.Get(hintKey));
    }

    private static bool Pick(string labelKey, string[] labels, int current, out int next)
    {
        next = current;
        if (current < 0 || current >= labels.Length)
            return false;

        var selected = labels[current];
        if (!MirageUi.Dropdown(
                LibraryText.Get(labelKey),
                ref selected,
                labels,
                allowClear: false,
                id: labelKey,
                labelWidth: LabelWidth))
            return false;

        var index = Array.IndexOf(labels, selected);
        if (index < 0)
            return false;

        next = index;
        return true;
    }
}
