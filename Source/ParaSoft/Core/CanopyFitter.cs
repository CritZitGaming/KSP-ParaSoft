using System;
using System.Collections.Generic;

namespace ParaSoft.Core
{
    /// <summary>
    /// The shape of one round canopy, recovered from a part's own model.
    ///
    /// Everything is expressed in the canopy transform's frame: position and rotation of
    /// the transform the parachute module hangs the canopy from, with its scale removed.
    /// That frame's origin is where the canopy is attached to the part. The lines meet
    /// there too, or further out along the axis with a riser between.
    /// </summary>
    public sealed class CanopyShape
    {
        /// <summary>Unit vector from the line confluence towards the crown.</summary>
        public Vec3 Axis;
        /// <summary>Two unit vectors completing a right-handed basis with Axis.</summary>
        public Vec3 E1, E2;
        /// <summary>Where the suspension lines meet: the origin, or the top of the riser.</summary>
        public Vec3 Confluence;

        /// <summary>Length of the riser from the part to the confluence; 0 if the lines meet at the part.</summary>
        public float RiserLength { get { return Confluence.Length; } }

        /// <summary>
        /// Where a cluster's risers join, if they share a strap from the part before
        /// splitting to their own confluences (ReStock's Mk16-XL, Boring Crew Services'
        /// swivel). Zero when each riser runs straight from the part.
        /// </summary>
        public Vec3 RiserJunction;
        /// <summary>Radius of the hem, where the lines attach.</summary>
        public float HemRadius;
        /// <summary>Distance of the hem plane from the frame origin along Axis.</summary>
        public float HemHeight;
        /// <summary>
        /// The meridian of the canopy - distance from the axis and height along it - at
        /// points evenly spaced by arc length from the vent edge (index 0) to the hem.
        /// </summary>
        public float[] ProfileRho, ProfileH;
        /// <summary>Largest sector radius over smallest, measured near the hem. 1 is perfectly round.</summary>
        public float Symmetry;
        /// <summary>RMS distance of dome vertices from the fitted profile, as a fraction of the hem radius.</summary>
        public float RmsError;
        /// <summary>Number of model vertices that belong to this canopy.</summary>
        public int Vertices;

        /// <summary>Largest radius anywhere on the profile - the inflated canopy's projected radius.</summary>
        public float MaxRadius
        {
            get
            {
                var r = 0f;
                for (var i = 0; i < ProfileRho.Length; i++) r = Math.Max(r, ProfileRho[i]);
                return r;
            }
        }

        /// <summary>Area the inflated canopy presents to the flow along its axis.</summary>
        public float ProjectedArea
        {
            get
            {
                var r = MaxRadius;
                var v = ProfileRho[0];
                return MathX.Pi * (r * r - v * v);
            }
        }

        /// <summary>Fabric area, from the surface of revolution of the profile.</summary>
        public float SurfaceArea
        {
            get
            {
                var a = 0f;
                for (var i = 0; i < ProfileRho.Length - 1; i++)
                {
                    var dr = ProfileRho[i + 1] - ProfileRho[i];
                    var dh = ProfileH[i + 1] - ProfileH[i];
                    var ds = MathX.Sqrt(dr * dr + dh * dh);
                    a += MathX.Pi * (ProfileRho[i] + ProfileRho[i + 1]) * ds;
                }
                return a;
            }
        }

        /// <summary>Length of the meridian from vent edge to hem.</summary>
        public float MeridianLength
        {
            get
            {
                var l = 0f;
                for (var i = 0; i < ProfileRho.Length - 1; i++)
                {
                    var dr = ProfileRho[i + 1] - ProfileRho[i];
                    var dh = ProfileH[i + 1] - ProfileH[i];
                    l += MathX.Sqrt(dr * dr + dh * dh);
                }
                return l;
            }
        }

        /// <summary>A point on the hem at the given angle around the axis.</summary>
        public Vec3 HemPoint(float theta)
        {
            var n = ProfileRho.Length - 1;
            return Axis * ProfileH[n] + (E1 * (float)Math.Cos(theta) + E2 * (float)Math.Sin(theta)) * ProfileRho[n];
        }

        /// <summary>Suspension line length, confluence to hem.</summary>
        public float LineLength
        {
            get { return Vec3.Distance(Confluence, HemPoint(0f)); }
        }

