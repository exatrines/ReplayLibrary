using System.Diagnostics;
using System.IO;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using MirageUI.Layout;
using ReplayLibrary.Convert;
using ReplayLibrary.Library;
using ReplayLibrary.Playback;

namespace ReplayLibrary.UI;

/// <summary>Library list, the selected-replay card, then load, convert, and anonymize.</summary>
internal sealed partial class MainWindow
{
    private enum LibrarySort
    {
        Newest,
        ContentType,
    }

    private static readonly uint[] LibraryPrimaryTypes = Configuration.DefaultContentTypes;

    private LibrarySort _librarySort = LibrarySort.ContentType;
    private bool _libraryExpanded;
    private readonly HashSet<uint> _libraryOpenGroups = [];
    private readonly Dictionary<uint, string> _libraryPatchKeys = [];

    private readonly record struct LibraryListRow(bool Header, uint GroupId, int Count, Catalog.Entry Entry);

    private void DrawLibrary()
    {
        var scale = MathF.Max(0.01f, Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        var avail = ImGui.GetContentRegionAvail();
        var origin = ImGui.GetCursorPos();
        if (_types.Count == 0)
            LoadTypes();

        var edge = new Vector2(16f, 12f) * scale;
        var inset = 12f * scale;
        var gap = 12f * scale;
        var pad = new Vector2(12f, 12f) * scale;
        var filterWidth = LibraryFilterWidth(pad.X);
        var filterHeight = MathF.Max(1f, avail.Y - inset * 2f);
        var listX = origin.X + inset + filterWidth + gap;
        var listWidth = MathF.Max(1f, origin.X + avail.X - edge.X - listX);
        var toolbar = MirageUi.ResolveControlHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var listTop = origin.Y + edge.Y + toolbar + spacing;
        var columnBottom = origin.Y + inset + filterHeight;
        var columnHeight = MathF.Max(0f, columnBottom - listTop);
        var cardHeight = MathF.Min(CardHeight(), MathF.Max(0f, columnHeight - spacing));
        var listHeight = MathF.Max(1f, columnHeight - spacing - cardHeight);

        ImGui.SetCursorPos(origin + new Vector2(inset, inset));
        DrawLibraryFilterCard(ImGui.GetCursorScreenPos(), new Vector2(filterWidth, filterHeight), scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, pad);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using (var filters = ImRaii.Child(
            "##libraryFilters",
            new Vector2(filterWidth, filterHeight),
            false,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (filters)
                DrawLibraryFilters();
        }

        ImGui.PopStyleVar(2);
        ImGui.SetCursorPos(new Vector2(listX, origin.Y + edge.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using (var bar = ImRaii.Child(
            "##libraryToolbar",
            new Vector2(listWidth, toolbar),
            false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (bar)
                DrawLibraryToolbar();
        }

        ImGui.PopStyleVar();
        ImGui.SetCursorPos(new Vector2(listX, listTop));
        DrawLibraryBrowser(new Vector2(listWidth, listHeight), scale);
        ImGui.SetCursorPos(new Vector2(listX, listTop + listHeight + spacing));
        DrawReplayCard(new Vector2(listWidth, cardHeight), scale);
    }

    private void DrawLibraryToolbar()
    {
        var side = MirageUi.ResolveControlHeight();
        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        var searchWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (side * 3f + gap * 3f));
        DrawLibrarySort();
        ImGui.SameLine(0f, gap);
        MirageUi.SearchFilter("##librarySearch", ref _librarySearch, "Search library", width: searchWidth);
        ImGui.SameLine(0f, gap);
        if (MirageUi.IconButton(FontAwesomeIcon.FolderOpen, id: "library-folder", tooltip: "Open library folder"))
            OpenLibraryFolder();
        ImGui.SameLine(0f, gap);
        if (MirageUi.IconButton(FontAwesomeIcon.Sync, id: "library-refresh", tooltip: "Refresh"))
            Refresh();
    }

    private void DrawLibrarySort()
    {
        var newest = _librarySort == LibrarySort.Newest;
        if (!MirageUi.IconButton(
                newest ? FontAwesomeIcon.SortAmountDown : FontAwesomeIcon.LayerGroup,
                id: "library-sort",
                tooltip: newest ? "Order by newest" : "Order by contents"))
            return;

        _librarySort = newest ? LibrarySort.ContentType : LibrarySort.Newest;
    }

    private void DrawLibraryBrowser(Vector2 size, float scale)
    {
        var rounding = 8f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var colors = MirageTheme.Active ?? MirageTheme.ResolveAppliedColors();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(colors.FrameBg), rounding);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(colors.Separator), rounding);

        var pad = new Vector2(8f, 8f) * scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, pad);
        using var list = ImRaii.Child(
            "##libraryFiles",
            size,
            false,
            ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (list)
            DrawLibraryList();
    }

    private void DrawLibraryList()
    {
        if (_entries.Count == 0)
        {
            MirageUi.Text("No replays in the library.", MirageUi.Color.Secondary);
            return;
        }

        foreach (var row in LibraryListRows())
        {
            if (row.Header)
                DrawLibraryGroupLine(row);
            else if (row.GroupId == 0)
                DrawLibrarySingleLine(row.Entry);
            else
                DrawLibraryReplayLine(row.Entry);
        }
    }

    private void DrawLibraryGroupLine(LibraryListRow row)
    {
        var entry = row.Entry;
        var height = LibraryRowHeight();
        if (LibraryListHit($"group-{row.GroupId}", height, selected: false))
            ToggleLibraryGroup(row.GroupId);
        PaintLibraryGroup(entry, row.Count, _libraryOpenGroups.Contains(row.GroupId));
    }

    private void DrawLibrarySingleLine(Catalog.Entry entry)
    {
        var height = LibraryRowHeight();
        var selected = entry.Path == _selectedReplayPath;
        if (LibraryListHit(entry.Path, height, selected))
            LoadReplay(entry);
        PaintLibraryIdentity(entry, count: null, chevron: null);
    }

    private void DrawLibraryReplayLine(Catalog.Entry entry)
    {
        var height = LibraryRowHeight();
        var selected = entry.Path == _selectedReplayPath;
        if (LibraryListHit(entry.Path, height, selected))
            LoadReplay(entry);
        PaintLibraryReplay(entry);
    }

    private static bool LibraryListHit(string id, float height, bool selected)
    {
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var clicked = ImGui.InvisibleButton($"##library-row-{id}", new Vector2(width, height));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        if (selected)
        {
            var accent = MirageUi.ToUInt(MirageUi.WithAlpha(MirageUi.GetColor(MirageUi.Color.Accent), 0.28f));
            draw.AddRectFilled(min, max, accent, 4f);
        }
        else if (ImGui.IsItemHovered())
        {
            draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.HeaderHovered), 4f);
        }

        return clicked;
    }

