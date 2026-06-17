using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using UnityEngine.Rendering;
using Unity.Netcode;

public class CustomLevelPlugin : IPuckPlugin
{
    private static GameObject spawnedLevel;
    private static AssetBundle bundle;
    private Harmony harmony;
    private static bool _practiceHelperPatched;
    // Kept static so TryPatchPracticeHelpers can access it after OnEnable returns
    private static Harmony _harmony;

    // Pucks we spawned that should not be touched by the game's out-of-bounds / face-off systems
    internal static readonly System.Collections.Generic.HashSet<Puck> ProtectedPucks =
        new System.Collections.Generic.HashSet<Puck>();

    public static AssetBundle GetBundle() => bundle;

    public bool OnEnable()
    {
        try
        {
            harmony = new Harmony("com.testlevel.plugin");
            _harmony = harmony;

            string bundlePath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "assets", "puckobjects");
            Debug.Log($"[CustomLevel] Looking for bundle at: {bundlePath}");
            bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogError("[CustomLevel] Failed to load AssetBundle");
                return false;
            }

            var original = typeof(LevelController).GetMethod("Awake",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(CustomLevelPlugin),
                nameof(OnLevelAwake)));

            var spawnPucksMethod = typeof(PuckManager).GetMethod("Server_SpawnPucksForPhase",
                BindingFlags.Instance | BindingFlags.Public);
            if (spawnPucksMethod != null)
            {
                harmony.Patch(spawnPucksMethod, new HarmonyMethod(typeof(CustomLevelPlugin),
                    nameof(PreventPuckSpawn)));
                Debug.Log("[CustomLevel] Patched Server_SpawnPucksForPhase");
            }

            // Track whether the chat input box is open so PuckSpawnSync can suppress R.
            // UIChat uses Unity UI Toolkit (VisualElement), not InputField/TMP, so we can't
            // detect it via EventSystem — patching StartInput/StopInput is the only reliable way.
            // UIChat only exists on clients — dedicated servers won't find it, which is fine
            var chatStart = typeof(UIChat).GetMethod("StartInput",
                BindingFlags.Instance | BindingFlags.Public);
            if (chatStart != null)
                harmony.Patch(chatStart, null,
                    new HarmonyMethod(typeof(CustomLevelPlugin), nameof(OnChatStartInput)));

            var chatStop = typeof(UIChat).GetMethod("StopInput",
                BindingFlags.Instance | BindingFlags.Public);
            if (chatStop != null)
                harmony.Patch(chatStop, null,
                    new HarmonyMethod(typeof(CustomLevelPlugin), nameof(OnChatStopInput)));

            // Install network bounds patch immediately — safe before chunks activate
            CL_NetworkBoundsPatch.EnsurePatched();

            // When loaded from the Steam Workshop the mod is enabled after the server has
            // already initialised the scene, so LevelController.Awake fires before our
            // postfix is installed and OnLevelAwake never runs. Catch that case here.
            if (GameObject.FindFirstObjectByType<LevelController>() != null)
            {
                Debug.Log("[CustomLevel] Level already active at plugin load — running OnLevelAwake immediately.");
                OnLevelAwake();
            }
            else
            {
                Debug.Log("[CustomLevel] Plugin enabled, waiting for level load");
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[CustomLevel] OnEnable error: {e.Message}");
            return false;
        }
    }

    public static bool MinimapShowPrefix() => false;

    // True while the player has the chat input box open
    internal static bool ChatInputActive = false;
    public static void OnChatStartInput() => ChatInputActive = true;
    public static void OnChatStopInput()  => ChatInputActive = false;
    public static bool PreventPuckSpawn() => false;

    // Blocks the game from despawning pucks we own; PuckSpawnSync removes from the set
    // before calling Server_DespawnPuck itself, so our own despawns still go through
    public static bool PreventPuckDespawn(Puck puck) => !ProtectedPucks.Contains(puck);

    // Blocks the game from teleporting our pucks to face-off / reset positions
    public static bool PreventPuckMove(Puck __instance, ref Vector3 position)
    {
        if (ProtectedPucks.Contains(__instance)) return false;
        return true;
    }

    public static bool PreventClamp(object __instance, ref Vector3 __result, Vector3 position)
    {
        __result = position;
        return false;
    }

    public static bool ExpandVirtualRinkBounds(object __instance, ref bool __result,
        ref float xMin, ref float xMax, ref float zMin, ref float zMax, ref float cornerRadius)
    {
        xMin = -500f; xMax = 500f; zMin = -500f; zMax = 500f; cornerRadius = 0f;
        __result = true;
        return false;
    }

    // MaxPractice is an optional plugin; we defer patching until the level loads so it
    // has time to register its assembly before we search for it
    private static void TryPatchPracticeHelpers()
    {
        if (_practiceHelperPatched) return;
        Type t = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            if (asm.GetName().Name == "MaxPractice")
            { t = asm.GetType("MaxPractice.PracticeHelpers"); break; }

        if (t == null) { Debug.LogWarning("[CustomLevel] MaxPractice.PracticeHelpers not found"); return; }

        var clamp = t.GetMethod("ClampToVirtualRink",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (clamp != null)
        { _harmony.Patch(clamp, new HarmonyMethod(typeof(CustomLevelPlugin), nameof(PreventClamp))); Debug.Log("[CustomLevel] Patched ClampToVirtualRink"); }

        var bounds = t.GetMethod("TryGetVirtualRinkBounds",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (bounds != null)
        { _harmony.Patch(bounds, new HarmonyMethod(typeof(CustomLevelPlugin), nameof(ExpandVirtualRinkBounds))); Debug.Log("[CustomLevel] Patched TryGetVirtualRinkBounds"); }

        _practiceHelperPatched = true;
    }

    public static void OnLevelAwake()
    {
        try
        {
            Debug.Log("[CustomLevel] Level awoke, swapping geometry");

            TryPatchPracticeHelpers();

            // NetworkManager is ready by the time LevelController.Awake fires
            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    CL_ChunkSyncServer.CmmName, CL_ChunkSyncClient.OnChunkMessage);
                Debug.Log("[CustomLevel] Registered CUSTOMLEVEL/Chunks CMM handler");
            }
            catch (Exception cmmEx)
            {
                Debug.LogWarning("[CustomLevel] CMM registration failed: " + cmmEx.Message);
            }

            CL_NetworkBoundsPatch.EnableChunks();

            bool isServer = NetworkManager.Singleton?.IsServer ?? true;
            bool isClient = NetworkManager.Singleton?.IsClient ?? true;
            // Offline / solo play: treat the local instance as both server and client
            if (!isServer && !isClient) { isServer = true; isClient = true; }

            Debug.Log($"[CustomLevel] IsServer={isServer} IsClient={isClient}");

            // Exact-name targets
            string[] toHide = {
                "Rink", "Hangar", "Blue Goal", "Red Goal", "Goal Blue", "Goal Red",
                "Scoreboard Blue", "Scoreboard Red", "Lights",
                "Spectator Booth 1","Spectator Booth 2","Spectator Booth 3","Spectator Booth 4",
                "Spectator Booth 5","Spectator Booth 6","Spectator Booth 7","Spectator Booth 8",
                "Spectator Booth 9","Spectator Booth 10",
                // Net/goal child objects discovered via keyword sweep logs
                "Net", "Net Collider", "Goal Post Collider", "Goal Trigger", "Goal Player Collider",
            };
            foreach (string hideName in toHide)
            {
                var go = GameObject.Find(hideName);
                if (go != null) { go.SetActive(false); Debug.Log($"[CustomLevel] Hid {hideName}"); }
            }

            if (bundle == null) { Debug.LogError("[CustomLevel] Bundle is null"); return; }

            GameObject prefab = bundle.LoadAsset<GameObject>("TestLevel");
            if (prefab == null) { Debug.LogError("[CustomLevel] Failed to load TestLevel prefab"); return; }

            if (isServer)
            {
                GameObject serverLevel = GameObject.Instantiate(prefab);
                serverLevel.name = "CustomLevel_Server";
                serverLevel.transform.position = Vector3.zero;

                int iceLayer = LayerMask.NameToLayer("Ice");
                foreach (Transform t in serverLevel.GetComponentsInChildren<Transform>(true))
                    t.gameObject.layer = iceLayer >= 0 ? iceLayer : t.gameObject.layer;

                foreach (MeshFilter mf in serverLevel.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    MeshCollider mc = mf.gameObject.GetComponent<MeshCollider>()
                        ?? mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mc.convex = false;
                    if (iceLayer >= 0) mf.gameObject.layer = iceLayer;
                }

                // Server only needs collision; disable renderers to avoid double-rendering on the host
                foreach (Renderer r in serverLevel.GetComponentsInChildren<Renderer>(true))
                    if (r != null) r.enabled = false;

                spawnedLevel = serverLevel;
                Debug.Log("[CustomLevel] Server level spawned (colliders only)");

                Level level = GameObject.FindFirstObjectByType<Level>();
                if (level != null)
                {
                    level.Bounds = new Bounds(Vector3.zero, new Vector3(1000, 50, 1000));
                    Debug.Log("[CustomLevel] Expanded level bounds");
                }

                var syncObj = new GameObject("PuckSpawnSync");
                syncObj.AddComponent<PuckSpawnSync>();
                Debug.Log("[CustomLevel] PuckSpawnSync added");

                // Prevent the game's out-of-bounds / face-off logic from touching our custom pucks
                var despawnPuck = typeof(PuckManager).GetMethod("Server_DespawnPuck",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (despawnPuck != null)
                {
                    _harmony.Patch(despawnPuck,
                        new HarmonyMethod(typeof(CustomLevelPlugin), nameof(PreventPuckDespawn)));
                    Debug.Log("[CustomLevel] Patched PuckManager.Server_DespawnPuck");
                }
                else
                    Debug.LogWarning("[CustomLevel] PuckManager.Server_DespawnPuck not found");

                // Try common names for the method that teleports a puck to a face-off / reset spot
                string[] moveMethods = { "Server_MovePuck", "Server_ResetPuck", "Server_FaceOffDrop",
                                         "Server_MovePuckToFaceOff", "Server_SetPuckPosition" };
                foreach (string mn in moveMethods)
                {
                    var m = typeof(PuckManager).GetMethod(mn,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null)
                    {
                        _harmony.Patch(m, new HarmonyMethod(typeof(CustomLevelPlugin), nameof(PreventPuckMove)));
                        Debug.Log($"[CustomLevel] Patched PuckManager.{mn}");
                    }
                }
            }

            if (isClient)
            {
                // Re-apply bundle materials by matching renderer/slot name because Instantiate loses
                // bundle material references on the client side
                var materials = new System.Collections.Generic.Dictionary<string, Material>();
                foreach (string assetName in bundle.GetAllAssetNames())
                    if (assetName.EndsWith(".mat"))
                    {
                        Material mat = bundle.LoadAsset<Material>(assetName);
                        if (mat != null) { materials[mat.name.ToLower()] = mat; }
                    }

                var meshToMaterial = new System.Collections.Generic.Dictionary<string, string>();
                foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    for (int i = 0; i < r.sharedMaterials.Length; i++)
                        if (r.sharedMaterials[i] != null)
                            meshToMaterial[r.gameObject.name + "_" + i] = r.sharedMaterials[i].name.ToLower();
                }

                GameObject clientLevel = GameObject.Instantiate(prefab);
                clientLevel.name = "CustomLevel_Client";
                clientLevel.transform.position = Vector3.zero;

                // Client only needs visuals; remove colliders to avoid duplicate physics
                foreach (Collider c in clientLevel.GetComponentsInChildren<Collider>(true))
                    if (c != null) GameObject.Destroy(c);

                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                foreach (Renderer r in clientLevel.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    Material[] mats = r.materials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        string key = r.gameObject.name + "_" + i;
                        if (meshToMaterial.TryGetValue(key, out string mn) &&
                            materials.TryGetValue(mn, out Material bm))
                            mats[i] = bm;
                        if (shader != null && mats[i] != null)
                        {
                            mats[i].shader = shader;
                            if (mats[i].HasProperty("_Surface")) mats[i].SetFloat("_Surface", 0f);
                            if (mats[i].HasProperty("_EmissionColor")) mats[i].DisableKeyword("_EMISSION");
                        }
                    }
                    r.materials = mats;
                    r.shadowCastingMode = ShadowCastingMode.On;
                    r.receiveShadows = true;
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                    r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                }

                var minimapType = typeof(LevelController).Assembly.GetType("UIMinimap");
                if (minimapType != null)
                {
                    var showMethod = minimapType.GetMethod("Show",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (showMethod != null)
                        new Harmony("com.testlevel.minimap").Patch(showMethod,
                            new HarmonyMethod(typeof(CustomLevelPlugin), nameof(MinimapShowPrefix)));
                }

                UIMinimap minimap = GameObject.FindFirstObjectByType<UIMinimap>();
                minimap?.Hide();

                var syncObj = new GameObject("PuckSpawnSync");
                syncObj.AddComponent<PuckSpawnSync>();

                Debug.Log("[CustomLevel] Client level spawned (visuals only)");
            }

            Debug.Log("[CustomLevel] Level swap complete");
        }
        catch (Exception e)
        {
            Debug.LogError($"[CustomLevel] OnLevelAwake error: {e.Message}");
        }
    }

    public bool OnDisable()
    {
        CL_NetworkBoundsPatch.Disable();
        harmony?.UnpatchSelf();
        new Harmony("com.testlevel.minimap").UnpatchSelf();
        _practiceHelperPatched = false;
        ProtectedPucks.Clear();
        ChatInputActive = false;

        if (spawnedLevel != null) GameObject.Destroy(spawnedLevel);
        if (bundle != null) { bundle.Unload(true); bundle = null; }

        return true;
    }
}
