// BuildModeNoAutoZoom.cs
//
// BepInEx 5.x / HarmonyX, C# 7.3


using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BuildModeNoAutoZoom
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class BuildModeNoAutoZoomPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "nekogod.dsp.buildmode.noautozoom";
        public const string PluginName = "DSP Build Mode No AutoZoom";
        public const string PluginVersion = "1.0.2";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ExtraMaxZoomOut;

        // Short enforcement window for shift-click path (frames)
        internal static ConfigEntry<int> ShiftClickPinFrames;

        private void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true, "Enable mod.");
            ExtraMaxZoomOut = Config.Bind("General", "ExtraMaxZoomOut", 2f, "Permanent extra max zoom-out distance (meters). 0 disables.");
            ShiftClickPinFrames = Config.Bind("General", "ShiftClickPinFrames", 8,
                "How many frames to enforce the non-build camera pose after Shift+LMB (covers shift-click entry path). -Shouldn't need changing, but increase if you still see auto zooming when shift+clicking.");

            var h = new Harmony(PluginGuid);

            // Patch CameraPoseBlender.Calculate (known-good hook)
            var mCalc = AccessTools.Method(typeof(CameraPoseBlender), "Calculate");
            if (mCalc != null)
            {
                var pre = new HarmonyMethod(typeof(BlenderPinPatches), nameof(BlenderPinPatches.CameraPoseBlender_Calculate_Prefix));
                h.Patch(mCalc, prefix: pre);
            }

            // Patch RTSPoser.Calculate (if present)
            PatchPoserByExactTypeNameFromAssemblyCSharp(h, "RTSPoser");
            PatchPoserByExactTypeNameFromAssemblyCSharp(h, "PRTSPoser"); // harmless if absent
        }

        private void Update()
        {
            if (!Enabled.Value) return;

            // Detect Shift+LMB down before build mode becomes active.
            try
            {
                if (!Input.GetMouseButtonDown(0)) return;
                if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return;

                // Blueprint tools are handled later inside build; here tool is usually null. We still avoid interfering
                // if blueprint is already active for some reason.
                if (BlenderPinPatches.IsBlueprintToolActive_Public())
                    return;

                BlenderPinPatches.OnShiftClick_Public();
            }
            catch { }
        }

        private static void PatchPoserByExactTypeNameFromAssemblyCSharp(Harmony h, string typeName)
        {
            try
            {
                var asm = FindAssembly("Assembly-CSharp");
                if (asm == null) return;

                var t = asm.GetType(typeName, false);
                if (t == null) return;

                var m = AccessTools.Method(t, "Calculate");
                if (m == null) return;

                // Require core fields
                var fMax = AccessTools.Field(t, "distMax");
                var fMin = AccessTools.Field(t, "distMin");
                var fDist = AccessTools.Field(t, "dist");
                var fCoef = AccessTools.Field(t, "distCoef");
                if (fMax == null || fMax.FieldType != typeof(float)) return;
                if (fMin == null || fMin.FieldType != typeof(float)) return;
                if (fDist == null || fDist.FieldType != typeof(float)) return;
                if (fCoef == null || fCoef.FieldType != typeof(float)) return;

                var pre = new HarmonyMethod(typeof(PermanentZoomPatches), nameof(PermanentZoomPatches.Calculate_Prefix));
                h.Patch(m, prefix: pre);
            }
            catch { }
        }

        private static Assembly FindAssembly(string simpleName)
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    var a = asms[i];
                    if (a == null) continue;
                    var n = a.GetName().Name;
                    if (string.Equals(n, simpleName, StringComparison.OrdinalIgnoreCase))
                        return a;
                }
            }
            catch { }
            return null;
        }
    }

    internal static class BlenderPinPatches
    {
        private sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            public static readonly RefEq<T> Instance = new RefEq<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class BlenderState
        {
            public FieldInfo IndexField;
            public int LastNonBuildIndex;
            public bool HasNonBuild;
            public int Baseline;
            public bool PinActive;
        }

        private static readonly Dictionary<CameraPoseBlender, BlenderState> _states =
            new Dictionary<CameraPoseBlender, BlenderState>(RefEq<CameraPoseBlender>.Instance);

        private static bool _lastBuildActive;

        // When > 0, we enforce pinning even if buildActive is still false.
        private static int _forcedPinFrames = 0;

        internal static void OnShiftClick_Public()
        {
            // Snapshot *right now* (still non-build), and start a short enforcement window
            SnapshotAllBlendersAsNonBuild();
            ArmPinningFromSnapshots();
            _forcedPinFrames = Math.Max(1, BuildModeNoAutoZoomPlugin.ShiftClickPinFrames.Value);
        }

        internal static bool IsBlueprintToolActive_Public() => IsBlueprintToolActive();

        private static void SnapshotAllBlendersAsNonBuild()
        {
            try
            {
                var blenders = Resources.FindObjectsOfTypeAll<CameraPoseBlender>();
                if (blenders == null) return;

                for (int i = 0; i < blenders.Length; i++)
                {
                    var b = blenders[i];
                    if (b == null) continue;

                    if (!_states.TryGetValue(b, out var st))
                    {
                        st = new BlenderState();
                        st.IndexField = b.GetType().GetField("index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        _states[b] = st;
                    }

                    if (st.IndexField == null) continue;

                    int idx = (int)st.IndexField.GetValue(b);
                    st.LastNonBuildIndex = idx;
                    st.HasNonBuild = true;
                }
            }
            catch { }
        }

        private static void ArmPinningFromSnapshots()
        {
            foreach (var kv in _states)
            {
                var s = kv.Value;
                if (s == null || s.IndexField == null) continue;

                if (s.HasNonBuild)
                    s.Baseline = s.LastNonBuildIndex;

                s.PinActive = true;
            }
        }

        private static void DisarmPinning()
        {
            foreach (var kv in _states)
                kv.Value.PinActive = false;
        }

        public static void CameraPoseBlender_Calculate_Prefix(CameraPoseBlender __instance)
        {
            if (!BuildModeNoAutoZoomPlugin.Enabled.Value) return;

            try
            {
                bool buildActive = IsBuild();

                // Normal build enter/exit handling
                if (buildActive != _lastBuildActive)
                {
                    if (buildActive)
                    {
                        // On normal entry, baseline from last non-build snapshots we already have
                        foreach (var kv in _states)
                        {
                            var s = kv.Value;
                            if (s.IndexField == null) continue;
                            s.Baseline = s.HasNonBuild ? s.LastNonBuildIndex : (int)s.IndexField.GetValue(kv.Key);
                            s.PinActive = true;
                        }
                    }
                    else
                    {
                        DisarmPinning();
                    }

                    _lastBuildActive = buildActive;
                }

                // Track this blender instance
                if (__instance == null) return;

                if (!_states.TryGetValue(__instance, out var st))
                {
                    st = new BlenderState();
                    st.IndexField = __instance.GetType().GetField("index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _states[__instance] = st;
                }

                if (st.IndexField == null) return;

                int idx = (int)st.IndexField.GetValue(__instance);

                // Update non-build snapshot whenever we are not in build mode and not in forced window
                if (!buildActive && _forcedPinFrames <= 0)
                {
                    st.LastNonBuildIndex = idx;
                    st.HasNonBuild = true;
                    return;
                }

                // Blueprint exception: do not pin during blueprint tools (keeps keyboard panning)
                if ((buildActive || _forcedPinFrames > 0) && IsBlueprintToolActive())
                {
                    // If we’re in forced window, let it expire naturally
                    if (_forcedPinFrames > 0) _forcedPinFrames--;
                    return;
                }

                // Enforce pinning during build mode OR during the short forced window
                if ((buildActive || _forcedPinFrames > 0) && st.PinActive && idx != st.Baseline)
                    st.IndexField.SetValue(__instance, st.Baseline);

                if (_forcedPinFrames > 0) _forcedPinFrames--;

                // Once forced window ends and build is still not active, disarm
                if (_forcedPinFrames <= 0 && !buildActive)
                    DisarmPinning();
            }
            catch { }
        }

        private static bool IsBuild()
        {
            try
            {
                var p = GameMain.mainPlayer;
                var ab = p?.controller?.actionBuild;
                return ab != null && ab.active;
            }
            catch { return false; }
        }

        private static bool IsBlueprintToolActive()
        {
            try
            {
                var p = GameMain.mainPlayer;
                var ab = p?.controller?.actionBuild;
                var tool = ab?.activeTool;
                if (tool == null) return false;

                string tn = tool.GetType().Name;
                return tn.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }

    internal static class PermanentZoomPatches
    {
        private sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            public static readonly RefEq<T> Instance = new RefEq<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly HashSet<object> Done = new HashSet<object>(RefEq<object>.Instance);

        [HarmonyPrefix]
        public static void Calculate_Prefix(object __instance)
        {
            if (!BuildModeNoAutoZoomPlugin.Enabled.Value) return;

            float extra = BuildModeNoAutoZoomPlugin.ExtraMaxZoomOut.Value;
            if (extra <= 0.0001f) return;

            if (!Done.Add(__instance)) return; // once per instance

            try
            {
                var t = __instance.GetType();

                var fMin = AccessTools.Field(t, "distMin");
                var fMax = AccessTools.Field(t, "distMax");
                var fDist = AccessTools.Field(t, "dist");
                var fCoef = AccessTools.Field(t, "distCoef");
                var fWanted = AccessTools.Field(t, "distCoefWanted");
                var fBegin = AccessTools.Field(t, "distCoefBegin");

                if (fMin == null || fMax == null || fDist == null || fCoef == null) return;

                float min = (float)fMin.GetValue(__instance);
                float max = (float)fMax.GetValue(__instance);
                float dist = (float)fDist.GetValue(__instance);

                float newMax = max + extra;
                fMax.SetValue(__instance, newMax);

                float span = newMax - min;
                if (span <= 0.0001f) return;

                float coef = (dist - min) / span;
                if (coef < 0f) coef = 0f; else if (coef > 1f) coef = 1f;

                fCoef.SetValue(__instance, coef);
                if (fWanted != null && fWanted.FieldType == typeof(float)) fWanted.SetValue(__instance, coef);
                if (fBegin != null && fBegin.FieldType == typeof(float)) fBegin.SetValue(__instance, coef);
            }
            catch { }
        }
    }
}
