using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using System.Text.RegularExpressions;
using System.Linq;
using Oxide.Game.Rust.Cui;
using System.Globalization;

namespace Oxide.Plugins
{
    [Info("DronePatrol", "RFC1920", "1.0.0")]
    [Description("Oxide Plugin")]
    class DronePatrol : RustPlugin
    {
        #region vars
        [PluginReference]
        private Plugin Economics, RoadFinder;

        public GameObject obj;
        public Dictionary<string, Road> roads = new Dictionary<string, Road>();
        public List<string> monNames = new List<string>();
        public SortedDictionary<string, Vector3> monPos  = new SortedDictionary<string, Vector3>();
        public SortedDictionary<string, Vector3> monSize = new SortedDictionary<string, Vector3>();
        public List<BasePlayer> guis = new List<BasePlayer>();

        public static string droneprefab = "assets/prefabs/deployable/drone/drone.deployed.prefab";
        ConfigData configData;

        public static DronePatrol Instance = null;
        private const string permDriver = "dronepatrol.use";
        private const string permAdmin  = "dronepatrol.admin";
        const string DRONEGUI = "npc.hud";

        public static Dictionary<string, BaseEntity> drones = new Dictionary<string, BaseEntity>();
        #endregion

        #region Message
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        private void Message(IPlayer player, string key, params object[] args) => player.Reply(Lang(key, player.Id, args));
        #endregion

        #region init
        void OnServerInitialized()
        {
            LoadConfigVariables();
            Instance = this;

            AddCovalenceCommand("d", "CmdSpawnDrone");
            AddCovalenceCommand("md", "CmdSpawnMonumentDrone");
            AddCovalenceCommand("pd", "CmdSpawnPatrolDrone");
            permission.RegisterPermission(permDriver, this);
            permission.RegisterPermission(permAdmin, this);

            var dnav = UnityEngine.Object.FindObjectsOfType<DroneNav>();
            foreach (var d in dnav)
            {
                d.drone.Kill();
                UnityEngine.Object.Destroy(d);
            }

            drones = new Dictionary<string, BaseEntity>();

            object x = RoadFinder.CallHook("GetRoads");
            var json = JsonConvert.SerializeObject(x);
            roads = JsonConvert.DeserializeObject<Dictionary<string, Road>>(json);
            SpawnRingDrone();
            SpawnRoadDrone();

            FindMonuments();
            SpawnMonumentDrone();
        }

        void SetDroneInCS(string drone)
        {
            if (!configData.Options.setDronesInAllCS) return;
            var stations = UnityEngine.Object.FindObjectsOfType<ComputerStation>();
            foreach(var station in stations)
            {
                if (station.controlBookmarks.ContainsKey(drone))
                {
                    station.controlBookmarks.Remove(drone);
                }
                if (drones.ContainsKey(drone))
                {
                    station.controlBookmarks.Add(drone, drones[drone].net.ID);
                }
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["drone"] = "Drone",
                ["heading"] = "Drone headed to {0}"
            }, this);
        }

