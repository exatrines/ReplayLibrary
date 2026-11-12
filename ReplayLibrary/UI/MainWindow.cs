using System.Diagnostics;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using MirageUI.Layout;
using MirageUI.Ui;
using ReplayLibrary.Library;

namespace ReplayLibrary.UI;

/// <summary>Library and settings in the Mirage window.</summary>
internal sealed partial class MainWindow : Window
{
    internal const string LibraryPageId = "library";
    internal const string SettingsPageId = "settings";
    private const string ContentTypesPageId = "content-types";
    private const string AutoLockPageId = "auto-lock";
    private const string DeletedReplaysPageId = "deleted-replays";

    private static readonly Vector2 ExpandedSize = new(980f, 560f);
    private static readonly Vector4 MismatchColor = new(0.93f, 0.33f, 0.36f, 1f);
    private static readonly Vector4 ClearColor = new(0.40f, 0.78f, 0.48f, 1f);
    private static readonly Vector4 LockColor = new(0.93f, 0.76f, 0.22f, 1f);

    private static readonly MirageWindowFeature[] Features =
    [
        new()
        {
            Id = MirageWindowHeader.PluginPageId,
            PageId = MirageWindowHeader.PluginPageId,
            Icon = FontAwesomeIcon.Heart,
            ShowOnTitle = true,
            TitleKind = MirageWindowFeatureKind.OpenPage,
        },
        new()
        {
            Id = MirageWindowHeader.LanguagePageId,
            PageId = MirageWindowHeader.LanguagePageId,
            Icon = FontAwesomeIcon.Language,
            ShowOnTitle = true,
            TitleKind = MirageWindowFeatureKind.Shortcut,
            TitleOpensPage = true,
            TitleOrder = 2,
            ShowOnRail = true,
            RailKind = MirageWindowFeatureKind.OpenPage,
            RailOrder = 0,
            DrawPopup = DrawLanguagePopup,
            PopupTextWidth = LanguagePopupWidth,
        },
        new()
        {
            Id = MirageWindowHeader.ThemePageId,
            PageId = MirageWindowHeader.ThemePageId,
            Icon = FontAwesomeIcon.Palette,
            ShowOnTitle = true,
            TitleKind = MirageWindowFeatureKind.Shortcut,
            TitleOpensPage = true,
            TitleOrder = 1,
            ShowOnRail = true,
            RailKind = MirageWindowFeatureKind.OpenPage,
            RailOrder = 1,
            DrawPopup = DrawThemePopup,
            PopupTextWidth = ThemePopupWidth,
        },
    ];

    private readonly List<(uint Id, uint IconId, string Name)> _types = [];
    private readonly MirageTwoColumnState _settingsColumns;
    private IReadOnlyList<Catalog.Entry> _entries = [];
    private readonly MirageMessageState _dialog = new();
    private readonly List<string> _pendingDelete = [];
    private string? _selectedReplayPath;
    private string? _pendingConvertPath;
    private string _librarySearch = string.Empty;
    private readonly HashSet<string> _libraryHiddenPatches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _libraryHiddenTypes = new(StringComparer.Ordinal);
    private bool _chromeCollapsed;
    private bool _railCollapsed;
    private string _pageId = LibraryPageId;
    private IDisposable? _themeScope;

    internal MainWindow() : base("Replay Library###replaylibrary", ImGuiWindowFlags.NoDecoration)
    {
        AllowBackgroundBlur = false;
        ApplySize(collapsed: false);
        _settingsColumns = new MirageTwoColumnState
        {
            ShowSearch = true,
            SearchHint = "Search",
            AllowDeselect = false,
            SidebarWidth = 220f,
            SelectedId = ContentTypesPageId,
            SidebarNodes =
            [
                new MirageTwoColumnFolderNode
                {
                    Id = "recording-settings",
                    Label = "Recording Settings",
                    Entries =
                    [
                        new MirageTwoColumnEntry
                        {
                            Id = ContentTypesPageId,
                            Label = "Content Types",
                        },
                    ],
                },
                new MirageTwoColumnFolderNode
                {
                    Id = "file-settings",
                    Label = "File Settings",
                    Entries =
                    [
                        new MirageTwoColumnEntry
                        {
                            Id = AutoLockPageId,
                            Label = "Auto-Lock Rules",
                        },
                        new MirageTwoColumnEntry
                        {
                            Id = DeletedReplaysPageId,
                            Label = "Auto-Delete Rules",
                        },
                    ],
                },
            ],
        };
        MirageUi.I18n.LanguageChanged += ApplySidebarLabels;
        ApplySidebarLabels();
    }

    internal void UnhookUi() =>
        MirageUi.I18n.LanguageChanged -= ApplySidebarLabels;

