using System;
using ParaSoft.Core;
using ParaSoft.Environment;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// A canopy that has been cut loose, or whose part was destroyed, flying on by itself
    /// until it is out of range, has lain still on the ground or the sea for a while, or its
    /// time is up - whichever comes first.
    ///
    /// With no anchor, the simulation frame has to be carried along by hand. Its origin is
    /// kept relative to the planet's centre, which floating-origin and Krakensbane shifts
    /// move in step with everything else, and its velocity follows the canopy's own, so the
    /// fabric stays near the origin wherever it drifts.
    /// </summary>
    internal sealed class DetachedCanopy : IDisposable
    {
        /// <summary>A canopy lying still this long on the ground or the water is gone.</summary>
        private const float SettledLifetime = 15f;

        private readonly CanopySim[] sims;
        private readonly CanopyRenderer renderer;
        private readonly CelestialBody body;
        private readonly Surroundings surroundings = new Surroundings();
        private readonly string name;
        private Vector3d origin;          // relative to body.position
        private Vector3 frameVelocity;    // Unity + Krakensbane
        private float age;
        private float settledFor;
        private readonly float lifetime;
        private readonly bool sharedConfluence;
        private bool disposed;

        internal DetachedCanopy(CanopySim[] sims, CanopyRenderer renderer, CelestialBody body, Vector3 anchorWorld,
                                Vector3 frameVelocity, float lifetime, string name, bool sharedConfluence)
        {
            this.sims = sims;
            this.sharedConfluence = sharedConfluence;
            this.renderer = renderer;
            this.body = body;
            this.name = name;
            this.frameVelocity = frameVelocity;
            this.lifetime = lifetime;
            origin = (Vector3d)anchorWorld - body.position;
            foreach (var s in sims) s.Detach();
            renderer.Show(true, false);
        }

        internal bool Finished { get { return disposed || age >= lifetime || body == null; } }
        internal CanopySim[] Sims { get { return sims; } }
        internal Vector3 Anchor { get { return (Vector3)(body.position + origin); } }
        internal Vector3 FrameVelocity { get { return frameVelocity; } }

        internal void FixedStep(float dt, Vector3 camera, ParaSoftSystem system)
        {
            if (Finished) return;
            age += dt;
            var anchor = Anchor;
            if (OutOfRange(anchor, camera))
            {
                age = lifetime;
                return;
            }

            var altitude = ((Vector3d)anchor - body.position).magnitude - body.Radius;
            var pressure = body.GetPressure(altitude);
            var temperature = body.GetTemperature(altitude);
            var density = pressure > 0 ? body.GetDensity(pressure, temperature) : 0.0;
            var air = FlightGlobals.RefFrameIsRotating ? Vector3.zero : (Vector3)body.getRFrmVel(anchor);
            air += Wind.At(body, null, anchor);
            var env = new SimEnvironment
            {
                FrameVelocity = frameVelocity.ToVec3(),
                AirVelocity = air.ToVec3(),
                Gravity = ((Vector3)FlightGlobals.getGeeForceAtPosition(anchor)).ToVec3(),
                Density = (float)Math.Max(0.0, density),
                SpeedOfSound = density > 0 ? (float)body.GetSpeedOfSound(pressure, density) : 0f,
                Turbulence = ParaSoftParameters.Turbulence * 0.5f
            };

            var centre = Vector3.zero;
            var radius = 0f;
            foreach (var s in sims)
            {
                centre += s.BubbleCentre.ToVector3();
                radius = Mathf.Max(radius, s.Extent);
            }
            centre = anchor + centre / sims.Length;
            surroundings.Update(null, null, body, centre, radius * 0.6f + 2f, anchor, frameVelocity, dt, true);
            var o = anchor.ToVec3();
            var ov = frameVelocity.ToVec3();
            foreach (var s in sims)
            {
                system.Contacts.Fill(s, o, ov);
                s.Step(dt, env, surroundings.World);
            }
            if (sharedConfluence) CanopySim.TieConfluences(sims);

            // Carry the frame along with the canopy: move the origin at the frame's velocity,
            // then match the frame's velocity to the canopy's and re-centre if it has drifted.
            // Relative to the planet a point moves at its Unity velocity plus the Krakensbane
            // frame velocity - exactly the "absolute" velocity the frame is kept in.
            origin += (Vector3d)frameVelocity * dt;
            var mean = Vector3.zero;
            var drift = Vector3.zero;
            foreach (var s in sims)
            {
                mean += s.MeanVelocity.ToVector3();
                drift += s.BubbleCentre.ToVector3();
            }
            mean /= sims.Length;
            drift /= sims.Length;
            frameVelocity += mean;
            if (drift.magnitude > 20f)
            {
                origin += (Vector3d)drift;
                foreach (var s in sims) s.ShiftOrigin(drift.ToVec3());
            }

            // Lying still on the ground or the sea: nothing more to see.
            var still = surroundings.World.Ground != null || surroundings.World.Water != null;
            foreach (var s in sims)
                if (s.Fill > 0.05f) still = false;
            // Relative to the surface: in KSP's rotating frame that is the Unity velocity.
            var surfaceSpeed = FlightGlobals.RefFrameIsRotating
                ? frameVelocity.magnitude
                : (frameVelocity - (Vector3)body.getRFrmVel(anchor)).magnitude;
            if (still && surfaceSpeed < 1f) settledFor += dt;
            else settledFor = 0f;
            if (settledFor > SettledLifetime) age = lifetime;
        }

        /// <summary>
        /// Far enough from both the camera and the craft being flown that nobody is looking
        /// at it any more.
        /// </summary>
        private static bool OutOfRange(Vector3 anchor, Vector3 camera)
        {
            var range = Settings.CutCanopyRange;
            if ((anchor - camera).sqrMagnitude < range * range) return false;
            var active = FlightGlobals.ActiveVessel;
            if (active != null && ((Vector3)active.CoMD - anchor).sqrMagnitude < range * range) return false;
            return true;
        }

        internal void Render()
        {
            if (Finished) return;
            try
            {
                renderer.Update(sims, Anchor);
            }
            catch (Exception e)
            {
                Log.Exception("drawing the cut canopy from " + name, e);
                age = lifetime;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            renderer.Dispose();
            surroundings.Dispose();
        }
    }
}
