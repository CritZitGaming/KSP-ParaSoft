using System;
using System.Collections.Generic;
using ParaSoft.Core;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// Runs every canopy in the flight scene from one place, so the budget (how many
    /// canopies at once, how often distant ones step) and canopy-to-canopy contact can be
    /// handled together instead of per part.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ParaSoftSystem : MonoBehaviour
    {
        internal static ParaSoftSystem Instance { get; private set; }

        private readonly List<CanopyController> controllers = new List<CanopyController>();
        private readonly List<DetachedCanopy> detached = new List<DetachedCanopy>();

        // Scratch lists for canopy contact, reused every frame.
        private readonly List<CanopySim> contactSims = new List<CanopySim>();
        private readonly List<Vector3> contactAnchors = new List<Vector3>();

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
            if (Instance == this) Instance = null;
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
                try { d.FixedStep(dt, camera); }
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

            CanopyContact();
        }

        /// <summary>
        /// Keeps canopies out of each other. Each inflated canopy is a rough sphere to the
        /// others; fabric that strays inside one is pushed back out, which is how the
        /// canopies of a cluster spread apart.
        /// </summary>
        private void CanopyContact()
        {
            contactSims.Clear();
            contactAnchors.Clear();
            foreach (var c in controllers)
            {
                if (!c.Active) continue;
                var a = c.Anchor;
                foreach (var s in c.Sims)
                {
                    if (s.Phase != CanopyPhase.Flying) continue;
                    contactSims.Add(s);
                    contactAnchors.Add(a);
                }
            }
            foreach (var d in detached)
            {
                var a = d.Anchor;
                foreach (var s in d.Sims)
                {
                    contactSims.Add(s);
                    contactAnchors.Add(a);
                }
            }

            for (var i = 0; i < contactSims.Count; i++)
            {
                var si = contactSims[i];
                var ci = contactAnchors[i] + si.BubbleCentre.ToVector3();
                var ri = si.BubbleRadius;
                for (var j = i + 1; j < contactSims.Count; j++)
                {
                    var sj = contactSims[j];
                    var cj = contactAnchors[j] + sj.BubbleCentre.ToVector3();
                    var rj = sj.BubbleRadius;
                    if ((ci - cj).sqrMagnitude >= (ri + rj) * (ri + rj)) continue;
                    si.PushOutOfSphere((cj - contactAnchors[i]).ToVec3(), rj, 0.35f);
                    sj.PushOutOfSphere((ci - contactAnchors[j]).ToVec3(), ri, 0.35f);
                }
            }
        }

        public void LateUpdate()
        {
            if (!FlightGlobals.ready) return;
            foreach (var c in controllers) c.Render();
            foreach (var d in detached) d.Render();
        }
    }
}
