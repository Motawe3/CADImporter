# CAD Importer for Unity

[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](Packages/com.motawea.cad-importer/LICENSE.md)

Import CAD models — **STL, PLY, OBJ, STEP, IGES** — into Unity for **robotics simulation
and digital twins**. Every import is automatically converted to meters and Y-up, welded,
given smooth normals with crisp hard edges, a per-part **LOD chain**, and **simplified
physics colliders**. A runtime async API loads CAD files in player builds without
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
STEP/IGES import additionally needs a local [FreeCAD](https://www.freecad.org) install
(free) — auto-detected, configurable under `Tools → CAD Importer`.

## Highlights

- **Drag & drop** `.stl` / `.ply` / `.step` / `.iges` files — they import like any model,
  producing a prefab with part hierarchy, LODGroups, colliders, materials and metadata.
- **Batch window** (`Tools → CAD Importer`) for importing many external files at once.
- **Assembly-aware STEP import** — each solid becomes a named child GameObject, so robot
  links map cleanly to `ArticulationBody` / simulation logic.
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
the on-screen panel imports any `.stl`/`.ply`/`.obj` file at runtime, or a generated
sample part when the path is empty. Alternatively, add the `DemoCadRuntimeImporter`
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
