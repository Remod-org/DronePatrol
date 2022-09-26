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
using Network;

namespace Oxide.Plugins
{
    [Info("DronePatrol", "RFC1920", "1.0.19")]
    [Description("Create server drones that fly and roam, and allow users to spawn a drone of their own.")]
    internal class DronePatrol : RustPlugin
    {
        #region vars
        [PluginReference]
        private readonly Plugin RoadFinder, Friends, Clans, Chute, GridAPI, Economics, ServerRewards;

        public GameObject obj;
        public Dictionary<string, Road> roads = new Dictionary<string, Road>();
        public List<string> monNames = new List<string>();
        public SortedDictionary<string, Vector3> monPos  = new SortedDictionary<string, Vector3>();
        public SortedDictionary<string, Vector3> monSize = new SortedDictionary<string, Vector3>();
        private Dictionary<ulong, DroneNav> pguis = new Dictionary<ulong, DroneNav>();

        public static Timer checkTimer;
        public static string droneprefab = "assets/prefabs/deployable/drone/drone.deployed.prefab";
        private ConfigData configData;

        public static DronePatrol Instance;
        private const string permDriver = "dronepatrol.use";
        private const string permAdmin  = "dronepatrol.admin";
        private const string DRONEGUI = "npc.hud";

        public static Dictionary<string, BaseEntity> drones = new Dictionary<string, BaseEntity>();
        public static bool initdone;

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

            drones = new Dictionary<string, BaseEntity>();
            ComputerStation[] stations = UnityEngine.Object.FindObjectsOfType<ComputerStation>();
            foreach (ComputerStation station in stations)
            {
                if (station.controlBookmarks.ContainsKey(configData.Drones["road"].name)) station.controlBookmarks.Remove(configData.Drones["road"].name);
                if (station.controlBookmarks.ContainsKey(configData.Drones["ring"].name)) station.controlBookmarks.Remove(configData.Drones["ring"].name);
                if (station.controlBookmarks.ContainsKey(configData.Drones["monument"].name)) station.controlBookmarks.Remove(configData.Drones["monument"].name);

                for (int i = 1; i < 10; i++)
                {
                    string testName = configData.Drones["road"].name + i.ToString();
                    if (station.controlBookmarks.ContainsKey(testName)) station.controlBookmarks.Remove(testName);
                    testName = configData.Drones["ring"].name + i.ToString();
                    if (station.controlBookmarks.ContainsKey(testName)) station.controlBookmarks.Remove(testName);
                    testName = configData.Drones["monument"].name + i.ToString();
                    if (station.controlBookmarks.ContainsKey(testName)) station.controlBookmarks.Remove(testName);
                }
            }

