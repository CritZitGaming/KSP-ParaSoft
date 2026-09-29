using ParaSoft.Core;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>The boundary between the Unity-free solver and the game. Same three floats either way.</summary>
    internal static class Conversions
    {
        internal static Vec3 ToVec3(this Vector3 v) { return new Vec3(v.x, v.y, v.z); }
        internal static Vec3 ToVec3(this Vector3d v) { return new Vec3((float)v.x, (float)v.y, (float)v.z); }
        internal static Vector3 ToVector3(this Vec3 v) { return new Vector3(v.X, v.Y, v.Z); }
        internal static Quat ToQuat(this Quaternion q) { return new Quat(q.x, q.y, q.z, q.w); }
    }
}
