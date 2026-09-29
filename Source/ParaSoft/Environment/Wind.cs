using System;
using System.Reflection;
using UnityEngine;

namespace ParaSoft.Environment
{
    /// <summary>
    /// Wind, from whichever weather mod is installed. Nothing here is a hard dependency:
    /// each source is found by name at startup and silently skipped if absent.
    ///
    /// Kerbal Weather Project publishes one world-space wind vector per frame for the
    /// active vessel's location (KerbalWxClimo or KerbalWxPoint.windVectorWS, depending on
    /// the mode the player picked) and subtracts it from part velocities in its own
    /// aerodynamics, so the canopy sees exactly the wind KWP's craft do. Failing that, FAR
    /// exposes a wind function any mod can register with; KWP itself registers there when
    /// FAR is present, and so do other wind mods.
    /// </summary>
    internal static class Wind
    {
        private static bool initialised;

        // Kerbal Weather Project
        private static FieldInfo kwpUseClimo, kwpUsePoint, kwpClimoWind, kwpPointWind, kwpEnabled;

        // FAR
        private static Func<CelestialBody, Part, Vector3, Vector3> farWind;

        internal static string Source { get; private set; }

        private static void Init()
        {
            initialised = true;
            Source = "none";
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var asm = la.assembly;
                    var util = asm.GetType("KerbalWeatherProject.Util");
                    if (util != null)
                    {
                        const BindingFlags sf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                        kwpUseClimo = util.GetField("use_climo", sf);
                        kwpUsePoint = util.GetField("use_point", sf);
                        kwpEnabled = util.GetField("wx_enabled", sf);
                        var climo = asm.GetType("KerbalWeatherProject.KerbalWxClimo");
                        var point = asm.GetType("KerbalWeatherProject.KerbalWxPoint");
                        if (climo != null) kwpClimoWind = climo.GetField("windVectorWS", sf);
                        if (point != null) kwpPointWind = point.GetField("windVectorWS", sf);
                        if (kwpClimoWind != null || kwpPointWind != null) Source = "Kerbal Weather Project";
                    }

                    foreach (var name in new[] { "FerramAerospaceResearch.FARWind", "FerramAerospaceResearch.FARAtmosphere" })
                    {
                        var t = asm.GetType(name);
                        if (t == null || farWind != null) continue;
                        var m = t.GetMethod("GetWind", BindingFlags.Public | BindingFlags.Static, null,
                            new[] { typeof(CelestialBody), typeof(Part), typeof(Vector3) }, null);
                        if (m == null || m.ReturnType != typeof(Vector3)) continue;
                        farWind = (Func<CelestialBody, Part, Vector3, Vector3>)Delegate.CreateDelegate(
                            typeof(Func<CelestialBody, Part, Vector3, Vector3>), m);
                    }
                }
                if (Source == "none" && farWind != null) Source = "FAR";
                Log.Info("Wind source: {0}", Source);
            }
            catch (Exception e)
            {
                Log.Exception("looking for a wind mod", e);
            }
        }

        /// <summary>Wind at a position, in the world frame (Unity axes), m/s.</summary>
        internal static Vector3 At(CelestialBody body, Part part, Vector3 position)
        {
            if (!initialised) Init();
            if (!ParaSoftParameters.Wind) return Vector3.zero;
            try
            {
                if (kwpClimoWind != null || kwpPointWind != null)
                {
                    if (kwpEnabled != null && !(bool)kwpEnabled.GetValue(null)) return Vector3.zero;
                    if (kwpUseClimo != null && (bool)kwpUseClimo.GetValue(null) && kwpClimoWind != null)
                        return (Vector3)kwpClimoWind.GetValue(null);
                    if (kwpUsePoint != null && (bool)kwpUsePoint.GetValue(null) && kwpPointWind != null)
                        return (Vector3)kwpPointWind.GetValue(null);
                    return Vector3.zero;
                }
                if (farWind != null) return farWind(body, part, position);
            }
            catch (Exception e)
            {
                Log.Exception("reading wind from " + Source + "; wind disabled", e);
                kwpClimoWind = kwpPointWind = null;
                farWind = null;
                Source = "none";
            }
            return Vector3.zero;
        }
    }
}
