using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ReplayLibrary.Game;
using CSVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace ReplayLibrary.Playback;

/// <summary>Locks the world camera to the center, position, and angle presets.</summary>
internal sealed unsafe class CameraLockService : IDisposable
{
    internal const float MinZoom = -20f;
    internal const float MaxZoom = 20f;

    private const int VfUpdate = 3;
    private const int VfSetCameraLookAt = 15;
    private const int VfGetCameraPosition = 16;
    private const int VfGetZoomSpeed = 28;
    private const int VfGetZoomDelta = 29;
    private const float LockedDirVMin = -1.559f;
    private const float LockedDirVMax = 1.559f;
    private const string CollisionCallSig =
        "E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7 01";
    private const string RmiWalkCallSig = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";
    private const string RmiFlyCallSig = "E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8";
    private const string ForceDisableMovementSig = "F3 0F 10 05 ?? ?? ?? ?? 0F 2E C7";

    private static readonly byte[] CollisionPatchBytes = [0x30, 0xC0, 0x90, 0x90, 0x90];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void CameraUpdateDelegate(Camera* camera);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GetCameraPositionDelegate(
        Camera* camera,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* target,
        CSVector3* position,
        byte swapPerson);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetCameraLookAtDelegate(
        Camera* camera,
        CSVector3* lookAtPosition,
        CSVector3* cameraPosition,
        CSVector3* unused);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate float GetZoomSpeedDelegate(Camera* camera);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RmiWalkDelegate(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* a6,
        byte bAdditiveUnk);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RmiFlyDelegate(void* self, FlyInput* result);

    private Hook<CameraUpdateDelegate>? _updateHook;
    private Hook<GetCameraPositionDelegate>? _getPositionHook;
    private Hook<SetCameraLookAtDelegate>? _setLookAtHook;
    private Hook<GetZoomSpeedDelegate>? _zoomSpeedHook;
    private Hook<GetZoomSpeedDelegate>? _zoomDeltaHook;
    private Hook<RmiWalkDelegate>? _rmiWalkHook;
    private Hook<RmiFlyDelegate>? _rmiFlyHook;
    private CodePatch? _collision;
    private int* _forceDisableMovement;
    private bool _hooksReady;
    private bool _active;
    private bool _movementHeld;
    private SavedCameraState _saved;

    private static bool BlockWasd =>
        C.DisableWasdInput && PlaybackService.IsPlaying;

    public void Update()
    {
        if (!C.LockCamera)
        {
            SetActive(false);
            return;
        }

        EnsureHooks();
        if (!_hooksReady)
            return;

        SetActive(true);
        ApplyLockedCamera();
    }

    public void Dispose()
    {
        if (_active)
            SetActive(false);

        DisposeHook(ref _updateHook);
        DisposeHook(ref _getPositionHook);
        DisposeHook(ref _setLookAtHook);
        DisposeHook(ref _zoomSpeedHook);
        DisposeHook(ref _zoomDeltaHook);
        DisposeHook(ref _rmiWalkHook);
        DisposeHook(ref _rmiFlyHook);
        _collision?.Dispose();
        _hooksReady = false;
    }

    internal static void GetCenter(out float x, out float y, out float z)
    {
        switch (C.CameraCenterMode)
        {
            case CameraCenterMode.At100:
                x = 100f;
                y = 0f;
                z = 100f;
                return;
            case CameraCenterMode.AtOrigin:
                x = 0f;
                y = 0f;
                z = 0f;
                return;
            default:
                x = C.CameraX;
                y = C.CameraY;
                z = C.CameraZ;
                return;
        }
    }

    internal static void GetLockOrigin(out float x, out float y, out float z)
    {
        GetCenter(out var cx, out var cy, out var cz);
        switch (C.CameraPosMode)
        {
            case CameraPosMode.High:
                x = cx;
                y = cy + 50f;
                z = cz + 1f;
                return;
            case CameraPosMode.Far:
                x = cx;
                y = cy + 30f;
                z = cz + 40f;
                return;
            default:
                x = C.CameraPosX;
                y = C.CameraPosY;
                z = C.CameraPosZ;
                return;
        }
    }