    private void ApplySidebarLabels()
    {
        _settingsColumns.SearchHint = MirageUi.I18n.Get("window.search");
        foreach (var feature in Features)
        {
            if (feature.Id == MirageWindowHeader.LanguagePageId)
            {
                feature.Tooltip = MirageUi.I18n.Get("window.language");
                feature.OpenPageLabel = MirageUi.I18n.Get("language.settings");
            }
            else if (feature.Id == MirageWindowHeader.ThemePageId)
            {
                feature.Tooltip = MirageUi.I18n.Get("window.theme");
                feature.OpenPageLabel = MirageUi.I18n.Get("theme.settings");
            }
        }
        foreach (var node in _settingsColumns.SidebarNodes)
        {
            switch (node)
            {
                case MirageTwoColumnFolderNode folder:
                    folder.Label = FolderLabel(folder.Id);
                    foreach (var entry in folder.Entries)
                        entry.Label = PageLabel(entry.Id);
                    break;
                case MirageTwoColumnPageNode page:
                    page.Entry.Label = PageLabel(page.Entry.Id);
                    break;
            }
        }
    }

    private static string FolderLabel(string id) => id switch
    {
        "recording-settings" => LibraryText.Get("folder.recording"),
        "file-settings" => LibraryText.Get("folder.files"),
        _ => id,
    };

    private static string PageLabel(string id) => id switch
    {
        ContentTypesPageId => LibraryText.Get("page.contentTypes"),
        AutoLockPageId => LibraryText.Get("page.autoLock"),
        DeletedReplaysPageId => LibraryText.Get("page.autoDelete"),
        _ => id,
    };

    private static void DrawLanguagePopup(string _)
    {
        if (MirageWindowHeader.PopupRow(MirageUi.I18n.Get("language.en"), !MirageUi.I18n.IsJapanese, "lang-en"))
            MirageUi.SelectLanguage(MirageUi.I18n.English);
        if (MirageWindowHeader.PopupRow(MirageUi.I18n.Get("language.ja"), MirageUi.I18n.IsJapanese, "lang-ja"))
            MirageUi.SelectLanguage(MirageUi.I18n.Japanese);
    }

    private static float LanguagePopupWidth() =>
        MathF.Max(
            ImGui.CalcTextSize(MirageUi.I18n.Get("language.en")).X,
            ImGui.CalcTextSize(MirageUi.I18n.Get("language.ja")).X);

    private static void DrawThemePopup(string _)
    {
        foreach (var theme in MirageThemes.All.OrderBy(theme => MirageThemes.IsBuiltin(theme.Id) ? 0 : 1))
        {
            var current = theme.Id == MirageThemes.SelectedId;
            if (MirageWindowHeader.PopupRow(MirageThemes.DisplayName(theme), current, theme.Id) && !current)
                MirageThemes.Select(theme.Id);
        }
    }

    private static float ThemePopupWidth()
    {
        var width = 0f;
        foreach (var theme in MirageThemes.All)
            width = MathF.Max(width, ImGui.CalcTextSize(MirageThemes.DisplayName(theme)).X);
        return width;
    }

    internal bool IsExpanded => IsOpen && !_chromeCollapsed;

    public override void OnOpen()
    {
        Refresh();
        LoadTypes();
    }

    public override void PreDraw()
    {
        ApplySize(_chromeCollapsed);
        _themeScope = MirageTheme.PushCustom(MirageTheme.ResolveAppliedColors());
    }

    public override void Draw()
    {
        MirageWindowHeader.Section[] sections =
        [
            new(LibraryPageId, LibraryText.Get("rail.library"), FontAwesomeIcon.List),
            new(SettingsPageId, LibraryText.Get("rail.settings"), FontAwesomeIcon.Cog),
        ];
        var rail = MirageWindowHeader.RailWidth(_railCollapsed, sections, Features);
        MirageWindowHeader.Draw(this, "Replay Library", ref _chromeCollapsed, 0f, ref _pageId, Features);
        MirageWindowHeader.DrawRail(sections, ref _pageId, ref _railCollapsed, !_chromeCollapsed, Features);
        if (_chromeCollapsed)
        {
            MirageWindowHeader.DragClientArea(this);
            return;
        }

        ImGui.SetCursorPos(new Vector2(rail, MirageWindowHeader.BarHeight));
        MirageWindowHeader.FillContentPage();
        MirageWindowHeader.DrawRailDivider(rail);

        if (_pageId == MirageWindowHeader.PluginPageId)
            MirageUi.DrawPluginPage();
        else if (_pageId == MirageWindowHeader.LanguagePageId)
            MirageUi.DrawLanguagePage();
        else if (_pageId == MirageWindowHeader.ThemePageId)
            MirageUi.DrawThemePage();
        else if (_pageId == SettingsPageId)
            DrawSettings();
        else
            DrawLibrary();

        MirageUi.Message.Draw(_dialog);
        MirageWindowHeader.DragClientArea(this);
    }

