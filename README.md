# Unity 2019.4 Codex Bridge

A lightweight, file-based bridge for Unity 2019.4 Koikatsu Sunshine modding projects. It requires no MCP server, network listener, or window automation.

## Install

Copy `Assets/Editor/CodexBridge.cs` into the target Unity project's `Assets/Editor/` directory. Open the project and wait for Unity to compile the script.

Use the [KKS modding template](https://github.com/IllusionMods/KoikatsuSunshineModdingTools) with Unity 2019.4.9f1 for the established KKS workflow. The build implementation uses the template's `PreviewShaders` and `IllusionMods.KoikatuModdingTools.SB3UScript` classes. This file is not a dependency-free import into an arbitrary empty Unity project; `skipPostprocess` skips an execution step, not those compile-time dependencies.

Do not overwrite a customized bridge blindly. The maintainer's local Editor also contains TomX/BSDF-specific dispatchers that are intentionally not distributed here. Back up an existing script and merge reviewed changes, or use a separate clean authoring project. Never keep two files declaring the same global `CodexBridge` class.

The bridge creates these project-root directories automatically:

```text
CodexBridge/Inbox
CodexBridge/Outbox
CodexBridge/Logs
```

Place one JSON command in `CodexBridge/Inbox`. Unity polls that folder while the editor is open, moves each processed command to `*.json.done`, and writes the response to `CodexBridge/Outbox/<name>.result.json`.

Use `Tools > Codex Bridge > Process Inbox Now` only when an immediate manual run is needed.

## Commands

`ping`, `refresh`, `createFolder`, `moveAsset`, `createMaterial`, `createPrefab`, `createReflectionProbePrefab`, `createSHProbeVolumePrefab`, `inspectProbePrefab`, `validateProbePrefab`, `configureFaceWeightAccessory`, `inspectFaceWeightPrefab`, `setAssetBundle`, `setAssetBundles`, and `buildAssetBundles`.

The four Probe commands require the optional module described below; face-weight configuration is optional too. The core material/prefab/bundle operations do not require those plugins. `createPrefab` makes a native prefab, not a complete registered Studio catalog item; Studio item components and list/manifest registration are separate authoring steps.

See `examples/` for small command payloads. Asset paths must begin with `Assets/`. AssetBundle names are relative to `Build/abdata`; do not start them with `abdata/`.

## Notes

`buildAssetBundles` defaults to `Build/abdata`, `StandaloneWindows`, and chunk compression. Project-specific postprocessing is used unless `skipPostprocess` is set to `true`.

The operation builds the project's assigned bundles, not only the asset named in the previous command. Confirm scope before invoking it in a large shader project. It does not create a final zipmod or prove complete shader-program compilation. Check the result, build logs and packaged payload separately. Preserve successful caches; do not use Force Rebuild as a connection test.

## Optional Authoring Modules

The standard download leaves both defines disabled. Add a define only after importing its real authoring dependencies into the target project; do not substitute game-only DLLs blindly or fabricate missing component classes.

| Define | Dependencies | Commands |
| --- | --- | --- |
| `CODEX_BRIDGE_TOM_PROBE` | Matching `TomProbeRuntime` authoring assembly/source and the template's `Studio.ItemComponent` | Reflection/SH probe creation, inspection and validation |
| `CODEX_BRIDGE_FACE_WEIGHT` | `FaceWeightBinder` authoring types, `ChaAccessoryComponent` and `KK_Plugins.ChaAccessoryClothes` | `configureFaceWeightAccessory` |

Disabled module requests return an explicit error. Enabling a module retains its previous implementation; no probe resource or face-weight processing algorithm was changed. `inspectFaceWeightPrefab` only inspects transforms/renderers and remains available without the face-weight module.

## Local Source Review 2026-10-27

The public core now includes the local Editor's three busy guards: skip periodic processing during compilation/import, reject reentry while busy, and stop between queued commands if compilation/import begins. Commands remain queued until the Editor is ready. This is not an interrupt/cancel mechanism for an operation already running.

The local project-only BSDF/TomX dispatch cases, diagnostic payload fields and named-job `.running` lifecycle remain local. They are not missing general bridge features and must not be copied into a fresh project without their implementations. The public file therefore is not byte-identical to that customized Editor file.

For an installation smoke check, submit only `examples/ping.json` using a fresh filename and inspect `success` plus `message` in the corresponding Outbox result. No new full AssetBundle build or live project asset mutation is required to update this bridge source.

Offline C# compilation passed against local Unity 2019 API assemblies for the default core, Probe-enabled and both-modules-enabled configurations. The last case used the installed authoring assemblies; external modding-template build-hook signatures were compile fixtures, not executed postprocessing. This is not a fresh Editor-import, prefab-generation, optional-plugin behavior or AssetBundle acceptance test. The existing customized local bridge was not overwritten.