        /// <summary>A point on the profile at arc fraction s (0 = vent edge, 1 = hem), as (rho, h).</summary>
        public void ProfileAt(float s, out float rho, out float h)
        {
            var n = ProfileRho.Length - 1;
            var f = MathX.Clamp01(s) * n;
            var i = Math.Min((int)f, n - 1);
            var t = f - i;
            rho = MathX.Lerp(ProfileRho[i], ProfileRho[i + 1], t);
            h = MathX.Lerp(ProfileH[i], ProfileH[i + 1], t);
        }

        /// <summary>Nominal diameter: that of a flat circle with the same fabric area.</summary>
        public float NominalDiameter
        {
            get { return 2f * MathX.Sqrt(SurfaceArea / MathX.Pi); }
        }

        /// <summary>
        /// A canopy built from numbers rather than a model: a dome of the given radius and
        /// depth (depth = radius is a hemisphere), lines of lineRatio times the radius, axis
        /// along +Y. Used by the tests.
        /// </summary>
        public static CanopyShape Synthetic(float radius, float depthRatio, float lineRatio)
        {
            const int count = 33;
            var rho = new float[count];
            var h = new float[count];
            var depth = radius * depthRatio;
            var hemH = MathX.Sqrt(Math.Max(1e-4f, lineRatio * lineRatio * radius * radius - radius * radius));
            var vent = 0.06f;
            for (var i = 0; i < count; i++)
            {
                // Elliptical meridian from the vent edge (angle ~0) to the hem (angle pi/2).
                var a = MathX.Lerp((float)Math.Asin(vent), MathX.Pi * 0.5f, (float)i / (count - 1));
                rho[i] = radius * (float)Math.Sin(a);
                h[i] = hemH + depth * (float)Math.Cos(a);
            }
            return new CanopyShape
            {
                Axis = Vec3.UnitY,
                E1 = Vec3.UnitZ,
                E2 = Vec3.UnitX,
                Confluence = Vec3.Zero,
                HemRadius = radius,
                HemHeight = hemH,
                ProfileRho = rho,
                ProfileH = h,
                Symmetry = 1f,
                RmsError = 0f,
                Vertices = 0
            };
        }
    }

    public sealed class CanopyFitResult
    {
        public readonly List<CanopyShape> Canopies = new List<CanopyShape>();
        /// <summary>For every input vertex, the canopy it belongs to.</summary>
        public int[] VertexCanopy;
        /// <summary>
        /// True for a cluster whose canopies' lines all meet at one point (RealChute's triple
        /// chute): the solver then keeps their confluences together rather than letting each
        /// canopy take its own.
        /// </summary>
        public bool SharedConfluence;
        /// <summary>Null on success, otherwise why the model could not be read as round canopies.</summary>
        public string Failure;

        public bool Ok { get { return Failure == null && Canopies.Count > 0; } }
    }

    /// <summary>
    /// Recovers round canopies from a cloud of model vertices.
    ///
    /// Parachute models do not say which of their vertices are fabric and which are lines,
    /// where the hem is, or even how many canopies they contain - ReStock's Mk16-XL is three
    /// skinned canopies under one transform, RealChute's "Triple chute" is three in one mesh.
    /// So the fitter works from geometry alone: the fabric sits far out from the part, so the
    /// far vertices are fabric; k-means on those finds the canopies; each canopy's centroid
    /// gives its axis; a polar profile about a point just below the hem describes the dome
    /// whether it is flat, hemispherical or conical; and the cone of lines below the hem,
    /// extended to the axis, says where they meet. A cluster count is accepted only if every
    /// canopy it produces is round.
    /// </summary>
    public static class CanopyFitter
    {
        private const int ProfileSamples = 33;
        private const int PolarBins = 36;

