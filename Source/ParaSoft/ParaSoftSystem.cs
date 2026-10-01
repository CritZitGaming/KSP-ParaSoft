using System;
using System.Collections.Generic;
using ParaSoft.Core;
using ParaSoft.Environment;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// Runs every canopy in the flight scene from one place, so the budget (how many
    /// canopies at once, how often distant ones step), canopy-to-canopy contact and each
    /// craft's airflow can be handled together instead of per part.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ParaSoftSystem : MonoBehaviour
    {
        internal static ParaSoftSystem Instance { get; private set; }

        private readonly List<CanopyController> controllers = new List<CanopyController>();
        private readonly List<DetachedCanopy> detached = new List<DetachedCanopy>();
        private readonly Dictionary<Vessel, CraftFlow> flows = new Dictionary<Vessel, CraftFlow>();
        private readonly List<Vessel> staleFlows = new List<Vessel>();
        private int physicsFrame;

        /// <summary>Every open canopy's contact volume this frame, in world space.</summary>
        internal readonly CanopyContacts Contacts = new CanopyContacts();

        public void Awake()
        {
            Instance = this;
            Settings.Load();
        }

        public void OnDestroy()
        {
            foreach (var c in controllers) c.Dispose();
            foreach (var d in detached) d.Dispose();
            controllers.Clear();
            detached.Clear();
            flows.Clear();
            if (Instance == this) Instance = null;
        }

        /// <summary>The craft's airflow tracker, brought up to date for this physics frame.</summary>
        internal CraftFlow FlowFor(Vessel vessel)
        {
            if (vessel == null) return null;
            CraftFlow flow;
            if (!flows.TryGetValue(vessel, out flow))
            {
                flow = new CraftFlow(vessel);
                flows[vessel] = flow;
            }
            if (flow.LastFrame != physicsFrame)
            {
                flow.LastFrame = physicsFrame;
                flow.Update(TimeWarp.fixedDeltaTime);
            }
            return flow;
        }

        internal void Register(CanopyController c)
        {
            if (!controllers.Contains(c)) controllers.Add(c);
        }

        internal void Unregister(CanopyController c)
        {
            controllers.Remove(c);
        }

        internal void AdoptDetached(DetachedCanopy d)
        {
            detached.Add(d);
        }

        /// <summary>Whether another canopy (or cluster of them) may start simulating.</summary>
        internal bool CanActivate(int canopies)
        {
            var active = 0;
            foreach (var c in controllers) if (c.Active) active += c.Sims.Length;
            foreach (var d in detached) active += d.Sims.Length;
            return active + canopies <= Settings.MaxCanopies;
        }

        private static Vector3 CameraPosition
        {
            get
            {
                var cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                return cam != null ? cam.transform.position : Vector3.zero;
            }
        }

        public void FixedUpdate()
        {
            if (!FlightGlobals.ready) return;
            var dt = TimeWarp.fixedDeltaTime;
            if (dt <= 0f) return;
            var camera = CameraPosition;
            physicsFrame++;
            if ((physicsFrame & 255) == 0) PruneFlows();
            GatherContacts();

            for (var i = controllers.Count - 1; i >= 0; i--)
            {
                var c = controllers[i];
                if (c.Disposed) { controllers.RemoveAt(i); continue; }
                try { c.FixedStep(dt, camera, this); }
                catch (Exception e)
                {
                    Log.Exception("stepping a canopy", e);
                    c.Dispose();
                    controllers.RemoveAt(i);
                }
            }

            for (var i = detached.Count - 1; i >= 0; i--)
            {
                var d = detached[i];
                try { d.FixedStep(dt, camera, this); }
                catch (Exception e)
                {
                    Log.Exception("stepping a cut canopy", e);
                    d.Dispose();
                    detached.RemoveAt(i);
                    continue;
                }
                if (d.Finished)
                {
                    d.Dispose();
                    detached.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Where every open canopy is, before any of them steps, so each can be kept out of
        /// the others - its cluster-mates, the other chutes on the craft, other craft's, and
        /// cut canopies drifting past.
        /// </summary>
        private void GatherContacts()
        {
            Contacts.Clear();
            foreach (var c in controllers)
            {
                if (c.Disposed || !c.Active) continue;
                var origin = c.Anchor.ToVec3();
                var velocity = c.FrameVelocity.ToVec3();
                foreach (var s in c.Sims) Contacts.Add(s, origin, velocity);
            }
            foreach (var d in detached)
            {
                if (d.Finished) continue;
                var origin = d.Anchor.ToVec3();
                var velocity = d.FrameVelocity.ToVec3();
                foreach (var s in d.Sims) Contacts.Add(s, origin, velocity);
            }
        }

        private void PruneFlows()
        {
            staleFlows.Clear();
            foreach (var kv in flows)
                if (kv.Key == null || !kv.Key.loaded || kv.Value.LastFrame < physicsFrame - 100) staleFlows.Add(kv.Key);
            foreach (var v in staleFlows) flows.Remove(v);
        }

        public void LateUpdate()
        {
            if (!FlightGlobals.ready) return;
            foreach (var c in controllers) c.Render();
            foreach (var d in detached) d.Render();
        }
    }
}
