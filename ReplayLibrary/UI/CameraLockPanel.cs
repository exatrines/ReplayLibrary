using ReplayLibrary.Playback;

namespace ReplayLibrary.UI;

/// <summary>Camera lock presets in the playback popup.</summary>
internal static class CameraLockPanel
{
    private const float LabelWidth = 88f;

    internal static void Draw()
    {
        if (CammyLoaded())
            MirageUi.Warning(LibraryText.Get("camera.cammy"), ImGui.GetContentRegionAvail().X);

        var locked = C.LockCamera;
        if (MirageUi.Checkbox(LibraryText.Get("camera.lock"), ref locked))
        {
            C.LockCamera = locked;
            C.Save();
        }

        MirageUi.Tooltip(LibraryText.Get("camera.collision"));
        DrawCenter();
        DrawPosition();
        DrawAngle();
        DrawWasd();
        DrawZoom();
    }

    private static void DrawCenter()
    {
        var labels = new[]
        {
            LibraryText.Get("camera.center.100"),
            LibraryText.Get("camera.center.origin"),
            LibraryText.Get("camera.center.manual"),
        };
        if (Pick("camera.center", labels, (int)C.CameraCenterMode, out var next)
            && next != (int)C.CameraCenterMode)
        {
            if ((CameraCenterMode)next == CameraCenterMode.Manual)
            {
                CameraLockService.GetCenter(out var x, out var y, out var z);
                C.CameraX = x;
                C.CameraY = y;
                C.CameraZ = z;
            }

            C.CameraCenterMode = (CameraCenterMode)next;
            C.Save();
        }

        if (C.CameraCenterMode != CameraCenterMode.Manual)
            return;

        var xValue = C.CameraX;
        var yValue = C.CameraY;
        var zValue = C.CameraZ;
        if (!(MirageUi.InputFloat(LibraryText.Get("camera.x"), ref xValue, format: "%.2f", id: "camera-x", labelWidth: LabelWidth)
            | MirageUi.InputFloat(LibraryText.Get("camera.y"), ref yValue, format: "%.2f", id: "camera-y", labelWidth: LabelWidth)
            | MirageUi.InputFloat(LibraryText.Get("camera.z"), ref zValue, format: "%.2f", id: "camera-z", labelWidth: LabelWidth)))
            return;

        C.CameraX = xValue;
        C.CameraY = yValue;
        C.CameraZ = zValue;
        C.Save();
    }

    private static void DrawPosition()
    {
        var labels = new[]
        {
            LibraryText.Get("camera.pos.high"),
            LibraryText.Get("camera.pos.far"),
            LibraryText.Get("camera.pos.manual"),
        };
        if (Pick("camera.pos", labels, (int)C.CameraPosMode, out var next)
            && next != (int)C.CameraPosMode)
        {
            if ((CameraPosMode)next == CameraPosMode.Manual)
            {
                CameraLockService.GetLockOrigin(out var x, out var y, out var z);
                C.CameraPosX = x;
                C.CameraPosY = y;
                C.CameraPosZ = z;
            }

            C.CameraPosMode = (CameraPosMode)next;
            C.Save();
        }

        if (C.CameraPosMode != CameraPosMode.Manual)
            return;

        var xValue = C.CameraPosX;
        var yValue = C.CameraPosY;
        var zValue = C.CameraPosZ;
        var changed = MirageUi.InputFloat(LibraryText.Get("camera.x"), ref xValue, format: "%.2f", id: "camera-pos-x", labelWidth: LabelWidth)
            | MirageUi.InputFloat(LibraryText.Get("camera.y"), ref yValue, format: "%.2f", id: "camera-pos-y", labelWidth: LabelWidth)
            | MirageUi.InputFloat(LibraryText.Get("camera.z"), ref zValue, format: "%.2f", id: "camera-pos-z", labelWidth: LabelWidth);
        if (!changed)
            return;

        C.CameraPosX = xValue;
        C.CameraPosY = yValue;
        C.CameraPosZ = zValue;
        C.Save();
    }

