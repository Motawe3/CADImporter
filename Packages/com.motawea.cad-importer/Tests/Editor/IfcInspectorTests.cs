using System.Linq;
using CADImporter.Editor;
using NUnit.Framework;
using UnityEngine;

namespace CADImporter.Tests
{
    /// <summary>
    /// Category hiding in the IFC Inspector. The rules under test are cross-category: an
    /// element is hidden when any mode's rule matches it, so toggling a type must not resurrect
    /// elements a storey rule still hides.
    /// </summary>
    public class IfcInspectorTests
    {
        GameObject root;

        /// <summary>
        /// Two storeys of two elements each:
        ///   root
        ///     Storey01 (IfcBuildingStorey)   Door_1 (IfcDoor)   Wall_1 (IfcWall, load-bearing)
        ///     Storey02 (IfcBuildingStorey)   Door_2 (IfcDoor)   Wall_2 (IfcWall, load-bearing)
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Model");
            for (int s = 1; s <= 2; s++)
            {
                var storey = MakeElement($"Storey{s:00}", "IfcBuildingStorey", root.transform, withMesh: false);
                MakeElement($"Door_{s}", "IfcDoor", storey.transform);
                var wall = MakeElement($"Wall_{s}", "IfcWall", storey.transform);
                wall.GetComponent<IfcElement>().properties.Add(
                    new IfcProperty("Pset_WallCommon.LoadBearing", "true"));
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
        }

        static GameObject MakeElement(string name, string type, Transform parent, bool withMesh = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            var elem = go.AddComponent<IfcElement>();
            elem.ifcType = type;
            elem.globalId = name;
            if (withMesh)
            {
                go.AddComponent<MeshFilter>().sharedMesh = OneTriangle();
                go.AddComponent<MeshRenderer>();
            }
            return go;
        }

