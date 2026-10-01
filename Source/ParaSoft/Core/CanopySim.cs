using System;
using System.Collections.Generic;

namespace ParaSoft.Core
{
    /// <summary>What the air, gravity and the craft are doing this physics frame.</summary>
    public struct SimEnvironment
    {
        /// <summary>
        /// Velocity of the simulation frame - the anchor point on the part, or for a cut
        /// canopy whatever the game layer is tracking it with. Any consistent inertial-ish
        /// frame will do; KSP's is Unity velocity plus the Krakensbane frame velocity.
        /// </summary>
        public Vec3 FrameVelocity;
        /// <summary>Velocity of the air mass in the same frame: wind plus the planet's rotation.</summary>
        public Vec3 AirVelocity;
        public Vec3 Gravity;
        /// <summary>Air density, kg/m^3. Zero in vacuum.</summary>
        public float Density;
        /// <summary>Speed of sound, m/s. Zero if unknown.</summary>
        public float SpeedOfSound;
        /// <summary>Gustiness, 0 = none, 1 = strong. Scales small-scale turbulence.</summary>
        public float Turbulence;
    }

    /// <summary>Tuning for one canopy. Defaults are nylon canopy fabric and cord.</summary>
    public sealed class SimParameters
    {
        /// <summary>Fabric mass per area, kg/m^2 (ripstop nylon is 0.04-0.07).</summary>
        public float ArealDensity = 0.05f;
        /// <summary>Suspension line mass per length, kg/m.</summary>
        public float LineDensity = 0.012f;
        public float LineDiameter = 0.005f;
        /// <summary>Fraction of ram air the fabric lets through. Lower inflates harder.</summary>
        public float Porosity = 0.08f;
        /// <summary>Filling time in canopy diameters travelled; real round canopies take 2-8.</summary>
        public float FillDistance = 2.5f;
        /// <summary>
        /// Scales the mass of air the canopy drags along. It is what makes a real canopy
        /// swing slowly rather than snap about, and what keeps the explicit aerodynamics
        /// stable at high dynamic pressure.
        /// </summary>
        public float ApparentMass = 0.35f;
        public float MaxSubstep = 1f / 200f;
        public int Iterations = 2;
        /// <summary>Pilot chute drag area as a fraction of the main canopy's.</summary>
        public float PilotFraction = 0.02f;
        /// <summary>Speed the pack leaves the part at, relative to it, m/s.</summary>
        public float EjectSpeed = 12f;
        /// <summary>Longest a deployment may take before whatever is still packed is let go.</summary>
        public float ExtractionTimeout = 5f;
        /// <summary>Stiffness multiplier on every fabric and line constraint.</summary>
        public float Stiffness = 1f;
        /// <summary>
        /// How firmly an open canopy's pull lines up with the air it meets, 0..1. Real round
        /// canopies with a vent and porous cloth pull almost straight along the relative
        /// wind, and that is what stops them swinging: a canopy moving sideways meets the air
        /// at an angle and is pulled back. 0 leaves only the pressure on the cloth, which
        /// swings and glides like a solid canopy with no vent.
        /// </summary>
        public float Stability = 0.8f;

        // Pressure coefficients - see docs/TECHNICAL.md, "Canopy aerodynamics".
        public float InternalPressure = 1.0f;
        public float BasePressure = 0.4f;
        public float WindwardPressure = 1.0f;
        public float FlatPlate = 1.2f;
        public float SkinFriction = 0.015f;
    }

    public enum CanopyPhase
    {
        Stowed,
        /// <summary>Pack leaving the part, lines and canopy paying out behind it.</summary>
        Extracting,
        Flying
    }

    /// <summary>
    /// One canopy as a softbody: XPBD cloth on the lattice's particles, with ram-air
    /// inflation, suspension lines, a pilot chute, reefing and collisions.
    ///
    /// Positions live in a frame whose origin is the anchor point and whose axes are the
    /// world's; velocities are relative to that frame, which moves at the anchor's velocity.
    /// That makes the canopy immune to KSP's floating origin and Krakensbane shifts - both
    /// move the anchor and the canopy together - and keeps coordinates small.
    ///
    /// The anchor is kinematic. The canopy is pulled along by the craft and pushed around
    /// by air, gravity and anything it touches, but never pushes back: drag stays exactly
    /// what the parachute module computes.
    /// </summary>
    public sealed class CanopySim
    {
        public readonly CanopyLattice Lattice;
        public readonly SimParameters Parameters;

        private readonly int n;
        private readonly Vec3[] x;
        private readonly Vec3[] v;         // relative to the frame
        private readonly Vec3[] pred;
        private readonly Vec3[] force;
        private readonly float[] damping;
        private readonly float[] gravMass;
        private readonly float[] invMass;
        private readonly float[] gravFactor; // weight over inertia: below 1 where air rides along
        private readonly bool[] released;
        private readonly Vec3[] jitter;
        private readonly float[] conRest;  // working rest lengths (hem hoops follow the reefing)
        private readonly Vec3[] gust;      // turbulence per triangle, sampled once a frame

        private readonly float collisionRadius;
        private readonly float hemAreaRest;
        private readonly float riserLength;
        private readonly float canopyCdA;
        private readonly Vec3[] turbDir = new Vec3[3];
        private readonly Vec3[] turbWave = new Vec3[3];
        private readonly float[] turbPhase = new float[3];
        private readonly Vec3 spreadDir;   // which way to go if a neighbour is right on top