    private static void DrawAngle()
    {
        var labels = new[]
        {
            LibraryText.Get("camera.angle.off"),
            LibraryText.Get("camera.angle.center"),
            LibraryText.Get("camera.angle.lookAt"),
            LibraryText.Get("camera.angle.manual"),
        };
        if (Pick("camera.angle", labels, (int)C.CameraAngleMode, out var next)
            && next != (int)C.CameraAngleMode)
        {
            if ((CameraAngleMode)next == CameraAngleMode.LookAt)
            {
                CameraLockService.GetCenter(out var x, out var y, out var z);
                C.LookAtX = x;
                C.LookAtY = y;
                C.LookAtZ = z;
            }

            C.CameraAngleMode = (CameraAngleMode)next;
            C.Save();
        }

        if (C.CameraAngleMode == CameraAngleMode.LookAt)
        {
            var xValue = C.LookAtX;
            var yValue = C.LookAtY;
            var zValue = C.LookAtZ;
            if (MirageUi.InputFloat(LibraryText.Get("camera.x"), ref xValue, format: "%.2f", id: "camera-look-x", labelWidth: LabelWidth)
                | MirageUi.InputFloat(LibraryText.Get("camera.y"), ref yValue, format: "%.2f", id: "camera-look-y", labelWidth: LabelWidth)
                | MirageUi.InputFloat(LibraryText.Get("camera.z"), ref zValue, format: "%.2f", id: "camera-look-z", labelWidth: LabelWidth))
            {
                C.LookAtX = xValue;
                C.LookAtY = yValue;
                C.LookAtZ = zValue;
                C.Save();
            }
        }
        else if (C.CameraAngleMode == CameraAngleMode.Manual)
        {
            var yaw = C.CameraYaw;
            var pitch = C.CameraPitch;
            if (MirageUi.InputFloat(LibraryText.Get("camera.yaw"), ref yaw, format: "%.1f", id: "camera-yaw", labelWidth: LabelWidth)
                | MirageUi.InputFloat(LibraryText.Get("camera.pitch"), ref pitch, format: "%.1f", id: "camera-pitch", labelWidth: LabelWidth))
            {
                C.CameraYaw = yaw;
                C.CameraPitch = pitch;
                C.Save();
            }
        }
    }

    private static void DrawWasd()
    {
        var wasd = C.DisableWasdInput;
        if (MirageUi.Checkbox(LibraryText.Get("camera.wasd"), ref wasd))
        {
            C.DisableWasdInput = wasd;
            C.Save();
        }

        MirageUi.Tooltip(LibraryText.Get("camera.wasd.hint"));
    }

    private static void DrawZoom()
    {
        var wheel = C.DisableMouseWheelZoom;
        if (MirageUi.Checkbox(LibraryText.Get("camera.zoom"), ref wheel))
        {
            C.DisableMouseWheelZoom = wheel;
            C.Save();
        }

        if (!C.DisableMouseWheelZoom)
            return;

        var zoom = C.CameraZoom;
        if (!MirageUi.SliderFloat(
                LibraryText.Get("camera.zoom.value"),
                ref zoom,
                CameraLockService.MinZoom,
                CameraLockService.MaxZoom,
                "%.2f",
                "camera-zoom",
                labelWidth: LabelWidth))
            return;

        C.CameraZoom = zoom;
        C.Save();
    }

    private static bool Pick(string labelKey, string[] labels, int current, out int next)
    {
        next = current;
        if (current < 0 || current >= labels.Length)
            return false;

        var selected = labels[current];
        if (!MirageUi.Dropdown(LibraryText.Get(labelKey), ref selected, labels, allowClear: false, id: labelKey, labelWidth: LabelWidth))
            return false;

        var index = Array.IndexOf(labels, selected);
        if (index < 0)
            return false;

        next = index;
        return true;
    }

    private static bool CammyLoaded() =>
        PluginServices.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.InternalName == "Cammy" && plugin.IsLoaded);
}
