using System;
using ParaSoft.Core;
using UnityEngine;

namespace ParaSoft.Environment
{
    /// <summary>
    /// Builds a canopy's CollisionWorld each physics frame: the ground under it, the sea,
    /// and every collider it could touch, all in the canopy's frame (world axes, origin at
    /// its anchor).
    ///
    /// The ground is sampled by raycasting a small grid onto the scenery layers, which
    /// picks up PQS terrain, KSC buildings and Parallax's collideable scatter alike - a
    /// canopy can come down on the VAB roof. Part colliders become spheres, capsules and
    /// oriented boxes; a convex mesh collider is approximated by its mesh bounds, which is
    /// generous by a few centimetres on most parts and never lets fabric through.
    /// </summary>
    internal sealed class Surroundings : IDisposable
    {
        private const int TerrainGrid = 5;
        private const int SceneryMask = (1 << 15) | (1 << 28);
        private const int SolidsMask = (1 << 0) | (1 << 16) | (1 << 17) | (1 << 19);
        private const int MaxSolids = 48;

        private static readonly Collider[] overlap = new Collider[128];

        internal readonly CollisionWorld World = new CollisionWorld();
        private readonly HeightField terrain = new HeightField(TerrainGrid);
        private readonly HeightField water = new HeightField(ScattererWaves.GridSize);
        private readonly ScattererWaves.Query waves = new ScattererWaves.Query();
        private Vector3d terrainCentreBodyRel;
        private float terrainAge = float.MaxValue;
        private bool haveTerrain;
        private float waveAge = float.MaxValue;

        /// <param name="centre">Centre of the canopy, world space.</param>
        /// <param name="radius">Radius that holds all of it.</param>
        /// <param name="anchor">The simulation frame's origin, world space.</param>
        /// <param name="frameVelocity">The frame's velocity (Unity + Krakensbane).</param>
        /// <param name="ownPart">Colliders on this part are ignored while the craft is airborne.</param>
        internal void Update(Vessel vessel, Part ownPart, CelestialBody body, Vector3 centre, float radius,
                             Vector3 anchor, Vector3 frameVelocity, float dt, bool includeOwnPart)
        {
            World.Spheres.Clear();
            World.Capsules.Clear();
            World.Boxes.Clear();
            World.Ground = null;
            World.Water = null;
            if (body == null) return;

            var up = (centre - (Vector3)body.position).normalized;
            var kb = Krakensbane.GetFrameVelocityV3f();
            var staticVelocity = kb - frameVelocity;
            var altitude = (float)(((Vector3d)centre - body.position).magnitude - body.Radius);

            if (ParaSoftParameters.SurfaceCollisions)
            {
                UpdateTerrain(body, centre, radius, up, altitude, anchor, staticVelocity, dt);
                UpdateWater(ownPart, body, centre, radius, up, altitude, anchor, staticVelocity, dt);
            }
            if (ParaSoftParameters.PartCollisions)
                GatherSolids(ownPart, centre, radius, anchor, frameVelocity, kb, includeOwnPart);
        }

        private void UpdateTerrain(CelestialBody body, Vector3 centre, float radius, Vector3 up, float altitude,
                                   Vector3 anchor, Vector3 staticVelocity, float dt)
        {
            // Far above anything a canopy could reach, skip the raycasts altogether.
            var terrainAlt = body.pqsController != null
                ? (float)(body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(body.GetLatitude(centre, false), body.GetLongitude(centre, false))) - body.Radius)
                : 0f;
            if (altitude - terrainAlt > 2f * radius + 150f)
            {
                haveTerrain = false;
                return;
            }

            terrainAge += dt;
            var centreBodyRel = (Vector3d)centre - body.position;
            var cell = Mathf.Max(1f, radius * 2f / (TerrainGrid - 1));
            var moved = (float)(centreBodyRel - terrainCentreBodyRel).magnitude;
            if (terrainAge > 0.3f || moved > cell * 0.5f || !haveTerrain)
            {
                terrainAge = 0f;
                terrainCentreBodyRel = centreBodyRel;
                var east = Vector3.Cross(up, Vector3.Cross(Vector3.up, up)).normalized;
                if (east.sqrMagnitude < 0.5f) east = Vector3.Cross(up, Vector3.right).normalized;
                var north = Vector3.Cross(up, east);
                var half = (TerrainGrid - 1) * 0.5f;
                var top = radius * 2f + 200f;
                var hits = 0;
                float sum = 0f;
                for (var j = 0; j < TerrainGrid; j++)
                {
                    for (var i = 0; i < TerrainGrid; i++)
                    {
                        var p = centre + east * ((i - half) * cell) + north * ((j - half) * cell);
                        RaycastHit hit;
                        float h;
                        if (Physics.Raycast(p + up * top, -up, out hit, top * 2f + 2000f, SceneryMask, QueryTriggerInteraction.Ignore))
                        {
                            h = Vector3.Dot(hit.point - centre, up);
                            hits++;
                            sum += h;
                        }
                        else h = float.NaN;
                        terrain.Heights[j * TerrainGrid + i] = h;
                    }
                }
                haveTerrain = hits > 0;
                if (haveTerrain)
                {
                    // Missed samples (over the sea, off a cliff edge) take the average.
                    var avg = sum / hits;
                    for (var k = 0; k < terrain.Heights.Length; k++)
                        if (float.IsNaN(terrain.Heights[k])) terrain.Heights[k] = avg;
                }
                terrain.East = east.ToVec3();
                terrain.North = north.ToVec3();
                terrain.Up = up.ToVec3();
                terrain.Cell = cell;
            }
            if (!haveTerrain) return;
            var origin = (Vector3)(body.position + terrainCentreBodyRel) - anchor;
            terrain.Origin = origin.ToVec3();
            terrain.SurfaceVelocity = staticVelocity.ToVec3();
            terrain.Friction = 0.7f;
            World.Ground = terrain;
        }