        static Mesh OneTriangle()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new[] { Vector3.zero, Vector3.up, Vector3.right });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, false);
            return mesh;
        }

        static IfcInspectorGroup Group(IfcInspectorModel m, IfcInspectorMode mode, string label) =>
            m.Groups(mode).Single(g => g.Label == label);

        static bool HiddenByName(IfcInspectorModel m, string name) =>
            m.Elements.Single(e => e.Go.name == name).Go != null &&
            UnityEditor.SceneVisibilityManager.instance.IsHidden(
                m.Elements.Single(e => e.Go.name == name).Go, false);

        // --- scanning ---------------------------------------------------------------------

        [Test]
        public void Scan_SkipsSpatialContainersAndClassifiesEveryMode()
        {
            var m = IfcInspectorModel.Scan(root);

            // Storeys own no geometry of their own, so they are not legend elements.
            Assert.AreEqual(4, m.Elements.Length);
            CollectionAssert.AreEquivalent(
                new[] { "Door_1", "Wall_1", "Door_2", "Wall_2" },
                m.Elements.Select(e => e.Go.name).ToArray());

            CollectionAssert.AreEquivalent(
                new[] { "IfcDoor", "IfcWall" },
                m.Groups(IfcInspectorMode.ByType).Select(g => g.Label).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { "Storey01", "Storey02" },
                m.Groups(IfcInspectorMode.ByStorey).Select(g => g.Label).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { "Load-bearing", "Not specified" },
                m.Groups(IfcInspectorMode.ByLoadBearing).Select(g => g.Label).ToArray());
        }

        [Test]
        public void Scan_CountsEverySubmeshNotJustTheFirst()
        {
            // A two-material part carries one submesh per material; counting submesh 0 only
            // would report half its triangles.
            var mesh = new Mesh();
            mesh.SetVertices(new[]
            {
                Vector3.zero, Vector3.up, Vector3.right,
                Vector3.one, Vector3.forward, Vector3.left
            });
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, false);
            mesh.SetTriangles(new[] { 3, 4, 5 }, 1, false);

            var multi = MakeElement("Slab_1", "IfcSlab", root.transform);
            multi.GetComponent<MeshFilter>().sharedMesh = mesh;

            var m = IfcInspectorModel.Scan(root);
            Assert.AreEqual(2, Group(m, IfcInspectorMode.ByType, "IfcSlab").Triangles);
        }

        // --- cross-category hiding --------------------------------------------------------

        [Test]
        public void HidingAStorey_IsStillCountedAfterSwitchingToTypes()
        {
            // The "Show All" button vanished here: the hidden count used to be derived from the
            // current mode's groups, so switching category lost sight of the hidden elements.
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);
            Assert.AreEqual(2, m.HiddenElements);

            m.Groups(IfcInspectorMode.ByType); // switch category
            Assert.AreEqual(2, m.HiddenElements, "hidden count must survive a mode switch");
        }

        [Test]
        public void TogglingDoorsBackOn_LeavesTheHiddenStoreysDoorHidden()
        {
            // The reported bug: hide a storey, switch to types, toggle doors off then on, and
            // the hidden storey's door reappeared floating on its own.
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);

            m.SetCategoryHidden(IfcInspectorMode.ByType, "IfcDoor", true);
            Assert.AreEqual(3, m.HiddenElements); // Door_1, Door_2, Wall_2

            m.SetCategoryHidden(IfcInspectorMode.ByType, "IfcDoor", false);

            Assert.AreEqual(2, m.HiddenElements, "Storey02 must still hide its own elements");
            Assert.IsTrue(HiddenByName(m, "Door_2"), "Door_2 is in the hidden storey");
            Assert.IsTrue(HiddenByName(m, "Wall_2"), "Wall_2 is in the hidden storey");
            Assert.IsFalse(HiddenByName(m, "Door_1"), "Door_1 was un-hidden");
        }

        [Test]
        public void CategoryHiddenByAnotherMode_ReadsAsPartial()
        {
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);

            var doors = Group(m, IfcInspectorMode.ByType, "IfcDoor");
            Assert.AreEqual(IfcCategoryState.Partial, m.StateOf(doors, IfcInspectorMode.ByType));
            Assert.AreEqual(1, doors.HiddenCount, "one of the two doors is in the hidden storey");

            m.SetCategoryHidden(IfcInspectorMode.ByType, "IfcDoor", true);
            Assert.AreEqual(IfcCategoryState.Hidden, m.StateOf(doors, IfcInspectorMode.ByType),
                "the eye reflects this category's own rule");
            Assert.AreEqual(2, doors.HiddenCount);
        }

        [Test]
        public void ShowAll_ClearsRulesFromEveryMode()
        {
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);
            m.SetCategoryHidden(IfcInspectorMode.ByType, "IfcDoor", true);
            Assert.AreEqual(3, m.HiddenElements);

            m.ShowAll();

            Assert.AreEqual(0, m.HiddenElements);
            Assert.IsFalse(m.IsCategoryHidden(IfcInspectorMode.ByStorey, "Storey02"));
            Assert.IsFalse(m.IsCategoryHidden(IfcInspectorMode.ByType, "IfcDoor"));
            foreach (var e in m.Elements)
                Assert.IsFalse(HiddenByName(m, e.Go.name), $"{e.Go.name} still hidden");
        }

        [Test]
        public void Solo_ShowsOnlyThatCategoryAcrossEveryMode()
        {
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);

            m.Solo(IfcInspectorMode.ByType, "IfcDoor");

            // A solo that left the storey rule standing would not actually isolate the doors.
            Assert.IsFalse(m.IsCategoryHidden(IfcInspectorMode.ByStorey, "Storey02"));
            Assert.IsTrue(m.IsCategoryHidden(IfcInspectorMode.ByType, "IfcWall"));
            Assert.AreEqual(2, m.HiddenElements, "both walls, no doors");
            Assert.IsFalse(HiddenByName(m, "Door_2"), "a soloed door shows even in a hidden storey");
        }

        [Test]
        public void Restore_DropsEveryRuleAndUnhidesTheModel()
        {
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);
            m.SetCategoryHidden(IfcInspectorMode.ByType, "IfcDoor", true);

            m.Restore();

            Assert.AreEqual(0, m.HiddenElements);
            foreach (var e in m.Elements)
                Assert.IsFalse(HiddenByName(m, e.Go.name));
        }

        [Test]
        public void Rescan_KeepsTheHideRulesForTheSameRoot()
        {
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);

            // Rules are labels, not object references, so a scene edit must not drop them.
            var rescanned = IfcInspectorModel.Scan(root, m);

            Assert.IsTrue(rescanned.IsCategoryHidden(IfcInspectorMode.ByStorey, "Storey02"));
            Assert.AreEqual(2, rescanned.HiddenElements);
        }

        [Test]
        public void Rescan_DoesNotCarryRulesToADifferentModel()
        {
            var m = IfcInspectorModel.Scan(root);
            m.SetCategoryHidden(IfcInspectorMode.ByStorey, "Storey02", true);

            var other = new GameObject("Other Model");
            try
            {
                var storey = MakeElement("Storey01", "IfcBuildingStorey", other.transform, withMesh: false);
                MakeElement("Door_1", "IfcDoor", storey.transform);

                var scanned = IfcInspectorModel.Scan(other, m);

                Assert.IsFalse(scanned.IsCategoryHidden(IfcInspectorMode.ByStorey, "Storey02"));
                Assert.AreEqual(0, scanned.HiddenElements);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }
    }
}
