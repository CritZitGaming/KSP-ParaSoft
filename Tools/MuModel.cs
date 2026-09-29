using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ParaSoft.Core;

/// <summary>
/// A reader for KSP's .mu model format, just deep enough to rebuild a part's transform
/// hierarchy, its meshes (plain and skinned) and its animation clips outside the game.
///
/// It exists so the canopy fitter can be tested against the real parachute models that
/// ship with stock, ReStock, RealChute and Custom Parachute Message, instead of against
/// shapes invented for the test. Coordinates are kept in Unity's own left-handed space -
/// nothing here converts to Blender's conventions the way the format's reference reader
/// (taniwha/io_object_mu) does, because the fitter runs on Unity-space data in game.
/// </summary>
internal sealed class MuModel
{
    internal sealed class Node
    {
        internal string Name;
        internal Node Parent;
        internal readonly List<Node> Children = new List<Node>();
        internal Vec3 LocalPosition;
        internal Quat LocalRotation = Quat.Identity;
        internal Vec3 LocalScale = new Vec3(1f, 1f, 1f);
        internal MuMesh Mesh;               // MeshFilter
        internal bool HasMeshRenderer;
        internal int[] RendererMaterials;
        internal MuMesh SkinnedMesh;        // SkinnedMeshRenderer
        internal string[] SkinnedBones;
        internal List<MuClip> Clips;        // Animation component

        internal string Path
        {
            get { return Parent == null ? Name : Parent.Path + "/" + Name; }
        }

        internal Mat4 LocalMatrix
        {
            get { return Mat4.TRS(LocalPosition, LocalRotation, LocalScale); }
        }

        internal Mat4 WorldMatrix
        {
            get { return Parent == null ? LocalMatrix : Parent.WorldMatrix * LocalMatrix; }
        }

        internal Quat WorldRotation
        {
            get { return Parent == null ? LocalRotation.Normalized : (Parent.WorldRotation * LocalRotation).Normalized; }
        }

        /// <summary>
        /// The node's frame with scale removed - what the game's Transform.position and
        /// Transform.rotation describe, and the frame the canopy fitter works in.
        /// </summary>
        internal Mat4 UnscaledWorldMatrix
        {
            get
            {
                var w = WorldMatrix;
                return Mat4.TRS(new Vec3(w.M03, w.M13, w.M23), WorldRotation, new Vec3(1f, 1f, 1f));
            }
        }
    }

    internal sealed class MuMesh
    {
        internal Vec3[] Vertices;
        internal float[] Uv;               // 2 per vertex
        internal Vec3[] Normals;
        internal int[][] Submeshes;
        internal int[] BoneIndex;          // 4 per vertex
        internal float[] BoneWeight;       // 4 per vertex
        internal Mat4[] BindPoses;
    }

    internal sealed class MuKey
    {
        internal float Time, Value, InTangent, OutTangent;
    }

    internal sealed class MuCurve
    {
        internal string Path, Property;
        internal int Type;
        internal MuKey[] Keys;

        /// <summary>Unity's hermite evaluation between keys, clamped at both ends.</summary>
        internal float Evaluate(float t)
        {
            if (Keys.Length == 0) return 0f;
            if (t <= Keys[0].Time) return Keys[0].Value;
            if (t >= Keys[Keys.Length - 1].Time) return Keys[Keys.Length - 1].Value;
            for (var i = 0; i < Keys.Length - 1; i++)
            {
                var a = Keys[i];
                var b = Keys[i + 1];
                if (t > b.Time) continue;
                var dt = b.Time - a.Time;
                if (dt <= 0f) return b.Value;
                if (float.IsInfinity(a.OutTangent) || float.IsInfinity(b.InTangent)) return a.Value; // stepped
                var s = (t - a.Time) / dt;
                var s2 = s * s;
                var s3 = s2 * s;
                var h00 = 2f * s3 - 3f * s2 + 1f;
                var h10 = s3 - 2f * s2 + s;
                var h01 = -2f * s3 + 3f * s2;
                var h11 = s3 - s2;
                return h00 * a.Value + h10 * dt * a.OutTangent + h01 * b.Value + h11 * dt * b.InTangent;
            }
            return Keys[Keys.Length - 1].Value;
        }

        internal float Length
        {
            get { return Keys.Length == 0 ? 0f : Keys[Keys.Length - 1].Time; }
        }
    }

    internal sealed class MuClip
    {
        internal string Name;
        internal readonly List<MuCurve> Curves = new List<MuCurve>();

        internal float Length
        {
            get
            {
                var l = 0f;
                foreach (var c in Curves) l = Math.Max(l, c.Length);
                return l;
            }
        }
    }

    internal Node Root;
    internal int Version;

    private BinaryReader reader;

