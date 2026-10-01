using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// The switches a player actually wants, in the stock difficulty settings screen, so
    /// there is no extra window to learn. Per-save: a screenshot save can go to high
    /// quality without touching a career that needs its frame rate.
    ///
    /// Numbers that only a modder would tune live in ParaSoft/Settings.cfg instead.
    /// </summary>
    public class ParaSoftParameters : GameParameters.CustomParameterNode
    {
        [GameParameters.CustomParameterUI("Softbody parachutes",
            toolTip = "Master switch. Off puts every parachute back to its own animation.")]
        public bool enabled = true;

        [GameParameters.CustomIntParameterUI("Quality", minValue = 0, maxValue = 2,
            toolTip = "0 = low, 1 = medium, 2 = high. How finely each canopy is simulated. " +
                      "Applies to canopies deployed after the change.")]
        public int quality = 1;

        [GameParameters.CustomParameterUI("Canopies collide with craft",
            toolTip = "Fabric drapes over and slides off parts, kerbals and other vessels.")]
        public bool partCollisions = true;

        [GameParameters.CustomParameterUI("Canopies land on terrain and water",
            toolTip = "Canopies collapse onto the ground and lie on the sea after landing.")]
        public bool surfaceCollisions = true;

        [GameParameters.CustomParameterUI("Ocean waves move canopies",
            toolTip = "With Scatterer's wave interactions on, canopies on the water ride its waves.")]
        public bool waves = true;

        [GameParameters.CustomParameterUI("Wind moves canopies",
            toolTip = "Uses Kerbal Weather Project's wind, or any wind registered with FAR.")]
        public bool wind = true;

        [GameParameters.CustomFloatParameterUI("Turbulence", minValue = 0f, maxValue = 2f, displayFormat = "F2",
            toolTip = "Small gusts that make canopies breathe and flutter. 1.00 is the tuned default.")]
        public float turbulence = 1f;

        [GameParameters.CustomParameterUI("Kerbal EVA parachutes",
            toolTip = "Simulate kerbals' personal parachutes too, where their canopy shape allows it.")]
        public bool evaChutes = true;

        [GameParameters.CustomIntParameterUI("Cut canopies linger (s)", minValue = 0, maxValue = 120,
            toolTip = "Longest a cut canopy keeps flying. It goes sooner once it is out of range " +
                      "(750 m from the camera and your craft) or has settled on the ground or sea. 0 removes it at once.")]
        public int detachedLifetime = 30;

        public override string Title { get { return "ParaSoft Airbraking Technologies"; } }
        public override string Section { get { return "ParaSoft"; } }
        public override string DisplaySection { get { return "ParaSoft"; } }
        public override int SectionOrder { get { return 1; } }
        public override GameParameters.GameMode GameMode { get { return GameParameters.GameMode.ANY; } }
        public override bool HasPresets { get { return false; } }

        public override bool Interactible(System.Reflection.MemberInfo member, GameParameters parameters)
        {
            if (member.Name == "enabled") return true;
            return enabled;
        }

        // -----------------------------------------------------------------------------
        // Static access, safe from anywhere including the main menu
        // -----------------------------------------------------------------------------

        private static ParaSoftParameters Current
        {
            get
            {
                if (HighLogic.CurrentGame == null) return null;
                var p = HighLogic.CurrentGame.Parameters;
                return p == null ? null : p.CustomParams<ParaSoftParameters>();
            }
        }

        /// <summary>Not called Enabled: the base class has an Enabled(MemberInfo, GameParameters).</summary>
        internal static bool SimEnabled { get { var c = Current; return c == null || c.enabled; } }
        internal static int Quality { get { var c = Current; return c == null ? 1 : Mathf.Clamp(c.quality, 0, 2); } }
        internal static bool PartCollisions { get { var c = Current; return c == null || c.partCollisions; } }
        internal static bool SurfaceCollisions { get { var c = Current; return c == null || c.surfaceCollisions; } }
        internal static bool Waves { get { var c = Current; return c == null || c.waves; } }
        internal static bool Wind { get { var c = Current; return c == null || c.wind; } }
        internal static float Turbulence { get { var c = Current; return c == null ? 1f : Mathf.Clamp(c.turbulence, 0f, 2f); } }
        internal static bool EvaChutes { get { var c = Current; return c == null || c.evaChutes; } }
        internal static float DetachedLifetime { get { var c = Current; return c == null ? 30f : Mathf.Clamp(c.detachedLifetime, 0, 120); } }
    }
}
