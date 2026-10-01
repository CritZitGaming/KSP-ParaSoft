using System.Collections.Generic;

namespace ParaSoft.Core
{
    /// <summary>
    /// Where the risers of one canopy, or of a cluster sharing a frame, run right now: from
    /// the part (or wherever a cut riser trails to), through the junction a cluster's risers
    /// may share, to each canopy's confluence. The riser is not simulated as cloth - it is a
    /// strap held straight by the canopy's pull - so this is worked out from the canopies.
    /// </summary>
    public static class RiserPath
    {
        /// <param name="ends">Receives each canopy's riser end, one per sim.</param>
        /// <param name="junction">Where the risers join: out from the part along their mean pull.</param>
        public static void Locate(IList<CanopySim> sims, Vec3[] ends, out Vec3 junction)
        {
            junction = Vec3.Zero;
            if (sims == null || sims.Count == 0) return;
            var first = sims[0];
            for (var i = 0; i < sims.Count; i++) ends[i] = sims[i] != null ? sims[i].RiserEnd : Vec3.Zero;
            if (first == null) return;
            var rest = first.Lattice.Shape.RiserJunction;
            var length = rest.Length;
            if (length < 1e-3f)
            {
                junction = ends[0];
                return;
            }

            // The shared strap points along the canopies' combined pull.
            var conf = Vec3.Zero;
            var centre = Vec3.Zero;
            var upper = 0f;
            var count = 0;
            foreach (var s in sims)
            {
                if (s == null) continue;
                conf += s.Positions[s.Lattice.ConfluenceIndex];
                centre += s.BubbleCentre;
                upper += Vec3.Distance(s.Lattice.Shape.Confluence, s.Lattice.Shape.RiserJunction);
                count++;
            }
            conf = conf / count;
            if (first.Anchored)
            {
                junction = conf.NormalizedOr(rest / length) * length;
                return;
            }

            // Cut loose: the strap trails below the confluences, away from the canopies.
            var away = (conf - centre / count).NormalizedOr(-(rest / length));
            junction = conf + away * (upper / count);
            var end = junction + away * length;
            for (var i = 0; i < sims.Count; i++) ends[i] = end;
        }
    }
}
