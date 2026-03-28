using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OverlayDearImGui;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Path = System.IO.Path;

namespace InteractableSpawnFixNS
{
    public static class DictionaryExtensions
    {
        public static TValue GetValueOrAddDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key) where TValue : new()
        {
            if (!dict.TryGetValue(key, out var value))
            {
                value = new TValue();
                dict[key] = value;
            }
            return value;
        }
    }

    [BepInDependency(OverlayDearImGuiBepInEx5.PluginGUID)]
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class InteractableSpawnFix : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "iDeathHD";
        public const string PluginName = "InteractableSpawnFix";
        public const string PluginVersion = "1.0.0";

        private static bool IsValidFloat(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsValidVector2(ImGuiNET.Vector2 vec)
        {
            return IsValidFloat(vec.x) && IsValidFloat(vec.y);
        }

        private static void DrawDottedLine(ImDrawListPtr draw, ImGuiNET.Vector2 a, ImGuiNET.Vector2 b, uint color)
        {
            float segmentLength = 6f;
            float gap = 4f;

            var dir = b - a;
            float length = dir.magnitude;

            if (length <= 0.001f || float.IsNaN(length) || float.IsInfinity(length))
                return;

            dir /= length;

            float dist = 0f;
            while (dist < length)
            {
                var start = a + dir * dist;
                var end = a + dir * MathF.Min(dist + segmentLength, length);

                if (!IsValidVector2(start) || !IsValidVector2(end))
                    break;

                draw.AddLine(start, end, color, 1f);

                dist += segmentLength + gap;
            }
        }

        private static bool IsOccluded(Camera cam, UnityEngine.Vector3 a, UnityEngine.Vector3 b)
        {
            UnityEngine.Vector3 mid = (a + b) * 0.5f;
            UnityEngine.Vector3 dir = mid - cam.transform.position;

            if (Physics.Raycast(cam.transform.position, dir.normalized, out RaycastHit hit, dir.magnitude))
            {
                // anything hit before midpoint = occluded
                return true;
            }

            return false;
        }

        private static void DrawWireBox(UnityEngine.Vector3 topLeft, UnityEngine.Vector3 bottomRight, ImGuiNET.Vector4 color)
        {
            var cam = Camera.main;
            if (cam == null) return;

            var draw = ImGui.GetBackgroundDrawList();
            float height = Screen.height;

            // Normalize bounds (in case inputs are flipped)
            UnityEngine.Vector3 min = UnityEngine.Vector3.Min(topLeft, bottomRight);
            UnityEngine.Vector3 max = UnityEngine.Vector3.Max(topLeft, bottomRight);

            // 8 corners of the AABB
            UnityEngine.Vector3[] pts = new UnityEngine.Vector3[8]
            {
                new UnityEngine.Vector3(min.x, min.y, min.z), // 0
                new UnityEngine.Vector3(max.x, min.y, min.z), // 1
                new UnityEngine.Vector3(min.x, max.y, min.z), // 2
                new UnityEngine.Vector3(max.x, max.y, min.z), // 3

                new UnityEngine.Vector3(min.x, min.y, max.z), // 4
                new UnityEngine.Vector3(max.x, min.y, max.z), // 5
                new UnityEngine.Vector3(min.x, max.y, max.z), // 6
                new UnityEngine.Vector3(max.x, max.y, max.z), // 7
            };

            // Project to screen
            ImGuiNET.Vector2[] screen = new ImGuiNET.Vector2[8];
            bool[] visible = new bool[8];

            for (int i = 0; i < 8; i++)
            {
                var sp = cam.WorldToScreenPoint(pts[i]);
                visible[i] = sp.z > 0f;

                screen[i] = new ImGuiNET.Vector2(
                    sp.x,
                    height - sp.y
                );
            }

            // Proper AABB edges
            int[,] edges = new int[,]
            {
                {0,1},{1,3},{3,2},{2,0}, // bottom face
                {4,5},{5,7},{7,6},{6,4}, // top face
                {0,4},{1,5},{2,6},{3,7}  // verticals
            };

            uint coloruInt = ImGui.GetColorU32(color);
            var dottedColorFromColor = new ImGuiNET.Vector4(color.x, color.y, color.z, 0.4f);
            uint dottedColor = ImGui.GetColorU32(dottedColorFromColor);

            for (int i = 0; i < edges.GetLength(0); i++)
            {
                int a = edges[i, 0];
                int b = edges[i, 1];

                if (!visible[a] || !visible[b]) continue;

                bool occluded = IsOccluded(cam, pts[a], pts[b]);

                if (occluded)
                    DrawDottedLine(draw, screen[a], screen[b], dottedColor);
                else
                {
                    if (!IsValidVector2(screen[a]) || !IsValidVector2(screen[b]))
                        break;

                    draw.AddLine(screen[a], screen[b], coloruInt, 2f);
                }
            }
        }

        public void Awake()
        {
            Log.Init(Logger);

            On.RoR2.Navigation.NodeGraph.GetNodePosition += BlockInvalidSpawnPositions;

            SceneManager.activeSceneChanged += (Scene oldScene, Scene newScene) =>
            {
                Log.Info($"Scene changed to '{newScene.name}'");
            };

            {
                if (Directory.Exists(SaveFolder))
                {
                    cachedFiles = Directory.GetFiles(SaveFolder, "*.json").Select(s => new FileInfo() { FilePath = s, FileName = Path.GetFileName(s) }).ToArray();
                    selectedFile = cachedFiles.FirstOrDefault(f => f.FileName == "Default.json");
                    if (selectedFile != null)
                    {
                        LoadSpawnPoints(selectedFile.FilePath);
                    }
                }
                else
                {
                    Directory.CreateDirectory(SaveFolder);
                }
            }

#if DEBUG
            On.RoR2.SceneDirector.Start += SceneDirector_Start;
#endif
        }

        private static int CustomMultiplierInteractableCredit = 1;

#if DEBUG
        private static bool StartBlockingSpawns = false;

        private void SceneDirector_Start(On.RoR2.SceneDirector.orig_Start orig, SceneDirector self)
        {
            if (NetworkServer.active)
            {
                self.rng = new Xoroshiro128Plus((ulong)Run.instance.stageRng.nextUint);
                float num = 0.5f + (float)Run.instance.participatingPlayerCount * 0.5f;
                ClassicStageInfo component = SceneInfo.instance.GetComponent<ClassicStageInfo>();
                if (component)
                {
                    self.interactableCredit = (int)((float)component.sceneDirectorInteractibleCredits * num);
                    if (component.bonusInteractibleCreditObjects != null)
                    {
                        for (int i = 0; i < component.bonusInteractibleCreditObjects.Length; i++)
                        {
                            ClassicStageInfo.BonusInteractibleCreditObject bonusInteractibleCreditObject = component.bonusInteractibleCreditObjects[i];
                            if (bonusInteractibleCreditObject.objectThatGrantsPointsIfEnabled && bonusInteractibleCreditObject.objectThatGrantsPointsIfEnabled.activeSelf)
                            {
                                self.interactableCredit += bonusInteractibleCreditObject.points;
                            }
                        }
                    }

                    self.interactableCredit *= CustomMultiplierInteractableCredit;

                    Debug.LogFormat("Spending {0} credits on interactables...", new object[] { self.interactableCredit });
                    self.monsterCredit = (int)((float)component.sceneDirectorMonsterCredits * Run.instance.difficultyCoefficient);
                }

                static void InvokeEvent<T>(T instance, string eventName)
                {
                    EventInfo eventInfo = typeof(T).GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
                    if (eventInfo == null)
                    {
                        throw new Exception("Event not found.");
                    }

                    FieldInfo field = typeof(T).GetField(eventName, BindingFlags.Static | BindingFlags.NonPublic);
                    if (field == null)
                    {
                        throw new Exception("Backing field for event not found.");
                    }

                    var del = (Action<T>)field.GetValue(null);

                    del?.Invoke(instance);
                }
                Try(() => InvokeEvent(self, nameof(SceneDirector.onPrePopulateSceneServer)));
                StartBlockingSpawns = true;
                self.PopulateScene();
                StartBlockingSpawns = false;
                Try(() => InvokeEvent(self, nameof(SceneDirector.onPostPopulateSceneServer)));
            }
        }
#endif

        private bool BlockInvalidSpawnPositions(On.RoR2.Navigation.NodeGraph.orig_GetNodePosition orig, RoR2.Navigation.NodeGraph self, RoR2.Navigation.NodeGraph.NodeIndex nodeIndex, out UnityEngine.Vector3 position)
        {
            var spawnPoints = _sceneNameToBlockedInteractableSpawnPoints.GetValueOrAddDefault(SceneManager.GetActiveScene().name);

            var nodePos = orig(self, nodeIndex, out position);

#if DEBUG
            if (!StartBlockingSpawns)
            {
                return nodePos;
            }
#endif

            //Log.Info("Checking interactable spawn point at node " + nodeIndex + " (position " + position + ") against " + spawnPoints.Count + " blocked areas.");

            foreach (var item in spawnPoints)
            {
                //Log.Info($"Checking against blocked area '{item.name}' with bounds min {item.boundsMin} and max {item.boundsMax}.");

                bool inside =
                    position.x >= Mathf.Min(item.boundsMin.x, item.boundsMax.x) &&
                    position.x <= Mathf.Max(item.boundsMin.x, item.boundsMax.x) &&
                    position.y >= Mathf.Min(item.boundsMin.y, item.boundsMax.y) &&
                    position.y <= Mathf.Max(item.boundsMin.y, item.boundsMax.y) &&
                    position.z >= Mathf.Min(item.boundsMin.z, item.boundsMax.z) &&
                    position.z <= Mathf.Max(item.boundsMin.z, item.boundsMax.z);

                if (inside)
                {
                    Log.Info($"Prevented interactable spawn at node {nodeIndex} (position {position}) due to it being inside disallowed area '{item.name}'");

                    {
                        ImGuiNET.Vector3 halfSize = new(1f, 1f, 1f);
                        var positionImGui = new ImGuiNET.Vector3(position.x, position.y, position.z);
                        _sceneNameToInteractablesThatGotDenied.GetValueOrAddDefault(SceneManager.GetActiveScene().name).Add(
                            new BlockedInteractableSpawnPoint()
                            {
                                name = $"Denied Spawn Point {spawnPoints.Count + 1}",
                                boundsMin = positionImGui - halfSize,
                                boundsMax = positionImGui + halfSize
                            });
                    }

                    // inside disallowed spawn area - return false with zero position to prevent spawn
                    position = UnityEngine.Vector3.zero;
                    return false;
                }
            }

            return nodePos;
        }

        public void OnEnable()
        {
            Overlay.OnRender += MyUI;
        }

        private static bool _isMyUIOpen = false;

        class BlockedInteractableSpawnPoint
        {
            public string name;

            [JsonConverter(typeof(Vector3Converter))]
            public ImGuiNET.Vector3 boundsMin;
            [JsonConverter(typeof(Vector3Converter))]
            public ImGuiNET.Vector3 boundsMax;
        }

        // This is needed because Newtonsoft.Json just choke while reflecting that type because of a skill issue on their part.
        public class Vector3Converter : JsonConverter<ImGuiNET.Vector3>
        {
            public override void WriteJson(JsonWriter writer, ImGuiNET.Vector3 value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x");
                writer.WriteValue(value.x);
                writer.WritePropertyName("y");
                writer.WriteValue(value.y);
                writer.WritePropertyName("z");
                writer.WriteValue(value.z);
                writer.WriteEndObject();
            }

            public override ImGuiNET.Vector3 ReadJson(JsonReader reader, System.Type objectType, ImGuiNET.Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                var obj = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new ImGuiNET.Vector3
                {
                    x = obj["x"]?.Value<float>() ?? 0,
                    y = obj["y"]?.Value<float>() ?? 0,
                    z = obj["z"]?.Value<float>() ?? 0
                };
            }
        }

        private static Dictionary<string, List<BlockedInteractableSpawnPoint>> _sceneNameToBlockedInteractableSpawnPoints = new();
        private static Dictionary<string, List<BlockedInteractableSpawnPoint>> _sceneNameToInteractablesThatGotDenied = new();

        private static void DrawDisallowedInteractableSpawnBoxes()
        {
            {
                foreach (var item in _sceneNameToBlockedInteractableSpawnPoints.GetValueOrAddDefault(SceneManager.GetActiveScene().name))
                {
                    DrawWireBox(
                        new(item.boundsMin.x, item.boundsMin.y, item.boundsMin.z),
                        new(item.boundsMax.x, item.boundsMax.y, item.boundsMax.z),
                        new ImGuiNET.Vector4(1f, 1f, 0f, 1f)
                    );
                }
            }

            {
                foreach (var item in _sceneNameToInteractablesThatGotDenied.GetValueOrAddDefault(SceneManager.GetActiveScene().name))
                {
                    DrawWireBox(
                        new(item.boundsMin.x, item.boundsMin.y, item.boundsMin.z),
                        new(item.boundsMax.x, item.boundsMax.y, item.boundsMax.z),
                        new ImGuiNET.Vector4(1f, 0f, 0f, 1f)
                    );
                }
            }
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private static string spawnPointNameFilter = "";

        private static void MyUI()
        {
            if (ImGui.BeginMainMenuBar())
            {
                if (ImGui.BeginMenu("Debug", true))
                {
                    if (ImGui.MenuItem("Open Debug Window", null, _isMyUIOpen))
                    {
                        _isMyUIOpen ^= true;
                    }

                    ImGui.EndMenu();
                }

                ImGui.EndMainMenuBar();
            }

            if (ImGui.Begin("Interactable Node Fixes", ImGuiWindowFlags.AlwaysAutoResize))
            {
                Try(() => ImGui.Text($"Current Scene: {SceneManager.GetActiveScene().name}"));

                ImGui.DragInt("Custom Interactable Credit Multiplier", ref CustomMultiplierInteractableCredit, 1, 1, 1000);

                var spawnPoints = _sceneNameToBlockedInteractableSpawnPoints.GetValueOrAddDefault(SceneManager.GetActiveScene().name);

                Try(DrawSerializationUI);

                ImGui.Separator();

                if (ImGui.Button("Add Interactable Spawn Point"))
                {
                    Try(() =>
                    {
                        var body = PlayerCharacterMasterController.instances[0].body;
                        var origin = new ImGuiNET.Vector3(body.corePosition.x, body.corePosition.y, body.corePosition.z);

                        // define size
                        ImGuiNET.Vector3 halfSize = new(1f, 1f, 1f);

                        // create bounds around player
                        _sceneNameToBlockedInteractableSpawnPoints.GetValueOrAddDefault(SceneManager.GetActiveScene().name).Add(new BlockedInteractableSpawnPoint
                        {
                            name = $"Spawn Point {spawnPoints.Count + 1}",
                            boundsMin = origin - halfSize,
                            boundsMax = origin + halfSize
                        });
                    });
                }


                // Filter input
                ImGui.InputText("Filter by Name", ref spawnPointNameFilter, 256);

                for (int i = 0; i < spawnPoints.Count; i++)
                {
                    var item = spawnPoints[i];

                    // Skip items that don't match the filter
                    if (!string.IsNullOrEmpty(spawnPointNameFilter) &&
                        !item.name.Contains(spawnPointNameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Try(() =>
                    {
                        ImGui.Text($"[{i}] Name: {item.name}");

                        void UpdateBounds()
                        {
                            var minUnity = new UnityEngine.Vector3(item.boundsMin.x, item.boundsMin.y, item.boundsMin.z);
                            var maxUnity = new UnityEngine.Vector3(item.boundsMax.x, item.boundsMax.y, item.boundsMax.z);
                            var min = UnityEngine.Vector3.Min(minUnity, maxUnity);
                            var max = UnityEngine.Vector3.Max(minUnity, maxUnity);
                            item.boundsMin = new(item.boundsMin.x, item.boundsMin.y, item.boundsMin.z);
                            item.boundsMax = new(item.boundsMax.x, item.boundsMax.y, item.boundsMax.z);
                        }

                        if (ImGui.DragFloat3($"Min##{i}", ref item.boundsMin) |
                            ImGui.DragFloat3($"Max##{i}", ref item.boundsMax))
                        {
                            UpdateBounds();
                        }

                        if (ImGui.Button($"Set Min To Current Player Pos##{i}"))
                        {
                            var body = PlayerCharacterMasterController.instances[0].body;
                            var origin = new ImGuiNET.Vector3(body.corePosition.x, body.corePosition.y, body.corePosition.z);
                            item.boundsMin = origin;
                            UpdateBounds();
                        }

                        if (ImGui.Button($"Set Max To Current Player Pos##{i}"))
                        {
                            var body = PlayerCharacterMasterController.instances[0].body;
                            var origin = new ImGuiNET.Vector3(body.corePosition.x, body.corePosition.y, body.corePosition.z);
                            item.boundsMax = origin;
                            UpdateBounds();
                        }

                        if (ImGui.Button($"Remove##{i}"))
                        {
                            spawnPoints.RemoveAt(i);
                            i--; // adjust index after removal
                        }

                        if (i < spawnPoints.Count - 1)
                        {
                            ImGui.Separator();
                        }
                    });
                }

                ImGui.End();
            }

            Try(DrawDisallowedInteractableSpawnBoxes);

            if (!_isMyUIOpen)
            {
                return;
            }
        }

        private void Update()
        {

        }

        private static readonly string SaveFolder = Path.Combine(Application.persistentDataPath, "InteractableSpawnFix");

        private static void SaveSpawnPoints(string fileName)
        {
            Directory.CreateDirectory(SaveFolder);

            var path = Path.Combine(SaveFolder, fileName);
            try
            {
                var json = JsonConvert.SerializeObject(_sceneNameToBlockedInteractableSpawnPoints, Formatting.Indented);
                File.WriteAllText(path, json);
                Log.Info($"Saved blocked spawn points to {path}");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private static void LoadSpawnPoints(string filePath, bool merge = false)
        {
            if (!File.Exists(filePath))
            {
                Log.Error($"File not found: {filePath}");
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var data = JsonConvert.DeserializeObject<Dictionary<string, List<BlockedInteractableSpawnPoint>>>(json);

                if (data == null)
                {
                    Log.Info("No data found in file.");
                    return;
                }

                if (merge)
                {
                    // Merge with existing dictionary
                    foreach (var kv in data)
                    {
                        var list = _sceneNameToBlockedInteractableSpawnPoints.GetValueOrAddDefault(kv.Key);
                        list.AddRange(kv.Value);
                    }
                }
                else
                {
                    _sceneNameToBlockedInteractableSpawnPoints = data;
                }

                Log.Info($"Loaded blocked spawn points from {filePath} (merge={merge})");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        class FileInfo
        {
            public string FilePath;
            public string FileName;
        }

        private static FileInfo[] cachedFiles = new FileInfo[0];
        private static FileInfo selectedFile = null;

        private static void DrawSerializationUI()
        {
            if (!ImGui.CollapsingHeader("Spawn Points Serialization"))
                return;

            ImGui.Text($"Folder: {SaveFolder}");
            if (ImGui.Button("Copy Folder Path To Clipboard"))
            {
                ImGui.SetClipboardText(SaveFolder);
            }

            // --- Save / Load current scene ---
            if (ImGui.Button("Save To Default.json"))
            {
                string filePath = $"Default.json";
                SaveSpawnPoints(filePath);
                selectedFile = new FileInfo() { FilePath = Path.Combine(SaveFolder, filePath), FileName = filePath };
            }

            // --- Refresh file list ---
            ImGui.SameLine();
            if (ImGui.Button("Refresh File List"))
            {
                if (Directory.Exists(SaveFolder))
                    cachedFiles = Directory.GetFiles(SaveFolder, "*.json").Select(s => new FileInfo() { FilePath = s, FileName = Path.GetFileName(s) }).ToArray();
                else
                    cachedFiles = new FileInfo[0];
            }

            ImGui.Separator();
            ImGui.Text("Available Files:");

            if (cachedFiles.Length == 0)
            {
                ImGui.TextColored(new ImGuiNET.Vector4(1, 0, 0, 1), "No files found or folder missing. Refresh List if needed.");
                return;
            }

            // --- Display file list ---
            foreach (var file in cachedFiles)
            {
                bool isCurrent = selectedFile != null && selectedFile == file;

                // Highlight currently loaded file
                if (isCurrent)
                    ImGui.PushStyleColor(ImGuiCol.Text, new ImGuiNET.Vector4(0, 1, 0, 1));

                ImGui.Text(file.FileName);
                ImGui.SameLine();

                if (ImGui.Button($"Load##{file.FileName}"))
                {
                    LoadSpawnPoints(file.FilePath, merge: false);
                    selectedFile = file;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Override existing blocked spawn points with the ones from {file.FileName}");

                ImGui.SameLine();
                if (ImGui.Button($"Merge##{file.FileName}"))
                {
                    LoadSpawnPoints(file.FilePath, merge: true);
                    selectedFile = file;
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Load or merge blocked spawn points from {file.FileName}");

                if (isCurrent)
                    ImGui.PopStyleColor();
            }
        }
    }
}