    private void EnsureHooks()
    {
        if (_hooksReady)
            return;

        var camera = GetWorldCamera();
        if (camera == null)
            return;

        var vtbl = *(nint**)camera;
        TryHookVfunc(vtbl, VfUpdate, ref _updateHook, CameraUpdateDetour);
        TryHookVfunc(vtbl, VfGetCameraPosition, ref _getPositionHook, GetCameraPositionDetour);
        TryHookVfunc(vtbl, VfSetCameraLookAt, ref _setLookAtHook, SetCameraLookAtDetour);
        TryHookVfunc(vtbl, VfGetZoomSpeed, ref _zoomSpeedHook, GetZoomSpeedDetour);
        TryHookVfunc(vtbl, VfGetZoomDelta, ref _zoomDeltaHook, GetZoomDeltaDetour);
        TryResolveCollisionPatch();
        TryResolveMovementHooks();
        _hooksReady = _updateHook != null && _getPositionHook != null;
    }

    private void CameraUpdateDetour(Camera* camera)
    {
        if (_active && camera != null && camera == GetWorldCamera())
            ApplyZoomLock(camera);

        _updateHook?.Original(camera);
        if (!_active || camera == null || camera != GetWorldCamera())
            return;

        ApplyLockedCamera();
        if (C.CameraZoom < 0)
            ApplySceneTransform(camera);
    }

    private float GetZoomSpeedDetour(Camera* camera)
    {
        if (_active && C.DisableMouseWheelZoom)
            return 0f;

        return _zoomSpeedHook?.Original(camera) ?? 0f;
    }

    private float GetZoomDeltaDetour(Camera* camera)
    {
        if (_active && C.DisableMouseWheelZoom)
            return 0f;

        return _zoomDeltaHook?.Original(camera) ?? 0f;
    }

    private void GetCameraPositionDetour(
        Camera* camera,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* target,
        CSVector3* position,
        byte swapPerson)
    {
        if (camera == null || position == null)
            return;

        if (!_active)
        {
            _getPositionHook?.Original(camera, target, position, swapPerson);
            return;
        }

        ApplyLockedAngle(camera);
        GetLockOrigin(out var x, out var y, out var z);
        *position = new CSVector3(x, y, z);
    }

    private void SetCameraLookAtDetour(
        Camera* camera,
        CSVector3* lookAtPosition,
        CSVector3* cameraPosition,
        CSVector3* unused)
    {
        if (camera == null)
            return;

        if (!_active)
        {
            _setLookAtHook?.Original(camera, lookAtPosition, cameraPosition, unused);
            return;
        }

        if (C.CameraZoom >= 0 || lookAtPosition == null || cameraPosition == null)
            return;

        FillLockedSceneVectors(camera, out var eye, out var lookAt);
        *lookAtPosition = lookAt;
        *cameraPosition = eye;
        _setLookAtHook?.Original(camera, lookAtPosition, cameraPosition, unused);
    }

    private void RmiWalkDetour(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* a6,
        byte bAdditiveUnk)
    {
        _rmiWalkHook?.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        if (!_active || !BlockWasd)
            return;

        if (sumLeft != null)
            *sumLeft = 0f;
        if (sumForward != null)
            *sumForward = 0f;
        if (sumTurnLeft != null)
            *sumTurnLeft = 0f;
    }

    private void RmiFlyDetour(void* self, FlyInput* result)
    {
        _rmiFlyHook?.Original(self, result);
        if (!_active || !BlockWasd || result == null)
            return;

        result->Forward = 0f;
        result->Left = 0f;
        result->Up = 0f;
        result->Turn = 0f;
    }

