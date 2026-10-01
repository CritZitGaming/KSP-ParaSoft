using System.Collections.Generic;

namespace ParaSoft.Core
{
    /// <summary>
    /// Lets canopies that live in different simulation frames - each hangs from its own
    /// part, and cut ones fly free - feel each other. Once a frame every open canopy's
    /// contact volume is gathered into one shared frame (the game's world space); then,
    /// just before it steps, each canopy is handed the others near it, moved into its own
    /// frame, as its Neighbours.
    /// </summary>
    public sealed class CanopyContacts
    {
        private struct Entry
        {
            internal CanopySim Sim;
            internal Vec3 A, B, Velocity;
            internal float Radius;
        }

        private readonly List<Entry> entries = new List<Entry>();

        public int Count { get { return entries.Count; } }

        public void Clear()
        {
            entries.Clear();
        }

        /// <param name="frameOrigin">Where the canopy's frame origin is, in the shared frame.</param>
        /// <param name="frameVelocity">How fast that origin is moving, in the shared frame.</param>
        public void Add(CanopySim sim, Vec3 frameOrigin, Vec3 frameVelocity)
        {
            Vec3 a, b;
            float r;
            if (!sim.ContactCapsule(out a, out b, out r)) return;
            entries.Add(new Entry
            {
                Sim = sim,
                A = frameOrigin + a,
                B = frameOrigin + b,
                Radius = r,
                Velocity = frameVelocity + sim.MeanVelocity
            });
        }

        /// <summary>Sets sim.Neighbours to every other gathered canopy close enough to touch it.</summary>
        public void Fill(CanopySim sim, Vec3 frameOrigin, Vec3 frameVelocity)
        {
            sim.Neighbours.Clear();
            if (entries.Count < 2) return;
            Vec3 a, b;
            float r;
            if (!sim.ContactCapsule(out a, out b, out r)) return;
            a += frameOrigin;
            b += frameOrigin;
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (ReferenceEquals(e.Sim, sim)) continue;
                Vec3 pa, pb;
                Geometry.ClosestPoints(a, b, e.A, e.B, out pa, out pb);
                var reach = (r + e.Radius) * 1.25f;
                if ((pa - pb).SqrLength > reach * reach) continue;
                sim.Neighbours.Add(new CollisionCapsule
                {
                    A = e.A - frameOrigin,
                    B = e.B - frameOrigin,
                    Radius = e.Radius,
                    Velocity = e.Velocity - frameVelocity
                });
            }
        }
    }
}
