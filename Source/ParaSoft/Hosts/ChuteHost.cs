using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ParaSoft.Hosts
{
    internal enum HostState
    {
        Stowed,
        /// <summary>Out but reefed: stock SEMIDEPLOYED, RealChute PREDEPLOYED.</summary>
        Semi,
        /// <summary>Opening or open: stock DEPLOYED, RealChute DEPLOYED or LOWDEPLOYED.</summary>
        Full,
        Cut
    }

    /// <summary>
    /// One canopy transform owned by a parachute module - its "slot". Most parts have one;
    /// RealChute's dual-chute cases have a main and a drogue, each a separate slot.
    /// </summary>
    internal abstract class ChuteSlot
    {
        internal abstract Transform Canopy { get; }
        internal abstract Transform Cap { get; }
        internal abstract HostState State { get; }
        /// <summary>
        /// The module's own idea of how open the canopy is, as a fraction of its full drag
        /// area. The softbody is never allowed to look more open than this, so what the
        /// player sees always matches the drag the module is applying.
        /// </summary>
        internal abstract float OpenFraction { get; }
        internal abstract string SemiAnimation { get; }
        internal abstract string FullAnimation { get; }
        /// <summary>Canopies in the model if the module knows (RealChute), else 0.</summary>
        internal virtual int ExpectedCanopies { get { return 0; } }
        /// <summary>Fabric areal density in kg/m^2 if the module knows its material, else 0.</summary>
        internal virtual float ArealDensity { get { return 0f; } }
        /// <summary>Stable identity for caching the fitted model.</summary>
        internal abstract string Key { get; }
    }

    /// <summary>A parachute module seen through whatever interface it happens to have.</summary>
    internal abstract class ChuteHost
    {
        internal readonly PartModule Module;
        internal readonly List<ChuteSlot> Slots = new List<ChuteSlot>();

        protected ChuteHost(PartModule module)
        {
            Module = module;
        }

        internal abstract string Name { get; }
        internal virtual bool IsEva { get { return false; } }

        /// <summary>
        /// Called every physics frame before the slots are read, so a host that has to
        /// discover its canopies lazily (RealChute builds them in OnStart) can refresh.
        /// </summary>
        internal virtual void Refresh() { }

        /// <summary>Wraps every parachute module on a part that ParaSoft understands.</summary>
        internal static List<ChuteHost> Discover(Part part)
        {
            var hosts = new List<ChuteHost>();
            foreach (PartModule m in part.Modules)
            {
                try
                {
                    ChuteHost h = null;
                    var stock = m as ModuleParachute;
                    if (stock != null) h = new StockHost(stock);
                    else
                    {
                        var typeName = m.GetType().FullName;
                        if (typeName == "RealChute.RealChuteModule") h = new RealChuteHost(m);
                        else if (typeName == "FerramAerospaceResearch.RealChuteLite.RealChuteFAR") h = new RealChuteFARHost(m);
                    }
                    if (h != null) hosts.Add(h);
                }
                catch (Exception e)
                {
                    Log.Exception("reading parachute module " + m.moduleName + " on " + part.partInfo.name, e);
                }
            }
            return hosts;
        }
    }

    /// <summary>Cached reflection access to a field or property by name.</summary>
    internal sealed class Member
    {
        private readonly FieldInfo field;
        private readonly PropertyInfo property;

        internal Member(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = type; t != null && field == null && property == null; t = t.BaseType)
            {
                field = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field == null) property = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            }
        }

        internal bool Exists { get { return field != null || property != null; } }

        internal object Get(object target)
        {
            if (target == null) return null;
            if (field != null) return field.GetValue(target);
            if (property != null) return property.GetValue(target, null);
            return null;
        }

        internal float GetFloat(object target, float fallback)
        {
            var v = Get(target);
            if (v == null) return fallback;
            try { return Convert.ToSingle(v); }
            catch { return fallback; }
        }

        internal static Member Require(Type type, string name)
        {
            var m = new Member(type, name);
            if (!m.Exists) throw new MissingMemberException(type.FullName, name);
            return m;
        }
    }

    internal static class HostStates
    {
        /// <summary>Maps any module's deployment enum onto ours by name, which survives renumbering.</summary>
        internal static HostState FromName(string name)
        {
            switch (name)
            {
                case "SEMIDEPLOYED":
                case "PREDEPLOYED":
                    return HostState.Semi;
                case "DEPLOYED":
                case "LOWDEPLOYED":
                    return HostState.Full;
                case "CUT":
                    return HostState.Cut;
                default:
                    return HostState.Stowed;
            }
        }
    }

    // ===================================================================================
    // Stock ModuleParachute and ModuleEvaChute
    // ===================================================================================

    internal sealed class StockHost : ChuteHost
    {
        private static Member canopyField, capField, lerpField;
        private readonly ModuleParachute chute;

        internal StockHost(ModuleParachute chute) : base(chute)
        {
            this.chute = chute;
            if (canopyField == null)
            {
                canopyField = new Member(typeof(ModuleParachute), "canopy");
                capField = new Member(typeof(ModuleParachute), "cap");
                lerpField = new Member(typeof(ModuleParachute), "lerpTime");
            }
            Slots.Add(new Slot(this));
        }

        internal override string Name { get { return chute is ModuleEvaChute ? "EVA parachute" : "stock parachute"; } }
        internal override bool IsEva { get { return chute is ModuleEvaChute; } }

        private sealed class Slot : ChuteSlot
        {
            private readonly StockHost host;
            internal Slot(StockHost host) { this.host = host; }

            internal override Transform Canopy
            {
                get
                {
                    var t = canopyField.Get(host.chute) as Transform;
                    return t != null ? t : host.chute.part.FindModelTransform(host.chute.canopyName);
                }
            }

            internal override Transform Cap
            {
                get
                {
                    var t = capField.Get(host.chute) as Transform;
                    return t != null ? t : host.chute.part.FindModelTransform(host.chute.capName);
                }
            }

            internal override HostState State
            {
                get
                {
                    var s = HostStates.FromName(host.chute.deploymentState.ToString());
                    // ACTIVE is armed and waiting, which to us is still stowed.
                    return s;
                }
            }

            internal override float OpenFraction
            {
                get
                {
                    var c = host.chute;
                    var lerp = Mathf.Clamp01(lerpField.GetFloat(c, 1f));
                    var full = c.areaDeployed > 0 ? (float)c.areaDeployed : 1f;
                    var semi = c.areaSemi > 0 ? (float)c.areaSemi : 0.1f * full;
                    switch (State)
                    {
                        case HostState.Semi: return Mathf.Clamp01(semi * lerp / full);
                        case HostState.Full: return Mathf.Clamp01(Mathf.Lerp(semi, full, lerp) / full);
                        default: return 0f;
                    }
                }
            }

            internal override string SemiAnimation { get { return host.chute.semiDeployedAnimation; } }
            internal override string FullAnimation { get { return host.chute.fullyDeployedAnimation; } }
            internal override string Key { get { return host.chute.part.partInfo.name + "/stock/" + host.chute.canopyName; } }
        }
    }

    // ===================================================================================
    // RealChute
    // ===================================================================================

    internal sealed class RealChuteHost : ChuteHost
    {
        private static Member parachutesField, canopyField, capField, stateProp, currentArea, deployedArea,
            preAnim, depAnim, canopyCount, mat, areaDensity, nameField;

        private object lastList;

        internal RealChuteHost(PartModule module) : base(module)
        {
            if (parachutesField == null)
            {
                var moduleType = module.GetType();
                var chuteType = moduleType.Assembly.GetType("RealChute.Parachute", true);
                parachutesField = Member.Require(moduleType, "parachutes");
                canopyField = Member.Require(chuteType, "parachute");
                capField = new Member(chuteType, "cap");
                stateProp = Member.Require(chuteType, "DeploymentState");
                currentArea = new Member(chuteType, "currentArea");
                deployedArea = new Member(chuteType, "DeployedArea");
                preAnim = new Member(chuteType, "preDeploymentAnimation");
                depAnim = new Member(chuteType, "deploymentAnimation");
                canopyCount = new Member(chuteType, "canopyCount");
                mat = new Member(chuteType, "mat");
                nameField = new Member(chuteType, "parachuteName");
                var matType = chuteType.Assembly.GetType("RealChute.Libraries.MaterialsLibrary.MaterialDefinition");
                if (matType != null) areaDensity = new Member(matType, "AreaDensity");
            }
            Refresh();
        }

        internal override string Name { get { return "RealChute"; } }

        internal override void Refresh()
        {
            var list = parachutesField.Get(Module) as IList;
            if (list == null || ReferenceEquals(list, lastList) && Slots.Count == list.Count) return;
            lastList = list;
            Slots.Clear();
            for (var i = 0; i < list.Count; i++) Slots.Add(new Slot(this, list[i], i));
        }

        private sealed class Slot : ChuteSlot
        {
            private readonly RealChuteHost host;
            private readonly object chute;
            private readonly int index;

            internal Slot(RealChuteHost host, object chute, int index)
            {
                this.host = host;
                this.chute = chute;
                this.index = index;
            }

            internal override Transform Canopy { get { return canopyField.Get(chute) as Transform; } }
            internal override Transform Cap { get { return capField.Get(chute) as Transform; } }

            internal override HostState State
            {
                get
                {
                    var s = stateProp.Get(chute);
                    return s == null ? HostState.Stowed : HostStates.FromName(s.ToString());
                }
            }

            internal override float OpenFraction
            {
                get
                {
                    var full = deployedArea.GetFloat(chute, 0f);
                    if (full <= 0f) return State == HostState.Stowed ? 0f : 1f;
                    return Mathf.Clamp01(currentArea.GetFloat(chute, full) / full);
                }
            }

            internal override string SemiAnimation { get { return preAnim.Get(chute) as string; } }
            internal override string FullAnimation { get { return depAnim.Get(chute) as string; } }
            internal override int ExpectedCanopies { get { return (int)canopyCount.GetFloat(chute, 0f); } }

            internal override float ArealDensity
            {
                get
                {
                    if (areaDensity == null) return 0f;
                    var m = mat.Get(chute);
                    // RealChute works in tonnes per square metre.
                    return m == null ? 0f : areaDensity.GetFloat(m, 0f) * 1000f;
                }
            }

            internal override string Key
            {
                get
                {
                    return host.Module.part.partInfo.name + "/rc" + index + "/" + (nameField.Get(chute) as string);
                }
            }
        }
    }

    // ===================================================================================
    // FAR's RealChuteLite
    // ===================================================================================

    internal sealed class RealChuteFARHost : ChuteHost
    {
        private static Member canopyField, capField, stateProp, currentArea, deployedDiameter, semiAnim, fullAnim;

        internal RealChuteFARHost(PartModule module) : base(module)
        {
            if (canopyField == null)
            {
                var t = module.GetType();
                canopyField = Member.Require(t, "parachute");
                capField = new Member(t, "cap");
                stateProp = Member.Require(t, "DeploymentState");
                currentArea = new Member(t, "currentArea");
                deployedDiameter = new Member(t, "deployedDiameter");
                semiAnim = new Member(t, "semiDeployedAnimation");
                fullAnim = new Member(t, "fullyDeployedAnimation");
            }
            Slots.Add(new Slot(this));
        }

        internal override string Name { get { return "FAR RealChuteLite"; } }

        private sealed class Slot : ChuteSlot
        {
            private readonly RealChuteFARHost host;
            internal Slot(RealChuteFARHost host) { this.host = host; }

            internal override Transform Canopy { get { return canopyField.Get(host.Module) as Transform; } }
            internal override Transform Cap { get { return capField.Get(host.Module) as Transform; } }

            internal override HostState State
            {
                get
                {
                    var s = stateProp.Get(host.Module);
                    return s == null ? HostState.Stowed : HostStates.FromName(s.ToString());
                }
            }

            internal override float OpenFraction
            {
                get
                {
                    var d = deployedDiameter.GetFloat(host.Module, 0f);
                    var full = d * d * Mathf.PI / 4f;
                    if (full <= 0f) return State == HostState.Stowed ? 0f : 1f;
                    return Mathf.Clamp01(currentArea.GetFloat(host.Module, full) / full);
                }
            }

            internal override string SemiAnimation { get { return semiAnim.Get(host.Module) as string; } }
            internal override string FullAnimation { get { return fullAnim.Get(host.Module) as string; } }
            // RealChuteLite is nylon: 5.65e-5 t/m^2.
            internal override float ArealDensity { get { return 0.0565f; } }
            internal override string Key { get { return host.Module.part.partInfo.name + "/far"; } }
        }
    }
}
