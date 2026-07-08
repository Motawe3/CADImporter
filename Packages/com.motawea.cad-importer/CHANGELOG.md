# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.1] - 2026-07-09

### Fixed
- Batch-importing a multi-file text `.gltf` (a `.gltf` plus an external `.bin` buffer and/or
  external texture images) failed with "glTF external buffer not found". The importer now
  copies the `.gltf` and its external dependencies into a dedicated project folder,
  preserving relative paths so buffers and images resolve. GLB and single-file glTF are
  unaffected.

## [1.2.0] - 2026-07-09

### Added
- **glTF 2.0 (.gltf) and binary GLB (.glb) import** — editor (drag & drop / batch window)
  and runtime (`CADRuntimeImporter`). Pure-C# parser, no external dependencies:
  - Binary GLB container plus text .gltf with embedded, external, and base64 data-URI
    buffers and images.
  - Full accessor decoding: every component type, normalized integers, interleaved
    `byteStride`, and sparse accessors; indexed and non-indexed primitives; triangle
    lists, strips and fans.
  - Node-graph transforms (TRS or matrix) baked into geometry, with correct normals under
    non-uniform scale and winding flips for mirrored (negative-scale) nodes.
  - Metallic-roughness PBR materials: base colour, metallic-roughness, normal, occlusion
    and emissive maps (glTF→Unity channel repacking), alpha mode (opaque/mask/blend),
    double-sided, and `KHR_materials_unlit` / `KHR_materials_emissive_strength`.
  - Geometry flows through the same pipeline as every other format (welding, LODs,
    simplified colliders). glTF's metre / Y-up right-handed convention is applied by default.
  - Files requiring Draco, meshopt or KTX2/basisu compression fail with a clear message
    (re-export uncompressed with PNG/JPEG textures).
- Material system now carries full PBR (textures + factors), shared by all importers.

## [1.1.0] - 2026-07-08

### Added
- STEP/IGES conversion timeout is now configurable (**Step Timeout Seconds** import
  setting, default 900, 0 = no limit). Large assemblies (100+ MB, hundreds of parts)
  that legitimately need more than the old fixed 5-minute limit now import successfully.
- Conversion progress is logged to the Console during STEP/IGES import, so a slow job on
  a big assembly is visibly making progress instead of looking hung.

### Fixed
- STEP assembly import: missing parts, wrong placements, and duplicate names caused by
  reading `.Shape` off the flat document object list instead of walking the assembly tree.

## [1.0.0] - 2026-07-06

### Added
- STL importer (binary and ASCII, multi-solid files become separate parts).
- PLY importer (ascii, binary little/big endian; vertex colors and UVs preserved).
- STEP / IGES importer via headless FreeCAD (Open CASCADE tessellation); assembly
  structure and part labels preserved.
- Runtime OBJ parser (groups, MTL diffuse colors, negative indices).
- Geometry pipeline: unit scaling (mm/cm/m/inch/foot to meters), Z-up right-handed to
  Unity Y-up left-handed conversion with winding fix, vertex welding, angle-weighted
  smooth normals with hard-edge splitting.
- Quadric-error-metric decimation driving per-part LOD chains and simplified physics
  collision meshes (position-welded topology, so hard edges never tear open).
- Collider modes: Box, Convex Mesh, Simplified Mesh, Full Mesh, None.
- Batch import window (Tools → CAD Importer) with persisted settings and drag & drop.
- Async runtime import API (`CADRuntimeImporter.ImportAsync`) for digital twins —
  parsing and processing run off the main thread.
- `CADModelInfo` metadata component on every imported root.
- `DemoCadRuntimeImporter` component: on-screen runtime import panel with source-unit
  and scale controls, camera framing, turntable, and a generated sample part when no
  file path is given.
- "CAD Importer Demo" package sample (Package Manager → CAD Importer → Samples):
  demo scene with the runtime import panel, reflective showcase materials, HDR
  environment and volume profile. Requires the Universal Render Pipeline.
- EditMode test suite.
