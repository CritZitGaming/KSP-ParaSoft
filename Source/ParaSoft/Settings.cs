using System;
using ParaSoft.Core;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// Modder-level tuning from ParaSoft/Settings.cfg. Player-facing switches are in
    /// ParaSoftParameters; these are the physical constants behind them, with every
    /// default documented in the cfg itself.
    /// </summary>
    internal static class Settings
    {
        internal static float ArealDensity = 0.05f;
        internal static float LineDensity = 0.012f;
        internal static float Porosity = 0.08f;
        internal static float FillDistance = 2.5f;
        internal static float ApparentMass = 0.35f;
        internal static float Stiffness = 1f;
        internal static float Stability = 0.8f;
        internal static float EjectSpeed = 12f;
        internal static float SubstepsPerSecond = 200f;
        /// <summary>Beyond this distance from the camera a canopy runs at half rate.</summary>
        internal static float LodDistance = 600f;
        /// <summary>Beyond this distance a canopy is frozen until the camera comes back.</summary>
        internal static float FreezeDistance = 2500f;
        /// <summary>A cut canopy this far from both the camera and the active craft is removed.</summary>
        internal static float CutCanopyRange = 750f;
        /// <summary>Most canopies simulated at once; any more keep their stock animation.</summary>
        internal static int MaxCanopies = 24;
        /// <summary>Largest vertex count a canopy model may have before it is left alone.</summary>
        internal static int MaxVertices = 40000;

        private static bool loaded;

        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                var nodes = GameDatabase.Instance.GetConfigNodes("PARASOFT_SETTINGS");
                if (nodes == null || nodes.Length == 0)
                {
                    Log.Warn("No PARASOFT_SETTINGS node found; using built-in defaults.");
                    return;
                }
                var n = nodes[0];
                Read(n, "arealDensity", ref ArealDensity, 0.005f, 1f);
                Read(n, "lineDensity", ref LineDensity, 0.001f, 0.5f);
                Read(n, "porosity", ref Porosity, 0f, 0.6f);
                Read(n, "fillDistance", ref FillDistance, 0.3f, 20f);
                Read(n, "apparentMass", ref ApparentMass, 0f, 3f);
                Read(n, "stiffness", ref Stiffness, 0.1f, 10f);
                Read(n, "stability", ref Stability, 0f, 1f);
                Read(n, "ejectSpeed", ref EjectSpeed, 0f, 60f);
                Read(n, "substepsPerSecond", ref SubstepsPerSecond, 100f, 1000f);
                Read(n, "lodDistance", ref LodDistance, 50f, 100000f);
                Read(n, "freezeDistance", ref FreezeDistance, 100f, 100000f);
                Read(n, "cutCanopyRange", ref CutCanopyRange, 50f, 100000f);
                float maxc = MaxCanopies;
                Read(n, "maxCanopies", ref maxc, 1f, 500f);
                MaxCanopies = (int)maxc;
                float maxv = MaxVertices;
                Read(n, "maxVertices", ref maxv, 1000f, 1000000f);
                MaxVertices = (int)maxv;
                bool verbose;
                if (n.HasValue("verboseLogging") && bool.TryParse(n.GetValue("verboseLogging"), out verbose))
                    Log.Verbose = verbose;
            }
            catch (Exception e)
            {
                Log.Exception("reading Settings.cfg", e);
            }
        }

        private static void Read(ConfigNode n, string key, ref float value, float min, float max)
        {
            if (!n.HasValue(key)) return;
            float v;
            if (float.TryParse(n.GetValue(key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v))
                value = Mathf.Clamp(v, min, max);
            else
                Log.Warn("Settings.cfg: '{0}' is not a number", key);
        }

        internal static LatticeResolution Resolution
        {
            get
            {
                switch (ParaSoftParameters.Quality)
                {
                    case 0: return LatticeResolution.Low;
                    case 2: return LatticeResolution.High;
                    default: return LatticeResolution.Medium;
                }
            }
        }

        /// <summary>A fresh parameter set for one canopy, from these settings and its material.</summary>
        internal static SimParameters NewParameters(float arealDensityOverride)
        {
            return new SimParameters
            {
                ArealDensity = arealDensityOverride > 0f ? arealDensityOverride : ArealDensity,
                LineDensity = LineDensity,
                Porosity = Porosity,
                FillDistance = FillDistance,
                ApparentMass = ApparentMass,
                Stiffness = Stiffness,
                Stability = Stability,
                EjectSpeed = EjectSpeed,
                MaxSubstep = 1f / SubstepsPerSecond
            };
        }
    }

    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class SettingsLoader : MonoBehaviour
    {
        public void Start()
        {
            Settings.Load();
            Log.Info("ParaSoft Airbraking Technologies {0} loaded.",
                typeof(SettingsLoader).Assembly.GetName().Version);
        }
    }
}
