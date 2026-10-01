using System;
using ParaSoft.Core;
using UnityEngine;

namespace ParaSoft.Environment
{
    /// <summary>
    /// How much of the wind is actually moving one craft.
    ///
    /// ParaSoft never pushes on the craft, so a canopy has to agree with whatever the
    /// parachute module's own drag is doing, and wind is where the two can disagree.
    /// Kerbal Weather Project in stock aerodynamics adds only a fraction of the wind's force
    /// to a light part like a parachute; RealChute works its drag out without any wind; FAR
    /// applies all of it. A canopy that felt the whole wind regardless would stream off
    /// sideways from a craft that is falling straight down.
    ///
    /// So this watches the craft instead. Its acceleration, less gravity and engine thrust,
    /// is what the air is doing to it; the air must be blowing past it the opposite way;
    /// and the fraction of the wind that makes that so is the fraction its canopies are
    /// given (see WindResponse). With FAR that comes out at the whole wind, with RealChute
    /// at none, and in between for stock - whatever keeps the lines pointing the way the
    /// craft is actually being pulled.
    /// </summary>
    internal sealed class CraftFlow
    {
        internal readonly Vessel Vessel;
        internal int LastFrame = -1;

        private Vector3d lastVelocity;
        private bool haveVelocity;
        private Vector3d aero;               // filtered aerodynamic acceleration, m/s^2
        private float windFactor = 1f;

        internal CraftFlow(Vessel vessel)
        {
            Vessel = vessel;
        }

        /// <summary>Fraction of the wind the craft's canopies should feel, 0..1.</summary>
        internal float WindFactor { get { return windFactor; } }

        internal void Update(float dt)
        {
            var v = Vessel;
            // No air, no wind to share out.
            if (v == null || !v.loaded || v.packed || dt <= 0f || v.atmDensity <= 0.0)
            {
                haveVelocity = false;
                return;
            }

            // Centre-of-mass velocity and engine thrust.
            var momentum = Vector3d.zero;
            var moment = Vector3d.zero;
            var thrust = Vector3d.zero;
            var mass = 0.0;
            for (var i = 0; i < v.parts.Count; i++)
            {
                var p = v.parts[i];
                var rb = p.Rigidbody;
                if (rb != null)
                {
                    momentum += (Vector3d)rb.velocity * rb.mass;
                    moment += (Vector3d)rb.worldCenterOfMass * rb.mass;
                    mass += rb.mass;
                }
                for (var j = 0; j < p.Modules.Count; j++)
                {
                    var e = p.Modules[j] as ModuleEngines;
                    if (e == null || !e.EngineIgnited || e.finalThrust <= 0f || e.thrustTransforms == null) continue;
                    var tts = e.thrustTransforms;
                    var mults = e.thrustTransformMultipliers;
                    for (var k = 0; k < tts.Count; k++)
                    {
                        if (tts[k] == null) continue;
                        var share = mults != null && k < mults.Count ? mults[k] : 1f / tts.Count;
                        thrust -= (Vector3d)tts[k].forward * (e.finalThrust * share);
                    }
                }
            }
            if (mass <= 0.0)
            {
                haveVelocity = false;
                return;
            }
            var velocity = momentum / mass + Krakensbane.GetFrameVelocity();
            var com = moment / mass;
            if (!haveVelocity)
            {
                lastVelocity = velocity;
                haveVelocity = true;
                return;
            }

            var body = v.mainBody;
            var gravity = FlightGlobals.getGeeForceAtPosition(com);
            var raw = (velocity - lastVelocity) / dt - gravity - thrust / mass;
            if (FlightGlobals.RefFrameIsRotating)
                raw -= FlightGlobals.getCentrifugalAcc(com, body) + FlightGlobals.getCoriolisAcc(velocity, body);
            lastVelocity = velocity;
            // Joints ring and parts knock; a third of a second smooths that out while still
            // following a canopy opening.
            aero += (raw - aero) * (1.0 - Math.Exp(-dt / 0.3));

            // Only trust it while the air is clearly doing the pushing: not on the ground,
            // not in near free fall.
            var g = Math.Max(gravity.magnitude, 0.5);
            var confidence = v.LandedOrSplashed ? 0f : Mathf.Clamp01((float)((aero.magnitude / g - 0.15) / 0.25));
            if (confidence <= 0f)
            {
                // Landed, or nothing to go on: the canopy feels the real wind.
                windFactor += (1f - windFactor) * (1f - Mathf.Exp(-dt / 2f));
                return;
            }
            var wind = Wind.At(body, v.rootPart, (Vector3)com);
            if (wind.sqrMagnitude < 0.25f) return;
            var still = FlightGlobals.RefFrameIsRotating ? velocity : velocity - body.getRFrmVel(com);
            var target = WindResponse.Factor(still.ToVec3(), wind.ToVec3(), aero.ToVec3());
            if (target < 0f) return;
            windFactor += (target - windFactor) * (1f - Mathf.Exp(-dt * confidence / 0.5f));
        }
    }
}
