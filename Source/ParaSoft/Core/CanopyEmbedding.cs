using System;

namespace ParaSoft.Core
{
    /// <summary>
    /// Ties every vertex of a part's own canopy model to the simulation lattice, so the
    /// model the player already sees - its texture, its UVs, Custom Parachute Message's
    /// encoded gores, whatever TexturesUnlimited did to it - is what deforms.
    ///
    /// A fabric vertex is located by where it sits on the canopy surface (angle around the
    /// axis, distance down the meridian) and a small offset in that spot's local frame, so
    /// seams, scallops and double-sided fabric keep their shape. A line vertex is located by
    /// how far it is down the suspension lines and which lines it sits between. A riser
    /// vertex - on the shock cord between the part and the confluence - by how far along
    /// that cord it is. Evaluating on the rest lattice reproduces the model exactly;
    /// evaluating on the simulated lattice gives the deformed canopy.
    /// </summary>
    public sealed class CanopyEmbedding
    {
        private const byte DomeRegion = 0;
        private const byte LineRegion = 1;
        private const byte RiserRegion = 2;

        private readonly CanopyLattice lattice;
        private readonly byte[] region;
        private readonly int[] cellA;      // ring (dome) or line segment (lines)
        private readonly int[] gore;
        private readonly float[] fa, fg;
        private readonly Vec3[] offset;    // in (t1, t2, n)
        private readonly Vec3[] normal;    // in (t1, t2, n), may be null
        private readonly Vec3[] tangent;   // in (t1, t2, n), may be null
        private readonly float[] tangentW;
        private float restJunctionLength;

        public int Count { get { return region.Length; } }
        public int DomeCount { get; private set; }
        public int LineCount { get; private set; }
        public int RiserCount { get; private set; }

        /// <summary>
        /// Farthest any line vertex sits from the line it was matched to, at rest. Lines are
        /// thin: anything more than a few centimetres means the lattice's lines are not where
        /// the model's are, and that vertex will be dragged about as they move.
        /// </summary>
        public float MaxLineOffset { get; private set; }
        /// <summary>The same for the riser, which also carries whatever fittings sit near the part.</summary>
        public float MaxRiserOffset { get; private set; }

        private CanopyEmbedding(CanopyLattice lattice, int n, bool normals, bool tangents)
        {
            this.lattice = lattice;
            region = new byte[n];
            cellA = new int[n];
            gore = new int[n];
            fa = new float[n];
            fg = new float[n];
            offset = new Vec3[n];
            if (normals) normal = new Vec3[n];
            if (tangents)
            {
                tangent = new Vec3[n];
                tangentW = new float[n];
            }
        }

