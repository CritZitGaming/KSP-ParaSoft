using System;

namespace ParaSoft.Core
{
    // The solver core is deliberately free of UnityEngine types. That is what lets
    // Tools/Test.ps1 compile it into a plain console program and drop canopies, fire
    // gusts at them and check the numbers, with no game running. The Unity layer converts
    // at the boundary (see Conversions.cs), which costs nothing measurable - these are
    // the same three floats either way.

    /// <summary>A 3-vector of floats with the handful of operations the cloth solver needs.</summary>
    public struct Vec3
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 UnitX = new Vec3(1f, 0f, 0f);
        public static readonly Vec3 UnitY = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 UnitZ = new Vec3(0f, 0f, 1f);

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }
        public static Vec3 operator *(Vec3 a, float s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3 operator *(float s, Vec3 a) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3 operator /(Vec3 a, float s) { var inv = 1f / s; return new Vec3(a.X * inv, a.Y * inv, a.Z * inv); }

        public static float Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        public float SqrLength { get { return X * X + Y * Y + Z * Z; } }
        public float Length { get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); } }

        public Vec3 Normalized
        {
            get
            {
                var l = Length;
                return l > 1e-12f ? this / l : Zero;
            }
        }

        /// <summary>Normalises, falling back to a given direction for a zero vector.</summary>
        public Vec3 NormalizedOr(Vec3 fallback)
        {
            var l = Length;
            return l > 1e-9f ? this / l : fallback;
        }

        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) { return a + (b - a) * t; }

        public static float Distance(Vec3 a, Vec3 b) { return (a - b).Length; }

        /// <summary>The component of v perpendicular to a unit axis.</summary>
        public static Vec3 Reject(Vec3 v, Vec3 unitAxis) { return v - unitAxis * Dot(v, unitAxis); }

        /// <summary>Any unit vector perpendicular to a unit vector.</summary>
        public static Vec3 AnyPerpendicular(Vec3 unit)
        {
            var other = Math.Abs(unit.X) < 0.9f ? UnitX : UnitY;
            return Cross(unit, other).Normalized;
        }

        public bool IsFinite
        {
            get
            {
                return !(float.IsNaN(X) || float.IsNaN(Y) || float.IsNaN(Z) ||
                         float.IsInfinity(X) || float.IsInfinity(Y) || float.IsInfinity(Z));
            }
        }

        public override string ToString()
        {
            return "(" + X.ToString("0.###") + ", " + Y.ToString("0.###") + ", " + Z.ToString("0.###") + ")";
        }
    }

    /// <summary>A unit quaternion, laid out and multiplied the way Unity's Quaternion is.</summary>
    public struct Quat
    {
        public float X, Y, Z, W;

        public Quat(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);

        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
                a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        public static Vec3 operator *(Quat q, Vec3 v)
        {
            // v' = v + 2w(q x v) + 2 q x (q x v)
            var qv = new Vec3(q.X, q.Y, q.Z);
            var t = Vec3.Cross(qv, v) * 2f;
            return v + t * q.W + Vec3.Cross(qv, t);
        }

        public Quat Normalized
        {
            get
            {
                var l = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return l > 1e-12f ? new Quat(X / l, Y / l, Z / l, W / l) : Identity;
            }
        }

        public Quat Inverse { get { return new Quat(-X, -Y, -Z, W); } }

        public static Quat AngleAxis(float radians, Vec3 unitAxis)
        {
            var h = radians * 0.5f;
            var s = (float)Math.Sin(h);
            return new Quat(unitAxis.X * s, unitAxis.Y * s, unitAxis.Z * s, (float)Math.Cos(h));
        }

        /// <summary>The shortest rotation taking unit vector a onto unit vector b.</summary>
        public static Quat FromTo(Vec3 a, Vec3 b)
        {
            var d = Vec3.Dot(a, b);
            if (d > 0.999999f) return Identity;
            if (d < -0.999999f) return AngleAxis((float)Math.PI, Vec3.AnyPerpendicular(a));
            var c = Vec3.Cross(a, b);
            return new Quat(c.X, c.Y, c.Z, 1f + d).Normalized;
        }
    }

    /// <summary>
    /// A 4x4 affine matrix, row-major, used only by the offline model tools and by the
    /// canopy capture to express baked vertices in the canopy's own frame.
    /// </summary>
    public struct Mat4
    {
        public float M00, M01, M02, M03;
        public float M10, M11, M12, M13;
        public float M20, M21, M22, M23;
        public float M30, M31, M32, M33;

        public static Mat4 Identity
        {
            get
            {
                var m = new Mat4();
                m.M00 = m.M11 = m.M22 = m.M33 = 1f;
                return m;
            }
        }

        public static Mat4 FromRowMajor(float[] v)
        {
            var m = new Mat4();
            m.M00 = v[0]; m.M01 = v[1]; m.M02 = v[2]; m.M03 = v[3];
            m.M10 = v[4]; m.M11 = v[5]; m.M12 = v[6]; m.M13 = v[7];
            m.M20 = v[8]; m.M21 = v[9]; m.M22 = v[10]; m.M23 = v[11];
            m.M30 = v[12]; m.M31 = v[13]; m.M32 = v[14]; m.M33 = v[15];
            return m;
        }

        public static Mat4 TRS(Vec3 t, Quat r, Vec3 s)
        {
            var x = r * Vec3.UnitX;
            var y = r * Vec3.UnitY;
            var z = r * Vec3.UnitZ;
            var m = new Mat4();
            m.M00 = x.X * s.X; m.M01 = y.X * s.Y; m.M02 = z.X * s.Z; m.M03 = t.X;
            m.M10 = x.Y * s.X; m.M11 = y.Y * s.Y; m.M12 = z.Y * s.Z; m.M13 = t.Y;
            m.M20 = x.Z * s.X; m.M21 = y.Z * s.Y; m.M22 = z.Z * s.Z; m.M23 = t.Z;
            m.M33 = 1f;
            return m;
        }

        public static Mat4 operator *(Mat4 a, Mat4 b)
        {
            var m = new Mat4();
            m.M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20 + a.M03 * b.M30;
            m.M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21 + a.M03 * b.M31;
            m.M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22 + a.M03 * b.M32;
            m.M03 = a.M00 * b.M03 + a.M01 * b.M13 + a.M02 * b.M23 + a.M03 * b.M33;
            m.M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20 + a.M13 * b.M30;
            m.M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31;
            m.M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32;
            m.M13 = a.M10 * b.M03 + a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33;
            m.M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20 + a.M23 * b.M30;
            m.M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31;
            m.M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32;
            m.M23 = a.M20 * b.M03 + a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33;
            m.M30 = a.M30 * b.M00 + a.M31 * b.M10 + a.M32 * b.M20 + a.M33 * b.M30;
            m.M31 = a.M30 * b.M01 + a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31;
            m.M32 = a.M30 * b.M02 + a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32;
            m.M33 = a.M30 * b.M03 + a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33;
            return m;
        }

        public Vec3 MultiplyPoint(Vec3 p)
        {
            return new Vec3(
                M00 * p.X + M01 * p.Y + M02 * p.Z + M03,
                M10 * p.X + M11 * p.Y + M12 * p.Z + M13,
                M20 * p.X + M21 * p.Y + M22 * p.Z + M23);
        }

        public Vec3 MultiplyVector(Vec3 p)
        {
            return new Vec3(
                M00 * p.X + M01 * p.Y + M02 * p.Z,
                M10 * p.X + M11 * p.Y + M12 * p.Z,
                M20 * p.X + M21 * p.Y + M22 * p.Z);
        }

        /// <summary>Inverse of a rigid transform (rotation + translation, no scale).</summary>
        public Mat4 RigidInverse
        {
            get
            {
                var m = new Mat4();
                m.M00 = M00; m.M01 = M10; m.M02 = M20;
                m.M10 = M01; m.M11 = M11; m.M12 = M21;
                m.M20 = M02; m.M21 = M12; m.M22 = M22;
                m.M03 = -(m.M00 * M03 + m.M01 * M13 + m.M02 * M23);
                m.M13 = -(m.M10 * M03 + m.M11 * M13 + m.M12 * M23);
                m.M23 = -(m.M20 * M03 + m.M21 * M13 + m.M22 * M23);
                m.M33 = 1f;
                return m;
            }
        }
    }

    /// <summary>Small scalar helpers that Mathf would otherwise provide.</summary>
    public static class MathX
    {
        public const float Pi = (float)Math.PI;
        public const float TwoPi = (float)(2.0 * Math.PI);

        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        public static float SmoothStep(float edge0, float edge1, float x)
        {
            var t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Wraps an angle into [0, 2pi).</summary>
        public static float WrapAngle(float a)
        {
            a %= TwoPi;
            return a < 0f ? a + TwoPi : a;
        }

        public static float Sqrt(float v) { return (float)Math.Sqrt(v); }
    }
}