    internal static MuModel Load(string path)
    {
        var model = new MuModel();
        using (var stream = File.OpenRead(path))
        using (var r = new BinaryReader(stream, Encoding.UTF8))
        {
            model.reader = r;
            var magic = r.ReadInt32();
            if (magic != 76543) throw new InvalidDataException("not a .mu file: " + path);
            model.Version = r.ReadInt32();
            r.ReadString(); // model name
            model.Root = model.ReadNode(null);
            model.reader = null;
        }
        return model;
    }

    private Vec3 ReadVec3()
    {
        var x = reader.ReadSingle();
        var y = reader.ReadSingle();
        var z = reader.ReadSingle();
        return new Vec3(x, y, z);
    }

    private Node ReadNode(Node parent)
    {
        var node = new Node { Parent = parent };
        node.Name = reader.ReadString();
        node.LocalPosition = ReadVec3();
        var qx = reader.ReadSingle();
        var qy = reader.ReadSingle();
        var qz = reader.ReadSingle();
        var qw = reader.ReadSingle();
        node.LocalRotation = new Quat(qx, qy, qz, qw);
        node.LocalScale = ReadVec3();

        while (true)
        {
            int entry;
            try { entry = reader.ReadInt32(); }
            catch (EndOfStreamException) { break; }

            switch (entry)
            {
                case 0: node.Children.Add(ReadNode(node)); break;
                case 1: return node;
                case 2: node.Clips = ReadAnimation(); break;
                case 3: reader.ReadByte(); ReadMesh(); break;
                case 4: reader.ReadSingle(); ReadVec3(); break;
                case 5: reader.ReadSingle(); reader.ReadSingle(); reader.ReadInt32(); ReadVec3(); break;
                case 6: ReadVec3(); ReadVec3(); break;
                case 7: node.Mesh = ReadMesh(); break;
                case 8:
                    if (Version > 0) { reader.ReadByte(); reader.ReadByte(); }
                    var n = reader.ReadInt32();
                    node.RendererMaterials = new int[n];
                    for (var i = 0; i < n; i++) node.RendererMaterials[i] = reader.ReadInt32();
                    node.HasMeshRenderer = true;
                    break;
                case 9:
                    var nm = reader.ReadInt32();
                    node.RendererMaterials = new int[nm];
                    for (var i = 0; i < nm; i++) node.RendererMaterials[i] = reader.ReadInt32();
                    ReadVec3(); ReadVec3();
                    reader.ReadInt32(); reader.ReadByte();
                    var nb = reader.ReadInt32();
                    node.SkinnedBones = new string[nb];
                    for (var i = 0; i < nb; i++) node.SkinnedBones[i] = reader.ReadString();
                    node.SkinnedMesh = ReadMesh();
                    break;
                case 10: SkipMaterials(); break;
                case 12:
                    var tc = reader.ReadInt32();
                    for (var i = 0; i < tc; i++) { reader.ReadString(); reader.ReadInt32(); }
                    break;
                case 23:
                    reader.ReadInt32(); reader.ReadSingle(); reader.ReadSingle();
                    for (var i = 0; i < 4; i++) reader.ReadSingle();
                    reader.ReadUInt32();
                    if (Version > 1) reader.ReadSingle();
                    break;
                case 24: reader.ReadString(); reader.ReadInt32(); break;
                case 25: reader.ReadByte(); reader.ReadByte(); ReadMesh(); break;
                case 26: reader.ReadByte(); reader.ReadSingle(); ReadVec3(); break;
                case 27: reader.ReadByte(); reader.ReadSingle(); reader.ReadSingle(); reader.ReadInt32(); ReadVec3(); break;
                case 28: reader.ReadByte(); ReadVec3(); ReadVec3(); break;
                case 29:
                    reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); ReadVec3();
                    for (var i = 0; i < 3 + 5 + 5; i++) reader.ReadSingle();
                    break;
                case 30:
                    reader.ReadInt32();
                    for (var i = 0; i < 4; i++) reader.ReadSingle();
                    reader.ReadUInt32(); reader.ReadByte();
                    for (var i = 0; i < 4; i++) reader.ReadSingle();
                    break;
                default:
                    // Old (v1) exports carry an extra float after each transform. The
                    // reference reader survives it by treating unknown codes as empty
                    // entries, and so does this one.
                    break;
            }
        }
        return node;
    }

    private void SkipMaterials()
    {
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            reader.ReadString();
            if (Version >= 4)
            {
                reader.ReadString();
                var props = reader.ReadInt32();
                for (var p = 0; p < props; p++)
                {
                    reader.ReadString();
                    var type = reader.ReadInt32();
                    switch (type)
                    {
                        case 0:
                        case 1: for (var k = 0; k < 4; k++) reader.ReadSingle(); break;
                        case 2:
                        case 3: reader.ReadSingle(); break;
                        case 4: reader.ReadInt32(); for (var k = 0; k < 4; k++) reader.ReadSingle(); break;
                    }
                }
            }
            else
            {
                // v3 and older: a fixed property layout per built-in shader type.
                // T = texture slot (index + scale + offset), C = colour, F = float.
                var type = reader.ReadInt32();
                string layout;
                switch (type)
                {
                    case 1: layout = "T"; break;
                    case 2: layout = "TCF"; break;
                    case 3: layout = "TT"; break;
                    case 4: layout = "TTCF"; break;
                    case 5: layout = "TTC"; break;
                    case 6: layout = "TCFTC"; break;
                    case 7: layout = "TTCFTC"; break;
                    case 8: layout = "TF"; break;
                    case 9: layout = "TTF"; break;
                    case 10: layout = "T"; break;
                    case 11: layout = "TFCF"; break;
                    case 12:
                    case 13: layout = "TC"; break;
                    case 14:
                    case 15: layout = "TCF"; break;
                    default: throw new InvalidDataException("unknown v3 material type " + type);
                }
                foreach (var c in layout)
                {
                    if (c == 'T') { reader.ReadInt32(); for (var k = 0; k < 4; k++) reader.ReadSingle(); }
                    else if (c == 'C') { for (var k = 0; k < 4; k++) reader.ReadSingle(); }
                    else reader.ReadSingle();
                }
            }
        }
    }

    private List<MuClip> ReadAnimation()
    {
        var clips = new List<MuClip>();
        var count = reader.ReadInt32();
        for (var c = 0; c < count; c++)
        {
            var clip = new MuClip { Name = reader.ReadString() };
            ReadVec3(); ReadVec3();
            reader.ReadInt32();
            var curves = reader.ReadInt32();
            for (var i = 0; i < curves; i++)
            {
                var curve = new MuCurve { Path = reader.ReadString(), Property = reader.ReadString() };
                curve.Type = reader.ReadInt32();
                var pre = reader.ReadInt32();
                var post = reader.ReadInt32();
                int keys;
                if (curve.Type == 8) { keys = post; }
                else keys = reader.ReadInt32();
                curve.Keys = new MuKey[keys];
                for (var k = 0; k < keys; k++)
                {
                    var key = new MuKey();
                    key.Time = reader.ReadSingle();
                    key.Value = reader.ReadSingle();
                    key.InTangent = reader.ReadSingle();
                    key.OutTangent = reader.ReadSingle();
                    reader.ReadInt32();
                    curve.Keys[k] = key;
                }
                clip.Curves.Add(curve);
            }
            clips.Add(clip);
        }
        reader.ReadString();
        reader.ReadByte();
        return clips;
    }

    private MuMesh ReadMesh()
    {
        var mesh = new MuMesh();
        if (reader.ReadInt32() != 13) throw new InvalidDataException("mesh start expected");
        var verts = reader.ReadInt32();
        var submeshCount = reader.ReadInt32();
        var subs = new List<int[]>();
        while (true)
        {
            var type = reader.ReadInt32();
            if (type == 22) break;
            switch (type)
            {
                case 14:
                    mesh.Vertices = new Vec3[verts];
                    for (var i = 0; i < verts; i++) mesh.Vertices[i] = ReadVec3();
                    break;
                case 15:
                    mesh.Uv = new float[verts * 2];
                    for (var i = 0; i < verts * 2; i++) mesh.Uv[i] = reader.ReadSingle();
                    break;
                case 16:
                    for (var i = 0; i < verts * 2; i++) reader.ReadSingle();
                    break;
                case 17:
                    mesh.Normals = new Vec3[verts];
                    for (var i = 0; i < verts; i++) mesh.Normals[i] = ReadVec3();
                    break;
                case 18:
                    for (var i = 0; i < verts * 4; i++) reader.ReadSingle();
                    break;
                case 19:
                    var n = reader.ReadInt32();
                    var tris = new int[n];
                    for (var i = 0; i < n; i++) tris[i] = reader.ReadInt32();
                    subs.Add(tris);
                    break;
                case 20:
                    mesh.BoneIndex = new int[verts * 4];
                    mesh.BoneWeight = new float[verts * 4];
                    for (var i = 0; i < verts; i++)
                    {
                        for (var k = 0; k < 4; k++)
                        {
                            mesh.BoneIndex[i * 4 + k] = reader.ReadInt32();
                            mesh.BoneWeight[i * 4 + k] = reader.ReadSingle();
                        }
                    }
                    break;
                case 21:
                    var poses = reader.ReadInt32();
                    mesh.BindPoses = new Mat4[poses];
                    for (var p = 0; p < poses; p++)
                    {
                        var m = new float[16];
                        for (var k = 0; k < 16; k++) m[k] = reader.ReadSingle();
                        mesh.BindPoses[p] = Mat4.FromRowMajor(m);
                    }
                    break;
                case 32:
                    for (var i = 0; i < verts * 4; i++) reader.ReadByte();
                    break;
                default:
                    throw new InvalidDataException("unhandled mesh entry " + type);
            }
        }
        mesh.Submeshes = subs.ToArray();
        return mesh;
    }

    // -------------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------------

    internal IEnumerable<Node> AllNodes()
    {
        var stack = new Stack<Node>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    internal Node Find(string name)
    {
        foreach (var n in AllNodes())
            if (n.Name == name) return n;
        return null;
    }

    internal Node FindByPath(Node from, string path)
    {
        if (string.IsNullOrEmpty(path)) return from;
        var cur = from;
        foreach (var part in path.Split('/'))
        {
            Node next = null;
            foreach (var c in cur.Children) if (c.Name == part) { next = c; break; }
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// Applies every clip with the given names at its last frame, in order, the way the
    /// game ends up after playing a parachute's semi- then full-deploy animation.
    /// </summary>
    internal void ApplyClipEnd(string clipName)
    {
        foreach (var owner in AllNodes())
        {
            if (owner.Clips == null) continue;
            foreach (var clip in owner.Clips)
            {
                if (clip.Name != clipName) continue;
                var t = clip.Length;
                foreach (var curve in clip.Curves)
                {
                    var target = FindByPath(owner, curve.Path);
                    if (target == null) continue;
                    var v = curve.Evaluate(t);
                    ApplyProperty(target, curve.Property, v);
                }
            }
        }
    }

    private static void ApplyProperty(Node n, string property, float v)
    {
        var p = property.Replace("m_", "");
        switch (p)
        {
            case "LocalPosition.x": case "localPosition.x": n.LocalPosition.X = v; break;
            case "LocalPosition.y": case "localPosition.y": n.LocalPosition.Y = v; break;
            case "LocalPosition.z": case "localPosition.z": n.LocalPosition.Z = v; break;
            case "LocalScale.x": case "localScale.x": n.LocalScale.X = v; break;
            case "LocalScale.y": case "localScale.y": n.LocalScale.Y = v; break;
            case "LocalScale.z": case "localScale.z": n.LocalScale.Z = v; break;
            case "LocalRotation.x": case "localRotation.x": n.LocalRotation.X = v; break;
            case "LocalRotation.y": case "localRotation.y": n.LocalRotation.Y = v; break;
            case "LocalRotation.z": case "localRotation.z": n.LocalRotation.Z = v; break;
            case "LocalRotation.w": case "localRotation.w": n.LocalRotation.W = v; break;
        }
    }

    internal HashSet<string> ClipNames()
    {
        var names = new HashSet<string>();
        foreach (var n in AllNodes())
            if (n.Clips != null)
                foreach (var c in n.Clips) names.Add(c.Name);
        return names;
    }

    /// <summary>
    /// World-space vertices of a node's mesh, skinned if it is a skinned mesh - the
    /// offline equivalent of MeshFilter + localToWorldMatrix, or SkinnedMeshRenderer.BakeMesh.
    /// </summary>
    internal Vec3[] BakeWorld(Node node)
    {
        if (node.Mesh != null)
        {
            var m = node.WorldMatrix;
            var result = new Vec3[node.Mesh.Vertices.Length];
            for (var i = 0; i < result.Length; i++) result[i] = m.MultiplyPoint(node.Mesh.Vertices[i]);
            return result;
        }
        if (node.SkinnedMesh != null)
        {
            var mesh = node.SkinnedMesh;
            var bones = new Mat4[node.SkinnedBones.Length];
            for (var b = 0; b < bones.Length; b++)
            {
                var bn = Find(node.SkinnedBones[b]);
                var world = bn != null ? bn.WorldMatrix : node.WorldMatrix;
                bones[b] = world * mesh.BindPoses[b];
            }
            var result = new Vec3[mesh.Vertices.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var v = mesh.Vertices[i];
                var acc = new Vec3();
                var wsum = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var w = mesh.BoneWeight[i * 4 + k];
                    if (w <= 0f) continue;
                    acc += bones[mesh.BoneIndex[i * 4 + k]].MultiplyPoint(v) * w;
                    wsum += w;
                }
                result[i] = wsum > 0f ? acc / wsum : node.WorldMatrix.MultiplyPoint(v);
            }
            return result;
        }
        return null;
    }

    internal static bool IsUnder(Node n, Node ancestor)
    {
        for (var c = n; c != null; c = c.Parent) if (c == ancestor) return true;
        return false;
    }
}
