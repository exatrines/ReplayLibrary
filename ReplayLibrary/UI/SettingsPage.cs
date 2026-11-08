using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using ReplayLibrary.Library;

namespace ReplayLibrary.UI;

/// <summary>Content types, keep counts, and auto-delete.</summary>
internal sealed partial class MainWindow
{
    private readonly record struct KeepDuty(uint Id, string Name, uint PatchId, uint TypeId, ushort SortKey);

    private readonly record struct KeepTarget(byte Scope, uint TypeId, uint PatchId, uint ContentId)
    {
        public string Id => Scope switch
        {
            KeepCount.Content => $"c{ContentId}",
            KeepCount.Type => $"t{PatchId}-{TypeId}",
            KeepCount.Version => $"v{PatchId}",
            _ => "default",
        };
    }

    private System.Action? _pendingSettingsReset;

    private readonly List<KeepDuty> _keepDuties = [];
    private readonly List<(uint Id, string Name)> _keepPatches = [];
    private readonly HashSet<uint> _openKeepVersions = [];
    private readonly HashSet<(uint Patch, uint Type)> _openKeepTypes = [];
    private readonly List<(uint Id, uint IconId, string Name)> _recordTypes = [];
    private readonly Dictionary<string, string> _keepDrafts = [];
    private bool _recordTypesReady;
    private bool _keepTreeReady;

    private void DrawSettings()
    {
        if (_types.Count == 0)
            LoadTypes();

        MirageUi.TwoColumn.Draw(_settingsColumns, DrawSettingsPage);
    }

    private void DrawSettingsPage()
    {
        var page = _settingsColumns.SelectedId;
        if (page == AutoLockPageId)
            DrawAutoLock();
        else if (page == DeletedReplaysPageId)
            DrawDeletedSettings();
        else
            DrawContentTypes();
    }

