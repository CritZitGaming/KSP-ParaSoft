using System;
using System.Collections.Generic;
using ParaSoft.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParaSoft
{
    /// <summary>
    /// Draws a simulated canopy using the part's own canopy meshes and materials.
    ///
    /// Each original renderer gets a stand-in: a plain MeshRenderer on a dynamic copy of
    /// its mesh, whose vertices are moved every frame to follow the simulation. The stand-in
    /// uses the original's materials by reference, re-read every frame, so anything that
    /// swaps or tweaks them afterwards - Custom Parachute Message's encoded canopy shader,
    /// RealChute's texture picker, TexturesUnlimited recolours, KSP's own heat glow and
    /// highlighting - keeps working. The original is hidden with forceRenderingOff rather
    /// than disabled, so parachute modules that switch canopies on and off are unaffected.
    /// </summary>
    internal sealed class CanopyRenderer : IDisposable
    {
        private sealed class Proxy
        {
            internal Renderer Original;
            internal CanopySource Source;
            internal GameObject Go;
            internal MeshRenderer Renderer;
            internal Mesh Mesh;
            internal Vector3[] Vertices;
            internal Vector3[] Normals;
            internal Vector4[] Tangents;
            internal Material[] LastMaterials;
            internal int MaterialCheck;
        }

        private readonly CanopyModel model;
        private readonly List<Proxy> proxies = new List<Proxy>();
        private readonly Vec3[] positions;
        private readonly Vec3[] normals;
        private readonly float[] tangents;
        private readonly Vec3[][] canopyPositions;
        private readonly Vec3[][] canopyNormals;
        private readonly float[][] canopyTangents;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private bool visible;
        private bool originalsHidden;
        private int[] versions;
        private bool firstFrame = true;

        internal CanopyRenderer(CanopyModel model, List<Renderer> originals, string name)
        {
            this.model = model;
            positions = new Vec3[model.VertexCount];
            normals = new Vec3[model.VertexCount];
            tangents = new float[model.VertexCount * 4];
            canopyPositions = new Vec3[model.Members.Length][];
            canopyNormals = new Vec3[model.Members.Length][];
            canopyTangents = new float[model.Members.Length][];
            for (var c = 0; c < model.Members.Length; c++)
            {
                canopyPositions[c] = new Vec3[model.Members[c].Length];
                canopyNormals[c] = new Vec3[model.Members[c].Length];
                canopyTangents[c] = new float[model.Members[c].Length * 4];
            }

            for (var i = 0; i < model.Sources.Length && i < originals.Count; i++)
            {
                var src = model.Sources[i];
                var orig = originals[i];
                var p = new Proxy { Original = orig, Source = src };
                p.Go = new GameObject("ParaSoft canopy (" + name + ")");
                p.Go.layer = orig.gameObject.layer;
                p.Go.SetActive(false);
                var mf = p.Go.AddComponent<MeshFilter>();
                p.Renderer = p.Go.AddComponent<MeshRenderer>();
                p.Renderer.shadowCastingMode = orig.shadowCastingMode;
                p.Renderer.receiveShadows = orig.receiveShadows;
                p.Renderer.sharedMaterials = orig.sharedMaterials;
                p.LastMaterials = orig.sharedMaterials;

                p.Mesh = new Mesh { name = "ParaSoft " + orig.name };
                p.Mesh.MarkDynamic();
                if (src.Count > 65000) p.Mesh.indexFormat = IndexFormat.UInt32;
                p.Vertices = new Vector3[src.Count];
                p.Mesh.vertices = p.Vertices;
                if (src.Uv != null && src.Uv.Length == src.Count) p.Mesh.uv = src.Uv;
                if (src.Uv2 != null && src.Uv2.Length == src.Count) p.Mesh.uv2 = src.Uv2;
                if (src.Colors != null && src.Colors.Length == src.Count) p.Mesh.colors32 = src.Colors;
                p.Mesh.subMeshCount = src.Submeshes.Length;
                for (var s = 0; s < src.Submeshes.Length; s++) p.Mesh.SetTriangles(src.Submeshes[s], s, false);
                p.Normals = new Vector3[src.Count];
                if (src.HasTangents) p.Tangents = new Vector4[src.Count];
                mf.sharedMesh = p.Mesh;
                proxies.Add(p);
            }
        }

        /// <summary>
        /// Moves every stand-in vertex to follow the simulated canopies. The simulations
        /// share one frame - world axes, origin at the anchor - so that is where the
        /// stand-ins are placed.
        /// </summary>
        internal void Update(CanopySim[] sims, Vector3 anchorWorld)
        {
            // Physics steps at 50 Hz and rendering usually runs faster: only rebuild the
            // meshes when a simulation has actually moved. And a canopy no camera saw last
            // frame only needs its bounds kept honest (from the lattice, which is cheap) so
            // that culling notices when it comes back into view.
            var changed = versions == null || versions.Length != sims.Length;
            if (changed) versions = new int[sims.Length];
            for (var c = 0; c < sims.Length; c++)
            {
                if (sims[c] != null && sims[c].Version != versions[c])
                {
                    versions[c] = sims[c].Version;
                    changed = true;
                }
            }
            var seen = false;
            foreach (var p in proxies)
                if (p.Renderer != null && p.Renderer.isVisible) seen = true;

            foreach (var p in proxies)
            {
                if (p.Go == null) continue;
                p.Go.transform.position = anchorWorld;
                p.Go.transform.rotation = Quaternion.identity;
                SyncLook(p);
            }
            if (!changed) return;
            if (!seen && visible && !firstFrame)
            {
                var lb = LatticeBounds(sims);
                foreach (var p in proxies) if (p.Mesh != null) p.Mesh.bounds = lb;
                return;
            }
            firstFrame = false;

            for (var c = 0; c < sims.Length; c++)
            {
                if (sims[c] == null) continue;
                model.Embeddings[c].Evaluate(sims[c].Positions, canopyPositions[c], canopyNormals[c], canopyTangents[c]);
                var members = model.Members[c];
                for (var k = 0; k < members.Length; k++)
                {
                    var g = members[k];
                    positions[g] = canopyPositions[c][k];
                    normals[g] = canopyNormals[c][k];
                    tangents[g * 4] = canopyTangents[c][k * 4];
                    tangents[g * 4 + 1] = canopyTangents[c][k * 4 + 1];
                    tangents[g * 4 + 2] = canopyTangents[c][k * 4 + 2];
                    tangents[g * 4 + 3] = canopyTangents[c][k * 4 + 3];
                }
            }

            foreach (var p in proxies)
            {
                if (p.Go == null) continue;
                var src = p.Source;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (var i = 0; i < src.Count; i++)
                {
                    var v = positions[src.Offset + i];
                    var u = new Vector3(v.X, v.Y, v.Z);
                    p.Vertices[i] = u;
                    min = Vector3.Min(min, u);
                    max = Vector3.Max(max, u);
                    if (src.HasNormals)
                    {
                        var n = normals[src.Offset + i];
                        p.Normals[i] = new Vector3(n.X, n.Y, n.Z);
                    }
                    if (p.Tangents != null)
                    {
                        var t = (src.Offset + i) * 4;
                        p.Tangents[i] = new Vector4(tangents[t], tangents[t + 1], tangents[t + 2], tangents[t + 3]);
                    }
                }
                p.Mesh.vertices = p.Vertices;
                if (src.HasNormals) p.Mesh.normals = p.Normals;
                else p.Mesh.RecalculateNormals();
                if (p.Tangents != null) p.Mesh.tangents = p.Tangents;
                p.Mesh.bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one);
            }
        }

        private static Bounds LatticeBounds(CanopySim[] sims)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var s in sims)
            {
                if (s == null) continue;
                var pos = s.Positions;
                for (var i = 0; i < pos.Length; i++)
                {
                    var u = new Vector3(pos[i].X, pos[i].Y, pos[i].Z);
                    min = Vector3.Min(min, u);
                    max = Vector3.Max(max, u);
                }
            }
            return new Bounds((min + max) * 0.5f, max - min + Vector3.one * 2f);
        }

        private void SyncLook(Proxy p)
        {
            var orig = p.Original;
            if (orig == null) return;
            // Swapped materials: cheap reference check on the first, full check now and then.
            var first = orig.sharedMaterial;
            if (p.LastMaterials.Length == 0 || !ReferenceEquals(first, p.LastMaterials[0]) || ++p.MaterialCheck >= 30)
            {
                p.MaterialCheck = 0;
                var mats = orig.sharedMaterials;
                if (!SameMaterials(mats, p.LastMaterials))
                {
                    p.Renderer.sharedMaterials = mats;
                    p.LastMaterials = mats;
                }
            }
            orig.GetPropertyBlock(block);
            p.Renderer.SetPropertyBlock(block);
            p.Go.layer = orig.gameObject.layer;
        }

        private static bool SameMaterials(Material[] a, Material[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>
        /// Shows the stand-ins. hideOriginals is false once a canopy is cut: the module has
        /// switched its own canopy off by then, and the next deployment must find it untouched.
        /// </summary>
        internal void Show(bool show, bool hideOriginals)
        {
            if (show != visible)
            {
                visible = show;
                // Whatever the mesh last held belongs to an earlier deployment.
                if (show) firstFrame = true;
                foreach (var p in proxies) if (p.Go != null) p.Go.SetActive(show);
            }
            SetOriginalsHidden(show && hideOriginals);
        }

        private void SetOriginalsHidden(bool hide)
        {
            if (hide == originalsHidden) return;
            originalsHidden = hide;
            foreach (var p in proxies)
                if (p.Original != null) p.Original.forceRenderingOff = hide;
        }

        public void Dispose()
        {
            SetOriginalsHidden(false);
            foreach (var p in proxies)
            {
                if (p.Go != null) UnityEngine.Object.Destroy(p.Go);
                if (p.Mesh != null) UnityEngine.Object.Destroy(p.Mesh);
            }
            proxies.Clear();
        }
    }
}
