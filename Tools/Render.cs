using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using ParaSoft.Core;

/// <summary>
/// Renders the offline solver's canopies to PNG: a deployment sequence, a crosswind,
/// reefing, a landing and a splashdown, plus - if KSP is installed - the stock Mk16's own
/// canopy mesh deformed by the simulation. These are the pictures in the README, and a
/// quick way to see whether a solver change still produces believable shapes.
///
/// Usage: Render.exe &lt;output dir&gt; [KSP dir]
/// </summary>
internal static class Render
{
    private static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
    private static readonly Vec3 Down = new Vec3(0f, -1f, 0f);

    private static int Main(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : "docs/images";
        Directory.CreateDirectory(dir);
        DeploySequence(Path.Combine(dir, "deploy-sequence.png"));
        Conditions(Path.Combine(dir, "conditions.png"));
        if (args.Length > 1) ModelMesh(args[1], Path.Combine(dir, "stock-mk16-mesh.png"));
        Console.WriteLine("Rendered to " + dir);
        return 0;
    }

    private static SimEnvironment Kerbin(Vec3 craft)
    {
        return new SimEnvironment
        {
            FrameVelocity = craft,
            Gravity = new Vec3(0f, -9.81f, 0f),
            Density = 1.225f,
            SpeedOfSound = 340f,
            Turbulence = 0.4f
        };
    }

    private static CanopySim NewSim(int seed)
    {
        var shape = CanopyShape.Synthetic(5f, 0.75f, 2.4f);
        return new CanopySim(CanopyLattice.Build(shape, LatticeResolution.High), new SimParameters(), seed);
    }

    private static void Run(CanopySim sim, SimEnvironment env, CollisionWorld world, float seconds)
    {
        for (var t = 0f; t < seconds; t += 0.02f) sim.Step(0.02f, env, world);
    }

    private static void DeploySequence(string path)
    {
        var times = new[] { 0.12f, 0.3f, 0.6f, 1.0f, 1.6f, 4f };
        var sim = NewSim(1);
        sim.BeginDeploy(Up);
        var env = Kerbin(Down * 55f);
        var frames = new List<Vec3[]>();
        var labels = new List<string>();
        var t = 0f;
        foreach (var target in times)
        {
            while (t < target) { sim.Step(0.02f, env, null); t += 0.02f; }
            frames.Add((Vec3[])sim.Positions.Clone());
            labels.Add(t.ToString("0.0") + " s  " + (sim.Phase == CanopyPhase.Extracting ? "extracting" : (sim.Fill * 100).ToString("0") + "% full"));
        }
        DrawStrip(path, sim.Lattice, frames, labels, "Deployment at 55 m/s, sea level: pack, lines, streamer, inflation", null);
    }

    private static void Conditions(string path)
    {
        var frames = new List<Vec3[]>();
        var labels = new List<string>();
        var grounds = new List<float?>();
        CanopyLattice lattice = null;

        var a = NewSim(2);
        a.InitialiseOpen(Up);
        Run(a, Kerbin(Down * 7f), null, 12f);
        frames.Add((Vec3[])a.Positions.Clone()); labels.Add("steady descent"); grounds.Add(null);
        lattice = a.Lattice;

        var b = NewSim(3);
        b.InitialiseOpen(Up);
        var wind = Kerbin(Down * 7f);
        wind.AirVelocity = new Vec3(9f, 0f, 0f);
        Run(b, wind, null, 15f);
        frames.Add((Vec3[])b.Positions.Clone()); labels.Add("9 m/s crosswind"); grounds.Add(null);

        var c = NewSim(4);
        c.SetReefTarget(0.12f);
        c.InitialiseOpen(Up);
        Run(c, Kerbin(Down * 35f), null, 5f);
        frames.Add((Vec3[])c.Positions.Clone()); labels.Add("reefed"); grounds.Add(null);

        var d = NewSim(5);
        d.InitialiseOpen(Up);
        var duna = new SimEnvironment { FrameVelocity = Down * 90f, Gravity = new Vec3(0f, -2.94f, 0f), Density = 0.018f, SpeedOfSound = 220f, Turbulence = 0.3f };
        Run(d, duna, null, 10f);
        frames.Add((Vec3[])d.Positions.Clone()); labels.Add("Duna, 90 m/s"); grounds.Add(null);

        var e = NewSim(6);
        e.InitialiseOpen(Up);
        var ground = new CollisionWorld { Ground = HeightField.Flat(new Vec3(0f, -1f, 0f), Up) };
        var landedWind = Kerbin(Vec3.Zero);
        landedWind.AirVelocity = new Vec3(3f, 0f, 0f);
        Run(e, landedWind, ground, 20f);
        frames.Add((Vec3[])e.Positions.Clone()); labels.Add("landed, light breeze"); grounds.Add(-1f);

        var f = NewSim(7);
        f.BeginDeploy(Up);
        Run(f, new SimEnvironment { FrameVelocity = new Vec3(2200f, 0f, 0f), Density = 0f }, null, 2.2f);
        frames.Add((Vec3[])f.Positions.Clone()); labels.Add("vacuum"); grounds.Add(null);

        DrawStrip(path, lattice, frames, labels, "The same canopy in different conditions", grounds);
    }

