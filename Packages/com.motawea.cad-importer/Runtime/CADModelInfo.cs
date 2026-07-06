using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Metadata attached to the root of every imported CAD model.
    /// Useful for digital-twin tooling that needs to map scene objects back to source CAD data.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CADModelInfo : MonoBehaviour
    {
        public string sourceFile;
        public string sourceFormat;
        public SourceUnit sourceUnit;
        public float appliedScale = 1f;
        public int totalVertices;
        public int totalTriangles;
        public int partCount;
        public string importedAt;
    }
}