        /// <param name="lattice">The lattice the vertices will follow.</param>
        /// <param name="vertices">Vertices in the canopy frame, deployed pose.</param>
        /// <param name="normals">Matching normals, or null.</param>
        /// <param name="tangents">Matching tangents as (x, y, z, w) quadruples, or null.</param>
        public static CanopyEmbedding Compute(CanopyLattice lattice, Vec3[] vertices, Vec3[] normals, float[] tangents)
        {
            var e = new CanopyEmbedding(lattice, vertices.Length, normals != null, tangents != null);
            var s = lattice.Shape;
            var rest = lattice.RestPositions;
            var confH = Vec3.Dot(s.Confluence, s.Axis);
            var hasRiser = s.RiserLength > 1e-3f;
            var junction = s.RiserJunction;
            var hasJunction = hasRiser && junction.Length > 1e-3f;
            e.restJunctionLength = hasJunction ? junction.Length : 0f;

            for (var i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                var h = Vec3.Dot(v, s.Axis);
                var radial = Vec3.Reject(v, s.Axis);
                var rho = radial.Length;
                var theta = MathX.WrapAngle((float)Math.Atan2(Vec3.Dot(v, s.E2), Vec3.Dot(v, s.E1)));
                var gf = theta / MathX.TwoPi * lattice.Gores;
                var g0 = Math.Min((int)gf, lattice.Gores - 1);

                float domeDist;
                var sParam = CanopyFitter.ProjectOntoProfile(s, rho, h, out domeDist);

                // Distance to the nearest line. Which line: the one whose hem point is where
                // the ray from the confluence out through this vertex meets the hem's plane -
                // the confluence need not be on the axis (a cluster's often is not), so the
                // vertex's own angle round the axis is not the line's.
                var hemRho = s.ProfileRho[s.ProfileRho.Length - 1];
                var hemH = s.ProfileH[s.ProfileH.Length - 1];
                var ray = v - s.Confluence;
                var rise = Vec3.Dot(ray, s.Axis);
                var onHem = ray.Length > 0.02f * hemRho && rise > 1e-4f ? s.Confluence + ray * ((hemH - confH) / rise) : v;
                var lineTheta = MathX.WrapAngle((float)Math.Atan2(Vec3.Dot(onHem, s.E2), Vec3.Dot(onHem, s.E1)));
                var lineHem = s.Axis * hemH + (s.E1 * (float)Math.Cos(lineTheta) + s.E2 * (float)Math.Sin(lineTheta)) * hemRho;
                var seg = lineHem - s.Confluence;
                var l2 = seg.SqrLength;
                var t = l2 > 1e-12f ? MathX.Clamp01(Vec3.Dot(ray, seg) / l2) : 0f;
                var lineDist = Vec3.Distance(v, s.Confluence + seg * t);
                var lgf = lineTheta / MathX.TwoPi * lattice.Gores;
                var lg0 = Math.Min((int)lgf, lattice.Gores - 1);

                // Distance to the riser: from the part to the junction a cluster's risers share,
                // if any, then on to this canopy's confluence.
                var riserDist = float.MaxValue;
                var tr = 0f;
                var rseg = 1;
                if (hasRiser)
                {
                    var upper = s.Confluence - junction;
                    tr = MathX.Clamp01(Vec3.Dot(v - junction, upper) / Math.Max(upper.SqrLength, 1e-12f));
                    riserDist = Vec3.Distance(v, junction + upper * tr);
                    if (hasJunction)
                    {
                        var tl = MathX.Clamp01(Vec3.Dot(v, junction) / junction.SqrLength);
                        var dl = Vec3.Distance(v, junction * tl);
                        if (dl < riserDist)
                        {
                            riserDist = dl;
                            tr = tl;
                            rseg = 0;
                        }
                    }
                }

                Vec3 point, t1, t2, nrm;
                var region = LineRegion;
                if (riserDist < lineDist && riserDist < domeDist)
                {
                    e.region[i] = RiserRegion;
                    region = RiserRegion;
                    e.RiserCount++;
                    e.cellA[i] = rseg;
                    e.fa[i] = tr;
                    e.RiserFrame(rest, Vec3.Zero, junction, rseg, tr, s.Axis, out point, out t1, out t2, out nrm);
                }
                else if (domeDist <= lineDist || t > 0.999f)
                {
                    e.region[i] = DomeRegion;
                    e.DomeCount++;
                    var kf = MathX.Clamp(sParam * lattice.Rings, 0f, lattice.Rings);
                    var k0 = Math.Min((int)kf, lattice.Rings - 1);
                    e.cellA[i] = k0;
                    e.fa[i] = kf - k0;
                    e.gore[i] = g0;
                    e.fg[i] = gf - g0;
                    e.DomeFrame(rest, k0, g0, e.fa[i], e.fg[i], s.Axis, out point, out t1, out t2, out nrm);
                    region = DomeRegion;
                }
                else
                {
                    e.region[i] = LineRegion;
                    e.LineCount++;
                    var jf = MathX.Clamp((1f - t) * lattice.LineSegments, 0f, lattice.LineSegments);
                    var j0 = Math.Min((int)jf, lattice.LineSegments - 1);
                    e.cellA[i] = j0;
                    e.fa[i] = jf - j0;
                    e.gore[i] = lg0;
                    e.fg[i] = lgf - lg0;
                    e.LineFrame(rest, j0, lg0, e.fa[i], e.fg[i], s.Axis, out point, out t1, out t2, out nrm);
                }

                var d = v - point;
                e.offset[i] = new Vec3(Vec3.Dot(d, t1), Vec3.Dot(d, t2), Vec3.Dot(d, nrm));
                if (region == LineRegion) e.MaxLineOffset = Math.Max(e.MaxLineOffset, d.Length);
                else if (region == RiserRegion) e.MaxRiserOffset = Math.Max(e.MaxRiserOffset, d.Length);
                if (normals != null)
                {
                    var nn = normals[i];
                    e.normal[i] = new Vec3(Vec3.Dot(nn, t1), Vec3.Dot(nn, t2), Vec3.Dot(nn, nrm));
                }
                if (tangents != null)
                {
                    var tt = new Vec3(tangents[i * 4], tangents[i * 4 + 1], tangents[i * 4 + 2]);
                    e.tangent[i] = new Vec3(Vec3.Dot(tt, t1), Vec3.Dot(tt, t2), Vec3.Dot(tt, nrm));
                    e.tangentW[i] = tangents[i * 4 + 3];
                }
            }
            return e;
        }

