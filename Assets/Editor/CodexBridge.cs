using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

#pragma warning disable 0649

[InitializeOnLoad]
public static class CodexBridge
{
    private const string BridgeRootName = "CodexBridge";
    private const double ProcessIntervalSeconds = 0.5d;
    private const string KoikatsuPathDefault = "C:/Illusion/Koikatu";
    private static readonly object LogLock = new object();
    private static readonly string ProjectRoot = Directory.GetParent(Application.dataPath).FullName;
    private static readonly string Root = Path.Combine(ProjectRoot, BridgeRootName);
    private static readonly string Inbox = Path.Combine(Root, "Inbox");
    private static readonly string Outbox = Path.Combine(Root, "Outbox");
    private static readonly string Logs = Path.Combine(Root, "Logs");
    private static readonly string UnityLog = Path.Combine(Logs, "unity-log.jsonl");
    private static string[] _resultFiles = new string[0];
    private static ProbeInspection _probeInspection;
    private static double _nextProcessTime;
    private static bool _isProcessing;

    static CodexBridge()
    {
        EnsureBridgeFolders();

        Application.logMessageReceived -= OnLogMessageReceived;
        Application.logMessageReceived += OnLogMessageReceived;

        EditorApplication.update -= ProcessCommandsOnTick;
        EditorApplication.update += ProcessCommandsOnTick;

        Debug.Log("[CodexBridge] Ready. Inbox: " + Inbox);
    }

    [MenuItem("Tools/Codex Bridge/Process Inbox Now")]
    public static void ProcessCommandsMenuItem()
    {
        ProcessCommands();
    }

    [MenuItem("Tools/Codex Bridge/Open Bridge Folder")]
    public static void OpenBridgeFolder()
    {
        EnsureBridgeFolders();
        EditorUtility.RevealInFinder(Root);
    }

    [MenuItem("Tools/Codex Bridge/Write Test Log")]
    public static void WriteTestLog()
    {
        Debug.Log("[CodexBridge] Test log entry.");
    }

    private static void ProcessCommandsOnTick()
    {
        if (EditorApplication.timeSinceStartup < _nextProcessTime)
            return;

        _nextProcessTime = EditorApplication.timeSinceStartup + ProcessIntervalSeconds;
        ProcessCommands();
    }

