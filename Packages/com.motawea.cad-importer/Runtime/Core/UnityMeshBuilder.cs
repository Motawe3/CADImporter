using UnityEngine;
using UnityEngine.Rendering;

namespace CADImporter
{
    /// <summary>Converts intermediate <see cref="CADMeshData"/> into UnityEngine meshes.</summary>
    public static class UnityMeshBuilder
    {
        public static Mesh Build(CADMeshData data)
        {
            var mesh = new Mesh { name = data.Name };

            // 16-bit index buffers halve GPU index memory; use them whenever possible.
            if (data.Positions.Length > ushort.MaxValue)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(data.Positions);
            if (data.Normals != null && data.Normals.Length == data.Positions.Length)
                mesh.SetNormals(data.Normals);
            if (data.Colors != null && data.Colors.Length == data.Positions.Length)
                mesh.SetColors(data.Colors);
            if (data.UV != null && data.UV.Length == data.Positions.Length)
                mesh.SetUVs(0, data.UV);

            mesh.subMeshCount = data.Submeshes.Length;
            for (int i = 0; i < data.Submeshes.Length; i++)
                mesh.SetTriangles(data.Submeshes[i], i, false);

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Builds a position-only mesh for physics collision use.</summary>
        public static Mesh BuildCollision(CADMeshData data, string name)
        {
            var mesh = new Mesh { name = name };
            if (data.Positions.Length > ushort.MaxValue)
                mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(data.Positions);
            mesh.SetTriangles(data.CombinedIndices(), 0, false);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
