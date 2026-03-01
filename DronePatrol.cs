#region License (GPL v2)
/*
    Drone Patrol - Spawn server drones and allow for player spawn
    Copyright (c)2021 RFC1920 <desolationoutpostpve@gmail.com>

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License v2.0.

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
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("DronePatrol", "RFC1920", "1.0.25")]
    [Description("Create server drones that fly and roam, and allow users to spawn a drone of their own.")]
    internal class DronePatrol : RustPlugin
    {
        #region vars
        [PluginReference]
        private readonly Plugin RoadFinder, Friends, Clans, Chute, GridAPI, Economics, ServerRewards, BankSystem;

        public GameObject obj;
        public Dictionary<string, Road> roads = new();
        public List<string> monNames = new();
        public SortedDictionary<string, Vector3> monPos = new();
        public SortedDictionary<string, Vector3> monSize = new();
        private Dictionary<ulong, DroneNav> pguis = new();

        public static Timer checkTimer;
        public static string droneprefab = "assets/prefabs/deployable/drone/drone.deployed.prefab";
        private ConfigData configData;

        public static DronePatrol Instance;
        private const string permDriver = "dronepatrol.use";
        private const string permAdmin = "dronepatrol.admin";
        private const string DRONEGUI = "dronepatrol.hud";

        public static Dictionary<string, uint> drones = new();
        public static bool initdone;

        public class Road
        {
            public List<Vector3> points = new();
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
        private void OnServerInitialized()
        {
            LoadConfigVariables();
            Instance = this;

            AddCovalenceCommand("drone", "CmdSpawnDrone");
            AddCovalenceCommand("drones", "CmdDroneStatus");
            AddCovalenceCommand("md", "CmdSpawnMonumentDrone");
            AddCovalenceCommand("rd", "CmdSpawnRoadDrone");
            AddCovalenceCommand("fd", "CmdSpawnSpyDrone");
            AddCovalenceCommand("ringd", "CmdSpawnRingDrone");
            permission.RegisterPermission(permDriver, this);
            permission.RegisterPermission(permAdmin, this);

            if (configData.Options.SetMaxControlRange)
            {
                float maxControlRange = configData.Options?.maxControlRange > 0 ? configData.Options.maxControlRange : 5000;
                ConsoleSystem.Run(ConsoleSystem.Option.Server.FromServer(), $"drone.maxControlRange {maxControlRange}");
            }

            LoadData();

            foreach (ComputerStation station in UnityEngine.Object.FindObjectsOfType<ComputerStation>())
            {
                if (station == null) continue;
                foreach (string drone in drones.Keys)
                {
                    if (station.controlBookmarks.Contains(drone)) station.controlBookmarks.Remove(drone);
                }
            }

            FindMonuments();
            CheckDrones(true);

            object roads = RoadFinder?.CallHook("GetRoads");
            if (roads != null)
            {
                string json = JsonConvert.SerializeObject(roads);
                this.roads = JsonConvert.DeserializeObject<Dictionary<string, Road>>(json);
                NextTick(() =>
                {
                    SpawnRingDrone();
                    SpawnRoadDrone();
                });
            }

            NextTick(() => SpawnMonumentDrone());

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
                ["spydronekilled"] = "Drone {0} has been killed",
                ["nosuchspydrone"] = "Could not find spy drone named {0}",
                ["boughtdrone"] = "Drone purchased for {0} points.",
                ["droneprice"] = "Drone must be purchased for {0} points.",
                ["helptext"] = "To spawn a drone, type /drone NAM.EOFDRONE",
                ["heading"] = "Drone headed to {0}",
                ["nextpoint"] = "next point."
            }, this);
        }

        private void CheckDrones(bool startup = false)
        {
            if (startup)
            {
                DoLog("Checking drones");
                Drone[] allDrones = UnityEngine.Object.FindObjectsOfType<Drone>();
                if (allDrones != null)
                {
                    DoLog("Searching for drones with NONE as name");
                    foreach (Drone drone in allDrones)
                    {
                        if (drone?.rcIdentifier == "NONE" && !drone.IsDestroyed)
                        {
                            UnityEngine.Object.Destroy(drone.gameObject);
                            RemoveDroneFromCS(drone?.rcIdentifier);
                        }
                    }
                }

                foreach (KeyValuePair<string, uint> d in new Dictionary<string, uint>(drones))
                {
                    Drone drone = BaseNetworkable.serverEntities.Find(new NetworkableId(d.Value)) as Drone;
                    if (drone != null)
                    {
                        DoLog($"Killing spawned drone {d.Key}({d.Value})");
                        drone?.Kill();
                        UnityEngine.Object.Destroy(drone?.gameObject);
                        RemoveDroneFromCS(drone?.rcIdentifier);
                        drones.Remove(drone?.rcIdentifier);
                    }
                    if (!configData.Drones["monument"].name.Equals(d.Key) && !configData.Drones["ring"].name.Equals(d.Key) && !configData.Drones["road"].name.Equals(d.Key))
                    {
                        try
                        {
                            DoLog($"Killing spawned drone ({d.Key})");
                            drone?.Kill();
                            UnityEngine.Object.Destroy(drone?.gameObject);
                        }
                        catch { }
                        finally
                        {
                            if (drone != null)
                            {
                                RemoveDroneFromCS(drone?.rcIdentifier);
                                drones.Remove(drone?.rcIdentifier);
                            }
                        }
                    }
                }

                SaveData();
            }

            // Missing network group
            BaseEntity.saveList.RemoveWhere(p => !p);
            BaseEntity.saveList.RemoveWhere(p => p == null);

            // Broken/dead drones
            DoLog("Checking configured drones");
            foreach (KeyValuePair<string, uint> d in new Dictionary<string, uint>(drones))
            {
                BaseEntity drone = BaseNetworkable.serverEntities.Find(new NetworkableId(d.Value)) as BaseEntity;
                //(drone as BaseCombatEntity).diesAtZeroHealth = true;
                DoLog($"--Checking {d.Key}({d.Value})");
                if (drone == null) continue;
                if (drone.IsDestroyed || drone.IsBroken() || !drone.HasFlag(BaseEntity.Flags.Reserved2))
                {
                    DoLog($"--Respawning {d.Key}");
                    drone?.Kill();
                    UnityEngine.Object.Destroy(drone.gameObject);
                    drones.Remove(d.Key);
                    SaveData();

                    if (configData.Drones["monument"].name.Equals(d.Key))
                    {
                        SpawnMonumentDrone();
                    }
                    else if (configData.Drones["ring"].name.Equals(d.Key))
                    {
                        SpawnRingDrone();
                    }
                    else if (configData.Drones["road"].name.Equals(d.Key))
                    {
                        SpawnRoadDrone();
                    }
                }
            }
            checkTimer = timer.Once(30, () => CheckDrones());
        }

        private void Unload()
        {
            // KILL ALL DRONES OF ANY ORIGIN
            //foreach (Drone d in UnityEngine.Object.FindObjectsOfType<Drone>())
            //{
            //    UnityEngine.Object.Destroy(d.gameObject);
            //}
            foreach (DroneNav d in UnityEngine.Object.FindObjectsOfType<DroneNav>())
            {
                UnityEngine.Object.Destroy(d?.gameObject);
            }
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, DRONEGUI);
            }
        }

        private object CanPickupEntity(BasePlayer player, BaseCombatEntity drone)
        {
            if (player == null || drone == null) return null;

            DroneNav dnav = drone.gameObject.GetComponentInParent<DroneNav>();
            if (dnav != null)
            {
                if (dnav.ownerid == 0 && !player.IsAdmin)
                {
                    return true;
                }
                if (dnav.ownerid != player.userID && !IsFriend(player.userID, dnav.ownerid))
                {
                    return true;
                }
            }
            return null;
        }

        private void OnEntitySpawned(Drone drone)
        {
            if (!initdone) return;
            if (!configData.Options.setPlayerDroneInCS) return;
            if (drone == null) return;

            ComputerStation[] stations = UnityEngine.Object.FindObjectsOfType<ComputerStation>();
            if (stations.Length == 0) return;

            foreach (ComputerStation station in stations)
            {
                if (station.OwnerID != drone.OwnerID && !IsFriend(station.OwnerID, drone.OwnerID)) continue;

                if (station.controlBookmarks.Contains(drone.rcIdentifier))
                {
                    station.controlBookmarks.Remove(drone?.rcIdentifier);
                }
                station.controlBookmarks.Add(drone.rcIdentifier);
            }
        }

        private void OnEntitySpawned(ComputerStation station)
        {
            if (!initdone) return;
            if (!configData.Options.setServerDroneInAllCS) return;
            if (station == null) return;

            foreach (string drone in new List<string>() { "MonumentDrone", "RingDrone", "RoadDrone" })
            {
                if (drone == null) return;
                if (station.controlBookmarks.Contains(drone))
                {
                    station.controlBookmarks.Remove(drone);
                }
                if (drones.ContainsKey(drone))
                {
                    station.controlBookmarks.Add(drone);
                }
            }
        }

        private void OnEntityDeath(Drone drone, HitInfo info)
        {
            CheckAndRespawnDrones(drone);
        }

        //private void OnEntityKill(Drone drone)
        //{
        //    CheckAndRespawnDrones(drone);
        //}

        private void CheckAndRespawnDrones(Drone drone)
        {
            DroneNav dnav = drone.gameObject.GetComponentInParent<DroneNav>();
            if (dnav != null)
            {
                string oldName = dnav.drone.rcIdentifier;
                DoLog($"Drone {oldName} died");
                RemoveDroneFromCS(oldName);
                UnityEngine.Object.Destroy(dnav.gameObject);

                foreach (KeyValuePair<string, uint> item in drones.Where(kvp => kvp.Value == drone.net.ID.Value).ToList())
                {
                    drones.Remove(item.Key);
                }

                foreach (KeyValuePair<string, DroneInfo> item in configData.Drones.Where(kvp => kvp.Value.name == oldName).ToList())
                {
                    if (item.Value.spawn)
                    {
                        switch (item.Key)
                        {
                            case "monument":
                                SpawnMonumentDrone();
                                break;
                            case "ring":
                                SpawnRingDrone();
                                break;
                            case "road":
                                SpawnRoadDrone();
                                break;
                        }
                    }
                }
            }
        }
        #endregion

        #region Main
        private void CmdDroneStatus(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.IsAdmin) return;

            string message = "Drone Status:\n";
            if (args.Length > 0)
            {
                string realName = args[0];
                Drone realDrone = RemoteControlEntity.FindByID(realName) as Drone;
                if (realDrone != null)
                {
                    string[] gc = (string[])GridAPI.CallHook("GetGrid", realDrone.transform.position);
                    string curr = realDrone.transform.position.ToString() + "(" + string.Concat(gc) + ")";
                    message += $"{realName} ({realDrone.net.ID}):\n\tspawned: {realDrone.isSpawned}\n\tbroken: {realDrone.IsBroken()}"
                        + $"\n\tactive: {realDrone.isActiveAndEnabled}\n\tlocation: {curr}\n";
                    Message(iplayer, message);
                }
                return;
            }
            foreach (KeyValuePair<string, DroneInfo> drone in configData.Drones)
            {
                string realName = "";
                foreach (KeyValuePair<string, uint> d in drones)
                {
                    if (d.Key.StartsWith(drone.Value.name))
                    {
                        realName = d.Key;
                    }
                }
                if (realName?.Length == 0) continue;
                BaseEntity realDrone = BaseNetworkable.serverEntities.Find(new NetworkableId(drones[realName])) as BaseEntity;
                if (realDrone == null) continue;

                string[] gc = (string[])GridAPI.CallHook("GetGrid", realDrone.transform.position);
                string curr = realDrone.transform.position.ToString() + "(" + string.Concat(gc) + ")";
                message += $"{realName} ({realDrone.net.ID}):\n\tspawned: {realDrone.isSpawned}\n\tbroken: {realDrone.IsBroken()}"
                    + $"\n\tactive: {realDrone.isActiveAndEnabled}\n\tlocation: {curr}\n";
            }
            Message(iplayer, message);
        }

        private void CmdSpawnDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permDriver))
            {
                Message(iplayer, "notauthorized");
                return;
            }

            if (args.Length == 0)
            {
                Message(iplayer, "helptext");
                return;
            }
            if (args.Length == 2)
            {
                if (args[1] == "kill")
                {
                    foreach (Drone d in UnityEngine.Object.FindObjectsOfType<Drone>())
                    {
                        if (d != null && d.rcIdentifier == args[0])
                        {
                            DoLog($"Killing {d.rcIdentifier}");
                            RemoveDroneFromCS(d.rcIdentifier);
                            UnityEngine.Object.Destroy(d.gameObject);
                        }
                    }
                }
                return;
            }

            string droneName = Lang("drone");
            if (args[0] != null) droneName = args[0];

            BasePlayer player = iplayer.Object as BasePlayer;
            if (player == null) return;

            if (configData.Options.useEconomics || configData.Options.useServerRewards)
            {
                if (CheckEconomy(player, configData.Options.droneCost))
                {
                    CheckEconomy(player, configData.Options.droneCost, true);
                    Message(iplayer, "boughtdrone", configData.Options.droneCost.ToString());
                }
                else
                {
                    Message(iplayer, "droneprice", configData.Options.droneCost.ToString());
                    return;
                }
            }

            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            Drone drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation) as Drone;
            drone.OwnerID = player.userID;

            int i = 1;
            string newName = droneName;
            while (RemoteControlEntity.IDInUse(newName))
            {
                newName = droneName + i.ToString();
                i++;
                if (i > 10) break;
            }
            drone?.UpdateIdentifier(newName, true);
            drone._maxHealth = 1000;
            drone.SetHealth(1000);
            drone.Spawn();
            drone.SendNetworkUpdateImmediate();
        }

        private void CmdSpawnSpyDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            //string debug = string.Join(",", args); Puts($"{debug}");
            if (args.Length > 0)
            {
                if (args.Length > 1)
                {
                    if (args[1] == "kill")
                    {
                        IRemoteControllable tokillir = RemoteControlEntity.FindByID($"SPY{args[0].ToUpper()}");
                        if (tokillir != null)
                        {
                            string tkid = tokillir.GetIdentifier();
                            Drone tokill = tokillir as Drone;
                            tokill.Kill();
                            UnityEngine.Object.Destroy(tokill);
                            Message(iplayer, "spydronekilled", tkid);
                            return;
                        }
                        Message(iplayer, "nosuchspydrone", args[0].ToUpper());
                        return;
                    }
                }
                else if (args[0] == "list")
                {
                    Drone[] dnav = UnityEngine.Object.FindObjectsOfType<Drone>();
                    Dictionary<string, DroneInfo>.KeyCollection sdrones = configData.Drones.Keys;
                    foreach (Drone d in dnav)
                    {
                        if (d?.rcIdentifier.Contains("SPY") == true)
                        {
                            Message(iplayer, d.rcIdentifier);
                        }
                    }
                    return;
                }

                BasePlayer targetPlayer = FindPlayerByName(args[0]);
                if (targetPlayer == null)
                {
                    Message(iplayer, "nosuchplayer", args[0]);
                    return;
                }

                Vector3 target = targetPlayer.transform.position;
                target.y = TerrainMeta.HeightMap.GetHeight(targetPlayer.transform.position) + configData.Options.minHeight;

                Drone drone = GameManager.server.CreateEntity(droneprefab, target, targetPlayer.transform.rotation) as Drone;
                DroneNav obj = drone.gameObject.AddComponent<DroneNav>();

                BasePlayer player = iplayer.Object as BasePlayer;
                drone.OwnerID = player.userID;
                obj.ownerid = player.userID;
                obj.SetType(DroneType.Spy);
                obj.SetPlayerTarget(targetPlayer);
                obj.enabled = true;

                string plName = targetPlayer.displayName ?? targetPlayer.UserIDString;
                string droneName = $"SPY{plName}";

                int i = 1;
                string newName = droneName;
                while (RemoteControlEntity.IDInUse(newName))
                {
                    newName = droneName + i.ToString();
                    i++;
                    if (i > 10) break;
                }
                drone?.UpdateIdentifier($"{newName}", true);
                drone._maxHealth = 1000;
                drone.SetHealth(1000);
                drone.Spawn();
                drone.SendNetworkUpdateImmediate();
                SetDroneInCS(droneName);
                Message(iplayer, "spydrone", newName, plName);
            }
        }

        private void CmdSpawnMonumentDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            if (args.Length > 0)
            {
                BaseEntity dr = BaseNetworkable.serverEntities.Find(new NetworkableId(drones[configData.Drones["monument"].name])) as BaseEntity;
                if (args[0] == "kill")
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        UnityEngine.Object.Destroy(nav.gameObject);
                    }
                }
                else if (args[0] == "status")
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        string curr;
                        string tgt;
                        if (GridAPI != null)
                        {
                            string[] gc = (string[])GridAPI.CallHook("GetGrid", nav.current);
                            curr = nav.current.ToString() + "(" + string.Concat(gc) + ")";
                            string[] gt = (string[])GridAPI.CallHook("GetGrid", nav.target);
                            tgt = nav.target.ToString() + "(" + string.Concat(gt) + ")";
                        }
                        else
                        {
                            curr = nav.current.ToString();
                            tgt = nav.target.ToString();
                        }
                        Message(iplayer, "mdstatus", nav.drone.rcIdentifier, curr, nav.currentMonument, tgt, nav.currentMonSize);
                    }
                    return;
                }
                else if (args[0] == "list")
                {
                    foreach (string key in monPos.Keys)
                    {
                        Message(iplayer, key);
                    }
                    return;
                }
                string nextMon = string.Join(" ", args);
                if (monPos.ContainsKey(nextMon))
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        nav.currentMonument = nextMon;
                        OnDroneNavChange(nav, nextMon);
                    }
                }
                return;
            }
            BasePlayer player = iplayer.Object as BasePlayer;

            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            Drone drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation) as Drone;
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            obj.type = DroneType.MonAll;
            drone._maxHealth = 1000;
            drone.SetHealth(1000);
            drone.Spawn();

            if (true)
            {
                var ds = drone.gameObject.GetComponent<StorageContainer>();
                if (ds != null)
                {
                    Puts("Opening drone inventory");
                    Item item = ItemManager.CreateByName("explosive.timed.item", 1);
                    item.MoveToContainer(ds.inventory);
                    ds.inventory.MarkDirty();
                }
            }

            Message(iplayer, "Spawned Monument drone");
        }

        private void CmdSpawnRoadDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            if (args.Length > 0)
            {
                BaseEntity dr = BaseNetworkable.serverEntities.Find(new NetworkableId(drones[configData.Drones["road"].name])) as BaseEntity;
                if (args[0] == "kill")
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        UnityEngine.Object.Destroy(nav.drone.gameObject);
                    }
                }
                else if (args[0] == "status")
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        string curr = nav.current.ToString();
                        string tgt = nav.target.ToString();
                        if (GridAPI != null)
                        {
                            string[] gc = (string[])GridAPI.CallHook("GetGrid", nav.current);
                            curr += "(" + string.Concat(gc) + ")";
                            string[] gt = (string[])GridAPI.CallHook("GetGrid", nav.target);
                            tgt += "(" + string.Concat(gt) + ")";
                        }

                        Message(iplayer, "rdstatus", nav.drone.rcIdentifier, curr, $"{nav.currentRoadName} ", tgt);
                    }
                    return;
                }
                else if (args[0] == "list")
                {
                    foreach (string key in roads.Keys)
                    {
                        Message(iplayer, key);
                    }
                    return;
                }
                if (roads.ContainsKey(args[0]))
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        nav.currentRoad = roads[args[0]];
                        OnDroneNavChange(nav, Instance.configData.Drones["road"].name);
                    }
                }
                return;
            }
            BasePlayer player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            Drone drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation) as Drone;
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            drone._maxHealth = 1000;
            drone.SetHealth(1000);
            drone.Spawn();
            string road = configData.Drones["road"].start;
            DoLog($"CmdSpawnRoadDrone: Moving to start of {road}...");
            obj.SetRoad(road);
        }

        private void CmdSpawnRingDrone(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permAdmin))
            {
                Message(iplayer, "notauthorized");
                return;
            }
            if (args.Length > 0)
            {
                BaseEntity dr = BaseNetworkable.serverEntities.Find(new NetworkableId(drones[configData.Drones["road"].name])) as BaseEntity;
                if (args[0] == "kill")
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        UnityEngine.Object.Destroy(nav.drone.gameObject);
                    }
                }
                else if (args[0] == "status")
                {
                    DroneNav nav = dr.GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        string curr;
                        if (GridAPI != null)
                        {
                            string[] gc = (string[])GridAPI.CallHook("GetGrid", nav.current);
                            curr = nav.current.ToString() + "(" + string.Concat(gc) + ")";
                        }
                        else
                        {
                            curr = nav.current.ToString();
                        }
                        Message(iplayer, "ringstatus", nav.drone.rcIdentifier, curr);
                    }
                }
                return;
            }
            BasePlayer player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            Drone drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation) as Drone;
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            drone._maxHealth = 1000;
            drone.SetHealth(1000);
            drone.Spawn();
            string road = configData.Drones["ring"].start;
            DoLog($"CmdSpawnRingDrone: Moving to start of {road}...");
            obj.SetRoad(road);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            //if (input.current.buttons > 1)
            //    Puts($"OnPlayerInput: {input.current.buttons}");

            if (input.current.buttons == configData.Options.ActivationCode && Chute != null && pguis.ContainsKey(player.userID))
            {
                ComputerStation station = player.GetMounted() as ComputerStation;
                Drone drone = station.currentlyControllingEnt.Get(true).GetComponent<Drone>();
                if (drone != null)
                {
                    Vector3 newPos = new(drone.transform.position.x, drone.transform.position.y + 10f, drone.transform.position.z);
                    station.StopControl(player);
                    station.DismountPlayer(player, true);
                    station.SendNetworkUpdateImmediate();

                    pguis.Remove(player.userID);
                    Teleport(player, newPos);
                    Chute?.CallHook("ExternalAddPlayerChute", player, null);
                    if (drone.OwnerID == player.userID)
                    {
                        DoLog("Killing player drone.");
                        UnityEngine.Object.Destroy(drone.gameObject);
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
            player.SendNetworkUpdateImmediate();

            if (player.net?.connection != null) player.ClientRPC(RpcTarget.Player("StartLoading", player));
        }

        private object OnBookmarkAdd(ComputerStation computerStation, BasePlayer player, string bookmarkName)
        {
            DoLog($"Player {player.UserIDString} added bookmark {bookmarkName}");
            return null;
        }

        private object OnBookmarkControl(ComputerStation station, BasePlayer player, string bookmarkName, IRemoteControllable remoteControllable)
        {
            if (player == null) return null;
            Drone drone = remoteControllable as Drone;
            if (drone != null)
            {
                //CameraViewerId viewerId = drone.ControllingViewerId.Value;
                //drone.StopControl(viewerId);
                //drone.InitializeControl(new CameraViewerId(player.userID, 0));

                DroneNav obj = drone.gameObject.GetComponent<DroneNav>();
                if (obj != null)
                {
                    DoLog("Found DroneNav component");
                    obj.player = player;
                    if (!string.IsNullOrEmpty(drone?.rcIdentifier))
                    {
                        DoLog($"Player {player?.UserIDString} now controlling drone {drone?.rcIdentifier}, owned by {drone?.OwnerID}");
                    }

                    if (obj?.currentRoad != null)
                    {
                        string roadname = string.IsNullOrEmpty(obj?.currentRoadName) ? Lang("nexpoint") : obj?.currentRoadName;
                        DroneGUI(player, obj, Lang("heading", roadname, roadname));
                    }
                    else if (obj.currentMonument?.Length > 0)
                    {
                        DroneGUI(player, obj, Lang("heading", obj.currentMonument, obj.currentMonument));
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(drone.rcIdentifier))
                    {
                        DroneGUI(player, null, $"{player?.displayName}'s {Lang("drone")}, {drone.rcIdentifier}.");
                    }
                    else
                    {
                        DroneGUI(player, null, player?.displayName + "'s " + Lang("drone"));
                    }
                }
            }
            return null;
        }

        private object OnBookmarkControlEnd(ComputerStation computerStation, BasePlayer player, BaseEntity controlledEntity)
        {
            CuiHelper.DestroyUi(player, DRONEGUI);
            pguis.Remove(player.userID);
            return null;
        }

        private void DroneGUI(BasePlayer player, DroneNav drone, string target = null, string monName = "UNKNOWN")
        {
            if (player == null) return;

            pguis.Remove(player.userID);
            CuiHelper.DestroyUi(player, DRONEGUI);
            if (drone != null) pguis.Add(player.userID, drone);

            CuiElementContainer container = UI.Container(DRONEGUI, UI.Color("FFF5E1", 0.16f), "0.4 0.95", "0.6 0.99", false, "Overlay");
            string uicolor = "#ffff33";
            if (target == null)
            {
                target = Lang("drone");
                uicolor = "#dddddd";
            }
            string label = target;
            UI.Label(ref container, DRONEGUI, UI.Color(uicolor, 1f), label, 12, "0 0", "1 1");

            if (monPos.ContainsKey(monName))
            {
                Vector3 point = monPos[monName];
                player.SendConsoleCommand("ddraw.text", 90, Color.green, point, $"<size=20>{monName}</size>");
            }
            CuiHelper.AddUi(player, container);
        }
        #endregion

        #region our_hooks
        private object OnDroneNavChange(DroneNav drone, string newdirection)
        {
            foreach (KeyValuePair<ulong, DroneNav> pgui in new Dictionary<ulong, DroneNav>(pguis))
            {
                if (pgui.Value == drone)
                {
                    BasePlayer pl = BasePlayer.FindByID(pgui.Key);
                    DroneGUI(pl, drone, Lang("heading", null, newdirection), newdirection);
                }
            }
            return null;
        }

        private object OnDroneNavArrived(BasePlayer player, DroneNav drone, string newdirection)
        {
            foreach (KeyValuePair<ulong, DroneNav> pgui in new Dictionary<ulong, DroneNav>(pguis))
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
            ConfigData config = new()
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
                    useTeams = false,
                    DisplayMapMarkersOnServerDrones = true,
                    SetMaxControlRange = true,
                    maxControlRange = 5000
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

            if (configData.Options.maxControlRange == 0) configData.Options.maxControlRange = 5000;

            configData.Version = Version;
            SaveConfig(configData);
        }

        private void SaveConfig(ConfigData config)
        {
            Config.WriteObject(config, true);
        }

        public class ConfigData
        {
            public Options Options = new();
            public Dictionary<string, DroneInfo> Drones;
            public VersionNumber Version;
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
            public bool useEconomics;
            public bool useServerRewards;
            public bool useBankSystem;
            public double droneCost;
            public bool DisplayMapMarkersOnServerDrones;
            public bool SetMaxControlRange;
            public float maxControlRange;
        }

        private void SaveData()
        {
            // Save the data file as we add/remove minicopters.
            Interface.GetMod().DataFileSystem.WriteObject(Name, drones);
        }

        private void LoadData()
        {
            drones = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, uint>>(Name);
            if (drones == null)
            {
                drones = new Dictionary<string, uint>();
                SaveData();
            }
        }
        #endregion

        #region classes
        internal class DroneInfo
        {
            public string name;
            public bool spawn;
            public string start;
        }

        internal enum DroneType
        {
            None = 0,
            User = 1,
            Road = 2,
            Ring = 4,
            MonSingle = 8,
            MonAll = 16,
            Spy = 32
        }

        private class DroneNav : MonoBehaviour
        {
            public DroneType type;

            public Drone drone;
            public BasePlayer player;
            public ulong controllingUserId;
            public BasePlayer targetPlayer;
            public int buildingMask = LayerMask.GetMask("Construction", "Prevent Building", "Deployed", "World", "Terrain", "Tree", "Invisible", "Default");
            public int groundMask = LayerMask.GetMask("Construction", "Terrain", "World", "Water");
            public int terrainMask = LayerMask.GetMask("Terrain");

            public uint droneid;
            public ulong ownerid;
            public InputState input;

            public Road currentRoad;
            public string droneName;
            public string currentRoadName;
            public string currentMonument;
            public float currentMonSize;
            public Vector3 current;
            public Quaternion rotation;
            public Vector3 direction;
            public Vector3 target = Vector3.zero;
            public Vector3 last = Vector3.zero;
            public GameObject slop = new();

            public Stopwatch stuckTimer;

            public int whichPoint;
            public int totalPoints;

            public bool grounded = true;
            public bool started;
            public bool ending;
            public int markerdelay;
            public MapMarkerGenericRadius marker;

            private void Awake()
            {
                Instance.DoLog("Awake()");
                drone = GetComponent<Drone>();
                stuckTimer = new Stopwatch();
                stuckTimer.Stop();
                enabled = false;
            }

            private void OnDestroy()
            {
                if (drone?.IsDestroyed == false)
                {
                    Destroy(drone?.gameObject); drone?.Kill();
                }
                if (marker?.IsDestroyed == false)
                {
                    Destroy(marker?.gameObject); marker?.Kill();
                }
            }

            private void Start()
            {
                droneid = (uint)drone.net.ID.Value;
            }

            public void SetPlayerTarget(BasePlayer pl)
            {
                if (type != DroneType.Spy) return;
                targetPlayer = pl;
            }

            public string SetName(string name)
            {
                Instance.DoLog($"Trying to set drone '{drone.rcIdentifier}' name to '{name}'");
                int i = 1;
                string newName = name;
                while (RemoteControlEntity.IDInUse(newName))
                {
                    newName = name + i.ToString();
                    i++;
                    if (i > 10) break;
                }
                (drone as IRemoteControllable).UpdateIdentifier(newName, true);
                Instance.DoLog($"Set name to '{newName}'");
                droneName = newName;
                return newName;
            }

            public void SetType(DroneType type)
            {
                Instance.DoLog($"Set drone {drone.rcIdentifier} type to {type}");
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

            private void MinimizeRoadPoints()
            {
                // Cut down on the jerkiness - we don't need no stinkin points!
                int cnt = currentRoad.points.Count;
                Instance.DoLog($"{drone.rcIdentifier} road points {cnt}");
                List<Vector3> newpts = new();

                int skip;
                if (cnt > 500) skip = 8;
                else if (cnt > 250) skip = 6;
                else if (cnt > 100) skip = 4;
                else if (cnt > 30) skip = 3;
                else if (cnt > 15) skip = 2;
                else return;

                int i = 0;
                foreach (Vector3 pts in currentRoad.points)
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
                Instance.DoLog($"{drone.rcIdentifier} road points changed to {newpts.Count}");
            }

            private void UpdateMarker()
            {
                if (!Instance.configData.Options.DisplayMapMarkersOnServerDrones) return;
                if (type != DroneType.Road && type != DroneType.Ring && type != DroneType.MonAll && type != DroneType.MonSingle) return;
                markerdelay++;
                if (markerdelay > 50)
                {
                    markerdelay = 0;
                    if (marker != null)
                    {
                        marker.transform.position = drone.transform.position;
                        marker.SendUpdate();
                        marker.SendNetworkUpdateImmediate();
                        return;
                    }

                    Instance.DoLog($"Creating mapmarker for {droneName} at {drone.transform.position}");
                    marker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", drone.transform.position) as MapMarkerGenericRadius;
                    if (marker != null)
                    {
                        marker.alpha = 0.8f;
                        marker.color1 = Color.black;
                        marker.color2 = Color.gray;
                        marker.name = droneName;
                        marker.radius = 0.15f;
                        marker.Spawn();
                        marker.SendUpdate();
                        marker.SendNetworkUpdateImmediate();
                    }
                }
            }

            private void Update()
            {
                drone._maxHealth = 1000;
                drone.SetHealth(1000);
                // You might be tempted to switch to FixedUpdate, but then the viewing player will take over flight...
                if (!enabled || drone.IsDead() || !drone.isSpawned)
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
                        if (targetPlayer != null)
                        {
                            FollowPlayer();
                        }
                        break;
                }
                UpdateMarker();
            }

            private void MoveToRoadPoint()
            {
                if (drone == null) return;
                if (grounded) return;

                current = drone.transform.position;
                target.x = currentRoad.points[whichPoint].x;
                target.y = GetHeight(currentRoad.points[whichPoint]);
                target.z = currentRoad.points[whichPoint].z;

                direction = (target - current).normalized;

                //Instance.DoLog($"{rc.rcIdentifier} trying to move to target point {whichPoint.ToString()} {target.ToString()}, currently at {current.ToString()}");
                if (Vector3.Distance(current, target) < 2)
                {
                    Instance.DoLog($"{drone.rcIdentifier} arrived at target point {whichPoint}");
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
                    Instance.DoLog($"{drone.rcIdentifier} changed target point to {whichPoint}");
                }

                //drone.transform.LookAt(target);
                SmoothRot();
                DoMoveDrone();
            }

            private void MoveToMonument()
            {
                if (drone == null) return;
                if (grounded) return;

                current = drone.transform.position;
                target = new Vector3(Instance.monPos[currentMonument].x, GetHeight(Instance.monPos[currentMonument]), Instance.monPos[currentMonument].z);

                direction = target - current;

                float monsize = Mathf.Max(25, currentMonSize);
                if (Vector3.Distance(current, target) < monsize)
                {
                    Instance.DoLog($"Within {monsize}m of {currentMonument}.  Switching...", true);
                    GetMonument();
                    target = Instance.monPos[currentMonument];
                    target.y = GetHeight(target);
                }
                SmoothRot();
                DoMoveDrone();
            }

            public void SmoothRot()
            {
                //Instance.Puts($"Smooth rotation for {drone.rcIdentifier}");
                //drone.transform.LookAt(target);
                Quaternion toRotation = Quaternion.LookRotation(direction);
                drone.transform.rotation = Quaternion.Slerp(drone.transform.rotation, toRotation, 3.5f * Time.time);
            }

            public void Stabilize()
            {
                Quaternion q = Quaternion.FromToRotation(drone.transform.up, Vector3.up) * drone.transform.rotation;
                drone.transform.rotation = Quaternion.Slerp(drone.transform.rotation, q, Time.deltaTime * 3.5f);
            }

            private void FollowPlayer()
            {
                if (drone == null) return;
                if (targetPlayer == null) return;
                current = drone.transform.position;
                target = targetPlayer.transform.position;
                target.y = targetPlayer.transform.position.y + Instance.configData.Options.minHeight;
                drone.transform.position = target;
                direction = (target - current).normalized;
                //drone.viewEyes.transform.LookAt(targetPlayer.transform.position); // does nothing
                Stabilize();
                last = drone.transform.position;
            }

            private void DoMoveDrone()
            {
                TakeControl();

                int x = 0;
                int z = 0;

                InputMessage message = new() { buttons = 0 };

                bool toolow = TooLow(current);
                bool above = DangerAbove(current);
                bool frontcrash = DangerFront(current);
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(current);

                // Flip if necessary
                if (Vector3.Dot(Vector3.up, drone.transform.up) < 0.1f)
                {
                    Instance.DoLog($"{drone.rcIdentifier} was tipping over", true);
                    drone.transform.rotation = Quaternion.identity;
                }
                if (above)
                {
                    // Move right to try to get around this crap
                    Instance.DoLog("Moving down and right to try to avoid...", true);
                    message.buttons = 48;
                }
                if (current.y < target.y || (current.y - terrainHeight < 1.5f) || toolow || frontcrash)
                {
                    if (!above)
                    {
                        // Move UP
                        Instance.DoLog($"{drone.rcIdentifier} Moving UP {current.y}", true);
                        message.buttons = 128;
                    }
                }
                else if (current.y > terrainHeight + (Instance.configData.Options.minHeight * 2) + 5 && !frontcrash)
                {
                    // Move Down
                    Instance.DoLog($"{drone.rcIdentifier} Moving DOWN {current.y}", true);
                    message.buttons = 64;
                }

                //if (BigRock(target))
                //{
                //    // Move up, allow forward below
                //    message.buttons = 128;
                //}
                if (!toolow)
                {
                    if (!DangerLeft(current) && frontcrash)
                    {
                        // Move RIGHT
                        Instance.DoLog($"{drone.rcIdentifier} Moving RIGHT to avoid frontal crash", true);
                        message.buttons = 16;
                        z = 1;
                    }
                    else if (!DangerRight(current) && frontcrash)
                    {
                        // Move LEFT
                        Instance.DoLog($"{drone.rcIdentifier} Moving LEFT to avoid frontal crash", true);
                        message.buttons = 8;
                        z = -1;
                    }
                }

                if (!frontcrash && !toolow)
                {
                    // Move FORWARD
                    message.buttons += 2;
                    x = 1;
                }

                message.mouseDelta.x = x;
                message.mouseDelta.z = z;

                InputState input = new() { current = message };
                drone.UserInput(input, new CameraViewerId(controllingUserId, 0));
                last = drone.transform.position;
            }

            private float GetHeight(Vector3 tgt)
            {
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(tgt);

                // Nice idea, but causes drones to disappear under terrain.
                //RaycastHit hitinfo;
                //if (Physics.Raycast(current, Vector3.down, out hitinfo, 100f, LayerMask.GetMask("Water")) && TerrainMeta.WaterMap.GetHeight(hitinfo.point) < terrainHeight)
                //{
                //    targetHeight = TerrainMeta.WaterMap.GetHeight(tgt) + Instance.configData.Options.minHeight;
                //}

                return terrainHeight + Instance.configData.Options.minHeight;
            }

            private void GetRoad()
            {
                // Pick a random road if road is null
                int cnt = Instance.roads.Count;

                System.Random rand = new();
                List<string> roadlist = new(Instance.roads.Keys);
                string croad = roadlist[rand.Next(cnt)];
                currentRoad = Instance.roads[croad];

                Instance.DoLog($"Set {drone.rcIdentifier} road to {currentRoad}");
                target = currentRoad.points[0];
                target.y = Instance.configData.Options.minHeight;
            }

            private void GetMonument()
            {
                // Pick a random monument if currentMonument is null
                int cnt = Instance.monNames.Count;
                List<string> monlist = new(Instance.monSize.Keys);

                System.Random rand = new();
                int index = rand.Next(cnt);
                string cmon = monlist[index];
                currentMonument = Instance.monNames[index];
                currentMonSize = Instance.monSize[cmon].z;

                Instance.DoLog($"Set {drone.rcIdentifier} monument to {currentMonument} {currentMonSize}", false);
                target = Instance.monPos[currentMonument];
                target.y = Instance.configData.Options.minHeight;

                Instance.OnDroneNavChange(this, currentMonument);
            }

            private void TakeControl()
            {
                if (!drone.IsBeingControlled)
                {
                    if (controllingUserId == 0)
                    {
                        controllingUserId = (ulong)UnityEngine.Random.Range(0, 2147483647);
                    }
                    drone.InitializeControl(new CameraViewerId(controllingUserId, 0));
                    Instance.DoLog($"Drone not being controlled by player.  Taking over as {controllingUserId}");
                }
            }

            #region crash_avoidance
            private bool DangerLeft(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Raycast(tgt, drone.transform.TransformDirection(Vector3.left), out hitinfo, 4f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        if (hitinfo.distance < 2) return false;
                        string hit;
                        try
                        {
                            hit = $" with {hitinfo.GetEntity().ShortPrefabName}";
                        }
                        catch
                        {
                            return false;
                        }
                        string d = Math.Round(hitinfo.distance, 2).ToString();
                        Instance.DoLog($"{drone.rcIdentifier} CRASH LEFT{hit} distance {d}!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool DangerRight(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Raycast(tgt, drone.transform.TransformDirection(Vector3.right), out hitinfo, 4f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        if (hitinfo.distance < 2) return false;
                        string hit;
                        try
                        {
                            hit = $" with {hitinfo.GetEntity().ShortPrefabName}";
                        }
                        catch
                        {
                            return false;
                        }
                        string d = Math.Round(hitinfo.distance, 2).ToString();
                        Instance.DoLog($"{drone.rcIdentifier} CRASH RIGHT{hit} distance {d}!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool DangerAbove(Vector3 tgt)
            {
                // In case we get stuck under a building component, esp at OilRigs
                RaycastHit hitinfo;
                if (Physics.Raycast(tgt + drone.transform.up, Vector3.up, out hitinfo, 2f, buildingMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        Instance.DoLog($"{drone.rcIdentifier} CRASH ABOVE!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool DangerFront(Vector3 tgt)
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
                        string hit;
                        try
                        {
                            hit = $" with {hitinfo.GetEntity().ShortPrefabName}";
                        }
                        catch
                        {
                            return false;
                        }
                        string d = Math.Round(hitinfo.distance, 2).ToString();
                        Instance.DoLog($"{drone.rcIdentifier} FRONTAL CRASH{hit} distance {d}m!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool BigRock(Vector3 tgt)
            {
                RaycastHit hitinfo;

                if (Physics.Linecast(current, tgt, out hitinfo, terrainMask, QueryTriggerInteraction.Ignore))
                {
                    if (hitinfo.collider.name.Contains("rock") || hitinfo.collider.name.Contains("Terr"))
                    {
                        Instance.DoLog($"{drone.rcIdentifier} found {hitinfo.collider.name} in path!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool TooLow(Vector3 tgt)
            {
                RaycastHit hitinfo;

                if (Physics.Raycast(tgt, Vector3.down, out hitinfo, 10f, groundMask) || Physics.Raycast(current, Vector3.up, out hitinfo, 10f, groundMask))
                {
                    if (hitinfo.GetEntity() != drone)
                    {
                        Instance.DoLog($"{drone.rcIdentifier} TOO LOW!", true);
                        return true;
                    }
                }

                return false;
            }
            #endregion
        }

        internal static class UI
        {
            public static CuiElementContainer Container(string panel, string color, string min, string max, bool useCursor = false, string parent = "Overlay")
            {
                return new CuiElementContainer()
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
                if (hexColor.StartsWith("#"))
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
        private static BasePlayer FindPlayerByName(string name)
        {
            BasePlayer result = null;
            foreach (BasePlayer current in BasePlayer.activePlayerList)
            {
                if (current.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
                if (current.UserIDString.Contains(name, CompareOptions.OrdinalIgnoreCase))
                {
                    return current;
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
            int cnt = Instance.roads.Count;

            System.Random rand = new();
            List<string> roadlist = new(Instance.roads.Keys);
            return roadlist[rand.Next(cnt)];
        }

        public void SpawnRoadDrone()
        {
            if (!configData.Drones["road"].spawn) return;
            if (configData.Drones["road"].start == null)
            {
                configData.Drones["road"].start = GetRoad();
            }
            drones.Remove(configData.Drones["road"].name);

            if (!roads.ContainsKey(configData.Drones["road"].start))
            {
                DoLog("No such road on this map :(");
                return;
            }

            string road = configData.Drones["road"].start;
            Vector3 target = roads[road].points[0];
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            Drone drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion()) as Drone;
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();

            DoLog($"SpawnRoadDrone: Moving to start of {road}...");
            obj.SetType(DroneType.Road);
            string newName = obj.SetName(configData.Drones["road"].name);
            obj.SetRoad(road);
            obj.enabled = true;
            drone._maxHealth = 1000;
            drone.SetHealth(1000);
            drone.Spawn();

            if (drones.ContainsKey(newName)) drones.Remove(newName);
            drones.Add(newName, (uint)drone.net.ID.Value);
            SaveData();
            if (!string.IsNullOrEmpty(newName)) SetDroneInCS(newName);
        }

        public void SpawnRingDrone()
        {
            if (!configData.Drones["ring"].spawn) return;
            drones.Remove(configData.Drones["ring"].name);
            SaveData();
            if (configData.Drones["ring"].start == null) configData.Drones["ring"].start = "Road 0";

            if (!roads.ContainsKey(configData.Drones["ring"].start))
            {
                DoLog("No such road on this map :(");
                return;
            }

            string road = configData.Drones["ring"].start;
            Vector3 target = roads[road].points[0];
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            Drone drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion()) as Drone;
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();

            DoLog($"SpawnRingDrone: Moving to start of {road}...");
            obj.SetType(DroneType.Ring);
            string newName = obj.SetName(configData.Drones["ring"].name);
            obj.SetRoad(road, true);
            obj.enabled = true;
            drone._maxHealth = 1000;
            drone.SetHealth(1000);
            drone.Spawn();

            if (drones.ContainsKey(newName)) drones.Remove(newName);
            drones.Add(newName, (uint)drone.net.ID.Value);
            SaveData();
            if (!string.IsNullOrEmpty(newName)) SetDroneInCS(newName);
        }

        public void SpawnMonumentDrone()
        {
            if (!configData.Drones["monument"].spawn) return;
            drones.Remove(configData.Drones["monument"].name);
            SaveData();

            Vector3 target = Vector3.zero;
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            Drone drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion()) as Drone;
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.SetType(DroneType.MonAll);
            string newName = obj.SetName(configData.Drones["monument"].name);
            obj.enabled = true;
            drone._maxHealth = 1000;
            drone.SetHealth(1000);
            drone.Spawn();

            if (drones.ContainsKey(newName)) drones.Remove(newName);
            drones.Add(newName, (uint)drone.net.ID.Value);
            SaveData();
            if (!string.IsNullOrEmpty(newName)) SetDroneInCS(newName);
        }

        private void RemoveDroneFromCS(string drone)
        {
            if (drone == null) return;
            if (!drones.ContainsKey(drone)) return;
            foreach (ComputerStation station in UnityEngine.Object.FindObjectsOfType<ComputerStation>())
            {
                if (station.controlBookmarks.Contains(drone))
                {
                    DoLog($"Removing drone {drone} from CS");
                    station.controlBookmarks.Remove(drone);
                    station.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                }
            }
        }

        private void SetDroneInCS(string drone)
        {
            if (!configData.Options.setServerDroneInAllCS) return;
            if (!drones.ContainsKey(drone)) return;
            foreach (ComputerStation station in UnityEngine.Object.FindObjectsOfType<ComputerStation>())
            {
                if (!station.controlBookmarks.Contains(drone))
                {
                    DoLog($"Adding drone {drone}:{drones[drone]} to CS {station.net.ID}");
                    station.controlBookmarks.Add(drone);
                    station.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                }
            }
        }

        private void FindMonuments()
        {
            bool ishapis = ConVar.Server.level.Contains("Hapis");

            foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (monument.name.Contains("power_sub")) continue;// || monument.name.Contains("cave")) continue;
                if (monument.name.Contains("derwater")) continue;
                float realWidth = 0f;
                string name = string.Empty;

                if (monument.name == "OilrigAI")
                {
                    name = "Small Oilrig";
                }
                else if (monument.name == "OilrigAI2")
                {
                    name = "Large Oilrig";
                }
                else if (monument.name == "assets/bundled/prefabs/autospawn/monument/medium/radtown_small_3.prefab")
                {
                    name = "Sewer Branch";
                    realWidth = 100;
                }
                else
                {
                    if (ishapis)
                    {
                        foreach (Match e in Regex.Matches(monument.name, @"\w{4,}|\d{1,}"))
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
                if (monPos.ContainsKey(name)) continue;

                Vector3 extents = monument.Bounds.extents;

                if (realWidth > 0f)
                {
                    extents.z = realWidth;
                }

                if (extents.z < 1)
                {
                    extents.z = 100f;
                }
                //DoLog($"Adding monument: {name}");
                monNames.Add(name);
                monPos.Add(name, monument.transform.position);
                monSize.Add(name, extents);
            }
            monPos.OrderBy(x => x.Key);
            monSize.OrderBy(x => x.Key);
        }

        private string RandomString()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            List<char> charList = chars.ToList();

            string random = "";

            for (int i = 0; i <= UnityEngine.Random.Range(5, 10); i++)
            {
                random += charList[UnityEngine.Random.Range(0, charList.Count - 1)];
            }
            return random;
        }

        private void DoLog(string message, bool ismovement = false)
        {
            if (ismovement && !configData.Options.debugMovement) return;
            if (configData.Options.debugMovement || configData.Options.debug) Interface.GetMod().LogInfo(message);
        }

        private bool IsFriend(ulong playerid, ulong ownerid)
        {
            if (configData.Options.useFriends && Friends != null)
            {
                object fr = Friends?.CallHook("AreFriends", playerid, ownerid);
                if (fr != null && (bool)fr)
                {
                    return true;
                }
            }
            if (configData.Options.useClans && Clans != null)
            {
                string playerclan = (string)Clans?.CallHook("GetClanOf", playerid);
                string ownerclan = (string)Clans?.CallHook("GetClanOf", ownerid);
                if (playerclan != null && ownerclan != null && playerclan == ownerclan)
                {
                    return true;
                }
            }
            if (configData.Options.useTeams)
            {
                RelationshipManager.PlayerTeam playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerid);
                if (playerTeam?.members.Contains(ownerid) == true)
                {
                    return true;
                }
            }
            return false;
        }

        private bool CheckEconomy(BasePlayer player, double cost, bool withdraw = false, bool deposit = false)
        {
            bool foundmoney = false;

            double balance;
            // Check Economics first.  If not in use or balance low, check ServerRewards below
            if (configData.Options.useEconomics && Economics)
            {
                balance = (double)Economics?.CallHook("Balance", player.UserIDString);
                if (balance >= cost)
                {
                    foundmoney = true;
                    if (withdraw)
                    {
                        return (bool)Economics?.CallHook("Withdraw", player.userID, cost);
                    }
                    else if (deposit)
                    {
                        bool w = (bool)Economics?.CallHook("Deposit", player.userID, cost);
                    }
                }
            }

            // No money via Economics, or plugin not in use.  Try ServerRewards.
            if (configData.Options.useServerRewards && ServerRewards)
            {
                object bal = ServerRewards?.Call("CheckPoints", player.userID);
                balance = Convert.ToDouble(bal);
                if (balance >= cost && !foundmoney)
                {
                    foundmoney = true;
                    if (withdraw)
                    {
                        return (bool)ServerRewards?.Call("TakePoints", player.userID, (int)cost);
                    }
                    else if (deposit)
                    {
                        bool w = (bool)ServerRewards?.Call("AddPoints", player.userID, (int)cost);
                    }
                }
            }

            // No money via Economics nor ServerRewards, or plugins not in use.  Try BankSystem.
            if (configData.Options.useBankSystem && BankSystem)
            {
                object bal = BankSystem?.Call("Balance", player.userID);
                balance = Convert.ToDouble(bal);
                if (balance >= cost && !foundmoney)
                {
                    foundmoney = true;
                    if (withdraw)
                    {
                        return (bool)BankSystem?.Call("Withdraw", player.userID, (int)cost);
                    }
                    else if (deposit)
                    {
                        bool w = (bool)BankSystem?.Call("Deposit", player.userID, (int)cost);
                    }
                }
            }
            // Just checking balance without withdrawal - did we find anything?
            return foundmoney;
        }
        #endregion
    }
}
