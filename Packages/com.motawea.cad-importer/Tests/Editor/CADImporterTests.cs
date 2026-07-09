using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace CADImporter.Tests
{
    public class CADImporterTests
    {
        // --- helpers -----------------------------------------------------------------------

        /// <summary>Builds a binary STL of a unit cube (12 triangles) in memory.</summary>
        static byte[] BuildBinaryCubeStl()
        {
            var tris = CubeTriangles();
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(new byte[80]);
            w.Write((uint)(tris.Count / 3));
            for (int t = 0; t < tris.Count; t += 3)
            {
                for (int i = 0; i < 3; i++) w.Write(0f); // facet normal (ignored)
                for (int i = 0; i < 3; i++)
                {
                    var v = tris[t + i];
                    w.Write(v.x); w.Write(v.y); w.Write(v.z);
                }
                w.Write((ushort)0);
            }
            w.Flush();
            return ms.ToArray();
        }

        static List<Vector3> CubeTriangles()
        {
            var c = new Vector3[8];
            for (int i = 0; i < 8; i++)
                c[i] = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);

            // 6 faces, outward winding for a right-handed Z-up system
            int[][] quads =
            {
                new[] { 0, 2, 3, 1 }, // z = 0
                new[] { 4, 5, 7, 6 }, // z = 1
                new[] { 0, 1, 5, 4 }, // y = 0
                new[] { 2, 6, 7, 3 }, // y = 1
                new[] { 0, 4, 6, 2 }, // x = 0
                new[] { 1, 3, 7, 5 }  // x = 1
            };

            var tris = new List<Vector3>(36);
            foreach (var q in quads)
            {
                tris.Add(c[q[0]]); tris.Add(c[q[1]]); tris.Add(c[q[2]]);
                tris.Add(c[q[0]]); tris.Add(c[q[2]]); tris.Add(c[q[3]]);
            }
            return tris;
        }

        static CADProcessOptions MetersNoConvert(bool weld = true) => new CADProcessOptions
        {
            Scale = 1f,
            Orientation = SourceOrientation.YUpLeftHanded,
            Weld = weld,
            WeldTolerance = 1e-5f,
            RecalculateNormals = false,
            SmoothingAngleDeg = 30f
        };

        // --- STL ---------------------------------------------------------------------------

        [Test]
        public void StlBinary_ParsesCube()
        {
            var model = StlParser.Parse(BuildBinaryCubeStl(), "cube");
            Assert.AreEqual(1, model.Root.Children.Count);
            Assert.AreEqual(12, model.TotalTriangles);
            Assert.AreEqual(36, model.TotalVertices); // raw soup before processing
        }

        [Test]
        public void StlAscii_ParsesMultipleSolids()
        {
            var sb = new StringBuilder();
            for (int s = 0; s < 2; s++)
            {
                sb.AppendLine($"solid part{s}");
                sb.AppendLine("facet normal 0 0 1");
                sb.AppendLine(" outer loop");
                sb.AppendLine($"  vertex {s} 0 0");
                sb.AppendLine("  vertex 1 0 0");
                sb.AppendLine("  vertex 0 1 0");
                sb.AppendLine(" endloop");
                sb.AppendLine("endfacet");
                sb.AppendLine($"endsolid part{s}");
            }
            var model = StlParser.Parse(Encoding.ASCII.GetBytes(sb.ToString()), "multi");
            Assert.AreEqual(2, model.Root.Children.Count);
            Assert.AreEqual("part0", model.Root.Children[0].Name);
            Assert.AreEqual(2, model.TotalTriangles);
        }

        [Test]
        public void StlCube_WeldsTo24VerticesWithHardEdges()
        {
            var model = StlParser.Parse(BuildBinaryCubeStl(), "cube");
            MeshProcessor.Process(model, MetersNoConvert());
            var mesh = model.Root.Children[0].Mesh;
            // A cube with 90° hard edges welds to exactly 24 vertices (4 per face).
            Assert.AreEqual(24, mesh.VertexCount);
            Assert.AreEqual(12, mesh.TriangleCount);
            Assert.IsNotNull(mesh.Normals);
        }

        [Test]
        public void StlCube_NormalsPointOutward()
        {
            var model = StlParser.Parse(BuildBinaryCubeStl(), "cube");
            MeshProcessor.Process(model, MetersNoConvert());
            var mesh = model.Root.Children[0].Mesh;
            Vector3 center = new Vector3(0.5f, 0.5f, 0.5f);
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                float dot = Vector3.Dot(mesh.Normals[i], (mesh.Positions[i] - center).normalized);
                Assert.Greater(dot, 0.5f, $"normal {i} does not point outward");
            }
        }

        [Test]
        public void ZUpConversion_MapsAxesAndKeepsOutwardNormals()
        {
            var model = StlParser.Parse(BuildBinaryCubeStl(), "cube");
            var opts = MetersNoConvert();
            opts.Orientation = SourceOrientation.ZUpRightHanded;
            opts.Scale = 0.001f; // mm -> m
            MeshProcessor.Process(model, opts);
            var mesh = model.Root.Children[0].Mesh;

            var bounds = new Bounds(mesh.Positions[0], Vector3.zero);
            foreach (var p in mesh.Positions) bounds.Encapsulate(p);
            Assert.AreEqual(0.001f, bounds.size.x, 1e-6f);

            // Winding must have been flipped so normals still point outward.
            Vector3 center = bounds.center;
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                float dot = Vector3.Dot(mesh.Normals[i], (mesh.Positions[i] - center).normalized);
                Assert.Greater(dot, 0.5f, $"normal {i} flipped inward after axis conversion");
            }
        }

        // --- PLY ---------------------------------------------------------------------------

        [Test]
        public void PlyAscii_ParsesVerticesFacesAndColors()
        {
            string ply = string.Join("\n",
                "ply",
                "format ascii 1.0",
                "element vertex 4",
                "property float x",
                "property float y",
                "property float z",
                "property uchar red",
                "property uchar green",
                "property uchar blue",
                "element face 2",
                "property list uchar int vertex_indices",
                "end_header",
                "0 0 0 255 0 0",
                "1 0 0 0 255 0",
                "1 1 0 0 0 255",
                "0 1 0 255 255 255",
                "3 0 1 2",
                "4 0 1 2 3", // quad fan-triangulates to 2 tris
                "");
            var model = PlyParser.Parse(Encoding.ASCII.GetBytes(ply), "quad");
            var mesh = model.Root.Children[0].Mesh;
            Assert.AreEqual(4, mesh.VertexCount);
            Assert.AreEqual(3, mesh.TriangleCount);
            Assert.IsNotNull(mesh.Colors);
            Assert.AreEqual(255, mesh.Colors[0].r);
        }

        [Test]
        public void PlyBinaryLittleEndian_Parses()
        {
            using var ms = new MemoryStream();
            var header = Encoding.ASCII.GetBytes(string.Join("\n",
                "ply",
                "format binary_little_endian 1.0",
                "element vertex 3",
                "property float x",
                "property float y",
                "property float z",
                "element face 1",
                "property list uchar int vertex_indices",
                "end_header") + "\n");
            ms.Write(header, 0, header.Length);
            using var w = new BinaryWriter(ms);
            w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write(1f); w.Write(0f); w.Write(0f);
            w.Write(0f); w.Write(1f); w.Write(0f);
            w.Write((byte)3); w.Write(0); w.Write(1); w.Write(2);
            w.Flush();

            var model = PlyParser.Parse(ms.ToArray(), "tri");
            var mesh = model.Root.Children[0].Mesh;
            Assert.AreEqual(3, mesh.VertexCount);
            Assert.AreEqual(1, mesh.TriangleCount);
            Assert.AreEqual(new Vector3(1, 0, 0), mesh.Positions[1]);
        }

        // --- OBJ ---------------------------------------------------------------------------

        [Test]
        public void Obj_ParsesGroupsAndMaterialSubmeshes()
        {
            string obj = string.Join("\n",
                "v 0 0 0",
                "v 1 0 0",
                "v 0 1 0",
                "v 1 1 0",
                "g partA",
                "usemtl red",
                "f 1 2 3",
                "usemtl blue",
                "f 2 4 3",
                "g partB",
                "f 1 2 4",
                "");
            var model = ObjParser.Parse(obj, "test");
            Assert.AreEqual(2, model.Root.Children.Count);
            var partA = model.Root.Children[0].Mesh;
            Assert.AreEqual(2, partA.Submeshes.Length);
            Assert.AreEqual("red", partA.SubmeshMaterials[0]);
            Assert.AreEqual("blue", partA.SubmeshMaterials[1]);
            Assert.AreEqual(1, model.Root.Children[1].Mesh.TriangleCount);
        }

        [Test]
        public void Obj_SupportsNegativeAndSlashIndices()
        {
            string obj = string.Join("\n",
                "v 0 0 0",
                "vt 0 0",
                "vn 0 0 1",
                "v 1 0 0",
                "v 0 1 0",
                "f -3/1/1 -2/1/1 -1/1/1",
                "");
            var model = ObjParser.Parse(obj, "neg");
            var mesh = model.Root.Children[0].Mesh;
            Assert.AreEqual(1, mesh.TriangleCount);
            Assert.AreEqual(new Vector3(0, 0, 1), mesh.Normals[0]);
        }

        // --- processing ----------------------------------------------------------------------

        [Test]
        public void Weld_MergesDuplicatesAndDropsDegenerates()
        {
            var mesh = new CADMeshData
            {
                Positions = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 0) // degenerate tri
                },
                Submeshes = new[] { new[] { 0, 1, 2, 3, 4, 5 } }
            };
            MeshProcessor.RecalculateSmoothNormals(mesh, 1e-5f, 30f);
            MeshProcessor.Weld(mesh, 1e-5f);
            Assert.AreEqual(3, mesh.VertexCount);
            Assert.AreEqual(1, mesh.TriangleCount);
        }

        [Test]
        public void Decimator_ReducesGridAndKeepsValidIndices()
        {
            // 24x24 flat grid: 1152 triangles
            const int n = 24;
            var pos = new List<Vector3>();
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                    pos.Add(new Vector3(x, 0, y));
            var idx = new List<int>();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                    idx.AddRange(new[] { a, c, b, b, c, d });
                }

            int srcTris = idx.Count / 3;
            MeshDecimator.Decimate(pos.ToArray(), idx.ToArray(), 0.25f,
                out var outPos, out var outIdx);

            int outTris = outIdx.Length / 3;
            Assert.Greater(outTris, 0);
            Assert.Less(outTris, srcTris / 2, "decimator failed to reduce triangle count");
            foreach (int i in outIdx)
                Assert.That(i, Is.InRange(0, outPos.Length - 1));

            // Flat grid must stay flat.
            foreach (var p in outPos)
                Assert.AreEqual(0f, p.y, 1e-4f);
        }

        [Test]
        public void PositionWeldedCopy_MergesHardEdgeSplits()
        {
            // The processed cube is split to 24 vertices for hard-edge shading; the
            // topology copy used for decimation must merge it back to a closed 8-vertex solid.
            var model = StlParser.Parse(BuildBinaryCubeStl(), "cube");
            MeshProcessor.Process(model, MetersNoConvert());
            var render = model.Root.Children[0].Mesh;
            Assert.AreEqual(24, render.VertexCount);

            var topo = MeshProcessor.PositionWeldedCopy(render, 1e-5f);
            Assert.AreEqual(8, topo.VertexCount);
            Assert.AreEqual(12, topo.TriangleCount);
            Assert.AreEqual(0, CountBoundaryEdges(topo.CombinedIndices()), "cube topology must be closed");
            Assert.AreEqual(24, render.VertexCount, "render mesh must not be modified");
        }

        [Test]
        public void Decimator_ClosedMeshStaysClosed_AcrossHardEdges()
        {
            // Regression test for LOD holes: a hard-edged closed cylinder, run through the
            // full visual pipeline (which splits hard edges), must survive decimation with
            // no boundary edges when decimated via the position-welded topology copy.
            var mesh = BuildCylinderSoup(64, 8, 0.5f, 2f);
            MeshProcessor.RecalculateSmoothNormals(mesh, 1e-5f, 30f);
            MeshProcessor.Weld(mesh, 1e-5f); // shading weld: rim vertices stay split

            var topo = MeshProcessor.PositionWeldedCopy(mesh, 1e-5f);
            Assert.AreEqual(0, CountBoundaryEdges(topo.CombinedIndices()),
                "position-welded cylinder must be closed before decimation");

            int srcTris = topo.TriangleCount;
            var lod = MeshDecimator.Decimate(topo, 0.4f);
            Assert.Greater(lod.TriangleCount, 0);
            Assert.Less(lod.TriangleCount, srcTris, "decimation must reduce triangles");
            Assert.AreEqual(0, CountBoundaryEdges(lod.CombinedIndices()),
                "decimated LOD must have no holes (boundary edges)");
        }

        /// <summary>Edges referenced by exactly one triangle — every one is a hole border.</summary>
        static int CountBoundaryEdges(int[] tris)
        {
            var counts = new Dictionary<(int, int), int>();
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[i + e], b = tris[i + (e + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    counts.TryGetValue(key, out int n);
                    counts[key] = n + 1;
                }
            }
            int boundary = 0;
            foreach (var kv in counts)
                if (kv.Value == 1) boundary++;
            return boundary;
        }

        /// <summary>Closed cylinder as triangle soup (like STL): side quads + cap fans.</summary>
        static CADMeshData BuildCylinderSoup(int segments, int rings, float radius, float height)
        {
            var tris = new List<Vector3>();
            Vector3 P(int s, float y) =>
                new Vector3(radius * Mathf.Cos(2f * Mathf.PI * s / segments), y,
                            radius * Mathf.Sin(2f * Mathf.PI * s / segments));

            for (int r = 0; r < rings; r++)
            {
                float y0 = height * r / rings, y1 = height * (r + 1) / rings;
                for (int s = 0; s < segments; s++)
                {
                    Vector3 a = P(s, y0), b = P(s + 1, y0), c = P(s, y1), d = P(s + 1, y1);
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }
            var bottom = new Vector3(0, 0, 0);
            var top = new Vector3(0, height, 0);
            for (int s = 0; s < segments; s++)
            {
                tris.Add(bottom); tris.Add(P(s, 0)); tris.Add(P(s + 1, 0));
                tris.Add(top); tris.Add(P(s + 1, height)); tris.Add(P(s, height));
            }

            return new CADMeshData
            {
                Name = "cylinder",
                Positions = tris.ToArray(),
                Submeshes = new[] { CADMeshData.SequentialIndices(tris.Count) }
            };
        }

        [Test]
        public void Decimator_PreservesSubmeshCount()
        {
            var model = StlParser.Parse(BuildBinaryCubeStl(), "cube");
            MeshProcessor.Process(model, MetersNoConvert());
            var mesh = model.Root.Children[0].Mesh;
            var result = MeshDecimator.Decimate(mesh, 1f); // no-op quality still compacts
            Assert.AreEqual(mesh.Submeshes.Length, result.Submeshes.Length);
            Assert.AreEqual(mesh.TriangleCount, result.TriangleCount);
        }

        [Test]
        public void UnityMeshBuilder_BuildsValidMesh()
        {
            var model = StlParser.Parse(BuildBinaryCubeStl(), "cube");
            MeshProcessor.Process(model, MetersNoConvert());
            var mesh = UnityMeshBuilder.Build(model.Root.Children[0].Mesh);
            Assert.AreEqual(24, mesh.vertexCount);
            Assert.AreEqual(36, mesh.triangles.Length);
            Assert.Greater(mesh.bounds.size.magnitude, 0f);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void RuntimeImporter_ImportsStlFromDisk()
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"cadimporter_test_{Guid.NewGuid():N}.stl");
            File.WriteAllBytes(tmp, BuildBinaryCubeStl());
            GameObject go = null;
            try
            {
                go = CADRuntimeImporter.Import(tmp, new CADRuntimeImportSettings
                {
                    sourceUnit = SourceUnit.Millimeters,
                    generateColliders = true,
                    colliderQuality = 1f
                });
                Assert.IsNotNull(go);
                var info = go.GetComponent<CADModelInfo>();
                Assert.IsNotNull(info);
                Assert.AreEqual(12, info.totalTriangles);
                Assert.IsNotNull(go.GetComponentInChildren<MeshFilter>());
                Assert.IsNotNull(go.GetComponentInChildren<MeshCollider>());
            }
            finally
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                File.Delete(tmp);
            }
        }

        // --- glTF / GLB --------------------------------------------------------------------

        [Test]
        public void GltfGlb_PreservesNodeTransformAndParsesMaterial()
        {
            byte[] glb = BuildQuadGlb();
            var model = GltfParser.Parse(glb, "quad", null);

            Assert.AreEqual("GLB", model.Format);
            Assert.AreEqual(1, model.Root.Children.Count);
            var node = model.Root.Children[0];
            var mesh = node.Mesh;
            Assert.IsNotNull(mesh);
            Assert.AreEqual(4, mesh.VertexCount);
            Assert.AreEqual(2, mesh.TriangleCount);
            // The node transform is preserved (not baked). glTF translation (10,0,0) mirrors to
            // Unity (-10,0,0); vertices stay in node-local space.
            Assert.IsTrue(node.HasLocalTransform);
            Assert.AreEqual(-10f, node.LocalPosition.x, 1e-4f);
            Assert.AreEqual(0f, mesh.Positions[0].x, 1e-4f);
            Assert.AreEqual(1f, mesh.Positions[1].x, 1e-4f);
            // glTF's top-down V is flipped to Unity's bottom-up convention.
            Assert.AreEqual(0f, mesh.UV[0].y, 1e-4f);

            Assert.AreEqual(1, model.Materials.Count);
            var mat = model.Materials[0];
            Assert.AreEqual(1f, mat.Color.r, 1e-4f);
            Assert.AreEqual(0f, mat.Color.g, 1e-4f);
            Assert.AreEqual(0.5f, mat.Smoothness, 1e-4f); // 1 - roughness(0.5)
            Assert.IsNotNull(mat.BaseColorTex);
            Assert.AreEqual(CADAlphaMode.Opaque, mat.AlphaMode);
        }

        [Test]
        public void Gltf_ThroughPipeline_MirrorsLocalMeshAndKeepsNodeTransform()
        {
            var model = GltfParser.Parse(BuildQuadGlb(), "quad", null);
            var node = model.Root.Children[0];
            var opts = MetersNoConvert();
            opts.Orientation = SourceOrientation.YUpRightHanded; // how the glTF importer runs it
            MeshProcessor.Process(model, opts);
            var mesh = node.Mesh;
            // Pipeline mirrors X on the local mesh: local v1.x = 1 -> -1.
            Assert.AreEqual(-1f, mesh.Positions[1].x, 1e-4f);
            Assert.IsNotNull(mesh.Normals);
            // Node transform is untouched by MeshProcessor (it only transforms geometry).
            Assert.AreEqual(-10f, node.LocalPosition.x, 1e-4f);
        }

        [Test]
        public void Gltf_RejectsDracoCompressionWithClearError()
        {
            string json = "{ \"asset\": { \"version\": \"2.0\" }, " +
                          "\"extensionsRequired\": [\"KHR_draco_mesh_compression\"], " +
                          "\"scenes\": [{ \"nodes\": [] }], \"buffers\": [] }";
            byte[] glb = WrapGlb(json, new byte[0]);
            var ex = Assert.Throws<InvalidDataException>(() => GltfParser.Parse(glb, "x", null));
            StringAssert.Contains("compression", ex.Message);
        }

        [Test]
        public void GltfRepack_MetallicRoughnessAndOcclusionChannels()
        {
            var mr = new[] { new Color32(9, 200, 128, 77) };
            TextureRepack.MetallicRoughnessGltfToUnity(mr);
            Assert.AreEqual(128, mr[0].r);          // metallic from glTF B
            Assert.AreEqual(255 - 200, mr[0].a);    // smoothness = 1 - roughness(G)

            var occ = new[] { new Color32(180, 5, 6, 7) };
            TextureRepack.OcclusionGltfToUnity(occ);
            Assert.AreEqual(180, occ[0].g);         // occlusion sampled from G in Unity
        }

        [Test]
        public void Gltf_GetExternalResources_ListsSidecarsExcludingDataUris()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"gltfext_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "scene.gltf");
            try
            {
                File.WriteAllText(path,
                    "{ \"asset\": {\"version\":\"2.0\"}, " +
                    "\"buffers\": [ {\"uri\":\"scene.bin\"}, {\"uri\":\"data:application/octet-stream;base64,AAAA\"} ], " +
                    "\"images\": [ {\"uri\":\"textures/wood%20grain.png\"} ] }");
                var ext = GltfParser.GetExternalResources(path);
                Assert.AreEqual(2, ext.Count);
                Assert.Contains("scene.bin", ext);
                Assert.Contains("textures/wood grain.png", ext); // URI-unescaped
                Assert.IsFalse(ext.Exists(u => u.StartsWith("data:")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void ConvertPlacement_MatchesVertexReflectionForEveryOrientation()
        {
            // A preserved node hierarchy is correct only if a node's transform is converted with
            // the SAME reflection the mesh pipeline applies to vertices. Verify the invariant
            //   Reflect(placement * p) == ConvertedPlacement * Reflect(p)
            // for an arbitrary placement and point, across all orientations. A wrong quaternion
            // sign here is exactly what scatters ("explodes") an assembly.
            var orientations = new[]
            {
                SourceOrientation.ZUpRightHanded,
                SourceOrientation.YUpRightHanded,
                SourceOrientation.YUpLeftHanded
            };
            var pos = new Vector3(10f, 20f, 30f);
            var rot = Quaternion.Euler(35f, 47f, 61f);
            var p = new Vector3(1.5f, -2.5f, 3.5f);

            foreach (var o in orientations)
            {
                Vector3 worldSrc = pos + rot * p;              // placement applied in source space
                Vector3 expected = Reflect(o, worldSrc);       // then converted like a vertex

                var cp = pos; var cr = rot; var cs = Vector3.one;
                MeshProcessor.ConvertPlacement(o, ref cp, ref cr, ref cs);
                Vector3 actual = cp + cr * Reflect(o, p);      // converted placement * converted point

                Assert.Less(Vector3.Distance(actual, expected), 1e-3f,
                    $"ConvertPlacement inconsistent with vertex reflection for {o}");
            }
        }

        static Vector3 Reflect(SourceOrientation o, Vector3 v)
        {
            switch (o)
            {
                case SourceOrientation.ZUpRightHanded: return new Vector3(v.x, v.z, v.y);
                case SourceOrientation.YUpRightHanded: return new Vector3(-v.x, v.y, v.z);
                default: return v;
            }
        }

        /// <summary>Builds an in-memory GLB: a unit quad translated by (10,0,0) with a red
        /// metallic-roughness material carrying an embedded (dummy) base-colour image.</summary>
        static byte[] BuildQuadGlb()
        {
            var bin = new List<byte>();
            void Align4() { while (bin.Count % 4 != 0) bin.Add(0); }
            void AddF(float f) => bin.AddRange(BitConverter.GetBytes(f));
            void AddU16(ushort v) => bin.AddRange(BitConverter.GetBytes(v));

            int posOff = bin.Count;
            float[] pos = { 0,0,0, 1,0,0, 1,1,0, 0,1,0 };
            foreach (var f in pos) AddF(f);
            int uvOff = bin.Count;
            float[] uv = { 0,1, 1,1, 1,0, 0,0 };
            foreach (var f in uv) AddF(f);
            int idxOff = bin.Count;
            ushort[] idx = { 0,1,2, 0,2,3 };
            foreach (var i in idx) AddU16(i);
            Align4();
            int imgOff = bin.Count;
            byte[] img = { 0xDE, 0xAD, 0xBE, 0xEF };
            bin.AddRange(img);
            Align4();

            byte[] binArr = bin.ToArray();
            string json = "{" +
              "\"asset\":{\"version\":\"2.0\"}," +
              "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
              "\"nodes\":[{\"name\":\"quad\",\"mesh\":0,\"translation\":[10,0,0]}]," +
              "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"TEXCOORD_0\":1},\"indices\":2,\"material\":0}]}]," +
              "\"materials\":[{\"name\":\"Red\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,0,0,1],\"metallicFactor\":0.0,\"roughnessFactor\":0.5,\"baseColorTexture\":{\"index\":0}}}]," +
              "\"textures\":[{\"source\":0}]," +
              "\"images\":[{\"bufferView\":3,\"mimeType\":\"image/png\"}]," +
              "\"accessors\":[" +
                "{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\"}," +
                "{\"bufferView\":1,\"componentType\":5126,\"count\":4,\"type\":\"VEC2\"}," +
                "{\"bufferView\":2,\"componentType\":5123,\"count\":6,\"type\":\"SCALAR\"}]," +
              "\"bufferViews\":[" +
                "{\"buffer\":0,\"byteOffset\":" + posOff + ",\"byteLength\":48}," +
                "{\"buffer\":0,\"byteOffset\":" + uvOff + ",\"byteLength\":32}," +
                "{\"buffer\":0,\"byteOffset\":" + idxOff + ",\"byteLength\":12}," +
                "{\"buffer\":0,\"byteOffset\":" + imgOff + ",\"byteLength\":" + img.Length + "}]," +
              "\"buffers\":[{\"byteLength\":" + binArr.Length + "}]}";
            return WrapGlb(json, binArr);
        }

        static byte[] WrapGlb(string json, byte[] bin)
        {
            var jsonPad = new List<byte>(Encoding.UTF8.GetBytes(json));
            while (jsonPad.Count % 4 != 0) jsonPad.Add(0x20);
            var binPad = new List<byte>(bin);
            while (binPad.Count % 4 != 0) binPad.Add(0);

            var o = new List<byte>();
            void U32(uint v) => o.AddRange(BitConverter.GetBytes(v));
            int total = 12 + 8 + jsonPad.Count + (bin.Length > 0 ? 8 + binPad.Count : 0);
            o.AddRange(new byte[] { 0x67, 0x6C, 0x54, 0x46 });
            U32(2); U32((uint)total);
            U32((uint)jsonPad.Count); U32(0x4E4F534A); o.AddRange(jsonPad);
            if (bin.Length > 0) { U32((uint)binPad.Count); U32(0x004E4942); o.AddRange(binPad); }
            return o.ToArray();
        }
    }
}