        void Unload()
        {
            var dnav = UnityEngine.Object.FindObjectsOfType<DroneNav>();
            foreach (var d in dnav)
            {
                d.drone.Kill();
            }
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, DRONEGUI);
            }
        }

        void OnEntitySpawned(ComputerStation station)
        {
            if (!configData.Options.setDronesInAllCS) return;
            List<string> dronelist = new List<string>() { "MonumentDrone", "RingDrone", "RoadDrone" };

            foreach(var drone in dronelist)
            {
                if (station.controlBookmarks.ContainsKey(drone))
                {
                    station.controlBookmarks.Remove(drone);
                }
                if (drones.ContainsKey(drone))
                {
                    station.controlBookmarks.Add(drone, drones[drone].net.ID);
                }
            }
        }
        #endregion

        #region Main
        private void DoLog(string message, bool ismovement = false)
        {
            if (ismovement && !configData.Options.debugMovement) return;
            if (configData.Options.debug) Interface.Oxide.LogInfo(message);
        }

        void SpawnRoadDrone()
        {
            if (!configData.Options.spawnRoadDrone) return;
            if(!roads.ContainsKey(configData.Options.startRoad))
            {
                DoLog("No such road on this map :(");
                return;
            }

            var road = configData.Options.startRoad;
            Vector3 target = roads[road].points[0];
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            DoLog($"Drone object created...");
            var obj = drone.gameObject.AddComponent<DroneNav>();
            DoLog($"DroneNav object created...");
            drone.Spawn();

            DoLog($"Moving to start of {road}...");
            obj.SetType(DroneType.Road);
            obj.SetRoad(road);

            drones.Add("RoadDrone", drone);
            SetDroneInCS("RoadDrone");
        }
        void SpawnRingDrone()
        {
            if (!configData.Options.spawnRingRoadDrone) return;
            if(!roads.ContainsKey(configData.Options.startRingRoad))
            {
                DoLog("No such road on this map :(");
                return;
            }

            var road = configData.Options.startRingRoad;
            Vector3 target = roads[road].points[0];
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            DoLog($"Drone object created...");
            var obj = drone.gameObject.AddComponent<DroneNav>();
            DoLog($"DroneNav object created...");
            drone.Spawn();

            DoLog($"Moving to start of {road}...");
            obj.SetType(DroneType.Ring);
            obj.SetRoad(road, true);

            drones.Add("RingDrone", drone);
            SetDroneInCS("RingDrone");
        }
        void SpawnMonumentDrone()
        {
            if (!configData.Options.spawnMonumentPatrolDrone) return;

            Vector3 target = Vector3.zero;
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            DoLog($"Drone object created...");
            var obj = drone.gameObject.AddComponent<DroneNav>();
            DoLog($"DroneNav object created...");
            obj.SetType(DroneType.MonAll);
            drone.Spawn();

            drones.Add("MonumentDrone", drone);
            SetDroneInCS("MonumentDrone");
        }

        void CmdSpawnMonumentDrone(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;

            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            DoLog($"Drone object created...");
            var obj = drone.gameObject.AddComponent<DroneNav>();
            DoLog($"DroneNav object created...");
            obj.ownerid = player.userID;
            obj.type = DroneType.MonAll;
            drone.Spawn();
            Message(iplayer, "Spawned Monument drone");
        }

        void CmdSpawnPatrolDrone(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            DoLog($"Drone object created...");
            var obj = drone.gameObject.AddComponent<DroneNav>();
            DoLog($"DroneNav object created...");
            obj.ownerid = player.userID;
            drone.Spawn();
            string road = "Road 5"; // TESTING
            DoLog($"Moving to start of {road}...");
            obj.SetRoad(road);
        }

        //private void OnPlayerInput(BasePlayer player, InputState input)
        //{
        //    if (player == null || input == null) return;
        //    if(input.current.buttons > 1)
        //        Puts($"OnPlayerInput: {input.current.buttons}");
        //}

        void CmdSpawnDrone(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            string prefab = "assets/prefabs/deployable/drone/drone.deployed.prefab";

            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(prefab, target, player.transform.rotation);
            DoLog($"Drone object created...");
            drone.Spawn();
        }

        public class Road
        {
            public List<Vector3> points = new List<Vector3>();
            public float width;
            public float offset;
            public int topo;
        }

        object OnBookmarkAdd(ComputerStation computerStation, BasePlayer player, string bookmarkName)
        {
            DoLog($"Player {player.UserIDString} added bookmark {bookmarkName}");
            return null;
        }
        object OnBookmarkControl(ComputerStation station, BasePlayer player, string bookmarkName, IRemoteControllable remoteControllable)
        {
            if (player == null) return null;
            DoLog($"Player {player.UserIDString} controlling drone {remoteControllable.GetEnt().PrefabName}");
            var ent = remoteControllable.GetEnt();
            var drone = ent as Drone;
            if(drone != null)
            {
                var obj = drone.GetComponent<DroneNav>();
                if(obj != null)
                {
                    if (obj.currentRoad != null)
                    {
                        DroneGUI(player, obj.currentRoadName);
                    }
                    else
                    {
                        DroneGUI(player, obj.currentMonument);
                    }
                }
            }
            return null;
        }
        object OnBookmarkControlEnd(ComputerStation computerStation, BasePlayer player, BaseEntity controlledEntity)
        {
            CuiHelper.DestroyUi(player, DRONEGUI);
            guis.Remove(player);
            return null;
        }

        // Our Hooks
        private object OnDroneNavDirection(string newdirection)
        {
            List<BasePlayer> newgui = new List<BasePlayer>(guis);
            foreach (var pl in newgui)
            {
                DroneGUI(pl, newdirection);
            }
            newgui.Clear();
            return null;
        }

        private void OnDroneNavDestroyed(DroneNav drone)
        {
            switch(drone.type)
            {
                case DroneType.MonAll:
                    SpawnMonumentDrone();
                    break;
                case DroneType.Ring:
                    SpawnRingDrone();
                    break;
                case DroneType.Road:
                    SpawnRoadDrone();
                    break;
            }
        }

        void DroneGUI(BasePlayer player, string target = null)
        {
            guis.Remove(player);
            CuiHelper.DestroyUi(player, DRONEGUI);
            guis.Add(player);

            CuiElementContainer container = UI.Container(DRONEGUI, UI.Color("FFF5E1", 0.16f), "0.4 0.95", "0.6 0.99", false, "Overlay");
            string uicolor = "#ffff33";
            if(target == null)
            {
                target = Lang("drone");
                uicolor = "#dddddd";
            }
            var label = Lang("heading", null, target);
            UI.Label(ref container, DRONEGUI, UI.Color(uicolor, 1f), label, 12, "0 0", "1 1");

            if (monPos.ContainsKey(target))
            {
                string drawtext = target;
                var point = monPos[target];
                player.SendConsoleCommand("ddraw.text", 90, Color.green, point, $"<size=20>{target}</size>");
            }
            CuiHelper.AddUi(player, container);
        }
        #endregion

        #region config
        protected override void LoadDefaultConfig()
        {
            Puts("Creating new config file.");
            var config = new ConfigData
            {
                Options = new Options()
                {
                    spawnMonumentPatrolDrone = true,
                    startMonument = "Airfield",
                    spawnRingRoadDrone = false,
                    startRingRoad = "Road 0",
                    spawnRoadDrone = false,
                    minHeight = 40,
                    debug = false,
                    setDronesInAllCS = true
                },
                Version = Version
            };
            SaveConfig(config);
        }

        private void LoadConfigVariables()
        {
            configData = Config.ReadObject<ConfigData>();

            configData.Version = Version;
            SaveConfig(configData);
        }

        private void SaveConfig(ConfigData config)
        {
            Config.WriteObject(config, true);
        }

        public class ConfigData
        {
            public Options Options = new Options();
            public VersionNumber Version;
        }

        public class Options
        {
            public bool debug;
            public bool debugMovement;
            public float minHeight;
            public bool spawnMonumentPatrolDrone;
            public bool spawnRingRoadDrone;
            public bool spawnRoadDrone;
            public string startMonument;
            public string startRingRoad;
            public string startRoad;
            public bool setDronesInAllCS;
        }
        #endregion

		#region classes
        public enum DroneType
        {
            None = 0,
            User = 1,
            Road = 2,
            Ring = 4,
            MonSingle = 8,
            MonAll = 16
        }

        class DroneNav : MonoBehaviour
        {
            public DroneType type;

            public Drone drone;
            public Rigidbody rb;
            public RemoteControlEntity rc;
            public BasePlayer player;
            public ComputerStation station;
            string stationprefab = "assets/prefabs/deployable/computerstation/computerstation.deployed.prefab";
            public int buildingMask = LayerMask.GetMask("Construction", "Prevent Building", "Deployed", "World", "Terrain");

            public uint droneid;
            public ulong ownerid;
            public InputState input;

            public Road currentRoad;
            public string currentRoadName;
            public string currentMonument;
            public Vector3 current;
            public Quaternion rotation;
            public static Vector3 target = Vector3.zero;

            public int whichPoint;
            public int totalPoints;

            public static bool grounded = true;
            public static bool hover = true;
            public static bool started = false;
            public static bool ending = false;

            void Awake()
            {
                Instance.DoLog($"Awake()");
                drone = GetComponent<Drone>();
                //drone.CanControl();
                //rb = GetComponent<Rigidbody>();
                rc = GetComponent<RemoteControlEntity>();
            }

            void Start()
            {
                droneid = drone.net.ID;
            }

            public void SetType(DroneType type)
            {
                Instance.DoLog($"Set drone {rc.rcIdentifier} type to {type.ToString()}");
                this.type = type;
            }

            public void SetRoad(string road, bool isring = false)
            {
                if (type == DroneType.None)
                {
                    if(isring)
                    {
                        SetType(DroneType.Ring);
                    }
                    else
                    {
                        SetType(DroneType.Road);
                    }
                }
                currentRoad = Instance.roads[road];
                currentRoadName = road;
                target = currentRoad.points[0];
                totalPoints = currentRoad.points.Count - 1;
            }

            void Update()
            {
                if (drone.IsDead())
                {
                    Interface.Oxide.CallHook("OnDroneNavDestroyed", this);
                    Destroy(this);
                }
                //if (grounded) return;
                switch (type)
                {
                    case DroneType.Ring:
                        drone.diesAtZeroHealth = false;
                        if (rc.rcIdentifier != "RingDrone") rc.UpdateIdentifier("RingDrone", true);
                        if (currentRoad == null) return;
                        if (target != Vector3.zero)
                        {
                            grounded = false;
                            MoveToRoadPoint(target, true);
                            target = currentRoad.points[0];
                        }
                        grounded = false;
                        break;
                    case DroneType.Road:
                        drone.diesAtZeroHealth = false;
                        if(rc.rcIdentifier != "RoadDrone") rc.UpdateIdentifier("RoadDrone", true);
                        if (currentRoad == null) return;
                        if (target != Vector3.zero)
                        {
                            grounded = false;
                            MoveToRoadPoint(target);
                            target = currentRoad.points[0];
                        }
                        grounded = false;
                        break;
                    case DroneType.MonAll:
                        drone.diesAtZeroHealth = false;
                        if(rc.rcIdentifier != "MonumentDrone") rc.UpdateIdentifier("MonumentDrone", true);
                        if (currentMonument == null)
                        {
                            if (grounded)
                            {
                                if (!string.IsNullOrEmpty(Instance.configData.Options.startMonument))
                                {
                                    currentMonument = Instance.configData.Options.startMonument;
                                }
                                else
                                {
                                    GetMonument();
                                }
                            }
                            else
                            {
                                GetMonument();
                            }
                        }
                        grounded = false;
                        MoveToMonument();
                        break;
                }
            }

            void MoveToMonument()
            {
                if (drone == null) return;

                current = drone.transform.position;
                target = new Vector3(Instance.monPos[currentMonument].x, GetHeight(Instance.monPos[currentMonument]), Instance.monPos[currentMonument].z);

                var direction = (target - current).normalized;
                var lookrotation = Quaternion.LookRotation(direction);

                if (Vector3.Distance(current, target) < 2)
                {
                    GetMonument();
                    target = Instance.monPos[currentMonument];
                    target.y = GetHeight(target);
                }
                drone.transform.rotation = lookrotation;
                DoMoveDrone(direction);
            }

            void MoveToRoadPoint(Vector3 point, bool isring = false)
            {
                if (drone == null) return;
                if (grounded) return;

                current = drone.transform.position;
                target = new Vector3(currentRoad.points[whichPoint].x, GetHeight(currentRoad.points[whichPoint]), currentRoad.points[whichPoint].z);
                //target = currentRoad.points[whichPoint];
                var direction = (target - current).normalized;
                var lookrotation = Quaternion.LookRotation(direction);

                if (Vector3.Distance(current, target) < 2)
                {
                    Instance.DoLog($"{rc.rcIdentifier} ARRIVED at point ({whichPoint}) {target.ToString()}", true);
                    if (ending)
                    {
                        //enabled = false;
                        whichPoint = 0;
                        target = currentRoad.points[0];
                        ending = false;
                        return;
                    }
                    if (isring)
                    {
                        Instance.DoLog($"{rc.rcIdentifier} incrementing to point {whichPoint.ToString()}", true);
                        whichPoint++;
                        if (whichPoint == totalPoints) whichPoint = 0;
                        target = currentRoad.points[whichPoint];
                        //drone.transform.rotation = lookrotation;
                    }
                    else
                    {
                        whichPoint = totalPoints;
                        target = currentRoad.points[totalPoints];
                        ending = true;
                        drone.transform.rotation = lookrotation;
                    }
                    return;
                }

                DoMoveDrone(direction);
            }

            void DoMoveDrone(Vector3 direction)
            {
                if (!drone.IsBeingControlled)
                {
                    return;
                }

                //drone.transform.rotation = Quaternion.LookRotation(target);
                Instance.DoLog($"{rc.rcIdentifier} trying to move from {current.ToString()} to {target.ToString()} via {direction.ToString()}", true);
                int x = 0;
                int z = 0;

                InputMessage message = new InputMessage() { buttons = 0 };

                bool toolow = TooLow(current);
                bool frontcrash = DangerFront(current);
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(current);

                if (current.y < target.y || toolow || frontcrash)
                {
                    // Move UP
                    Instance.DoLog($"{rc.rcIdentifier} Moving UP {current.y}", true);
                    message.buttons = 128;
                }
                else if (current.y > (terrainHeight + Instance.configData.Options.minHeight * 2) + 5 && !frontcrash)
                {
                    // Move Down
                    Instance.DoLog($"{rc.rcIdentifier} Moving DOWN {current.y}", true);
                    message.buttons = 64;
                }

                if (!toolow)
                {
//                    if (Vector3.Angle(drone.transform.forward, direction) > 40 && !DangerRight(current))
//                    {
//                        // Move RIGHT
//                        Instance.DoLog($"{rc.rcIdentifier} Moving RIGHT", true);
//                        message.buttons = 16;
//                        z = 1;
//                    }
//                    else if (Vector3.Angle(drone.transform.forward, direction) < -40 && !DangerLeft(current))
//                    {
//                        // Move LEFT
//                        Instance.DoLog($"{rc.rcIdentifier} Moving LEFT", true);
//                        message.buttons = 8;
//                        z = -1;
//                    }

                    if (!DangerRight(current) && frontcrash)
                    {
                        // Move RIGHT
                        Instance.DoLog($"{rc.rcIdentifier} Moving RIGHT to avoid frontal crash", true);
                        message.buttons = 16;
                        z = 1;
                    }
                    else if (!DangerLeft(current) && frontcrash)
                    {
                        // Move LEFT
                        Instance.DoLog($"{rc.rcIdentifier} Moving LEFT to avoid frontal crash", true);
                        message.buttons = 8;
                        z = -1;
                    }
                }
//                message.buttons += 2;
                if(!frontcrash && !toolow)
                {
                    // Move FORWARD
                    message.buttons += 2;
                    x = 1;
                }

                message.mouseDelta.x = x;
                message.mouseDelta.z = z;

                InputState input = new InputState() { current = message };

                //Instance.DoLog($"Moving from {current.ToString()} to {target.ToString()} via {direction.ToString()}");
                rc.UserInput(input, player);
            }

            float GetHeight(Vector3 tgt)
            {
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(tgt);
                float targetHeight = terrainHeight + Instance.configData.Options.minHeight;

                return targetHeight;
            }

            void GetMonument()
            {
                var cnt = Instance.monNames.Count;

                System.Random rand = new System.Random();
                currentMonument = Instance.monNames[rand.Next(cnt)];
                Instance.DoLog($"Set {rc.rcIdentifier} monument to {currentMonument}", true);
                target = Instance.monPos[currentMonument];
                Interface.Oxide.CallHook("OnDroneNavDirection", currentMonument);
            }

            bool DangerLeft(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Raycast(current, Vector3.left, out hitinfo, 4f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        Instance.DoLog($"{rc.rcIdentifier} CRASH LEFT!", true);
                        return true;
                    }
                }
                return false;
            }

            bool DangerRight(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Raycast(current, Vector3.right, out hitinfo, 4f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        Instance.DoLog($"{rc.rcIdentifier} CRASH RIGHT!", true);
                        return true;
                    }
                }
                return false;
            }

            bool DangerFront(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Raycast(current, Vector3.forward, out hitinfo, 20f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        Instance.DoLog($"{rc.rcIdentifier} FRONTAL CRASH!", true);
                        return true;
                    }
                }
                return false;
            }

            bool TooLow(Vector3 tgt)
            {
                RaycastHit hitinfo;
                int groundLayer = LayerMask.GetMask("Construction", "Terrain", "World");
                if(Physics.Raycast(current, Vector3.down, out hitinfo, 10f, groundLayer) || Physics.Raycast(current, Vector3.up, out hitinfo, 10f, groundLayer))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        Instance.DoLog($"{rc.rcIdentifier} TOO LOW!", true);
                        return true;
                    }
                }
                return false;
            }
        }

        public static class UI
        {
            public static CuiElementContainer Container(string panel, string color, string min, string max, bool useCursor = false, string parent = "Overlay")
            {
                CuiElementContainer container = new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = color },
                            RectTransform = {AnchorMin = min, AnchorMax = max},
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };
                return container;
            }
            public static void Panel(ref CuiElementContainer container, string panel, string color, string min, string max, bool cursor = false)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = color },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    CursorEnabled = cursor
                },
                panel);
            }
            public static void Label(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text = { Color = color, FontSize = size, Align = align, Text = text },
                    RectTransform = { AnchorMin = min, AnchorMax = max }
                },
                panel);

            }
            public static void Button(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = command, FadeIn = 0f },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    Text = { Text = text, FontSize = size, Align = align }
                },
                panel);
            }
            public static void Input(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Align = align,
                            CharsLimit = 30,
                            Color = color,
                            Command = command + text,
                            FontSize = size,
                            IsPassword = false,
                            Text = text
                        },
                        new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max },
                        new CuiNeedsCursorComponent()
                    }
                });
            }
            public static void Icon(ref CuiElementContainer container, string panel, string color, string imageurl, string min, string max)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Url = imageurl,
                            Sprite = "assets/content/textures/generic/fulltransparent.tga",
                            Color = color
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = min,
                            AnchorMax = max
                        }
                    }
                });
            }
            public static string Color(string hexColor, float alpha)
            {
                if(hexColor.StartsWith("#"))
                {
                    hexColor = hexColor.Substring(1);
                }
                int red = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int green = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int blue = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);
                return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
            }
        }
        #endregion

        void FindMonuments()
        {
            Vector3 extents = Vector3.zero;
            float realWidth = 0f;
            string name = null;

            foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (monument.name.Contains("power_sub")) continue;// || monument.name.Contains("cave")) continue;
                realWidth = 0f;
                name = null;

                if(monument.name == "OilrigAI")
                {
                    name = "Small Oilrig";
                }
                else if(monument.name == "OilrigAI2")
                {
                    name = "Large Oilrig";
                }
                else
                {
                    name = Regex.Match(monument.name, @"\w{6}\/(.+\/)(.+)\.(.+)").Groups[2].Value.Replace("_", " ").Replace(" 1", "").Titleize();
                }
                if(monPos.ContainsKey(name)) continue;

                extents = monument.Bounds.extents;

                if(realWidth > 0f)
                {
                    extents.z = realWidth;
                }

                if (extents.z < 1)
                {
                    extents.z = 100f;
                }
                monNames.Add(name);
                monPos.Add(name, monument.transform.position);
                monSize.Add(name, extents);
            }
            monPos.OrderBy(x => x.Key);
            monSize.OrderBy(x => x.Key);
        }

        string RandomString()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            List<char> charList = chars.ToList();

            string random = "";

            for (int i = 0; i <= UnityEngine.Random.Range(5, 10); i++)
                random = random + charList[UnityEngine.Random.Range(0, charList.Count - 1)];

            return random;
        }
    }
}