        /// <summary>
        /// Positions (and optionally normals and tangents) of every embedded vertex, given
        /// the lattice's particle positions. Output arrays must be Count long (tangents 4x).
        /// The riser, if there is one, runs from the frame origin - the anchor on the part.
        /// </summary>
        public void Evaluate(Vec3[] particles, Vec3[] positions, Vec3[] normalsOut, float[] tangentsOut)
        {
            Evaluate(particles, Vec3.Zero, positions, normalsOut, tangentsOut);
        }

        /// <param name="riserEnd">Where the riser's lower end is: the anchor, or wherever a cut riser trails to.</param>
        public void Evaluate(Vec3[] particles, Vec3 riserEnd, Vec3[] positions, Vec3[] normalsOut, float[] tangentsOut)
        {
            // On its own, a canopy's share of a cluster's riser junction lies on its own riser.
            var toConf = (particles[lattice.ConfluenceIndex] - riserEnd).NormalizedOr(lattice.Shape.Axis);
            Evaluate(particles, riserEnd, riserEnd + toConf * restJunctionLength, positions, normalsOut, tangentsOut);
        }

        /// <param name="riserEnd">Where the riser's lower end is: the anchor, or wherever a cut riser trails to.</param>
        /// <param name="riserJunction">Where a cluster's risers join (see RiserPath); ignored if the model has no junction.</param>
        public void Evaluate(Vec3[] particles, Vec3 riserEnd, Vec3 riserJunction, Vec3[] positions, Vec3[] normalsOut, float[] tangentsOut)
        {
            if (restJunctionLength <= 0f) riserJunction = riserEnd;
            // A canopy axis for the degenerate cases - fully collapsed rings - taken from the
            // vent towards... the confluence, reversed.
            var ventCentre = Vec3.Zero;
            for (var g = 0; g < lattice.Gores; g++) ventCentre += particles[lattice.Dome(0, g)];
            ventCentre = ventCentre / lattice.Gores;
            var axis = (ventCentre - particles[lattice.ConfluenceIndex]).NormalizedOr(lattice.Shape.Axis);

            var wantNormals = normalsOut != null && normal != null;
            var wantTangents = tangentsOut != null && tangent != null;
            for (var i = 0; i < region.Length; i++)
            {
                Vec3 point, t1, t2, nrm;
                if (region[i] == DomeRegion)
                    DomeFrame(particles, cellA[i], gore[i], fa[i], fg[i], axis, out point, out t1, out t2, out nrm);
                else if (region[i] == LineRegion)
                    LineFrame(particles, cellA[i], gore[i], fa[i], fg[i], axis, out point, out t1, out t2, out nrm);
                else
                    RiserFrame(particles, riserEnd, riserJunction, cellA[i], fa[i], axis, out point, out t1, out t2, out nrm);

                var o = offset[i];
                positions[i] = point + t1 * o.X + t2 * o.Y + nrm * o.Z;
                if (wantNormals)
                {
                    var nl = normal[i];
                    normalsOut[i] = t1 * nl.X + t2 * nl.Y + nrm * nl.Z;
                }
                if (wantTangents)
                {
                    var tl = tangent[i];
                    var tw = t1 * tl.X + t2 * tl.Y + nrm * tl.Z;
                    tangentsOut[i * 4] = tw.X;
                    tangentsOut[i * 4 + 1] = tw.Y;
                    tangentsOut[i * 4 + 2] = tw.Z;
                    tangentsOut[i * 4 + 3] = tangentW[i];
                }
            }
        }

