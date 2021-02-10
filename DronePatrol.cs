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
    [Info("DronePatrol", "RFC1920", "1.0.5")]
    [Description("Oxide Plugin")]
    class DronePatrol : RustPlugin
    {
        #region vars
        [PluginReference]
        private Plugin Economics, RoadFinder, Friends, Clans, Chute;

        public GameObject obj;
        public Dictionary<string, Road> roads = new Dictionary<string, Road>();
        public List<string> monNames = new List<string>();
        public SortedDictionary<string, Vector3> monPos  = new SortedDictionary<string, Vector3>();
        public SortedDictionary<string, Vector3> monSize = new SortedDictionary<string, Vector3>();
        public List<BasePlayer> guis = new List<BasePlayer>();

        public static Timer checkTimer;
        public static string droneprefab = "assets/prefabs/deployable/drone/drone.deployed.prefab";
        ConfigData configData;

        public static DronePatrol Instance = null;
        private const string permDriver = "dronepatrol.use";
        private const string permAdmin  = "dronepatrol.admin";
        const string DRONEGUI = "npc.hud";

        public static Dictionary<string, BaseEntity> drones = new Dictionary<string, BaseEntity>();

        public class Road
        {
            public List<Vector3> points = new List<Vector3>();
            public float width;
            public float offset;
            public int topo;
        }
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

            AddCovalenceCommand("drone", "CmdSpawnDrone");
            AddCovalenceCommand("md", "CmdSpawnMonumentDrone");
            AddCovalenceCommand("pd", "CmdSpawnRoadDrone");
            permission.RegisterPermission(permDriver, this);
            permission.RegisterPermission(permAdmin, this);

            var dnav = UnityEngine.Object.FindObjectsOfType<DroneNav>();
            foreach (var d in dnav)
            {
                d.drone.Kill();
                UnityEngine.Object.Destroy(d);
            }

            drones = new Dictionary<string, BaseEntity>();

            object x = RoadFinder?.CallHook("GetRoads");
            if (x != null)
            {
                var json = JsonConvert.SerializeObject(x);
                roads = JsonConvert.DeserializeObject<Dictionary<string, Road>>(json);
                SpawnRingDrone();
                SpawnRoadDrone();
            }

            FindMonuments();
            SpawnMonumentDrone();
            CheckDrones(true);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["notauthorized"] = "You don't have permission to use this command.",
                ["drone"] = "Drone",
                ["helptext1"] = "To spawn a drone, type /d NAMEOFDRONE",
                ["heading"] = "Drone headed to {0}"
            }, this);
        }

        private void CheckDrones(bool startup = false)
        {
            if (startup)
            {
                var dnav = UnityEngine.Object.FindObjectsOfType<Drone>();
                var sdrones = configData.Drones.Keys;
                foreach (var d in dnav)
                {
                    var rc = d.GetComponent<RemoteControlEntity>();
                    if (rc != null && sdrones.Contains(rc.rcIdentifier))
                    {
                        d.Kill();
                    }
                    d.Kill();
                }
            }

            Dictionary<string, BaseEntity> tmpDrones = new Dictionary<string, BaseEntity>(drones);
            foreach(var d in tmpDrones)
            {
                var drone = d.Value;
                if(drone.IsDestroyed | drone.IsBroken())
                {
                    drone.Kill();
                    drones.Remove(d.Key);
                    if (d.Key == configData.Drones["monument"].name)
                    {
                        SpawnMonumentDrone();
                    }
                    else if (d.Key == configData.Drones["ring"].name)
                    {
                        SpawnRingDrone();
                    }
                    else if (d.Key == configData.Drones["road"].name)
                    {
                        SpawnRoadDrone();
                    }
                }
            }
            checkTimer = timer.Once(5, () => CheckDrones());
        }