    private void ApplyLockedCamera()
    {
        var camera = GetWorldCamera();
        if (camera == null)
            return;

        ApplyZoomLock(camera);
        camera->DirVMin = LockedDirVMin;
        camera->DirVMax = LockedDirVMax;
        camera->ControlMode = CameraControlMode.ThirdPersonFixed;
        camera->IsEventCameraAutoControl = false;
        ApplyLockedAngle(camera);
        SetMovementHeld(BlockWasd);
        if (C.CameraZoom < 0)
            ApplySceneTransform(camera);
    }

    private void ApplyZoomLock(Camera* camera)
    {
        if (!C.DisableMouseWheelZoom)
        {
            if (_saved.Valid)
            {
                camera->MinDistance = _saved.MinDistance;
                camera->MaxDistance = _saved.MaxDistance;
                camera->MinFoV = _saved.MinFoV;
                camera->MaxFoV = _saved.MaxFoV;
            }

            return;
        }

        var zoom = Math.Clamp(C.CameraZoom, MinZoom, MaxZoom);
        var distance = MathF.Max(MathF.Abs(zoom), 0.01f);
        camera->Distance = distance;
        camera->MinDistance = distance;
        camera->MaxDistance = distance;
        camera->InterpDistance = distance;
        camera->SavedDistance = distance;
        if (!_saved.Valid)
            return;

        camera->FoV = _saved.FoV;
        camera->MinFoV = _saved.FoV;
        camera->MaxFoV = _saved.FoV;
    }

    private static void ApplyLockedAngle(Camera* camera)
    {
        if (!TryGetLockedAngles(out var dirH, out var dirV))
            return;

        camera->DirH = dirH;
        camera->DirV = dirV;
        camera->InputDeltaH = 0f;
        camera->InputDeltaV = 0f;
        camera->InputDeltaHAdjusted = 0f;
        camera->InputDeltaVAdjusted = 0f;
    }

    private static bool TryGetLockedAngles(out float dirH, out float dirV)
    {
        dirH = 0f;
        dirV = 0f;
        if (C.CameraAngleMode == CameraAngleMode.Manual)
        {
            dirH = C.CameraYaw * MathF.PI / 180f;
            dirV = Math.Clamp(C.CameraPitch * MathF.PI / 180f, LockedDirVMin, LockedDirVMax);
            return true;
        }

        float lookX;
        float lookY;
        float lookZ;
        if (C.CameraAngleMode == CameraAngleMode.Center)
        {
            GetCenter(out lookX, out lookY, out lookZ);
        }
        else if (C.CameraAngleMode == CameraAngleMode.LookAt)
        {
            lookX = C.LookAtX;
            lookY = C.LookAtY;
            lookZ = C.LookAtZ;
        }
        else
        {
            return false;
        }

        GetLockOrigin(out var originX, out var originY, out var originZ);
        var dx = lookX - originX;
        var dy = lookY - originY;
        var dz = lookZ - originZ;
        var horizontal = MathF.Sqrt(dx * dx + dz * dz);
        if (horizontal < 0.001f && MathF.Abs(dy) < 0.001f)
            return false;

        dirH = MathF.Atan2(dx, -dz);
        dirV = horizontal < 0.001f
            ? MathF.CopySign(LockedDirVMax, dy)
            : Math.Clamp(MathF.Atan2(dy, horizontal), LockedDirVMin, LockedDirVMax);
        return true;
    }

    private static void ApplySceneTransform(Camera* camera)
    {
        FillLockedSceneVectors(camera, out var eye, out var lookAt);
        camera->SceneCamera.Position = eye;
        camera->SceneCamera.LookAtVector = lookAt;
        camera->LastPosition = eye;
        camera->LastLookAtVector = lookAt;
    }