    public override void PostDraw()
    {
        MirageWindowCapture.Copy();
        _themeScope?.Dispose();
        _themeScope = null;
    }

    private void ApplySize(bool collapsed)
    {
        var size = collapsed
            ? new Vector2(ExpandedSize.X, MirageWindowHeader.BarHeight)
            : ExpandedSize;
        Size = size;
        SizeCondition = ImGuiCond.Always;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = size,
            MaximumSize = size,
        };
    }

    internal void TogglePage(string pageId)
    {
        if (IsOpen && _pageId == pageId && !_chromeCollapsed)
        {
            IsOpen = false;
            return;
        }

        _pageId = pageId;
        _chromeCollapsed = false;
        IsOpen = true;
    }

    internal void OpenLibrary()
    {
        _pageId = LibraryPageId;
        _chromeCollapsed = false;
        IsOpen = true;
    }

    internal void Refresh() => _entries = Catalog.List(GameBuild.Current);

    private void LoadTypes()
    {
        var instanceTypes = PluginServices.DataManager.GetExcelSheet<ContentFinderCondition>()
            .Select(row => row.ContentType.RowId)
            .ToHashSet();

        _types.Clear();
        _types.AddRange(PluginServices.DataManager.GetExcelSheet<ContentType>()
            .Select(row => (
                Id: row.RowId,
                IconId: row.Icon != 0 ? row.Icon : row.IconDutyFinder,
                Name: Catalog.ContentTypeLabel(row.RowId, row.Name.ToString())))
            .Where(row => instanceTypes.Contains(row.Id) && !string.IsNullOrWhiteSpace(row.Name))
            .OrderBy(row => row.Id));
    }
    private void AskDelete(string title, string message, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        if (BackupRunning())
            return;

        _pendingDelete.Clear();
        _pendingDelete.AddRange(paths);
        _dialog.Show(
            title,
            message,
            FontAwesomeIcon.Trash,
            MirageUi.Yes(ConfirmDelete),
            MirageUi.No());
    }

    private void ConfirmDelete()
    {
        var paths = _pendingDelete.ToArray();
        _pendingDelete.Clear();
        var count = Catalog.Delete(paths);
        if (_selectedReplayPath != null && paths.Contains(_selectedReplayPath))
            _selectedReplayPath = null;
        Refresh();
        PluginServices.ChatGui.Print(count == 1 ? "Deleted 1 replay." : $"Deleted {count} replays.");
    }

    private void OpenLibraryFolder()
    {
        try
        {
            var directory = ReplayPaths.LibraryDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Failed to open the library folder");
            PluginServices.ChatGui.PrintError("Failed to open the library folder.");
        }
    }

    private bool BackupRunning()
    {
        var status = ReplayBackup.MutationBlock();
        if (status == null)
            return false;

        PluginServices.ChatGui.PrintError(status);
        return true;
    }

    private static void DrawActionBar(string id, Vector2 size, System.Action draw)
    {
        if (size.X <= 0f || size.Y <= 0f)
            return;

        var origin = ImGui.GetCursorScreenPos();
        var colors = MirageTheme.Active ?? MirageTheme.ResolveAppliedColors();
        var list = ImGui.GetWindowDrawList();
        list.AddRectFilled(origin, origin + size, ImGui.GetColorU32(colors.FrameBg), 4f);
        list.AddRect(origin, origin + size, ImGui.GetColorU32(colors.Separator), 4f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 8f));
        using var bar = ImRaii.Child(id, size, false, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (bar)
            draw();
    }

    private static void BeginBarRow()
    {
        var buttonH = MirageUi.ResolveControlHeight();
        var textH = ImGui.GetTextLineHeight();
        var shift = MathF.Max(0f, (ImGui.GetContentRegionAvail().Y - buttonH) * 0.5f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + shift + MathF.Max(0f, (buttonH - textH) * 0.5f));
    }

    private static void PlaceBarButton(float buttonWidth)
    {
        var buttonH = MirageUi.ResolveControlHeight();
        var textH = ImGui.GetTextLineHeight();
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - MathF.Max(0f, (buttonH - textH) * 0.5f));
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - buttonWidth));
    }

    private static string RecordedText(Catalog.Entry entry) =>
        entry.Timestamp == 0
            ? string.Empty
            : DateTimeOffset.FromUnixTimeSeconds(entry.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    private static string ReplayLine(Catalog.Entry entry)
    {
        var recorded = RecordedText(entry);
        var line = string.IsNullOrEmpty(recorded) ? entry.FolderName : $"{entry.FolderName} - {recorded}";
        var copy = Catalog.CopyLabel(entry.Path);
        return copy.Length == 0 ? line : $"{line} {copy}";
    }

    private static bool IsMismatch(Catalog.Entry entry) =>
        uint.TryParse(entry.Version, out var build) && build != GameBuild.Current;

}