//        void Loaded()
//        {
//            var bp = BasePlayer.FindObjectsOfType<BasePlayer>();
//            foreach (var d in bp)
//            {
//                d.Kill();
//            }
//        }

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

        void OnEntitySpawned(Drone drone)
        {
            if (!configData.Options.setPlayerDroneInCS) return;

            var stations = UnityEngine.Object.FindObjectsOfType<ComputerStation>();
            var rc = drone.GetComponent<RemoteControlEntity>();
            if (rc == null) return;

            foreach(var station in stations)
            {
                if (station.OwnerID != drone.OwnerID && !IsFriend(station.OwnerID, drone.OwnerID)) continue;

                if (station.controlBookmarks.ContainsKey(rc.rcIdentifier))
                {
                    station.controlBookmarks.Remove(rc.rcIdentifier);
                }
                station.controlBookmarks.Add(rc.rcIdentifier, drone.net.ID);
            }
        }

        void OnEntitySpawned(ComputerStation station)
        {
            if (!configData.Options.setServerDroneInAllCS) return;
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
        void CmdSpawnMonumentDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            var player = iplayer.Object as BasePlayer;

            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            var obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            obj.type = DroneType.MonAll;
            drone.Spawn();
            Message(iplayer, "Spawned Monument drone");
        }

        void CmdSpawnRoadDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            var player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            var obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            drone.Spawn();
            string road = "Road 5"; // TESTING
            DoLog($"Moving to start of {road}...");
            obj.SetRoad(road);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            //if(input.current.buttons > 1)
            //    Puts($"OnPlayerInput: {input.current.buttons}");

            if(input.current.buttons == configData.Options.ActivationCode && Chute != null)
            {
                if (guis.Contains(player))
                {
                    var station = player.GetMounted() as ComputerStation;
                    var drone = station.currentlyControllingEnt.Get(true).GetComponent<Drone>();
                    if(drone != null)
                    {
                        var newPos = new Vector3(drone.transform.position.x, drone.transform.position.y, drone.transform.position.z);
                        newPos.y += 10f;
                        station.StopControl(player);
                        station.DismountPlayer(player);
                        station.SendNetworkUpdate();

                        guis.Remove(player);
                        Teleport(player, newPos);
                        Chute?.CallHook("ExternalAddPlayerChute", player, null);
                        var dnav = drone.GetComponentInParent<DroneNav>() ?? null;
                        if (dnav != null)
                        {
                            UnityEngine.Object.Destroy(dnav);
                        }
                        if (drone.OwnerID == player.userID) drone.Kill();
                    }
                }
            }
        }

        public void Teleport(BasePlayer player, Vector3 position)
        {
            if (player.net?.connection != null) player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);

            player.SetParent(null, true, true);
            player.EnsureDismounted();
            player.Teleport(position);
            player.UpdateNetworkGroup();
            player.SendNetworkUpdateImmediate(false);

            if(player.net?.connection != null) player.ClientRPCPlayer(null, player, "StartLoading");
        }

        void CmdSpawnDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permDriver))
            {
                Message(iplayer, "notauthorized");
                return;
            }

            if(args.Length == 0)
            {
                Message(iplayer, "helptext1");
                return;
            }
            string droneName = Lang("drone");
            if (args[0] != null) droneName = args[0];

            var player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            drone.OwnerID = player.userID;
            var rc = drone.GetComponent<RemoteControlEntity>();
            if(rc != null) rc.UpdateIdentifier(droneName);
            drone.Spawn();
            drone.SendNetworkUpdateImmediate();
        }

        object OnBookmarkAdd(ComputerStation computerStation, BasePlayer player, string bookmarkName)
        {
            DoLog($"Player {player.UserIDString} added bookmark {bookmarkName}");
            return null;
        }
        object OnBookmarkControl(ComputerStation station, BasePlayer player, string bookmarkName, IRemoteControllable remoteControllable)
        {
            if (player == null) return null;
            var ent = remoteControllable.GetEnt();
            var drone = ent as Drone;
            if(drone != null)
            {
                DoLog($"Player {player.UserIDString} controlling drone {remoteControllable.GetEnt().ShortPrefabName}");
                var obj = drone.GetComponent<DroneNav>();
                if (obj != null)
                {
                    if (obj.currentRoad != null)
                    {
                        DroneGUI(player, obj.currentRoadName);
                    }
                    else if (obj.currentMonument != null)
                    {
                        DroneGUI(player, obj.currentMonument);
                    }
                }
                else
                {
                    var rc = drone.GetComponent<RemoteControlEntity>();
                    if (rc != null)
                    {
                        DroneGUI(player, $"{player.displayName}'s {Lang("drone")}, {rc.rcIdentifier}.", true);
                    }
                    else
                    {
                        DroneGUI(player, player.displayName + "'s " + Lang("drone"), true);
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

        void DroneGUI(BasePlayer player, string target = null, bool pdrone = false)
        {
            guis.Remove(player);
            CuiHelper.DestroyUi(player, DRONEGUI);
            guis.Add(player);

            CuiElementContainer container = UI.Container(DRONEGUI, UI.Color("FFF5E1", 0.16f), "0.4 0.95", "0.6 0.99", false, "Overlay");
            string uicolor = "#ffff33";
            string label = "";
            if(target == null)
            {
                target = Lang("drone");
                uicolor = "#dddddd";
            }
            if (pdrone)
            {
                label = target;
            }
            else
            {
                label = Lang("heading", null, target);
            }
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

        #region our_hooks
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
        #endregion

        #region config
        protected override void LoadDefaultConfig()
        {
            Puts("Creating new config file.");
            var config = new ConfigData
            {
                Options = new Options()
                {
                    minHeight = 40,
                    ActivationCode = 2112,
                    debug = false,
                    setServerDroneInAllCS = true,
                    setPlayerDroneInCS = true,
                    playerDronesImmortal = false,
                    useClans = false,
                    useFriends = false,
                    useTeams = false
                },
                Drones = new Dictionary<string, DroneInfo>(),
                Version = Version
            };
            config.Drones.Add("monument", new DroneInfo()
            {
                name = "MonumentDrone",
                start = "Airfield",
                spawn = true
            });
            config.Drones.Add("ring", new DroneInfo()
            {
                name = "RingDrone",
                start = "Road 0",
                spawn = true
            });
            config.Drones.Add("road", new DroneInfo()
            {
                name = "RoadDrone",
                start = "Road 1",
                spawn = false
            });

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
            public Dictionary<string, DroneInfo> Drones;
            public VersionNumber Version;
        }
        public class DroneInfo
        {
            public string name;
            public bool spawn;
            public string start;
        }
        public class Options
        {
            public bool debug;
            public bool debugMovement;
            public float minHeight;
            public int ActivationCode;
            public bool setServerDroneInAllCS;
            public bool setPlayerDroneInCS;
            public bool playerDronesImmortal;
            public bool useFriends;
            public bool useClans;
            public bool useTeams;
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
            public RemoteControlEntity rc;
            public BasePlayer player;
            public int buildingMask = LayerMask.GetMask("Construction", "Prevent Building", "Deployed", "World", "Terrain", "Tree");

            public uint droneid;
            public ulong ownerid;
            public InputState input;

            public Road currentRoad;
            public string currentRoadName;
            public string currentMonument;
            public Vector3 current;
            public Quaternion rotation;
            public static Vector3 direction;
            public static Vector3 target = Vector3.zero;
            public static GameObject slop = new GameObject();

            public int whichPoint;
            public int totalPoints;

            public static bool grounded = true;
            public static bool started = false;
            public static bool ending;

            private struct DroneInputState
            {
                public Vector3 movement;
                public float throttle;
                public float yaw;
                public void Reset()
                {
                    movement = Vector3.zero;
                }
            }

            void Awake()
            {
                Instance.DoLog($"Awake()");
                drone = GetComponent<Drone>();
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

            public void SetRoad(string road, bool isringroad = false)
            {
                if (type == DroneType.None)
                {
                    if (isringroad)
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
                MinimizeRoadPoints();

                target = currentRoad.points[0];
                target.y += 10f;
                totalPoints = currentRoad.points.Count - 1;
            }

            void MinimizeRoadPoints()
            {
                // Cut down on the jerkiness - we don't need no stinkin points!
                var cnt = currentRoad.points.Count;
                Instance.DoLog($"{rc.rcIdentifier} road points {cnt.ToString()}");
                var newpts = new List<Vector3>();

                int skip = 1;
                if (cnt > 500) skip = 8;
                else if (cnt > 250) skip = 6;
                else if (cnt > 100) skip = 4;
                else if (cnt > 30) skip = 3;
                else return;

                int i = 0;
                foreach (var pts in currentRoad.points)
                {
                    if (i > skip)
                    {
                        newpts.Add(pts);
                        i = 0;
                    }
                    i++;
                }
                newpts.Add(currentRoad.points.Last());
                currentRoad.points = newpts;
                Instance.DoLog($"{rc.rcIdentifier} road points changed to {newpts.Count.ToString()}");
            }

            void Update()
            {
                if (drone.IsDead() || rc.IsDead() || !drone.isSpawned)
                {
                    return;
                }
                switch (type)
                {
                    case DroneType.Ring:
                        drone.diesAtZeroHealth = false;
                        if (rc.rcIdentifier == "NONE")
                        {
                            rc.UpdateIdentifier(Instance.configData.Drones["ring"].name, true);
                        }
                        if (currentRoad == null) return;
                        MoveToRoadPoint();
                        grounded = false;
                        break;
                    case DroneType.Road:
                        drone.diesAtZeroHealth = false;
                        if (rc.rcIdentifier == "NONE")
                        {
                            rc.UpdateIdentifier(Instance.configData.Drones["road"].name, true);
                        }
                        if (currentRoad == null) return;
                        MoveToRoadPoint();
                        grounded = false;
                        break;
                    case DroneType.MonAll:
                        drone.diesAtZeroHealth = false;
                        if (rc.rcIdentifier == "NONE")
                        {
                            rc.UpdateIdentifier(Instance.configData.Drones["monument"].name, true);
                        }
                        if (string.IsNullOrEmpty(currentMonument))
                        {
                            currentMonument = Instance.configData.Drones["monument"].start;
                        }
                        if (string.IsNullOrEmpty(currentMonument))
                        {
                            if (grounded)
                            {
                                GetMonument();
                            }
                        }
                        grounded = false;
                        MoveToMonument();
                        break;
                }
            }

            void MoveToRoadPoint()
            {
                if (drone == null) return;
                if (grounded) return;

                current = drone.transform.position;
                target.x = currentRoad.points[whichPoint].x;
                target.y = GetHeight(currentRoad.points[whichPoint]);
                target.z = currentRoad.points[whichPoint].z;

                direction = (target - current).normalized;
                Quaternion lookrotation = Quaternion.LookRotation(direction);

                if (Vector3.Distance(current, target) < 2)
                {
                    Instance.DoLog($"{rc.rcIdentifier} arrived at target point {whichPoint.ToString()}");
                    whichPoint++;
                    if (whichPoint > totalPoints) whichPoint = 0;

                    if (type == DroneType.Ring)
                    {
                        target = currentRoad.points[whichPoint];
                    }
                    else if (ending)
                    {
                        target = currentRoad.points[0];
                        ending = false;
                    }
                    else
                    {
                        whichPoint = totalPoints;
                        target = currentRoad.points[whichPoint];
                        ending = true;
                    }
                    Instance.DoLog($"{rc.rcIdentifier} changed target point to {whichPoint.ToString()}");
                }

                //Interface.Oxide.CallHook("OnDroneNavDirection", $"{whichPoint.ToString()}");
                drone.transform.LookAt(target);
                DoMoveDrone(direction);
            }

            void MoveToMonument()
            {
                if (drone == null) return;
                if (grounded) return;

                current = drone.transform.position;
                target = new Vector3(Instance.monPos[currentMonument].x, GetHeight(Instance.monPos[currentMonument]), Instance.monPos[currentMonument].z);

                direction = (target - current).normalized;
                var lookrotation = Quaternion.LookRotation(direction);

                if (Vector3.Distance(current, target) < 2)
                {
                    GetMonument();
                    target = Instance.monPos[currentMonument];
                    target.y = GetHeight(target);
                }
                drone.transform.LookAt(target);
                DoMoveDrone(direction);
            }

            void DoMoveDrone(Vector3 direction)
            {
                TakeControl();

                int x = 0;
                int z = 0;

                InputMessage message = new InputMessage() { buttons = 0 };

                bool toolow = TooLow(current);
                bool frontcrash = DangerFront(current);
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(current);

                // Flip if necessary
                if(drone.transform.up.y < 0.8f)
                {
                    Instance.DoLog($"{rc.rcIdentifier} was tipping over", true);
                    drone.transform.rotation = new Quaternion(0, 1, 0, 0);
                }
                if (current.y < target.y || (current.y - terrainHeight < 1.5f) || toolow || frontcrash)
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
                target.y = Instance.configData.Options.minHeight;
                Interface.Oxide.CallHook("OnDroneNavDirection", currentMonument);
            }

            void TakeControl()
            {
                if (!drone.IsBeingControlled)
                {
                    if (player == null)
                    {
                        player = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", Vector3.zero, new Quaternion()).ToPlayer();
                        player.Spawn();
                        player.displayName = rc.rcIdentifier + " Pilot";
                    }
                    rc.InitializeControl(player);
                }
            }

            #region crash_avoidance
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
            #endregion
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

        #region helpers
        public void SpawnRoadDrone()
        {
            if (!configData.Drones["road"].spawn) return;
            drones.Remove(configData.Drones["road"].name);
            if(!roads.ContainsKey(configData.Drones["road"].start))
            {
                DoLog("No such road on this map :(");
                return;
            }

            var road = configData.Drones["road"].start;
            Vector3 target = roads[road].points[0];
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            var obj = drone.gameObject.AddComponent<DroneNav>();
            drone.Spawn();

            DoLog($"Moving to start of {road}...");
            obj.SetType(DroneType.Road);
            obj.rc.UpdateIdentifier(configData.Drones["road"].name);
            obj.SetRoad(road);

            drones.Add(configData.Drones["road"].name, drone);
            SetDroneInCS(configData.Drones["road"].name);
        }
        public void SpawnRingDrone()
        {
            if (!configData.Drones["ring"].spawn) return;
            drones.Remove(configData.Drones["ring"].name);
            if(!roads.ContainsKey(configData.Drones["ring"].start))
            {
                DoLog("No such road on this map :(");
                return;
            }

            var road = configData.Drones["ring"].start;
            Vector3 target = roads[road].points[0];
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            var obj = drone.gameObject.AddComponent<DroneNav>();
            obj.rc.UpdateIdentifier(configData.Drones["ring"].name);
            drone.Spawn();

            DoLog($"Moving to start of {road}...");
            obj.SetType(DroneType.Ring);
            obj.SetRoad(road, true);

            drones.Add(configData.Drones["ring"].name, drone);
            SetDroneInCS(configData.Drones["ring"].name);
        }
        public void SpawnMonumentDrone()
        {
            if (!configData.Drones["monument"].spawn) return;
            drones.Remove(configData.Drones["monument"].name);

            Vector3 target = Vector3.zero;
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            var obj = drone.gameObject.AddComponent<DroneNav>();
            obj.rc.UpdateIdentifier(configData.Drones["ring"].name);
            obj.SetType(DroneType.MonAll);
            drone.Spawn();

            drones.Add(configData.Drones["monument"].name, drone);
            SetDroneInCS(configData.Drones["monument"].name);
        }

        void SetDroneInCS(string drone)
        {
            if (!configData.Options.setServerDroneInAllCS) return;
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

        private void DoLog(string message, bool ismovement = false)
        {
            if (ismovement && !configData.Options.debugMovement) return;
            if (configData.Options.debug) Interface.Oxide.LogInfo(message);
        }

        private bool IsFriend(ulong playerid, ulong ownerid)
        {
            if (configData.Options.useFriends && Friends != null)
            {
                var fr = Friends?.CallHook("AreFriends", playerid, ownerid);
                if (fr != null && (bool)fr)
                {
                    return true;
                }
            }
            if (configData.Options.useClans && Clans != null)
            {
                string playerclan = (string)Clans?.CallHook("GetClanOf", playerid);
                string ownerclan = (string)Clans?.CallHook("GetClanOf", ownerid);
                if (playerclan != null && ownerclan != null)
                {
                    if (playerclan == ownerclan)
                    {
                        return true;
                    }
                }
            }
            if (configData.Options.useTeams)
            {
                BasePlayer player = BasePlayer.FindByID(playerid);
                if (player != null)
                {
                    if (player.currentTeam != 0)
                    {
                        RelationshipManager.PlayerTeam playerTeam = RelationshipManager.Instance.FindTeam(player.currentTeam);
                        if (playerTeam != null)
                        {
                            if (playerTeam.members.Contains(ownerid))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }
        #endregion
    }
}