        private Vec3 lastFrameVelocity;
        private bool haveFrameVelocity;
        private float extractTime;
        private float time;
        private float reefTarget = 1f;
        private Vec3 flowDir;              // which way the air moves past the canopy
        private Vec3 clusterForce;         // from neighbouring canopies, this frame
        private float clusterArea;
        private Vec3 contactA, contactB;   // this canopy's own contact volume, this frame
        private bool haveContact;

        /// <summary>
        /// Other canopies close to this one, as capsules in this canopy's frame, with their
        /// velocities relative to it. The game layer fills this before each step (see
        /// CanopyContacts). Fabric is kept out of them, and the canopy as a whole is pushed
        /// away from any it crowds - which is what spreads a cluster apart.
        /// </summary>
        public readonly List<CollisionCapsule> Neighbours = new List<CollisionCapsule>();

        /// <summary>How close two canopies' contact volumes may come, as a multiple of their radii, before the air between them pushes them apart.</summary>
        private const float ClusterSpacing = 1.1f;
        /// <summary>That push at full overlap, in canopy drag areas.</summary>
        private const float ClusterStiffness = 2.5f;

        public CanopyPhase Phase { get; private set; }
        public bool Anchored { get; private set; }
        /// <summary>0 = limp, 1 = fully inflated.</summary>
        public float Fill { get; private set; }
        /// <summary>Hem circumference as a fraction of its design length.</summary>
        public float Reef { get; private set; }
        public float Speed { get; private set; }
        public float DynamicPressure { get; private set; }
        /// <summary>How squarely the mouth faces into the airflow, -1..1.</summary>
        public float Alignment { get; private set; }
        /// <summary>Hem opening area over its design area.</summary>
        public float MouthOpening { get; private set; }
        public Vec3 CanopyAxis { get; private set; }
        public float Mach { get; private set; }
        /// <summary>Set when the solver had to recover from a numerical blow-up.</summary>
        public int Recoveries { get; private set; }

        /// <summary>Particle positions in the simulation frame. Read-only for callers.</summary>
        public Vec3[] Positions { get { return x; } }
        public int ParticleCount { get { return n; } }

