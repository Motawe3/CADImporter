# CAD Importer Demo

A demo scene for the runtime CAD import workflow.

**Requires the Universal Render Pipeline** (the scene's camera, lights and volume use
URP components, and the showcase materials use a URP Shader Graph).

## Contents

- `Scenes/DemoRuntimeImporter.unity` — open it and press Play. The on-screen panel
  (driven by the `DemoCadRuntimeImporter` component) imports any `.stl` / `.ply` / `.obj`
  file you point it at; with an empty path it imports a generated sample part.
- `Materials/` — lit color materials and reflective showcase materials.
- `Shaders/Relfective.shadergraph` — the reflective Shader Graph used by the showcase materials.
- `Textures/photo_studio_01_4k.hdr` — HDR environment used for reflections (1024×512).
- `Settings/DemoVolumeProfile.asset` — post-processing volume profile for the scene.

## Tips

- Select the `DemoCadRuntimeImporter` object in the scene to tweak import settings
  (units, orientation, colliders, turntable, camera framing) in the Inspector.
- The same API the panel uses is one call in your own code:
  `await CADRuntimeImporter.ImportAsync(path, settings)`.
