using System;
using System.Collections.Generic;

namespace ParaSoft.Core
{
    // Everything a canopy can touch, rebuilt by the game layer each physics frame in the
    // simulation's frame (world axes, origin at the anchor). Colliders are one-way: they
    // push fabric around, and nothing is pushed back - a canopy never changes how the
    // craft it hangs from, or anything it lands on, moves.

    public struct CollisionSphere
    {
        public Vec3 Centre;
        public float Radius;
        public Vec3 Velocity;
    }

    public struct CollisionCapsule
    {
        public Vec3 A, B;
        public float Radius;
        public Vec3 Velocity;
    }

    /// <summary>An oriented box: centre, three unit axes and half extents along them.</summary>
    public struct CollisionBox
    {
        public Vec3 Centre;
        public Vec3 AxisX, AxisY, AxisZ;
        public Vec3 HalfExtents;
        public Vec3 Velocity;
    }

    /// <summary>
    /// A surface sampled on a small grid in its own tangent plane - terrain from raycasts,
    /// or the sea with Scatterer's waves on it.
    /// </summary>
    public sealed class HeightField
    {
        public Vec3 Origin;          // grid centre, on the reference plane
        public Vec3 Up, East, North; // orthonormal
        public float Cell;           // grid spacing
        public int Size;             // Size x Size samples
        public float[] Heights;      // above the reference plane, row-major [north][east]
        public Vec3 SurfaceVelocity; // e.g. a current; zero for terrain
        public float Friction = 0.6f;
        public bool IsWater;

        public HeightField(int size)
        {
            Size = Math.Max(2, size);
            Heights = new float[Size * Size];
        }

        /// <summary>A flat plane through a point.</summary>
        public static HeightField Flat(Vec3 point, Vec3 up)
        {
            var hf = new HeightField(2);
            hf.Origin = point;
            hf.Up = up.NormalizedOr(Vec3.UnitY);
            hf.East = Vec3.AnyPerpendicular(hf.Up);
            hf.North = Vec3.Cross(hf.Up, hf.East);
            hf.Cell = 1e4f;
            return hf;
        }

        /// <summary>Height of p above the surface (negative when below it).</summary>
        public float Clearance(Vec3 p)
        {
            var d = p - Origin;
            var e = Vec3.Dot(d, East) / Cell + (Size - 1) * 0.5f;
            var n = Vec3.Dot(d, North) / Cell + (Size - 1) * 0.5f;
            e = MathX.Clamp(e, 0f, Size - 1.001f);
            n = MathX.Clamp(n, 0f, Size - 1.001f);
            var ei = (int)e;
            var ni = (int)n;
            var fe = e - ei;
            var fn = n - ni;
            var h00 = Heights[ni * Size + ei];
            var h10 = Heights[ni * Size + ei + 1];
            var h01 = Heights[(ni + 1) * Size + ei];
            var h11 = Heights[(ni + 1) * Size + ei + 1];
            var h = MathX.Lerp(MathX.Lerp(h00, h10, fe), MathX.Lerp(h01, h11, fe), fn);
            return Vec3.Dot(d, Up) - h;
        }
    }

    public sealed class CollisionWorld
    {
        public readonly List<CollisionSphere> Spheres = new List<CollisionSphere>();
        public readonly List<CollisionCapsule> Capsules = new List<CollisionCapsule>();
        public readonly List<CollisionBox> Boxes = new List<CollisionBox>();
        public HeightField Ground;
        public HeightField Water;
        /// <summary>Friction against solid colliders (spheres, capsules, boxes).</summary>
        public float Friction = 0.4f;

        public void Clear()
        {
            Spheres.Clear();
            Capsules.Clear();
            Boxes.Clear();
            Ground = null;
            Water = null;
        }

        public bool IsEmpty
        {
            get { return Spheres.Count == 0 && Capsules.Count == 0 && Boxes.Count == 0 && Ground == null && Water == null; }
        }