        /// <param name="vertices">Model vertices in the canopy frame, in the fully deployed pose.</param>
        /// <param name="expectedCanopies">How many canopies the model holds, or 0 to work it out.</param>
        public static CanopyFitResult Fit(Vec3[] vertices, int expectedCanopies)
        {
            var result = new CanopyFitResult();
            if (vertices == null || vertices.Length < 24)
            {
                result.Failure = "too few vertices to describe a canopy";
                return result;
            }

            var n = vertices.Length;
            var dmax = 0f;
            for (var i = 0; i < n; i++) dmax = Math.Max(dmax, vertices[i].Length);
            if (dmax < 1e-4f)
            {
                result.Failure = "the canopy is collapsed to a point in its deployed pose";
                return result;
            }

            var far = new List<int>();
            for (var i = 0; i < n; i++)
                if (vertices[i].Length > 0.45f * dmax) far.Add(i);

            int[] candidates;
            if (expectedCanopies > 0) candidates = new[] { expectedCanopies };
            else candidates = new[] { 1, 2, 3, 4, 5, 6, 7 };

            // Every candidate count is tried. The first whose canopies are all convincingly
            // round wins - a real single canopy fits to about 1% of its radius and a few
            // percent of asymmetry, while a cluster forced into one canopy is lopsided by
            // 20% or more. Failing that, the least-bad loose fit is used.
            string lastFailure = "no cluster count produced round canopies";
            List<CanopyShape> bestShapes = null;
            int[] bestAssignment = null;
            var bestScore = float.MaxValue;
            foreach (var k in candidates)
            {
                if (far.Count < k * 12) break;
                var axes = ClusterAxes(vertices, far, k);
                var assignment = AssignToAxes(vertices, axes, dmax);

                var shapes = new List<CanopyShape>();
                var ok = true;
                var score = 0f;
                for (var c = 0; c < k && ok; c++)
                {
                    var members = new List<int>();
                    for (var i = 0; i < n; i++) if (assignment[i] == c) members.Add(i);
                    string why;
                    var shape = FitOne(vertices, members, axes[c], out why);
                    if (shape == null)
                    {
                        ok = false;
                        lastFailure = "with " + k + " canopies: " + why;
                        break;
                    }
                    shapes.Add(shape);
                    score = Math.Max(score, shape.RmsError + (shape.Symmetry - 1f));
                }
                if (!ok) continue;

                var strict = true;
                foreach (var s in shapes)
                    if (s.Symmetry > StrictSymmetry || s.RmsError > StrictRms) strict = false;
                if (strict)
                {
                    result.Canopies.AddRange(shapes);
                    result.VertexCanopy = assignment;
                    result.SharedConfluence = ShareConfluence(result.Canopies);
                    if (!result.SharedConfluence) FindJunction(vertices, assignment, result.Canopies);
                    return result;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestShapes = shapes;
                    bestAssignment = assignment;
                }
            }

            if (bestShapes != null)
            {
                result.Canopies.AddRange(bestShapes);
                result.VertexCanopy = bestAssignment;
                result.SharedConfluence = ShareConfluence(result.Canopies);
                if (!result.SharedConfluence) FindJunction(vertices, bestAssignment, result.Canopies);
                return result;
            }
            result.Failure = lastFailure;
            return result;
        }

        private const float StrictSymmetry = 1.12f;
        private const float StrictRms = 0.04f;

        // -------------------------------------------------------------------------------
        // Clustering
        // -------------------------------------------------------------------------------

        /// <summary>K-means on the far (fabric) vertices; returns each cluster's unit axis.</summary>
        private static Vec3[] ClusterAxes(Vec3[] v, List<int> far, int k)
        {
            var centers = new Vec3[k];
            // Farthest-point seeding: deterministic, and for well-separated canopies it
            // lands one seed in each immediately.
            centers[0] = Centroid(v, far);
            if (k > 1)
            {
                var best = far[0];
                var bestD = -1f;
                foreach (var i in far)
                {
                    var d = (v[i] - centers[0]).SqrLength;
                    if (d > bestD) { bestD = d; best = i; }
                }
                centers[0] = v[best];
                for (var c = 1; c < k; c++)
                {
                    bestD = -1f;
                    foreach (var i in far)
                    {
                        var dmin = float.MaxValue;
                        for (var j = 0; j < c; j++) dmin = Math.Min(dmin, (v[i] - centers[j]).SqrLength);
                        if (dmin > bestD) { bestD = dmin; best = i; }
                    }
                    centers[c] = v[best];
                }
            }

            var label = new int[far.Count];
            for (var iter = 0; iter < 12; iter++)
            {
                for (var f = 0; f < far.Count; f++)
                {
                    var p = v[far[f]];
                    var bestC = 0;
                    var bestD = float.MaxValue;
                    for (var c = 0; c < k; c++)
                    {
                        var d = (p - centers[c]).SqrLength;
                        if (d < bestD) { bestD = d; bestC = c; }
                    }
                    label[f] = bestC;
                }
                for (var c = 0; c < k; c++)
                {
                    var sum = Vec3.Zero;
                    var cnt = 0;
                    for (var f = 0; f < far.Count; f++)
                        if (label[f] == c) { sum += v[far[f]]; cnt++; }
                    if (cnt > 0) centers[c] = sum / cnt;
                }
            }

            var axes = new Vec3[k];
            for (var c = 0; c < k; c++) axes[c] = centers[c].NormalizedOr(Vec3.UnitZ);
            return axes;
        }

