using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using ParaSoft.Core;

/// <summary>
/// Runs the canopy fitter over real parachute models and reports what it found.
///
/// For each model: load the .mu, play the semi- and then full-deploy clips to their last
/// frame, bake every mesh under the canopy transform (skinned ones through their bones),
/// express the vertices in the canopy frame, fit, build the simulation lattice, embed
/// every vertex and check that the embedding reproduces the model. A side-view PNG of
/// each fit is written so a bad fit is visible at a glance.
///
/// Usage: ModelReport.exe &lt;KSP dir&gt; &lt;output dir&gt; [label|file|canopyTransform|semiClip|fullClip|count ...]
/// </summary>
internal static class ModelReport
{
    private sealed class Spec
    {
        internal string Label, File, Canopy, Semi, Full;
        internal int Count;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: ModelReport <KSP dir> <output dir> [extra specs]");
            return 2;
        }
        var ksp = args[0];
        var outDir = args[1];
        Directory.CreateDirectory(outDir);
        var g = Path.Combine(ksp, "GameData");

        var specs = new List<Spec>
        {
            S("Stock Mk16", g, @"Squad\Parts\Utility\parachuteMk1\model.mu", "canopy", "semiDeploySmall", "fullyDeploySmall", 0),
            S("Stock Mk2-R", g, @"Squad\Parts\Utility\parachuteMk2-R\model.mu", "canopy", "semiDeployLarge", "fullyDeployLarge", 0),
            S("Stock Mk25", g, @"Squad\Parts\Utility\parachuteMk25\model.mu", "canopy", "semiDeployLarge", "fullyDeployLarge", 0),
            S("Stock Mk12-R", g, @"Squad\Parts\Utility\parachuteMk12-R\model.mu", "canopy", "semiDeployLarge", "fullyDeployLarge", 0),
            S("Stock Mk16-XL", g, @"Squad\Parts\Utility\parachuteMk16-XL\model.mu", "canopy", "semiDeployLarge", "fullyDeployLarge", 0),
            S("RealChute single", g, @"RealChute\Parts\model_RC_canopy.mu", "RC_canopy", "RC_chute_semi_deploy", "RC_chute_full_deploy", 1),
            S("RealChute single 2", g, @"RealChute\Parts\model_RC_canopy2.mu", "RC_canopy2", "RC_chute2_semi_deploy", "RC_chute2_full_deploy", 1),
            S("RealChute triple", g, @"RealChute\Parts\model_RC_triple_canopy.mu", "RC_triple_canopy", "RC_triple_chute_semi_deploy", "RC_triple_chute_full_deploy", 3),
            S("RealChute triple 2", g, @"RealChute\Parts\model_RC_triple_canopy2.mu", "RC_triple_canopy2", "RC_triple_chute2_semi_deploy", "RC_triple_chute2_full_deploy", 3),
            S("ReStock Mk16", g, @"ReStock\Assets\Utility\restock-parachute-0625-1.mu", "B_ParachuteRoot004", "semiDeployLarge", "fullyDeployLarge", 0),
            S("ReStock Mk16-XL", g, @"ReStock\Assets\Utility\restock-parachute-125-1.mu", "B_ParachuteLargeRotator", "semiDeployLarge", "fullyDeployLarge", 0),
            S("ReStock Mk25", g, @"ReStock\Assets\Utility\restock-parachute-drogue-125-1.mu", "B_ParachuteLargeDrogueRotator", "semiDeployLarge", "fullyDeployLarge", 0),
            S("ReStock Mk2-R", g, @"ReStock\Assets\Utility\restock-parachute-radial-1.mu", "B_ParachuteRoot", "semiDeployLarge", "fullyDeployLarge", 0),
            S("ReStock Mk12-R", g, @"ReStock\Assets\Utility\restock-parachute-drogue-radial-1.mu", "B_ParachuteRoot005", "semiDeployLarge", "fullyDeployLarge", 0),
            S("CPM Encoded", g, @"CustomParachuteMessage\Models\model_canopyWithMessage.mu", "parachute", "chute_semi_deploy", "chute_full_deploy", 1),
            // Lines gathered metres out on a riser, and modelled with vertices only at their ends.
            S("BCS Starliner main", g, @"BoringCrewServices\Parts\Starliner\BCS_Centauri_MainChute.mu", "canopy", "semi_deploy", "full_deploy", 0),
            S("BCS Starliner drogue", g, @"BoringCrewServices\Parts\Starliner\BCS_Centauri_DrogueChutes.mu", "canopy", "semiDeploy", "fullDeploy", 0),
        };
        for (var i = 2; i < args.Length; i++)
        {
            var p = args[i].Split('|');
            specs.Add(new Spec { Label = p[0], File = p[1], Canopy = p[2], Semi = p[3], Full = p[4], Count = int.Parse(p[5]) });
        }