        /// <summary>
        /// Pushes a particle out of every solid it has entered, with Coulomb friction
        /// against the solid's own motion. Returns true if anything was touched.
        /// </summary>
        internal bool Resolve(ref Vec3 p, Vec3 prev, float radius, float dt)
        {
            var hit = false;
            for (var i = 0; i < Spheres.Count; i++)
            {
                var s = Spheres[i];
                var d = p - s.Centre;
                var r = s.Radius + radius;
                var l2 = d.SqrLength;
                if (l2 >= r * r) continue;
                var l = MathX.Sqrt(l2);
                var n = l > 1e-6f ? d / l : Vec3.UnitY;
                Push(ref p, prev, n, r - l, s.Velocity, dt);
                hit = true;
            }
            for (var i = 0; i < Capsules.Count; i++)
            {
                var c = Capsules[i];
                var ab = c.B - c.A;
                var l2ab = ab.SqrLength;
                var t = l2ab > 1e-9f ? MathX.Clamp01(Vec3.Dot(p - c.A, ab) / l2ab) : 0f;
                var closest = c.A + ab * t;
                var d = p - closest;
                var r = c.Radius + radius;
                var l2 = d.SqrLength;
                if (l2 >= r * r) continue;
                var l = MathX.Sqrt(l2);
                var n = l > 1e-6f ? d / l : Vec3.AnyPerpendicular(ab.NormalizedOr(Vec3.UnitY));
                Push(ref p, prev, n, r - l, c.Velocity, dt);
                hit = true;
            }
            for (var i = 0; i < Boxes.Count; i++)
            {
                var b = Boxes[i];
                var d = p - b.Centre;
                var lx = Vec3.Dot(d, b.AxisX);
                var ly = Vec3.Dot(d, b.AxisY);
                var lz = Vec3.Dot(d, b.AxisZ);
                var ex = b.HalfExtents.X + radius;
                var ey = b.HalfExtents.Y + radius;
                var ez = b.HalfExtents.Z + radius;
                var px = ex - Math.Abs(lx);
                var py = ey - Math.Abs(ly);
                var pz = ez - Math.Abs(lz);
                if (px <= 0f || py <= 0f || pz <= 0f) continue;
                Vec3 n;
                float depth;
                if (px <= py && px <= pz) { n = b.AxisX * (lx >= 0f ? 1f : -1f); depth = px; }
                else if (py <= pz) { n = b.AxisY * (ly >= 0f ? 1f : -1f); depth = py; }
                else { n = b.AxisZ * (lz >= 0f ? 1f : -1f); depth = pz; }
                Push(ref p, prev, n, depth, b.Velocity, dt);
                hit = true;
            }
            if (Ground != null)
            {
                var c = Ground.Clearance(p) - radius;
                if (c < 0f)
                {
                    PushWithFriction(ref p, prev, Ground.Up, -c, Ground.SurfaceVelocity, dt, Ground.Friction);
                    hit = true;
                }
            }
            return hit;
        }

        private void Push(ref Vec3 p, Vec3 prev, Vec3 n, float depth, Vec3 surfaceVel, float dt)
        {
            PushWithFriction(ref p, prev, n, depth, surfaceVel, dt, Friction);
        }

        private static void PushWithFriction(ref Vec3 p, Vec3 prev, Vec3 n, float depth, Vec3 surfaceVel, float dt, float mu)
        {
            p += n * depth;
            // Friction acts on this substep's motion relative to the surface, limited by
            // how hard the particle was pressed in.
            var motion = (p - prev) - surfaceVel * dt;
            var tangential = motion - n * Vec3.Dot(motion, n);
            var tl = tangential.Length;
            if (tl < 1e-9f) return;
            var maxFriction = mu * depth;
            p -= tl <= maxFriction ? tangential : tangential * (maxFriction / tl);
        }
    }
}