    private static void FillLockedSceneVectors(Camera* camera, out CSVector3 eye, out CSVector3 lookAt)
    {
        GetLockOrigin(out var ox, out var oy, out var oz);
        var zoom = Math.Clamp(C.CameraZoom, MinZoom, MaxZoom);
        var dirH = camera->DirH;
        var dirV = camera->DirV;
        var cosV = MathF.Cos(dirV);
        var lookX = -cosV * MathF.Sin(dirH);
        var lookY = MathF.Sin(dirV);
        var lookZ = -cosV * MathF.Cos(dirH);
        eye = new CSVector3(ox - lookX * zoom, oy - lookY * zoom, oz - lookZ * zoom);

        if (TryGetVisualLookAt(out var tx, out var ty, out var tz))
        {
            lookAt = new CSVector3(tx, ty, tz);
            var dx = tx - eye.X;
            var dy = ty - eye.Y;
            var dz = tz - eye.Z;
            var horizontal = MathF.Sqrt(dx * dx + dz * dz);
            if (horizontal < 0.001f && MathF.Abs(dy) < 0.001f)
                return;

            camera->DirH = MathF.Atan2(dx, -dz);
            camera->DirV = horizontal < 0.001f
                ? MathF.CopySign(LockedDirVMax, dy)
                : Math.Clamp(MathF.Atan2(dy, horizontal), LockedDirVMin, LockedDirVMax);
            return;
        }

        lookAt = new CSVector3(eye.X + lookX, eye.Y + lookY, eye.Z + lookZ);
    }

    private static bool TryGetVisualLookAt(out float x, out float y, out float z)
    {
        switch (C.CameraAngleMode)
        {
            case CameraAngleMode.Center:
                GetCenter(out x, out y, out z);
                return true;
            case CameraAngleMode.LookAt:
                x = C.LookAtX;
                y = C.LookAtY;
                z = C.LookAtZ;
                return true;
            default:
                x = 0f;
                y = 0f;
                z = 0f;
                return false;
        }
    }

    private void SetActive(bool active)
    {
        if (_active == active)
            return;

        if (active)
        {
            SaveCameraState();
            _collision?.SetEnabled(true);
            SetHooksEnabled(true);
        }
        else
        {
            SetMovementHeld(false);
            RestoreCameraState();
            _collision?.SetEnabled(false);
            SetHooksEnabled(false);
        }

        _active = active;
    }

    private void SetHooksEnabled(bool enabled)
    {
        if (enabled)
        {
            _updateHook?.Enable();
            _getPositionHook?.Enable();
            _setLookAtHook?.Enable();
            _zoomSpeedHook?.Enable();
            _zoomDeltaHook?.Enable();
            _rmiWalkHook?.Enable();
            _rmiFlyHook?.Enable();
            return;
        }

        _updateHook?.Disable();
        _getPositionHook?.Disable();
        _setLookAtHook?.Disable();
        _zoomSpeedHook?.Disable();
        _zoomDeltaHook?.Disable();
        _rmiWalkHook?.Disable();
        _rmiFlyHook?.Disable();
    }

    private void SetMovementHeld(bool hold)
    {
        if (_forceDisableMovement == null || hold == _movementHeld)
            return;

        if (hold)
            *_forceDisableMovement += 1;
        else if (*_forceDisableMovement > 0)
            *_forceDisableMovement -= 1;

        _movementHeld = hold;
    }

    private void SaveCameraState()
    {
        var camera = GetWorldCamera();
        if (camera == null)
            return;

        _saved = new SavedCameraState
        {
            Distance = camera->Distance,
            MinDistance = camera->MinDistance,
            MaxDistance = camera->MaxDistance,
            InterpDistance = camera->InterpDistance,
            FoV = camera->FoV,
            MinFoV = camera->MinFoV,
            MaxFoV = camera->MaxFoV,
            DirVMin = camera->DirVMin,
            DirVMax = camera->DirVMax,
            ControlMode = camera->ControlMode,
            EventAutoControl = camera->IsEventCameraAutoControl,
            Valid = true,
        };
    }

