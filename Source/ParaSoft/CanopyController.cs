using System;
using System.Collections.Generic;
using ParaSoft.Core;
using ParaSoft.Environment;
using ParaSoft.Hosts;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// Drives the softbody for one canopy slot of one parachute module.
    ///
    /// The parachute module stays in charge of everything that matters to the flight:
    /// when to deploy, how much drag, when to cut, heat, repacking, spares. This follows
    /// its state - stowed, semi-deployed, deployed, cut - and makes the canopy look and move
    /// the way it would. It never applies a force to the craft.
    /// </summary>
    internal sealed class CanopyController : IDisposable
    {
        internal readonly Part Part;
        internal readonly ChuteHost Host;
        private readonly ChuteSlot slot;

        private CanopyModel model;
        private List<Renderer> originals;
        private bool captureTried;
        private bool unsupported;

        private CanopySim[] sims;
        private CanopyRenderer renderer;
        private Surroundings surroundings;
        private HostState lastState = HostState.Stowed;
        private bool seenStowed;
        private bool frozen;
        private int lodCounter;
        private float deployedFor;
        private bool disposed;
        private Vector3 frameVelocity;     // the anchor's, Unity + Krakensbane, lightly smoothed
        private bool haveFrameVelocity;

        internal CanopyController(Part part, ChuteHost host, ChuteSlot slot)
        {
            Part = part;
            Host = host;
            this.slot = slot;
        }

        internal bool Active { get { return sims != null; } }
        internal CanopySim[] Sims { get { return sims; } }
        internal bool Disposed { get { return disposed; } }
        /// <summary>Velocity of the canopies' frame (the anchor), Unity + Krakensbane.</summary>
        internal Vector3 FrameVelocity { get { return frameVelocity; } }

        internal Vector3 Anchor
        {
            get
            {
                var c = SafeCanopy;
                return c != null ? c.position : (Part != null ? Part.transform.position : Vector3.zero);
            }
        }

        private Transform SafeCanopy
        {
            get
            {
                try { return slot.Canopy; }
                catch { return null; }
            }
        }

        /// <summary>A line for the part's right-click menu.</summary>
        internal string Status
        {
            get
            {
                if (unsupported) return "not a round canopy";
                if (!ParaSoftParameters.SimEnabled) return "off";
                if (sims == null) return lastState == HostState.Cut ? "cut" : "packed";
                if (frozen) return "paused (far away)";
                var s = sims[0];
                if (s.Phase == CanopyPhase.Extracting) return "deploying";
                if (s.DynamicPressure < 0.05f && s.Fill < 0.05f) return Part.vessel != null && Part.vessel.LandedOrSplashed ? "collapsed" : "limp (no airflow)";
                if (s.Reef < 0.97f) return "reefed, " + (s.Fill * 100f).ToString("0") + "% full";
                return (s.Fill * 100f).ToString("0") + "% inflated";
            }
        }

        // -------------------------------------------------------------------------------
        // Physics
        // -------------------------------------------------------------------------------

        internal void FixedStep(float dt, Vector3 camera, ParaSoftSystem system)
        {
            if (disposed) return;
            if (Part == null || Part.vessel == null) return;

            HostState state;
            try
            {
                Host.Refresh();
                state = slot.State;
            }
            catch (Exception e)
            {
                Log.Exception("reading " + Host.Name + " on " + Part.partInfo.title + "; softbody disabled for it", e);
                unsupported = true;
                Deactivate();
                return;
            }

            if (!ParaSoftParameters.SimEnabled || unsupported || (Host.IsEva && !ParaSoftParameters.EvaChutes))
            {
                Deactivate();
                lastState = state;
                return;
            }

            if (!captureTried) TryCapture();

            var deployed = state == HostState.Semi || state == HostState.Full;
            if (deployed && sims == null && model != null && system.CanActivate(model.Lattices.Length))
                Activate(seenStowed && lastState == HostState.Stowed);
            else if (state == HostState.Cut && sims != null)
                CutAway(system);
            else if (state == HostState.Stowed && sims != null)
                Deactivate();
            if (state == HostState.Stowed) seenStowed = true;
            lastState = state;

            // Keep up with how the craft moves even while packed, so a canopy that opens
            // already knows how much of the wind the craft is feeling.
            var flow = system.FlowFor(Part.vessel);

            if (sims == null) return;

            var vessel = Part.vessel;
            if (vessel.packed)
            {
                Freeze();
                return;
            }

            var anchor = Anchor;
            var distance = Vector3.Distance(camera, anchor);
            if (distance > Settings.FreezeDistance)
            {
                Freeze();
                return;
            }
            if (frozen)
            {
                frozen = false;
                haveFrameVelocity = false;
                foreach (var s in sims) s.ResetFrame();
            }
            // Beyond the LOD distance, step every other frame with twice the time step.
            var stepDt = dt;
            if (distance > Settings.LodDistance)
            {
                if ((lodCounter++ & 1) == 0) return;
                stepDt = dt * 2f;
            }

            deployedFor += stepDt;
            var open = 0f;
            try { open = slot.OpenFraction; }
            catch { open = 1f; }
            // The hem may open as far as the module's own drag says it has.
            var reef = Mathf.Sqrt(Mathf.Clamp01(open));
            if (state == HostState.Semi) reef = Mathf.Max(reef, 0.05f);

            TrackFrameVelocity(anchor, vessel, stepDt);
            var env = BuildEnvironment(anchor, vessel, flow);
            UpdateSurroundings(anchor, env, stepDt, vessel);
            var origin = anchor.ToVec3();
            var originVelocity = frameVelocity.ToVec3();
            foreach (var s in sims)
            {
                s.SetReefTarget(reef);
                system.Contacts.Fill(s, origin, originVelocity);
                s.Step(stepDt, env, surroundings.World);
            }
            if (model.Fit.SharedConfluence) CanopySim.TieConfluences(sims);
        }

        /// <summary>
        /// The anchor's velocity. A parachute is a light part on a joint and rings with the
        /// craft's vibration; the lines would pass that on to the canopy as a buzz no real
        /// cord would carry, so it is smoothed over a few frames.
        /// </summary>
        private void TrackFrameVelocity(Vector3 anchor, Vessel vessel, float dt)
        {
            // Physicsless parts ride on their parent's rigidbody.
            Rigidbody rb = null;
            for (var p = Part; p != null && rb == null; p = p.parent) rb = p.Rigidbody;
            var raw = (rb != null ? rb.GetPointVelocity(anchor) : vessel.rb_velocity) + Krakensbane.GetFrameVelocityV3f();
            if (!haveFrameVelocity || dt <= 0f)
            {
                frameVelocity = raw;
                haveFrameVelocity = true;
                return;
            }
            frameVelocity += (raw - frameVelocity) * (1f - Mathf.Exp(-dt / 0.05f));
        }

        private SimEnvironment BuildEnvironment(Vector3 anchor, Vessel vessel, CraftFlow flow)
        {
            if (!haveFrameVelocity) TrackFrameVelocity(anchor, vessel, 0f);
            var body = vessel.mainBody;
            var air = FlightGlobals.RefFrameIsRotating ? Vector3.zero : (Vector3)body.getRFrmVel(anchor);
            // Only as much wind as is actually moving the craft; see CraftFlow.
            var wind = Wind.At(body, Part, anchor) * (flow != null ? flow.WindFactor : 1f);
            air += wind;
            var density = (float)(Part.atmDensity > 0.0 ? Part.atmDensity : vessel.atmDensity);
            return new SimEnvironment
            {
                FrameVelocity = frameVelocity.ToVec3(),
                AirVelocity = air.ToVec3(),
                Gravity = ((Vector3)FlightGlobals.getGeeForceAtPosition(anchor)).ToVec3(),
                Density = Mathf.Max(0f, density),
                SpeedOfSound = (float)vessel.speedOfSound,
                Turbulence = ParaSoftParameters.Turbulence * (0.35f + Mathf.Min(0.65f, wind.magnitude / 20f))
            };
        }

        private void UpdateSurroundings(Vector3 anchor, SimEnvironment env, float dt, Vessel vessel)
        {
            if (surroundings == null) surroundings = new Surroundings();
            var centre = Vector3.zero;
            var radius = 0f;
            foreach (var s in sims)
            {
                centre += s.BubbleCentre.ToVector3();
                radius = Mathf.Max(radius, s.Extent);
            }
            centre = anchor + centre / sims.Length;
            // Fabric falling onto its own part is only interesting once the craft is down;
            // in flight the lines start inside the part's collider.
            var includeOwn = vessel.LandedOrSplashed && deployedFor > 1f;
            surroundings.Update(vessel, Part, vessel.mainBody, centre, radius * 0.6f + 2f, anchor,
                env.FrameVelocity.ToVector3(), dt, includeOwn);
        }

        private void Freeze()
        {
            frozen = true;
        }

        // -------------------------------------------------------------------------------
        // State changes
        // -------------------------------------------------------------------------------

        private void TryCapture()
        {
            captureTried = true;
            try
            {
                model = CanopyCapture.Capture(Part, slot, out originals);
                if (model == null) unsupported = true;
            }
            catch (Exception e)
            {
                Log.Exception("capturing the canopy of " + Part.partInfo.title, e);
                unsupported = true;
            }
        }

        private void Activate(bool fromPack)
        {
            try
            {
                var areal = 0f;
                try { areal = slot.ArealDensity; }
                catch { areal = 0f; }
                sims = new CanopySim[model.Lattices.Length];
                var vessel = Part.vessel;
                haveFrameVelocity = false;
                var env = BuildEnvironment(Anchor, vessel, ParaSoftSystem.Instance != null ? ParaSoftSystem.Instance.FlowFor(vessel) : null);
                var rel = env.FrameVelocity.ToVector3() - env.AirVelocity.ToVector3();
                var canopy = SafeCanopy;
                var up = (Anchor - (Vector3)vessel.mainBody.position).normalized;
                var crown = rel.magnitude > 1f ? -rel.normalized : up;

                // The canopy frame's mean axis, so a cluster keeps its spread when it
                // starts out open.
                var mean = Vector3.zero;
                foreach (var l in model.Lattices) mean += canopy.rotation * l.Shape.Axis.ToVector3();
                mean = mean.normalized;
                var toFlow = Quaternion.FromToRotation(mean, crown);
                // A cluster's canopies leave in their own bags, fanned out the way the model
                // spreads them, so they do not open inside one another.
                var eject = fromPack ? EjectDirection(canopy) : crown;
                var toEject = Quaternion.FromToRotation(mean, eject);

                for (var c = 0; c < sims.Length; c++)
                {
                    var s = new CanopySim(model.Lattices[c], Settings.NewParameters(areal), Part.GetInstanceID() * 31 + c);
                    var own = canopy.rotation * model.Lattices[c].Shape.Axis.ToVector3();
                    if (fromPack) s.BeginDeploy((sims.Length > 1 ? toEject * own : eject).ToVec3());
                    else
                    {
                        s.SetReefTarget(Mathf.Sqrt(Mathf.Clamp01(slot.OpenFraction)));
                        s.InitialiseOpen((toFlow * own).ToVec3());
                    }
                    sims[c] = s;
                }
                if (renderer == null) renderer = new CanopyRenderer(model, originals, Part.partInfo.title);
                deployedFor = 0f;
                frozen = false;
                Log.Debug_("{0}: softbody {1} ({2} canopy(s)).", Part.partInfo.title, fromPack ? "deploying" : "open", sims.Length);
            }
            catch (Exception e)
            {
                Log.Exception("starting the softbody for " + Part.partInfo.title, e);
                unsupported = true;
                Deactivate();
            }
        }

        /// <summary>
        /// Which way the pack leaves the part: out through the cap if the model has one,
        /// otherwise along the part's own up axis.
        /// </summary>
        private Vector3 EjectDirection(Transform canopy)
        {
            Transform cap = null;
            try { cap = slot.Cap; }
            catch { cap = null; }
            var partCentre = Part.transform.position;
            if (cap != null)
            {
                var r = cap.GetComponentInChildren<Renderer>();
                var capPos = r != null ? r.bounds.center : cap.position;
                var d = capPos - partCentre;
                if (d.magnitude > 0.02f) return d.normalized;
            }
            var toCanopy = canopy.position - partCentre;
            if (toCanopy.magnitude > 0.05f) return toCanopy.normalized;
            return Part.transform.up;
        }

        /// <summary>The module cut the lines: the canopy flies on as debris for a while.</summary>
        private void CutAway(ParaSoftSystem system)
        {
            var life = ParaSoftParameters.DetachedLifetime;
            if (life > 0f && sims != null && renderer != null && Part.vessel != null && !Part.vessel.packed)
            {
                if (!haveFrameVelocity) TrackFrameVelocity(Anchor, Part.vessel, 0f);
                system.AdoptDetached(new DetachedCanopy(sims, renderer, Part.vessel.mainBody, Anchor,
                    frameVelocity, life, Part.partInfo.title, model.Fit.SharedConfluence));
                renderer = null;   // now owned by the debris
                sims = null;
                surroundings = DisposeSurroundings();
                return;
            }
            Deactivate();
        }

        /// <summary>The part is going away (destroyed, or its vessel unloading).</summary>
        internal void HostLost(ParaSoftSystem system, bool destroyed)
        {
            if (destroyed && sims != null && renderer != null && ParaSoftParameters.DetachedLifetime > 0f &&
                Part != null && Part.vessel != null && !Part.vessel.packed)
            {
                CutAway(system);
            }
            Dispose();
        }

        private void Deactivate()
        {
            sims = null;
            if (renderer != null) renderer.Show(false, false);
            surroundings = DisposeSurroundings();
            frozen = false;
        }

        private Surroundings DisposeSurroundings()
        {
            if (surroundings != null) surroundings.Dispose();
            return null;
        }

        // -------------------------------------------------------------------------------
        // Rendering
        // -------------------------------------------------------------------------------

        internal void Render()
        {
            if (disposed || renderer == null) return;
            if (sims == null)
            {
                renderer.Show(false, false);
                return;
            }
            var canopy = SafeCanopy;
            if (canopy == null) return;
            try
            {
                renderer.Update(sims, canopy.position);
                renderer.Show(true, true);
            }
            catch (Exception e)
            {
                Log.Exception("drawing the canopy of " + Part.partInfo.title, e);
                unsupported = true;
                Deactivate();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            sims = null;
            if (renderer != null) renderer.Dispose();
            renderer = null;
            surroundings = DisposeSurroundings();
        }
    }
}
