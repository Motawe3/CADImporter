using System;
using System.Collections.Generic;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Quadric-error-metric mesh simplification (Garland–Heckbert quadrics with the
    /// iterative threshold scheme popularized by Sven Forstmann's MIT-licensed
    /// "Fast Quadric Mesh Simplification"). Used for LOD chains and simplified
    /// physics-collision meshes. Requires welded (index-shared) input to find edges.
    /// </summary>
    public static class MeshDecimator
    {
        /// <summary>
        /// Decimates every submesh of <paramref name="source"/> to roughly
        /// <paramref name="quality"/> (0..1) of its triangle count. Normals/colors/UVs are
        /// not carried over — recompute normals afterwards. Returns a new mesh data object.
        /// </summary>
        public static CADMeshData Decimate(CADMeshData source, float quality)
        {
            var outPos = new List<Vector3>();
            var outSubmeshes = new List<int[]>();
            var outMats = new List<string>();

            for (int s = 0; s < source.Submeshes.Length; s++)
            {
                var idx = source.Submeshes[s];
                if (idx.Length < 3) continue;

                Decimate(source.Positions, idx, quality, out var pos, out var tris);
                if (tris.Length < 3) continue;

                int offset = outPos.Count;
                outPos.AddRange(pos);
                if (offset != 0)
                    for (int i = 0; i < tris.Length; i++) tris[i] += offset;
                outSubmeshes.Add(tris);
                outMats.Add(source.SubmeshMaterials != null && s < source.SubmeshMaterials.Length
                    ? source.SubmeshMaterials[s] : null);
            }

            return new CADMeshData
            {
                Name = source.Name,
                Positions = outPos.ToArray(),
                Submeshes = outSubmeshes.ToArray(),
                SubmeshMaterials = source.SubmeshMaterials != null ? outMats.ToArray() : null
            };
        }

        /// <summary>
        /// Decimates one indexed triangle list. Vertices not referenced by
        /// <paramref name="indices"/> are dropped from the output.
        /// </summary>
        public static void Decimate(Vector3[] positions, int[] indices, float quality,
            out Vector3[] outPositions, out int[] outIndices)
        {
            int srcTris = indices.Length / 3;
            int target = Mathf.Max(4, Mathf.RoundToInt(srcTris * Mathf.Clamp01(quality)));
            if (target >= srcTris)
            {
                var simplifierNoOp = new Simplifier(positions, indices);
                simplifierNoOp.GetResult(out outPositions, out outIndices); // compacts unused verts
                return;
            }

            var simplifier = new Simplifier(positions, indices);
            simplifier.Simplify(target);
            simplifier.GetResult(out outPositions, out outIndices);
        }

        struct Vector3d
        {
            public double x, y, z;

            public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public Vector3d(Vector3 v) { x = v.x; y = v.y; z = v.z; }

            public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
            public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
            public static Vector3d operator *(Vector3d a, double s) => new Vector3d(a.x * s, a.y * s, a.z * s);

            public static double Dot(in Vector3d a, in Vector3d b) => a.x * b.x + a.y * b.y + a.z * b.z;

            public static Vector3d Cross(in Vector3d a, in Vector3d b) =>
                new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

            public double Magnitude => Math.Sqrt(x * x + y * y + z * z);

            public Vector3d Normalized
            {
                get
                {
                    double m = Magnitude;
                    return m > 1e-30 ? new Vector3d(x / m, y / m, z / m) : new Vector3d(0, 1, 0);
                }
            }

            public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);
        }

        struct SymmetricMatrix
        {
            public double m0, m1, m2, m3, m4, m5, m6, m7, m8, m9;

            public SymmetricMatrix(double a, double b, double c, double d)
            {
                m0 = a * a; m1 = a * b; m2 = a * c; m3 = a * d;
                m4 = b * b; m5 = b * c; m6 = b * d;
                m7 = c * c; m8 = c * d;
                m9 = d * d;
            }

            public double this[int i]
            {
                get
                {
                    switch (i)
                    {
                        case 0: return m0;
                        case 1: return m1;
                        case 2: return m2;
                        case 3: return m3;
                        case 4: return m4;
                        case 5: return m5;
                        case 6: return m6;
                        case 7: return m7;
                        case 8: return m8;
                        default: return m9;
                    }
                }
            }

            public static SymmetricMatrix operator +(SymmetricMatrix a, SymmetricMatrix b)
            {
                var r = default(SymmetricMatrix);
                r.m0 = a.m0 + b.m0; r.m1 = a.m1 + b.m1; r.m2 = a.m2 + b.m2; r.m3 = a.m3 + b.m3;
                r.m4 = a.m4 + b.m4; r.m5 = a.m5 + b.m5; r.m6 = a.m6 + b.m6;
                r.m7 = a.m7 + b.m7; r.m8 = a.m8 + b.m8; r.m9 = a.m9 + b.m9;
                return r;
            }

            public double Det(int a11, int a12, int a13, int a21, int a22, int a23, int a31, int a32, int a33)
            {
                return this[a11] * this[a22] * this[a33]
                     + this[a13] * this[a21] * this[a32]
                     + this[a12] * this[a23] * this[a31]
                     - this[a13] * this[a22] * this[a31]
                     - this[a11] * this[a23] * this[a32]
                     - this[a12] * this[a21] * this[a33];
            }
        }

        struct Triangle
        {
            public int v0, v1, v2;
            public double err0, err1, err2, err3;
            public bool deleted, dirty;
            public Vector3d n;

            public int V(int j) => j == 0 ? v0 : j == 1 ? v1 : v2;

            public void SetV(int j, int value)
            {
                if (j == 0) v0 = value;
                else if (j == 1) v1 = value;
                else v2 = value;
            }
        }

        struct Vertex
        {
            public Vector3d p;
            public int tstart, tcount;
            public SymmetricMatrix q;
            public bool border;
        }

        struct Ref
        {
            public int tid, tvertex;
        }

        sealed class Simplifier
        {
            Triangle[] triangles;
            int triCount;
            Vertex[] vertices;
            int vertCount;
            Ref[] refs = new Ref[64];
            int refCount;

            bool[] deleted0 = new bool[32];
            bool[] deleted1 = new bool[32];

            public Simplifier(Vector3[] positions, int[] indices)
            {
                vertCount = positions.Length;
                vertices = new Vertex[vertCount];
                for (int i = 0; i < vertCount; i++)
                    vertices[i].p = new Vector3d(positions[i]);

                triangles = new Triangle[indices.Length / 3];
                triCount = 0;
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if (a == b || b == c || c == a) continue;
                    triangles[triCount].v0 = a;
                    triangles[triCount].v1 = b;
                    triangles[triCount].v2 = c;
                    triCount++;
                }
            }

            public void Simplify(int targetCount, double aggressiveness = 7.0)
            {
                int deletedTriangles = 0;
                int startTriangles = triCount;

                for (int iteration = 0; iteration < 100; iteration++)
                {
                    if (startTriangles - deletedTriangles <= targetCount) break;

                    if (iteration % 5 == 0)
                    {
                        UpdateMesh(iteration);
                        if (iteration > 0)
                        {
                            startTriangles = triCount;
                            deletedTriangles = 0;
                        }
                    }

                    for (int i = 0; i < triCount; i++) triangles[i].dirty = false;

                    // Triangles with squared error below the threshold are candidates this pass.
                    double threshold = 1e-9 * Math.Pow(iteration + 3.0, aggressiveness);

                    for (int i = 0; i < triCount; i++)
                    {
                        if (triangles[i].err3 > threshold || triangles[i].deleted || triangles[i].dirty)
                            continue;

                        for (int j = 0; j < 3; j++)
                        {
                            double err = j == 0 ? triangles[i].err0 : j == 1 ? triangles[i].err1 : triangles[i].err2;
                            if (err >= threshold) continue;

                            int i0 = triangles[i].V(j);
                            int i1 = triangles[i].V((j + 1) % 3);
                            if (vertices[i0].border != vertices[i1].border) continue;

                            CalculateError(i0, i1, out Vector3d p);

                            EnsureCapacity(ref deleted0, vertices[i0].tcount);
                            EnsureCapacity(ref deleted1, vertices[i1].tcount);

                            if (Flipped(p, i1, ref vertices[i0], deleted0)) continue;
                            if (Flipped(p, i0, ref vertices[i1], deleted1)) continue;

                            vertices[i0].p = p;
                            vertices[i0].q = vertices[i1].q + vertices[i0].q;

                            int tstart = refCount;
                            UpdateTriangles(i0, ref vertices[i0], deleted0, ref deletedTriangles);
                            UpdateTriangles(i0, ref vertices[i1], deleted1, ref deletedTriangles);
                            int tcount = refCount - tstart;

                            if (tcount <= vertices[i0].tcount)
                            {
                                if (tcount > 0)
                                    Array.Copy(refs, tstart, refs, vertices[i0].tstart, tcount);
                            }
                            else
                            {
                                vertices[i0].tstart = tstart;
                            }
                            vertices[i0].tcount = tcount;
                            break;
                        }

                        if (startTriangles - deletedTriangles <= targetCount) break;
                    }
                }
                CompactMesh();
            }

            public void GetResult(out Vector3[] positions, out int[] indices)
            {
                if (vertices == null || triangles == null)
                {
                    positions = new Vector3[0];
                    indices = new int[0];
                    return;
                }
                if (!compacted) CompactMesh();

                positions = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++)
                    positions[i] = vertices[i].p.ToVector3();

                indices = new int[triCount * 3];
                for (int i = 0; i < triCount; i++)
                {
                    indices[i * 3] = triangles[i].v0;
                    indices[i * 3 + 1] = triangles[i].v1;
                    indices[i * 3 + 2] = triangles[i].v2;
                }
            }

            bool compacted;

            static void EnsureCapacity(ref bool[] arr, int size)
            {
                if (arr.Length < size)
                    arr = new bool[Mathf.NextPowerOfTwo(size)];
            }

            void AddRef(Ref r)
            {
                if (refCount == refs.Length)
                    Array.Resize(ref refs, refs.Length * 2);
                refs[refCount++] = r;
            }

            void UpdateMesh(int iteration)
            {
                if (iteration > 0)
                {
                    int dst = 0;
                    for (int i = 0; i < triCount; i++)
                        if (!triangles[i].deleted)
                            triangles[dst++] = triangles[i];
                    triCount = dst;
                }

                if (iteration == 0)
                {
                    for (int i = 0; i < vertCount; i++)
                        vertices[i].q = default;

                    for (int i = 0; i < triCount; i++)
                    {
                        ref var t = ref triangles[i];
                        Vector3d p0 = vertices[t.v0].p, p1 = vertices[t.v1].p, p2 = vertices[t.v2].p;
                        Vector3d n = Vector3d.Cross(p1 - p0, p2 - p0).Normalized;
                        t.n = n;
                        var plane = new SymmetricMatrix(n.x, n.y, n.z, -Vector3d.Dot(n, p0));
                        vertices[t.v0].q += plane;
                        vertices[t.v1].q += plane;
                        vertices[t.v2].q += plane;
                    }

                    for (int i = 0; i < triCount; i++)
                    {
                        ref var t = ref triangles[i];
                        t.err0 = CalculateError(t.v0, t.v1, out _);
                        t.err1 = CalculateError(t.v1, t.v2, out _);
                        t.err2 = CalculateError(t.v2, t.v0, out _);
                        t.err3 = Math.Min(t.err0, Math.Min(t.err1, t.err2));
                    }
                }

                // rebuild vertex -> triangle references
                for (int i = 0; i < vertCount; i++)
                {
                    vertices[i].tstart = 0;
                    vertices[i].tcount = 0;
                }
                for (int i = 0; i < triCount; i++)
                {
                    vertices[triangles[i].v0].tcount++;
                    vertices[triangles[i].v1].tcount++;
                    vertices[triangles[i].v2].tcount++;
                }
                int tstart = 0;
                for (int i = 0; i < vertCount; i++)
                {
                    vertices[i].tstart = tstart;
                    tstart += vertices[i].tcount;
                    vertices[i].tcount = 0;
                }
                refCount = tstart;
                if (refs.Length < refCount)
                    Array.Resize(ref refs, Mathf.NextPowerOfTwo(refCount));
                for (int i = 0; i < triCount; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int v = triangles[i].V(j);
                        refs[vertices[v].tstart + vertices[v].tcount] = new Ref { tid = i, tvertex = j };
                        vertices[v].tcount++;
                    }
                }

                if (iteration == 0)
                    IdentifyBorders();
            }

            void IdentifyBorders()
            {
                var vcount = new List<int>(16);
                var vids = new List<int>(16);

                for (int i = 0; i < vertCount; i++)
                    vertices[i].border = false;

                for (int i = 0; i < vertCount; i++)
                {
                    vcount.Clear();
                    vids.Clear();
                    ref var v = ref vertices[i];
                    for (int k = 0; k < v.tcount; k++)
                    {
                        ref var t = ref triangles[refs[v.tstart + k].tid];
                        for (int j = 0; j < 3; j++)
                        {
                            int id = t.V(j);
                            int ofs = vids.IndexOf(id);
                            if (ofs < 0)
                            {
                                vcount.Add(1);
                                vids.Add(id);
                            }
                            else vcount[ofs]++;
                        }
                    }
                    for (int j = 0; j < vcount.Count; j++)
                        if (vcount[j] == 1)
                            vertices[vids[j]].border = true;
                }
            }

            bool Flipped(Vector3d p, int i1, ref Vertex v0, bool[] deleted)
            {
                for (int k = 0; k < v0.tcount; k++)
                {
                    ref var t = ref triangles[refs[v0.tstart + k].tid];
                    if (t.deleted) continue;

                    int s = refs[v0.tstart + k].tvertex;
                    int id1 = t.V((s + 1) % 3);
                    int id2 = t.V((s + 2) % 3);

                    if (id1 == i1 || id2 == i1)
                    {
                        deleted[k] = true; // this triangle collapses with the edge
                        continue;
                    }
                    deleted[k] = false;

                    Vector3d d1 = (vertices[id1].p - p).Normalized;
                    Vector3d d2 = (vertices[id2].p - p).Normalized;
                    if (Math.Abs(Vector3d.Dot(d1, d2)) > 0.999) return true; // sliver

                    Vector3d n = Vector3d.Cross(d1, d2).Normalized;
                    if (Vector3d.Dot(n, t.n) < 0.2) return true; // normal flip
                }
                return false;
            }

            void UpdateTriangles(int i0, ref Vertex v, bool[] deleted, ref int deletedTriangles)
            {
                for (int k = 0; k < v.tcount; k++)
                {
                    var r = refs[v.tstart + k];
                    ref var t = ref triangles[r.tid];
                    if (t.deleted) continue;
                    if (deleted[k])
                    {
                        t.deleted = true;
                        deletedTriangles++;
                        continue;
                    }
                    t.SetV(r.tvertex, i0);
                    t.dirty = true;
                    t.err0 = CalculateError(t.v0, t.v1, out _);
                    t.err1 = CalculateError(t.v1, t.v2, out _);
                    t.err2 = CalculateError(t.v2, t.v0, out _);
                    t.err3 = Math.Min(t.err0, Math.Min(t.err1, t.err2));
                    AddRef(r);
                }
            }

            double CalculateError(int idV1, int idV2, out Vector3d result)
            {
                SymmetricMatrix q = vertices[idV1].q + vertices[idV2].q;
                bool border = vertices[idV1].border && vertices[idV2].border;
                double det = q.Det(0, 1, 2, 1, 4, 5, 2, 5, 7);

                if (Math.Abs(det) > 1e-12 && !border)
                {
                    result = new Vector3d(
                        -1.0 / det * q.Det(1, 2, 3, 4, 5, 6, 5, 7, 8),
                         1.0 / det * q.Det(0, 2, 3, 1, 5, 6, 2, 7, 8),
                        -1.0 / det * q.Det(0, 1, 3, 1, 4, 6, 2, 5, 8));
                    return VertexError(q, result.x, result.y, result.z);
                }

                Vector3d p1 = vertices[idV1].p;
                Vector3d p2 = vertices[idV2].p;
                Vector3d p3 = (p1 + p2) * 0.5;
                double e1 = VertexError(q, p1.x, p1.y, p1.z);
                double e2 = VertexError(q, p2.x, p2.y, p2.z);
                double e3 = VertexError(q, p3.x, p3.y, p3.z);
                double min = Math.Min(e1, Math.Min(e2, e3));
                result = min == e1 ? p1 : min == e2 ? p2 : p3;
                return min;
            }

            static double VertexError(in SymmetricMatrix q, double x, double y, double z)
            {
                return q.m0 * x * x + 2 * q.m1 * x * y + 2 * q.m2 * x * z + 2 * q.m3 * x
                     + q.m4 * y * y + 2 * q.m5 * y * z + 2 * q.m6 * y
                     + q.m7 * z * z + 2 * q.m8 * z
                     + q.m9;
            }

            void CompactMesh()
            {
                compacted = true;
                for (int i = 0; i < vertCount; i++)
                    vertices[i].tcount = 0;

                int dst = 0;
                for (int i = 0; i < triCount; i++)
                {
                    if (triangles[i].deleted) continue;
                    triangles[dst++] = triangles[i];
                    vertices[triangles[i].v0].tcount = 1;
                    vertices[triangles[i].v1].tcount = 1;
                    vertices[triangles[i].v2].tcount = 1;
                }
                triCount = dst;

                int vdst = 0;
                for (int i = 0; i < vertCount; i++)
                {
                    if (vertices[i].tcount == 0) continue;
                    vertices[i].tstart = vdst;       // reuse tstart as the remap slot
                    vertices[vdst].p = vertices[i].p;
                    vdst++;
                }
                for (int i = 0; i < triCount; i++)
                {
                    ref var t = ref triangles[i];
                    t.v0 = vertices[t.v0].tstart;
                    t.v1 = vertices[t.v1].tstart;
                    t.v2 = vertices[t.v2].tstart;
                }
                vertCount = vdst;
            }
        }
    }
}
