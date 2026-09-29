using System;
using System.Collections.Generic;

namespace ParaSoft.Core
{
    /// <summary>How finely a canopy is simulated.</summary>
    public struct LatticeResolution
    {
        public int Gores;         // particles around the canopy
        public int Rings;         // ring intervals from vent edge to hem
        public int LineSegments;  // segments per suspension line

        public LatticeResolution(int gores, int rings, int lineSegments)
        {
            Gores = gores;
            Rings = rings;
            LineSegments = lineSegments;
        }

        public static readonly LatticeResolution Low = new LatticeResolution(12, 5, 3);
        public static readonly LatticeResolution Medium = new LatticeResolution(20, 7, 4);
        public static readonly LatticeResolution High = new LatticeResolution(28, 9, 5);
    }

    public enum ConstraintKind : byte
    {
        /// <summary>Along a radial seam. Fabric: resists stretch, buckles freely in compression.</summary>
        Seam = 0,
        /// <summary>Around a ring, between neighbouring seams. The hem ring's are the reefing line.</summary>
        Hoop = 1,
        /// <summary>Across a panel's diagonal.</summary>
        Shear = 2,
        /// <summary>Two cells apart: a weak spring that keeps fabric from folding to a crease.</summary>
        Bend = 3,
        /// <summary>A suspension line segment, or the pilot chute's bridle.</summary>
        Line = 4
    }

    /// <summary>
    /// The particle lattice a round canopy is simulated on, and its designed (fully
    /// inflated) shape.
    ///
    /// Particles sit on the radial seams - where the real load tapes run - at evenly spaced
    /// rings from the vent edge down to the hem, then down each suspension line to the
    /// confluence. One more particle is the pilot chute, bridled to the vent; it is not
    /// drawn, but it is what pulls the deployment bag off the part and keeps the crown
    /// from folding in. A lattice is immutable and shared by every canopy made from the
    /// same model.
    /// </summary>
    public sealed class CanopyLattice
    {
        public readonly CanopyShape Shape;
        public readonly int Gores, Rings, LineSegments;

        public readonly Vec3[] RestPositions;
        /// <summary>Distance from the confluence along the structure: the order a canopy leaves its bag in.</summary>
        public readonly float[] PayoutDistance;
        /// <summary>Fabric area each particle represents (0 for line particles).</summary>
        public readonly float[] FabricArea;
        /// <summary>Line length each particle represents.</summary>
        public readonly float[] LineLength;

        public readonly int[] ConA, ConB;
        public readonly float[] ConRest;
        public readonly ConstraintKind[] ConKind;
        /// <summary>Indices (into the constraint arrays) of the hem's hoop constraints.</summary>
        public readonly int[] HemHoops;

        /// <summary>Triangles of fabric, wound so their normal faces out of the canopy.</summary>
        public readonly int[] Triangles;

        public readonly float TotalFabricArea;
        public readonly float TotalLineLength;
        public readonly float PilotBridle;

        public int DomeCount { get { return Gores * (Rings + 1); } }
        public int ConfluenceIndex { get { return DomeCount + Gores * (LineSegments - 1); } }
        public int PilotIndex { get { return ConfluenceIndex + 1; } }
        public int ParticleCount { get { return ConfluenceIndex + 2; } }

        public int Dome(int ring, int gore)
        {
            gore %= Gores;
            if (gore < 0) gore += Gores;
            return ring * Gores + gore;
        }

        /// <summary>Particle j along gore g's line: 0 is the hem, LineSegments the confluence.</summary>
        public int LineParticle(int gore, int j)
        {
            gore %= Gores;
            if (gore < 0) gore += Gores;
            if (j <= 0) return Dome(Rings, gore);
            if (j >= LineSegments) return ConfluenceIndex;
            return DomeCount + gore * (LineSegments - 1) + (j - 1);
        }

        public float GoreAngle(int gore) { return MathX.TwoPi * gore / Gores; }

