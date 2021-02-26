#region License (GPL v3)
/*
    Loot Protection - Prevent access to player containers
    Copyright (c) 2020 RFC1920 <desolationoutpostpve@gmail.com>

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/
#endregion
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
using System;
using System.Diagnostics;

namespace Oxide.Plugins
{
    [Info("DronePatrol", "RFC1920", "1.0.12")]
    [Description("Create server drones that fly and roam, and allow users to spawn a drone of their own.")]
    class DronePatrol : RustPlugin
    {
        #region vars
        [PluginReference]
        private Plugin Economics, RoadFinder, Friends, Clans, Chute, GridAPI;

        public GameObject obj;
        public Dictionary<string, Road> roads = new Dictionary<string, Road>();
        public List<string> monNames = new List<string>();
        public SortedDictionary<string, Vector3> monPos  = new SortedDictionary<string, Vector3>();
        public SortedDictionary<string, Vector3> monSize = new SortedDictionary<string, Vector3>();
        private Dictionary<ulong, DroneNav> pguis = new Dictionary<ulong, DroneNav>();

        public static Timer checkTimer;
        public static string droneprefab = "assets/prefabs/deployable/drone/drone.deployed.prefab";
        ConfigData configData;

        public static DronePatrol Instance = null;
        private const string permDriver = "dronepatrol.use";
        private const string permAdmin  = "dronepatrol.admin";
        const string DRONEGUI = "npc.hud";

        public static Dictionary<string, BaseEntity> drones = new Dictionary<string, BaseEntity>();
        public static bool initdone = false;

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
            AddCovalenceCommand("rd", "CmdSpawnRoadDrone");
            AddCovalenceCommand("fd", "CmdSpawnSpyDrone");
            AddCovalenceCommand("ringd", "CmdSpawnRingDrone");
            permission.RegisterPermission(permDriver, this);
            permission.RegisterPermission(permAdmin, this);

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
            initdone = true;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["notauthorized"] = "You don't have permission to use this command.",
                ["mdstatus"] = "{0} @ {1}\n  Current target: {2} {3} (size {4}m)",
                ["rdstatus"] = "{0} @ {1}\n  Current target: {2} {3}",
                ["ringstatus"] = "{0} @ {1}",
                ["drone"] = "Drone",
                ["nosuchplayer"] = "Could not find player named {0}",
                ["spydrone"] = "Drone {0} sent to spy on {1}",
                ["helptext"] = "To spawn a drone, type /drone NAMEOFDRONE",
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
                        if (!d.IsDestroyed) d.Kill();
                        if (!rc.IsDestroyed) rc.Kill();
                        RemoveDroneFromCS(rc.rcIdentifier);
                    }
                }
            }

            // Do some cleanup.  You're welcome.
            // Missing network group
            BaseEntity.saveList.RemoveWhere(p => !p);
            BaseEntity.saveList.RemoveWhere(p => p == null);
            // Sleeping automaton controllers
            foreach(var pl in BasePlayer.sleepingPlayerList)
            {
                foreach(var di in configData.Drones)
                {
                    if (pl.displayName.Contains(di.Value.name) || pl.displayName.Contains("NONE Pilot"))
                    {
                        if(!pl.IsDestroyed) pl.Kill();
                    }
                }
            }
            // NONE Drones
            var ndrones = UnityEngine.Object.FindObjectsOfType<Drone>();
            foreach(var drone in ndrones)
            {
                RemoteControlEntity rc = drone.GetComponent<RemoteControlEntity>();
                if(rc != null)
                {
                    if (rc.rcIdentifier == "NONE")
                    {
                        if (!drone.IsDestroyed) drone.Kill();
                    }
                }
            }

            Dictionary<string, BaseEntity> tmpDrones = new Dictionary<string, BaseEntity>(drones);
            foreach(var d in tmpDrones)
            {
                var drone = d.Value;
                (drone as BaseCombatEntity).diesAtZeroHealth = true;

                if (drone.IsDestroyed | drone.IsBroken())
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
            checkTimer = timer.Once(30, () => CheckDrones());
        }

        void Unload()
        {
            var dnav = UnityEngine.Object.FindObjectsOfType<DroneNav>();
            foreach (var d in dnav)
            {
                d.drone.Kill();
                UnityEngine.GameObject.Destroy(d);
            }
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, DRONEGUI);
            }
        }

        private object CanPickupEntity(BasePlayer player, BaseCombatEntity ent)
        {
            if (player == null || ent == null) return null;

            var dnav = ent.GetComponentInParent<DroneNav>() ?? null;
            if(dnav != null)
            {
                if(dnav.ownerid == 0 && !player.IsAdmin)
                {
                    return true;
                }
                if(dnav.ownerid != player.userID && !IsFriend(player.userID, dnav.ownerid))
                {
                    return true;
                }
            }
            return null;
        }

        void OnEntitySpawned(Drone drone)
        {
            if (!initdone) return;
            if (!configData.Options.setPlayerDroneInCS) return;
            if (drone == null) return;

            var stations = UnityEngine.Object.FindObjectsOfType<ComputerStation>();
            if (stations.Count() == 0) return;
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
            if (!initdone) return;
            if (!configData.Options.setServerDroneInAllCS) return;
            if (station == null) return;

            List<string> dronelist = new List<string>() { "MonumentDrone", "RingDrone", "RoadDrone" };
            foreach(var drone in dronelist)
            {
                if (drone == null) return;
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
        void CmdSpawnDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permDriver))
            {
                Message(iplayer, "notauthorized");
                return;
            }

            if(args.Length == 0)
            {
                Message(iplayer, "helptext");
                return;
            }
            if (args.Length == 2)
            {
                if (args[1] == "kill")
                {
                    var dnav = UnityEngine.Object.FindObjectsOfType<Drone>();
                    foreach (var d in dnav)
                    {
                        var rcd = d.GetComponent<RemoteControlEntity>();
                        if (rcd != null && rcd.rcIdentifier == args[0])
                        {
                            Puts($"Killing {rcd.rcIdentifier}");
                            RemoveDroneFromCS(rcd.rcIdentifier);
                            d.Kill();
                            rcd.Kill();
                        }
                    }
                }
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
            if (rc != null)
            {
                rc.UpdateIdentifier(droneName);
            }
            drone.Spawn();
            drone.SendNetworkUpdateImmediate();
        }

        void CmdSpawnSpyDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            string debug = string.Join(",", args); Puts($"{debug}");
            if (args.Length > 0)
            {
                if(args.Length > 1)
                {
                    if (args[1] == "kill")
                    {
                        var dnav = UnityEngine.Object.FindObjectsOfType<Drone>();
                        var sdrones = configData.Drones.Keys;
                        foreach (var d in dnav)
                        {
                            var rcd = d.GetComponent<RemoteControlEntity>();
                            if (rcd != null && rcd.rcIdentifier == args[0])
                            {
                                d.Kill();
                            }
                            d.Kill();
                        }
                    }
                    return;
                }
                else if(args[0] == "list")
                {
                    var dnav = UnityEngine.Object.FindObjectsOfType<Drone>();
                    var sdrones = configData.Drones.Keys;
                    foreach (var d in dnav)
                    {
                        var rcd = d.GetComponent<RemoteControlEntity>();
                        if (rcd != null && rcd.rcIdentifier.Contains("SPY"))
                        {
                            Message(iplayer, rcd.rcIdentifier);
                        }
                    }
                    return;
                }

                BasePlayer pl = FindPlayerByName(args[0]);
                if (pl == null)
                {
                    Message(iplayer, "nosuchplayer", args[0]);
                    return;
                }

                Vector3 target = pl.transform.position;
                target.y = TerrainMeta.HeightMap.GetHeight(pl.transform.position) + configData.Options.minHeight;

                var drone = GameManager.server.CreateEntity(droneprefab, target, pl.transform.rotation);
                var obj = drone.gameObject.AddComponent<DroneNav>();

                var player = iplayer.Object as BasePlayer;
                obj.ownerid = player.userID;
                obj.SetType(DroneType.Spy);
                obj.SetPlayerTarget(pl);

                var rc = drone.GetComponent<RemoteControlEntity>();
                var plName = pl.displayName != null ? pl.displayName : pl.UserIDString;
                var droneName = $"SPY{plName}";

                if (rc != null)
                {
                    rc.UpdateIdentifier($"{droneName}");
                }

                drone.Spawn();
                drone.SendNetworkUpdateImmediate();
                SetDroneInCS(droneName);
                Message(iplayer, "spydrone", droneName, plName);
            }
        }

        void CmdSpawnMonumentDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            if (args.Length > 0)
            {
                if(args[0] == "status")
                {
                    var nav = drones[configData.Drones["monument"].name].GetComponent<DroneNav>();
                    if(nav != null)
                    {
                        string curr = null;
                        string tgt = null;
                        if (GridAPI != null)
                        {
                            var gc = (string[])GridAPI.CallHook("GetGrid", nav.current);
                            curr = nav.current.ToString() + "(" + string.Join("", gc) + ")";
                            var gt = (string[])GridAPI.CallHook("GetGrid", nav.target);
                            tgt = nav.target.ToString() + "(" + string.Join("", gt) + ")";
                        }
                        else
                        {
                            curr = nav.current.ToString();
                            tgt = nav.target.ToString();
                        }
                        Message(iplayer, "mdstatus", nav.rc.rcIdentifier, curr, nav.currentMonument, tgt, nav.currentMonSize);
                    }
                    return;
                }
                else if(args[0] == "list")
                {
                    foreach(var key in monPos.Keys)
                    {
                        Message(iplayer, key);
                    }
                    return;
                }
                var nextMon = string.Join(" ", args);
                if(monPos.ContainsKey(nextMon))
                {
                    var nav = drones[configData.Drones["monument"].name].GetComponent<DroneNav>();
                    if(nav != null)
                    {
                        nav.currentMonument = nextMon;
                        OnDroneNavChange(nav, nextMon);
                    }
                }
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
            if (args.Length > 0)
            {
                if (args[0] == "status")
                {
                    var nav = drones[configData.Drones["road"].name].GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        string curr = null;
                        string tgt = null;
                        if (GridAPI != null)
                        {
                            var gc = (string[])GridAPI.CallHook("GetGrid", nav.current);
                            curr = nav.current.ToString() + "(" + string.Join("", gc) + ")";
                            var gt = (string[])GridAPI.CallHook("GetGrid", nav.target);
                            tgt = nav.target.ToString() + "(" + string.Join("", gt) + ")";
                        }
                        else
                        {
                            curr = nav.current.ToString();
                            tgt = nav.target.ToString();
                        }
                        Message(iplayer, "rdstatus", nav.rc.rcIdentifier, curr, nav.currentRoadName, tgt);
                    }
                    return;
                }
                else if (args[0] == "list")
                {
                    foreach (var key in roads.Keys)
                    {
                        Message(iplayer, key);
                    }
                    return;
                }
                if(roads.ContainsKey(args[0]))
                {
                    var nav = drones["road"].GetComponent<DroneNav>();
                    if(nav != null)
                    {
                        nav.currentRoad = roads[args[0]];
                        OnDroneNavChange(nav, Instance.configData.Drones["road"].name);
                    }
                }
                return;
            }
            var player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            var obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            drone.Spawn();
            string road = configData.Drones["road"].start;
            DoLog($"CmdSpawnRoadDrone: Moving to start of {road}...");
            obj.SetRoad(road);
        }

        void CmdSpawnRingDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            if (args.Length > 0)
            {
                if (args[0] == "status")
                {
                    var nav = drones[configData.Drones["ring"].name].GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        string curr = null;
                        if (GridAPI != null)
                        {
                            var gc = (string[])GridAPI.CallHook("GetGrid", nav.current);
                            curr = nav.current.ToString() + "(" + string.Join("", gc) + ")";
                        }
                        else
                        {
                            curr = nav.current.ToString();
                        }
                        Message(iplayer, "ringstatus", nav.rc.rcIdentifier, curr);
                    }
                }
                return;
            }
            var player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            var obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            drone.Spawn();
            string road = configData.Drones["ring"].start;
            DoLog($"CmdSpawnRingDrone: Moving to start of {road}...");
            obj.SetRoad(road);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            //if(input.current.buttons > 1)
            //    Puts($"OnPlayerInput: {input.current.buttons}");

            if(input.current.buttons == configData.Options.ActivationCode && Chute != null)
            {
                if (pguis.ContainsKey(player.userID))
                {
                    var station = player.GetMounted() as ComputerStation;
                    var drone = station.currentlyControllingEnt.Get(true).GetComponent<Drone>();
                    if(drone != null)
                    {
                        var newPos = new Vector3(drone.transform.position.x, drone.transform.position.y + 10f, drone.transform.position.z);
                        station.StopControl(player);
                        station.DismountPlayer(player, true);
                        station.SendNetworkUpdateImmediate();

                        pguis.Remove(player.userID);
                        Teleport(player, newPos);
                        Chute?.CallHook("ExternalAddPlayerChute", player, null);
                        if (drone.OwnerID == player.userID)
                        {
                            DoLog($"Killing player drone.");
                            drone.Kill();
                        }
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
                DoLog($"Player {player.UserIDString} now controlling drone {remoteControllable.GetEnt().ShortPrefabName}");
                var obj = drone.GetComponent<DroneNav>();
                if (obj != null)
                {
                    if (obj.currentRoad != null)
                    {
                        DroneGUI(player, obj, Lang("heading", obj.currentRoadName));
                    }
                    else if (obj.currentMonument != null)
                    {
                        DroneGUI(player, obj, Lang("heading", obj.currentMonument, obj.currentMonument));
                    }
                }
                else
                {
                    var rc = drone.GetComponent<RemoteControlEntity>();
                    if (rc != null)
                    {
                        DroneGUI(player, null, $"{player.displayName}'s {Lang("drone")}, {rc.rcIdentifier}.");
                    }
                    else
                    {
                        DroneGUI(player, null, player.displayName + "'s " + Lang("drone"));
                    }
                }
            }
            return null;
        }
        object OnBookmarkControlEnd(ComputerStation computerStation, BasePlayer player, BaseEntity controlledEntity)
        {
            CuiHelper.DestroyUi(player, DRONEGUI);
            pguis.Remove(player.userID);
            return null;
        }

        void DroneGUI(BasePlayer player, DroneNav drone, string target = null, string monName = "UNKNOWN")
        {
            if (player == null) return;

            pguis.Remove(player.userID);
            CuiHelper.DestroyUi(player, DRONEGUI);
            if(drone != null) pguis.Add(player.userID, drone);

            CuiElementContainer container = UI.Container(DRONEGUI, UI.Color("FFF5E1", 0.16f), "0.4 0.95", "0.6 0.99", false, "Overlay");
            string uicolor = "#ffff33";
            string label = "";
            if(target == null)
            {
                target = Lang("drone");
                uicolor = "#dddddd";
            }
            label = target;
            UI.Label(ref container, DRONEGUI, UI.Color(uicolor, 1f), label, 12, "0 0", "1 1");

            if (monPos.ContainsKey(monName))
            {
                string drawtext = monName;
                var point = monPos[monName];
                player.SendConsoleCommand("ddraw.text", 90, Color.green, point, $"<size=20>{monName}</size>");
            }
            CuiHelper.AddUi(player, container);
        }
        #endregion

        #region our_hooks
        private object OnDroneNavChange(DroneNav drone, string newdirection)
        {
            var tmpgui = new Dictionary<ulong, DroneNav>(pguis);
            foreach (KeyValuePair<ulong, DroneNav> pgui in tmpgui)
            {
                if (pgui.Value == drone)
                {
                    var pl = BasePlayer.FindByID(pgui.Key);
                    DroneGUI(pl, drone, Lang("heading", null, newdirection), newdirection);
                }
            }
            return null;
        }
        private object OnDroneNavArrived(BasePlayer player, DroneNav drone, string newdirection)
        {
            var tmpgui = new Dictionary<ulong, DroneNav>(pguis);
            foreach (KeyValuePair<ulong, DroneNav> pgui in tmpgui)
            {
                if (pgui.Key == player.userID && pgui.Value == drone)
                {
                    DroneGUI(player, drone, Lang("arrived", null, newdirection), newdirection);
                }
            }
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
            MonAll = 16,
            Spy = 32
        }

        class DroneNav : MonoBehaviour
        {
            public DroneType type;

            public Drone drone;
            public RemoteControlEntity rc;
            public BasePlayer player;
            public BasePlayer targetPlayer;
            public int buildingMask = LayerMask.GetMask("Construction", "Prevent Building", "Deployed", "World", "Terrain", "Tree", "Invisible", "Default");
            public int groundMask = LayerMask.GetMask("Construction", "Terrain", "World", "Water");
            public int terrainMask = LayerMask.GetMask("Terrain");

            public uint droneid;
            public ulong ownerid;
            public InputState input;

            public Road currentRoad;
            public string currentRoadName;
            public string currentMonument;
            public float currentMonSize;
            public Vector3 current;
            public Quaternion rotation;
            public static Vector3 direction;
            public Vector3 target = Vector3.zero;
            public static Vector3 last = Vector3.zero;
            public static GameObject slop = new GameObject();

            public static Stopwatch stuckTimer;

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
                stuckTimer = new Stopwatch();
                stuckTimer.Stop();
                enabled = false;
            }

            void Start()
            {
                droneid = drone.net.ID;
            }

            public void SetPlayerTarget(BasePlayer pl)
            {
                if (type != DroneType.Spy) return;
                targetPlayer = pl;
            }

            public bool SetName(string name)
            {
                Instance.DoLog($"Set drone {rc.rcIdentifier} to {name}");
                rc.UpdateIdentifier(name, true);
                if (rc.rcIdentifier == name) return true;

                return false;
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
                if (!enabled || drone.IsDead() || rc.IsDead() || !drone.isSpawned)
                {
                    return;
                }

                switch (type)
                {
                    case DroneType.Ring:
                        if (currentRoad == null) return;
                        MoveToRoadPoint();
                        grounded = false;
                        break;
                    case DroneType.Road:
                        if (currentRoad == null) return;
                        MoveToRoadPoint();
                        grounded = false;
                        break;
                    case DroneType.MonAll:
                        if (string.IsNullOrEmpty(currentMonument) || !Instance.monPos.ContainsKey(currentMonument))
                        {
                            GetMonument();
                        }
                        else if (string.IsNullOrEmpty(currentMonument))
                        {
                            currentMonument = Instance.configData.Drones["monument"].start;
                            currentMonSize = Instance.monSize[Instance.configData.Drones["monument"].start].z;
                        }
                        grounded = false;
                        MoveToMonument();
                        break;
                    case DroneType.Spy:
                        if(targetPlayer != null)
                        {
                            FollowPlayer();
                        }
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

                var monsize = Mathf.Max(25, currentMonSize);
                if (Vector3.Distance(current, target) < monsize)
                {
                    Instance.DoLog($"Within {monsize.ToString()}m of {currentMonument.ToString()}.  Switching...", true);
                    GetMonument();
                    target = Instance.monPos[currentMonument];
                    target.y = GetHeight(target);
                }
                drone.transform.LookAt(target);
                DoMoveDrone(direction);
            }

            void FollowPlayer()
            {
                if (drone == null) return;
                if (targetPlayer == null) return;

                current = drone.transform.position;
                target = targetPlayer.transform.position;
                direction = target - current.normalized;

                drone.transform.LookAt(target);
                DoMoveDrone(direction);
            }

            void DoMoveDrone(Vector3 direction)
            {
                TakeControl();

                int x = 0;
                int z = 0;

                InputMessage message = new InputMessage() { buttons = 0 };

                bool toolow = TooLow(current) | BigRock(target);
                bool above = DangerAbove(current);
                bool frontcrash = DangerFront(current);
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(current);

                // Flip if necessary
                if(drone.transform.up.y < 0.7f)
                {
                    Instance.DoLog($"{rc.rcIdentifier} was tipping over", true);
                    //drone.transform.rotation = new Quaternion(0, 1, 0, 0);
                    drone.transform.rotation = Quaternion.identity;
                }
                if (above)
                {
                    // Move right to try to get around this crap
                    Instance.DoLog($"Moving down and right to try to avoid...", true);
                    message.buttons = 48;
                }
                if (current.y < target.y || (current.y - terrainHeight < 1.5f) || toolow || frontcrash)
                {
                    if (!above)
                    {
                        // Move UP
                        Instance.DoLog($"{rc.rcIdentifier} Moving UP {current.y}", true);
                        message.buttons = 128;
                    }
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

                last = drone.transform.position;
            }

            float GetHeight(Vector3 tgt)
            {
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(tgt);
                float targetHeight = terrainHeight + Instance.configData.Options.minHeight;

                RaycastHit hitinfo;
                if(Physics.Raycast(current, Vector3.down, out hitinfo, 100f, LayerMask.GetMask("Water")))
                {
                    if (TerrainMeta.WaterMap.GetHeight(hitinfo.point) < terrainHeight)
                    {
                        targetHeight = TerrainMeta.WaterMap.GetHeight(tgt) + Instance.configData.Options.minHeight;
                    }
                }

                return targetHeight;
            }

            void GetRoad()
            {
                // Pick a random road if road is null
                var cnt = Instance.roads.Count;

                System.Random rand = new System.Random();
                List<string> roadlist = new List<string>(Instance.roads.Keys);
                var croad = roadlist[rand.Next(cnt)];
                currentRoad = Instance.roads[croad];

                Instance.DoLog($"Set {rc.rcIdentifier} road to {currentRoad}", true);
                target = currentRoad.points[0];
                target.y = Instance.configData.Options.minHeight;
            }
            void GetMonument()
            {
                // Pick a random monument if currentMonument is null
                var cnt = Instance.monNames.Count;
                List<string> monlist = new List<string>(Instance.monSize.Keys);

                System.Random rand = new System.Random();
                var index = rand.Next(cnt);
                var cmon = monlist[index];
                currentMonument = Instance.monNames[index];
                currentMonSize = Instance.monSize[cmon].z;

                Instance.DoLog($"Set {rc.rcIdentifier} monument to {currentMonument} {currentMonSize}", true);
                target = Instance.monPos[currentMonument];
                target.y = Instance.configData.Options.minHeight;

                Instance.OnDroneNavChange(this, currentMonument);
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
                //if (Physics.Raycast(current - drone.transform.right, Vector3.left, out hitinfo, 4f, buildingMask))
                if (Physics.Raycast(current, drone.transform.TransformDirection(Vector3.left), out hitinfo, 4f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        if (hitinfo.distance < 2) return false;
                        string hit = null;
                        try
                        {
                            hit = $" with {hitinfo.GetEntity().ShortPrefabName}";
                        }
                        catch
                        {
                            return false;
                        }
                        var d = Math.Round(hitinfo.distance, 2).ToString();
                        Instance.DoLog($"{rc.rcIdentifier} CRASH LEFT{hit} distance {d}!", true);
                        return true;
                    }
                }
                return false;
            }

            bool DangerRight(Vector3 tgt)
            {
                RaycastHit hitinfo;
                //if (Physics.Raycast(current + drone.transform.right, Vector3.right, out hitinfo, 4f, buildingMask))
                if (Physics.Raycast(current, drone.transform.TransformDirection(Vector3.right), out hitinfo, 4f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        if (hitinfo.distance < 2) return false;
                        string hit = null;
                        try
                        {
                            hit = $" with {hitinfo.GetEntity().ShortPrefabName}";
                        }
                        catch
                        {
                            return false;
                        }
                        var d = Math.Round(hitinfo.distance, 2).ToString();
                        Instance.DoLog($"{rc.rcIdentifier} CRASH RIGHT{hit} distance {d}!", true);
                        return true;
                    }
                }
                return false;
            }

            bool DangerAbove(Vector3 tgt)
            {
                // In case we get stuck under a building component, esp at OilRigs
                RaycastHit hitinfo;
                if (Physics.Raycast(current + drone.transform.up, Vector3.up, out hitinfo, 2f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        Instance.DoLog($"{rc.rcIdentifier} CRASH ABOVE!", true);
                        return true;
                    }
                }
                return false;
            }

            bool DangerFront(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Linecast(current, tgt, out hitinfo, terrainMask, QueryTriggerInteraction.Collide))
                {
                    if (hitinfo.collider.name.Contains("rock") || hitinfo.collider.name.Contains("Terr"))
                    {
                        Instance.DoLog($"{hitinfo.collider.name}");
                        return true;
                    }
                }
                else if (Physics.Raycast(current, drone.transform.TransformDirection(Vector3.forward), out hitinfo, 10f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)// && hitinfo.distance <= 4f)
                    {
                        if (hitinfo.distance < 2) return false;
                        string hit = null;
                        try
                        {
                            hit = $" with {hitinfo.GetEntity().ShortPrefabName}";
                        }
                        catch
                        {
                            return false;
                        }
                        var d = Math.Round(hitinfo.distance, 2).ToString();
                        Instance.DoLog($"{rc.rcIdentifier} FRONTAL CRASH{hit} distance {d}m!", true);
                        return true;
                    }
                }
                return false;
            }

            bool BigRock(Vector3 tgt)
            {
                RaycastHit hitinfo;

                if (Physics.Linecast(current, tgt, out hitinfo, terrainMask, QueryTriggerInteraction.Ignore))
                {
                    if (hitinfo.collider.name.Contains("rock") || hitinfo.collider.name.Contains("Terr"))
                    {
                        Instance.DoLog($"{rc.rcIdentifier} found {hitinfo.collider.name} in path!", true);
                        return true;
                    }
                }
                return false;
            }

            bool TooLow(Vector3 tgt)
            {
                RaycastHit hitinfo;

                if(Physics.Raycast(current, Vector3.down, out hitinfo, 10f, groundMask) || Physics.Raycast(current, Vector3.up, out hitinfo, 10f, groundMask))
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
        static BasePlayer FindPlayerByName(string name)
        {
            BasePlayer result = null;
            foreach (BasePlayer current in BasePlayer.activePlayerList)
            {
                if (current.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    BasePlayer result2 = current;
                    return result2;
                }
                if (current.UserIDString.Contains(name, CompareOptions.OrdinalIgnoreCase))
                {
                    BasePlayer result2 = current;
                    return result2;
                }
                if (current.displayName.Contains(name, CompareOptions.OrdinalIgnoreCase))
                {
                    result = current;
                }
            }
            return result;
        }

        public string GetRoad()
        {
            // Pick a random road if road is null
            var cnt = Instance.roads.Count;

            System.Random rand = new System.Random();
            List<string> roadlist = new List<string>(Instance.roads.Keys);
            return roadlist[rand.Next(cnt)];
        }

        public void SpawnRoadDrone()
        {
            if (!configData.Drones["road"].spawn) return;
            if(configData.Drones["road"].start == null)
            {
                configData.Drones["road"].start = GetRoad();
            }
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

            DoLog($"SpawnRoadDrone: Moving to start of {road}...");
            obj.SetType(DroneType.Road);
            bool success = obj.SetName(configData.Drones["road"].name);
            obj.SetRoad(road);
            obj.enabled = true;
            drone.Spawn();

            drones.Add(configData.Drones["road"].name, drone);
            if(success) SetDroneInCS(configData.Drones["road"].name);
        }
        public void SpawnRingDrone()
        {
            if (!configData.Drones["ring"].spawn) return;
            drones.Remove(configData.Drones["ring"].name);
            if (configData.Drones["ring"].start == null) configData.Drones["ring"].start = "Road 0";

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

            DoLog($"SpawnRingDrone: Moving to start of {road}...");
            obj.SetType(DroneType.Ring);
            bool success = obj.SetName(configData.Drones["ring"].name);
            obj.SetRoad(road, true);
            obj.enabled = true;
            drone.Spawn();

            drones.Add(configData.Drones["ring"].name, drone);
            if(success) SetDroneInCS(configData.Drones["ring"].name);
        }
        public void SpawnMonumentDrone()
        {
            if (!configData.Drones["monument"].spawn) return;
            drones.Remove(configData.Drones["monument"].name);

            Vector3 target = Vector3.zero;
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            var drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            var obj = drone.gameObject.AddComponent<DroneNav>();
            obj.SetType(DroneType.MonAll);
            bool success = obj.SetName(configData.Drones["monument"].name);
            obj.enabled = true;
            drone.Spawn();

            drones.Add(configData.Drones["monument"].name, drone);
            if(success) SetDroneInCS(configData.Drones["monument"].name);
        }

        void RemoveDroneFromCS(string drone)
        {
            var stations = UnityEngine.Object.FindObjectsOfType<ComputerStation>();
            foreach(var station in stations)
            {
                if (station.controlBookmarks.ContainsKey(drone))
                {
                    station.controlBookmarks.Remove(drone);
                }
            }
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
            bool ishapis =  ConVar.Server.level.Contains("Hapis");

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
                    if (ishapis)
                    {
                        var elem = Regex.Matches(monument.name, @"\w{4,}|\d{1,}");
                        foreach (Match e in elem)
                        {
                            if (e.Value.Equals("MONUMENT")) continue;
                            if (e.Value.Contains("Label")) continue;
                            name += e.Value + " ";
                        }
                        name = name.Trim();
                    }
                    else
                    {
                        name = Regex.Match(monument.name, @"\w{6}\/(.+\/)(.+)\.(.+)").Groups[2].Value.Replace("_", " ").Replace(" 1", "").Titleize();
                    }
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
            if (configData.Options.debugMovement | configData.Options.debug) Interface.Oxide.LogInfo(message);
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