    private void RestoreCameraState()
    {
        if (!_saved.Valid)
            return;

        var camera = GetWorldCamera();
        if (camera != null)
        {
            camera->Distance = _saved.Distance;
            camera->MinDistance = _saved.MinDistance;
            camera->MaxDistance = _saved.MaxDistance;
            camera->InterpDistance = _saved.InterpDistance;
            camera->FoV = _saved.FoV;
            camera->MinFoV = _saved.MinFoV;
            camera->MaxFoV = _saved.MaxFoV;
            camera->DirVMin = _saved.DirVMin;
            camera->DirVMax = _saved.DirVMax;
            camera->ControlMode = _saved.ControlMode;
            camera->IsEventCameraAutoControl = _saved.EventAutoControl;
        }

        _saved = default;
    }

    private void TryResolveCollisionPatch()
    {
        if (_collision != null)
            return;

        try
        {
            var address = PluginServices.SigScanner.ScanText(CollisionCallSig);
            if (address == 0 || Marshal.ReadByte(address) != 0xE8)
                return;

            _collision = CodePatch.TryAt(address, CollisionPatchBytes);
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Camera collision patch was not found");
        }
    }

    private void TryResolveMovementHooks()
    {
        TryHookRelCall(RmiWalkCallSig, ref _rmiWalkHook, RmiWalkDetour);
        TryHookRelCall(RmiFlyCallSig, ref _rmiFlyHook, RmiFlyDetour);
        if (_forceDisableMovement != null)
            return;

        try
        {
            var site = PluginServices.SigScanner.ScanText(ForceDisableMovementSig);
            if (site == 0)
                return;

            var disp = Marshal.ReadInt32(site, 4);
            var address = site + 8 + disp;
            if (address != 0)
                _forceDisableMovement = (int*)address;
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Camera movement lock was not found");
        }
    }

    private void TryHookVfunc<T>(nint* vtbl, int index, ref Hook<T>? hook, T detour)
        where T : Delegate
    {
        if (hook != null)
            return;

        try
        {
            hook = PluginServices.GameInterop.HookFromFunctionPointerVariable<T>((nint)(vtbl + index), detour);
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Camera hook {Index} was not installed", index);
            DisposeHook(ref hook);
        }
    }

    private void TryHookRelCall<T>(string signature, ref Hook<T>? hook, T detour)
        where T : Delegate
    {
        if (hook != null)
            return;

        try
        {
            var callSite = PluginServices.SigScanner.ScanText(signature);
            if (callSite == 0 || Marshal.ReadByte(callSite) != 0xE8)
                return;

            var target = callSite + 5 + Marshal.ReadInt32(callSite, 1);
            hook = PluginServices.GameInterop.HookFromAddress<T>(target, detour);
        }
        catch (Exception e)
        {
            PluginServices.Log.Warning(e, "Camera call hook was not installed");
            DisposeHook(ref hook);
        }
    }

    private static Camera* GetWorldCamera()
    {
        var manager = CameraManager.Instance();
        return manager == null ? null : manager->GetActiveCamera();
    }

    private static void DisposeHook<T>(ref Hook<T>? hook) where T : Delegate
    {
        hook?.Disable();
        hook?.Dispose();
        hook = null;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    private struct FlyInput
    {
        [FieldOffset(0x0)] public float Forward;
        [FieldOffset(0x4)] public float Left;
        [FieldOffset(0x8)] public float Up;
        [FieldOffset(0xC)] public float Turn;
    }

    private struct SavedCameraState
    {
        public float Distance;
        public float MinDistance;
        public float MaxDistance;
        public float InterpDistance;
        public float FoV;
        public float MinFoV;
        public float MaxFoV;
        public float DirVMin;
        public float DirVMax;
        public CameraControlMode ControlMode;
        public bool EventAutoControl;
        public bool Valid;
    }
}
