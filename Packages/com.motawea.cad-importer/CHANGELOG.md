# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