    private void DrawSettingsHeader(
        string title,
        string resetId,
        System.Action reset,
        string? resetLabel = null,
        string? confirm = null)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5f * ImGuiHelpers.GlobalScale);
        MirageUi.Text(title, MirageUi.Color.Title, wrap: false, fontSize: MirageUi.FontSize.Large);
        var afterTitle = ImGui.GetCursorPos();
        var titleMin = ImGui.GetItemRectMin();
        var titleMax = ImGui.GetItemRectMax();

        var buttonLabel = resetLabel ?? LibraryText.Get("settings.reset");
        var width = MirageUi.IconLabelButtonWidth(FontAwesomeIcon.Undo, buttonLabel);
        var buttonHeight = MirageUi.ResolveControlHeight();
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorScreenPos(new Vector2(right - width, (titleMin.Y + titleMax.Y - buttonHeight) * 0.5f));
        var clicked = MirageUi.PrimaryIconLabelButton(
            FontAwesomeIcon.Undo,
            buttonLabel,
            width: width,
            id: resetId);

        ImGui.SetCursorPos(afterTitle);
        MirageUi.PaddedSeparator();
        if (clicked)
            AskSettingsReset(
                buttonLabel,
                confirm ?? LibraryText.Format("settings.reset.confirm", title),
                reset);
    }

    private void AskSettingsReset(string dialogTitle, string message, System.Action reset)
    {
        _pendingSettingsReset = reset;
        _dialog.Show(
            dialogTitle,
            message,
            FontAwesomeIcon.ExclamationTriangle,
            new MirageMessageButton { Label = LibraryText.Get("settings.reset.action"), Primary = true, OnClick = ConfirmSettingsReset },
            new MirageMessageButton { Label = LibraryText.Get("settings.cancel") });
    }

    private void ConfirmSettingsReset()
    {
        var reset = _pendingSettingsReset;
        _pendingSettingsReset = null;
        reset?.Invoke();
    }

    private void ResetContentTypes() =>
        C.SetRecordedContentTypes(Configuration.DefaultContentTypes);

    private void ResetAutoLock()
    {
        _keepDrafts.Clear();
        C.ResetKeepCounts();
        Catalog.MaintainLibrary(force: true);
        Refresh();
    }

    private void ResetAutoDelete()
    {
        C.DeletedKeepDays = 7;
        C.MaxLibraryGigabytes = 5;
        C.Save();
        if (Catalog.MaintainLibrary(force: true))
            Refresh();
    }

    private void DrawContentTypes()
    {
        DrawSettingsHeader(PageLabel(ContentTypesPageId), "reset-content-types", ResetContentTypes);
        MirageUi.Text(LibraryText.Get("contentTypes.intro"), MirageUi.Color.Secondary);
        EnsureRecordTypes();
        DrawFillCard("##contentTypes", DrawContentTypeList);
    }

    private void EnsureRecordTypes()
    {
        if (_recordTypesReady)
            return;

        _recordTypesReady = true;
        _recordTypes.Clear();
        foreach (var row in PluginServices.DataManager.GetExcelSheet<ContentType>())
        {
            var name = Catalog.ContentTypeLabel(row.RowId, row.Name.ToString());
            if (string.IsNullOrWhiteSpace(name))
                continue;

            _recordTypes.Add((
                row.RowId,
                row.Icon != 0 ? row.Icon : row.IconDutyFinder,
                name));
        }

        _recordTypes.Sort(static (left, right) => left.Id.CompareTo(right.Id));
    }

    private void DrawContentTypeList()
    {
        var selected = C.RecordedContentTypes.ToHashSet();
        var all = _recordTypes.Count > 0 && _recordTypes.All(row => selected.Contains(row.Id));
        var headerChanged = DrawContentTypeHeader(ref all);
        if (headerChanged)
        {
            selected.Clear();
            if (all)
            {
                foreach (var row in _recordTypes)
                    selected.Add(row.Id);
            }
        }

        MirageUi.PaddedSeparator();
        var snapshot = selected.ToHashSet();
        foreach (var row in _recordTypes)
            DrawContentTypeEntry(row, selected);

        if (headerChanged || !selected.SetEquals(snapshot))
            C.SetRecordedContentTypes(selected);
    }

    private static bool DrawContentTypeHeader(ref bool on)
    {
        var origin = ImGui.GetCursorScreenPos();
        var pad = LibraryRowPad();
        ImGui.SetCursorScreenPos(new Vector2(origin.X + pad, origin.Y));
        var changed = MirageUi.Checkbox("##contentTypeAll", ref on);
        var bottom = ImGui.GetItemRectMax().Y;
        MirageUi.Tooltip(on ? LibraryText.Get("contentTypes.clearAll") : LibraryText.Get("contentTypes.selectAll"));
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        MirageUi.Text(PageLabel(ContentTypesPageId), MirageUi.Color.Secondary, wrap: false);
        ImGui.SetCursorScreenPos(new Vector2(origin.X, bottom));
        return changed;
    }

    private static void DrawContentTypeEntry((uint Id, uint IconId, string Name) row, HashSet<uint> selected)
    {
        var on = selected.Contains(row.Id);
        if (!DrawContentTypeHit($"##type{row.Id}", ref on, row.IconId, $"{row.Id} {row.Name}"))
            return;

        if (on)
            selected.Add(row.Id);
        else
            selected.Remove(row.Id);
    }

    private static bool DrawContentTypeHit(string id, ref bool on, uint iconId, string label)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var box = LibraryContentIconSize();
        var pad = LibraryRowPad();
        var check = MirageUi.ResolveControlHeight();
        var content = MathF.Max(check, box);
        var height = content + pad * 2f;
        var toggled = ImGui.InvisibleButton($"{id}-row", new Vector2(width, height));
        var rowMax = origin + new Vector2(width, height);
        if (ImGui.IsMouseHoveringRect(origin, rowMax))
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                origin,
                rowMax,
                ImGui.GetColorU32(ImGuiCol.HeaderHovered),
                4f);
        }

        if (toggled)
            on = !on;

        ImGui.SetCursorScreenPos(new Vector2(origin.X + pad, origin.Y + pad + (content - check) * 0.5f));
        var shown = on;
        var changed = MirageUi.Checkbox(id, ref shown);
        if (!toggled && changed)
            on = shown;

        var checkMin = ImGui.GetItemRectMin();
        var checkHeight = ImGui.GetItemRectMax().Y - checkMin.Y;
        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SameLine(0f, gap);
        var iconMin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(box, checkHeight));
        PaintLibraryType(iconId, new Vector2(iconMin.X, checkMin.Y + (checkHeight - box) * 0.5f), box);
        ImGui.SameLine(0f, gap);
        ImGui.AlignTextToFramePadding();
        MirageUi.Text(label, wrap: false);
        ImGui.SetCursorScreenPos(new Vector2(origin.X, rowMax.Y));
        return toggled || changed;
    }

    private void DrawAutoLock()
    {
        EnsureKeepTree();
        DrawSettingsHeader(PageLabel(AutoLockPageId), "reset-auto-lock", ResetAutoLock);
        MirageUi.Text(LibraryText.Get("autoLock.intro"), MirageUi.Color.Secondary);

        DrawFillCard("##keepRules", DrawKeepRules);
    }

    private void DrawKeepRules()
    {
        DrawKeepNode(new KeepTarget(0, 0, 0, 0), 0, false, false, null, null, LibraryText.Get("autoLock.default"), spaced: false);
        MirageUi.PaddedSeparator();
        foreach (var patch in _keepPatches)
        {
            if (_keepDuties.Any(row => row.PatchId == patch.Id))
                DrawKeepVersion(patch);
        }
    }

    private void DrawFillCard(
        string id,
        System.Action draw,
        Vector2? size = null,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        var scale = MathF.Max(0.01f, ImGuiHelpers.GlobalScale);
        var rounding = 8f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var cardSize = size ?? ImGui.GetContentRegionAvail();
        cardSize.X = MathF.Max(1f, cardSize.X);
        cardSize.Y = MathF.Max(1f, cardSize.Y);
        var colors = MirageTheme.Active ?? MirageTheme.ResolveAppliedColors();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + cardSize, ImGui.GetColorU32(colors.FrameBg), rounding);
        drawList.AddRect(origin, origin + cardSize, ImGui.GetColorU32(colors.Separator), rounding);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f) * scale);
        using var card = ImRaii.Child(id, cardSize, false, ImGuiWindowFlags.AlwaysUseWindowPadding | flags);
        ImGui.PopStyleVar();
        if (card)
            draw();
    }

    private void DrawKeepVersion((uint Id, string Name) patch)
    {
        var duties = _keepDuties.Where(row => row.PatchId == patch.Id).ToList();
        var versionKey = new KeepTarget(KeepCount.Version, 0, patch.Id, 0);
        var versionOpen = _openKeepVersions.Contains(patch.Id);
        if (DrawKeepNode(
                versionKey,
                0,
                true,
                versionOpen,
                null,
                patch.Id,
                patch.Name)
            && !_openKeepVersions.Add(patch.Id))
            _openKeepVersions.Remove(patch.Id);
        if (!_openKeepVersions.Contains(patch.Id))
            return;

        foreach (var type in _types)
        {
            var typeDuties = duties.Where(row => row.TypeId == type.Id).ToList();
            if (typeDuties.Count == 0)
                continue;

            var typeKey = new KeepTarget(KeepCount.Type, type.Id, patch.Id, 0);
            var typeOpen = _openKeepTypes.Contains((patch.Id, type.Id));
            if (DrawKeepNode(
                    typeKey,
                    1,
                    true,
                    typeOpen,
                    type.IconId,
                    null,
                    type.Name)
                && !_openKeepTypes.Add((patch.Id, type.Id)))
                _openKeepTypes.Remove((patch.Id, type.Id));
            if (!_openKeepTypes.Contains((patch.Id, type.Id)))
                continue;

            foreach (var duty in typeDuties)
            {
                var contentKey = new KeepTarget(KeepCount.Content, type.Id, patch.Id, duty.Id);
                DrawKeepNode(contentKey, 2, false, false, null, null, duty.Name);
            }
        }
    }

    private void EnsureKeepTree()
    {
        if (_keepTreeReady)
            return;

        _keepTreeReady = true;
        _keepDuties.Clear();
        _keepPatches.Clear();

        var pending = new List<KeepDuty>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in PluginServices.DataManager.GetExcelSheet<ContentFinderCondition>())
        {
            var name = row.Name.ToString();
            if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                continue;

            counts[name] = counts.GetValueOrDefault(name) + 1;
            pending.Add(new KeepDuty(
                row.RowId,
                name,
                row.RequiredExVersion.RowId,
                row.ContentType.RowId,
                row.SortKey));
        }

        foreach (var row in pending)
        {
            var name = counts[row.Name] > 1 ? $"{row.Name}_{row.Id}" : row.Name;
            _keepDuties.Add(row with { Name = name });
        }

        _keepDuties.Sort(static (left, right) =>
        {
            var sort = left.SortKey.CompareTo(right.SortKey);
            return sort != 0 ? sort : left.Id.CompareTo(right.Id);
        });

        var seen = new HashSet<uint>();
        foreach (var row in PluginServices.DataManager.GetExcelSheet<ExVersion>().Reverse())
        {
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name) && row.Icon == 0)
                continue;

            seen.Add(row.RowId);
            _keepPatches.Add((row.RowId, string.IsNullOrWhiteSpace(name) ? "Unknown" : name));
        }

        foreach (var patch in _keepDuties.Select(row => row.PatchId).Distinct())
        {
            if (seen.Contains(patch))
                continue;

            _keepPatches.Add((patch, "Unknown"));
        }
    }

    private static string KeepText(KeepTarget key) =>
        C.TryCount(key.Scope, key.TypeId, key.PatchId, key.ContentId, out var count) ? count.ToString() : "";

    private static string KeepHint(KeepTarget key) =>
        key.Scope == 0 ? "0" : C.InheritedCount(key.Scope, key.TypeId, key.PatchId).ToString();

    private static bool KeepRowHit(string id, Vector2 origin, float height, float inputX, float gap)
    {
        var left = MathF.Max(1f, inputX - gap - origin.X);
        ImGui.SetCursorScreenPos(origin);
        var toggled = ImGui.InvisibleButton($"##keep-open-{id}", new Vector2(left, height));
        if (!ImGui.IsItemHovered())
            return toggled;

        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            new Vector2(origin.X + left, origin.Y + height),
            ImGui.GetColorU32(ImGuiCol.HeaderHovered),
            4f);
        return toggled;
    }

    private bool DrawKeepNode(
        KeepTarget key,
        int depth,
        bool expandable,
        bool open,
        uint? typeIcon,
        uint? patchId,
        string name,
        bool spaced = true)
    {
        var box = LibraryContentIconSize();
        var control = MirageUi.ResolveControlHeight();
        var height = MathF.Max(box, control);
        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        var origin = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var inputWidth = 64f * MathF.Max(0.01f, ImGuiHelpers.GlobalScale);
        var inputX = origin.X + width - inputWidth;
        var x = origin.X + depth * (box + gap);
        var toggled = expandable && KeepRowHit(key.Id, origin, height, inputX, gap);

        if (expandable)
        {
            var glyph = LibraryIconSize();
            var shown = toggled ? !open : open;
            PaintLibraryGlyph(
                shown ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight,
                new Vector2(x + (box - glyph) * 0.5f, origin.Y + (height - glyph) * 0.5f),
                MirageUi.GetColor(MirageUi.Color.Secondary));
            x += box + gap;
        }

        var iconY = origin.Y + (height - box) * 0.5f;
        if (patchId is uint patch)
        {
            DrawLibraryExpansionIcon(patch, new Vector2(x, iconY), new Vector2(x + box, iconY + box));
            x += box + gap;
        }
        else if (typeIcon is uint icon)
        {
            PaintLibraryType(icon, new Vector2(x, iconY), box);
            x += box + gap;
        }

        var nameWidth = MathF.Max(1f, inputX - gap - x);
        var textY = origin.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(new Vector2(x, origin.Y), new Vector2(x + nameWidth, origin.Y + height), true);
        draw.AddText(
            new Vector2(x, textY),
            MirageUi.ToUInt(MirageUi.GetColor(MirageUi.Color.Default)),
            FitLibraryText(name, nameWidth));
        draw.PopClipRect();

        if (!_keepDrafts.TryGetValue(key.Id, out var text))
            text = key.Scope == 0 ? Math.Max(0, C.DefaultKeepCount).ToString() : KeepText(key);
        ImGui.SetCursorScreenPos(new Vector2(inputX, origin.Y + (height - control) * 0.5f));
        if (MirageUi.InputText(
                "",
                ref text,
                6,
                $"keep-{key.Id}",
                hint: KeepHint(key),
                width: inputWidth,
                flags: ImGuiInputTextFlags.CharsDecimal))
            _keepDrafts[key.Id] = text;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            CommitKeep(key, text);
            _keepDrafts.Remove(key.Id);
        }

        var nextY = origin.Y + height;
        if (spaced)
            nextY += ImGui.GetStyle().ItemSpacing.Y;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, nextY));
        return toggled;
    }

    private void CommitKeep(KeepTarget key, string text)
    {
        var trimmed = text.Trim();
        if (key.Scope == 0)
        {
            if (trimmed.Length > 0 && (!int.TryParse(trimmed, out var parsed) || parsed < 0))
                return;

            var count = trimmed.Length == 0 ? 0 : int.Parse(trimmed);
            if (count == Math.Max(0, C.DefaultKeepCount))
                return;

            C.SetDefaultKeepCount(count);
            ApplyKeep();
            return;
        }

        int? next = null;
        if (trimmed.Length > 0)
        {
            if (!int.TryParse(trimmed, out var parsed) || parsed < 0)
                return;

            next = parsed;
        }

        var has = C.TryCount(key.Scope, key.TypeId, key.PatchId, key.ContentId, out var current);
        if (next is int value && has && value == current)
            return;
        if (next == null && !has)
            return;

        C.SetKeep(key.Scope, key.TypeId, key.PatchId, key.ContentId, next);
        ApplyKeep();
    }

    private void ApplyKeep()
    {
        Catalog.MaintainLibrary(force: true);
        Refresh();
    }

    private void DrawDeletedSettings()
    {
        DrawSettingsHeader(PageLabel(DeletedReplaysPageId), "reset-auto-delete", ResetAutoDelete);
        MirageUi.Text(LibraryText.Get("autoDelete.intro"), MirageUi.Color.Secondary);

        var days = C.DeletedKeepDays;
        if (DrawAutoDeleteAmount(
                LibraryText.Get("autoDelete.days"),
                LibraryText.Get("autoDelete.days.info"),
                ref days,
                "deletedDays"))
        {
            C.DeletedKeepDays = Math.Max(0, days);
            C.Save();
            if (Catalog.MaintainLibrary(force: true))
                Refresh();
        }

        var deletedMax = C.MaxLibraryGigabytes;
        if (DrawAutoDeleteAmount(
                LibraryText.Get("autoDelete.size"),
                LibraryText.Get("autoDelete.size.info"),
                ref deletedMax,
                "maxDeleted"))
        {
            C.MaxLibraryGigabytes = Math.Max(0, deletedMax);
            C.Save();
            if (Catalog.MaintainLibrary(force: true))
                Refresh();
        }
    }

    private static bool DrawAutoDeleteAmount(string label, string detail, ref int value, string id)
    {
        var scale = MathF.Max(0.01f, ImGuiHelpers.GlobalScale);
        var inputWidth = 120f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var rowWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);

        ImGui.AlignTextToFramePadding();
        MirageUi.Text(label, wrap: false);
        ImGui.SetCursorScreenPos(new Vector2(origin.X + rowWidth - inputWidth, origin.Y));
        var changed = MirageUi.InputInt(
            "",
            ref value,
            step: 0,
            stepFast: 0,
            id: id,
            width: inputWidth);
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y));
        MirageUi.Text(detail, MirageUi.Color.Secondary);
        return changed;
    }
}
