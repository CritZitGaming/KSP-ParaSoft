namespace ParaSoft.Core
{
    /// <summary>
    /// How much of the wind is actually moving a craft.
    ///
    /// The canopy never pushes on its craft, so it has to agree with whatever the parachute
    /// module's own drag does to it, and wind is where the two can part ways: some
    /// aerodynamics apply all of a weather mod's wind to the parachute, some a fraction,
    /// some none. The craft's aerodynamic acceleration shows which: the air relative to the
    /// craft must be blowing the opposite way to the push it gives, so the fraction of the
    /// wind that lines the two up is the fraction the craft is feeling.
    /// </summary>
    public static class WindResponse
    {
        /// <param name="velocity">The craft's velocity through still air (wind not counted).</param>
        /// <param name="wind">The wind at the craft.</param>
        /// <param name="aeroAcceleration">The craft's acceleration from the air alone: not gravity, not thrust.</param>
        /// <returns>0..1, or -1 when the push says nothing about the wind (none, or no wind across it).</returns>
        public static float Factor(Vec3 velocity, Vec3 wind, Vec3 aeroAcceleration)
        {
            var push = aeroAcceleration.Length;
            if (push < 1e-3f) return -1f;
            var dir = aeroAcceleration / push;
            // Relative air is (velocity - f * wind); it should be anti-parallel to the push.
            // Least squares on the part of it across the push: |c0 - f c1|^2.
            var c0 = Vec3.Cross(velocity, dir);
            var c1 = Vec3.Cross(wind, dir);
            var c11 = c1.SqrLength;
            if (c11 < 0.01f * wind.SqrLength || c11 < 1e-6f) return -1f;
            var f = MathX.Clamp01(Vec3.Dot(c0, c1) / c11);
            // The air has to be pushing back, not along: otherwise something other than the
            // air (a missed engine, a collision) is behind the acceleration.
            if (Vec3.Dot(velocity - wind * f, dir) >= 0f) return -1f;
            return f;
        }
    }
}