        public CanopySim(CanopyLattice lattice, SimParameters parameters, int seed)
        {
            Lattice = lattice;
            Parameters = parameters ?? new SimParameters();
            n = lattice.ParticleCount;
            x = new Vec3[n];
            v = new Vec3[n];
            pred = new Vec3[n];
            force = new Vec3[n];
            damping = new float[n];
            gravMass = new float[n];
            invMass = new float[n];
            gravFactor = new float[n];
            released = new bool[n];
            jitter = new Vec3[n];
            conRest = (float[])lattice.ConRest.Clone();
            gust = new Vec3[lattice.Triangles.Length / 3];

            var shape = lattice.Shape;
            var p = Parameters;
            var totalFabric = 0f;
            for (var i = 0; i < n; i++)
            {
                gravMass[i] = lattice.FabricArea[i] * p.ArealDensity + lattice.LineLength[i] * p.LineDensity;
                totalFabric += gravMass[i];
            }
            gravMass[lattice.PilotIndex] = Math.Max(0.2f, 0.02f * totalFabric);
            gravMass[lattice.ConfluenceIndex] = Math.Max(gravMass[lattice.ConfluenceIndex], 0.05f);
            for (var i = 0; i < n; i++) gravMass[i] = Math.Max(gravMass[i], 1e-3f);

            collisionRadius = MathX.Clamp(0.02f * shape.MaxRadius, 0.03f, 0.3f);
            riserLength = shape.Confluence.Length;
            canopyCdA = (p.InternalPressure + p.BasePressure) * shape.ProjectedArea;

            Vec3 hemCentre;
            hemAreaRest = Math.Max(HemArea(lattice.RestPositions, out hemCentre), 1e-4f);

            var rng = new Random(seed);
            for (var i = 0; i < n; i++)
                jitter[i] = new Vec3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
            for (var m = 0; m < 3; m++)
            {
                turbDir[m] = new Vec3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).NormalizedOr(Vec3.UnitX);
                turbWave[m] = new Vec3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).NormalizedOr(Vec3.UnitY);
                turbPhase[m] = (float)(rng.NextDouble() * Math.PI * 2);
            }
            spreadDir = new Vec3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).NormalizedOr(Vec3.UnitX);

            Phase = CanopyPhase.Stowed;
            Anchored = true;
            Reef = 1f;
            CanopyAxis = shape.Axis;
        }

        // -------------------------------------------------------------------------------
        // Control
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Starts a deployment: the whole canopy is a pack at the anchor, fired off along
        /// ejectDirection. Lines, then the hem, then each ring of fabric leave the pack as
        /// it travels far enough away to have pulled them out.
        /// </summary>
        public void BeginDeploy(Vec3 ejectDirection)
        {
            Version++;
            var dir = ejectDirection.NormalizedOr(Lattice.Shape.Axis);
            var packRadius = MathX.Clamp(0.04f * Lattice.Shape.MaxRadius, 0.05f, 0.6f);
            var start = dir * packRadius * 2f;
            for (var i = 0; i < n; i++)
            {
                released[i] = false;
                x[i] = start + jitter[i] * packRadius;
                v[i] = dir * Parameters.EjectSpeed;
            }
            released[Lattice.PilotIndex] = true;
            ReleaseConfluence();
            Phase = CanopyPhase.Extracting;
            Anchored = true;
            Fill = 0f;
            extractTime = 0f;
            haveFrameVelocity = false;
        }

        /// <summary>
        /// Puts the canopy straight into flight, fully open and trailing along the given
        /// direction - for craft loaded with their chutes already out.
        /// </summary>
        public void InitialiseOpen(Vec3 crownDirection)
        {
            Version++;
            var dir = crownDirection.NormalizedOr(Lattice.Shape.Axis);
            var q = Quat.FromTo(Lattice.Shape.Axis, dir);
            for (var i = 0; i < n; i++)
            {
                x[i] = q * Lattice.RestPositions[i];
                v[i] = Vec3.Zero;
                released[i] = true;
            }
            Phase = CanopyPhase.Flying;
            Fill = 1f - Parameters.Porosity;
            Reef = reefTarget;
            ApplyReef();
            haveFrameVelocity = false;
            CanopyAxis = dir;
        }

        /// <summary>
        /// How far the canopy may open: the hem's circumference as a fraction of its
        /// design length. Parachute modules report deployment as a drag area, so the game
        /// layer passes sqrt(current area / full area) - the canopy is then never visibly
        /// more open than the drag it is producing.
        /// </summary>
        public void SetReefTarget(float fraction)
        {
            reefTarget = MathX.Clamp(fraction, 0.02f, 1f);
        }

        /// <summary>Cuts the lines from the craft. The canopy flies on by itself.</summary>
        public void Detach()
        {
            Anchored = false;
            invMass[Lattice.ConfluenceIndex] = 1f / gravMass[Lattice.ConfluenceIndex];
        }

        public void Stow()
        {
            Phase = CanopyPhase.Stowed;
            Fill = 0f;
        }

        private void ReleaseConfluence()
        {
            var c = Lattice.ConfluenceIndex;
            released[c] = true;
            x[c] = riserLength > 0.01f ? Lattice.Shape.Confluence.NormalizedOr(Lattice.Shape.Axis) * Math.Min(riserLength, 0.05f) : Vec3.Zero;
            v[c] = Vec3.Zero;
        }

        /// <summary>
        /// Forgets the frame's last velocity, so the next step does not read a jump in it
        /// as an impulse - after the craft has been on rails, or the canopy frozen by
        /// distance, the anchor's velocity may have changed arbitrarily in between.
        /// </summary>
        public void ResetFrame()
        {
            haveFrameVelocity = false;
        }

        /// <summary>Mass-weighted mean velocity of the fabric, relative to the frame.</summary>
        public Vec3 MeanVelocity
        {
            get
            {
                var mv = Vec3.Zero;
                var mm = 0f;
                for (var i = 0; i < n; i++)
                {
                    if (!released[i]) continue;
                    mv += v[i] * gravMass[i];
                    mm += gravMass[i];
                }
                return mm > 0f ? mv / mm : Vec3.Zero;
            }
        }

        /// <summary>Distance from the frame origin to the farthest particle.</summary>
        public float Extent
        {
            get
            {
                var m = 0f;
                for (var i = 0; i < n; i++) m = Math.Max(m, x[i].SqrLength);
                return MathX.Sqrt(m);
            }
        }

        /// <summary>
        /// Moves the simulation frame's origin by delta (in the frame's own coordinates).
        /// The game layer calls this for a detached canopy it re-centres each frame.
        /// </summary>
        public void ShiftOrigin(Vec3 delta)
        {
            for (var i = 0; i < n; i++) x[i] -= delta;
            Version++;
        }

        // -------------------------------------------------------------------------------
        // Stepping
        // -------------------------------------------------------------------------------

        /// <summary>Changes whenever the particles move - lets a renderer skip frames where nothing did.</summary>
        public int Version { get; private set; }

        public void Step(float dt, SimEnvironment env, CollisionWorld world)
        {
            if (Phase == CanopyPhase.Stowed || dt <= 0f) return;
            Version++;

            // The frame moves with the anchor, so a change in anchor velocity is felt by
            // the canopy as the opposite change in its relative velocity.
            if (haveFrameVelocity)
            {
                var dv = lastFrameVelocity - env.FrameVelocity;
                if (dv.SqrLength > 0f)
                    for (var i = 0; i < n; i++) v[i] += dv;
            }
            lastFrameVelocity = env.FrameVelocity;
            haveFrameVelocity = true;

            time += dt;
            if (Phase == CanopyPhase.Extracting) extractTime += dt;

            UpdateMasses(env);
            UpdateCanopyState(dt, env);
            UpdateClusterForce();

            // Gusts change on the scale of a canopy diameter, far slower than a substep,
            // so they are sampled once per frame.
            var tris = Lattice.Triangles;
            for (var t = 0; t < gust.Length; t++)
            {
                var centre = (x[tris[t * 3]] + x[tris[t * 3 + 1]] + x[tris[t * 3 + 2]]) / 3f;
                gust[t] = Turbulence(centre, Speed, env);
            }

            var steps = Math.Max(1, (int)Math.Ceiling(dt / Parameters.MaxSubstep - 1e-4f));
            var h = dt / steps;
            var iterations = Math.Max(1, Parameters.Iterations);
            for (var s = 0; s < steps; s++)
            {
                ComputeForces(env);
                Integrate(h, env);
                if (Phase == CanopyPhase.Extracting) CarryPack(h);
                for (var it = 0; it < iterations; it++) SolveConstraints(h);
                SolveAnchor();
                Collide(world, h);
                Finish(h, env);
            }

            if (!Healthy())
            {
                Recoveries++;
                InitialiseOpen(CanopyAxis);
            }
        }

        private void UpdateMasses(SimEnvironment env)
        {
            var am = Parameters.ApparentMass * env.Density * Lattice.Shape.MaxRadius;
            var pack = 0f;
            for (var i = 0; i < n; i++)
            {
                var m = gravMass[i] + am * Lattice.FabricArea[i];
                invMass[i] = released[i] ? 1f / m : 0f;
                gravFactor[i] = gravMass[i] * invMass[i];
                if (!released[i]) pack += gravMass[i];
            }
            // The bag carries whatever is still packed in it.
            var bag = Lattice.PilotIndex;
            var bagMass = gravMass[bag] + pack;
            invMass[bag] = 1f / bagMass;
            gravFactor[bag] = 1f;
            if (Anchored && riserLength <= 0.01f) invMass[Lattice.ConfluenceIndex] = 0f;
        }

        /// <summary>Canopy-wide airflow, mouth, and the filling of the canopy with ram air.</summary>
        private void UpdateCanopyState(float dt, SimEnvironment env)
        {
            var mv = Vec3.Zero;
            var mm = 0f;
            for (var i = 0; i < Lattice.DomeCount; i++)
            {
                if (!released[i]) continue;
                mv += v[i] * gravMass[i];
                mm += gravMass[i];
            }
            var rel = (mm > 0f ? mv / mm : Vec3.Zero) + env.FrameVelocity - env.AirVelocity;
            Speed = rel.Length;
            flowDir = Speed > 1e-3f ? -rel / Speed : -CanopyAxis;
            Mach = env.SpeedOfSound > 1f ? Speed / env.SpeedOfSound : 0f;
            DynamicPressure = 0.5f * env.Density * Speed * Speed * MachFactor(Mach);

            Vec3 hemCentre;
            var hemArea = HemArea(x, out hemCentre);
            var vent = Vec3.Zero;
            for (var g = 0; g < Lattice.Gores; g++) vent += x[Lattice.Dome(0, g)];
            vent = vent / Lattice.Gores;
            CanopyAxis = (vent - hemCentre).NormalizedOr(CanopyAxis);
            MouthOpening = hemArea / hemAreaRest;
            Alignment = Speed > 1e-3f ? Vec3.Dot(CanopyAxis, -rel / Speed) : 0f;

            var target = 0f;
            if (Phase == CanopyPhase.Flying && Speed > 1e-3f)
            {
                var support = Parameters.ArealDensity * Math.Max(env.Gravity.Length, 0.1f) * 3f;
                target = (1f - Parameters.Porosity)
                         * MathX.SmoothStep(0.05f, 0.6f, Alignment)
                         * MathX.Clamp01(2.5f * MathX.Sqrt(Math.Max(0f, MouthOpening)))
                         * MathX.Clamp01(DynamicPressure / support);
            }
            var diameter = 2f * Lattice.Shape.MaxRadius;
            var tau = Parameters.FillDistance * diameter / Math.Max(Speed, 1f);
            // Once the air stops, a canopy empties in a second or two whatever its size.
            if (target < Fill) tau = Math.Min(tau * 0.5f, 1f);
            Fill += (target - Fill) * (1f - (float)Math.Exp(-dt / Math.Max(tau, 1e-3f)));

            // The reefing line lets out quickly when cut but is never taken in faster than
            // the fabric could be gathered.
            var dr = reefTarget - Reef;
            Reef += dr > 0f ? Math.Min(dr, 4f * dt) : Math.Max(dr, -2f * dt);
            ApplyReef();
        }

        private void ApplyReef()
        {
            for (var k = 0; k < Lattice.HemHoops.Length; k++)
            {
                var c = Lattice.HemHoops[k];
                conRest[c] = Lattice.ConRest[c] * Reef;
            }
        }

        /// <summary>Supersonic canopies lose drag and inflate less; see TECHNICAL.md.</summary>
        private static float MachFactor(float mach)
        {
            if (mach <= 0.8f) return 1f;
            if (mach >= 2.5f) return 0.6f;
            return MathX.Lerp(1f, 0.6f, (mach - 0.8f) / 1.7f);
        }

        private Vec3 Turbulence(Vec3 p, float speed, SimEnvironment env)
        {
            if (env.Turbulence <= 0f || speed < 0.1f) return Vec3.Zero;
            var d = 2f * Lattice.Shape.MaxRadius;
            var result = Vec3.Zero;
            var scale = 1f;
            for (var m = 0; m < 3; m++)
            {
                var k = MathX.TwoPi / (d * scale);
                var phase = Vec3.Dot(turbWave[m], p) * k + time * k * speed * 0.8f + turbPhase[m];
                result += turbDir[m] * ((float)Math.Sin(phase) * scale);
                scale *= 0.5f;
            }
            return result * (0.06f * env.Turbulence * speed);
        }

        private void ComputeForces(SimEnvironment env)
        {
            for (var i = 0; i < n; i++)
            {
                force[i] = Vec3.Zero;
                damping[i] = 0f;
            }
            if (env.Density <= 0f) return;

            var p = Parameters;
            var rho = env.Density;
            var machScale = MachFactor(Mach);
            var qInf = DynamicPressure;
            if (Mach > 1.05f)
            {
                // Supersonic canopies "breathe" - the shock ahead of them stands off and
                // collapses a few times a second. Mars parachutes do it in every test.
                var breathe = 0.18f * ((float)Math.Sin(time * 13.1f) + 0.6f * (float)Math.Sin(time * 29.7f + 1.3f));
                qInf *= 1f + breathe * MathX.Clamp01((Mach - 1.05f) * 4f);
            }
            var frameToAir = env.FrameVelocity - env.AirVelocity;
            var fill = Fill;
            // Loose fabric meets the air as a plate; a full canopy is all ram pressure and
            // wake. Leaving even a little plate drag on a full canopy lets its panels flutter
            // at speed, and a small drogue then glides off 30 degrees to one side.
            var plate = 1f - MathX.Clamp01(fill / Math.Max(0.05f, 1f - p.Porosity));

            var tris = Lattice.Triangles;
            for (var t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (!released[a] || !released[b] || !released[c]) continue;
                var pa = x[a];
                var nraw = Vec3.Cross(x[b] - pa, x[c] - pa);
                var a2 = nraw.Length;
                if (a2 < 1e-8f) continue;
                var nrm = nraw / a2;
                var area = 0.5f * a2;

                var u = (v[a] + v[b] + v[c]) / 3f + frameToAir - gust[t / 3];
                var us = u.Length;
                if (us < 1e-4f && fill <= 0f) continue;
                var w = us > 1e-4f ? -u / us : Vec3.Zero;
                var s = Vec3.Dot(nrm, w);
                var qt = 0.5f * rho * us * us * machScale;

                var cout = s >= 0f ? -p.BasePressure : -p.BasePressure + (p.WindwardPressure + p.BasePressure) * s * s;
                var flat = p.FlatPlate * s * Math.Abs(s);
                var dp = fill * qInf * (p.InternalPressure - cout) + plate * qt * flat;
                var f = nrm * (area * dp);
                if (us > 1e-4f)
                {
                    var ut = u - nrm * Vec3.Dot(u, nrm);
                    f -= ut * (0.5f * rho * us * area * p.SkinFriction);
                }
                f = f / 3f;
                force[a] += f;
                force[b] += f;
                force[c] += f;
                var damp = rho * us * area * (p.FlatPlate * Math.Abs(s) + p.SkinFriction + 0.2f) / 3f;
                damping[a] += damp;
                damping[b] += damp;
                damping[c] += damp;
            }

            // Pressure on the cloth pulls along the canopy's own axis whichever way the air
            // meets it, which leaves a canopy free to swing and glide. Turn that pull towards
            // the air the canopy is actually meeting - its own sideways motion included, so a
            // swing is met by a pull back.
            if (p.Stability > 0f && fill > 0.05f)
            {
                var net = Vec3.Zero;
                var mv = Vec3.Zero;
                var mm = 0f;
                var area = 0f;
                for (var i = 0; i < Lattice.DomeCount; i++)
                {
                    if (!released[i]) continue;
                    net += force[i];
                    mv += v[i] * gravMass[i];
                    mm += gravMass[i];
                    area += Lattice.FabricArea[i];
                }
                var rel = (mm > 0f ? mv / mm : Vec3.Zero) + frameToAir;
                var speed = rel.Length;
                var pull = net.Length;
                if (speed > 1f && pull > 0f && area > 0f)
                {
                    var along = rel * (-pull / speed);
                    var corr = (along - net) * (MathX.Clamp01(p.Stability) * fill / area);
                    for (var i = 0; i < Lattice.DomeCount; i++)
                        if (released[i]) force[i] += corr * Lattice.FabricArea[i];
                }
            }

            // Crowding by neighbouring canopies: spread over the fabric like a pressure,
            // so the whole canopy moves off rather than just dimpling.
            if (clusterArea > 0f)
            {
                for (var i = 0; i < Lattice.DomeCount; i++)
                    if (released[i]) force[i] += clusterForce * (Lattice.FabricArea[i] / clusterArea);
            }

            // Cord drag: a cylinder in crossflow.
            for (var k = 0; k < Lattice.ConA.Length; k++)
            {
                if (Lattice.ConKind[k] != ConstraintKind.Line) continue;
                int a = Lattice.ConA[k], b = Lattice.ConB[k];
                if (!released[a] || !released[b]) continue;
                var seg = x[b] - x[a];
                var len = seg.Length;
                if (len < 1e-4f) continue;
                var dir = seg / len;
                var u = (v[a] + v[b]) * 0.5f + frameToAir;
                var un = u - dir * Vec3.Dot(u, dir);
                var uns = un.Length;
                var f = un * (-0.5f * rho * uns * 1.2f * p.LineDiameter * len * 0.5f);
                force[a] += f;
                force[b] += f;
                var damp = rho * uns * 1.2f * p.LineDiameter * len * 0.5f;
                damping[a] += damp;
                damping[b] += damp;
            }

            // The pilot chute: small, always open.
            var pi = Lattice.PilotIndex;
            var up = v[pi] + frameToAir;
            var ups = up.Length;
            var pilotCdA = p.PilotFraction * canopyCdA;
            if (Phase == CanopyPhase.Extracting) pilotCdA *= 1.5f; // the bag adds its own drag
            force[pi] += up * (-0.5f * rho * ups * pilotCdA);
            damping[pi] += rho * ups * pilotCdA;
        }

        private void Integrate(float h, SimEnvironment env)
        {
            for (var i = 0; i < n; i++)
            {
                if (invMass[i] <= 0f)
                {
                    pred[i] = x[i];
                    continue;
                }
                var m = 1f / invMass[i];
                // Aerodynamic damping is taken implicitly (linearised backward Euler), which
                // keeps light fabric stable when the air pushing on it is hundreds of times
                // its weight.
                var aero = force[i] / (m + h * damping[i]);
                var grav = env.Gravity * gravFactor[i];
                v[i] += (aero + grav) * h;
                pred[i] = x[i] + v[i] * h;
            }
        }

        /// <summary>
        /// While extracting, the pilot particle is the deployment bag. Anything not yet
        /// pulled out rides along inside it; anything the bag has travelled far enough to
        /// pull out is let go where the bag is, so the canopy lays out behind it as a
        /// streamer the way a real one leaves its bag. The riser comes out first, then the
        /// lines, then the canopy from the hem to the crown.
        /// </summary>
        private void CarryPack(float h)
        {
            var bag = Lattice.PilotIndex;
            var conf = Lattice.ConfluenceIndex;
            var bagDist = pred[bag].Length;
            var pull = (pred[bag] / Math.Max(bagDist, 1e-6f)).NormalizedOr(Lattice.Shape.Axis);
            if (riserLength > 0.01f) pred[conf] = pull * Math.Min(bagDist, riserLength);
            // How much of the lines and canopy is out, measured from the confluence.
            var reach = Math.Max(0f, bagDist - riserLength);
            var side1 = Vec3.AnyPerpendicular(pull);
            var side2 = Vec3.Cross(pull, side1);
            var timeout = extractTime > Parameters.ExtractionTimeout;
            var packRadius = MathX.Clamp(0.04f * Lattice.Shape.MaxRadius, 0.05f, 0.6f);
            var allOut = true;
            for (var i = 0; i < n; i++)
            {
                if (i == bag || i == conf) continue;
                if (released[i])
                {
                    // Already paid out: while the bag is still pulling, everything behind it
                    // is under tension and lies straight along the way it came out - even
                    // though, in the lattice, it is not yet joined to what is still packed.
                    if (invMass[i] <= 0f) continue;
                    pred[i] = pred[conf] + StreamerLayout(i, reach, pull, side1, side2);
                    continue;
                }
                if (timeout || reach >= Lattice.PayoutDistance[i])
                {
                    released[i] = true;
                    // Let go where its line and seam actually put it, not wherever the bag
                    // has got to this substep. Once that is reached the line is taut, and only
                    // the bag's sideways motion carries on: the outward part is what the
                    // snatch takes out.
                    var vel = v[bag];
                    if (reach >= Lattice.PayoutDistance[i])
                    {
                        var outward = Vec3.Dot(vel, pull);
                        if (outward > 0f) vel -= pull * outward;
                    }
                    pred[i] = pred[conf] + StreamerLayout(i, reach, pull, side1, side2);
                    // x is set one substep behind so the particle leaves with that velocity.
                    x[i] = pred[i] - vel * h;
                    v[i] = vel;
                    invMass[i] = 1f / gravMass[i];
                    gravFactor[i] = 1f;
                }
                else
                {
                    allOut = false;
                    pred[i] = pred[bag] + jitter[i] * packRadius;
                    x[i] = pred[i] - v[bag] * h;
                    v[i] = v[bag];
                }
            }
            if (allOut)
            {
                // The bag's momentum is what the snatch at full stretch absorbs; what is
                // left flying is the pilot chute on its own, moving with the crown.
                Phase = CanopyPhase.Flying;
                var crown = Vec3.Zero;
                for (var g = 0; g < Lattice.Gores; g++) crown += v[Lattice.Dome(0, g)];
                crown = crown / Lattice.Gores;
                v[bag] = crown;
                x[bag] = pred[bag] - crown * h;
                invMass[bag] = 1f / gravMass[bag];
            }
        }

        /// <summary>
        /// Where particle i lies in a canopy being pulled out straight, relative to the
        /// confluence: gores spread a little around the pull so the streamer has a mouth,
        /// lines fanning from the confluence to them, and every seam and line segment at
        /// exactly its rest length - so nothing is pre-stretched when the canopy is let go.
        /// </summary>
        private Vec3 StreamerLayout(int i, float reach, Vec3 pull, Vec3 side1, Vec3 side2)
        {
            var lat = Lattice;
            var spread = 0.04f * lat.Shape.MaxRadius;
            var lineLen = lat.PayoutDistance[lat.Dome(lat.Rings, 0)];
            var hemAlong = MathX.Sqrt(Math.Max(1e-6f, lineLen * lineLen - spread * spread));
            int gore;
            bool isLine;
            if (i < lat.DomeCount)
            {
                gore = i % lat.Gores;
                isLine = false;
            }
            else
            {
                gore = (i - lat.DomeCount) / Math.Max(1, lat.LineSegments - 1);
                isLine = true;
            }
            var th = lat.GoreAngle(gore);
            var offset = (side1 * (float)Math.Cos(th) + side2 * (float)Math.Sin(th)) * spread;
            var pay = Math.Min(reach, lat.PayoutDistance[i]);
            if (isLine)
            {
                var t = lineLen > 1e-4f ? pay / lineLen : 0f;
                return (pull * hemAlong + offset) * t;
            }
            return pull * (hemAlong + (pay - lineLen)) + offset;
        }

        private void SolveConstraints(float h)
        {
            var stiffScale = Parameters.Stiffness * StiffnessScale();
            var h2 = h * h;
            var ca = Lattice.ConA;
            var cb = Lattice.ConB;
            var kind = Lattice.ConKind;
            for (var c = 0; c < ca.Length; c++)
            {
                int a = ca[c], b = cb[c];
                var wa = invMass[a];
                var wb = invMass[b];
                if (!released[a] || !released[b]) continue;
                var wsum = wa + wb;
                if (wsum <= 0f) continue;
                var d = pred[b] - pred[a];
                var len = d.Length;
                if (len < 1e-7f) continue;
                var rest = conRest[c];
                var k = kind[c];
                float err;
                if (k == ConstraintKind.Bend)
                {
                    // Fabric resists being folded flat on itself, not being straightened:
                    // a streamer is longer between these particles than the dome is.
                    rest *= 0.6f;
                    err = len - rest;
                    if (err >= 0f) continue;
                }
                else
                {
                    err = len - rest;
                    if (err <= 0f) continue; // fabric and cord go slack
                }
                float ea;
                switch (k)
                {
                    case ConstraintKind.Seam: ea = 4e4f; break;
                    case ConstraintKind.Hoop: ea = 2e4f; break;
                    case ConstraintKind.Shear: ea = 4e3f; break;
                    // Fabric has almost no bending stiffness. This is only enough to
                    // stop the lattice folding into a knot, not enough to hold up a
                    // collapsed canopy's own weight.
                    case ConstraintKind.Bend: ea = 1.5f; break;
                    default: ea = 6e4f; break;
                }
                var compliance = Math.Max(rest, 1e-3f) / (ea * stiffScale) / h2;
                var lambda = -err / (wsum + compliance);
                var corr = d * (lambda / len);
                pred[a] -= corr * wa;
                pred[b] += corr * wb;
            }
        }

        /// <summary>Tape and cord strength grows with canopy size; so does the load.</summary>
        private float StiffnessScale()
        {
            var r = Lattice.Shape.MaxRadius / 5f;
            return Math.Max(0.2f, r * r);
        }

        private void SolveAnchor()
        {
            if (!Anchored) return;
            var c = Lattice.ConfluenceIndex;
            if (riserLength <= 0.01f) pred[c] = Vec3.Zero;
            else
            {
                var len = pred[c].Length;
                if (len > riserLength) pred[c] = pred[c] * (riserLength / len);
            }

            // Long-range attachments: nothing can be further from the anchor than the
            // lines and seams it hangs from are long. A chain of light cord particles
            // holding a canopy dozens of times heavier (it drags its air along) is the
            // worst case for a Gauss-Seidel solver - left alone it lets the lines stretch
            // like bungee cord and then snaps back. This caps them exactly and cheaply.
            // The 3% slack matters: a tether held exactly at length fights every other
            // constraint that nudges the particle sideways, and each of those projections
            // leaks a little inward momentum until the whole canopy recoils. With slack
            // the lines themselves hold length and the tether only catches a runaway.
            var pay = Lattice.PayoutDistance;
            for (var i = 0; i < n; i++)
            {
                if (!released[i] || invMass[i] <= 0f) continue;
                var max = (pay[i] + riserLength) * 1.03f;
                var l2 = pred[i].SqrLength;
                if (l2 <= max * max) continue;
                pred[i] = pred[i] * (max / MathX.Sqrt(l2));
            }
        }

        private void Collide(CollisionWorld world, float h)
        {
            var neighbours = Phase == CanopyPhase.Flying ? Neighbours.Count : 0;
            if (world == null && neighbours == 0) return;
            var water = world != null ? world.Water : null;
            for (var i = 0; i < n; i++)
            {
                if (invMass[i] <= 0f || !released[i]) continue;
                if (i == Lattice.PilotIndex) continue;
                var p = pred[i];
                if (world != null) world.Resolve(ref p, x[i], collisionRadius, h);
                // Fabric on fabric slides easily.
                for (var k = 0; k < neighbours; k++)
                    CollisionWorld.PushOutOfCapsule(ref p, x[i], Neighbours[k], collisionRadius, h, 0.15f, haveContact, contactA, contactB);
                if (water != null)
                {
                    var c = water.Clearance(p);
                    if (c < 0f)
                    {
                        // Wet fabric: carried up towards the surface, and dragged hard
                        // towards the water's own motion.
                        p += water.Up * (-c * 0.35f);
                        var motion = (p - x[i]) - water.SurfaceVelocity * h;
                        p -= motion * 0.4f;
                    }
                }
                pred[i] = p;
            }
        }

        private void Finish(float h, SimEnvironment env)
        {
            var inv = 1f / h;
            var limit = 3f * (Speed + 60f);
            var limit2 = limit * limit;
            for (var i = 0; i < n; i++)
            {
                var nv = (pred[i] - x[i]) * inv;
                if (nv.SqrLength > limit2) nv = nv * (limit / nv.Length);
                v[i] = nv;
                x[i] = pred[i];
            }
        }

        private bool Healthy()
        {
            var bound = 50f * (Lattice.Shape.LineLength + Lattice.Shape.MeridianLength + 1f);
            var b2 = Anchored ? bound * bound : float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                if (!x[i].IsFinite || !v[i].IsFinite) return false;
                if (x[i].SqrLength > b2) return false;
            }
            return true;
        }

        private float HemArea(Vec3[] pos, out Vec3 centre)
        {
            var c = Vec3.Zero;
            for (var g = 0; g < Lattice.Gores; g++) c += pos[Lattice.Dome(Lattice.Rings, g)];
            c = c / Lattice.Gores;
            var s = Vec3.Zero;
            for (var g = 0; g < Lattice.Gores; g++)
            {
                var p0 = pos[Lattice.Dome(Lattice.Rings, g)] - c;
                var p1 = pos[Lattice.Dome(Lattice.Rings, g + 1)] - c;
                s += Vec3.Cross(p0, p1);
            }
            centre = c;
            return 0.5f * s.Length;
        }

        // -------------------------------------------------------------------------------
        // Canopy-to-canopy contact
        // -------------------------------------------------------------------------------

        /// <summary>Centre of the fabric, in this canopy's frame.</summary>
        public Vec3 BubbleCentre
        {
            get
            {
                var s = Vec3.Zero;
                var cnt = 0;
                for (var i = 0; i < Lattice.DomeCount; i++)
                {
                    if (!released[i]) continue;
                    s += x[i];
                    cnt++;
                }
                return cnt > 0 ? s / cnt : Vec3.Zero;
            }
        }

        private Vec3 RingCentre(Vec3[] pos, int ring)
        {
            var c = Vec3.Zero;
            for (var g = 0; g < Lattice.Gores; g++) c += pos[Lattice.Dome(ring, g)];
            return c / Lattice.Gores;
        }

        /// <summary>
        /// The space this canopy's fabric takes up, for other canopies to keep out of: a
        /// capsule from the hem's centre up towards the crown, as wide as the widest ring.
        /// For an open canopy that is close to a sphere about the mouth; for a streamer or a
        /// tightly reefed one it is long and thin. False while the canopy is still packed.
        /// </summary>
        public bool ContactCapsule(out Vec3 a, out Vec3 b, out float radius)
        {
            a = b = Vec3.Zero;
            radius = 0f;
            if (Phase != CanopyPhase.Flying) return false;
            var hem = RingCentre(x, Lattice.Rings);
            var vent = RingCentre(x, 0);
            var r = 0f;
            for (var k = 0; k <= Lattice.Rings; k++)
            {
                var c = RingCentre(x, k);
                var sum = 0f;
                for (var g = 0; g < Lattice.Gores; g++) sum += Vec3.Distance(x[Lattice.Dome(k, g)], c);
                r = Math.Max(r, sum / Lattice.Gores);
            }
            // A little inside the fabric, so neighbours touch rather than stand off.
            r *= 0.95f;
            if (!(r >= 1e-3f) || float.IsInfinity(r)) return false;
            var along = vent - hem;
            var len = along.Length;
            a = hem;
            b = len > r ? vent - along * (r / len) : hem;
            radius = r;
            return true;
        }

        /// <summary>
        /// Canopies in a cluster push each other apart: the air squeezed out between them
        /// has to go somewhere, and fabric pressed on fabric takes the shortest way round.
        /// Worked out once a frame, across the flow, from how far this canopy's contact
        /// volume crowds each neighbour's.
        /// </summary>
        private void UpdateClusterForce()
        {
            clusterForce = Vec3.Zero;
            clusterArea = 0f;
            haveContact = false;
            if (Neighbours.Count == 0) return;
            Vec3 a, b;
            float r;
            if (!ContactCapsule(out a, out b, out r)) return;
            contactA = a;
            contactB = b;
            haveContact = true;
            if (DynamicPressure <= 0f) return;
            var drag = DynamicPressure * MathX.Pi * r * r;
            for (var k = 0; k < Neighbours.Count; k++)
            {
                var nb = Neighbours[k];
                Vec3 pi, pj;
                Geometry.ClosestPoints(a, b, nb.A, nb.B, out pi, out pj);
                var reach = (r + nb.Radius) * ClusterSpacing;
                var d = pi - pj;
                var dist = d.Length;
                if (dist >= reach) continue;
                // Sideways only: along the flow the lines already say where each canopy goes.
                // Two canopies right on top of each other each take their own way out.
                var side = Vec3.Reject(d, flowDir);
                if (side.Length < 0.05f * reach) side += Vec3.Reject(spreadDir, flowDir) * (0.05f * reach);
                var dir = side.NormalizedOr(Vec3.AnyPerpendicular(flowDir));
                clusterForce += dir * (ClusterStiffness * drag * (reach - dist) / reach);
            }
            if (clusterForce.SqrLength <= 0f) return;
            for (var i = 0; i < Lattice.DomeCount; i++)
                if (released[i]) clusterArea += Lattice.FabricArea[i];
        }

        /// <summary>
        /// Joins the confluences of a cluster whose lines all meet at one point: each canopy
        /// pulls its own during a step, and afterwards they are put back together where the
        /// pulls balance, moving as one. All the sims must share a frame.
        /// </summary>
        public static void TieConfluences(IList<CanopySim> sims)
        {
            if (sims == null || sims.Count < 2) return;
            var p = Vec3.Zero;
            var mv = Vec3.Zero;
            var m = 0f;
            foreach (var s in sims)
            {
                var c = s.Lattice.ConfluenceIndex;
                if (!s.released[c]) return;
                p += s.x[c];
                mv += s.v[c] * s.gravMass[c];
                m += s.gravMass[c];
            }
            p = p / sims.Count;
            var vel = m > 0f ? mv / m : Vec3.Zero;
            foreach (var s in sims)
            {
                var c = s.Lattice.ConfluenceIndex;
                s.x[c] = p;
                s.v[c] = vel;
            }
        }

        /// <summary>
        /// The lower end of the riser, in the frame: at the anchor while attached, and once
        /// cut, trailing loose below the confluence.
        /// </summary>
        public Vec3 RiserEnd
        {
            get
            {
                if (Anchored) return Vec3.Zero;
                var c = x[Lattice.ConfluenceIndex];
                if (riserLength <= 0.01f) return c;
                return c + (c - BubbleCentre).NormalizedOr(-CanopyAxis) * riserLength;
            }
        }
    }
}
