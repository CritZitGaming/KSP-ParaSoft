using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ParaSoft.Environment
{
    /// <summary>
    /// Ocean wave heights from Scatterer, for any point, not just parts.
    ///
    /// Scatterer simulates its waves on the GPU and, when its craft wave interactions are
    /// turned on, answers "how high is the water here?" for every floating part with a
    /// compute shader (FindHeights) and an async readback. A canopy lying on the sea is
    /// not a part, so this runs its own copy of that same shader over a small grid under
    /// the canopy, fed the same wave textures and parameters Scatterer feeds its own.
    /// Scatterer's own queries are untouched: the shader is instantiated, so its buffer
    /// bindings are separate.
    ///
    /// Everything here is found by reflection against the installed Scatterer (checked
    /// against v0.0838+), and any mismatch or error turns the feature off for the session.
    /// Canopies then fall back to the water level Scatterer writes into the parachute
    /// part's own PartBuoyancy - they still bob, they just do not tilt with each wave.
    /// </summary>
    internal static class ScattererWaves
    {
        internal const int GridSize = 8; // 64 points: one thread group of the shader

        private static bool resolved, broken;
        private static Type oceanType;
        private static FieldInfo handlerField, choppyField, gridField, map0Field, map3Field, map4Field, idxField, uxField, uyField, shaderField;
        private static PropertyInfo offsetProp, instanceProp;
        private static FieldInfo nearCameraField;
        private static MethodInfo toVector3;

        private static Object ocean;
        private static float nextSearch;
        private static ComputeShader shader;
        private static ComputeShader shaderSource;

        internal static bool Available
        {
            get
            {
                if (broken || !ParaSoftParameters.Waves) return false;
                if (!resolved) Resolve();
                return !broken && oceanType != null && SystemInfo.supportsAsyncGPUReadback && SystemInfo.supportsComputeShaders;
            }
        }

        private static void Resolve()
        {
            resolved = true;
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    var t = la.assembly.GetType("Scatterer.OceanFFTgpu");
                    if (t == null) continue;
                    const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    oceanType = t;
                    handlerField = t.GetField("waveInteractionHandler", f);
                    choppyField = t.GetField("m_choppyness", f);
                    gridField = t.GetField("m_gridSizes", f);
                    map0Field = t.GetField("m_fourierBuffer0", f);
                    map3Field = t.GetField("m_fourierBuffer3", f);
                    map4Field = t.GetField("m_fourierBuffer4", f);
                    idxField = t.GetField("m_idx", f);
                    uxField = t.BaseType.GetField("ux", f);
                    uyField = t.BaseType.GetField("uy", f);
                    offsetProp = t.BaseType.GetProperty("OffsetVector3", f);
                    var handlerType = la.assembly.GetType("Scatterer.GPUWaveInteractionHandler");
                    shaderField = handlerType != null ? handlerType.GetField("findHeightsShader", f) : null;
                    var main = la.assembly.GetType("Scatterer.Scatterer");
                    instanceProp = main != null ? main.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static) : null;
                    nearCameraField = main != null ? main.GetField("nearCamera", f) : null;
                    toVector3 = uxField != null ? uxField.FieldType.GetMethod("ToVector3", Type.EmptyTypes) : null;

                    if (handlerField == null || choppyField == null || gridField == null || map0Field == null ||
                        map3Field == null || map4Field == null || idxField == null || uxField == null ||
                        uyField == null || offsetProp == null || shaderField == null || instanceProp == null ||
                        nearCameraField == null || toVector3 == null)
                    {
                        Log.Warn("Scatterer found, but not the version ParaSoft knows; canopies will ride the water level only.");
                        broken = true;
                    }
                    else Log.Info("Scatterer ocean waves available.");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Exception("looking for Scatterer", e);
                broken = true;
            }
        }

        /// <summary>A grid of wave heights under one canopy, filled in a few frames after it is asked for.</summary>
        internal sealed class Query : IDisposable
        {
            internal readonly float[] Heights = new float[GridSize * GridSize];
            internal Vector3d CentreBodyRelative;   // where the grid was taken, relative to the body centre
            internal Vector3 East, North;
            internal float Cell;
            internal bool HasResult;
            internal bool Pending;
            private ComputeBuffer positions, results;
            private readonly Vector2[] oceanPositions = new Vector2[GridSize * GridSize];
            private readonly Vector3d[] pendingCentre = new Vector3d[1];
            private Vector3 pendingEast, pendingNorth;
            private float pendingCell;

            /// <summary>Asks for the heights around a sea-level point. Returns false if it cannot.</summary>
            internal bool Submit(CelestialBody body, Vector3 centreWorld, Vector3 up, float cell)
            {
                if (Pending || !Available) return false;
                try
                {
                    var node = FindOcean();
                    if (node == null) return false;
                    var nearCam = nearCameraField.GetValue(instanceProp.GetValue(null, null)) as Camera;
                    if (nearCam == null) return false;
                    if (!PrepareShader(node)) return false;

                    var ux = (Vector3)toVector3.Invoke(uxField.GetValue(node), null);
                    var uy = (Vector3)toVector3.Invoke(uyField.GetValue(node), null);
                    var offset = (Vector3)offsetProp.GetValue(node, null);
                    var cam = nearCam.transform.position;

                    var east = Vector3.Cross(up, Vector3.Cross(Vector3.up, up)).normalized;
                    if (east.sqrMagnitude < 0.5f) east = Vector3.Cross(up, Vector3.right).normalized;
                    var north = Vector3.Cross(up, east);
                    var half = (GridSize - 1) * 0.5f;
                    for (var j = 0; j < GridSize; j++)
                        for (var i = 0; i < GridSize; i++)
                        {
                            var p = centreWorld + east * ((i - half) * cell) + north * ((j - half) * cell) - cam;
                            oceanPositions[j * GridSize + i] = new Vector2(Vector3.Dot(p, ux) + offset.x, Vector3.Dot(p, uy) + offset.y);
                        }

                    if (positions == null)
                    {
                        positions = new ComputeBuffer(GridSize * GridSize, 2 * sizeof(float));
                        results = new ComputeBuffer(GridSize * GridSize, sizeof(float));
                    }
                    positions.SetData(oceanPositions);
                    shader.SetBuffer(0, "positions", positions);
                    shader.SetBuffer(0, "result", results);
                    shader.SetInt("positionsCount", GridSize * GridSize);
                    shader.Dispatch(0, 1, 1, 1);

                    pendingCentre[0] = (Vector3d)centreWorld - body.position;
                    pendingEast = east;
                    pendingNorth = north;
                    pendingCell = cell;
                    Pending = true;
                    AsyncGPUReadback.Request(results, OnReadback);
                    return true;
                }
                catch (Exception e)
                {
                    Log.Exception("querying Scatterer's waves; wave sampling turned off", e);
                    broken = true;
                    Pending = false;
                    return false;
                }
            }

            private void OnReadback(AsyncGPUReadbackRequest request)
            {
                Pending = false;
                if (request.hasError || positions == null) return;
                var data = request.GetData<float>();
                if (data.Length < Heights.Length) return;
                for (var i = 0; i < Heights.Length; i++)
                {
                    var h = data[i];
                    Heights[i] = float.IsNaN(h) || float.IsInfinity(h) ? 0f : Mathf.Clamp(h, -50f, 50f);
                }
                CentreBodyRelative = pendingCentre[0];
                East = pendingEast;
                North = pendingNorth;
                Cell = pendingCell;
                HasResult = true;
            }

            public void Dispose()
            {
                if (positions != null) positions.Release();
                if (results != null) results.Release();
                positions = results = null;
            }
        }

        private static Object FindOcean()
        {
            if (ocean != null && handlerField.GetValue(ocean) != null) return ocean;
            if (Time.realtimeSinceStartup < nextSearch) return null;
            nextSearch = Time.realtimeSinceStartup + 3f;
            ocean = null;
            foreach (var o in Object.FindObjectsOfType(oceanType))
            {
                // Only the ocean that is simulating wave interactions has live buffers.
                if (handlerField.GetValue(o) != null) { ocean = o; break; }
            }
            return ocean;
        }

        private static bool PrepareShader(Object node)
        {
            var source = shaderField.GetValue(handlerField.GetValue(node)) as ComputeShader;
            if (source == null) return false;
            if (shader == null || !ReferenceEquals(source, shaderSource))
            {
                shaderSource = source;
                shader = Object.Instantiate(source);
            }
            var idx = (int)idxField.GetValue(node);
            var m0 = map0Field.GetValue(node) as RenderTexture[];
            var m3 = map3Field.GetValue(node) as RenderTexture[];
            var m4 = map4Field.GetValue(node) as RenderTexture[];
            if (m0 == null || m3 == null || m4 == null || idx < 0 || idx >= m0.Length) return false;
            shader.SetVector("_Ocean_Choppyness", (Vector4)choppyField.GetValue(node));
            shader.SetVector("_Ocean_GridSizes", (Vector4)gridField.GetValue(node));
            shader.SetTexture(0, "_Ocean_Map0", m0[idx]);
            shader.SetTexture(0, "_Ocean_Map3", m3[idx]);
            shader.SetTexture(0, "_Ocean_Map4", m4[idx]);
            return true;
        }
    }
}
