using System;
using System.Collections.Generic;
using ParaSoft.Core;
using ParaSoft.Hosts;
using UnityEngine;

namespace ParaSoft
{
    /// <summary>One renderer of the original canopy, and the static mesh data a stand-in needs.</summary>
    internal sealed class CanopySource
    {
        internal string Path;           // from the canopy transform, for finding it on other instances
        internal int RendererIndex;     // among renderers at that path
        internal int Offset, Count;     // into the capture's global vertex arrays
        internal int[][] Submeshes;
        internal Vector2[] Uv, Uv2;
        internal Color32[] Colors;
        internal bool HasNormals, HasTangents;
    }

    /// <summary>
    /// A canopy model, read and fitted once: which canopies it holds, the lattice each is
    /// simulated on, and how every original vertex hangs off that lattice. Immutable and
    /// shared between every part with the same model at the same size.
    /// </summary>
    internal sealed class CanopyModel
    {
        internal CanopyFitResult Fit;
        internal CanopyLattice[] Lattices;
        internal CanopyEmbedding[] Embeddings;
        internal int[][] Members;       // per canopy: global vertex indices
        internal CanopySource[] Sources;
        internal int VertexCount;
    }

    /// <summary>
    /// Reads a parachute's canopy out of the part it is on.
    ///
    /// The canopy's fully deployed shape is not something the model stores - stock and
    /// RealChute canopies are scaled open by their deploy animations, ReStock's are
    /// skinned and pulled open by bones. So the capture plays the module's semi- and full
    /// deploy animations to their last frame, bakes every renderer under the canopy
    /// transform, and puts the whole part back exactly as it was, all within one call.
    /// </summary>
    internal static class CanopyCapture
    {
        private static readonly Dictionary<string, CanopyModel> cache = new Dictionary<string, CanopyModel>();
        private static readonly HashSet<string> failed = new HashSet<string>();

        internal static CanopyModel Capture(Part part, ChuteSlot slot, out List<Renderer> renderers)
        {
            renderers = null;
            var canopy = slot.Canopy;
            if (canopy == null) return null;

            var snapshot = new TransformSnapshot(part.transform);
            var reactivate = new List<GameObject>();
            try
            {
                // The canopy is usually switched off while stowed; animation and skinning
                // both want it on for the duration of the bake.
                for (var t = canopy; t != null && t != part.transform.parent; t = t.parent)
                {
                    if (!t.gameObject.activeSelf)
                    {
                        reactivate.Add(t.gameObject);
                        t.gameObject.SetActive(true);
                    }
                }

                SampleDeployed(part, slot);

                renderers = new List<Renderer>();
                var sources = new List<CanopySource>();
                var verts = new List<Vec3>();
                var normals = new List<Vec3>();
                var tangents = new List<float>();
                var toCanopy = Matrix4x4.TRS(canopy.position, canopy.rotation, Vector3.one).inverse;

                foreach (var r in canopy.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh mesh;
                    var smr = r as SkinnedMeshRenderer;
                    if (smr != null) mesh = smr.sharedMesh;
                    else if (r is MeshRenderer)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                    }
                    else continue;
                    if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) continue;
                    if (verts.Count + mesh.vertexCount > Settings.MaxVertices)
                    {
                        Log.Warn("{0}: canopy has more than {1} vertices; leaving it animated.", part.partInfo.title, Settings.MaxVertices);
                        return null;
                    }

                    var src = new CanopySource
                    {
                        Path = RelativePath(canopy, r.transform),
                        RendererIndex = Array.IndexOf(r.GetComponents<Renderer>(), r),
                        Offset = verts.Count,
                        Count = mesh.vertexCount
                    };
                    src.Submeshes = new int[mesh.subMeshCount][];
                    for (var s = 0; s < mesh.subMeshCount; s++) src.Submeshes[s] = mesh.GetTriangles(s);
                    src.Uv = mesh.uv;
                    src.Uv2 = mesh.uv2;
                    src.Colors = mesh.colors32;
                    var srcNormals = mesh.normals;
                    var srcTangents = mesh.tangents;
                    src.HasNormals = srcNormals != null && srcNormals.Length == mesh.vertexCount;
                    src.HasTangents = srcTangents != null && srcTangents.Length == mesh.vertexCount;

                    Bake(r, smr, mesh, toCanopy, srcNormals, srcTangents, src, verts, normals, tangents);
                    sources.Add(src);
                    renderers.Add(r);
                }

                if (verts.Count == 0) return null;

                var key = slot.Key + "|" + Signature(verts);
                CanopyModel model;
                if (cache.TryGetValue(key, out model)) return model;
                if (failed.Contains(key)) return null;

