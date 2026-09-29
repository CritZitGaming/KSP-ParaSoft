using System;
using ParaSoft.Core;

/// <summary>
/// Behavioural checks on the canopy solver, run against the real Core sources compiled
/// straight into this program (Tools/Test.ps1). No game, no Unity: a canopy is dropped
/// into scripted conditions and the numbers that matter are checked.
///
/// These are the things that are hardest to judge by eye in flight: that a canopy fills
/// in air and stays limp in vacuum, that it leans the right way in a crosswind, that a
/// reefed canopy really is held small, that fabric lands on the ground and floats on
/// water instead of passing through, and that nothing blows up at Mach 2.
/// </summary>
internal static class Tests
{
    private static int failures;
    private static int checks;

    private static readonly Vec3 Down = new Vec3(0f, -1f, 0f);
    private static readonly Vec3 Up = new Vec3(0f, 1f, 0f);

    private static int Main()
    {
        SteadyDescentInflatesAboveTheCraft();
        DeploysFromThePack();
        StaysLimpInVacuum();
        InflatesInThinAir();
        LeansDownwindInACrosswind();
        ReefingHoldsTheCanopySmall();
        CollapsesOntoTheGround();
        FloatsOnWaves();
        FlowsAroundAnObstacle();
        FliesOnAfterBeingCut();
        SurvivesSupersonicFlight();
        SurvivesAHardOpening();
        IgnoresFrameJumps();
        EmbeddingFollowsTheCanopy();
        IsCheapEnough();

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("PASS - {0} checks", checks);
            return 0;
        }
        Console.WriteLine("FAIL - {0} of {1} checks failed", failures, checks);
        return 1;
    }

    // -------------------------------------------------------------------------------
    // Scenarios
    // -------------------------------------------------------------------------------

    private static CanopySim NewSim(float radius, int seed)
    {
        var shape = CanopyShape.Synthetic(radius, 0.75f, 2.4f);
        var lattice = CanopyLattice.Build(shape, LatticeResolution.Medium);
        return new CanopySim(lattice, new SimParameters(), seed);
    }

    private static SimEnvironment Kerbin(Vec3 craftVelocity)
    {
        return new SimEnvironment
        {
            FrameVelocity = craftVelocity,
            AirVelocity = Vec3.Zero,
            Gravity = new Vec3(0f, -9.81f, 0f),
            Density = 1.225f,
            SpeedOfSound = 340f,
            Turbulence = 0.3f
        };
    }

    private static void Run(CanopySim sim, SimEnvironment env, CollisionWorld world, float seconds)
    {
        const float dt = 0.02f;
        var steps = (int)(seconds / dt);
        for (var i = 0; i < steps; i++) sim.Step(dt, env, world);
    }

    private static void SteadyDescentInflatesAboveTheCraft()
    {
        Begin("steady descent: inflates, crown above the craft");
        var sim = NewSim(5f, 1);
        sim.InitialiseOpen(Up);
        Run(sim, Kerbin(Down * 7f), null, 12f);
        Check(sim.Fill > 0.8f, "fill " + sim.Fill.ToString("0.00"));
        Check(Vec3.Dot(sim.CanopyAxis, Up) > 0.95f, "axis " + sim.CanopyAxis);
        Check(sim.BubbleCentre.Y > 8f, "canopy centre height " + sim.BubbleCentre.Y.ToString("0.0"));
        Check(sim.MouthOpening > 0.6f, "mouth " + sim.MouthOpening.ToString("0.00"));
        Check(sim.Recoveries == 0, "no numerical recoveries");
    }

    private static void DeploysFromThePack()
    {
        Begin("deployment: pack to streamer to open canopy");
        var sim = NewSim(5f, 2);
        sim.BeginDeploy(Up);
        var env = Kerbin(Down * 60f);
        var t = 0f;
        var flyingAt = -1f;
        while (t < 10f)
        {
            sim.Step(0.02f, env, null);
            t += 0.02f;
            if (flyingAt < 0f && sim.Phase == CanopyPhase.Flying) flyingAt = t;
        }
        Check(flyingAt > 0f && flyingAt < 3f, "fully out of the pack at t=" + flyingAt.ToString("0.00") + "s");
        Check(sim.Fill > 0.7f, "fill after 10 s " + sim.Fill.ToString("0.00"));
        Check(sim.Alignment > 0.9f, "facing the flow " + sim.Alignment.ToString("0.00"));
        Check(sim.Recoveries == 0, "no numerical recoveries");
    }

    private static void StaysLimpInVacuum()
    {
        Begin("vacuum: pack leaves, canopy never fills");
        var sim = NewSim(5f, 3);
        sim.BeginDeploy(Up);
        var env = new SimEnvironment { FrameVelocity = new Vec3(2200f, 0f, 0f), Gravity = Vec3.Zero, Density = 0f };
        var maxFill = 0f;
        for (var i = 0; i < 500; i++)
        {
            sim.Step(0.02f, env, null);
            maxFill = Math.Max(maxFill, sim.Fill);
        }
        Check(maxFill == 0f, "max fill " + maxFill);
        Check(sim.BubbleCentre.Length > 2f, "fabric has drifted out of the part " + sim.BubbleCentre.Length.ToString("0.0") + " m");
        Check(Finite(sim), "all particles finite");
    }

    private static void InflatesInThinAir()
    {
        Begin("thin air (Duna): still fills at speed, stable");
        var sim = NewSim(8f, 4);
        sim.BeginDeploy(Up);
        var env = new SimEnvironment
        {
            FrameVelocity = Down * 110f,
            Gravity = new Vec3(0f, -2.94f, 0f),
            Density = 0.018f,
            SpeedOfSound = 220f,
            Turbulence = 0.2f
        };
        Run(sim, env, null, 12f);
        Check(sim.Fill > 0.5f, "fill " + sim.Fill.ToString("0.00"));
        Check(sim.Recoveries == 0, "no numerical recoveries");
    }

    private static void LeansDownwindInACrosswind()
    {
        Begin("crosswind: canopy trails downwind");
        var sim = NewSim(5f, 5);
        sim.InitialiseOpen(Up);
        var env = Kerbin(Down * 7f);
        env.AirVelocity = new Vec3(10f, 0f, 0f);
        Run(sim, env, null, 15f);
        // Relative wind is (10, 7): the canopy should sit about 55 degrees off vertical,
        // on the +x side.
        var angle = Math.Atan2(sim.CanopyAxis.X, sim.CanopyAxis.Y) * 180.0 / Math.PI;
        Check(angle > 35.0 && angle < 75.0, "lean " + angle.ToString("0") + " deg downwind");
        Check(sim.Fill > 0.6f, "still inflated " + sim.Fill.ToString("0.00"));
    }

    private static void ReefingHoldsTheCanopySmall()
    {
        Begin("reefing: hem held small, then released");
        var sim = NewSim(5f, 6);
        sim.SetReefTarget(0.15f);
        sim.InitialiseOpen(Up);
        var env = Kerbin(Down * 30f);
        Run(sim, env, null, 5f);
        var reefedMouth = sim.MouthOpening;
        var reefedFill = sim.Fill;
        Check(reefedMouth < 0.08f, "reefed mouth " + reefedMouth.ToString("0.000"));
        sim.SetReefTarget(1f);
        Run(sim, env, null, 6f);
        Check(sim.MouthOpening > 0.5f, "disreefed mouth " + sim.MouthOpening.ToString("0.00"));
        Check(sim.Fill > reefedFill, "fills further once disreefed (" + reefedFill.ToString("0.00") + " -> " + sim.Fill.ToString("0.00") + ")");
    }

    private static void CollapsesOntoTheGround()
    {
        Begin("landed: canopy collapses onto the ground");
        var sim = NewSim(5f, 7);
        sim.InitialiseOpen(Up);
        var world = new CollisionWorld { Ground = HeightField.Flat(new Vec3(0f, -1f, 0f), Up) };
        Run(sim, Kerbin(Vec3.Zero), world, 25f);
        var lowest = float.MaxValue;
        var highest = float.MinValue;
        for (var i = 0; i < sim.ParticleCount; i++)
        {
            if (i == sim.Lattice.PilotIndex) continue;
            lowest = Math.Min(lowest, sim.Positions[i].Y);
            highest = Math.Max(highest, sim.Positions[i].Y);
        }
        Check(lowest > -1.2f, "nothing through the ground (lowest " + lowest.ToString("0.00") + ")");
        Check(highest < 2.5f, "fabric lies down (highest " + highest.ToString("0.00") + ")");
        Check(sim.Fill < 0.1f, "deflated " + sim.Fill.ToString("0.00"));
    }

    private static void FloatsOnWaves()
    {
        Begin("splashdown: fabric rides the waves");
        var sim = NewSim(5f, 8);
        sim.InitialiseOpen(Up);
        var water = new HeightField(9);
        water.Origin = new Vec3(0f, -1f, 0f);
        water.Up = Up;
        water.East = Vec3.UnitX;
        water.North = Vec3.UnitZ;
        water.Cell = 5f;
        water.IsWater = true;
        var world = new CollisionWorld { Water = water };
        var env = Kerbin(Vec3.Zero);
        var t = 0f;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        for (var step = 0; step < 1500; step++)
        {
            t += 0.02f;
            for (var j = 0; j < water.Size; j++)
                for (var i = 0; i < water.Size; i++)
                    water.Heights[j * water.Size + i] = 0.6f * (float)Math.Sin(0.3 * i * water.Cell + 1.2 * t);
            sim.Step(0.02f, env, world);
            if (step < 1000) continue;
            for (var i = 0; i < sim.Lattice.DomeCount; i++)
            {
                minY = Math.Min(minY, sim.Positions[i].Y);
                maxY = Math.Max(maxY, sim.Positions[i].Y);
            }
        }
        Check(minY > -3.0f, "fabric does not sink (lowest " + minY.ToString("0.00") + ")");
        Check(maxY < 1.5f, "fabric lies on the water (highest " + maxY.ToString("0.00") + ")");
    }

    private static void FlowsAroundAnObstacle()
    {
        Begin("obstacle: fabric stays outside a sphere in its way");
        var sim = NewSim(5f, 9);
        sim.InitialiseOpen(Up);
        var world = new CollisionWorld();
        var centre = new Vec3(0f, 14f, 0f);
        world.Spheres.Add(new CollisionSphere { Centre = centre, Radius = 2.5f });
        Run(sim, Kerbin(Down * 7f), world, 8f);
        var inside = 0;
        for (var i = 0; i < sim.Lattice.DomeCount; i++)
            if (Vec3.Distance(sim.Positions[i], centre) < 2.5f) inside++;
        Check(inside == 0, inside + " particles inside the obstacle");
        Check(Finite(sim), "all particles finite");
    }

    private static void FliesOnAfterBeingCut()
    {
        Begin("cut: canopy flies away on its own");
        var sim = NewSim(5f, 10);
        sim.InitialiseOpen(Up);
        var env = Kerbin(Down * 7f);
        Run(sim, env, null, 5f);
        sim.Detach();
        // The craft keeps falling at 7 m/s; the released canopy slows to its own, much
        // lower terminal speed, so it drifts above the frame.
        Run(sim, env, null, 5f);
        var conf = sim.Positions[sim.Lattice.ConfluenceIndex];
        Check(conf.Y > 5f, "confluence has left the anchor (" + conf.Y.ToString("0.0") + " m)");
        Check(Finite(sim), "all particles finite");
    }

    private static void SurvivesSupersonicFlight()
    {
        Begin("supersonic: breathing canopy stays stable");
        var sim = NewSim(10f, 11);
        sim.InitialiseOpen(Up);
        var env = new SimEnvironment
        {
            FrameVelocity = Down * 420f,
            Gravity = new Vec3(0f, -3.7f, 0f),
            Density = 0.01f,
            SpeedOfSound = 230f,
            Turbulence = 0.3f
        };
        Run(sim, env, null, 6f);
        Check(sim.Mach > 1.5f, "Mach " + sim.Mach.ToString("0.0"));
        Check(sim.Recoveries == 0, "no numerical recoveries");
        Check(sim.Fill > 0.4f, "inflated " + sim.Fill.ToString("0.00"));
    }

    private static void SurvivesAHardOpening()
    {
        Begin("hard opening: 250 m/s at sea level");
        var sim = NewSim(5f, 12);
        sim.BeginDeploy(Up);
        Run(sim, Kerbin(Down * 250f), null, 4f);
        Check(sim.Recoveries == 0, "no numerical recoveries");
        Check(Finite(sim), "all particles finite");
    }

    private static void IgnoresFrameJumps()
    {
        Begin("frame: a sudden anchor velocity change drags the canopy, not teleports it");
        var sim = NewSim(5f, 13);
        sim.InitialiseOpen(Up);
        Run(sim, Kerbin(Down * 7f), null, 5f);
        var before = sim.BubbleCentre;
        // The craft is suddenly moving 5 m/s faster downward (a staging kick).
        sim.Step(0.02f, Kerbin(Down * 12f), null);
        var after = sim.BubbleCentre;
        Check(Vec3.Distance(before, after) < 0.5f, "moved " + Vec3.Distance(before, after).ToString("0.00") + " m in one frame");
    }

    private static void EmbeddingFollowsTheCanopy()
    {
        Begin("embedding: mesh vertices follow the simulated canopy");
        var shape = CanopyShape.Synthetic(5f, 0.75f, 2.4f);
        var lattice = CanopyLattice.Build(shape, LatticeResolution.Medium);
        // A fake model: points scattered on the dome and along the lines.
        var rng = new Random(7);
        var verts = new Vec3[400];
        for (var i = 0; i < verts.Length; i++)
        {
            var th = (float)(rng.NextDouble() * Math.PI * 2);
            if (i < 300)
            {
                float rho, h;
                shape.ProfileAt((float)rng.NextDouble(), out rho, out h);
                verts[i] = new Vec3(rho * (float)Math.Sin(th), h, rho * (float)Math.Cos(th));
            }
            else
            {
                var t = (float)rng.NextDouble();
                verts[i] = Vec3.Lerp(Vec3.Zero, shape.HemPoint(th), t);
            }
        }
        var emb = CanopyEmbedding.Compute(lattice, verts, null, null);
        var sim = new CanopySim(lattice, new SimParameters(), 14);
        sim.InitialiseOpen(Up);
        var env = Kerbin(Down * 7f);
        env.AirVelocity = new Vec3(6f, 0f, 0f);
        Run(sim, env, null, 6f);
        var outp = new Vec3[verts.Length];
        emb.Evaluate(sim.Positions, outp, null, null);
        // Every vertex should sit near the simulated surface: within a lattice cell of
        // some particle.
        var cell = MathX.TwoPi * 5f / lattice.Gores * 1.5f;
        var far = 0;
        for (var i = 0; i < outp.Length; i++)
        {
            if (!outp[i].IsFinite) { far++; continue; }
            var best = float.MaxValue;
            for (var p = 0; p < lattice.ConfluenceIndex + 1; p++)
                best = Math.Min(best, Vec3.Distance(outp[i], sim.Positions[p]));
            if (best > cell) far++;
        }
        Check(emb.DomeCount == 300 && emb.LineCount == 100, "classified " + emb.DomeCount + " fabric / " + emb.LineCount + " line vertices");
        Check(far == 0, far + " vertices strayed from the canopy");
    }

    private static void IsCheapEnough()
    {
        Begin("cost: one canopy's physics frame, per lattice quality");
        var qualities = new[] { LatticeResolution.Low, LatticeResolution.Medium, LatticeResolution.High };
        var names = new[] { "low", "medium", "high" };
        var budgets = new[] { 0.6, 1.2, 2.5 }; // ms per 20 ms physics frame, on a slow machine
        for (var q = 0; q < qualities.Length; q++)
        {
            var shape = CanopyShape.Synthetic(8f, 0.75f, 2.4f);
            var sim = new CanopySim(CanopyLattice.Build(shape, qualities[q]), new SimParameters(), 15);
            sim.InitialiseOpen(Up);
            var env = Kerbin(Down * 8f);
            env.AirVelocity = new Vec3(5f, 0f, 0f);
            Run(sim, env, null, 1f); // warm up the JIT
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int frames = 250;
            for (var i = 0; i < frames; i++) sim.Step(0.02f, env, null);
            var ms = sw.Elapsed.TotalMilliseconds / frames;
            CheckTiming(ms < budgets[q], names[q] + ": " + ms.ToString("0.000") + " ms per frame (" + sim.ParticleCount + " particles)");
        }

        // Moving a big model's vertices every rendered frame: ReStock's Mk16-XL is ~9400.
        var bigShape = CanopyShape.Synthetic(8f, 0.75f, 2.4f);
        var bigLattice = CanopyLattice.Build(bigShape, LatticeResolution.Medium);
        var rng = new Random(3);
        var verts = new Vec3[10000];
        var nrm = new Vec3[verts.Length];
        var tan = new float[verts.Length * 4];
        for (var i = 0; i < verts.Length; i++)
        {
            float rho, h;
            bigShape.ProfileAt((float)rng.NextDouble(), out rho, out h);
            var th = (float)(rng.NextDouble() * Math.PI * 2);
            verts[i] = new Vec3(rho * (float)Math.Sin(th), h + 0.05f, rho * (float)Math.Cos(th));
            nrm[i] = Up;
            tan[i * 4] = 1f; tan[i * 4 + 3] = 1f;
        }
        var emb = CanopyEmbedding.Compute(bigLattice, verts, nrm, tan);
        var bigSim = new CanopySim(bigLattice, new SimParameters(), 16);
        bigSim.InitialiseOpen(Up);
        Run(bigSim, Kerbin(Down * 8f), null, 1f);
        var outP = new Vec3[verts.Length];
        var outN = new Vec3[verts.Length];
        var outT = new float[verts.Length * 4];
        emb.Evaluate(bigSim.Positions, outP, outN, outT);
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) emb.Evaluate(bigSim.Positions, outP, outN, outT);
        var ems = sw2.Elapsed.TotalMilliseconds / 100;
        CheckTiming(ems < 3.0, "10000-vertex canopy mesh: " + ems.ToString("0.00") + " ms per rendered frame");
    }

    // -------------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------------

    private static bool Finite(CanopySim sim)
    {
        for (var i = 0; i < sim.ParticleCount; i++)
            if (!sim.Positions[i].IsFinite) return false;
        return true;
    }

    private static void Begin(string name)
    {
        Console.WriteLine();
        Console.WriteLine(name);
    }

    private static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine("  {0} {1}", ok ? "ok  " : "FAIL", what);
    }

    /// <summary>
    /// A timing check. Shared CI runners are too variable to fail a build on, so there it
    /// only reports; on a developer's machine a slowdown is a real regression.
    /// </summary>
    private static void CheckTiming(bool ok, string what)
    {
        if (Environment.GetEnvironmentVariable("CI") != null)
        {
            Console.WriteLine("  {0} {1}", ok ? "ok  " : "slow", what + " (timing, not enforced on CI)");
            return;
        }
        Check(ok, what);
    }
}
