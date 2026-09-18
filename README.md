# Unity 2019.4 Codex Bridge

A lightweight, file-based bridge for Unity 2019.4 projects. It requires no MCP server, network listener, or window automation.

## Install

Copy `Assets/Editor/CodexBridge.cs` into the target Unity project's `Assets/Editor/` directory. Open the project and wait for Unity to compile the script.

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

See `examples/` for small command payloads. Asset paths must begin with `Assets/`. AssetBundle names are relative to `Build/abdata`; do not start them with `abdata/`.

## Notes

`buildAssetBundles` defaults to `Build/abdata`, `StandaloneWindows`, and chunk compression. Project-specific postprocessing is used unless `skipPostprocess` is set to `true`.