        private CanopyLattice(CanopyShape shape, LatticeResolution res)
        {
            Shape = shape;
            Gores = Math.Max(6, res.Gores);
            Rings = Math.Max(2, res.Rings);
            LineSegments = Math.Max(1, res.LineSegments);

            var n = ParticleCount;
            RestPositions = new Vec3[n];
            PayoutDistance = new float[n];
            FabricArea = new float[n];
            LineLength = new float[n];

            // Dome particles on the resampled meridian.
            var lineLen = shape.LineLength;
            for (var k = 0; k <= Rings; k++)
            {
                float rho, h;
                shape.ProfileAt((float)k / Rings, out rho, out h);
                for (var g = 0; g < Gores; g++)
                {
                    var th = GoreAngle(g);
                    var dir = shape.E1 * (float)Math.Cos(th) + shape.E2 * (float)Math.Sin(th);
                    RestPositions[Dome(k, g)] = shape.Axis * h + dir * rho;
                }
            }
            // How far out along the structure each ring sits, summed over the seam segments
            // themselves (chords, not the curved meridian): a streamer laid out at these
            // distances has every seam exactly at its rest length.
            var along = lineLen;
            for (var k = Rings; k >= 0; k--)
            {
                if (k < Rings) along += Vec3.Distance(RestPositions[Dome(k, 0)], RestPositions[Dome(k + 1, 0)]);
                for (var g = 0; g < Gores; g++) PayoutDistance[Dome(k, g)] = along;
            }
            var meridian = along - lineLen;

            // Line particles, straight from the hem to the confluence.
            for (var g = 0; g < Gores; g++)
            {
                var hemPoint = RestPositions[Dome(Rings, g)];
                for (var j = 1; j < LineSegments; j++)
                {
                    var i = LineParticle(g, j);
                    var t = (float)j / LineSegments;
                    RestPositions[i] = Vec3.Lerp(hemPoint, shape.Confluence, t);
                    PayoutDistance[i] = lineLen * (1f - t);
                }
            }
            RestPositions[ConfluenceIndex] = shape.Confluence;
            PayoutDistance[ConfluenceIndex] = 0f;

            // Pilot chute: above the crown, a third of a canopy radius out.
            float vrho, vh;
            shape.ProfileAt(0f, out vrho, out vh);
            PilotBridle = Math.Max(0.35f * shape.MaxRadius, vrho * 1.2f);
            RestPositions[PilotIndex] = shape.Axis * (vh + MathX.Sqrt(Math.Max(0f, PilotBridle * PilotBridle - vrho * vrho)));
            PayoutDistance[PilotIndex] = lineLen + meridian + PilotBridle;

            var ca = new List<int>();
            var cb = new List<int>();
            var kind = new List<ConstraintKind>();
            var hem = new List<int>();

            Action<int, int, ConstraintKind> add = (a, b, k) =>
            {
                ca.Add(a);
                cb.Add(b);
                kind.Add(k);
            };

            for (var k = 0; k <= Rings; k++)
            {
                for (var g = 0; g < Gores; g++)
                {
                    if (k == Rings) hem.Add(ca.Count);
                    add(Dome(k, g), Dome(k, g + 1), ConstraintKind.Hoop);
                    if (k < Rings)
                    {
                        add(Dome(k, g), Dome(k + 1, g), ConstraintKind.Seam);
                        add(Dome(k, g), Dome(k + 1, g + 1), ConstraintKind.Shear);
                        add(Dome(k, g + 1), Dome(k + 1, g), ConstraintKind.Shear);
                    }
                    if (k < Rings - 1) add(Dome(k, g), Dome(k + 2, g), ConstraintKind.Bend);
                    add(Dome(k, g), Dome(k, g + 2), ConstraintKind.Bend);
                }
            }
            for (var g = 0; g < Gores; g++)
                for (var j = 0; j < LineSegments; j++)
                    add(LineParticle(g, j), LineParticle(g, j + 1), ConstraintKind.Line);
            for (var g = 0; g < Gores; g++)
                add(PilotIndex, Dome(0, g), ConstraintKind.Line);

            ConA = ca.ToArray();
            ConB = cb.ToArray();
            ConKind = kind.ToArray();
            HemHoops = hem.ToArray();
            ConRest = new float[ConA.Length];
            for (var c = 0; c < ConA.Length; c++)
                ConRest[c] = Vec3.Distance(RestPositions[ConA[c]], RestPositions[ConB[c]]);

            // Fabric triangles, wound outward. "Outward" is away from a point on the axis
            // below the hem - inside the canopy's mouth.
            var inside = shape.Axis * (shape.HemHeight - 0.25f * shape.MaxRadius);
            var tris = new List<int>();
            for (var k = 0; k < Rings; k++)
            {
                for (var g = 0; g < Gores; g++)
                {
                    int a = Dome(k, g), b = Dome(k, g + 1), c = Dome(k + 1, g + 1), d = Dome(k + 1, g);
                    AddTri(tris, a, b, c, inside);
                    AddTri(tris, a, c, d, inside);
                }
            }
            Triangles = tris.ToArray();

            // Mass bookkeeping: each triangle's area to its corners, each line to its ends.
            for (var t = 0; t < Triangles.Length; t += 3)
            {
                var area = TriArea(RestPositions[Triangles[t]], RestPositions[Triangles[t + 1]], RestPositions[Triangles[t + 2]]);
                TotalFabricArea += area;
                for (var q = 0; q < 3; q++) FabricArea[Triangles[t + q]] += area / 3f;
            }
            for (var c = 0; c < ConA.Length; c++)
            {
                if (ConKind[c] != ConstraintKind.Line) continue;
                LineLength[ConA[c]] += ConRest[c] * 0.5f;
                LineLength[ConB[c]] += ConRest[c] * 0.5f;
                if (ConA[c] != PilotIndex) TotalLineLength += ConRest[c];
            }
        }

        private void AddTri(List<int> tris, int a, int b, int c, Vec3 inside)
        {
            var pa = RestPositions[a];
            var pb = RestPositions[b];
            var pc = RestPositions[c];
            var nrm = Vec3.Cross(pb - pa, pc - pa);
            var centre = (pa + pb + pc) / 3f;
            if (Vec3.Dot(nrm, centre - inside) >= 0f)
            {
                tris.Add(a); tris.Add(b); tris.Add(c);
            }
            else
            {
                tris.Add(a); tris.Add(c); tris.Add(b);
            }
        }

        public static float TriArea(Vec3 a, Vec3 b, Vec3 c)
        {
            return 0.5f * Vec3.Cross(b - a, c - a).Length;
        }

        public static CanopyLattice Build(CanopyShape shape, LatticeResolution resolution)
        {
            if (shape == null) throw new ArgumentNullException("shape");
            return new CanopyLattice(shape, resolution);
        }
    }
}