        /// <summary>Each vertex goes to the canopy whose axis it lies closest to in angle.</summary>
        private static int[] AssignToAxes(Vec3[] v, Vec3[] axes, float dmax)
        {
            var a = new int[v.Length];
            for (var i = 0; i < v.Length; i++)
            {
                var len = v[i].Length;
                if (axes.Length == 1 || len < 1e-4f * dmax) { a[i] = 0; continue; }
                var dir = v[i] / len;
                var best = 0;
                var bestDot = -2f;
                for (var c = 0; c < axes.Length; c++)
                {
                    var d = Vec3.Dot(dir, axes[c]);
                    if (d > bestDot) { bestDot = d; best = c; }
                }
                a[i] = best;
            }
            return a;
        }

        // -------------------------------------------------------------------------------
        // Single canopy
        // -------------------------------------------------------------------------------

        private static CanopyShape FitOne(Vec3[] v, List<int> members, Vec3 axisGuess, out string why)
        {
            why = null;
            if (members.Count < 12)
            {
                why = "a cluster with only " + members.Count + " vertices";
                return null;
            }

            var axis = axisGuess;
            float hemR = 0f, hemH = 0f, maxR = 0f;
            var dome = new List<int>();
            var lines = new List<int>();
            var hs = new List<float>();
            var rs = new List<float>();

            // The axis estimate and the dome/line split depend on each other, so iterate.
            for (var iter = 0; iter < 4; iter++)
            {
                hs.Clear();
                rs.Clear();
                var htop = float.MinValue;
                foreach (var i in members)
                {
                    var h = Vec3.Dot(v[i], axis);
                    htop = Math.Max(htop, h);
                }
                if (htop <= 0f)
                {
                    why = "the canopy lies behind its own attachment point";
                    return null;
                }

                // Radius of the fabric: the widest the upper half of the canopy gets.
                foreach (var i in members)
                {
                    var h = Vec3.Dot(v[i], axis);
                    if (h > 0.5f * htop) rs.Add(Vec3.Reject(v[i], axis).Length);
                }
                maxR = Percentile(rs, 0.98f);
                if (maxR < 1e-4f)
                {
                    why = "the canopy has no width";
                    return null;
                }

                // The hem is the lowest part of the widest band - lines reach it from below,
                // fabric rises from it.
                hs.Clear();
                foreach (var i in members)
                {
                    var h = Vec3.Dot(v[i], axis);
                    var rho = Vec3.Reject(v[i], axis).Length;
                    if (h > 0.5f * htop && rho > 0.9f * maxR) hs.Add(h);
                }
                hemH = Percentile(hs, 0.05f);

                dome.Clear();
                lines.Clear();
                var tol = 0.04f * maxR;
                foreach (var i in members)
                {
                    var h = Vec3.Dot(v[i], axis);
                    if (h >= hemH - tol) dome.Add(i);
                    else lines.Add(i);
                }

                // The dome's centroid lies on the axis of a round canopy.
                var c = Centroid(v, dome);
                var newAxis = c.NormalizedOr(axis);
                var change = Vec3.Dot(newAxis, axis);
                axis = newAxis;
                if (change > 0.99999f && iter > 0) break;
            }

            if (dome.Count < 12)
            {
                why = "found only " + dome.Count + " fabric vertices";
                return null;
            }

            // Hem radius: the fabric's radius at the hem itself, which is less than the
            // widest point for canopies that bulge above their skirt.
            rs.Clear();
            foreach (var i in dome)
            {
                var h = Vec3.Dot(v[i], axis);
                if (h < hemH + 0.06f * maxR) rs.Add(Vec3.Reject(v[i], axis).Length);
            }
            hemR = rs.Count > 0 ? Percentile(rs, 0.9f) : maxR;

            var e1 = Vec3.AnyPerpendicular(axis);
            var e2 = Vec3.Cross(axis, e1);

            // Symmetry: the fabric's radius in eight sectors around the axis, near the hem.
            var sectorMax = new float[8];
            foreach (var i in dome)
            {
                var p = v[i];
                var rho = Vec3.Reject(p, axis).Length;
                if (rho < 0.6f * maxR) continue;
                var th = MathX.WrapAngle((float)Math.Atan2(Vec3.Dot(p, e2), Vec3.Dot(p, e1)));
                var s = Math.Min(7, (int)(th / MathX.TwoPi * 8f));
                sectorMax[s] = Math.Max(sectorMax[s], rho);
            }
            float smin = float.MaxValue, smax = 0f;
            for (var s = 0; s < 8; s++)
            {
                smin = Math.Min(smin, sectorMax[s]);
                smax = Math.Max(smax, sectorMax[s]);
            }
            var symmetry = smin > 0f ? smax / smin : float.PositiveInfinity;
            if (symmetry > 1.45f)
            {
                why = "not round (sector radius ratio " + symmetry.ToString("0.00") + ")";
                return null;
            }

            // Polar profile about a centre point just below the hem, which keeps every
            // canopy shape from flat to deep conical star-shaped around it.
            var centreH = hemH - 0.15f * maxR;
            var centre = axis * centreH;
            var phiMax = 0f;
            var polar = new List<float>[PolarBins];
            for (var b = 0; b < PolarBins; b++) polar[b] = new List<float>();
            var phis = new float[dome.Count];
            var radii = new float[dome.Count];
            for (var d = 0; d < dome.Count; d++)
            {
                var w = v[dome[d]] - centre;
                var hh = Vec3.Dot(w, axis);
                var rho = Vec3.Reject(w, axis).Length;
                phis[d] = (float)Math.Atan2(rho, hh);
                radii[d] = w.Length;
                phiMax = Math.Max(phiMax, phis[d]);
            }
            if (phiMax <= 0f)
            {
                why = "degenerate dome";
                return null;
            }
            for (var d = 0; d < dome.Count; d++)
            {
                var b = Math.Min(PolarBins - 1, (int)(phis[d] / phiMax * PolarBins));
                polar[b].Add(radii[d]);
            }

            var binR = new float[PolarBins];
            var has = new bool[PolarBins];
            int first = -1, last = -1;
            for (var b = 0; b < PolarBins; b++)
            {
                if (polar[b].Count == 0) continue;
                binR[b] = Percentile(polar[b], 0.5f);
                has[b] = true;
                if (first < 0) first = b;
                last = b;
            }
            if (first < 0 || last - first < 3)
            {
                why = "the dome spans too little of the profile";
                return null;
            }
            // Fill interior gaps linearly.
            for (var b = first + 1; b < last; b++)
            {
                if (has[b]) continue;
                var nb = b + 1;
                while (!has[nb]) nb++;
                var pb = b - 1;
                var t = (float)(b - pb) / (nb - pb);
                binR[b] = MathX.Lerp(binR[pb], binR[nb], t);
                has[b] = true;
            }

            // Polyline from the vent edge (or crown) down to the hem.
            var pts = new List<float[]>();
            for (var b = first; b <= last; b++)
            {
                var phi = (b + 0.5f) / PolarBins * phiMax;
                if (b == first && first == 0) phi = 0f;
                var rho = binR[b] * (float)Math.Sin(phi);
                var h = centreH + binR[b] * (float)Math.Cos(phi);
                pts.Add(new[] { rho, h });
            }
            // Pin the last point onto the hem.
            pts.Add(new[] { hemR, hemH });

            // An open crown still needs a vent ring for the lattice to hang from; give a
            // closed crown a small one, as every real canopy has.
            if (pts[0][0] < 0.04f * maxR)
            {
                // Walk the polyline to the point where the radius reaches 4% of the canopy.
                var target = 0.04f * maxR;
                for (var i = 1; i < pts.Count; i++)
                {
                    if (pts[i][0] >= target)
                    {
                        var t = (target - pts[i - 1][0]) / Math.Max(1e-6f, pts[i][0] - pts[i - 1][0]);
                        var h = MathX.Lerp(pts[i - 1][1], pts[i][1], t);
                        pts.RemoveRange(0, i);
                        pts.Insert(0, new[] { target, h });
                        break;
                    }
                }
            }

            float[] prho, ph;
            Resample(pts, ProfileSamples, out prho, out ph);

            // Fit error of dome vertices against the resampled profile.
            var err = 0.0;
            for (var d = 0; d < dome.Count; d++)
            {
                var p = v[dome[d]];
                var rho = Vec3.Reject(p, axis).Length;
                var h = Vec3.Dot(p, axis);
                var best = float.MaxValue;
                for (var i = 0; i < prho.Length - 1; i++)
                    best = Math.Min(best, DistToSegment2(rho, h, prho[i], ph[i], prho[i + 1], ph[i + 1]));
                err += best;
            }
            var rms = (float)Math.Sqrt(err / dome.Count) / maxR;
            if (rms > 0.2f)
            {
                why = "the fabric does not follow a surface of revolution (rms " + rms.ToString("0.00") + ")";
                return null;
            }

            var confluence = FindConfluence(v, lines, axis, e1, e2, hemR, hemH);

            return new CanopyShape
            {
                Axis = axis,
                E1 = e1,
                E2 = e2,
                Confluence = confluence,
                HemRadius = prho[prho.Length - 1],
                HemHeight = ph[ph.Length - 1],
                ProfileRho = prho,
                ProfileH = ph,
                Symmetry = symmetry,
                RmsError = rms,
                Vertices = members.Count
            };
        }