    private void PaintLibraryGroup(Catalog.Entry entry, int count, bool open)
    {
        PaintLibraryIdentity(entry, $"({count})", open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight);
    }

    private void PaintLibraryIdentity(Catalog.Entry entry, string? count, FontAwesomeIcon? chevron)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var pad = LibraryRowPad();
        var icon = LibraryContentIconSize();
        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        var y = min.Y + (max.Y - min.Y - icon) * 0.5f;
        var edge = max.X - pad;
        var x = min.X + pad;
        var patchKey = LibraryPatchKey(entry);
        var knownPatch = uint.TryParse(patchKey, out var exVersion);
            DrawLibraryExpansionIcon(knownPatch ? exVersion : uint.MaxValue, new Vector2(x, y), new Vector2(x + icon, y + icon));
        x += icon + gap;
        PaintLibraryType(ContentTypeIcon(entry.ContentTypeId), new Vector2(x, y), icon);
        x += icon + gap;

        var right = edge;
        var chevronWidth = chevron.HasValue ? LibraryIconSize() : 0f;
        if (chevron.HasValue)
            right -= chevronWidth;
        var countWidth = string.IsNullOrEmpty(count) ? 0f : ImGui.CalcTextSize(count).X;
        if (countWidth > 0f)
            right -= gap + countWidth;
        var nameWidth = MathF.Max(1f, right - gap - x);
        var textY = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) * 0.5f;
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(new Vector2(x, min.Y), new Vector2(x + nameWidth, max.Y), true);
        draw.AddText(new Vector2(x, textY), MirageUi.ToUInt(MirageUi.GetColor(MirageUi.Color.Default)), FitLibraryText(entry.FolderName, nameWidth));
        draw.PopClipRect();
        if (countWidth > 0f)
        {
            var countX = chevron.HasValue ? edge - chevronWidth - gap - countWidth : edge - countWidth;
            draw.AddText(new Vector2(countX, textY), MirageUi.ToUInt(MirageUi.GetColor(MirageUi.Color.Secondary)), count);
        }

        if (!chevron.HasValue)
            return;

        var glyphY = min.Y + (max.Y - min.Y - LibraryIconSize()) * 0.5f;
        PaintLibraryGlyph(chevron.Value, new Vector2(edge - chevronWidth, glyphY), MirageUi.GetColor(MirageUi.Color.Secondary));
    }

    private static void PaintLibraryType(uint icon, Vector2 min, float size)
    {
        var max = min + new Vector2(size, size);
        if (icon != 0 && MirageUi.TryGetGameIcon(icon, out var loaded, hiRes: false))
            DrawLibraryFramedImage(loaded.Handle, min, max, Vector2.Zero, Vector2.One, false, LibraryListIconFit);
        else
            DrawLibraryFramedImage(default, min, max, Vector2.Zero, Vector2.One, false, LibraryListIconFit);
    }

    private static void PaintLibraryReplay(Catalog.Entry entry)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var pad = LibraryRowPad();
        var icon = LibraryIconSize();
        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        var indent = LibraryContentIconSize() + gap;
        var y = min.Y + (max.Y - min.Y - icon) * 0.5f;
        var edge = max.X - pad;
        var x = min.X + pad + indent;
        PaintLibraryGlyph(FontAwesomeIcon.Lock, new Vector2(x, y), entry.Protected ? LockColor : LibraryMuted());
        x += icon + gap;
        PaintLibraryGlyph(FontAwesomeIcon.Check, new Vector2(x, y), entry.Cleared ? ClearColor : LibraryMuted());
        x += icon + gap;
        PaintLibraryGlyph(FontAwesomeIcon.UserSecret, new Vector2(x, y), entry.Anonymized ? MirageUi.GetColor(MirageUi.Color.Default) : LibraryMuted());
        x += icon + gap;

        var recorded = RecordedText(entry);
        var recordedText = string.IsNullOrEmpty(recorded) ? "Unknown" : recorded;
        var known = uint.TryParse(entry.Version, out var build);
        var buildText = known ? FormatBuild(build) : "Unknown";
        var buildWidth = ImGui.CalcTextSize(buildText).X;
        var textY = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) * 0.5f;
        var dateNatural = ImGui.CalcTextSize(recordedText).X;
        const float rule = 1f;
        var draw = ImGui.GetWindowDrawList();
        if (x + rule <= edge)
        {
            PaintLibrarySeparator(x, min.Y, max.Y);
            x += rule + gap;
        }

        var afterParty = gap + rule + gap + dateNatural + gap + rule + gap + buildWidth;
        var partyEnd = PaintLibraryParty(entry, x, min.Y, max.Y, edge - afterParty);
        if (partyEnd > x)
        {
            x = partyEnd + gap;
            PaintLibrarySeparator(x, min.Y, max.Y);
            x += rule + gap;
        }

        var dateWidth = MathF.Min(dateNatural, MathF.Max(0f, edge - (gap + rule + gap + buildWidth) - x));
        if (dateWidth > 0f)
        {
            draw.PushClipRect(new Vector2(x, min.Y), new Vector2(x + dateWidth, max.Y), true);
            draw.AddText(new Vector2(x, textY), MirageUi.ToUInt(MirageUi.GetColor(MirageUi.Color.Default)), recordedText);
            draw.PopClipRect();
            x += dateWidth;
        }

        if (x + gap + rule < edge)
        {
            x += gap;
            PaintLibrarySeparator(x, min.Y, max.Y);
            x += rule + gap;
        }

        var buildRoom = MathF.Max(0f, edge - x);
        if (buildRoom > 0f)
        {
            var buildColor = known && build != GameBuild.Current
                ? MismatchColor
                : MirageUi.GetColor(MirageUi.Color.Default);
            draw.PushClipRect(new Vector2(x, min.Y), new Vector2(x + MathF.Min(buildWidth, buildRoom), max.Y), true);
            draw.AddText(new Vector2(x, textY), MirageUi.ToUInt(buildColor), buildText);
            draw.PopClipRect();
        }
    }

    private static float PaintLibraryParty(Catalog.Entry entry, float x, float top, float bottom, float limit)
    {
        const uint iconBase = 62100;
        const float jobGap = 4f;
        var job = LibraryContentIconSize();
        if (x + job > limit)
            return x;

        Span<byte> order = stackalloc byte[PartyLimit];
        var count = PartyOrder(entry, order);
        var y = top + (bottom - top - job) * 0.5f;
        var draw = ImGui.GetWindowDrawList();
        for (var n = 0; n < PartyLimit; n++)
        {
            if (x + job > limit)
                break;

            var min = new Vector2(x, y);
            var max = min + new Vector2(job, job);
            if (n < count)
            {
                var index = order[n];
                if (MirageUi.TryGetGameIcon(iconBase + JobAt(entry.Jobs, index), out var texture, hiRes: false))
                    draw.AddImage(texture.Handle, min, max);
                if (index == entry.Viewpoint)
                    DrawMainMark(min, max);
            }
            else
            {
                PaintEmptyPartySlot(min, max);
            }

            x += job;
            if (n + 1 < PartyLimit && x + jobGap + job <= limit)
                x += jobGap;
        }

        return x;
    }

    private static void PaintLibrarySeparator(float x, float top, float bottom)
    {
        var height = LibraryContentIconSize();
        var y = top + (bottom - top - height) * 0.5f;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(x, y),
            new Vector2(x, y + height),
            ImGui.GetColorU32(ImGuiCol.Separator));
    }

    private static void PaintLibraryGlyph(FontAwesomeIcon icon, Vector2 pos, Vector4 color)
    {
        ImGui.GetWindowDrawList().AddText(
            UiBuilder.IconFont,
            LibraryIconSize(),
            pos,
            MirageUi.ToUInt(color),
            icon.ToIconString());
    }

    private List<LibraryListRow> LibraryListRows()
    {
        var visible = _entries.Where(LibraryVisible).ToList();
        var heads = new List<(Catalog.Entry Head, Catalog.Entry[]? Members)>();
        foreach (var bucket in visible.GroupBy(entry => entry.ContentId))
        {
            var members = bucket
                .OrderByDescending(entry => entry.Timestamp)
                .ThenBy(entry => entry.FolderName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (bucket.Key == 0)
            {
                foreach (var entry in members)
                    heads.Add((entry, null));
                continue;
            }

            heads.Add((members[0], members));
        }

        var ordered = _librarySort == LibrarySort.Newest
            ? heads
                .OrderByDescending(row => row.Head.Timestamp)
                .ThenBy(row => row.Head.FolderName, StringComparer.OrdinalIgnoreCase)
            : heads
                .OrderBy(row => row.Head.ContentTypeId == 0 ? uint.MaxValue : row.Head.ContentTypeId)
                .ThenByDescending(row => row.Head.ContentId)
                .ThenByDescending(row => row.Head.Timestamp)
                .ThenBy(row => row.Head.FolderName, StringComparer.OrdinalIgnoreCase);

        var rows = new List<LibraryListRow>();
        foreach (var (head, members) in ordered)
        {
            if (members == null)
            {
                rows.Add(new LibraryListRow(false, 0, 1, head));
                continue;
            }

            rows.Add(new LibraryListRow(true, head.ContentId, members.Length, head));
            if (!_libraryOpenGroups.Contains(head.ContentId))
                continue;

            foreach (var entry in members)
                rows.Add(new LibraryListRow(false, head.ContentId, members.Length, entry));
        }

        return rows;
    }

    private string LibraryPatchKey(Catalog.Entry entry)
    {
        if (_libraryPatchKeys.TryGetValue(entry.ContentId, out var cached))
            return cached;

        var key = "unknown";
        if (entry.ContentId != 0)
        {
            var row = PluginServices.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(entry.ContentId);
            if (row != null)
            {
                var version = row.Value.RequiredExVersion;
                if (version.IsValid)
                    key = version.RowId.ToString();
            }
        }

        _libraryPatchKeys[entry.ContentId] = key;
        return key;
    }

    private bool LibraryVisible(Catalog.Entry entry) =>
        !_libraryHiddenPatches.Contains(LibraryPatchKey(entry))
        && !_libraryHiddenTypes.Contains(entry.ContentTypeId.ToString())
        && LibraryMatch(entry, _librarySearch);

    private static void DrawLibraryFilterCard(Vector2 screenPos, Vector2 size, float scale)
    {
        var rounding = 8f * scale;
        var colors = MirageTheme.Active ?? MirageTheme.ResolveAppliedColors();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(screenPos, screenPos + size, ImGui.GetColorU32(colors.ChromeBg), rounding);
        draw.AddRect(screenPos, screenPos + size, ImGui.GetColorU32(ImGuiCol.Separator), rounding);
    }

    private float LibraryFilterWidth(float padX)
    {
        var icon = MirageUi.ResolveControlHeight();
        if (!_libraryExpanded)
            return padX * 2f + icon;

        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        var scroll = ImGui.GetStyle().ScrollbarSize;
        var longest = 0f;
        foreach (var row in PluginServices.DataManager.GetExcelSheet<ExVersion>())
        {
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name) && row.Icon == 0)
                continue;

            longest = MathF.Max(longest, ImGui.CalcTextSize(string.IsNullOrWhiteSpace(name) ? "Unknown" : name).X);
        }

        foreach (var row in _types)
            longest = MathF.Max(longest, ImGui.CalcTextSize(row.Name).X);

        var text = MathF.Min(longest, 220f * MathF.Max(0.01f, Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale));
        return padX * 2f + icon + gap + text + scroll;
    }

    private void DrawLibraryFilters()
    {
        var button = MirageUi.ResolveControlHeight();
        var footer = LibraryFilterRuleHeight() + button;
        DrawLibraryPatches(LibraryPatches());
        DrawLibraryFilterRule(LibraryFilterGap());
        var listHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - footer);
        var flags = ImGuiWindowFlags.NoBackground;
        if (!_libraryExpanded)
            flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        using (var list = ImRaii.Child("##libraryFilterScroll", new Vector2(0f, listHeight), false, flags))
        {
            if (list)
                DrawLibraryTypes(includeRest: _libraryExpanded);
        }

        DrawLibraryFilterRule();
        var mark = _libraryExpanded ? FontAwesomeIcon.ChevronLeft : FontAwesomeIcon.EllipsisH;
        if (DrawLibraryFilterMark(_libraryExpanded ? "fold" : "more", mark))
            _libraryExpanded = !_libraryExpanded;
    }

    private static List<(string Id, string Name, uint Icon)> LibraryPatches()
    {
        var patches = new List<(string Id, string Name, uint Icon)>();
        foreach (var row in PluginServices.DataManager.GetExcelSheet<ExVersion>().Reverse())
        {
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name) && row.Icon == 0)
                continue;

            patches.Add((row.RowId.ToString(), string.IsNullOrWhiteSpace(name) ? "Unknown" : name, row.Icon));
        }

        return patches;
    }

    private void DrawLibraryPatches(List<(string Id, string Name, uint Icon)> patches)
    {
        var patchIds = patches.Select(row => row.Id).ToList();
        foreach (var row in patches)
        {
            DrawLibraryFilterIcon(
                "patch",
                row.Id,
                row.Name,
                row.Icon,
                _libraryHiddenPatches,
                patchIds);
        }
    }

    private void DrawLibraryTypes(bool includeRest)
    {
        var types = _types.ToDictionary(row => row.Id);
        var typeIds = _types.Select(row => row.Id.ToString()).ToList();
        foreach (var id in LibraryPrimaryTypes)
        {
            if (!types.TryGetValue(id, out var row))
                continue;

            DrawLibraryFilterIcon(
                "type",
                row.Id.ToString(),
                row.Name,
                row.IconId,
                _libraryHiddenTypes,
                typeIds);
        }

        if (!includeRest)
            return;

        foreach (var row in _types)
        {
            if (LibraryPrimaryTypes.Contains(row.Id))
                continue;

            DrawLibraryFilterIcon(
                "type",
                row.Id.ToString(),
                row.Name,
                row.IconId,
                _libraryHiddenTypes,
                typeIds);
        }
    }

    private static bool DrawLibraryFilterMark(string id, FontAwesomeIcon icon)
    {
        var size = MirageUi.ResolveControlHeight();
        var width = MathF.Max(size, ImGui.GetContentRegionAvail().X);
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##library-filter-{id}", new Vector2(width, size));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                origin,
                origin + new Vector2(width, size),
                ImGui.GetColorU32(ImGuiCol.HeaderHovered),
                4f);
        }

        var glyph = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyphSize = ImGui.CalcTextSize(glyph);
            ImGui.SetCursorScreenPos(new Vector2(origin.X + (width - glyphSize.X) * 0.5f, origin.Y + (size - glyphSize.Y) * 0.5f));
            ImGui.TextColored(MirageUi.GetColor(MirageUi.Color.Secondary), glyph);
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + size + LibraryFilterGap()));
        return clicked;
    }

    private void DrawLibraryFilterIcon(
        string group,
        string id,
        string label,
        uint icon,
        HashSet<string> hidden,
        IReadOnlyList<string> categoryIds)
    {
        var size = MirageUi.ResolveControlHeight();
        var width = MathF.Max(size, ImGui.GetContentRegionAvail().X);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##library-filter-{group}-{id}", new Vector2(width, size));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            var solo = !hidden.Contains(id);
            if (solo)
            {
                foreach (var other in categoryIds)
                {
                    if (other != id && !hidden.Contains(other))
                    {
                        solo = false;
                        break;
                    }
                }
            }

            if (solo)
            {
                hidden.Clear();
            }
            else
            {
                hidden.Remove(id);
                foreach (var other in categoryIds)
                {
                    if (other != id)
                        hidden.Add(other);
                }
            }
        }
        else if (clicked)
        {
            if (!hidden.Remove(id))
                hidden.Add(id);
        }

        if (hovered)
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                pos,
                pos + new Vector2(width, size),
                ImGui.GetColorU32(ImGuiCol.HeaderHovered),
                4f);
        }

        var shown = !hidden.Contains(id);
        var iconPos = _libraryExpanded
            ? pos
            : new Vector2(pos.X + (width - size) * 0.5f, pos.Y);
        var iconMax = iconPos + new Vector2(size, size);
        var exVersion = 0u;
        var patch = group == "patch" && uint.TryParse(id, out exVersion);
        if (patch)
            DrawLibraryExpansionIcon(exVersion, iconPos, iconMax, muted: !shown);
        else
            DrawLibraryFilterTypeIcon(icon, iconPos, iconMax, muted: !shown);

        if (_libraryExpanded)
        {
            var gap = ImGui.GetStyle().ItemInnerSpacing.X;
            var textHeight = ImGui.GetTextLineHeight();
            ImGui.SetCursorScreenPos(new Vector2(pos.X + size + gap, pos.Y + (size - textHeight) * 0.5f));
            var color = shown ? MirageUi.GetColor(MirageUi.Color.Default) : LibraryMuted();
            MirageUi.Text(FitLibraryText(label, MathF.Max(1f, width - size - gap)), color, wrap: false);
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + size + LibraryFilterGap()));
        if (hovered && !_libraryExpanded)
            MirageUi.Tooltip(label);
    }

    private static void DrawLibraryFilterRule(float extraBottom = 0f)
    {
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var pos = ImGui.GetCursorScreenPos();
        var pad = 4f * LibraryUiScale();
        var inset = LibraryFilterGap();
        ImGui.Dummy(new Vector2(width, LibraryFilterRuleHeight() + extraBottom));
        var y = pos.Y + pad;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(pos.X + inset, y),
            new Vector2(pos.X + width - inset, y),
            ImGui.GetColorU32(ImGuiCol.Separator));
    }

    private static float LibraryFilterRuleHeight() => 4f * LibraryUiScale() * 2f + 1f;

    private static float LibraryFilterGap() => 2f * LibraryUiScale();

    private static float LibraryUiScale() =>
        MathF.Max(0.01f, Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);

    private static bool LibraryMatch(Catalog.Entry entry, string filter)
    {
        if (MirageUi.MatchesFilter(entry.FolderName, entry.FolderName, filter))
            return true;
        if (MirageUi.MatchesFilter(entry.ContentTypeName, entry.ContentTypeName, filter))
            return true;

        var recorded = RecordedText(entry);
        if (recorded.Length > 0 && MirageUi.MatchesFilter(recorded, recorded, filter))
            return true;
        if (entry.Version.Length > 0 && MirageUi.MatchesFilter(entry.Version, entry.Version, filter))
            return true;
        if (!uint.TryParse(entry.Version, out var build))
            return false;

        var label = FormatBuild(build);
        return MirageUi.MatchesFilter(label, label, filter);
    }


    private void ToggleLibraryGroup(uint contentId)
    {
        if (!_libraryOpenGroups.Remove(contentId))
            _libraryOpenGroups.Add(contentId);
    }

    private const float LibraryContentIconScale = 1.25f;
    private const float LibraryListIconFit = 0.94f;

    private static float LibraryRowPad() =>
        4f * MathF.Max(0.01f, Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);

    private static float LibraryRowHeight() =>
        LibraryContentIconSize() + LibraryRowPad() * 2f;

    private static float LibraryIconSize() => ImGui.GetTextLineHeight();

    private static float LibraryContentIconSize() => LibraryIconSize() * LibraryContentIconScale;

    private static string FitLibraryText(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;

        const string ellipsis = "...";
        var keep = text.Length;
        while (keep > 0 && ImGui.CalcTextSize(text[..keep] + ellipsis).X > width)
            keep--;
        return keep == 0 ? ellipsis : text[..keep] + ellipsis;
    }

    private static void DrawLibraryExpansionIcon(uint exVersion, Vector2 min, Vector2 max, bool muted = false)
    {
        var directory = PluginServices.PluginInterface.AssemblyLocation.DirectoryName;
        if (exVersion <= 5
            && !string.IsNullOrEmpty(directory)
            && MirageUi.TryGetTexture(Path.Combine(directory, "Assets", "patch.png"), out var texture))
        {
            var insetX = 0.5f / texture.Width;
            var insetY = 0.5f / texture.Height;
            var u0 = exVersion / 6f + insetX;
            var u1 = (exVersion + 1) / 6f - insetX;
            DrawLibraryFramedImage(
                texture.Handle,
                min,
                max,
                new Vector2(u0, insetY),
                new Vector2(u1, 1f - insetY),
                muted,
                1f);
            return;
        }

        DrawLibraryFramedImage(default, min, max, Vector2.Zero, Vector2.One, false, 1f);
    }

    private static void DrawLibraryFilterTypeIcon(uint icon, Vector2 min, Vector2 max, bool muted)
    {
        if (icon != 0 && MirageUi.TryGetGameIcon(icon, out var loaded, hiRes: false))
        {
            DrawLibraryFramedImage(loaded.Handle, min, max, Vector2.Zero, Vector2.One, muted, 1f);
            return;
        }

        DrawLibraryFramedImage(default, min, max, Vector2.Zero, Vector2.One, false, 1f);
    }

    private static void DrawLibraryFramedImage(
        ImTextureID texture,
        Vector2 min,
        Vector2 max,
        Vector2 uv0,
        Vector2 uv1,
        bool muted,
        float fit)
    {
        var accent = MirageUi.GetColor(MirageUi.Color.Accent);
        var fill = MirageUi.WithAlpha(accent, 0.14f);
        var border = MirageUi.WithAlpha(accent, 0.75f);
        var rounding = MirageUi.InputStyle.FrameRounding;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, MirageUi.ToUInt(fill), rounding);

        if (texture.Handle != 0)
        {
            var pad = new Vector2(1f, 1f);
            var innerMin = min + pad;
            var innerMax = max - pad;
            if (fit > 0f && fit < 1f)
            {
                var inner = innerMax - innerMin;
                var drawn = inner * fit;
                var origin = innerMin + (inner - drawn) * 0.5f;
                innerMin = origin;
                innerMax = origin + drawn;
            }

            if (innerMax.X - innerMin.X >= 1f && innerMax.Y - innerMin.Y >= 1f)
            {
                var tint = muted
                    ? ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f))
                    : uint.MaxValue;
                var radius = MathF.Max(0f, rounding - (innerMin.X - min.X));
                if (radius >= 0.5f)
                {
                    draw.AddImageRounded(
                        texture,
                        innerMin,
                        innerMax,
                        uv0,
                        uv1,
                        tint,
                        radius,
                        ImDrawFlags.RoundCornersAll);
                }
                else
                {
                    draw.AddImage(texture, innerMin, innerMax, uv0, uv1, tint);
                }
            }
        }

        draw.AddRect(min, max, MirageUi.ToUInt(border), rounding, ImDrawFlags.RoundCornersAll, 1f);
    }

    private static Vector4 LibraryMuted() =>
        MirageUi.WithAlpha(MirageUi.GetColor(MirageUi.Color.Secondary), 0.4f);


    private void DrawReplayCard(Vector2 size, float scale)
    {
        if (size.X <= 0f || size.Y <= 0f)
            return;

        var rounding = 8f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var colors = MirageTheme.Active ?? MirageTheme.ResolveAppliedColors();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(colors.FrameBg), rounding);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(colors.Separator), rounding);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        using var card = ImRaii.Child("##replayCard", size, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (!card)
            return;

        if (!TrySelectedReplay(out var entry))
        {
            MirageUi.Text("Select a replay.", MirageUi.Color.Secondary);
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var buttonWidth = CardButtonWidth();
        var gap = 16f;
        var textWidth = MathF.Max(1f, avail.X - buttonWidth - gap);
        var start = ImGui.GetCursorPos();
        using (var details = ImRaii.Child("##cardDetails", new Vector2(textWidth, avail.Y), false))
        {
            if (details)
            {
                DrawCardTitle(entry);
                DrawCardFacts(entry);
                DrawCardParty(entry);
            }
        }

        ImGui.SetCursorPos(new Vector2(start.X + textWidth + gap, start.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using (var actions = ImRaii.Child("##cardActions", new Vector2(buttonWidth, avail.Y), false))
        {
            ImGui.PopStyleVar();
            if (actions)
                DrawCardActions(entry, buttonWidth);
        }
    }

    private static float CardHeight()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var count = ReplayConvertClient.IsLoaded ? 5 : 3;
        var buttons = MirageUi.ResolveControlHeight() * count + spacing * (count - 1);
        var line = ImGui.GetTextLineHeight() + spacing;
        var details = 28f + spacing + line * 2f + 24f;
        return MathF.Max(buttons, details) + 20f;
    }

    private static float CardButtonWidth()
    {
        var width = MirageUi.IconLabelButtonWidth(FontAwesomeIcon.Play, LibraryText.Get("card.play"));
        width = MathF.Max(width, MirageUi.IconLabelButtonWidth(FontAwesomeIcon.Clone, LibraryText.Get("card.convert")));
        width = MathF.Max(width, MirageUi.IconLabelButtonWidth(FontAwesomeIcon.Clone, LibraryText.Get("card.reconvert")));
        width = MathF.Max(width, MirageUi.IconLabelButtonWidth(FontAwesomeIcon.UserSecret, LibraryText.Get("card.anonymize")));
        width = MathF.Max(width, MirageUi.IconLabelButtonWidth(FontAwesomeIcon.UserSecret, LibraryText.Get("card.reanonymize")));
        width = MathF.Max(width, MirageUi.IconLabelButtonWidth(FontAwesomeIcon.Trash, LibraryText.Get("card.delete")));
        return MathF.Max(width, MirageUi.IconLabelButtonWidth(FontAwesomeIcon.Folder, LibraryText.Get("card.explorer")));
    }

    private void DrawCardTitle(Catalog.Entry entry)
    {
        var icon = ContentTypeIcon(entry.ContentTypeId);
        var iconSize = 28f;
        var gap = 8f;
        var start = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();
        var contentRight = screen.X + ImGui.GetContentRegionAvail().X;
        var rowHeight = icon == 0 ? ImGui.GetTextLineHeight() : iconSize;
        if (icon != 0)
            MirageUi.GameIcon(icon, iconSize, iconSize, hiRes: false);

        var name = entry.FolderName;
        var nameSize = ImGui.CalcTextSize(name);
        var muted = MirageUi.WithAlpha(MirageUi.GetColor(MirageUi.Color.Secondary), 0.4f);
        var ink = MirageUi.GetColor(MirageUi.Color.Default);
        Span<FontAwesomeIcon> marks = [FontAwesomeIcon.Lock, FontAwesomeIcon.Check, FontAwesomeIcon.UserSecret];
        Span<Vector4> markColors =
        [
            entry.Protected ? LockColor : muted,
            entry.Cleared ? ClearColor : muted,
            entry.Anonymized ? ink : muted,
        ];
        const float markGap = 6f;
        var marksWidth = 0f;
        Span<float> markWidths = stackalloc float[marks.Length];
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            for (var index = 0; index < marks.Length; index++)
            {
                markWidths[index] = ImGui.CalcTextSize(marks[index].ToIconString()).X;
                marksWidth += markWidths[index];
                if (index != 0)
                    marksWidth += markGap;
            }
        }

        var nameX = screen.X + (icon == 0 ? 0f : iconSize + gap);
        var marksX = MathF.Min(nameX + nameSize.X + gap, contentRight - marksWidth);
        var textY = screen.Y + MathF.Max(0f, (rowHeight - ImGui.GetTextLineHeight()) * 0.5f);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(new Vector2(nameX, screen.Y), new Vector2(MathF.Max(nameX, marksX - gap), screen.Y + rowHeight), true);
        draw.AddText(new Vector2(nameX, textY), MirageUi.ToUInt(ink), name);
        draw.PopClipRect();

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var x = marksX;
            for (var index = 0; index < marks.Length; index++)
            {
                var glyph = marks[index].ToIconString();
                var markY = screen.Y + MathF.Max(0f, (rowHeight - ImGui.GetTextLineHeight()) * 0.5f);
                draw.AddText(new Vector2(x, markY), MirageUi.ToUInt(markColors[index]), glyph);
                x += markWidths[index] + markGap;
            }
        }

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + rowHeight + ImGui.GetStyle().ItemSpacing.Y));
    }

    private static void DrawCardFacts(Catalog.Entry entry)
    {
        var ink = MirageUi.GetColor(MirageUi.Color.Default);
        var known = uint.TryParse(entry.Version, out var build);
        var version = known ? FormatBuild(build) : "Unknown";
        var versionColor = known && build != GameBuild.Current ? MismatchColor : ink;
        var converted = entry.SourceBuild != 0 ? $" converted from {FormatBuild(entry.SourceBuild)}" : null;
        DrawCardIconText(FontAwesomeIcon.Hammer, version, ink, false, textColor: versionColor, suffix: converted);

        var recorded = RecordedText(entry);
        DrawCardIconText(FontAwesomeIcon.CalendarAlt, string.IsNullOrEmpty(recorded) ? "Unknown" : recorded, ink, false);
    }

    private static void DrawCardIconText(
        FontAwesomeIcon icon,
        string text,
        Vector4 iconColor,
        bool sameLine,
        float leading = 0f,
        Vector4? textColor = null,
        string? suffix = null)
    {
        if (sameLine)
            ImGui.SameLine(0f, leading);

        var gap = ImGui.GetStyle().ItemInnerSpacing.X;
        var glyph = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(glyph);

        var textSize = ImGui.CalcTextSize(text);
        var suffixSize = string.IsNullOrEmpty(suffix) ? Vector2.Zero : ImGui.CalcTextSize(suffix);
        var height = MathF.Max(iconSize.Y, MathF.Max(textSize.Y, suffixSize.Y));
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(iconSize.X + gap + textSize.X + suffixSize.X, height));

        var draw = ImGui.GetWindowDrawList();
        var textY = start.Y + (height - textSize.Y) * 0.5f;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            draw.AddText(
                new Vector2(start.X, start.Y + (height - iconSize.Y) * 0.5f),
                MirageUi.ToUInt(iconColor),
                glyph);
        draw.AddText(
            new Vector2(start.X + iconSize.X + gap, textY),
            MirageUi.ToUInt(textColor ?? MirageUi.GetColor(MirageUi.Color.Default)),
            text);
        if (!string.IsNullOrEmpty(suffix))
            draw.AddText(
                new Vector2(start.X + iconSize.X + gap + textSize.X, textY),
                MirageUi.ToUInt(MirageUi.GetColor(MirageUi.Color.Default)),
                suffix);
    }

    private static void DrawCardParty(Catalog.Entry entry)
    {
        const uint iconBase = 62100;
        const float size = 24f;
        const float jobGap = 4f;
        Span<byte> order = stackalloc byte[PartyLimit];
        var count = PartyOrder(entry, order);
        for (var n = 0; n < PartyLimit; n++)
        {
            if (n != 0)
            {
                var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
                if (ImGui.GetItemRectMax().X + jobGap + size > right)
                    break;

                ImGui.SameLine(0f, jobGap);
            }
            else if (ImGui.GetContentRegionAvail().X < size)
            {
                break;
            }

            if (n < count)
            {
                var index = order[n];
                if (MirageUi.GameIcon(iconBase + JobAt(entry.Jobs, index), size, size, hiRes: false) && index == entry.Viewpoint)
                    DrawMainMark(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            }
            else
            {
                var origin = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new Vector2(size, size));
                PaintEmptyPartySlot(origin, origin + new Vector2(size, size));
            }
        }
    }

    private static void PaintEmptyPartySlot(Vector2 min, Vector2 max)
    {
        var colors = MirageTheme.Active ?? MirageTheme.ResolveAppliedColors();
        var draw = ImGui.GetWindowDrawList();
        var rounding = MirageUi.InputStyle.FrameRounding;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(colors.FrameBg), rounding);
        draw.AddRect(min, max, ImGui.GetColorU32(colors.Separator), rounding);
    }

    private const int PartyLimit = 8;

    private static int PartyOrder(Catalog.Entry entry, Span<byte> order)
    {
        var limit = Math.Min(order.Length, PartyLimit);
        Span<byte> roles = stackalloc byte[PartyLimit];
        var count = 0;
        for (var index = 0; index < limit; index++)
        {
            var job = JobAt(entry.Jobs, index);
            if (job == 0)
                continue;

            order[count] = (byte)index;
            roles[count] = RoleOrder(job);
            count++;
        }

        for (var i = 1; i < count; i++)
        {
            var slot = order[i];
            var role = roles[i];
            var j = i;
            while (j > 0 && (roles[j - 1] > role || (roles[j - 1] == role && order[j - 1] > slot)))
            {
                order[j] = order[j - 1];
                roles[j] = roles[j - 1];
                j--;
            }

            order[j] = slot;
            roles[j] = role;
        }

        return count;
    }

    private static byte JobAt(ulong jobs, int index) =>
        (byte)((jobs >> (index * 8)) & 0xFF);

    private static byte RoleOrder(byte jobId)
    {
        var row = PluginServices.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId);
        if (row == null)
            return 5;

        var role = row.Value.Role;
        var category = row.Value.ClassJobCategory.RowId;
        return role switch
        {
            1 => 0,
            4 => 1,
            2 => 2,
            3 when category == 31 => 4,
            5 => 4,
            3 => 3,
            _ => 5,
        };
    }

    private static void DrawMainMark(Vector2 min, Vector2 max)
    {
        var glyph = FontAwesomeIcon.Check.ToIconString();
        const float mark = 10f;
        var pos = new Vector2(max.X - mark + 3f, min.Y - 3f);
        var draw = ImGui.GetWindowDrawList();
        var ink = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        var edge = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f));
        for (var y = -1; y <= 1; y++)
        {
            for (var x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0)
                    continue;

                draw.AddText(UiBuilder.IconFont, mark, pos + new Vector2(x, y), edge, glyph);
            }
        }

        draw.AddText(UiBuilder.IconFont, mark, pos, ink, glyph);
    }

    private void DrawCardActions(Catalog.Entry entry, float width)
    {
        var current = IsCurrentBuild(entry);
        var inInn = PlaybackService.InInn;
        var canPlay = current && inInn;
        if (MirageUi.PrimaryIconLabelButton(
                FontAwesomeIcon.Play,
                LibraryText.Get("card.play"),
                enabled: canPlay,
                width: width,
                id: "card-play",
                tooltip: current && !inInn ? LibraryText.Get("card.play.inn") : null,
                tooltipPosition: MirageTooltipPosition.Right)
            && canPlay)
            PlayReplay(entry);

        if (ReplayConvertClient.IsLoaded)
        {
            var authed = ReplayConvertClient.IsAuthenticated;
            var authTip = authed ? null : LibraryText.Get("card.converter.auth");
            var reconvert = current && entry.SourceBuild != 0;
            var canConvert = !current && ParsedBuild(entry) != 0;
            var convertEnabled = authed && (reconvert || canConvert);
            string? convertTip = authTip;
            if (convertTip == null)
            {
                if (reconvert)
                    convertTip = LibraryText.Get("card.convert.again");
                else if (current)
                    convertTip = LibraryText.Get("card.convert.current");
                else if (!canConvert)
                    convertTip = LibraryText.Get("card.convert.nomap");
            }

            if (MirageUi.PrimaryIconLabelButton(
                    FontAwesomeIcon.Clone,
                    reconvert ? LibraryText.Get("card.reconvert") : LibraryText.Get("card.convert"),
                    enabled: convertEnabled,
                    width: width,
                    id: "card-convert",
                    tooltip: convertTip,
                    tooltipPosition: MirageTooltipPosition.Right)
                && convertEnabled)
                StartConvert(entry);

            var anonymizeEnabled = authed && current;
            if (MirageUi.PrimaryIconLabelButton(
                    FontAwesomeIcon.UserSecret,
                    entry.Anonymized ? LibraryText.Get("card.reanonymize") : LibraryText.Get("card.anonymize"),
                    enabled: anonymizeEnabled,
                    width: width,
                    id: "card-anonymize",
                    tooltip: authTip ?? (current ? null : LibraryText.Get("card.anonymize.current")),
                    tooltipPosition: MirageTooltipPosition.Right)
                && anonymizeEnabled)
                StartAnonymize(entry);
        }

        var protectedReplay = entry.Protected;
        var inLibrary = Catalog.IsInLibrary(entry.Path);
        if (MirageUi.PrimaryIconLabelButton(
                FontAwesomeIcon.Trash,
                LibraryText.Get("card.delete"),
                enabled: inLibrary && !protectedReplay,
                width: width,
                id: "card-delete",
                tooltip: protectedReplay ? LibraryText.Get("card.delete.protected") : inLibrary ? null : LibraryText.Get("card.delete.outside"),
                tooltipPosition: MirageTooltipPosition.Right)
            && inLibrary
            && !protectedReplay)
            AskDelete(LibraryText.Get("dialog.delete.title"), LibraryText.Format("dialog.delete.message", ReplayLine(entry)), [entry.Path]);

        if (MirageUi.PrimaryIconLabelButton(FontAwesomeIcon.Folder, LibraryText.Get("card.explorer"), width: width, id: "card-explorer"))
            RevealInExplorer(entry.Path);
    }

    private void LoadReplay(Catalog.Entry entry)
    {
        _selectedReplayPath = entry.Path;
        if (IsCurrentBuild(entry) || !uint.TryParse(entry.Version, out _))
            PlaybackService.Play(entry.Path);
    }

    private void PlayReplay(Catalog.Entry entry)
    {
        _selectedReplayPath = entry.Path;
        PlaybackService.Start(entry.Path);
    }

    private static bool IsCurrentBuild(Catalog.Entry entry) =>
        ParsedBuild(entry) == GameBuild.Current;

    private static uint ParsedBuild(Catalog.Entry entry) =>
        uint.TryParse(entry.Version, out var build) ? build : 0;

    private void StartConvert(Catalog.Entry entry)
    {
        if (!Catalog.IsInLibrary(entry.Path))
        {
            AskOutside(entry.Path);
            return;
        }

        var source = entry.Path;
        if (IsCurrentBuild(entry))
        {
            if (entry.SourceBuild == 0)
                return;

            source = Catalog.FindOriginal(entry.SourceBuild, entry.Timestamp, entry.ContentId);
            if (source == null)
            {
                PluginServices.ChatGui.PrintError("Could not find the original replay.");
                return;
            }
        }

        RunConvert(source);
    }

    private void AskOutside(string path)
    {
        var duplicate = Catalog.LibraryHasSame(path);
        var message = duplicate
            ? LibraryText.Get("dialog.convert.duplicate")
            : LibraryText.Get("dialog.convert.outside");
        _pendingConvertPath = path;
        _dialog.Show(
            LibraryText.Get("dialog.convert.title"),
            message,
            FontAwesomeIcon.Clone,
            MirageUi.Yes(ConfirmOutside),
            MirageUi.No());
    }

    private void ConfirmOutside()
    {
        var path = _pendingConvertPath;
        _pendingConvertPath = null;
        if (string.IsNullOrEmpty(path))
            return;

        if (BackupRunning())
            return;

        var adopted = Catalog.Adopt(path);
        if (adopted == null)
        {
            PluginServices.ChatGui.PrintError("Could not move the replay into the library.");
            return;
        }

        Refresh();
        if (ReplayFile.TryRead(adopted, out var header) && header.GameBuildRevision != GameBuild.Current)
            RunConvert(adopted);
    }

    private void RunConvert(string path)
    {
        if (BackupRunning())
            return;

        _ = Task.Run(() =>
        {
            var output = ReplayConvertClient.Convert(path);
            var report = ReplayConvertClient.LastReport();
            PluginServices.Framework.RunOnFrameworkThread(() => FinishConvert(output, report));
        });
    }

    private void FinishConvert(string? output, string report)
    {
        if (output == null)
        {
            PluginServices.ChatGui.PrintError(string.IsNullOrEmpty(report) ? "Could not convert this replay." : report);
            return;
        }

        if (BackupRunning())
            return;

        Refresh();
        SelectStored(output);
        PluginServices.ChatGui.Print(string.IsNullOrEmpty(report) ? "Converted the replay." : report);
    }

    private void StartAnonymize(Catalog.Entry entry)
    {
        if (!IsCurrentBuild(entry))
            return;

        if (!Catalog.IsInLibrary(entry.Path))
        {
            AskOutsideAnonymize(entry.Path);
            return;
        }

        AnonymizeFile(entry.Path, replace: entry.Anonymized);
    }

    private void AskOutsideAnonymize(string path)
    {
        var duplicate = Catalog.LibraryHasSame(path);
        var message = duplicate
            ? LibraryText.Get("dialog.anonymize.duplicate")
            : LibraryText.Get("dialog.anonymize.outside");
        _pendingConvertPath = path;
        _dialog.Show(
            LibraryText.Get("dialog.anonymize.title"),
            message,
            FontAwesomeIcon.UserSecret,
            MirageUi.Yes(ConfirmOutsideAnonymize),
            MirageUi.No());
    }

    private void ConfirmOutsideAnonymize()
    {
        var path = _pendingConvertPath;
        _pendingConvertPath = null;
        if (string.IsNullOrEmpty(path))
            return;

        if (BackupRunning())
            return;

        var adopted = Catalog.Adopt(path);
        if (adopted == null)
        {
            PluginServices.ChatGui.PrintError("Could not move the replay into the library.");
            return;
        }

        AnonymizeFile(adopted, replace: false);
    }

    private void AnonymizeFile(string path, bool replace)
    {
        if (BackupRunning())
            return;

        _ = Task.Run(() =>
        {
            var output = ReplayConvertClient.Anonymize(path, replace);
            var report = ReplayConvertClient.LastReport();
            PluginServices.Framework.RunOnFrameworkThread(() => FinishAnonymize(path, replace, output, report));
        });
    }

    private void FinishAnonymize(string path, bool replace, string? output, string report)
    {
        if (output == null)
        {
            Refresh();
            PluginServices.ChatGui.PrintError(string.IsNullOrEmpty(report) ? "Could not anonymize this replay." : report);
            return;
        }

        if (BackupRunning())
            return;

        Refresh();
        SelectStored(output);
        PluginServices.ChatGui.Print(replace ? "Reanonymized this replay." : "Saved an anonymized copy.");
    }

    private void SelectStored(string path)
    {
        Refresh();
        _selectedReplayPath = path;
    }

    private static void RevealInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            PluginServices.ChatGui.PrintError("Could not find this replay.");
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
        {
            PluginServices.ChatGui.PrintError("Could not find this replay.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private bool TrySelectedReplay(out Catalog.Entry entry)
    {
        if (!string.IsNullOrEmpty(_selectedReplayPath))
        {
            foreach (var item in _entries)
            {
                if (item.Path != _selectedReplayPath)
                    continue;

                entry = item;
                return true;
            }
        }

        entry = default;
        return false;
    }

    private uint ContentTypeIcon(uint contentTypeId)
    {
        if (_types.Count == 0)
            LoadTypes();

        foreach (var row in _types)
        {
            if (row.Id == contentTypeId)
                return row.IconId;
        }

        return 0;
    }

    private static string PatchLabel(string version) =>
        version == "14155597" ? "7.57hf1" : version;

    private static string FormatBuild(uint build) =>
        PatchLabel(build.ToString());
}