    private static void ProcessCommands()
    {
        if (_isProcessing)
            return;

        _isProcessing = true;

        try
        {
            EnsureBridgeFolders();

            string[] files = Directory.GetFiles(Inbox, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
                ProcessCommandFile(file);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private static void ProcessCommandFile(string file)
    {
        if (!IsFileReady(file))
            return;

        Command command = null;
        string message = "OK";

        try
        {
            _resultFiles = new string[0];
            _probeInspection = null;
            string json = File.ReadAllText(file);
            command = JsonUtility.FromJson<Command>(json);

            if (command == null || string.IsNullOrEmpty(command.operation))
                throw new Exception("Invalid command: operation is empty.");

            switch (command.operation.Trim().ToLowerInvariant())
            {
                case "ping":
                    message = "pong";
                    break;

                case "refresh":
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    message = "AssetDatabase refreshed.";
                    break;

                case "createfolder":
                    CreateFolder(command);
                    message = "Folder created or already exists: " + command.assetPath;
                    break;

                case "moveasset":
                    MoveAsset(command);
                    message = "Asset moved: " + command.assetPath + " -> " + command.destinationAssetPath;
                    break;

                case "creatematerial":
                    CreateMaterial(command);
                    message = "Material created or updated: " + command.assetPath;
                    break;

                case "createprefab":
                    CreatePrefab(command);
                    message = "Prefab created or updated: " + command.assetPath;
                    break;

                case "createreflectionprobeprefab":
                    CreateReflectionProbePrefab(command);
                    message = "Reflection probe prefab created or updated: " + command.assetPath;
                    break;

                case "createshprobevolumeprefab":
                    CreateSHProbeVolumePrefab(command);
                    message = "SH probe volume prefab created or updated: " + command.assetPath;
                    break;

                case "inspectprobeprefab":
                    message = InspectProbePrefab(command, false);
                    break;

                case "validateprobeprefab":
                    message = InspectProbePrefab(command, true);
                    break;

                case "configurefaceweightaccessory":
                    message = ConfigureFaceWeightAccessory(command);
                    break;

                case "inspectfaceweightprefab":
                    message = InspectFaceWeightPrefab(command);
                    break;

                case "setassetbundle":
                case "setassetbundles":
                    int assignedCount = SetAssetBundles(command);
                    message = "AssetBundle assignment updated for " + assignedCount + " asset(s).";
                    break;

                case "buildassetbundles":
                    BuildAssetBundleResult buildResult = BuildAssetBundles(command);
                    _resultFiles = buildResult.changedFiles;
                    message = "Built/updated " + buildResult.changedFiles.Length + " asset bundle file(s) in " + buildResult.outputDirectory + ".";
                    break;

                default:
                    throw new Exception("Unknown operation: " + command.operation);
            }

            WriteResult(file, command, true, message);
        }
        catch (Exception ex)
        {
            Debug.LogError("[CodexBridge] Command failed: " + Path.GetFileName(file) + "\n" + ex);
            WriteResult(file, command, false, ex.ToString());
        }
    }

    private static void CreateFolder(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath").TrimEnd('/');

        if (path == "Assets")
            return;

        EnsureAssetFolder(path);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
    }

    private static void MoveAsset(Command command)
    {
        string sourcePath = NormalizeAssetPath(command.assetPath, "assetPath").TrimEnd('/');
        string destinationPath = NormalizeAssetPath(command.destinationAssetPath, "destinationAssetPath").TrimEnd('/');

        if (sourcePath == "Assets" || destinationPath == "Assets")
            throw new Exception("moveAsset cannot move the Assets root folder.");

        bool sourceExists = AssetDatabase.IsValidFolder(sourcePath) || AssetDatabase.LoadMainAssetAtPath(sourcePath) != null;

        if (!sourceExists)
            throw new Exception("Source asset does not exist: " + sourcePath);

        bool destinationExists = AssetDatabase.IsValidFolder(destinationPath) || AssetDatabase.LoadMainAssetAtPath(destinationPath) != null;

        if (destinationExists)
            throw new Exception("Destination asset already exists: " + destinationPath);

        EnsureParentFolder(destinationPath);

        string error = AssetDatabase.MoveAsset(sourcePath, destinationPath);

        if (!string.IsNullOrEmpty(error))
            throw new Exception("AssetDatabase.MoveAsset failed: " + error);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
    }

    private static void CreateMaterial(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");

        if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Material assetPath must end with .mat: " + path);

        EnsureParentFolder(path);

        string shaderName = string.IsNullOrEmpty(command.shader) ? "Standard" : command.shader;
        Shader shader = Shader.Find(shaderName);

        if (shader == null)
            throw new Exception("Shader not found: " + shaderName);

        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        UnityEngine.Object existingAsset = AssetDatabase.LoadMainAssetAtPath(path);

        if (material == null && existingAsset != null)
            throw new Exception("Asset exists but is not a Material: " + path);

        if (material == null)
        {
            material = new Material(shader);
            material.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }

        if (!string.IsNullOrEmpty(command.color))
            SetMaterialColor(material, string.IsNullOrEmpty(command.colorProperty) ? "_Color" : command.colorProperty, command.color);

        if (!string.IsNullOrEmpty(command.mainTexture))
            SetMaterialTexture(material, string.IsNullOrEmpty(command.textureProperty) ? "_MainTex" : command.textureProperty, command.mainTexture);

        if (!string.IsNullOrEmpty(command.floatProperty))
            SetMaterialFloat(material, command.floatProperty, command.floatValue.ToString(CultureInfo.InvariantCulture));

        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        Debug.Log("[CodexBridge] Material created or updated: " + path);
    }

    private static void CreatePrefab(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");

        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Prefab assetPath must end with .prefab: " + path);

        EnsureParentFolder(path);

        string objectName = string.IsNullOrEmpty(command.name) ? Path.GetFileNameWithoutExtension(path) : command.name;
        GameObject go = CreatePrefabSourceObject(command, objectName);

        try
        {
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, path);

            if (prefab == null)
                throw new Exception("PrefabUtility.SaveAsPrefabAsset returned null for " + path);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log("[CodexBridge] Prefab created or updated: " + path);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static GameObject CreatePrefabSourceObject(Command command, string objectName)
    {
        bool wantsPrimitive = !string.IsNullOrEmpty(command.primitive) || !string.IsNullOrEmpty(command.materialPath);
        GameObject go;

        if (wantsPrimitive)
        {
            PrimitiveType primitiveType = ParsePrimitiveType(command.primitive);
            go = GameObject.CreatePrimitive(primitiveType);
            Collider collider = go.GetComponent<Collider>();

            if (collider != null)
                UnityEngine.Object.DestroyImmediate(collider);
        }
        else
        {
            go = new GameObject();
        }

        go.name = objectName;

        if (!string.IsNullOrEmpty(command.materialPath))
        {
            string materialPath = NormalizeAssetPath(command.materialPath, "materialPath");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);

            if (material == null)
                throw new Exception("Material not found: " + materialPath);

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();

            if (renderer == null)
                renderer = go.AddComponent<MeshRenderer>();

            renderer.sharedMaterial = material;
        }

        return go;
    }

    private static void CreateReflectionProbePrefab(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");

        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Reflection probe assetPath must end with .prefab: " + path);

        EnsureParentFolder(path);
        string objectName = string.IsNullOrEmpty(command.name)
            ? Path.GetFileNameWithoutExtension(path)
            : command.name;
        GameObject root = new GameObject(objectName);

        try
        {
            AddStandardItemComponent(root);
            ReflectionProbe probe = root.AddComponent<ReflectionProbe>();
            TomProbeRuntime.TomReflectionProbeController controller =
                root.AddComponent<TomProbeRuntime.TomReflectionProbeController>();

            controller.probe = probe;
            controller.updateMode = ParseEnum(
                command.updateMode,
                TomProbeRuntime.TomProbeUpdateMode.OnTransformChanged);
            controller.renderOnEnable = command.renderOnEnable;
            controller.updateInterval = command.updateInterval > 0f ? command.updateInterval : 5f;
            controller.resolution = command.resolution > 0 ? command.resolution : 512;
            controller.hdr = command.hdr;
            controller.boxProjection = command.boxProjection;
            controller.intensity = command.intensity > 0f ? command.intensity : 1f;
            controller.blendDistance = Mathf.Max(command.blendDistance, 0f);
            controller.nearClip = command.nearClip > 0f ? command.nearClip : 0.1f;
            controller.farClip = command.farClip > controller.nearClip
                ? command.farClip
                : 100f;
            controller.localCenter = ParseVector3(command.center, Vector3.zero);
            controller.localSize = ParseVector3(command.size, new Vector3(10f, 6f, 10f));
            controller.cullingMask = command.cullingMask;
            controller.clearFlags = ParseEnum(
                command.reflectionClearFlags,
                UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox);
            controller.backgroundColor = ParseColor(command.backgroundColor, Color.black);
            controller.timeSlicingMode = ParseEnum(
                command.timeSlicingMode,
                UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces);
            controller.ApplySettings();

            SerializedObject serializedProbe = new SerializedObject(probe);
            SetSerializedBool(serializedProbe, "m_RenderDynamicObjects", command.renderDynamicObjects);
            SetSerializedBool(serializedProbe, "m_UseOcclusionCulling", command.useOcclusionCulling);
            SetSerializedInt(serializedProbe, "m_Importance", command.importance > 0 ? command.importance : 1);
            SetSerializedFloat(
                serializedProbe,
                "m_ShadowDistance",
                command.shadowDistance > 0f ? command.shadowDistance : controller.farClip);
            serializedProbe.ApplyModifiedPropertiesWithoutUndo();

            SavePrefabAsset(root, path);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void CreateSHProbeVolumePrefab(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");

        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new Exception("SH probe volume assetPath must end with .prefab: " + path);

        EnsureParentFolder(path);
        string objectName = string.IsNullOrEmpty(command.name)
            ? Path.GetFileNameWithoutExtension(path)
            : command.name;
        GameObject root = new GameObject(objectName);

        try
        {
            AddStandardItemComponent(root);
            TomProbeRuntime.TomSHProbeVolume volume =
                root.AddComponent<TomProbeRuntime.TomSHProbeVolume>();
            volume.localCenter = ParseVector3(command.center, Vector3.zero);
            volume.localSize = ParseVector3(command.size, new Vector3(8f, 4f, 8f));
            volume.gridResolution = new Vector3Int(
                command.gridX > 0 ? command.gridX : 4,
                command.gridY > 0 ? command.gridY : 3,
                command.gridZ > 0 ? command.gridZ : 4);
            volume.edgeFade = command.edgeFade >= 0f ? command.edgeFade : 0.15f;
            volume.intensity = command.intensity > 0f ? command.intensity : 1f;
            volume.priority = command.priority;
            volume.captureResolution = command.captureResolution > 0
                ? command.captureResolution
                : 16;
            volume.capturesPerFrame = command.capturesPerFrame > 0
                ? command.capturesPerFrame
                : 1;
            volume.cullingMask = command.cullingMask;
            volume.nearClip = command.nearClip > 0f ? command.nearClip : 0.1f;
            volume.farClip = command.farClip > volume.nearClip ? command.farClip : 100f;
            volume.clearFlags = ParseEnum(command.cameraClearFlags, CameraClearFlags.Skybox);
            volume.backgroundColor = ParseColor(command.backgroundColor, Color.black);
            volume.useOcclusionCulling = command.useOcclusionCulling;
            volume.updateMode = ParseEnum(
                command.updateMode,
                TomProbeRuntime.TomProbeUpdateMode.OnTransformChanged);
            volume.captureOnEnable = command.captureOnEnable;
            volume.updateInterval = command.updateInterval > 0f ? command.updateInterval : 30f;

            EditorUtility.SetDirty(volume);
            SavePrefabAsset(root, path);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static string InspectProbePrefab(Command command, bool validate)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");
        GameObject root = PrefabUtility.LoadPrefabContents(path);

        if (root == null)
            throw new Exception("Could not load probe prefab contents: " + path);

        try
        {
            Studio.ItemComponent item = root.GetComponent<Studio.ItemComponent>();
            ReflectionProbe reflectionProbe = root.GetComponentInChildren<ReflectionProbe>(true);
            TomProbeRuntime.TomReflectionProbeController reflectionController =
                root.GetComponentInChildren<TomProbeRuntime.TomReflectionProbeController>(true);
            TomProbeRuntime.TomSHProbeVolume shVolume =
                root.GetComponentInChildren<TomProbeRuntime.TomSHProbeVolume>(true);

            if (validate && item == null)
                throw new Exception("Probe prefab is missing Studio.ItemComponent: " + path);
            if (validate && reflectionProbe == null && shVolume == null)
                throw new Exception("Probe prefab has neither ReflectionProbe nor TomSHProbeVolume: " + path);
            if (validate && reflectionProbe != null && reflectionController == null)
                throw new Exception("Reflection probe prefab is missing TomReflectionProbeController: " + path);

            ProbeInspection inspection = new ProbeInspection
            {
                kind = reflectionProbe != null ? "ReflectionProbe" : shVolume != null ? "SHProbeVolume" : "Unknown",
                hasItemComponent = item != null,
                hasReflectionProbe = reflectionProbe != null,
                hasReflectionController = reflectionController != null,
                hasSHProbeVolume = shVolume != null
            };

            if (reflectionProbe != null)
            {
                inspection.resolution = reflectionProbe.resolution;
                inspection.hdr = reflectionProbe.hdr;
                inspection.boxProjection = reflectionProbe.boxProjection;
                inspection.intensity = reflectionProbe.intensity;
                inspection.blendDistance = reflectionProbe.blendDistance;
                inspection.nearClip = reflectionProbe.nearClipPlane;
                inspection.farClip = reflectionProbe.farClipPlane;
                inspection.center = ToArray(reflectionProbe.center);
                inspection.size = ToArray(reflectionProbe.size);

                SerializedObject serializedProbe = new SerializedObject(reflectionProbe);
                inspection.renderDynamicObjects = GetSerializedBool(
                    serializedProbe,
                    "m_RenderDynamicObjects");
                inspection.useOcclusionCulling = GetSerializedBool(
                    serializedProbe,
                    "m_UseOcclusionCulling");
                inspection.importance = GetSerializedInt(serializedProbe, "m_Importance");
            }

            if (shVolume != null)
            {
                inspection.gridX = shVolume.gridResolution.x;
                inspection.gridY = shVolume.gridResolution.y;
                inspection.gridZ = shVolume.gridResolution.z;
                inspection.captureResolution = shVolume.captureResolution;
                inspection.capturesPerFrame = shVolume.capturesPerFrame;
                inspection.edgeFade = shVolume.edgeFade;
                inspection.intensity = shVolume.intensity;
                inspection.priority = shVolume.priority;
                inspection.nearClip = shVolume.nearClip;
                inspection.farClip = shVolume.farClip;
                inspection.center = ToArray(shVolume.localCenter);
                inspection.size = ToArray(shVolume.localSize);
                inspection.useOcclusionCulling = shVolume.useOcclusionCulling;
            }

            _probeInspection = inspection;
            return (validate ? "Probe prefab validation passed: " : "Probe prefab inspected: ")
                + path + " (" + inspection.kind + ")";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void AddStandardItemComponent(GameObject root)
    {
        Studio.ItemComponent item = root.AddComponent<Studio.ItemComponent>();
        item.rendNormal = new Renderer[0];
        item.rendAlpha = new Renderer[0];
        item.rendGlass = new Renderer[0];
        item.info = new Studio.ItemComponent.Info[3];

        for (int i = 0; i < item.info.Length; i++)
        {
            item.info[i] = new Studio.ItemComponent.Info();
            item.info[i].useColor = false;
            item.info[i].usePattern = false;
        }

        EditorUtility.SetDirty(item);
    }

    private static void SavePrefabAsset(GameObject root, string path)
    {
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);

        if (prefab == null)
            throw new Exception("PrefabUtility.SaveAsPrefabAsset returned null for " + path);

        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        Debug.Log("[CodexBridge] Probe prefab created or updated: " + path);
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct
    {
        T parsed;
        return !string.IsNullOrEmpty(value) && Enum.TryParse(value, true, out parsed)
            ? parsed
            : fallback;
    }

    private static Vector3 ParseVector3(float[] values, Vector3 fallback)
    {
        return values != null && values.Length >= 3
            ? new Vector3(values[0], values[1], values[2])
            : fallback;
    }

    private static float[] ToArray(Vector3 value)
    {
        return new[] { value.x, value.y, value.z };
    }

    private static Color ParseColor(string value, Color fallback)
    {
        if (string.IsNullOrEmpty(value))
            return fallback;

        Color parsed;
        return ColorUtility.TryParseHtmlString(value, out parsed) ? parsed : fallback;
    }

    private static void SetSerializedBool(SerializedObject serializedObject, string name, bool value)
    {
        SerializedProperty property = serializedObject.FindProperty(name);
        if (property != null)
            property.boolValue = value;
    }

    private static void SetSerializedInt(SerializedObject serializedObject, string name, int value)
    {
        SerializedProperty property = serializedObject.FindProperty(name);
        if (property != null)
            property.intValue = value;
    }

    private static void SetSerializedFloat(SerializedObject serializedObject, string name, float value)
    {
        SerializedProperty property = serializedObject.FindProperty(name);
        if (property != null)
            property.floatValue = value;
    }

    private static bool GetSerializedBool(SerializedObject serializedObject, string name)
    {
        SerializedProperty property = serializedObject.FindProperty(name);
        return property != null && property.boolValue;
    }

    private static int GetSerializedInt(SerializedObject serializedObject, string name)
    {
        SerializedProperty property = serializedObject.FindProperty(name);
        return property == null ? 0 : property.intValue;
    }

    private static string ConfigureFaceWeightAccessory(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");

        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Face-weight accessory assetPath must end with .prefab: " + path);

        GameObject root = PrefabUtility.LoadPrefabContents(path);

        if (root == null)
            throw new Exception("Could not load prefab contents: " + path);

        try
        {
            FaceWeightBinder.FaceWeightProcess process =
                root.GetComponent<FaceWeightBinder.FaceWeightProcess>();

            if (process == null)
                throw new Exception("FaceWeightProcess is missing from prefab root: " + path);

            Transform moveRoot = root.transform.Find("N_move");

            if (moveRoot != null)
            {
                Transform[] weightedChildren = moveRoot.Cast<Transform>().ToArray();
                foreach (Transform child in weightedChildren)
                    child.SetParent(root.transform, false);
                UnityEngine.Object.DestroyImmediate(moveRoot.gameObject);
            }

            SkinnedMeshRenderer[] renderers = root
                .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(x => x.sharedMesh != null).ToArray();

            if (renderers.Length == 0)
                throw new Exception("No SkinnedMeshRenderer with a mesh was found in: " + path);

            ChaAccessoryComponent accessory = root.GetComponent<ChaAccessoryComponent>();

            if (accessory == null)
                accessory = root.AddComponent<ChaAccessoryComponent>();

            accessory.rendNormal = renderers.Cast<Renderer>().ToArray();
            accessory.rendAlpha = accessory.rendAlpha ?? new Renderer[0];
            accessory.rendHair = accessory.rendHair ?? new Renderer[0];
            accessory.enabled = true;

            Transform skeletonRoot = FindDescendantByName(root.transform, "cf_J_N_FaceRoot") ??
                                     FindDescendantByName(root.transform, "cm_J_N_FaceRoot");
            Transform preferredRootBone = FindDescendantByName(root.transform, "cf_J_FaceRoot") ??
                                          FindDescendantByName(root.transform, "cm_J_FaceRoot");

            if (skeletonRoot == null)
                throw new Exception("Face skeleton root cf_J_N_FaceRoot/cm_J_N_FaceRoot was not found.");
            if (preferredRootBone == null)
                throw new Exception("Face renderer root bone cf_J_FaceRoot/cm_J_FaceRoot was not found.");

            KK_Plugins.ChaAccessoryClothes accessoryClothes =
                root.GetComponent<KK_Plugins.ChaAccessoryClothes>();

            if (accessoryClothes != null)
                UnityEngine.Object.DestroyImmediate(accessoryClothes);

            FaceWeightBinder.FaceWeightRendererBinding[] bindings = renderers
                .Select(renderer =>
                {
                    renderer.rootBone = preferredRootBone;
                    Transform[] bones = renderer.bones ?? new Transform[0];

                    if (bones.Length == 0 || bones.Any(x => x == null))
                        throw new Exception("Renderer has missing or empty bones: " + renderer.name);

                    return new FaceWeightBinder.FaceWeightRendererBinding
                    {
                        renderer = renderer,
                        rendererPath = GetRelativeTransformPath(root.transform, renderer.transform),
                        sourceBones = bones,
                        boneNames = bones.Select(x => x.name).ToArray(),
                        rootBoneName = preferredRootBone.name
                    };
                }).ToArray();

            process.schemaVersion = 1;
            process.skeletonRoot = skeletonRoot;
            process.bindings = bindings;

            EditorUtility.SetDirty(accessory);
            EditorUtility.SetDirty(process);
            foreach (SkinnedMeshRenderer renderer in renderers)
                EditorUtility.SetDirty(renderer);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);

            if (saved == null)
                throw new Exception("PrefabUtility.SaveAsPrefabAsset returned null for " + path);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return "Configured FaceWeightBinder accessory without N_move or AccessoryClothes, " +
                   renderers.Length + " renderer(s), and refreshed bindings: " + path;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static string InspectFaceWeightPrefab(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");
        GameObject root = PrefabUtility.LoadPrefabContents(path);

        if (root == null)
            throw new Exception("Could not load prefab contents: " + path);

        try
        {
            var lines = new List<string>();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                lines.Add("T " + GetRelativeTransformPath(root.transform, transform) +
                          " localPosition=" + FormatVector3(transform.localPosition) +
                          " localEuler=" + FormatVector3(transform.localEulerAngles) +
                          " localScale=" + FormatVector3(transform.localScale) +
                          " worldPosition=" + FormatVector3(transform.position) +
                          " lossyScale=" + FormatVector3(transform.lossyScale));
            }

            foreach (SkinnedMeshRenderer renderer in
                     root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                lines.Add("R " + GetRelativeTransformPath(root.transform, renderer.transform) +
                          " rootBone=" + (renderer.rootBone == null
                              ? "(null)"
                              : GetRelativeTransformPath(root.transform, renderer.rootBone)) +
                          " bones=" + (renderer.bones == null ? 0 : renderer.bones.Length) +
                          " bindposes=" + (mesh == null || mesh.bindposes == null
                              ? 0
                              : mesh.bindposes.Length) +
                          " meshBoundsCenter=" + (mesh == null
                              ? "(null)"
                              : FormatVector3(mesh.bounds.center)) +
                          " meshBoundsSize=" + (mesh == null
                              ? "(null)"
                              : FormatVector3(mesh.bounds.size)) +
                          " localBoundsCenter=" + FormatVector3(renderer.localBounds.center) +
                          " localBoundsSize=" + FormatVector3(renderer.localBounds.size));
            }

            return string.Join("\n", lines.ToArray());
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static string FormatVector3(Vector3 value)
    {
        return "(" + value.x.ToString("R", CultureInfo.InvariantCulture) + "," +
               value.y.ToString("R", CultureInfo.InvariantCulture) + "," +
               value.z.ToString("R", CultureInfo.InvariantCulture) + ")";
    }

    private static Transform FindDescendantByName(Transform root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(x => x.name == name);
    }

    private static string GetRelativeTransformPath(Transform root, Transform target)
    {
        if (root == target)
            return string.Empty;

        Stack<string> parts = new Stack<string>();
        Transform current = target;

        while (current != null && current != root)
        {
            parts.Push(current.name);
            current = current.parent;
        }

        if (current != root)
            throw new Exception("Transform is outside prefab root: " + target.name);

        return string.Join("/", parts.ToArray());
    }

    private static int SetAssetBundles(Command command)
    {
        int assignedCount = 0;

        if (command.assets != null && command.assets.Length > 0)
        {
            foreach (AssetBundleAssignment assignment in command.assets)
            {
                if (assignment == null)
                    continue;

                SetAssetBundle(
                    assignment.assetPath,
                    string.IsNullOrEmpty(assignment.assetBundleName) ? command.assetBundleName : assignment.assetBundleName,
                    string.IsNullOrEmpty(assignment.assetBundleVariant) ? command.assetBundleVariant : assignment.assetBundleVariant);
                assignedCount++;
            }
        }
        else if (command.assetPaths != null && command.assetPaths.Length > 0)
        {
            foreach (string assetPath in command.assetPaths)
            {
                SetAssetBundle(assetPath, command.assetBundleName, command.assetBundleVariant);
                assignedCount++;
            }
        }
        else
        {
            SetAssetBundle(command.assetPath, command.assetBundleName, command.assetBundleVariant);
            assignedCount++;
        }

        AssetDatabase.RemoveUnusedAssetBundleNames();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        return assignedCount;
    }

    private static void SetAssetBundle(string assetPath, string assetBundleName, string assetBundleVariant)
    {
        string normalizedAssetPath = NormalizeAssetPath(assetPath, "assetPath");

        if (AssetDatabase.IsValidFolder(normalizedAssetPath))
            throw new Exception("assetPath must be an asset file, not a folder: " + normalizedAssetPath);

        AssetImporter importer = AssetImporter.GetAtPath(normalizedAssetPath);

        if (importer == null)
            throw new Exception("AssetImporter not found: " + normalizedAssetPath);

        importer.assetBundleName = NormalizeAssetBundleName(assetBundleName);
        importer.assetBundleVariant = NormalizeAssetBundleVariant(assetBundleVariant);
        importer.SaveAndReimport();
        Debug.Log("[CodexBridge] AssetBundle set: " + normalizedAssetPath + " -> " + importer.assetBundleName);
    }

    private static BuildAssetBundleResult BuildAssetBundles(Command command)
    {
        string relativeOutputDirectory = NormalizeProjectRelativePath(
            string.IsNullOrEmpty(command.outputDirectory) ? "Build/abdata" : command.outputDirectory,
            "outputDirectory");
        string absoluteOutputDirectory = Path.GetFullPath(Path.Combine(ProjectRoot, relativeOutputDirectory));
        string projectRootFullPath = Path.GetFullPath(ProjectRoot);

        if (!absoluteOutputDirectory.StartsWith(projectRootFullPath, StringComparison.OrdinalIgnoreCase))
            throw new Exception("outputDirectory must stay inside the Unity project: " + relativeOutputDirectory);

        Directory.CreateDirectory(absoluteOutputDirectory);

        BuildAssetBundleOptions options = BuildAssetBundleOptions.None;

        if (!command.uncompressed)
            options |= BuildAssetBundleOptions.ChunkBasedCompression;

        if (command.forceRebuild)
            options |= BuildAssetBundleOptions.ForceRebuildAssetBundle;

        if (command.strictMode)
            options |= BuildAssetBundleOptions.StrictMode;

        if (command.dryRun)
            options |= BuildAssetBundleOptions.DryRunBuild;

        if (command.disableWriteTypeTree)
            options |= BuildAssetBundleOptions.DisableWriteTypeTree;

        if (command.ignoreTypeTreeChanges)
            options |= BuildAssetBundleOptions.IgnoreTypeTreeChanges;

        BuildTarget buildTarget = ParseBuildTarget(command.buildTarget);
        DateTime prebuildTime = DateTime.Now;

        Debug.Log("[CodexBridge] Building AssetBundles to: " + absoluteOutputDirectory);

        AssetBundleManifest manifest;

        PreviewShaders.SetAllMaterialsOriginal();

        try
        {
            manifest = BuildPipeline.BuildAssetBundles(absoluteOutputDirectory, options, buildTarget);

            if (manifest == null)
                throw new Exception("BuildPipeline.BuildAssetBundles returned null. Check Unity logs for the build error.");

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }
        finally
        {
            PreviewShaders.SetAllMaterialsPreview();
        }

        string[] changedFiles = Directory.GetFiles(absoluteOutputDirectory, "*.unity3d", SearchOption.AllDirectories)
            .Where(path => File.GetLastWriteTime(path) >= prebuildTime)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!command.dryRun && !command.skipPostprocess && changedFiles.Length > 0)
        {
            string koikatsuPath = string.IsNullOrEmpty(command.koikatsuPath) ? KoikatsuPathDefault : command.koikatsuPath;
            IllusionMods.KoikatuModdingTools.SB3UScript.BuildAndRunScripts(
                relativeOutputDirectory,
                koikatsuPath,
                !command.uncompressed,
                changedFiles.ToList());
        }

        Debug.Log("[CodexBridge] Built/updated " + changedFiles.Length + " AssetBundle file(s).");

        return new BuildAssetBundleResult
        {
            outputDirectory = absoluteOutputDirectory,
            changedFiles = changedFiles
        };
    }

    private static BuildTarget ParseBuildTarget(string buildTarget)
    {
        if (string.IsNullOrEmpty(buildTarget))
            return BuildTarget.StandaloneWindows;

        try
        {
            return (BuildTarget)Enum.Parse(typeof(BuildTarget), buildTarget, true);
        }
        catch
        {
            throw new Exception("Unsupported buildTarget: " + buildTarget);
        }
    }

    private static PrimitiveType ParsePrimitiveType(string primitive)
    {
        if (string.IsNullOrEmpty(primitive))
            return PrimitiveType.Cube;

        try
        {
            return (PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitive, true);
        }
        catch
        {
            throw new Exception("Unsupported primitive: " + primitive);
        }
    }

    private static void SetMaterialColor(Material material, string property, string value)
    {
        if (!material.HasProperty(property))
            throw new Exception("Material shader has no color property: " + property);

        Color color;

        if (!ColorUtility.TryParseHtmlString(value, out color))
            throw new Exception("Invalid color: " + value);

        material.SetColor(property, color);
    }

    private static void SetMaterialTexture(Material material, string property, string texturePath)
    {
        if (!material.HasProperty(property))
            throw new Exception("Material shader has no texture property: " + property);

        string normalizedTexturePath = NormalizeAssetPath(texturePath, "mainTexture");
        Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(normalizedTexturePath);

        if (texture == null)
            throw new Exception("Texture not found: " + normalizedTexturePath);

        material.SetTexture(property, texture);
    }

    private static void SetMaterialFloat(Material material, string property, string value)
    {
        if (!material.HasProperty(property))
            throw new Exception("Material shader has no float property: " + property);

        float parsedValue;

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue))
            throw new Exception("Invalid float value for " + property + ": " + value);

        material.SetFloat(property, parsedValue);
    }

    private static void EnsureParentFolder(string assetPath)
    {
        string folder = Path.GetDirectoryName(assetPath);

        if (string.IsNullOrEmpty(folder))
            throw new Exception("assetPath has no parent folder: " + assetPath);

        EnsureAssetFolder(folder.Replace("\\", "/"));
    }

    private static void EnsureAssetFolder(string folder)
    {
        folder = NormalizeAssetPath(folder, "folder").TrimEnd('/');

        if (AssetDatabase.IsValidFolder(folder))
            return;

        string[] parts = folder.Split('/');
        string current = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];

            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);

            current = next;
        }
    }

    private static string NormalizeAssetPath(string path, string fieldName)
    {
        if (string.IsNullOrEmpty(path))
            throw new Exception(fieldName + " is empty.");

        path = path.Trim().Replace("\\", "/");

        if (path.Contains(":") || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("/../") || path.StartsWith("../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
            throw new Exception(fieldName + " must be a relative Assets path: " + path);

        if (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal))
            throw new Exception(fieldName + " must start with Assets/: " + path);

        return path;
    }

    private static string NormalizeProjectRelativePath(string path, string fieldName)
    {
        if (string.IsNullOrEmpty(path))
            throw new Exception(fieldName + " is empty.");

        path = path.Trim().Replace("\\", "/");

        if (path.Contains(":") || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("/../") || path.StartsWith("../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
            throw new Exception(fieldName + " must be a project-relative path: " + path);

        return path;
    }

    private static string NormalizeAssetBundleName(string assetBundleName)
    {
        if (string.IsNullOrEmpty(assetBundleName))
            return string.Empty;

        assetBundleName = assetBundleName.Trim().Replace("\\", "/");

        if (assetBundleName.Contains(":") || assetBundleName.StartsWith("/", StringComparison.Ordinal) || assetBundleName.Contains("/../") || assetBundleName.StartsWith("../", StringComparison.Ordinal) || assetBundleName.EndsWith("/..", StringComparison.Ordinal))
            throw new Exception("assetBundleName must be a relative bundle path: " + assetBundleName);

        assetBundleName = assetBundleName.ToLowerInvariant();

        if (assetBundleName == "abdata" || assetBundleName.StartsWith("abdata/", StringComparison.Ordinal))
            throw new Exception("assetBundleName must be relative to Build/abdata and must not start with abdata/: " + assetBundleName);

        return assetBundleName;
    }

    private static string NormalizeAssetBundleVariant(string assetBundleVariant)
    {
        if (string.IsNullOrEmpty(assetBundleVariant))
            return string.Empty;

        assetBundleVariant = assetBundleVariant.Trim();

        if (assetBundleVariant.Contains("/") || assetBundleVariant.Contains("\\") || assetBundleVariant.Contains(":"))
            throw new Exception("assetBundleVariant must be a simple variant name: " + assetBundleVariant);

        return assetBundleVariant.ToLowerInvariant();
    }

    private static bool IsFileReady(string file)
    {
        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalMilliseconds < 200d)
            return false;

        try
        {
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WriteResult(string commandFile, Command command, bool success, string message)
    {
        EnsureBridgeFolders();

        string fileName = Path.GetFileNameWithoutExtension(commandFile);
        string resultPath = Path.Combine(Outbox, fileName + ".result.json");
        Result result = new Result
        {
            success = success,
            operation = command == null ? string.Empty : command.operation,
            assetPath = command == null ? string.Empty : command.assetPath,
            message = message,
            projectRoot = ProjectRoot,
            bridgeRoot = Root,
            files = _resultFiles,
            probe = _probeInspection,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
        };

        File.WriteAllText(resultPath, JsonUtility.ToJson(result, true));

        string donePath = commandFile + ".done";

        if (File.Exists(donePath))
            File.Delete(donePath);

        File.Move(commandFile, donePath);
    }

    private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        try
        {
            EnsureBridgeFolders();

            LogEntry entry = new LogEntry
            {
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                type = type.ToString(),
                message = condition,
                stackTrace = stackTrace
            };

            lock (LogLock)
            {
                File.AppendAllText(UnityLog, JsonUtility.ToJson(entry) + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break the editor update loop.
        }
    }

    private static void EnsureBridgeFolders()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(Outbox);
        Directory.CreateDirectory(Logs);
    }

    [Serializable]
    private class Command
    {
        public string operation;
        public string assetPath;
        public string destinationAssetPath;
        public string shader;
        public string color;
        public string colorProperty;
        public string mainTexture;
        public string textureProperty;
        public string floatProperty;
        public float floatValue;
        public string name;
        public string materialPath;
        public string primitive;
        public int resolution;
        public int captureResolution;
        public int capturesPerFrame;
        public int gridX;
        public int gridY;
        public int gridZ;
        public int priority;
        public int importance;
        public int cullingMask;
        public bool hdr;
        public bool boxProjection;
        public bool renderDynamicObjects;
        public bool useOcclusionCulling;
        public bool renderOnEnable;
        public bool captureOnEnable;
        public float intensity;
        public float blendDistance;
        public float nearClip;
        public float farClip;
        public float shadowDistance;
        public float edgeFade;
        public float updateInterval;
        public float[] center;
        public float[] size;
        public string updateMode;
        public string timeSlicingMode;
        public string reflectionClearFlags;
        public string cameraClearFlags;
        public string backgroundColor;
        public string assetBundleName;
        public string assetBundleVariant;
        public string[] assetPaths;
        public AssetBundleAssignment[] assets;
        public string outputDirectory;
        public string buildTarget;
        public bool uncompressed;
        public bool forceRebuild;
        public bool strictMode;
        public bool dryRun;
        public bool disableWriteTypeTree;
        public bool ignoreTypeTreeChanges;
        public bool skipPostprocess;
        public string koikatsuPath;
    }

    [Serializable]
    private class AssetBundleAssignment
    {
        public string assetPath;
        public string assetBundleName;
        public string assetBundleVariant;
    }

    private class BuildAssetBundleResult
    {
        public string outputDirectory;
        public string[] changedFiles;
    }

    [Serializable]
    private class Result
    {
        public bool success;
        public string operation;
        public string assetPath;
        public string message;
        public string projectRoot;
        public string bridgeRoot;
        public string[] files;
        public ProbeInspection probe;
        public string time;
    }

    [Serializable]
    private class ProbeInspection
    {
        public string kind;
        public bool hasItemComponent;
        public bool hasReflectionProbe;
        public bool hasReflectionController;
        public bool hasSHProbeVolume;
        public int resolution;
        public int captureResolution;
        public int capturesPerFrame;
        public int gridX;
        public int gridY;
        public int gridZ;
        public int priority;
        public int importance;
        public bool hdr;
        public bool boxProjection;
        public bool renderDynamicObjects;
        public bool useOcclusionCulling;
        public float intensity;
        public float blendDistance;
        public float nearClip;
        public float farClip;
        public float edgeFade;
        public float[] center;
        public float[] size;
    }

    [Serializable]
    private class LogEntry
    {
        public string time;
        public string type;
        public string message;
        public string stackTrace;
    }
}

