using System;
using System.Collections.Generic;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Geometry post-processing shared by the editor importers and the runtime loader:
    /// unit scaling, coordinate-system conversion (with winding fix), smooth-normal
    /// generation with a hard-edge angle threshold, and vertex welding.
    /// </summary>
    public static class MeshProcessor
    {
        public static void Process(CADModel model, in CADProcessOptions options)
        {
            foreach (var node in model.EnumerateNodes())
            {
                var m = node.Mesh;
                if (m?.Positions == null || m.Positions.Length == 0 || m.Submeshes == null) continue;
                ProcessMesh(m, options);
            }
        }

        public static void ProcessMesh(CADMeshData mesh, in CADProcessOptions o)
        {
            ApplyTransform(mesh, o.Scale, o.Orientation);
            float tol = Mathf.Max(o.WeldTolerance, 1e-9f);
            bool needNormals = o.RecalculateNormals
                || mesh.Normals == null
                || mesh.Normals.Length != mesh.Positions.Length;
            if (needNormals)
                RecalculateSmoothNormals(mesh, tol, o.SmoothingAngleDeg);
            if (o.Weld)
                Weld(mesh, tol);
        }

        // --- transform ---------------------------------------------------------------------

        public static void ApplyTransform(CADMeshData mesh, float scale, SourceOrientation orientation)
        {
            var p = mesh.Positions;
            var n = mesh.Normals;
            bool flipWinding = false;

            switch (orientation)
            {
                case SourceOrientation.ZUpRightHanded:
                    // (x, y, z) -> (x, z, y): axis swap is a reflection, so winding flips.
                    for (int i = 0; i < p.Length; i++)
                    {
                        var v = p[i];
                        p[i] = new Vector3(v.x * scale, v.z * scale, v.y * scale);
                    }
                    if (n != null)
                        for (int i = 0; i < n.Length; i++)
                        {
                            var v = n[i];
                            n[i] = new Vector3(v.x, v.z, v.y);
                        }
                    flipWinding = true;
                    break;

                case SourceOrientation.YUpRightHanded:
                    // (x, y, z) -> (-x, y, z): mirror on X, same convention Unity's FBX path uses.
                    for (int i = 0; i < p.Length; i++)
                    {
                        var v = p[i];
                        p[i] = new Vector3(-v.x * scale, v.y * scale, v.z * scale);
                    }
                    if (n != null)
                        for (int i = 0; i < n.Length; i++)
                        {
                            var v = n[i];
                            n[i] = new Vector3(-v.x, v.y, v.z);
                        }
                    flipWinding = true;
                    break;

                default:
                    if (!Mathf.Approximately(scale, 1f))
                        for (int i = 0; i < p.Length; i++)
                            p[i] *= scale;
                    break;
            }

            if (flipWinding && mesh.Submeshes != null)
            {
                foreach (var idx in mesh.Submeshes)
                {
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        int t = idx[i + 1];
                        idx[i + 1] = idx[i + 2];
                        idx[i + 2] = t;
                    }
                }
            }
        }

        // --- smooth normals ------------------------------------------------------------------

        readonly struct QKey : IEquatable<QKey>
        {
            readonly int x, y, z;

            public QKey(Vector3 v, float invTol)
            {
                x = Mathf.RoundToInt(v.x * invTol);
                y = Mathf.RoundToInt(v.y * invTol);
                z = Mathf.RoundToInt(v.z * invTol);
            }

            public bool Equals(QKey o) => x == o.x && y == o.y && z == o.z;
            public override bool Equals(object o) => o is QKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = x * 73856093;
                    h ^= y * 19349663;
                    h ^= z * 83492791;
                    return h;
                }
            }
        }

        /// <summary>
        /// Computes angle-weighted smooth normals across coincident positions, splitting
        /// vertices where adjacent faces meet at more than <paramref name="smoothingAngleDeg"/>
        /// (hard edges). Works on both indexed meshes and raw triangle soup (e.g. STL).
        /// </summary>
        public static void RecalculateSmoothNormals(CADMeshData mesh, float positionTolerance, float smoothingAngleDeg)
        {
            var pos = mesh.Positions;
            int vcount = pos.Length;
            if (vcount == 0 || mesh.Submeshes == null) return;
            float cosThreshold = Mathf.Cos(Mathf.Clamp(smoothingAngleDeg, 0f, 180f) * Mathf.Deg2Rad);
            float invTol = 1f / Mathf.Max(positionTolerance, 1e-9f);

            // 1. cluster coincident positions
            var clusterOf = new int[vcount];
            var map = new Dictionary<QKey, int>(vcount);
            int clusterCount = 0;
            for (int i = 0; i < vcount; i++)
            {
                var key = new QKey(pos[i], invTol);
                if (!map.TryGetValue(key, out int c))
                {
                    c = clusterCount++;
                    map.Add(key, c);
                }
                clusterOf[i] = c;
            }

            // 2. flatten corners, computing face normals and corner angles
            int cornerCount = 0;
            foreach (var sm in mesh.Submeshes) cornerCount += sm.Length;

            var cornerVert = new int[cornerCount];
            var cornerFaceN = new Vector3[cornerCount];
            var cornerWeight = new float[cornerCount];
            var clusterHead = new int[clusterCount];
            var cornerNext = new int[cornerCount];
            for (int i = 0; i < clusterCount; i++) clusterHead[i] = -1;

            int corner = 0;
            foreach (var idx in mesh.Submeshes)
            {
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                    Vector3 a = pos[i0], b = pos[i1], c = pos[i2];
                    Vector3 fn = Vector3.Cross(b - a, c - a);
                    float mag = fn.magnitude;
                    Vector3 unit = mag > 1e-20f ? fn / mag : Vector3.up;

                    for (int j = 0; j < 3; j++)
                    {
                        int vi = idx[t + j];
                        Vector3 e0, e1;
                        if (j == 0) { e0 = b - a; e1 = c - a; }
                        else if (j == 1) { e0 = c - b; e1 = a - b; }
                        else { e0 = a - c; e1 = b - c; }
                        float angle = Vector3.Angle(e0, e1) * Mathf.Deg2Rad;

                        cornerVert[corner] = vi;
                        cornerFaceN[corner] = unit;
                        cornerWeight[corner] = mag > 1e-20f ? angle : 0f;
                        int cluster = clusterOf[vi];
                        cornerNext[corner] = clusterHead[cluster];
                        clusterHead[cluster] = corner;
                        corner++;
                    }
                }
            }

            // 3. greedily group corners within each cluster by face-normal similarity
            var cornerGroup = new int[cornerCount];
            var groupAccum = new List<Vector3>(clusterCount);
            var repNormals = new List<Vector3>(8);
            var repGroupIds = new List<int>(8);

            for (int c = 0; c < clusterCount; c++)
            {
                repNormals.Clear();
                repGroupIds.Clear();
                for (int k = clusterHead[c]; k != -1; k = cornerNext[k])
                {
                    Vector3 fn = cornerFaceN[k];
                    int group = -1;
                    for (int g = 0; g < repNormals.Count; g++)
                    {
                        if (Vector3.Dot(fn, repNormals[g]) >= cosThreshold)
                        {
                            group = repGroupIds[g];
                            break;
                        }
                    }
                    if (group < 0)
                    {
                        group = groupAccum.Count;
                        groupAccum.Add(Vector3.zero);
                        repNormals.Add(fn);
                        repGroupIds.Add(group);
                    }
                    cornerGroup[k] = group;
                    groupAccum[group] += fn * cornerWeight[k];
                }
            }

            // 4. rebuild vertices: split original vertices used by more than one group
            var vertKey = new Dictionary<long, int>(vcount);
            var newPos = new List<Vector3>(vcount);
            var newNrm = new List<Vector3>(vcount);
            List<Color32> newCol = mesh.Colors != null ? new List<Color32>(vcount) : null;
            List<Vector2> newUv = mesh.UV != null ? new List<Vector2>(vcount) : null;

            corner = 0;
            foreach (var idx in mesh.Submeshes)
            {
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int orig = cornerVert[corner];
                        int group = cornerGroup[corner];
                        long key = ((long)orig << 32) | (uint)group;
                        if (!vertKey.TryGetValue(key, out int ni))
                        {
                            ni = newPos.Count;
                            newPos.Add(pos[orig]);
                            Vector3 acc = groupAccum[group];
                            float m = acc.magnitude;
                            newNrm.Add(m > 1e-20f ? acc / m : cornerFaceN[corner]);
                            newCol?.Add(mesh.Colors[orig]);
                            newUv?.Add(mesh.UV[orig]);
                            vertKey.Add(key, ni);
                        }
                        idx[t + j] = ni;
                        corner++;
                    }
                }
            }

            mesh.Positions = newPos.ToArray();
            mesh.Normals = newNrm.ToArray();
            if (newCol != null) mesh.Colors = newCol.ToArray();
            if (newUv != null) mesh.UV = newUv.ToArray();
        }

        // --- welding ---------------------------------------------------------------------

        readonly struct WeldKey : IEquatable<WeldKey>
        {
            readonly int px, py, pz, nx, ny, nz, u, v;
            readonly uint color;

            public WeldKey(Vector3 p, Vector3 n, Vector2 uv, Color32 c, float invPosTol)
            {
                px = Mathf.RoundToInt(p.x * invPosTol);
                py = Mathf.RoundToInt(p.y * invPosTol);
                pz = Mathf.RoundToInt(p.z * invPosTol);
                nx = Mathf.RoundToInt(n.x * 1000f);
                ny = Mathf.RoundToInt(n.y * 1000f);
                nz = Mathf.RoundToInt(n.z * 1000f);
                u = Mathf.RoundToInt(uv.x * 100000f);
                v = Mathf.RoundToInt(uv.y * 100000f);
                color = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
            }

            public bool Equals(WeldKey o) =>
                px == o.px && py == o.py && pz == o.pz &&
                nx == o.nx && ny == o.ny && nz == o.nz &&
                u == o.u && v == o.v && color == o.color;

            public override bool Equals(object o) => o is WeldKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = px * 73856093 ^ py * 19349663 ^ pz * 83492791;
                    h = h * 31 + (nx * 5381 ^ ny * 2609 ^ nz * 947);
                    h = h * 31 + (u * 7919 ^ v * 6151);
                    h = h * 31 + (int)color;
                    return h;
                }
            }
        }

        /// <summary>
        /// Merges vertices whose position, normal, UV and color all match within tolerance,
        /// drops degenerate triangles and prunes vertices left unreferenced (e.g. only used
        /// by degenerate source facets). Run after normal generation so shading is preserved.
        /// </summary>
        public static void Weld(CADMeshData mesh, float positionTolerance)
        {
            var pos = mesh.Positions;
            int vcount = pos.Length;
            if (vcount == 0 || mesh.Submeshes == null) return;
            float invTol = 1f / Mathf.Max(positionTolerance, 1e-9f);

            var nrm = mesh.Normals;
            var col = mesh.Colors;
            var uv = mesh.UV;

            // 1. assign a welded id to every source vertex; remember one representative each
            var remap = new int[vcount];
            var map = new Dictionary<WeldKey, int>(vcount);
            var representative = new List<int>(vcount / 2 + 1);

            for (int i = 0; i < vcount; i++)
            {
                var key = new WeldKey(
                    pos[i],
                    nrm != null ? nrm[i] : Vector3.zero,
                    uv != null ? uv[i] : Vector2.zero,
                    col != null ? col[i] : default,
                    invTol);
                if (!map.TryGetValue(key, out int id))
                {
                    id = representative.Count;
                    representative.Add(i);
                    map.Add(key, id);
                }
                remap[i] = id;
            }

            // 2. rebuild index buffers, dropping triangles that became degenerate
            var used = new bool[representative.Count];
            for (int s = 0; s < mesh.Submeshes.Length; s++)
            {
                var idx = mesh.Submeshes[s];
                var outIdx = new List<int>(idx.Length);
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    int a = remap[idx[i]], b = remap[idx[i + 1]], c = remap[idx[i + 2]];
                    if (a == b || b == c || c == a) continue;
                    used[a] = used[b] = used[c] = true;
                    outIdx.Add(a); outIdx.Add(b); outIdx.Add(c);
                }
                mesh.Submeshes[s] = outIdx.ToArray();
            }

            // 3. compact away welded vertices no surviving triangle references
            var final = new int[representative.Count];
            int n = 0;
            for (int id = 0; id < representative.Count; id++)
                final[id] = used[id] ? n++ : -1;

            var newPos = new Vector3[n];
            var newNrm = nrm != null ? new Vector3[n] : null;
            var newCol = col != null ? new Color32[n] : null;
            var newUv = uv != null ? new Vector2[n] : null;
            for (int id = 0; id < representative.Count; id++)
            {
                if (!used[id]) continue;
                int src = representative[id];
                int dst = final[id];
                newPos[dst] = pos[src];
                if (newNrm != null) newNrm[dst] = nrm[src];
                if (newCol != null) newCol[dst] = col[src];
                if (newUv != null) newUv[dst] = uv[src];
            }

            foreach (var idx in mesh.Submeshes)
                for (int i = 0; i < idx.Length; i++)
                    idx[i] = final[idx[i]];

            mesh.Positions = newPos;
            if (newNrm != null) mesh.Normals = newNrm;
            if (newCol != null) mesh.Colors = newCol;
            if (newUv != null) mesh.UV = newUv;
        }

        /// <summary>
        /// Builds a position-only welded copy for topology-sensitive operations
        /// (LOD decimation, collision meshes). Vertices that were split for shading
        /// (hard edges, UV seams, color seams) are merged back together so the mesh is a
        /// connected surface again — decimating the render mesh directly would treat every
        /// hard edge as an open border and tear the surface apart. Attributes are dropped;
        /// recompute normals on the result if needed.
        /// </summary>
        public static CADMeshData PositionWeldedCopy(CADMeshData src, float positionTolerance)
        {
            var copy = new CADMeshData
            {
                Name = src.Name,
                Positions = (Vector3[])src.Positions.Clone(),
                SubmeshMaterials = (string[])src.SubmeshMaterials?.Clone()
            };
            copy.Submeshes = new int[src.Submeshes.Length][];
            for (int i = 0; i < src.Submeshes.Length; i++)
                copy.Submeshes[i] = (int[])src.Submeshes[i].Clone();
            Weld(copy, positionTolerance); // no attribute arrays -> keys are position-only
            return copy;
        }

        /// <summary>Deep copy of the mesh data (indices and attribute arrays).</summary>
        public static CADMeshData Clone(CADMeshData src)
        {
            var dst = new CADMeshData
            {
                Name = src.Name,
                Positions = (Vector3[])src.Positions?.Clone(),
                Normals = (Vector3[])src.Normals?.Clone(),
                Colors = (Color32[])src.Colors?.Clone(),
                UV = (Vector2[])src.UV?.Clone(),
                SubmeshMaterials = (string[])src.SubmeshMaterials?.Clone()
            };
            if (src.Submeshes != null)
            {
                dst.Submeshes = new int[src.Submeshes.Length][];
                for (int i = 0; i < src.Submeshes.Length; i++)
                    dst.Submeshes[i] = (int[])src.Submeshes[i].Clone();
            }
            return dst;
        }
    }
}
