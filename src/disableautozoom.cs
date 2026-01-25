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
        public const string PluginVersion = "1.0.3";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ExtraMaxZoomOut;

        // Short enforcement window for Shift+LMB path (frames).
        // Build mode may become active a few frames after the click; this bridges that gap.
        internal static ConfigEntry<int> ShiftClickPinFrames;

        private void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true, "Enable mod.");
            ExtraMaxZoomOut = Config.Bind("General", "ExtraMaxZoomOut", 2f,
                "Permanent extra max zoom-out distance (meters). 0 disables.");
            ShiftClickPinFrames = Config.Bind("General", "ShiftClickPinFrames", 8, "How many frames to enforce the non-build camera pose after Shift+LMB (covers shift-click entry path). -Shouldn't need changing, but increase if you still see auto zooming when shift+clicking.");

            var h = new Harmony(PluginGuid);

            // Patch CameraPoseBlender.Calculate (core hook for pinning the pose index).
            var mCalc = AccessTools.Method(typeof(CameraPoseBlender), "Calculate");
            if (mCalc != null)
            {
                var pre = new HarmonyMethod(typeof(BlenderPinPatches),
                    nameof(BlenderPinPatches.CameraPoseBlender_Calculate_Prefix));
                h.Patch(mCalc, prefix: pre);
            }

            // Patch RTSPoser.Calculate / PRTSPoser.Calculate (if present) to apply a permanent max zoom-out extension.
            PatchPoserByExactTypeNameFromAssemblyCSharp(h, "RTSPoser");
            PatchPoserByExactTypeNameFromAssemblyCSharp(h, "PRTSPoser");
        }

        private void Update()
        {
            if (!Enabled.Value) return;

            // Detect Shift+LMB before build mode becomes active.
            if (!Input.GetMouseButtonDown(0)) return;
            if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return;

            // Avoid interfering if a blueprint tool is already active (rare but harmless).
            if (BlenderPinPatches.IsBlueprintToolActive_Public())
                return;

            BlenderPinPatches.OnShiftClick_Public();
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

                // Require core float fields. If they aren't present, skip patching safely.
                var fMax = AccessTools.Field(t, "distMax");
                var fMin = AccessTools.Field(t, "distMin");
                var fDist = AccessTools.Field(t, "dist");
                var fCoef = AccessTools.Field(t, "distCoef");
                if (fMax == null || fMax.FieldType != typeof(float)) return;
                if (fMin == null || fMin.FieldType != typeof(float)) return;
                if (fDist == null || fDist.FieldType != typeof(float)) return;
                if (fCoef == null || fCoef.FieldType != typeof(float)) return;

                var pre = new HarmonyMethod(typeof(PermanentZoomPatches),
                    nameof(PermanentZoomPatches.Calculate_Prefix));
                h.Patch(m, prefix: pre);
            }
            catch
            {
                // Silent: failing to patch poser types should never break gameplay.
            }
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
        // Reference-identity comparer for Unity objects (fast, stable).
        private sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            public static readonly RefEq<T> Instance = new RefEq<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class BlenderState
        {
            // Last known "non-build" pose index (captured while not building).
            public int LastNonBuildIndex;
            public bool HasNonBuild;

            // Baseline pose index we pin to while building / during forced window.
            public int Baseline;
            public bool PinActive;

            // Epoch markers (avoid O(N) loops on build enter/exit).
            public int BuildEpochApplied;
            public int ShiftEpochApplied;
        }

        // Fast access to CameraPoseBlender.index (no reflection, no boxing).
        private static readonly AccessTools.FieldRef<CameraPoseBlender, int> IndexRef =
            AccessTools.FieldRefAccess<CameraPoseBlender, int>("index");

        // Track blender -> state. We prune occasionally to avoid unbounded growth.
        private static readonly Dictionary<CameraPoseBlender, BlenderState> _states =
            new Dictionary<CameraPoseBlender, BlenderState>(RefEq<CameraPoseBlender>.Instance);

        // Build state tracking.
        private static bool _lastBuildActive;
        private static int _buildEpoch;

        // Shift-click enforcement window (frames). While > 0, we pin even if buildActive is still false.
        private static int _forcedPinFrames;

        // Shift snapshot epoch counter.
        private static int _shiftEpoch;

        // Occasional pruning so _states doesn't grow unbounded over long sessions.
        private static int _pruneCountdown = 600; // ~once per 600 Calculate calls
        private static readonly List<CameraPoseBlender> _pruneKeys = new List<CameraPoseBlender>(64);

        // Cache: BuildTool Type -> "is blueprint tool?"
        private static readonly Dictionary<Type, bool> _blueprintTypeCache = new Dictionary<Type, bool>(16);

        internal static void OnShiftClick_Public()
        {
            // Shift-click path:
            // - Snapshot current (non-build) pose index immediately for *all* blenders
            // - Arm pinning based on that snapshot
            // - Start a short enforcement window bridging the few frames before buildActive flips true
            _shiftEpoch++;
            SnapshotAllBlendersAsNonBuildAndArmShiftEpoch(_shiftEpoch);
            _forcedPinFrames = Math.Max(1, BuildModeNoAutoZoomPlugin.ShiftClickPinFrames.Value);
        }

        internal static bool IsBlueprintToolActive_Public() => IsBlueprintToolActive();

        private static void SnapshotAllBlendersAsNonBuildAndArmShiftEpoch(int shiftEpoch)
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
                        _states[b] = st;
                    }

                    // Snapshot current index as a "non-build" baseline.
                    int idx = IndexRef(b);
                    st.LastNonBuildIndex = idx;
                    st.HasNonBuild = true;

                    // Arm pinning immediately from this snapshot for the forced window.
                    st.Baseline = idx;
                    st.PinActive = true;
                    st.ShiftEpochApplied = shiftEpoch;
                }
            }
            catch
            {
                // Silent: snapshot is an optimization for a specific entry path; failure should not break gameplay.
            }
        }

        public static void CameraPoseBlender_Calculate_Prefix(CameraPoseBlender __instance)
        {
            if (__instance == null) return;

            PruneDeadOccasionally();

            if (!BuildModeNoAutoZoomPlugin.Enabled.Value) return;

            bool buildActive = IsBuild();

            // On build enter/exit we do NOT loop over all blenders.
            // Instead we bump an epoch on entry; each blender lazily initializes its baseline once per epoch.
            if (buildActive != _lastBuildActive)
            {
                _lastBuildActive = buildActive;
                if (buildActive)
                    _buildEpoch++;
                // On exit: no work here; each blender self-disarms when it runs next in non-build.
            }

            if (!_states.TryGetValue(__instance, out var st))
            {
                st = new BlenderState();
                _states[__instance] = st;
            }

            int idxNow = IndexRef(__instance);

            // In true non-build and not in forced window:
            // - record snapshot (authoritative "non-build pose")
            // - disarm pinning for this blender (lazy, per-instance)
            if (!buildActive && _forcedPinFrames <= 0)
            {
                st.LastNonBuildIndex = idxNow;
                st.HasNonBuild = true;
                st.PinActive = false;
                return;
            }

            // Blueprint tools: don't pin (keeps blueprint keyboard/mouse behaviour intact).
            if ((buildActive || _forcedPinFrames > 0) && IsBlueprintToolActive())
            {
                if (_forcedPinFrames > 0) _forcedPinFrames--;
                return;
            }

            // If we are in build mode, lazily initialize baseline once per build epoch.
            if (buildActive && st.BuildEpochApplied != _buildEpoch)
            {
                st.BuildEpochApplied = _buildEpoch;

                // Prefer last known non-build snapshot; otherwise fall back to current index.
                st.Baseline = st.HasNonBuild ? st.LastNonBuildIndex : idxNow;
                st.PinActive = true;
            }

            // If build isn't active yet but we're within forced window, ensure pinning is armed.
            // (Normally shift snapshot already did this; this is a safety net.)
            if (!buildActive && _forcedPinFrames > 0 && !st.PinActive)
            {
                st.Baseline = st.HasNonBuild ? st.LastNonBuildIndex : idxNow;
                st.PinActive = true;
            }

            // Enforce pinning during build mode OR during the short forced window.
            if ((buildActive || _forcedPinFrames > 0) && st.PinActive && idxNow != st.Baseline)
            {
                IndexRef(__instance) = st.Baseline;
            }

            if (_forcedPinFrames > 0) _forcedPinFrames--;

            // Once forced window ends and build still isn't active, self-disarm (no global loop).
            if (_forcedPinFrames <= 0 && !buildActive)
            {
                st.PinActive = false;
            }
        }

        private static void PruneDeadOccasionally()
        {
            if (--_pruneCountdown > 0) return;
            _pruneCountdown = 600;

            _pruneKeys.Clear();
            foreach (var kv in _states)
            {
                // Unity destroyed objects compare equal to null
                if (kv.Key == null) _pruneKeys.Add(kv.Key);
            }

            for (int i = 0; i < _pruneKeys.Count; i++)
                _states.Remove(_pruneKeys[i]);
        }

        private static bool IsBuild()
        {
            var p = GameMain.mainPlayer;
            var ab = p?.controller?.actionBuild;
            return ab != null && ab.active;
        }

        private static bool IsBlueprintToolActive()
        {
            var tool = GameMain.mainPlayer?.controller?.actionBuild?.activeTool;
            if (tool == null) return false;

            var t = tool.GetType();
            if (_blueprintTypeCache.TryGetValue(t, out var cached)) return cached;

            bool isBp = t.Name.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) >= 0;
            _blueprintTypeCache[t] = isBp;
            return isBp;
        }
    }

    internal static class PermanentZoomPatches
    {
        // Reference-identity comparer for "Done" set.
        private sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            public static readonly RefEq<T> Instance = new RefEq<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        // Apply zoom extension only once per poser instance.
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

                // Core fields required for the zoom tweak.
                var fMin = AccessTools.Field(t, "distMin");
                var fMax = AccessTools.Field(t, "distMax");
                var fDist = AccessTools.Field(t, "dist");
                var fCoef = AccessTools.Field(t, "distCoef");

                // Optional fields (not always present across versions/types).
                var fWanted = AccessTools.Field(t, "distCoefWanted");
                var fBegin = AccessTools.Field(t, "distCoefBegin");

                if (fMin == null || fMax == null || fDist == null || fCoef == null) return;

                float min = (float)fMin.GetValue(__instance);
                float max = (float)fMax.GetValue(__instance);
                float dist = (float)fDist.GetValue(__instance);

                // Extend max distance.
                float newMax = max + extra;
                fMax.SetValue(__instance, newMax);

                // Recompute normalized coefficient so current dist maps correctly into [min..newMax].
                float span = newMax - min;
                if (span <= 0.0001f) return;

                float coef = (dist - min) / span;
                if (coef < 0f) coef = 0f;
                else if (coef > 1f) coef = 1f;

                fCoef.SetValue(__instance, coef);

                // Keep internal lerp/transition fields consistent if they exist.
                if (fWanted != null && fWanted.FieldType == typeof(float)) fWanted.SetValue(__instance, coef);
                if (fBegin != null && fBegin.FieldType == typeof(float)) fBegin.SetValue(__instance, coef);
            }
            catch
            {
                // Silent: if this fails for a poser type, it should never affect core gameplay.
            }
        }
    }
}