    /// <summary>The stock Mk16's own canopy mesh, deformed by a simulated crosswind.</summary>
    private static void ModelMesh(string ksp, string path)
    {
        var file = Path.Combine(ksp, @"GameData\Squad\Parts\Utility\parachuteMk1\model.mu");
        if (!File.Exists(file)) return;
        var model = MuModel.Load(file);
        var canopy = model.Find("canopy");
        model.ApplyClipEnd("semiDeploySmall");
        model.ApplyClipEnd("fullyDeploySmall");
        int[] owners;
        var verts = ModelReport.BakeCanopy(model, canopy, out owners);
        var fit = CanopyFitter.Fit(verts, 0);
        var lattice = CanopyLattice.Build(fit.Canopies[0], LatticeResolution.Medium);
        var emb = CanopyEmbedding.Compute(lattice, verts, null, null);
        MuModel.Node node = null;
        foreach (var nd in model.AllNodes()) if (nd.Mesh != null && MuModel.IsUnder(nd, canopy)) node = nd;
        var tris = new List<int>();
        foreach (var s in node.Mesh.Submeshes) tris.AddRange(s);

        var sim = new CanopySim(lattice, new SimParameters(), 9);
        var rest = new Vec3[verts.Length];
        emb.Evaluate(lattice.RestPositions, rest, null, null);
        sim.InitialiseOpen(Up);
        var env = Kerbin(Down * 7f);
        env.AirVelocity = new Vec3(6f, 0f, 0f);
        Run(sim, env, null, 10f);
        var deformed = new Vec3[verts.Length];
        emb.Evaluate(sim.Positions, deformed, null, null);

        // Rest pose is in the canopy frame (axis +Z for this model); stand it up for the picture.
        var q = Quat.FromTo(fit.Canopies[0].Axis, Up);
        for (var i = 0; i < rest.Length; i++) rest[i] = q * rest[i];

        using (var bmp = new Bitmap(1100, 620))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(236, 242, 248));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawMesh(g, rest, tris, new RectangleF(20, 50, 520, 540));
            DrawMesh(g, deformed, tris, new RectangleF(560, 50, 520, 540));
            using (var font = new Font("Segoe UI", 13f))
            {
                g.DrawString("Stock Mk16 canopy mesh: as modelled", font, Brushes.Black, 30, 14);
                g.DrawString("the same mesh, simulated in a 6 m/s crosswind", font, Brushes.Black, 570, 14);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // -------------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------------

    private const float Yaw = 0.55f, Pitch = 0.22f;

    private static PointF Project(Vec3 p, out float depth)
    {
        var cy = (float)Math.Cos(Yaw);
        var sy = (float)Math.Sin(Yaw);
        var x = p.X * cy - p.Z * sy;
        var z = p.X * sy + p.Z * cy;
        var cp = (float)Math.Cos(Pitch);
        var sp = (float)Math.Sin(Pitch);
        var y = p.Y * cp - z * sp;
        depth = p.Y * sp + z * cp;
        return new PointF(x, y);
    }

    private static void DrawStrip(string path, CanopyLattice lattice, List<Vec3[]> frames, List<string> labels, string title, List<float?> grounds)
    {
        const int cellW = 300, cellH = 420;
        using (var bmp = new Bitmap(cellW * frames.Count, cellH + 50))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(236, 242, 248));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var font = new Font("Segoe UI", 13f))
            using (var small = new Font("Segoe UI", 11f))
            {
                g.DrawString(title, font, Brushes.Black, 10, 8);
                for (var f = 0; f < frames.Count; f++)
                {
                    var rect = new RectangleF(f * cellW + 10, 45, cellW - 20, cellH - 40);
                    float? ground = grounds != null ? grounds[f] : null;
                    DrawCanopy(g, lattice, frames[f], rect, ground);
                    g.DrawString(labels[f], small, Brushes.Black, f * cellW + 14, cellH + 14);
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    private static void DrawCanopy(Graphics g, CanopyLattice lattice, Vec3[] pos, RectangleF rect, float? ground)
    {
        // Fixed scale for every panel so they compare: the canopy's design size.
        var extent = lattice.Shape.LineLength + lattice.Shape.MeridianLength * 0.6f + 4f;
        var scale = Math.Min(rect.Width, rect.Height) / (extent * 1.25f);
        var cx = rect.X + rect.Width * 0.5f;
        var baseY = rect.Bottom - 25f;
        float dummy;
        Func<Vec3, PointF> map = p =>
        {
            var q = Project(p, out dummy);
            return new PointF(cx + q.X * scale, baseY - q.Y * scale - 20f);
        };

        if (ground.HasValue)
        {
            var gy = map(new Vec3(0f, ground.Value, 0f)).Y;
            using (var b = new SolidBrush(Color.FromArgb(150, 170, 120)))
                g.FillRectangle(b, rect.X, gy, rect.Width, rect.Bottom - gy);
        }

        // Anchor: a small capsule stand-in for the craft.
        var a = map(Vec3.Zero);
        g.FillEllipse(Brushes.DimGray, a.X - 6, a.Y - 3, 12, 14);

        // Lines.
        using (var pen = new Pen(Color.FromArgb(160, 60, 60, 60), 1f))
        {
            for (var gi = 0; gi < lattice.Gores; gi++)
                for (var j = 0; j < lattice.LineSegments; j++)
                    g.DrawLine(pen, map(pos[lattice.LineParticle(gi, j)]), map(pos[lattice.LineParticle(gi, j + 1)]));
        }

        // Fabric, back to front, alternating gore colours like a stock canopy.
        var tris = lattice.Triangles;
        var order = new List<KeyValuePair<float, int>>();
        for (var t = 0; t < tris.Length; t += 3)
        {
            float d0, d1, d2;
            Project(pos[tris[t]], out d0);
            Project(pos[tris[t + 1]], out d1);
            Project(pos[tris[t + 2]], out d2);
            order.Add(new KeyValuePair<float, int>(-(d0 + d1 + d2), t));
        }
        order.Sort((x, y) => x.Key.CompareTo(y.Key));
        var light = new Vec3(-0.4f, 0.8f, 0.45f).Normalized;
        foreach (var kv in order)
        {
            var t = kv.Value;
            var p0 = pos[tris[t]];
            var p1 = pos[tris[t + 1]];
            var p2 = pos[tris[t + 2]];
            var n = Vec3.Cross(p1 - p0, p2 - p0).NormalizedOr(Up);
            var shade = 0.35f + 0.65f * Math.Abs(Vec3.Dot(n, light));
            var gore = (tris[t] % lattice.Gores) / 2 % 2;
            var baseColor = gore == 0 ? Color.FromArgb(240, 120, 40) : Color.FromArgb(245, 245, 240);
            var col = Color.FromArgb(255, (int)(baseColor.R * shade), (int)(baseColor.G * shade), (int)(baseColor.B * shade));
            using (var b = new SolidBrush(col))
                g.FillPolygon(b, new[] { map(p0), map(p1), map(p2) });
        }
    }

    private static void DrawMesh(Graphics g, Vec3[] v, List<int> tris, RectangleF rect)
    {
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        float dd;
        foreach (var p in v)
        {
            var q = Project(p, out dd);
            min = new Vec3(Math.Min(min.X, q.X), Math.Min(min.Y, q.Y), 0f);
            max = new Vec3(Math.Max(max.X, q.X), Math.Max(max.Y, q.Y), 0f);
        }
        var scale = Math.Min(rect.Width / (max.X - min.X), rect.Height / (max.Y - min.Y)) * 0.9f;
        var ox = rect.X + rect.Width * 0.5f - (min.X + max.X) * 0.5f * scale;
        var oy = rect.Y + rect.Height * 0.5f + (min.Y + max.Y) * 0.5f * scale;
        Func<Vec3, PointF> map = p =>
        {
            var q = Project(p, out dd);
            return new PointF(ox + q.X * scale, oy - q.Y * scale);
        };
        var order = new List<KeyValuePair<float, int>>();
        for (var t = 0; t < tris.Count; t += 3)
        {
            float d0, d1, d2;
            Project(v[tris[t]], out d0);
            Project(v[tris[t + 1]], out d1);
            Project(v[tris[t + 2]], out d2);
            order.Add(new KeyValuePair<float, int>(-(d0 + d1 + d2), t));
        }
        order.Sort((x, y) => x.Key.CompareTo(y.Key));
        var light = new Vec3(-0.4f, 0.8f, 0.45f).Normalized;
        using (var edge = new Pen(Color.FromArgb(60, 0, 0, 0), 0.5f))
        {
            foreach (var kv in order)
            {
                var t = kv.Value;
                var p0 = v[tris[t]];
                var p1 = v[tris[t + 1]];
                var p2 = v[tris[t + 2]];
                var n = Vec3.Cross(p1 - p0, p2 - p0).NormalizedOr(Up);
                var shade = 0.35f + 0.65f * Math.Abs(Vec3.Dot(n, light));
                var col = Color.FromArgb(255, (int)(235 * shade), (int)(235 * shade), (int)(228 * shade));
                var pts = new[] { map(p0), map(p1), map(p2) };
                using (var b = new SolidBrush(col)) g.FillPolygon(b, pts);
                g.DrawPolygon(edge, pts);
            }
        }
    }
}
