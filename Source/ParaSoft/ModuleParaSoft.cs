using System.Collections;
using System.Collections.Generic;
using System.Text;
using ParaSoft.Hosts;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>
    /// Added by ModuleManager to every part with a parachute module ParaSoft understands:
    /// stock ModuleParachute and ModuleEvaChute, RealChute's RealChuteModule, and FAR's
    /// RealChuteFAR. It only finds the parachute module and hands its canopies to the
    /// flight scene's ParaSoftSystem; it has no state worth saving, because the parachute
    /// module's own saved state says everything about where the canopy should be.
    ///
    /// A part config can opt out with simulate = false.
    /// </summary>
    public class ModuleParaSoft : PartModule
    {
        [KSPField]
        public bool simulate = true;

        [KSPField(guiActive = true, guiName = "Softbody canopy")]
        public string status = "";

        private readonly List<CanopyController> controllers = new List<CanopyController>();
        private bool dying;
        private float nextStatus;

        public override void OnStart(StartState state)
        {
            Fields["status"].guiActive = false;
            if (!HighLogic.LoadedSceneIsFlight || !simulate) return;
            GameEvents.onPartDie.Add(OnPartDie);
            StartCoroutine(Setup());
        }

        private IEnumerator Setup()
        {
            // Give the parachute module and anything that dresses it (RealChute's canopy
            // picker, Custom Parachute Message) a couple of frames to finish building the
            // canopy before it is read.
            yield return null;
            yield return null;
            var system = ParaSoftSystem.Instance;
            if (part == null) yield break;
            if (system == null)
            {
                Log.Warn("{0}: the flight-scene canopy system is not running; canopy stays animated.", part.partInfo.title);
                yield break;
            }

            foreach (var host in ChuteHost.Discover(part))
            {
                host.Refresh();
                foreach (var slot in host.Slots)
                {
                    var c = new CanopyController(part, host, slot);
                    controllers.Add(c);
                    system.Register(c);
                }
            }
            if (controllers.Count > 0) Fields["status"].guiActive = true;
            else Log.Debug_("{0}: no parachute module ParaSoft recognises.", part.partInfo.title);
        }

        public void Update()
        {
            if (controllers.Count == 0 || Time.time < nextStatus) return;
            nextStatus = Time.time + 0.5f;
            if (controllers.Count == 1)
            {
                status = controllers[0].Status;
                return;
            }
            var sb = new StringBuilder();
            for (var i = 0; i < controllers.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(controllers[i].Status);
            }
            status = sb.ToString();
        }

        private void OnPartDie(Part p)
        {
            if (p == part) dying = true;
        }

        public void OnDestroy()
        {
            GameEvents.onPartDie.Remove(OnPartDie);
            var system = ParaSoftSystem.Instance;
            foreach (var c in controllers)
            {
                if (system != null)
                {
                    c.HostLost(system, dying);
                    system.Unregister(c);
                }
                else c.Dispose();
            }
            controllers.Clear();
        }

        public override string GetInfo()
        {
            return simulate ? "Softbody canopy: deploys, inflates and reacts to wind, waves and impacts (ParaSoft). Drag is unchanged." : "";
        }

        public override string GetModuleDisplayName()
        {
            return "ParaSoft canopy";
        }
    }
}