        /// <summary>
        /// Where the suspension lines meet. Few models bring them right to the part: most
        /// gather them a few metres out and hang that point from the part on a riser (stock
        /// Mk16 and Mk25, RealChute, ReStock; Boring Crew Services' Starliner chutes have
        /// 5-8 m of shock cord), and in a cluster the meeting point is often shared, off to
        /// one side of each canopy's own axis. The lowest line vertex cannot tell any of that
        /// apart - the riser is below the lines too.
        ///
        /// So each line vertex is taken to lie on a straight line from the hem, and the point
        /// nearest all those lines is found by least squares. A first guess comes from
        /// extending every vertex down its line to the axis and taking the median; then the
        /// point is refined in 3D, working out for each vertex which hem point its line comes
        /// from by following the ray from the current guess out through it, and ignoring
        /// the riser below and anything that does not fit. Lines modelled with vertices all
        /// along them and lines modelled with vertices only at their ends both work.
        /// </summary>
        private static Vec3 FindConfluence(Vec3[] v, List<int> lines, Vec3 axis, Vec3 e1, Vec3 e2, float hemR, float hemH)
        {
            if (lines.Count < 8 || hemR <= 1e-4f) return Vec3.Zero;

            // First guess, on the axis.
            var apex = new List<float>();
            foreach (var i in lines)
            {
                var h = Vec3.Dot(v[i], axis);
                var rho = Vec3.Reject(v[i], axis).Length;
                // Not the hem, where the extension is ill-conditioned.
                if (rho > 0.9f * hemR || h >= hemH) continue;
                apex.Add(hemH - hemR * (hemH - h) / (hemR - rho));
            }
            if (apex.Count < 8) return Vec3.Zero;
            var c = axis * MathX.Clamp(Percentile(apex, 0.5f), 0f, 0.8f * hemH);

            // Refine in 3D.
            var cand = new List<int>();
            var hs = new List<Vec3>();
            var ds = new List<Vec3>();
            var weight = new List<float>();
            var resid = new List<float>();
            for (var iter = 0; iter < 6; iter++)
            {
                var ch = Vec3.Dot(c, axis);
                var down = c.Length > 0.01f * hemH ? c.Normalized : axis;
                cand.Clear();
                hs.Clear();
                ds.Clear();
                foreach (var i in lines)
                {
                    var p = v[i];
                    var h = Vec3.Dot(p, axis);
                    // Lines only: not the riser on the part's side of the meeting point, not the hem.
                    if (Vec3.Dot(p - c, down) < -0.02f * hemH || h >= hemH || Vec3.Reject(p, axis).Length > 0.9f * hemR) continue;
                    // Which hem point this vertex's line comes from: follow the ray from the
                    // current guess out through the vertex to the hem's plane. Right at the
                    // meeting point that direction means nothing, but any line through the
                    // vertex then passes through the meeting point anyway.
                    var ray = p - c;
                    var rise = Vec3.Dot(ray, axis);
                    var onHem = ray.Length > 0.02f * hemR && rise > 1e-4f ? c + ray * ((hemH - ch) / rise) : p;
                    var th = (float)Math.Atan2(Vec3.Dot(onHem, e2), Vec3.Dot(onHem, e1));
                    var hemPoint = axis * hemH + (e1 * (float)Math.Cos(th) + e2 * (float)Math.Sin(th)) * hemR;
                    var d = p - hemPoint;
                    var dl = d.Length;
                    if (dl < 0.05f * hemR) continue;
                    cand.Add(i);
                    hs.Add(hemPoint);
                    ds.Add(d / dl);
                }
                if (cand.Count < 8) break;

                // Trim: after the first pass, lines that miss the point by far more than most do
                // are fittings, the riser, or another canopy's lines.
                weight.Clear();
                resid.Clear();
                for (var k = 0; k < cand.Count; k++) resid.Add(DistToLine(c, hs[k], ds[k]));
                var cut = iter == 0 ? float.MaxValue : Math.Max(3f * Percentile(resid, 0.5f), 0.01f * hemR);
                for (var k = 0; k < cand.Count; k++) weight.Add(resid[k] <= cut ? 1f : 0f);

                Vec3 next;
                if (!NearestToLines(hs, ds, weight, out next)) break;
                var nh = Vec3.Dot(next, axis);
                if (nh < -0.1f * hemH || nh > 0.8f * hemH) break;
                var moved = Vec3.Distance(next, c);
                c = next;
                if (moved < 1e-4f * hemR) break;
            }

            // A riser shorter than this is the lines' own taper at the part: call it none.
            return c.Length > 0.05f * hemH ? c : Vec3.Zero;
        }

