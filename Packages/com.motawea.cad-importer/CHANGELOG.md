# Changelog

All notable changes to this package are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **BIM identity and property sets on imported IFC elements.** Every imported IFC element
  GameObject now carries an `IfcElement` component with its IFC entity type (e.g.
  `IfcWallStandardCase`), its stable **GlobalId** (the key for mapping scene objects back to
  the source model in issue tracking / BCF / dashboard workflows), and — when **Import
  Properties** is enabled (default) — the element's **property sets and quantities**,
  flattened as `Pset_WallCommon.FireRating` → value pairs and queryable via
  `IfcElement.GetProperty`. Type-level psets are merged into occurrences per BIM convention.
  Disable **IFC → Import Properties** to keep only type + GlobalId on very large models.
- **IFCZIP import (.ifczip)** — compressed IFC archives import exactly like plain `.ifc` files.
  (ifcXML was evaluated too, but the IfcOpenShell build FreeCAD currently bundles disables its
  ifcXML parser, so the extension is not claimed.)
- **IFC schema version surfaced.** The detected schema (IFC2X3 / IFC4 / IFC4X3…) is logged on
  import and recorded in the root `CADModelInfo.sourceFormat` (e.g. "IFC (IFC4)") — files of
  different vintages tessellate and colour differently, so the version is worth seeing.
- **IFC4.3 infrastructure palette.** Roads, rail, bridge and geotechnical element types
  (`IfcPavement`, `IfcKerb`, `IfcRail`, `IfcTrackElement`, `IfcSignal`, `IfcBearing`,
  `IfcPile`, `IfcEarthworksFill`, …) now get sensible default finishes (asphalt, steel,
  concrete, soil) instead of the generic grey.

- **Georeferenced IFC models import at the origin.** Models placed at real map coordinates
  (site offsets beyond 1 km) used to import kilometres from the Unity origin, where float32
  precision visibly jitters. The importer now detects the offset (site placement, or the first
  element for baked coordinates), moves the model to the origin, and records what it
  subtracted on `CADModelInfo.geoOffset` — so several files from the same project can still be
  co-aligned by offsetting each by the difference of their recorded offsets. The source
  georeference (projected CRS + map coordinates from `IfcMapConversion`, site
  latitude/longitude/elevation) is preserved on `CADModelInfo.geoReference`.

- **IFC Debug window** (`Tools → CAD Importer IFC Debug`) — visual + statistical BIM
  debugging of any imported model in the scene, driven by the `IfcElement` data. Recolour the
  model **by IFC type**, **by storey**, **by load-bearing** or **by external/internal** (the
  latter two read the imported property sets), with a colour-matched legend showing element
  count, LOD0 triangle count and share per category; clicking a legend row selects those
  elements in the Hierarchy. Colouring uses transient `MaterialPropertyBlock` overrides —
  the scene, prefab and imported assets are never modified, and *Original* mode (or closing
  the window) restores everything.
- **Import Spaces toggle.** `IfcSpace` volumes (rooms/zones) still import as translucent
  geometry by default, but can now be skipped entirely (**IFC → Import Spaces** off) — the
  default of most BIM viewers. Excluded spaces are never even tessellated.

### Changed
- **Lower memory and faster STL writing in the IFC converter.** Each element's STL is written
  the moment it is tessellated instead of holding the entire building's triangle lists in
  Python memory until placement (peak conversion memory on a 108 MB architecture model:
  3.2 GB → 2.4 GB, scaling with geometry size), and the per-triangle `struct.pack` loop was
  replaced with a vectorised numpy write. Output is byte-identical.

## [1.4.0] - 2026-07-09

