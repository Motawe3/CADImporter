# CAD Importer for Unity

[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](Packages/com.motawea.cad-importer/LICENSE.md)

Import CAD & BIM models — **STL, PLY, OBJ, glTF/GLB, STEP, IGES, IFC** — into Unity for
**robotics simulation and digital twins**. Every import is automatically converted to meters
and Y-up, has its **assembly hierarchy and pivots preserved**, is welded, given smooth normals
with crisp hard edges, a per-part **LOD chain**, **metallic-roughness PBR materials**, and
**simplified physics colliders**. A runtime async API loads CAD files in player builds without
hitching the simulation. Pure C#, no native plugins.

## Install

`Window → Package Manager` → `+` → *Add package from git URL…*

```
https://github.com/Motawe3/CADImporter.git?path=Packages/com.motawea.cad-importer
```

Pin a release with a tag: append `#v1.0.0`. Or add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.motawea.cad-importer": "https://github.com/Motawe3/CADImporter.git?path=Packages/com.motawea.cad-importer#v1.0.0"
  }
}
```

Requires Unity **6000.0+**. URP recommended (falls back to Built-in/Standard).
STEP/IGES/IFC import additionally needs a local [FreeCAD](https://www.freecad.org) install
(free; bundles the IfcOpenShell BIM kernel) — auto-detected, configurable under
`Tools → CAD Importer`.

## Highlights

- **Drag & drop** `.stl` / `.ply` / `.gltf` / `.glb` / `.step` / `.iges` / `.ifc` files —
  they import like any model, producing a prefab with part hierarchy, LODGroups, colliders,
  materials and metadata.
- **Batch window** (`Tools → CAD Importer`) for importing many external files at once, with a
  determinate progress bar (part *i* of *N*) on long conversions.
- **Hierarchy & pivots preserved** — STEP/IGES assemblies, glTF node rigs, and IFC spatial
  structure import as nested GameObjects at their correct pivots (robot joints, building
  storeys), so links map cleanly to `ArticulationBody` / simulation logic.
- **glTF 2.0 / GLB** with full metallic-roughness PBR — base colour, metallic-roughness,
  normal, occlusion and emissive maps, alpha modes and double-sided materials.
- **IFC (BIM)** — spatial hierarchy plus a professional colour-by-material/category palette
  (glass auto-translucent, steel metallic, concrete, timber, charcoal roofs, MEP…).
- **Runtime import for digital twins**:

```csharp
GameObject model = await CADRuntimeImporter.ImportAsync(
    @"C:\twins\gripper.stl",
    new CADRuntimeImportSettings { convexColliders = true });
```

- **Performance-first defaults**: mm→m scaling, vertex welding (~6× fewer vertices from
  STL), quadric-error LODs (100/50/15%), decimated collision meshes, 16-bit index buffers,
  non-readable mesh data.

## Documentation

- [Package README](Packages/com.motawea.cad-importer/README.md) — features, settings
  reference, API, limitations
- [Full manual (PDF)](Packages/com.motawea.cad-importer/Documentation~/CAD%20Importer%20-%20Documentation.pdf)
- [Quick start (PDF)](Packages/com.motawea.cad-importer/Documentation~/CAD%20Importer%20-%20Quick%20Start.pdf)
- [Changelog](Packages/com.motawea.cad-importer/CHANGELOG.md)

## Demo sample

After installing, open `Window → Package Manager → CAD Importer → Samples` and import
**CAD Importer Demo** (requires URP). Open `DemoRuntimeImporter.unity` and press Play —
the on-screen panel imports any `.stl`/`.ply`/`.obj`/`.gltf`/`.glb` file at runtime, or a
generated sample part when the path is empty. Alternatively, add the `DemoCadRuntimeImporter`
component to any GameObject.

## Repository layout

This repository is a Unity development project with the package embedded:

```
Packages/com.motawea.cad-importer/   ← the installable package (code, tests, docs)
  └── Samples~/CADImporterDemo/      ← demo scene, materials, HDR environment
Assets/                              ← Unity development project around the package
```

## Running the package tests

Add the package to `"testables"` in your project's `Packages/manifest.json`, then run
EditMode tests from `Window → General → Test Runner`:

```json
"testables": ["com.motawea.cad-importer"]
```

## License

[MIT](Packages/com.motawea.cad-importer/LICENSE.md) © 2026 Mohammed Motawea.
The decimator follows Sven Forstmann's MIT-licensed *Fast Quadric Mesh Simplification* scheme.

## Support

Bug reports and feature requests: [GitHub Issues](https://github.com/Motawe3/CADImporter/issues)
· mohammed.motawea90@gmail.com