                model = Build(part, slot, verts, normals, tangents, sources);
                if (model == null) failed.Add(key);
                else cache[key] = model;
                return model;
            }
            finally
            {
                snapshot.Restore();
                for (var i = reactivate.Count - 1; i >= 0; i--) reactivate[i].SetActive(false);
            }
        }

        private static CanopyModel Build(Part part, ChuteSlot slot, List<Vec3> verts, List<Vec3> normals,
                                         List<float> tangents, List<CanopySource> sources)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var all = verts.ToArray();
            var fit = CanopyFitter.Fit(all, slot.ExpectedCanopies);
            if (!fit.Ok)
            {
                Log.Info("{0}: canopy is not something ParaSoft can simulate ({1}); it keeps its own animation.",
                    part.partInfo.title, fit.Failure);
                return null;
            }

            var model = new CanopyModel
            {
                Fit = fit,
                Sources = sources.ToArray(),
                VertexCount = all.Length,
                Lattices = new CanopyLattice[fit.Canopies.Count],
                Embeddings = new CanopyEmbedding[fit.Canopies.Count],
                Members = new int[fit.Canopies.Count][]
            };
            var allNormals = normals.Count == all.Length ? normals.ToArray() : null;
            var allTangents = tangents.Count == all.Length * 4 ? tangents.ToArray() : null;
            for (var c = 0; c < fit.Canopies.Count; c++)
            {
                var members = new List<int>();
                for (var i = 0; i < all.Length; i++) if (fit.VertexCanopy[i] == c) members.Add(i);
                var mv = new Vec3[members.Count];
                var mn = allNormals != null ? new Vec3[members.Count] : null;
                var mt = allTangents != null ? new float[members.Count * 4] : null;
                for (var k = 0; k < members.Count; k++)
                {
                    mv[k] = all[members[k]];
                    if (mn != null) mn[k] = allNormals[members[k]];
                    if (mt != null) Array.Copy(allTangents, members[k] * 4, mt, k * 4, 4);
                }
                model.Members[c] = members.ToArray();
                model.Lattices[c] = CanopyLattice.Build(fit.Canopies[c], Settings.Resolution);
                model.Embeddings[c] = CanopyEmbedding.Compute(model.Lattices[c], mv, mn, mt);
            }
            var s0 = fit.Canopies[0];
            Log.Info("{0}: {1} canopy(s), {2:0.0} m across, {3:0.0} m lines, {4} vertices, fitted in {5} ms.",
                part.partInfo.title, fit.Canopies.Count, 2f * s0.MaxRadius, s0.LineLength, all.Length, sw.ElapsedMilliseconds);
            return model;
        }

        // -------------------------------------------------------------------------------
        // Animation sampling
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Poses the part as it looks at the end of the semi- then full-deploy animations.
        /// The full clip goes on a higher layer so it wins wherever both animate the same
        /// channel. Animation states are restored here; transforms by the caller's snapshot.
        /// </summary>
        private static void SampleDeployed(Part part, ChuteSlot slot)
        {
            var semi = slot.SemiAnimation;
            var full = slot.FullAnimation;
            var anims = new HashSet<Animation>();
            if (!string.IsNullOrEmpty(semi)) foreach (var a in part.FindModelAnimators(semi)) anims.Add(a);
            if (!string.IsNullOrEmpty(full)) foreach (var a in part.FindModelAnimators(full)) anims.Add(a);

            foreach (var anim in anims)
            {
                if (anim == null) continue;
                var saved = new List<SavedState>();
                foreach (AnimationState st in anim)
                {
                    saved.Add(new SavedState(st));
                    st.enabled = false;
                }
                var any = false;
                any |= Pose(anim, semi, 0);
                any |= Pose(anim, full, 1);
                if (any) anim.Sample();
                foreach (var s in saved) s.Restore();
            }
        }

        private static bool Pose(Animation anim, string clip, int layer)
        {
            if (string.IsNullOrEmpty(clip)) return false;
            var st = anim[clip];
            if (st == null) return false;
            st.enabled = true;
            st.weight = 1f;
            st.layer = layer;
            st.normalizedTime = 1f;
            return true;
        }

        private struct SavedState
        {
            private readonly AnimationState state;
            private readonly bool enabled;
            private readonly float weight, time;
            private readonly int layer;

            internal SavedState(AnimationState s)
            {
                state = s;
                enabled = s.enabled;
                weight = s.weight;
                time = s.normalizedTime;
                layer = s.layer;
            }

            internal void Restore()
            {
                state.enabled = enabled;
                state.weight = weight;
                state.layer = layer;
                state.normalizedTime = time;
            }
        }

        /// <summary>Every transform under a root, and how to put them all back.</summary>
        private sealed class TransformSnapshot
        {
            private readonly Transform[] transforms;
            private readonly Vector3[] positions, scales;
            private readonly Quaternion[] rotations;

            internal TransformSnapshot(Transform root)
            {
                transforms = root.GetComponentsInChildren<Transform>(true);
                positions = new Vector3[transforms.Length];
                rotations = new Quaternion[transforms.Length];
                scales = new Vector3[transforms.Length];
                for (var i = 0; i < transforms.Length; i++)
                {
                    positions[i] = transforms[i].localPosition;
                    rotations[i] = transforms[i].localRotation;
                    scales[i] = transforms[i].localScale;
                }
            }

            internal void Restore()
            {
                for (var i = 0; i < transforms.Length; i++)
                {
                    var t = transforms[i];
                    if (t == null) continue;
                    t.localPosition = positions[i];
                    t.localRotation = rotations[i];
                    t.localScale = scales[i];
                }
            }
        }

        // -------------------------------------------------------------------------------
        // Baking
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Vertices, normals and tangents in the canopy frame. Skinned meshes are skinned
        /// here on the CPU from the bones' current poses rather than with BakeMesh, whose
        /// handling of transform scale differs between Unity versions.
        /// </summary>
        private static void Bake(Renderer r, SkinnedMeshRenderer smr, Mesh mesh, Matrix4x4 toCanopy,
                                 Vector3[] srcNormals, Vector4[] srcTangents, CanopySource src,
                                 List<Vec3> verts, List<Vec3> normals, List<float> tangents)
        {
            var v = mesh.vertices;
            if (smr != null && smr.bones != null && smr.bones.Length > 0 && mesh.bindposes.Length == smr.bones.Length)
            {
                var bones = smr.bones;
                var bind = mesh.bindposes;
                var weights = mesh.boneWeights;
                var mats = new Matrix4x4[bones.Length];
                for (var b = 0; b < bones.Length; b++)
                    mats[b] = toCanopy * (bones[b] != null ? bones[b].localToWorldMatrix : r.transform.localToWorldMatrix) * bind[b];
                var fallback = toCanopy * r.transform.localToWorldMatrix;
                for (var i = 0; i < v.Length; i++)
                {
                    var m = weights.Length == v.Length ? Blend(mats, weights[i]) : fallback;
                    verts.Add(m.MultiplyPoint3x4(v[i]).ToVec3());
                    AddFrame(m, i, srcNormals, srcTangents, src, normals, tangents);
                }
            }
            else
            {
                var m = toCanopy * r.transform.localToWorldMatrix;
                for (var i = 0; i < v.Length; i++)
                {
                    verts.Add(m.MultiplyPoint3x4(v[i]).ToVec3());
                    AddFrame(m, i, srcNormals, srcTangents, src, normals, tangents);
                }
            }
        }

        private static void AddFrame(Matrix4x4 m, int i, Vector3[] srcNormals, Vector4[] srcTangents,
                                     CanopySource src, List<Vec3> normals, List<float> tangents)
        {
            var n = src.HasNormals ? m.MultiplyVector(srcNormals[i]).normalized : Vector3.up;
            normals.Add(n.ToVec3());
            if (src.HasTangents)
            {
                var t = srcTangents[i];
                var tw = m.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                tangents.Add(tw.x); tangents.Add(tw.y); tangents.Add(tw.z); tangents.Add(t.w);
            }
            else
            {
                tangents.Add(1f); tangents.Add(0f); tangents.Add(0f); tangents.Add(1f);
            }
        }

        private static Matrix4x4 Blend(Matrix4x4[] mats, BoneWeight w)
        {
            var r = new Matrix4x4();
            Add(ref r, mats[w.boneIndex0], w.weight0);
            if (w.weight1 > 0f) Add(ref r, mats[w.boneIndex1], w.weight1);
            if (w.weight2 > 0f) Add(ref r, mats[w.boneIndex2], w.weight2);
            if (w.weight3 > 0f) Add(ref r, mats[w.boneIndex3], w.weight3);
            var sum = w.weight0 + w.weight1 + w.weight2 + w.weight3;
            if (sum > 0f && Math.Abs(sum - 1f) > 1e-4f)
                for (var k = 0; k < 16; k++) r[k] /= sum;
            r.m33 = 1f;
            return r;
        }

        private static void Add(ref Matrix4x4 acc, Matrix4x4 m, float w)
        {
            for (var k = 0; k < 16; k++) acc[k] += m[k] * w;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var path = t.name;
            for (var p = t.parent; p != null && p != root; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        /// <summary>Vertex count and deployed size to the centimetre - enough to tell two RealChute diameters apart.</summary>
        private static string Signature(List<Vec3> verts)
        {
            var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var v in verts)
            {
                min = new Vec3(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
                max = new Vec3(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
            }
            var size = max - min;
            return verts.Count + ":" + Math.Round(size.X, 2) + "x" + Math.Round(size.Y, 2) + "x" + Math.Round(size.Z, 2) + ":" + Settings.Resolution.Gores;
        }
    }
}