        /// <summary>
        /// Whether a cluster's canopies meet at one point, rather than each at its own on
        /// its own riser. If so, they are all given exactly that point.
        /// </summary>
        private static bool ShareConfluence(List<CanopyShape> shapes)
        {
            if (shapes.Count < 2) return false;
            var mean = Vec3.Zero;
            var r = float.MaxValue;
            foreach (var s in shapes)
            {
                mean += s.Confluence;
                r = Math.Min(r, s.MaxRadius);
            }
            mean = mean / shapes.Count;
            foreach (var s in shapes)
                if (Vec3.Distance(s.Confluence, mean) > 0.1f * r) return false;
            foreach (var s in shapes) s.Confluence = mean;
            return true;
        }

        /// <summary>
        /// For a cluster with a riser per canopy, how far out from the part they share one
        /// strap before splitting. Near the part a vertex on the shared strap sits on the
        /// cluster's mean line; one on a canopy's own riser sits on that. The junction is as
        /// far out as vertices keep hugging the mean line.
        /// </summary>
        private static void FindJunction(Vec3[] v, int[] assignment, List<CanopyShape> shapes)
        {
            if (shapes.Count < 2) return;
            var sum = Vec3.Zero;
            var shortest = float.MaxValue;
            var r = float.MaxValue;
            foreach (var s in shapes)
            {
                sum += s.Confluence;
                shortest = Math.Min(shortest, s.RiserLength);
                r = Math.Min(r, s.MaxRadius);
            }
            if (shortest < 0.05f * r || sum.Length < 1e-4f) return;
            var dir = sum.Normalized;

            var along = new List<float>();
            for (var i = 0; i < v.Length; i++)
            {
                var s = shapes[assignment[i]];
                var h = Vec3.Dot(v[i], dir);
                if (h <= 0f || h > 0.9f * shortest) continue;
                var onMean = Vec3.Reject(v[i], dir).Length;
                if (onMean > 0.1f * r) continue;
                var c = s.Confluence;
                var t = MathX.Clamp01(Vec3.Dot(v[i], c) / c.SqrLength);
                var onOwn = Vec3.Distance(v[i], c * t);
                if (onMean * 2f < onOwn) along.Add(h);
            }
            if (along.Count < 4) return;
            var hj = Percentile(along, 0.95f);
            if (hj < 0.05f * shortest) return;
            foreach (var s in shapes) s.RiserJunction = dir * hj;
        }

