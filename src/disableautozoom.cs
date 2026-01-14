// BuildModeNoAutoZoom.cs
// Minimal: build-mode auto-zoom suppression + permanent max zoom-out increase (one-time)
//
// BepInEx 5.x / HarmonyX, C# 7.3
// GUID must stay: lee.dsp.buildmode.noautozoom

using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace BuildModeNoAutoZoom
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class BuildModeNoAutoZoomPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "nekogod.dsp.buildmode.noautozoom";
        public const string PluginName = "DSP Build Mode No AutoZoom";
        public const string PluginVersion = "1.0.0";

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ExtraMaxZoomOut;

        private void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true, "Enable mod.");
            ExtraMaxZoomOut = Config.Bind("General", "ExtraMaxZoomOut", 2f, "Permanent extra max zoom-out distance (meters). 0 disables.");

            var h = new Harmony(PluginGuid);
            h.PatchAll(typeof(BlenderPinPatches));

            PatchPoserByName(h, "RTSPoser");
            PatchPoserByName(h, "PRTSPoser");
        }

        private static void PatchPoserByName(Harmony h, string typeName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) return;

                var m = AccessTools.Method(t, "Calculate");
                if (m == null) return;

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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraPoseBlender), "Calculate")]
        private static void Prefix(CameraPoseBlender __instance)
        {
            if (!BuildModeNoAutoZoomPlugin.Enabled.Value) return;

            try
            {
                bool buildActive = IsBuild();

                if (buildActive != _lastBuildActive)
                {
                    if (buildActive)
                    {
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
                        foreach (var kv in _states) kv.Value.PinActive = false;
                    }

                    _lastBuildActive = buildActive;
                }

                if (!_states.TryGetValue(__instance, out var st))
                {
                    st = new BlenderState();
                    st.IndexField = __instance.GetType().GetField("index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _states[__instance] = st;
                }

                if (st.IndexField == null) return;

                int idx = (int)st.IndexField.GetValue(__instance);

                if (!buildActive)
                {
                    st.LastNonBuildIndex = idx;
                    st.HasNonBuild = true;
                    return;
                }

                if (st.PinActive && idx != st.Baseline)
                    st.IndexField.SetValue(__instance, st.Baseline);
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

                float min = (float)fMin.GetValue(__instance);
                float max = (float)fMax.GetValue(__instance);
                float dist = (float)fDist.GetValue(__instance);

                float newMax = max + extra;
                fMax.SetValue(__instance, newMax);

                float span = newMax - min;
                if (span <= 0.0001f) return;

                // Assume normal mapping: dist = min + span*coef
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
