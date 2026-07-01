using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class DroneDemoSwarmSpawner
{
    private const float DroneFlightAltitude = 3f;

    private readonly Action<GameObject> registerSpawned;

    public DroneDemoSwarmSpawner(Action<GameObject> registerSpawned)
    {
        this.registerSpawned = registerSpawned;
    }

    public DroneDemoSwarmSpawnResult Build(
        DroneDemoGridWorld world,
        int width,
        int depth,
        int droneCount,
        GameObject droneModelPrefab,
        int sensorRadius,
        float communicationRadius,
        DroneNative.PlannerType plannerType,
        int nonSensedLayer,
        Action<DroneGridSensor, DroneNative.DroneVec3i> targetSensedHandler)
    {
        var explorers = new List<DroneFrontierExplorer>();
        var communicationNodes = new List<DroneCommunicationNode>();
        int expectedDroneCount = Mathf.Clamp(droneCount, 1, 12);
        var commandResult = BuildCommand(world, width, depth, expectedDroneCount, communicationRadius, plannerType, nonSensedLayer, communicationNodes);
        BuildDrones(world, width, depth, droneCount, droneModelPrefab, sensorRadius, communicationRadius, plannerType, nonSensedLayer, targetSensedHandler, explorers, communicationNodes);
        return new DroneDemoSwarmSpawnResult(explorers, communicationNodes, commandResult.CommandState, commandResult.CommandRoutePlanner);
    }

    private CommandSpawnResult BuildCommand(DroneDemoGridWorld world, int width, int depth, int expectedDroneCount, float communicationRadius, DroneNative.PlannerType plannerType, int nonSensedLayer, List<DroneCommunicationNode> communicationNodes)
    {
        var command = Spawn("Command");
        command.layer = nonSensedLayer;
        command.transform.position = world.GridToWorld(new DroneNative.DroneVec3i(1, 0, 1), 0.35f);
        var commandState = command.AddComponent<DroneSwarmAgentState>();
        commandState.DroneId = 0;
        commandState.ConfigureSwarmMembership(expectedDroneCount);
        commandState.ConfigureMap(width, 1, depth);

        var node = command.AddComponent<DroneCommunicationNode>();
        node.Configure(communicationRadius, true);
        communicationNodes.Add(node);

        var routePlanner = command.AddComponent<DroneCommandRoutePlanner>();
        routePlanner.World = world;
        routePlanner.PlannerType = plannerType;

        var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Register(visual);
        visual.name = "Command Visual";
        visual.layer = nonSensedLayer;
        visual.transform.SetParent(command.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localScale = new Vector3(0.55f, 0.16f, 0.55f);
        DroneDemoVisualUtility.SetRendererColor(visual, new Color(0.88f, 0.88f, 0.92f));
        return new CommandSpawnResult(commandState, routePlanner);
    }

    private void BuildDrones(DroneDemoGridWorld world, int width, int depth, int droneCount, GameObject droneModelPrefab, int sensorRadius, float communicationRadius, DroneNative.PlannerType plannerType, int nonSensedLayer, Action<DroneGridSensor, DroneNative.DroneVec3i> targetSensedHandler, List<DroneFrontierExplorer> explorers, List<DroneCommunicationNode> communicationNodes)
    {
        var starts = new[]
        {
            new DroneNative.DroneVec3i(2, 0, 2),
            new DroneNative.DroneVec3i(2, 0, depth - 3),
            new DroneNative.DroneVec3i(width / 2, 0, 2),
            new DroneNative.DroneVec3i(4, 0, depth / 2),
            new DroneNative.DroneVec3i(width - 5, 0, 2),
            new DroneNative.DroneVec3i(1, 0, depth / 2),
            new DroneNative.DroneVec3i(width / 3, 0, depth - 2),
            new DroneNative.DroneVec3i(width - 7, 0, depth - 2),
        };

        int count = Mathf.Clamp(droneCount, 1, 12);
        for (int i = 0; i < count; i++)
        {
            var drone = Spawn($"Drone {i + 1:00}");
            drone.layer = nonSensedLayer;
            var start = starts[i % starts.Length];
            drone.transform.position = world.GridToWorld(start, DroneFlightAltitude) + new Vector3(0f, 0f, (i / starts.Length) * 0.15f);

            var state = drone.AddComponent<DroneSwarmAgentState>();
            state.DroneId = i + 1;
            state.ConfigureSwarmMembership(count);
            state.ConfigureMap(width, 1, depth);

            var sensor = drone.AddComponent<DroneGridSensor>();
            sensor.Configure(world, sensorRadius);
            if (targetSensedHandler != null)
            {
                sensor.TargetSensed += targetSensedHandler;
            }

            var motor = drone.AddComponent<DroneLocalAvoidanceMotor>();
            var node = drone.AddComponent<DroneCommunicationNode>();
            node.Configure(communicationRadius, false);
            communicationNodes.Add(node);

            var explorer = drone.AddComponent<DroneFrontierExplorer>();
            explorer.ConfigureHomeCell(start);
            explorer.PlannerType = plannerType;
            explorers.Add(explorer);

            drone.AddComponent<DroneAltitudeKeeper>();
            drone.AddComponent<DroneFoundSignalMarker>();
            drone.AddComponent<DroneMissionEndReporter>();

            var visual = CreateDroneVisual(droneModelPrefab, nonSensedLayer);
            visual.transform.SetParent(drone.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            if (droneModelPrefab == null)
            {
                visual.transform.localScale = Vector3.one * Mathf.Max(0.35f, motor.DroneRadius * 1.8f);
                DroneDemoVisualUtility.SetRendererColor(visual, Color.Lerp(new Color(0.1f, 0.5f, 1f), new Color(0.1f, 1f, 0.65f), i / Mathf.Max(1f, count - 1f)));
            }
        }
    }

    private GameObject CreateDroneVisual(GameObject droneModelPrefab, int nonSensedLayer)
    {
        GameObject visual;
        if (droneModelPrefab != null)
        {
            visual = UnityEngine.Object.Instantiate(droneModelPrefab);
            visual.name = "Drone Model";
            visual.AddComponent<DroneModelAnimationPlayer>().Configure(droneModelPrefab);
        }
        else
        {
            visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "Drone Visual";
        }

        Register(visual);
        SetLayerRecursively(visual, nonSensedLayer);
        return visual;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        for (int i = 0; i < root.transform.childCount; i++)
        {
            SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
        }
    }

    private GameObject Spawn(string objectName)
    {
        var instance = new GameObject(objectName);
        Register(instance);
        return instance;
    }

    private void Register(GameObject instance) => registerSpawned?.Invoke(instance);

    private readonly struct CommandSpawnResult
    {
        public CommandSpawnResult(DroneSwarmAgentState commandState, DroneCommandRoutePlanner commandRoutePlanner)
        {
            CommandState = commandState;
            CommandRoutePlanner = commandRoutePlanner;
        }

        public DroneSwarmAgentState CommandState { get; }
        public DroneCommandRoutePlanner CommandRoutePlanner { get; }
    }
}

public readonly struct DroneDemoSwarmSpawnResult
{
    public DroneDemoSwarmSpawnResult(List<DroneFrontierExplorer> explorers, List<DroneCommunicationNode> communicationNodes, DroneSwarmAgentState commandState, DroneCommandRoutePlanner commandRoutePlanner)
    {
        Explorers = explorers;
        CommunicationNodes = communicationNodes;
        CommandState = commandState;
        CommandRoutePlanner = commandRoutePlanner;
    }

    public List<DroneFrontierExplorer> Explorers { get; }
    public List<DroneCommunicationNode> CommunicationNodes { get; }
    public DroneSwarmAgentState CommandState { get; }
    public DroneCommandRoutePlanner CommandRoutePlanner { get; }
}