        private static float DistToLine(Vec3 p, Vec3 a, Vec3 dir)
        {
            return Vec3.Reject(p - a, dir).Length;
        }

        /// <summary>Least-squares point nearest a set of weighted lines (point, unit direction).</summary>
        private static bool NearestToLines(List<Vec3> points, List<Vec3> dirs, List<float> weights, out Vec3 result)
        {
            // Sum over lines of w (I - d d^T), and of w (I - d d^T) a.
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
            for (var k = 0; k < points.Count; k++)
            {
                var w = weights[k];
                if (w <= 0f) continue;
                var d = dirs[k];
                var p = points[k];
                double m00 = 1 - d.X * d.X, m01 = -d.X * d.Y, m02 = -d.X * d.Z;
                double m11 = 1 - d.Y * d.Y, m12 = -d.Y * d.Z, m22 = 1 - d.Z * d.Z;
                a00 += w * m00; a01 += w * m01; a02 += w * m02;
                a11 += w * m11; a12 += w * m12; a22 += w * m22;
                b0 += w * (m00 * p.X + m01 * p.Y + m02 * p.Z);
                b1 += w * (m01 * p.X + m11 * p.Y + m12 * p.Z);
                b2 += w * (m02 * p.X + m12 * p.Y + m22 * p.Z);
            }
            // Solve the symmetric 3x3 by Cramer's rule.
            var c00 = a11 * a22 - a12 * a12;
            var c01 = a02 * a12 - a01 * a22;
            var c02 = a01 * a12 - a02 * a11;
            var det = a00 * c00 + a01 * c01 + a02 * c02;
            if (Math.Abs(det) < 1e-9)
            {
                result = Vec3.Zero;
                return false;
            }
            var c11 = a00 * a22 - a02 * a02;
            var c12 = a01 * a02 - a00 * a12;
            var c22 = a00 * a11 - a01 * a01;
            result = new Vec3(
                (float)((c00 * b0 + c01 * b1 + c02 * b2) / det),
                (float)((c01 * b0 + c11 * b1 + c12 * b2) / det),
                (float)((c02 * b0 + c12 * b1 + c22 * b2) / det));
            return result.IsFinite;
        }

