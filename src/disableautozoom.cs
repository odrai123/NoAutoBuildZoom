using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace BuildModeNoAutoZoom
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BuildModeNoAutoZoomPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "lee.dsp.buildmode.noautozoom";
        public const string PluginName = "DSP Build Mode No AutoZoom";
        public const string PluginVersion = "1.0.6";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ExtraMaxZoomOut;
        internal static ConfigEntry<int> ShiftClickPinFrames;
        internal static ManualLogSource Log;

        private static bool _cameraPinAvailable;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
            ExtraMaxZoomOut = Config.Bind("General", "ExtraMaxZoomOut", 2f,
                "Permanent extra maximum zoom-out distance in metres. Set to 0 to disable.");
            ShiftClickPinFrames = Config.Bind("General", "ShiftClickPinFrames", 8,
                "How long to preserve the camera pose while Shift-click enters build mode.");

            var harmony = new Harmony(PluginGuid);
            string cameraStatus;
            _cameraPinAvailable = TryPatchCameraPin(harmony, out cameraStatus);

            string rtsStatus;
            string prtsStatus;
            TryPatchPoser(harmony, "RTSPoser", out rtsStatus);
            TryPatchPoser(harmony, "PRTSPoser", out prtsStatus);

            Logger.LogInfo(string.Format(
                "Compatibility: camera pin={0}; RTS zoom={1}; PRTS zoom={2}.",
                cameraStatus, rtsStatus, prtsStatus));
        }

        private void Update()
        {
            if (!_cameraPinAvailable || !Enabled.Value || !Input.GetMouseButtonDown(0))
                return;

            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
                return;

            if (!BlenderPinPatches.IsBlueprintModeActive())
                BlenderPinPatches.OnShiftClick();
        }

        private bool TryPatchCameraPin(Harmony harmony, out string status)
        {
            string error;
            if (!BlenderPinPatches.TryInitialize(out error))
            {
                status = "disabled (" + error + ")";
                Logger.LogWarning("Camera pin disabled: " + error + ".");
                return false;
            }

            MethodInfo calculate = AccessTools.Method(typeof(CameraPoseBlender), "Calculate");
            if (calculate == null)
            {
                status = "disabled (Calculate missing)";
                Logger.LogWarning("Camera pin disabled: CameraPoseBlender.Calculate was not found.");
                return false;
            }

            try
            {
                harmony.Patch(calculate, prefix: new HarmonyMethod(
                    typeof(BlenderPinPatches), nameof(BlenderPinPatches.CalculatePrefix)));
                status = "enabled";
                return true;
            }
            catch (Exception ex)
            {
                status = "disabled (patch failed)";
                Logger.LogWarning("Camera pin disabled because its Harmony patch failed: " + ex.Message);
                return false;
            }
        }

        private bool TryPatchPoser(Harmony harmony, string typeName, out string status)
        {
            Type type = typeof(CameraPoseBlender).Assembly.GetType(typeName, false);
            if (type == null)
            {
                status = "unavailable";
                return false;
            }

            MethodInfo calculate = AccessTools.Method(type, "Calculate");
            if (calculate == null)
            {
                status = "disabled (Calculate missing)";
                Logger.LogWarning(typeName + " zoom extension disabled: Calculate missing.");
                return false;
            }

            PoserFields fields;
            string error;
            if (!PoserFields.TryCreate(type, out fields, out error))
            {
                status = "disabled (" + error + ")";
                Logger.LogWarning(typeName + " zoom extension disabled: " + error + ".");
                return false;
            }

            try
            {
                PermanentZoomPatches.Register(type, fields);
                harmony.Patch(calculate, prefix: new HarmonyMethod(
                    typeof(PermanentZoomPatches), nameof(PermanentZoomPatches.CalculatePrefix)));
                status = "enabled";
                return true;
            }
            catch (Exception ex)
            {
                PermanentZoomPatches.Unregister(type);
                status = "disabled (patch failed)";
                Logger.LogWarning(typeName + " zoom extension disabled because its Harmony patch failed: " + ex.Message);
                return false;
            }
        }
    }

    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

        public bool Equals(T x, T y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(T obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    internal static class BlenderPinPatches
    {
        private sealed class BlenderState
        {
            internal int LastNonBuildIndex;
            internal bool HasNonBuildIndex;
            internal int Baseline;
            internal bool PinActive;
            internal int ProtectedEpochApplied;
        }

        private static AccessTools.FieldRef<CameraPoseBlender, int> _index;
        private static readonly Dictionary<CameraPoseBlender, BlenderState> States =
            new Dictionary<CameraPoseBlender, BlenderState>(ReferenceComparer<CameraPoseBlender>.Instance);
        private static readonly List<CameraPoseBlender> DeadBlenders = new List<CameraPoseBlender>(64);
        private static bool _lastProtectedActive;
        private static int _protectedEpoch;
        private static int _forcedPinFrames;
        private static int _lastForcedPinFrame = -1;
        private static int _pruneCountdown = 600;

        internal static bool TryInitialize(out string error)
        {
            FieldInfo indexField = AccessTools.Field(typeof(CameraPoseBlender), "index");
            if (indexField == null || indexField.FieldType != typeof(int))
            {
                error = "CameraPoseBlender.index is missing or incompatible";
                return false;
            }

            FieldInfo actionBuild = AccessTools.Field(typeof(PlayerController), "actionBuild");
            if (actionBuild == null || actionBuild.FieldType != typeof(PlayerAction_Build))
            {
                error = "PlayerController.actionBuild is missing or incompatible";
                return false;
            }

            MemberInfo active = (MemberInfo)AccessTools.Property(typeof(PlayerAction_Build), "active")
                ?? AccessTools.Field(typeof(PlayerAction_Build), "active")
                ?? AccessTools.Field(typeof(PlayerAction_Build), "<active>k__BackingField");
            if (GetMemberType(active) != typeof(bool))
            {
                error = "PlayerAction_Build.active is missing or incompatible";
                return false;
            }

            FieldInfo command = AccessTools.Field(typeof(PlayerController), "cmd");
            if (command == null || command.FieldType != typeof(CommandState))
            {
                error = "PlayerController.cmd is missing or incompatible";
                return false;
            }

            FieldInfo commandType = AccessTools.Field(typeof(CommandState), "type");
            if (commandType == null || commandType.FieldType != typeof(ECommand))
            {
                error = "CommandState.type is missing or incompatible";
                return false;
            }

            MemberInfo blueprintMode = (MemberInfo)AccessTools.Property(typeof(PlayerAction_Build), "blueprintMode")
                ?? AccessTools.Field(typeof(PlayerAction_Build), "blueprintMode")
                ?? AccessTools.Field(typeof(PlayerAction_Build), "<blueprintMode>k__BackingField");
            if (GetMemberType(blueprintMode) != typeof(EBlueprintMode))
            {
                error = "PlayerAction_Build.blueprintMode is missing or incompatible";
                return false;
            }

            FieldInfo viewMode = AccessTools.Field(typeof(UIGame), "viewMode");
            if (viewMode == null || viewMode.FieldType != typeof(EViewMode))
            {
                error = "UIGame.viewMode is missing or incompatible";
                return false;
            }

            try
            {
                _index = AccessTools.FieldRefAccess<CameraPoseBlender, int>("index");
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "CameraPoseBlender.index accessor failed: " + ex.Message;
                return false;
            }
        }

        private static Type GetMemberType(MemberInfo member)
        {
            PropertyInfo property = member as PropertyInfo;
            if (property != null)
                return property.PropertyType;

            FieldInfo field = member as FieldInfo;
            return field == null ? null : field.FieldType;
        }

        internal static void OnShiftClick()
        {
            CameraPoseBlender[] blenders = Resources.FindObjectsOfTypeAll<CameraPoseBlender>();
            if (blenders == null)
                return;

            for (int i = 0; i < blenders.Length; i++)
            {
                CameraPoseBlender blender = blenders[i];
                if (blender == null)
                    continue;

                BlenderState state = GetState(blender);
                int index = _index(blender);
                state.LastNonBuildIndex = index;
                state.HasNonBuildIndex = true;
                state.Baseline = index;
                state.PinActive = true;
            }

            _forcedPinFrames = Math.Max(1, BuildModeNoAutoZoomPlugin.ShiftClickPinFrames.Value);
            _lastForcedPinFrame = Time.frameCount;
        }

        internal static bool IsBlueprintModeActive()
        {
            PlayerAction_Build actionBuild = GameMain.mainPlayer?.controller?.actionBuild;
            return actionBuild != null && actionBuild.blueprintMode != EBlueprintMode.None;
        }

        public static void CalculatePrefix(CameraPoseBlender __instance)
        {
            if (__instance == null || !BuildModeNoAutoZoomPlugin.Enabled.Value)
                return;

            AdvanceForcedPinWindow();
            PruneDeadBlenders();

            bool protectedActive = IsProtectedCameraModeActive();
            if (protectedActive != _lastProtectedActive)
            {
                _lastProtectedActive = protectedActive;
                if (protectedActive)
                    _protectedEpoch++;
            }

            BlenderState state = GetState(__instance);
            int currentIndex = _index(__instance);

            if (!protectedActive && _forcedPinFrames <= 0)
            {
                state.LastNonBuildIndex = currentIndex;
                state.HasNonBuildIndex = true;
                state.PinActive = false;
                return;
            }

            if (IsBlueprintModeActive())
                return;

            if (protectedActive && state.ProtectedEpochApplied != _protectedEpoch)
            {
                state.ProtectedEpochApplied = _protectedEpoch;
                state.Baseline = state.HasNonBuildIndex ? state.LastNonBuildIndex : currentIndex;
                state.PinActive = true;
            }
            else if (!protectedActive && _forcedPinFrames > 0 && !state.PinActive)
            {
                state.Baseline = state.HasNonBuildIndex ? state.LastNonBuildIndex : currentIndex;
                state.PinActive = true;
            }

            if (state.PinActive && currentIndex != state.Baseline)
                _index(__instance) = state.Baseline;
        }

        private static BlenderState GetState(CameraPoseBlender blender)
        {
            BlenderState state;
            if (!States.TryGetValue(blender, out state))
            {
                state = new BlenderState();
                States.Add(blender, state);
            }

            return state;
        }

        private static bool IsProtectedCameraModeActive()
        {
            PlayerController controller = GameMain.mainPlayer?.controller;
            if (controller == null)
                return false;

            PlayerAction_Build actionBuild = controller.actionBuild;
            return controller.cmd.type == ECommand.Build ||
                UIGame.viewMode == EViewMode.Build ||
                (actionBuild != null && actionBuild.active);
        }

        private static void AdvanceForcedPinWindow()
        {
            int frame = Time.frameCount;
            if (_forcedPinFrames > 0 && frame != _lastForcedPinFrame)
            {
                _forcedPinFrames--;
                _lastForcedPinFrame = frame;
            }
        }

        private static void PruneDeadBlenders()
        {
            if (--_pruneCountdown > 0)
                return;

            _pruneCountdown = 600;
            DeadBlenders.Clear();
            foreach (KeyValuePair<CameraPoseBlender, BlenderState> entry in States)
            {
                if (entry.Key == null)
                    DeadBlenders.Add(entry.Key);
            }

            for (int i = 0; i < DeadBlenders.Count; i++)
                States.Remove(DeadBlenders[i]);
        }
    }

    internal sealed class PoserFields
    {
        internal readonly FieldInfo Minimum;
        internal readonly FieldInfo Maximum;
        internal readonly FieldInfo Distance;
        internal readonly FieldInfo Coefficient;
        internal readonly FieldInfo WantedCoefficient;
        internal readonly FieldInfo BeginCoefficient;

        private PoserFields(Type type)
        {
            Minimum = AccessTools.Field(type, "distMin");
            Maximum = AccessTools.Field(type, "distMax");
            Distance = AccessTools.Field(type, "dist");
            Coefficient = AccessTools.Field(type, "distCoef");
            WantedCoefficient = GetOptionalFloatField(type, "distCoefWanted");
            BeginCoefficient = GetOptionalFloatField(type, "distCoefBegin");
        }

        internal static bool TryCreate(Type type, out PoserFields fields, out string error)
        {
            fields = new PoserFields(type);
            if (!IsFloat(fields.Minimum) || !IsFloat(fields.Maximum) ||
                !IsFloat(fields.Distance) || !IsFloat(fields.Coefficient))
            {
                fields = null;
                error = "required distance fields are missing or incompatible";
                return false;
            }

            error = null;
            return true;
        }

        private static bool IsFloat(FieldInfo field)
        {
            return field != null && field.FieldType == typeof(float);
        }

        private static FieldInfo GetOptionalFloatField(Type type, string name)
        {
            FieldInfo field = AccessTools.Field(type, name);
            return IsFloat(field) ? field : null;
        }
    }

    internal static class PermanentZoomPatches
    {
        private static readonly Dictionary<Type, PoserFields> Fields = new Dictionary<Type, PoserFields>(2);
        private static readonly HashSet<object> Completed =
            new HashSet<object>(ReferenceComparer<object>.Instance);
        private static readonly HashSet<Type> Failed = new HashSet<Type>();

        internal static void Register(Type type, PoserFields fields)
        {
            Fields[type] = fields;
        }

        internal static void Unregister(Type type)
        {
            Fields.Remove(type);
        }

        public static void CalculatePrefix(object __instance)
        {
            if (!BuildModeNoAutoZoomPlugin.Enabled.Value || __instance == null)
                return;

            float extra = BuildModeNoAutoZoomPlugin.ExtraMaxZoomOut.Value;
            if (extra <= 0.0001f || Completed.Contains(__instance))
                return;

            Type type = __instance.GetType();
            PoserFields fields;
            if (Failed.Contains(type) || !Fields.TryGetValue(type, out fields))
                return;

            try
            {
                float minimum = (float)fields.Minimum.GetValue(__instance);
                float maximum = (float)fields.Maximum.GetValue(__instance);
                float distance = (float)fields.Distance.GetValue(__instance);
                float newMaximum = maximum + extra;
                float span = newMaximum - minimum;
                if (span <= 0.0001f)
                    return;

                float coefficient = Mathf.Clamp01((distance - minimum) / span);
                fields.Maximum.SetValue(__instance, newMaximum);
                fields.Coefficient.SetValue(__instance, coefficient);
                if (fields.WantedCoefficient != null)
                    fields.WantedCoefficient.SetValue(__instance, coefficient);
                if (fields.BeginCoefficient != null)
                    fields.BeginCoefficient.SetValue(__instance, coefficient);

                Completed.Add(__instance);
            }
            catch (Exception ex)
            {
                Failed.Add(type);
                BuildModeNoAutoZoomPlugin.Log.LogWarning(
                    type.Name + " zoom extension disabled after a runtime compatibility failure: " + ex.Message);
            }
        }
    }
}