        private void DomeFrame(Vec3[] p, int k0, int g0, float fk, float fgv, Vec3 axis,
                               out Vec3 point, out Vec3 t1, out Vec3 t2, out Vec3 n)
        {
            var g1 = g0 + 1;
            var a = p[lattice.Dome(k0, g0)];
            var b = p[lattice.Dome(k0, g1)];
            var c = p[lattice.Dome(k0 + 1, g0)];
            var d = p[lattice.Dome(k0 + 1, g1)];
            var top = Vec3.Lerp(a, b, fgv);
            var bottom = Vec3.Lerp(c, d, fgv);
            point = Vec3.Lerp(top, bottom, fk);
            var hoop = Vec3.Lerp(b - a, d - c, fk);
            var down = bottom - top;
            MakeFrame(hoop, down, axis, point, out t1, out t2, out n);
        }

        private void LineFrame(Vec3[] p, int j0, int g0, float fj, float fgv, Vec3 axis,
                               out Vec3 point, out Vec3 t1, out Vec3 t2, out Vec3 n)
        {
            var g1 = g0 + 1;
            var a0 = p[lattice.LineParticle(g0, j0)];
            var a1 = p[lattice.LineParticle(g0, j0 + 1)];
            var b0 = p[lattice.LineParticle(g1, j0)];
            var b1 = p[lattice.LineParticle(g1, j0 + 1)];
            var pa = Vec3.Lerp(a0, a1, fj);
            var pb = Vec3.Lerp(b0, b1, fj);
            point = Vec3.Lerp(pa, pb, fgv);
            var along = Vec3.Lerp(a1 - a0, b1 - b0, fgv);
            // Near the confluence neighbouring lines meet, so take the hoop direction from
            // the hem instead, where the lines are always apart.
            var hoop = p[lattice.Dome(lattice.Rings, g1)] - p[lattice.Dome(lattice.Rings, g0)];
            MakeFrame(hoop, along, axis, point, out t1, out t2, out n);
        }

        /// <summary>
        /// A point on the riser - segment 0 from its lower end to the junction, segment 1 on
        /// to the confluence - framed so that it turns with the canopy: its first side
        /// direction is taken across the hem.
        /// </summary>
        private void RiserFrame(Vec3[] p, Vec3 end, Vec3 junction, int segment, float f, Vec3 axis,
                                out Vec3 point, out Vec3 t1, out Vec3 t2, out Vec3 n)
        {
            var from = segment == 0 ? end : junction;
            var to = segment == 0 ? junction : p[lattice.ConfluenceIndex];
            var along = to - from;
            point = from + along * f;
            var across = p[lattice.Dome(lattice.Rings, 0)] - p[lattice.Dome(lattice.Rings, lattice.Gores / 2)];
            MakeFrame(across, along, axis, point, out t1, out t2, out n);
        }

        /// <summary>
        /// An orthonormal frame from a hoop direction and a meridian direction, robust to
        /// either collapsing to zero.
        /// </summary>
        private static void MakeFrame(Vec3 hoop, Vec3 meridian, Vec3 axis, Vec3 point,
                                      out Vec3 t1, out Vec3 t2, out Vec3 n)
        {
            var mLen = meridian.Length;
            var m = mLen > 1e-6f ? meridian / mLen : -axis;
            var h = hoop - m * Vec3.Dot(hoop, m);
            var hLen = h.Length;
            if (hLen > 1e-6f) t1 = h / hLen;
            else
            {
                // Hoop collapsed (a streamer): use the direction around the axis instead.
                var radial = Vec3.Reject(point, axis);
                var around = Vec3.Cross(axis, radial);
                around = around - m * Vec3.Dot(around, m);
                t1 = around.NormalizedOr(Vec3.AnyPerpendicular(m));
            }
            // n = hoop x meridian points out of the canopy for the lattice's winding.
            n = Vec3.Cross(t1, m).NormalizedOr(Vec3.AnyPerpendicular(t1));
            t2 = Vec3.Cross(n, t1);
        }
    }
}