        /// <summary>Resamples a 2D polyline to evenly spaced arc-length points.</summary>
        private static void Resample(List<float[]> pts, int count, out float[] rho, out float[] h)
        {
            var cum = new float[pts.Count];
            for (var i = 1; i < pts.Count; i++)
            {
                var dr = pts[i][0] - pts[i - 1][0];
                var dh = pts[i][1] - pts[i - 1][1];
                cum[i] = cum[i - 1] + MathX.Sqrt(dr * dr + dh * dh);
            }
            var total = cum[pts.Count - 1];
            rho = new float[count];
            h = new float[count];
            var seg = 0;
            for (var k = 0; k < count; k++)
            {
                var target = total * k / (count - 1);
                while (seg < pts.Count - 2 && cum[seg + 1] < target) seg++;
                var span = cum[seg + 1] - cum[seg];
                var t = span > 1e-9f ? (target - cum[seg]) / span : 0f;
                t = MathX.Clamp01(t);
                rho[k] = MathX.Lerp(pts[seg][0], pts[seg + 1][0], t);
                h[k] = MathX.Lerp(pts[seg][1], pts[seg + 1][1], t);
            }
        }

        internal static float DistToSegment2(float px, float py, float ax, float ay, float bx, float by)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var l2 = dx * dx + dy * dy;
            var t = l2 > 1e-12f ? MathX.Clamp01(((px - ax) * dx + (py - ay) * dy) / l2) : 0f;
            var cx = ax + dx * t - px;
            var cy = ay + dy * t - py;
            return cx * cx + cy * cy;
        }

        /// <summary>Arc fraction (0 at vent edge, 1 at hem) of the profile point nearest (rho, h).</summary>
        internal static float ProjectOntoProfile(CanopyShape s, float rho, float h, out float offset)
        {
            var n = s.ProfileRho.Length;
            var best = float.MaxValue;
            var bestS = 0f;
            for (var i = 0; i < n - 1; i++)
            {
                float ax = s.ProfileRho[i], ay = s.ProfileH[i];
                float bx = s.ProfileRho[i + 1], by = s.ProfileH[i + 1];
                var dx = bx - ax;
                var dy = by - ay;
                var l2 = dx * dx + dy * dy;
                var t = l2 > 1e-12f ? MathX.Clamp01(((rho - ax) * dx + (h - ay) * dy) / l2) : 0f;
                var cx = ax + dx * t - rho;
                var cy = ay + dy * t - h;
                var d = cx * cx + cy * cy;
                if (d < best)
                {
                    best = d;
                    bestS = (i + t) / (n - 1);
                }
            }
            offset = MathX.Sqrt(best);
            return bestS;
        }

        private static Vec3 Centroid(Vec3[] v, List<int> idx)
        {
            var s = Vec3.Zero;
            foreach (var i in idx) s += v[i];
            return idx.Count > 0 ? s / idx.Count : Vec3.Zero;
        }

        private static float Percentile(List<float> values, float p)
        {
            if (values.Count == 0) return 0f;
            var arr = values.ToArray();
            Array.Sort(arr);
            var idx = (int)Math.Round(MathX.Clamp01(p) * (arr.Length - 1));
            return arr[idx];
        }
    }
}
