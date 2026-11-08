using Dalamud.Configuration;

namespace ReplayLibrary;

public enum CameraCenterMode
{
    At100 = 0,
    AtOrigin = 1,
    Manual = 2,
}

public enum CameraPosMode
{
    High = 0,
    Far = 1,
    Manual = 2,
}

public enum CameraAngleMode
{
    Off = 0,
    Center = 1,
    LookAt = 2,
    Manual = 3,
}

public enum EntityDisplayMode
{
    DoNothing = 0,
    Hide = 1,
    ReplaceToDummy = 2,
}

public enum PlayerDummyLabelMode
{
    None = 0,
    Role = 1,
    Job = 2,
}

/// <summary>Saved recording, keep, and playback settings.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public static readonly uint[] DefaultContentTypes = [2, 4, 5, 28, 27, 40, 30, 21, 6];

    public int Version { get; set; } = 1;

    public List<uint> RecordedContentTypes { get; set; } = [.. DefaultContentTypes];

    public int MaxLibraryGigabytes { get; set; } = 5;

    public int DeletedKeepDays { get; set; } = 7;

    public int DefaultKeepCount { get; set; }

    public List<KeepCount> KeepCounts { get; set; } = CreateDefaultKeepCounts();

    public bool ReplayBackupCompleted { get; set; }

    public long LibraryBackupUnixSeconds { get; set; }

    public bool HideOwnName { get; set; }

    public bool QuickChapterLoad { get; set; } = true;

    public bool JumpToTime { get; set; }

    public float SeekRate { get; set; } = 100f;

    public bool ShowSpeedButtons { get; set; }

    public float SpeedPreset { get; set; } = 30f;

    public bool ShowWaymarks { get; set; } = true;

    public bool PinPlaybackBar { get; set; } = true;

    public bool LockCamera { get; set; }

    public CameraCenterMode CameraCenterMode { get; set; }

    public float CameraX { get; set; }

    public float CameraY { get; set; }

    public float CameraZ { get; set; }

    public CameraPosMode CameraPosMode { get; set; }

    public float CameraPosX { get; set; }

    public float CameraPosY { get; set; }

    public float CameraPosZ { get; set; }

    public CameraAngleMode CameraAngleMode { get; set; }

    public float CameraYaw { get; set; }

    public float CameraPitch { get; set; }

    public float LookAtX { get; set; }

    public float LookAtY { get; set; }

    public float LookAtZ { get; set; }

    public bool DisableWasdInput { get; set; } = true;

    public bool DisableMouseWheelZoom { get; set; } = true;

    public float CameraZoom { get; set; } = 0.06f;

    public EntityDisplayMode PlayerMode { get; set; } = EntityDisplayMode.ReplaceToDummy;

    public EntityDisplayMode PetMode { get; set; } = EntityDisplayMode.ReplaceToDummy;

    public EntityDisplayMode NpcMode { get; set; }

    public EntityDisplayMode EnemyMode { get; set; }

    public float DummySize { get; set; } = 1f;

    public PlayerDummyLabelMode PlayerDummyLabel { get; set; } = PlayerDummyLabelMode.Role;

    public Vector4 DummyColorTank { get; set; } = new(0.25f, 0.45f, 0.98f, 0.86f);

    public Vector4 DummyColorHealer { get; set; } = new(0.22f, 0.82f, 0.38f, 0.86f);

    public Vector4 DummyColorDps { get; set; } = new(0.95f, 0.32f, 0.32f, 0.86f);

    public Vector4 DummyColorCrafter { get; set; } = new(0.95f, 0.62f, 0.22f, 0.86f);

    public Vector4 DummyColorGatherer { get; set; } = new(0.22f, 0.78f, 0.72f, 0.86f);

    public Vector4 DummyColorPet { get; set; } = new(0.82f, 0.82f, 0.86f, 0.72f);

    public Vector4 DummyColorNpc { get; set; } = new(0.72f, 0.55f, 0.92f, 0.86f);

    public Vector4 DummyColorEnemy { get; set; } = new(0.95f, 0.45f, 0.18f, 0.86f);

    public string SelectedThemeId { get; set; } = "";

    public List<MirageSavedTheme> Themes { get; set; } = [];

    public string UiLanguage { get; set; } = "";

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface) =>
        _pluginInterface = pluginInterface;

    public bool Allows(uint contentType) =>
        RecordedContentTypes.Contains(contentType);

    public void SetRecordedContentTypes(IEnumerable<uint> contentTypes)
    {
        RecordedContentTypes = contentTypes.Distinct().ToList();
        Save();
    }

    public int ResolveKeep(uint contentId, uint typeId, uint patchId)
    {
        if (contentId != 0 && TryCount(KeepCount.Content, 0, 0, contentId, out var content))
            return content;
        return InheritedCount(contentId == 0 ? (byte)0 : KeepCount.Content, typeId, patchId);
    }

    public int InheritedCount(byte scope, uint typeId, uint patchId)
    {
        if (scope == KeepCount.Content
            && typeId != 0
            && TryCount(KeepCount.Type, typeId, patchId, 0, out var type))
            return type;
        if (scope != KeepCount.Version && TryCount(KeepCount.Version, 0, patchId, 0, out var version))
            return version;
        return Math.Max(0, DefaultKeepCount);
    }

    public bool TryCount(byte scope, uint typeId, uint patchId, uint contentId, out int count)
    {
        if (TryKeepRule(scope, typeId, patchId, contentId, out var rule))
        {
            count = Math.Max(0, rule.Count);
            return true;
        }

        count = 0;
        return false;
    }

    public bool TryKeepRule(byte scope, uint typeId, uint patchId, uint contentId, out KeepCount rule)
    {
        rule = null!;
        if (KeepCounts == null)
            return false;

        foreach (var candidate in KeepCounts)
        {
            if (candidate.Scope != scope || !SameKeep(candidate, scope, typeId, patchId, contentId))
                continue;
            if (!candidate.HasCount)
                continue;

            rule = candidate;
            return true;
        }

        return false;
    }

    public void SetDefaultKeepCount(int count)
    {
        DefaultKeepCount = Math.Max(0, count);
        Save();
    }

    public void SetKeep(byte scope, uint typeId, uint patchId, uint contentId, int? count)
    {
        KeepCounts ??= [];
        KeepCounts.RemoveAll(rule => rule.Scope == scope && SameKeep(rule, scope, typeId, patchId, contentId));
        if (count is int value)
        {
            KeepCounts.Add(new KeepCount
            {
                Scope = scope,
                TypeId = typeId,
                PatchId = patchId,
                ContentId = contentId,
                HasCount = true,
                Count = Math.Max(0, value),
            });
        }

        Save();
    }

    public void ResetKeepCounts()
    {
        DefaultKeepCount = 0;
        KeepCounts = CreateDefaultKeepCounts();
        Save();
    }

    private static List<KeepCount> CreateDefaultKeepCounts() =>
    [
        KeepRule(KeepCount.Version, 0, 5, 5),
        KeepRule(KeepCount.Type, 28, 2, 10),
        KeepRule(KeepCount.Type, 28, 3, 10),
        KeepRule(KeepCount.Type, 28, 4, 10),
        KeepRule(KeepCount.Type, 28, 5, 10),
    ];

    private static KeepCount KeepRule(byte scope, uint typeId, uint patchId, int count) =>
        new()
        {
            Scope = scope,
            TypeId = typeId,
            PatchId = patchId,
            HasCount = true,
            Count = count,
        };

    private static bool SameKeep(KeepCount rule, byte scope, uint typeId, uint patchId, uint contentId) =>
        scope switch
        {
            KeepCount.Content => rule.ContentId == contentId,
            KeepCount.Type => rule.TypeId == typeId && rule.PatchId == patchId,
            _ => rule.PatchId == patchId,
        };

    public void Save() => _pluginInterface?.SavePluginConfig(this);
}

public sealed class KeepCount
{
    public const byte Version = 1;
    public const byte Type = 2;
    public const byte Content = 3;

    public byte Scope { get; set; }

    public uint TypeId { get; set; }

    public uint PatchId { get; set; }

    public uint ContentId { get; set; }

    public int Count { get; set; }

    public bool HasCount { get; set; }
}
