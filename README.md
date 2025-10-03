# DronePatrol
Fly a drone, or watch a few special server drones

Drone Patrol spawns a few server drones:

    MonumentDrone - Travels between monuments
    RingDrone - Travels around the ring road,
    RoadDrone - Travels up and down a chosen additional road.

The plugin also allows privileged players to spawn their own drone.

Uses RoadFinder, Friends, Clans, Chute, GridAPI, Economics, ServerRewards.

The server drones should fly themselves, and the player can fly their drone.  All flight monitoring takes place from a computer station.

If at any point along the flight you want to bail out, you can via a configurable keystroke.  This requires the Chute plugin from ColonBlow.  This default keystroke is Ctrl-RightClick.

All server drones can optionally appear in everyone's computer station.  Without this they can still be added by name.  The names are configurable.

A HUD will show current target monument for the MonumentDrone, and the current road for the other server drones.  For the player drone, the drone name will be displayed.

Additionally, the target monument name will be shown for the player using debug text for 90 seconds.

Should any of the drones be destroyed along the way, they should respawn within a few seconds.

## Commands

    - /drone DRONENAME --  Spawn a player drone with name NAME.  NAME is required
    - /drone DRONENAME kill -- Attempt to kill that drone
    - /md -- Spawn the monument drone (normally would respawn on its own)
    - /md MONNAME or /md MON NAME -- Change target monument
    - /md list -- List available monuments
    - /md status -- Show current status/position
    - /rd -- Spawn a road drone (normally would respawn on its own).  Requires RoadFinder plugin.
    - /rd status -- Show current status/position
    - /fd PLAYERNAME -- Spawns a drone to follow a named player.  The drone will be named SPY{PLAYERNAME} <- Note that drone/CCTV names cannot contain special characters.  So, a drone set to follow me would be SPYRFC1920, for example.
    - /fd DRONENAME kill -- Attempt to kill that drone
    - /ringd status -- Show current status/position

## Permissions

    dronepatrol.use --  Required to spawn a player drone via /drone
    dronepatrol.admin -- Required to spawn monument or road drones via /md and /rd

## Configuration
```json
{
  "Options": {
    "debug": false,
    "debugMovement": false,
    "minHeight": 40.0,
    "ActivationCode": 2112,
    "setServerDroneInAllCS": true,
    "setPlayerDroneInCS": true,
    "playerDronesImmortal": false,
    "useFriends": false,
    "useClans": false,
    "useTeams": false,
    "useEconomics": false,
    "useServerRewards": false,
    "droneCost": 0.0,
    "DisplayMapMarkersOnServerDrones": false
  },
  "Drones": {
    "monument": {
      "name": "MonumentDrone",
      "spawn": true,
      "start": "Airfield"
    },
    "ring": {
      "name": "RingDrone",
      "spawn": true,
      "start": "Road 0"
    },
    "road": {
      "name": "RoadDrone",
      "spawn": false,
      "start": "Road 1"
    }
  },
  "Version": {
    "Major": 1,
    "Minor": 0,
    "Patch": 19
  }
}
```

    - minHeight -- Minimum ground height for flight - other checks are done to try to avoid obstacles, but this is the big one.
    - ActivationCode -- The default configuration above sets the value 2112 for the keystroke to jump out and spawn a chute (Ctrl-RightClick).
    - setServerDroneInAllCS -- If true, whenever server drones are spawned by the plugin, they will be added/updated in all player computer stations.
    - setPlayerDroneInCS -- If true will add a player-spawned drone to their owned computer stations (see the next config)
    - useFriends/useClans/useTeams -- Will add player drones to computer stations owned by the spawning players friends.
    - useEconomics/useServerRewards -- Use either of these plugins to charge for user drone spawns based on droneCost
    - droneCost -- Cost for spawning a drone, if set.  Requires either or both of the above configs.
    - Drones
        The above sections are currently limited to what you see (monument/ring/road).  In other words, adding more will not guarantee spawning those additional drones (one day)
        Please leave these in the config for now otherwise there will likely be errors.  Enable/disable as desired instead.
            name - Name of the spawned drone of each type
            spawn - true/false - whether to spawn or not
            start - Starting monument or road name.  Road names in Rust are, e.g. "Road 11", etc.
        If you set the monument start to null (no quotes), the plugin will choose one for you.  Once it gets there it will select another one to fly to.
        If you set the road drone start to null (no quotes), the plugin will choose one for you.  It will then travel from the road start point to end point and back (does not follow the road path)
        If you set the ring drone start to something other than "Road 0", e.g. null, it should correct that as it's intended use is to fly around the map in a semi-circle following the ring road (if present).

## TODO

    Further improve physics checks for crash detection, etc.
    Investigate recurring issue where a drone is not killed off properly, holds onto the drone name forever, etc.
    Fix GUI being changed for currently viewed drone but for other drones.