### Added
- **IFC (BIM) import (.ifc)** — editor drag & drop / batch window. Tessellated through the
  IfcOpenShell library bundled with FreeCAD (the same install the STEP/IGES importer uses), so
  no extra dependency is needed. Built for realtime rendering of building models:
  - **Spatial hierarchy preserved**: project → site → building → storey → element (and
    aggregated sub-parts) each becomes a nested GameObject with its own local pivot, so a
    building imports as a navigable tree rather than a flat mesh soup.
  - **Per-element surface colours** are read from IFC styles and deduplicated into a small set
    of shared materials (fewer materials → effective static batching); translucent styles
    (e.g. glazing) import as blended materials.
  - **Professional "colour by material / category" palette** for elements with no authored
    style — the muted, desaturated scheme modern BIM viewers default to. Each element is
    coloured first by its associated **IFC material** (so **glass/glazing imports automatically
    translucent**, steel gets a metallic finish, timber becomes wood — on any element type, e.g.
    a glazed `IfcWall` or curtain-wall plate), then by **IFC type** (plaster walls, concrete
    slabs, steel structure, wood doors, charcoal roofs, translucent windows, teal/tan MEP).
    "Glass wool"/"glass fibre" insulation is correctly kept opaque. Authored file colours still
    win when present.
  - Geometry is normalised to metres and Z-up right-handed, then flows through the same
    pipeline as every other format (welding, LODs, simplified colliders, static batching).
  - Tessellation detail is configurable (**IFC Linear Deflection**); the shared conversion
    timeout and Console progress logging apply to IFC too.

### Changed
- **Multi-core import processing.** The CPU-heavy stages of every import now run in parallel
  across all cores instead of one part at a time, which substantially speeds up large
  multi-part models (STEP/IFC assemblies, multi-solid STLs, glTF scenes):
  - Geometry processing (unit scaling, axis conversion, smooth-normal generation, welding)
    runs one worker task per part.
  - LOD decimation and collision-mesh decimation for all parts are precomputed on worker
    threads before the (main-thread-only) Unity mesh/prefab construction.
  - The per-part STL files FreeCAD emits for STEP/IGES/IFC are parsed in parallel.
  - The runtime loader's `ImportAsync` now also welds/decimates collision meshes on the
    worker thread (previously this ran on the main thread and could hitch the simulation).
  Results are identical to the previous sequential pipeline — parts are processed
  independently and each part's math is unchanged (covered by new equivalence tests).
- **Determinate import progress bar for every format.** Imports now show a real progress bar
  with a calculated part count instead of only an elapsed-time spinner, so long jobs don't look
  stuck:
  - STEP/IGES/IFC conversion reports "part *i* of *N*" (the converter pre-counts the assembly's
    leaf parts, and IFC uses IfcOpenShell's own tessellation percentage), and the bar keeps a
    ticking elapsed clock during the opaque B-rep load so it never looks frozen.
  - The shared build stage (geometry processing, then meshing / LOD / collider generation per
    part) reports determinate progress for **all** formats — STL, PLY, glTF/GLB, STEP/IGES, IFC.

## [1.3.1] - 2026-07-09

### Changed
- **STEP/IGES assembly hierarchy and pivots are now preserved.** The FreeCAD converter emits
  each part in its own local frame plus a manifest of the assembly tree with per-node
  placements; the importer rebuilds a nested GameObject hierarchy with each part/sub-assembly
  at its correct pivot, converting FreeCAD's Z-up right-handed placements to Unity. Previously
  every part's placement was baked into its vertices and flattened to a single level at the
  origin — sub-assembly structure (e.g. robot joints) is now retained. Verified against the
  SO101 assembly (225 parts, depth 9) with world bounds matching the CAD source.

## [1.3.0] - 2026-07-09

### Changed
- **glTF node hierarchy and pivots are now preserved instead of baked.** Each glTF node
  becomes a GameObject carrying its own local transform (position/rotation/scale), so
  articulation rigs — robot joints, kinematic trees — import with their pivots intact rather
  than flattened to the origin. Node `matrix` forms are decomposed to TRS; the glTF→Unity
  axis conversion is applied consistently to both transforms and geometry.

### Added
- `CADNode.LocalPosition` / `LocalRotation` / `LocalScale` / `HasLocalTransform` — scene
  formats populate them; formats that bake placement into vertices (STL, STEP) leave them
  identity, so their import is unchanged.

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
