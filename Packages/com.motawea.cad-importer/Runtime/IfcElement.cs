using System;
using System.Collections.Generic;
using UnityEngine;

namespace CADImporter
{
    /// <summary>One IFC property, flattened as "PsetName.PropertyName" → value string.</summary>
    [Serializable]
    public struct IfcProperty
    {
        public string name;
        public string value;

        public IfcProperty(string name, string value)
        {
            this.name = name;
            this.value = value;
        }
    }

    /// <summary>
    /// BIM identity carried on an imported IFC element: entity type, GlobalId and property sets.
    /// Attached by the IFC importer to every element GameObject, so scene objects can be mapped
    /// back to the source model (issue tracking, dashboards, BCF) and queried for BIM data
    /// (fire rating, load bearing, storey, ...) at runtime or in editor tooling.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IfcElement : MonoBehaviour
    {
        /// <summary>IFC entity type, e.g. "IfcWallStandardCase".</summary>
        public string ifcType;

        /// <summary>IFC GlobalId (22-char base64 GUID), stable across exports of the same model.</summary>
        public string globalId;

        /// <summary>Flattened property sets: "Pset_WallCommon.FireRating" → "REI60".</summary>
        public List<IfcProperty> properties = new List<IfcProperty>();

        /// <summary>Value of a property by its flattened name; null when absent.</summary>
        public string GetProperty(string name)
        {
            for (int i = 0; i < properties.Count; i++)
                if (properties[i].name == name)
                    return properties[i].value;
            return null;
        }
    }

    /// <summary>
    /// BIM identity parsed for one <see cref="CADNode"/>, before GameObjects exist. The asset
    /// builder turns this into an <see cref="IfcElement"/> component. Null on non-BIM formats.
    /// </summary>
    public sealed class IfcElementData
    {
        public string IfcType;
        public string GlobalId;
        public List<IfcProperty> Properties;
    }
}