            object x = RoadFinder?.CallHook("GetRoads");
            if (x != null)
            {
                string json = JsonConvert.SerializeObject(x);
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
                ["boughtdrone"] = "Drone purchased for {0} points.",
                ["droneprice"] = "Drone must be purchased for {0} points.",
                ["helptext"] = "To spawn a drone, type /drone NAM.EOFDRONE",
                ["heading"] = "Drone headed to {0}",
                ["nextpoint"] = "next point."
            }, this);
        }

        private void CheckDrones(bool startup = false)
        {
            Puts("Checking drones");
            if (startup)
            {
                Drone[] drones = UnityEngine.Object.FindObjectsOfType<Drone>();
                if (drones != null)
                {
                    Dictionary<string, DroneInfo>.KeyCollection sdrones = configData.Drones.Keys;
                    foreach (Drone drone in drones)
                    {
                        RemoteControlEntity rc = drone.GetComponent<RemoteControlEntity>();
                        if (rc?.rcIdentifier == "NONE" && !drone.IsDestroyed)
                        {
                            UnityEngine.Object.Destroy(drone.gameObject);
                        }
                        else if (rc != null && sdrones.Contains(rc.rcIdentifier))
                        {
                            if (!drone.IsDestroyed) UnityEngine.Object.Destroy(drone.gameObject);
                            if (!rc.IsDestroyed) UnityEngine.Object.Destroy(rc.gameObject);
                            RemoveDroneFromCS(rc.rcIdentifier);
                        }
                    }
                }
            }

            // Missing network group
            BaseEntity.saveList.RemoveWhere(p => !p);
            BaseEntity.saveList.RemoveWhere(p => p == null);
            // Sleeping automaton controllers
            foreach (BasePlayer pl in BasePlayer.sleepingPlayerList)
            {
                if (pl == null) continue;
                foreach (KeyValuePair<string, DroneInfo> di in configData.Drones)
                {
                    if (pl.displayName.Contains(di.Value.name) || pl.displayName.Contains("NONE Pilot"))
                    {
                        if (!pl.IsDestroyed) UnityEngine.Object.Destroy(pl.gameObject);
                    }
                }
            }

            // Broken/dead drones
            foreach (KeyValuePair<string, BaseEntity> d in new Dictionary<string, BaseEntity>(drones))
            {
                BaseEntity drone = d.Value;
                //(drone as BaseCombatEntity).diesAtZeroHealth = true;

                if (drone.IsDestroyed || drone.IsBroken())
                {
                    DroneNav dnav = drone.gameObject.GetComponent<DroneNav>();
                    if (dnav?.player?.IsDestroyed == false)
                    {
                        UnityEngine.Object.Destroy(dnav.player.gameObject);
                    }
                    UnityEngine.Object.Destroy(drone.gameObject);
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

        private void Unload()
        {
            // KILL ALL DRONES OF ANY ORIGIN
            //foreach (Drone d in UnityEngine.Object.FindObjectsOfType<Drone>())
            //{
            //    UnityEngine.Object.Destroy(d.gameObject);
            //}
            foreach (DroneNav d in UnityEngine.Object.FindObjectsOfType<DroneNav>())
            {
                UnityEngine.Object.Destroy(d.gameObject);
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
//                if (configData.Options.setPlayerDroneInCS)
//                {
//                    if (dnav.rc != null)
//                    {
//                        var stations = UnityEngine.Object.FindObjectsOfType<ComputerStation>();
//                        if (stations.Count() > 0)
//                        {
//                            foreach (var station in stations)
//                            {
//                                if (station.OwnerID == drone.OwnerID || IsFriend(station.OwnerID, drone.OwnerID))
//                                {
//                                    if (station.controlBookmarks.ContainsKey(dnav.rc.rcIdentifier))
//                                    {
//                                        station.controlBookmarks.Remove(dnav.rc.rcIdentifier);
//                                    }
//                                }
//                            }
//                        }
//                    }
//                }
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
            RemoteControlEntity rc = drone.GetComponent<RemoteControlEntity>();
            if (rc == null) return;

            foreach (ComputerStation station in stations)
            {
                if (station.OwnerID != drone.OwnerID && !IsFriend(station.OwnerID, drone.OwnerID)) continue;

                if (station.controlBookmarks.ContainsKey(rc.rcIdentifier))
                {
                    station.controlBookmarks.Remove(rc.rcIdentifier);
                }
                station.controlBookmarks.Add(rc.rcIdentifier, drone.net.ID);
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

        private void OnEntityKill(Drone drone)
        {
            DroneNav dnav = drone.gameObject.GetComponentInParent<DroneNav>();
            if (dnav != null)
            {
                string oldName = dnav.rc.rcIdentifier;
                Puts($"Drone {oldName} died");
                RemoveDroneFromCS(oldName);
                UnityEngine.Object.Destroy(dnav.gameObject);

                foreach (KeyValuePair<string, BaseEntity> item in drones.Where(kvp => kvp.Value == drone).ToList())
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
            foreach(KeyValuePair<string, DroneInfo> drone in configData.Drones)
            {
                string realName = "";
                foreach (KeyValuePair<string, BaseEntity> d in drones)
                {
                    if (d.Key.StartsWith(drone.Value.name))
                    {
                        realName = d.Key;
                    }
                }
                if (realName == "") continue;
                BaseEntity realDrone = drones[realName];
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
                        RemoteControlEntity rcd = d.GetComponent<RemoteControlEntity>();
                        if (rcd != null && rcd.rcIdentifier == args[0])
                        {
                            Puts($"Killing {rcd.rcIdentifier}");
                            RemoveDroneFromCS(rcd.rcIdentifier);
                            UnityEngine.Object.Destroy(d.gameObject);
                            UnityEngine.Object.Destroy(rcd.gameObject);
                        }
                    }
                }
                return;
            }

            string droneName = Lang("drone");
            if (args[0] != null) droneName = args[0];

            BasePlayer player = iplayer.Object as BasePlayer;

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

            BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            drone.OwnerID = player.userID;
            RemoteControlEntity rc = drone.GetComponent<RemoteControlEntity>();

            int i = 1;
            string newName = droneName;
            while (RemoteControlEntity.IDInUse(newName))
            {
                newName = droneName + i.ToString();
                i++;
                if (i > 10) break;
            }
            rc?.UpdateIdentifier(newName);
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
                        Drone[] dnav = UnityEngine.Object.FindObjectsOfType<Drone>();
                        Dictionary<string, DroneInfo>.KeyCollection sdrones = configData.Drones.Keys;
                        foreach (Drone d in dnav)
                        {
                            RemoteControlEntity rcd = d.GetComponent<RemoteControlEntity>();
                            if (rcd != null && rcd.rcIdentifier == args[0])
                            {
                                UnityEngine.Object.Destroy(rcd.gameObject);
                            }
                            UnityEngine.Object.Destroy(d.gameObject);
                        }
                    }
                    return;
                }
                else if (args[0] == "list")
                {
                    Drone[] dnav = UnityEngine.Object.FindObjectsOfType<Drone>();
                    Dictionary<string, DroneInfo>.KeyCollection sdrones = configData.Drones.Keys;
                    foreach (Drone d in dnav)
                    {
                        RemoteControlEntity rcd = d.GetComponent<RemoteControlEntity>();
                        if (rcd?.rcIdentifier.Contains("SPY") == true)
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

                BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, pl.transform.rotation);
                DroneNav obj = drone.gameObject.AddComponent<DroneNav>();

                BasePlayer player = iplayer.Object as BasePlayer;
                obj.ownerid = player.userID;
                obj.SetType(DroneType.Spy);
                obj.SetPlayerTarget(pl);

                RemoteControlEntity rc = drone.GetComponent<RemoteControlEntity>();
                string plName = pl.displayName ?? pl.UserIDString;
                string droneName = $"SPY{plName}";

                int i = 1;
                string newName = droneName;
                while (RemoteControlEntity.IDInUse(newName))
                {
                    newName = droneName + i.ToString();
                    i++;
                    if (i > 10) break;
                }
                rc?.UpdateIdentifier($"{newName}");

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
                if (args[0] == "kill")
                {
                    DroneNav nav = drones[configData.Drones["monument"].name].GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        UnityEngine.Object.Destroy(nav.gameObject);
                    }
                }
                else if (args[0] == "status")
                {
                    DroneNav nav = drones[configData.Drones["monument"].name].GetComponent<DroneNav>();
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
                        Message(iplayer, "mdstatus", nav.rc.rcIdentifier, curr, nav.currentMonument, tgt, nav.currentMonSize);
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
                    DroneNav nav = drones[configData.Drones["monument"].name].GetComponent<DroneNav>();
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

            BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
            obj.type = DroneType.MonAll;
            drone.Spawn();
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
                if (args[0] == "kill")
                {
                    DroneNav nav = drones[configData.Drones["road"].name].GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        UnityEngine.Object.Destroy(nav.drone.gameObject);
                    }
                }
                else if (args[0] == "status")
                {
                    DroneNav nav = drones[configData.Drones["road"].name].GetComponent<DroneNav>();
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

                        Message(iplayer, "rdstatus", nav.rc.rcIdentifier, curr, $"{nav.currentRoadName} ", tgt);
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
                    DroneNav nav = drones["road"].GetComponent<DroneNav>();
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

            BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
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
                if (args[0] == "kill")
                {
                    DroneNav nav = drones[configData.Drones["ring"].name].GetComponent<DroneNav>();
                    if (nav != null)
                    {
                        UnityEngine.Object.Destroy(nav.drone.gameObject);
                    }
                }
                else if (args[0] == "status")
                {
                    DroneNav nav = drones[configData.Drones["ring"].name].GetComponent<DroneNav>();
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
                        Message(iplayer, "ringstatus", nav.rc.rcIdentifier, curr);
                    }
                }
                return;
            }
            BasePlayer player = iplayer.Object as BasePlayer;
            Vector3 target = player.transform.position;
            target.y = TerrainMeta.HeightMap.GetHeight(player.transform.position) + configData.Options.minHeight;

            BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, player.transform.rotation);
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.ownerid = player.userID;
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

            if (input.current.buttons == configData.Options.ActivationCode && Chute != null)
            {
                if (pguis.ContainsKey(player.userID))
                {
                    ComputerStation station = player.GetMounted() as ComputerStation;
                    Drone drone = station.currentlyControllingEnt.Get(true).GetComponent<Drone>();
                    if (drone != null)
                    {
                        Vector3 newPos = new Vector3(drone.transform.position.x, drone.transform.position.y + 10f, drone.transform.position.z);
                        station.StopControl(player);
                        station.DismountPlayer(player, true);
                        station.SendNetworkUpdateImmediate();

                        pguis.Remove(player.userID);
                        Teleport(player, newPos);
                        Chute?.CallHook("ExternalAddPlayerChute", player, null);
                        if (drone.OwnerID == player.userID)
                        {
                            DoLog($"Killing player drone.");
                            UnityEngine.Object.Destroy(drone.gameObject);
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

            if (player.net?.connection != null) player.ClientRPCPlayer(null, player, "StartLoading");
        }

        private object OnBookmarkAdd(ComputerStation computerStation, BasePlayer player, string bookmarkName)
        {
            DoLog($"Player {player.UserIDString} added bookmark {bookmarkName}");
            return null;
        }

        private object OnBookmarkControl(ComputerStation station, BasePlayer player, string bookmarkName, IRemoteControllable remoteControllable)
        {
            if (player == null) return null;
            BaseEntity ent = remoteControllable.GetEnt();
            Drone drone = ent as Drone;
            if (drone != null)
            {
                DroneNav obj = drone.GetComponent<DroneNav>();
                if (obj != null)
                {
                    if (drone.OwnerID > 0 && obj.player?.IsDestroyed == false)
                    {
                        UnityEngine.Object.Destroy(obj.player.gameObject);
                    }
                    if (!string.IsNullOrEmpty(drone.rcIdentifier))
                    {
                        DoLog($"Player {player.UserIDString} now controlling drone {drone.rcIdentifier}, owned by {drone.OwnerID}");
                    }

                    if (obj.currentRoad != null)
                    {
                        string roadname = string.IsNullOrEmpty(obj.currentRoadName) ? Lang("nexpoint") : obj.currentRoadName;
                        DroneGUI(player, obj, Lang("heading", roadname, roadname));
                    }
                    else if (obj.currentMonument.Length > 0)
                    {
                        DroneGUI(player, obj, Lang("heading", obj.currentMonument, obj.currentMonument));
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(drone.rcIdentifier))
                    {
                        DroneGUI(player, null, $"{player.displayName}'s {Lang("drone")}, {drone.rcIdentifier}.");
                    }
                    else
                    {
                        DroneGUI(player, null, player.displayName + "'s " + Lang("drone"));
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
            ConfigData config = new ConfigData
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
                    DisplayMapMarkersOnServerDrones = true
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
            public double droneCost;
            public bool DisplayMapMarkersOnServerDrones;
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
            public string droneName;
            public string currentRoadName;
            public string currentMonument;
            public float currentMonSize;
            public Vector3 current;
            public Quaternion rotation;
            public Vector3 direction;
            public Vector3 target = Vector3.zero;
            public Vector3 last = Vector3.zero;
            public GameObject slop = new GameObject();

            public Stopwatch stuckTimer;

            public int whichPoint;
            public int totalPoints;

            public bool grounded = true;
            public bool started;
            public bool ending;
            public int markerdelay;
            public MapMarkerGenericRadius marker = null;

            private struct DroneInputState
            {
                public Vector3 movement;
                public float throttle;
                public float pitch;
                public float yaw;

                public void Reset()
                {
                    movement = Vector3.zero;
                    pitch = 0f;
                    yaw = 0f;
                }
            }

            private void Awake()
            {
                Instance.DoLog("Awake()");
                drone = GetComponent<Drone>();
                rc = GetComponent<RemoteControlEntity>();
                stuckTimer = new Stopwatch();
                stuckTimer.Stop();
                enabled = false;
            }

            private void OnDestroy()
            {
                if (!player.IsDestroyed) { Destroy(player.gameObject); player.Kill(); }
                if (!drone.IsDestroyed) { Destroy(drone.gameObject); drone.Kill(); }
                if (!rc.IsDestroyed) { Destroy(rc.gameObject); rc.Kill(); }
                if (!marker.IsDestroyed) { Destroy(marker.gameObject); marker.Kill(); }
            }

            private void Start()
            {
                droneid = drone.net.ID;
            }

            public void SetPlayerTarget(BasePlayer pl)
            {
                if (type != DroneType.Spy) return;
                targetPlayer = pl;
            }

            public string SetName(string name)
            {
                Instance.DoLog($"Trying to set drone '{rc.rcIdentifier}' name to '{name}'");
                int i = 1;
                string newName = name;
                while (RemoteControlEntity.IDInUse(newName))
                {
                    newName = name + i.ToString();
                    i++;
                    if (i > 10) break;
                }
                rc.UpdateIdentifier(newName, true);
                Instance.DoLog($"Set name to '{newName}'");
                droneName = newName;
                return newName;
            }

            public void SetType(DroneType type)
            {
                Instance.DoLog($"Set drone {rc.rcIdentifier} type to {type}");
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
                Instance.DoLog($"{rc.rcIdentifier} road points {cnt}");
                List<Vector3> newpts = new List<Vector3>();

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
                Instance.DoLog($"{rc.rcIdentifier} road points changed to {newpts.Count}");
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
                // You might be tempted to switch to FixedUpdate, but then the viewing player will take over flight...
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
                Quaternion lookrotation = Quaternion.LookRotation(direction);

                //Instance.DoLog($"{rc.rcIdentifier} trying to move to target point {whichPoint.ToString()} {target.ToString()}, currently at {current.ToString()}");
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

            private void MoveToMonument()
            {
                if (drone == null) return;
                if (grounded) return;

                current = drone.transform.position;
                target = new Vector3(Instance.monPos[currentMonument].x, GetHeight(Instance.monPos[currentMonument]), Instance.monPos[currentMonument].z);

                direction = (target - current).normalized;
                Quaternion lookrotation = Quaternion.LookRotation(direction);

                float monsize = Mathf.Max(25, currentMonSize);
                if (Vector3.Distance(current, target) < monsize)
                {
                    Instance.DoLog($"Within {monsize}m of {currentMonument}.  Switching...", true);
                    GetMonument();
                    target = Instance.monPos[currentMonument];
                    target.y = GetHeight(target);
                }
                drone.transform.LookAt(target);
                DoMoveDrone(direction);
            }

            private void FollowPlayer()
            {
                if (drone == null) return;
                if (targetPlayer == null) return;

                current = drone.transform.position;
                target = targetPlayer.transform.position;
                direction = (target - current).normalized;

                drone.transform.LookAt(target);
                DoMoveDrone(direction);
            }

            private void DoMoveDrone(Vector3 direction)
            {
                TakeControl();

                int x = 0;
                int z = 0;

                InputMessage message = new InputMessage() { buttons = 0 };

                bool toolow = TooLow(current);
                bool above = DangerAbove(current);
                bool frontcrash = DangerFront(current);
                float terrainHeight = TerrainMeta.HeightMap.GetHeight(current);

                // Flip if necessary
                if (Vector3.Dot(Vector3.up, drone.transform.up) < 0.1f)
                {
                    Instance.DoLog($"{rc.rcIdentifier} was tipping over", true);
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
                else if (current.y > terrainHeight + (Instance.configData.Options.minHeight * 2) + 5 && !frontcrash)
                {
                    // Move Down
                    Instance.DoLog($"{rc.rcIdentifier} Moving DOWN {current.y}", true);
                    message.buttons = 64;
                }

                //if (BigRock(target))
                //{
                //    // Move up, allow forward below
                //    message.buttons = 128;
                //}
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

                if (!frontcrash && !toolow)
                {
                    // Move FORWARD
                    message.buttons += 2;
                    x = 1;
                }

                message.mouseDelta.x = x;
                message.mouseDelta.z = z;

                InputState input = new InputState() { current = message };
                rc.UserInput(input, player);
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

                System.Random rand = new System.Random();
                List<string> roadlist = new List<string>(Instance.roads.Keys);
                string croad = roadlist[rand.Next(cnt)];
                currentRoad = Instance.roads[croad];

                Instance.DoLog($"Set {rc.rcIdentifier} road to {currentRoad}");
                target = currentRoad.points[0];
                target.y = Instance.configData.Options.minHeight;
            }

            private void GetMonument()
            {
                // Pick a random monument if currentMonument is null
                int cnt = Instance.monNames.Count;
                List<string> monlist = new List<string>(Instance.monSize.Keys);

                System.Random rand = new System.Random();
                int index = rand.Next(cnt);
                string cmon = monlist[index];
                currentMonument = Instance.monNames[index];
                currentMonSize = Instance.monSize[cmon].z;

                Instance.DoLog($"Set {rc.rcIdentifier} monument to {currentMonument} {currentMonSize}", true);
                target = Instance.monPos[currentMonument];
                target.y = Instance.configData.Options.minHeight;

                Instance.OnDroneNavChange(this, currentMonument);
            }

            private void TakeControl()
            {
                if (!drone.IsBeingControlled)
                {
                    if (player == null)
                    {
                        player = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", Vector3.zero, new Quaternion()).ToPlayer();
                        player.Spawn();
                        player.displayName = rc.rcIdentifier + " Pilot";
                        AntiHack.ShouldIgnore(player);
                        player._limitedNetworking = true;
                        player.EnablePlayerCollider();
                        List<Connection> connections = Net.sv.connections.Where(con => con.connected && con.isAuthenticated && con.player is BasePlayer && con.player != player).ToList();
                        player.OnNetworkSubscribersLeave(connections);
                    }
                    rc.InitializeControl(player);
                }
            }

            #region crash_avoidance
            private bool DangerLeft(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Raycast(current, drone.transform.TransformDirection(Vector3.left), out hitinfo, 4f, buildingMask))
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
                        Instance.DoLog($"{rc.rcIdentifier} CRASH LEFT{hit} distance {d}!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool DangerRight(Vector3 tgt)
            {
                RaycastHit hitinfo;
                if (Physics.Raycast(current, drone.transform.TransformDirection(Vector3.right), out hitinfo, 4f, buildingMask))
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
                        Instance.DoLog($"{rc.rcIdentifier} CRASH RIGHT{hit} distance {d}!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool DangerAbove(Vector3 tgt)
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
                        Instance.DoLog($"{rc.rcIdentifier} FRONTAL CRASH{hit} distance {d}m!", true);
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
                        Instance.DoLog($"{rc.rcIdentifier} found {hitinfo.collider.name} in path!", true);
                        return true;
                    }
                }
                return false;
            }

            private bool TooLow(Vector3 tgt)
            {
                RaycastHit hitinfo;

                if (Physics.Raycast(current, Vector3.down, out hitinfo, 10f, groundMask) || Physics.Raycast(current, Vector3.up, out hitinfo, 10f, groundMask))
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

            System.Random rand = new System.Random();
            List<string> roadlist = new List<string>(Instance.roads.Keys);
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

            BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();

            DoLog($"SpawnRoadDrone: Moving to start of {road}...");
            obj.SetType(DroneType.Road);
            string newName = obj.SetName(configData.Drones["road"].name);
            obj.SetRoad(road);
            obj.enabled = true;
            drone.Spawn();

            //drones.Add(configData.Drones["road"].name, drone);
            drones.Add(newName, drone);
            //if (!string.IsNullOrEmpty(newName)) SetDroneInCS(configData.Drones["road"].name);
            if (!string.IsNullOrEmpty(newName)) SetDroneInCS(newName);
        }

        public void SpawnRingDrone()
        {
            if (!configData.Drones["ring"].spawn) return;
            drones.Remove(configData.Drones["ring"].name);
            if (configData.Drones["ring"].start == null) configData.Drones["ring"].start = "Road 0";

            if (!roads.ContainsKey(configData.Drones["ring"].start))
            {
                DoLog("No such road on this map :(");
                return;
            }

            string road = configData.Drones["ring"].start;
            Vector3 target = roads[road].points[0];
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();

            DoLog($"SpawnRingDrone: Moving to start of {road}...");
            obj.SetType(DroneType.Ring);
            string newName = obj.SetName(configData.Drones["ring"].name);
            obj.SetRoad(road, true);
            obj.enabled = true;
            drone.Spawn();

            //drones.Add(configData.Drones["ring"].name, drone);
            drones.Add(newName, drone);
            //if (!string.IsNullOrEmpty(newName)) SetDroneInCS(configData.Drones["ring"].name);
            if (!string.IsNullOrEmpty(newName)) SetDroneInCS(newName);
        }

        public void SpawnMonumentDrone()
        {
            if (!configData.Drones["monument"].spawn) return;
            drones.Remove(configData.Drones["monument"].name);

            Vector3 target = Vector3.zero;
            target.y = TerrainMeta.HeightMap.GetHeight(target) + configData.Options.minHeight;

            BaseEntity drone = GameManager.server.CreateEntity(droneprefab, target, new Quaternion());
            DroneNav obj = drone.gameObject.AddComponent<DroneNav>();
            obj.SetType(DroneType.MonAll);
            string newName = obj.SetName(configData.Drones["monument"].name);
            obj.enabled = true;
            drone.Spawn();

            //drones.Add(configData.Drones["monument"].name, drone);
            drones.Add(newName, drone);
            //if (success) SetDroneInCS(configData.Drones["monument"].name);
            if (!string.IsNullOrEmpty(newName)) SetDroneInCS(newName);
        }

        private void RemoveDroneFromCS(string drone)
        {
            if (!drones.ContainsKey(drone)) return;
            foreach (ComputerStation station in UnityEngine.Object.FindObjectsOfType<ComputerStation>())
            {
                if (station.controlBookmarks.ContainsKey(drone))
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
                if (!station.controlBookmarks.ContainsKey(drone))
                {
                    DoLog($"Adding drone {drone}:{drones[drone].net.ID} to CS");
                    station.controlBookmarks.Add(drone, drones[drone].net.ID);
                    station.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                }
            }
        }

        private void FindMonuments()
        {
            Vector3 extents = Vector3.zero;
            float realWidth = 0f;
            string name = null;
            bool ishapis =  ConVar.Server.level.Contains("Hapis");

            foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (monument.name.Contains("power_sub")) continue;// || monument.name.Contains("cave")) continue;
                if (monument.name.Contains("derwater")) continue;
                realWidth = 0f;
                name = null;

                if (monument.name == "OilrigAI")
                {
                    name = "Small Oilrig";
                }
                else if (monument.name == "OilrigAI2")
                {
                    name = "Large Oilrig";
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

                extents = monument.Bounds.extents;

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
            if (configData.Options.debugMovement || configData.Options.debug) Interface.Oxide.LogInfo(message);
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
                BasePlayer player = BasePlayer.FindByID(playerid);
                if (player != null && player.currentTeam != 0)
                {
                    RelationshipManager.PlayerTeam playerTeam = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                    if (playerTeam?.members.Contains(ownerid) == true)
                    {
                        return true;
                    }
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

            // Just checking balance without withdrawal - did we find anything?
            return foundmoney;
        }
        #endregion
    }
}