        private void UpdateWater(Part ownPart, CelestialBody body, Vector3 centre, float radius, Vector3 up, float altitude,
                                 Vector3 anchor, Vector3 staticVelocity, float dt)
        {
            if (!body.ocean || altitude > 2f * radius + 60f) return;

            // What Scatterer (or nobody) says the water level is at the parachute part.
            var baseLevel = 0f;
            if (ownPart != null && ownPart.partBuoyancy != null)
            {
                var wl = (float)ownPart.partBuoyancy.waterLevel;
                if (!float.IsNaN(wl) && Mathf.Abs(wl) < 50f) baseLevel = wl;
            }

            // The canopy centre projected down onto sea level.
            var seaCentre = centre - up * altitude;

            waveAge += dt;
            if (ScattererWaves.Available && waveAge > 0.1f && !waves.Pending)
            {
                waveAge = 0f;
                waves.Submit(body, seaCentre, up, Mathf.Max(1f, radius * 2.2f / (ScattererWaves.GridSize - 1)));
            }

            if (waves.HasResult)
            {
                var origin = (Vector3)(body.position + waves.CentreBodyRelative);
                water.Origin = (origin - anchor).ToVec3();
                water.East = waves.East.ToVec3();
                water.North = waves.North.ToVec3();
                water.Cell = waves.Cell;
                Array.Copy(waves.Heights, water.Heights, water.Heights.Length);
            }
            else
            {
                water.Origin = (seaCentre - anchor).ToVec3();
                var east = Vector3.Cross(up, Vector3.Cross(Vector3.up, up)).normalized;
                if (east.sqrMagnitude < 0.5f) east = Vector3.Cross(up, Vector3.right).normalized;
                water.East = east.ToVec3();
                water.North = Vector3.Cross(up, east).ToVec3();
                water.Cell = 1e4f;
                for (var k = 0; k < water.Heights.Length; k++) water.Heights[k] = baseLevel;
            }
            water.Up = up.ToVec3();
            water.SurfaceVelocity = staticVelocity.ToVec3();
            water.IsWater = true;
            World.Water = water;
        }

        private void GatherSolids(Part ownPart, Vector3 centre, float radius, Vector3 anchor, Vector3 frameVelocity,
                                  Vector3 kb, bool includeOwnPart)
        {
            var count = Physics.OverlapSphereNonAlloc(centre, radius + 1f, overlap, SolidsMask, QueryTriggerInteraction.Ignore);
            var added = 0;
            for (var i = 0; i < count && added < MaxSolids; i++)
            {
                var c = overlap[i];
                // Wheel colliders and anything else exotic fall through the shape tests below.
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (!includeOwnPart && ownPart != null && c.transform.IsChildOf(ownPart.transform)) continue;

                var rb = c.attachedRigidbody;
                var t = c.transform;
                var cVel = (rb != null ? rb.GetPointVelocity(c.bounds.center) : Vector3.zero) + kb - frameVelocity;
                var vel = cVel.ToVec3();
                var scale = t.lossyScale;
                var maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));

                var sphere = c as SphereCollider;
                if (sphere != null)
                {
                    World.Spheres.Add(new CollisionSphere
                    {
                        Centre = (t.TransformPoint(sphere.center) - anchor).ToVec3(),
                        Radius = sphere.radius * maxScale,
                        Velocity = vel
                    });
                    added++;
                    continue;
                }

                var capsule = c as CapsuleCollider;
                if (capsule != null)
                {
                    var dir = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                    var axisScale = Mathf.Abs(Vector3.Scale(dir, scale).magnitude);
                    var r = capsule.radius * maxScale;
                    var half = Mathf.Max(0f, capsule.height * axisScale * 0.5f - r);
                    var cc = t.TransformPoint(capsule.center);
                    var wdir = t.TransformDirection(dir).normalized;
                    World.Capsules.Add(new CollisionCapsule
                    {
                        A = (cc - wdir * half - anchor).ToVec3(),
                        B = (cc + wdir * half - anchor).ToVec3(),
                        Radius = r,
                        Velocity = vel
                    });
                    added++;
                    continue;
                }

                Vector3 localCentre, localSize;
                var box = c as BoxCollider;
                if (box != null)
                {
                    localCentre = box.center;
                    localSize = box.size;
                }
                else
                {
                    var mesh = c as MeshCollider;
                    if (mesh == null || mesh.sharedMesh == null) continue;
                    var b = mesh.sharedMesh.bounds;
                    localCentre = b.center;
                    localSize = b.size;
                }
                World.Boxes.Add(new CollisionBox
                {
                    Centre = (t.TransformPoint(localCentre) - anchor).ToVec3(),
                    AxisX = t.right.ToVec3(),
                    AxisY = t.up.ToVec3(),
                    AxisZ = t.forward.ToVec3(),
                    HalfExtents = new Vec3(Mathf.Abs(localSize.x * scale.x) * 0.5f, Mathf.Abs(localSize.y * scale.y) * 0.5f, Mathf.Abs(localSize.z * scale.z) * 0.5f),
                    Velocity = vel
                });
                added++;
            }
        }

        public void Dispose()
        {
            waves.Dispose();
        }
    }
}