        var failures = 0;
        foreach (var spec in specs)
        {
            if (!File.Exists(spec.File))
            {
                Console.WriteLine("{0,-20} SKIP (not installed)", spec.Label);
                continue;
            }
            try
            {
                if (!Report(spec, outDir)) failures++;
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine("{0,-20} ERROR {1}", spec.Label, e.Message);
            }
        }
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All models fitted." : failures + " model(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static Spec S(string label, string g, string rel, string canopy, string semi, string full, int count)
    {
        return new Spec { Label = label, File = Path.Combine(g, rel), Canopy = canopy, Semi = semi, Full = full, Count = count };
    }

    internal static Vec3[] BakeCanopy(MuModel model, MuModel.Node canopy, out int[] owners)
    {
        var toCanopy = canopy.UnscaledWorldMatrix.RigidInverse;
        var verts = new List<Vec3>();
        var own = new List<int>();
        var idx = 0;
        foreach (var n in model.AllNodes())
        {
            if (!MuModel.IsUnder(n, canopy)) continue;
            if (n.Mesh == null && n.SkinnedMesh == null) continue;
            if (n.Mesh != null && !n.HasMeshRenderer) continue;
            var world = model.BakeWorld(n);
            foreach (var w in world)
            {
                verts.Add(toCanopy.MultiplyPoint(w));
                own.Add(idx);
            }
            idx++;
        }
        owners = own.ToArray();
        return verts.ToArray();
    }

    private static bool Report(Spec spec, string outDir)
    {
        var model = MuModel.Load(spec.File);
        var canopy = model.Find(spec.Canopy);
        if (canopy == null)
        {
            Console.WriteLine("{0,-20} FAIL no transform '{1}'", spec.Label, spec.Canopy);
            return false;
        }
        model.ApplyClipEnd(spec.Semi);
        model.ApplyClipEnd(spec.Full);
        int[] owners;
        var verts = BakeCanopy(model, canopy, out owners);

        var fit = CanopyFitter.Fit(verts, spec.Count);
        if (!fit.Ok)
        {
            Console.WriteLine("{0,-20} FAIL {1} ({2} verts)", spec.Label, fit.Failure, verts.Length);
            return false;
        }

        var ok = true;
        var line = string.Format("{0,-20} {1} canopy(s), {2} verts", spec.Label, fit.Canopies.Count, verts.Length);
        Console.WriteLine(line);
        for (var c = 0; c < fit.Canopies.Count; c++)
        {
            var s = fit.Canopies[c];
            var lattice = CanopyLattice.Build(s, LatticeResolution.Medium);
            var members = new List<int>();
            for (var i = 0; i < verts.Length; i++) if (fit.VertexCanopy[i] == c) members.Add(i);
            var mv = members.Select(i => verts[i]).ToArray();
            var emb = CanopyEmbedding.Compute(lattice, mv, null, null);

            // Reproduction at rest must be exact, and a rigid motion of the lattice must
            // move every vertex rigidly with it.
            var rest = new Vec3[mv.Length];
            emb.Evaluate(lattice.RestPositions, Vec3.Zero, s.RiserJunction, rest, null, null);
            var maxRest = 0f;
            for (var i = 0; i < mv.Length; i++) maxRest = Math.Max(maxRest, Vec3.Distance(rest[i], mv[i]));

            var q = Quat.AngleAxis(0.7f, new Vec3(0.3f, 0.8f, 0.52f).Normalized);
            var shift = new Vec3(3f, -2f, 5f);
            var moved = new Vec3[lattice.ParticleCount];
            for (var i = 0; i < moved.Length; i++) moved[i] = q * lattice.RestPositions[i] + shift;
            var outp = new Vec3[mv.Length];
            // The riser's lower end is the anchor, which moves with everything else here.
            emb.Evaluate(moved, shift, q * s.RiserJunction + shift, outp, null, null);
            var maxRigid = 0f;
            for (var i = 0; i < mv.Length; i++) maxRigid = Math.Max(maxRigid, Vec3.Distance(outp[i], q * mv[i] + shift));

            var scale = s.MaxRadius;
            // Lines are a few centimetres thick. A line vertex far from the line it follows
            // means the lattice's lines are not where the model's are, and that vertex will be
            // flung about as they move. The riser also carries fittings near the part (ReStock's
            // cluster joins its risers half a metre off them), so it is allowed a little more.
            var lineLimit = Math.Max(0.35f, 0.04f * scale);
            var riserLimit = Math.Max(0.6f, 0.15f * scale);
            var good = maxRest < 1e-3f * scale + 1e-4f && maxRigid < 2e-3f * scale + 1e-4f
                       && emb.MaxLineOffset < lineLimit && emb.MaxRiserOffset < riserLimit;
            if (!good) ok = false;
            Console.WriteLine("    #{0}: R={1:0.###} hem R={2:0.###} h={3:0.###} lines={4:0.###} riser={5:0.##}{18} D0={6:0.###} sym={7:0.00} rms={8:0.000} dome/line/riser verts={9}/{10}/{11} vent={12:0.##}R  rest err={13:0.0e0} rigid err={14:0.0e0} off line/riser={15:0.00}/{16:0.00} m {17}",
                c, s.MaxRadius, s.HemRadius, s.HemHeight, s.LineLength, s.RiserLength, s.NominalDiameter, s.Symmetry, s.RmsError,
                emb.DomeCount, emb.LineCount, emb.RiserCount, s.ProfileRho[0] / s.MaxRadius, maxRest / scale, maxRigid / scale,
                emb.MaxLineOffset, emb.MaxRiserOffset, good ? "ok" : "BAD",
                s.RiserJunction.Length > 0f ? " (joined at " + s.RiserJunction.Length.ToString("0.##") + ")" : "");
        }

        Plot(spec, fit, verts, Path.Combine(outDir, spec.Label.Replace(' ', '_') + ".png"));
        return ok;
    }

    /// <summary>Side view: every vertex as (distance from axis, height), with the fitted profile over it.</summary>
    private static void Plot(Spec spec, CanopyFitResult fit, Vec3[] verts, string path)
    {
        const int W = 900, H = 700, M = 40;
        using (var bmp = new Bitmap(W, H))
        using (var gr = Graphics.FromImage(bmp))
        {
            gr.Clear(Color.White);
            gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var colors = new[] { Color.SteelBlue, Color.DarkOrange, Color.SeaGreen, Color.Purple, Color.Brown };
            var maxRho = 0f;
            var maxH = 0f;
            var minH = 0f;
            for (var i = 0; i < verts.Length; i++)
            {
                var s = fit.Canopies[fit.VertexCanopy[i]];
                var h = Vec3.Dot(verts[i], s.Axis);
                var r = Vec3.Reject(verts[i], s.Axis).Length;
                maxRho = Math.Max(maxRho, r);
                maxH = Math.Max(maxH, h);
                minH = Math.Min(minH, h);
            }
            var scale = Math.Min((W - 2 * M) / (2f * maxRho), (H - 2 * M) / (maxH - minH));
            Func<float, float, PointF> map = (r, h) => new PointF(W / 2f + r * scale, H - M - (h - minH) * scale);

            for (var i = 0; i < verts.Length; i++)
            {
                var c = fit.VertexCanopy[i];
                var s = fit.Canopies[c];
                var h = Vec3.Dot(verts[i], s.Axis);
                var th = Math.Atan2(Vec3.Dot(verts[i], s.E2), Vec3.Dot(verts[i], s.E1));
                var r = Vec3.Reject(verts[i], s.Axis).Length * (Math.Cos(th) >= 0 ? 1f : -1f);
                var p = map(r, h);
                using (var b = new SolidBrush(Color.FromArgb(90, colors[c % colors.Length])))
                    gr.FillRectangle(b, p.X - 1, p.Y - 1, 2, 2);
            }
            for (var c = 0; c < fit.Canopies.Count; c++)
            {
                var s = fit.Canopies[c];
                using (var pen = new Pen(Color.Red, 2f))
                {
                    for (var side = -1; side <= 1; side += 2)
                    {
                        var pts = new List<PointF>();
                        for (var i = 0; i < s.ProfileRho.Length; i++) pts.Add(map(side * s.ProfileRho[i], s.ProfileH[i]));
                        gr.DrawLines(pen, pts.ToArray());
                        var hem = pts[pts.Count - 1];
                        var conf = map(0f, Vec3.Dot(s.Confluence, s.Axis));
                        gr.DrawLine(Pens.Red, hem, conf);
                    }
                }
            }
            gr.DrawString(spec.Label + "  (" + fit.Canopies.Count + " canopy, " + verts.Length + " verts)", SystemFonts.DefaultFont, Brushes.Black, 8, 8);
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
