using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class DroneDemoSwarmSpawner
{
    private const float DroneFlightAltitude = 3f;

    private readonly Action<GameObject> registerSpawned;

    public RuntimeAnimatorController DroneAnimationController { get; set; }

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
        int expectedDroneCount = Mathf.Clamp(droneCount, 1, DroneSwarmDemoBootstrap.MaximumDroneCount);
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
        int count = Mathf.Clamp(droneCount, 1, DroneSwarmDemoBootstrap.MaximumDroneCount);
        var starts = CreateRandomMarginStarts(world, width, depth, count);
        for (int i = 0; i < count; i++)
        {
            var drone = Spawn($"Drone {i + 1:00}");
            drone.layer = nonSensedLayer;
            var start = starts[i];
            drone.transform.position = world.GridToWorld(start, DroneFlightAltitude);

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

            var altitudeKeeper = drone.AddComponent<DroneAltitudeKeeper>();
            altitudeKeeper.Configure(world.SurfaceTerrain, DroneFlightAltitude);
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

    private static List<DroneNative.DroneVec3i> CreateRandomMarginStarts(DroneDemoGridWorld world, int width, int depth, int count)
    {
        width = Mathf.Max(1, width);
        depth = Mathf.Max(1, depth);
        var starts = new List<DroneNative.DroneVec3i>(count);
        var usedCells = new HashSet<int>();
        int marginThickness = GetStartMarginThickness(width, depth);
        int preferredPaddingCells = GetStartPaddingCells(width, depth);

        for (int paddingCells = preferredPaddingCells; paddingCells >= 1 && starts.Count < count; paddingCells--)
        {
            AddBalancedMarginStarts(world, width, depth, marginThickness, starts, usedCells, true, count, paddingCells);

            if (starts.Count < count)
            {
                AddStartsFromCandidates(world, width, starts, usedCells, BuildAllMarginCandidates(width, depth, marginThickness), true, count, paddingCells);
            }

            if (starts.Count < count)
            {
                AddStartsFromCandidates(world, width, starts, usedCells, BuildAllGridCandidates(width, depth), true, count, paddingCells);
            }
        }

        // Fallback keeps cells unique when the preferred margin is unavailable.
        if (starts.Count < count)
        {
            AddStartsFromCandidates(world, width, starts, usedCells, BuildAllGridCandidates(width, depth), false, count, 0);
        }

        return starts;
    }

    private static int GetStartMarginThickness(int width, int depth)
    {
        int shortestSide = Mathf.Min(Mathf.Max(1, width), Mathf.Max(1, depth));
        return Mathf.Clamp(shortestSide / 4, 1, 2);
    }

    private static int GetStartPaddingCells(int width, int depth)
    {
        int shortestSide = Mathf.Min(Mathf.Max(1, width), Mathf.Max(1, depth));
        return Mathf.Clamp(shortestSide / 3, 2, 4);
    }

    private static void AddBalancedMarginStarts(DroneDemoGridWorld world, int width, int depth, int marginThickness, List<DroneNative.DroneVec3i> starts, HashSet<int> usedCells, bool requireSpawnable, int targetCount, int paddingCells)
    {
        var sideCandidates = new[]
        {
            BuildMarginSideCandidates(width, depth, marginThickness, StartSide.Left),
            BuildMarginSideCandidates(width, depth, marginThickness, StartSide.Right),
            BuildMarginSideCandidates(width, depth, marginThickness, StartSide.Bottom),
            BuildMarginSideCandidates(width, depth, marginThickness, StartSide.Top),
        };
        Shuffle(sideCandidates);

        bool addedAny;
        do
        {
            addedAny = false;
            for (int sideIndex = 0; sideIndex < sideCandidates.Length && starts.Count < targetCount; sideIndex++)
            {
                addedAny |= TryTakeNextStart(world, width, starts, usedCells, sideCandidates[sideIndex], requireSpawnable, paddingCells);
            }
        }
        while (starts.Count < targetCount && addedAny);
    }

    private static List<DroneNative.DroneVec3i> BuildMarginSideCandidates(int width, int depth, int marginThickness, StartSide side)
    {
        var candidates = new List<DroneNative.DroneVec3i>();
        int minX = 0;
        int maxX = width - 1;
        int minZ = 0;
        int maxZ = depth - 1;

        switch (side)
        {
            case StartSide.Left:
                maxX = Mathf.Min(maxX, marginThickness - 1);
                break;
            case StartSide.Right:
                minX = Mathf.Max(minX, width - marginThickness);
                break;
            case StartSide.Bottom:
                maxZ = Mathf.Min(maxZ, marginThickness - 1);
                break;
            case StartSide.Top:
                minZ = Mathf.Max(minZ, depth - marginThickness);
                break;
        }

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                candidates.Add(new DroneNative.DroneVec3i(x, 0, z));
            }
        }

        Shuffle(candidates);
        return candidates;
    }

    private static List<DroneNative.DroneVec3i> BuildAllMarginCandidates(int width, int depth, int marginThickness)
    {
        var candidates = new List<DroneNative.DroneVec3i>();
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                bool inMargin = x < marginThickness
                    || x >= width - marginThickness
                    || z < marginThickness
                    || z >= depth - marginThickness;
                if (inMargin)
                {
                    candidates.Add(new DroneNative.DroneVec3i(x, 0, z));
                }
            }
        }

        Shuffle(candidates);
        return candidates;
    }

    private static List<DroneNative.DroneVec3i> BuildAllGridCandidates(int width, int depth)
    {
        var candidates = new List<DroneNative.DroneVec3i>(width * depth);
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                candidates.Add(new DroneNative.DroneVec3i(x, 0, z));
            }
        }

        Shuffle(candidates);
        return candidates;
    }

    private static bool TryTakeNextStart(DroneDemoGridWorld world, int width, List<DroneNative.DroneVec3i> starts, HashSet<int> usedCells, List<DroneNative.DroneVec3i> candidates, bool requireSpawnable, int paddingCells)
    {
        while (candidates.Count > 0)
        {
            int lastIndex = candidates.Count - 1;
            var candidate = candidates[lastIndex];
            candidates.RemoveAt(lastIndex);
            if (TryAddStart(world, width, starts, usedCells, candidate, requireSpawnable, paddingCells))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddStartsFromCandidates(DroneDemoGridWorld world, int width, List<DroneNative.DroneVec3i> starts, HashSet<int> usedCells, List<DroneNative.DroneVec3i> candidates, bool requireSpawnable, int targetCount, int paddingCells)
    {
        foreach (var candidate in candidates)
        {
            if (starts.Count >= targetCount)
            {
                return;
            }

            TryAddStart(world, width, starts, usedCells, candidate, requireSpawnable, paddingCells);
        }
    }

    private static bool TryAddStart(DroneDemoGridWorld world, int width, List<DroneNative.DroneVec3i> starts, HashSet<int> usedCells, DroneNative.DroneVec3i candidate, bool requireSpawnable, int paddingCells)
    {
        if (world != null && !world.IsInBounds(candidate))
        {
            return false;
        }

        if (candidate.x == 1 && candidate.z == 1)
        {
            return false;
        }

        int key = CellKey(candidate, width);
        if (!usedCells.Add(key))
        {
            return false;
        }

        if (requireSpawnable && !IsSpawnableStart(world, candidate))
        {
            usedCells.Remove(key);
            return false;
        }

        if (paddingCells > 0 && !HasStartPadding(starts, candidate, paddingCells))
        {
            usedCells.Remove(key);
            return false;
        }

        starts.Add(candidate);
        return true;
    }

    private static bool IsSpawnableStart(DroneDemoGridWorld world, DroneNative.DroneVec3i candidate)
    {
        if (world == null)
        {
            return true;
        }

        DroneCellState state = world.SenseCell(candidate);
        return state != DroneCellState.Blocked && state != DroneCellState.Target;
    }

    private static bool HasStartPadding(List<DroneNative.DroneVec3i> starts, DroneNative.DroneVec3i candidate, int paddingCells)
    {
        int requiredDistanceSquared = paddingCells * paddingCells;
        for (int i = 0; i < starts.Count; i++)
        {
            int dx = starts[i].x - candidate.x;
            int dz = starts[i].z - candidate.z;
            if (dx * dx + dz * dz < requiredDistanceSquared)
            {
                return false;
            }
        }

        return true;
    }

    private static int CellKey(DroneNative.DroneVec3i cell, int width) => cell.z * width + cell.x;

    private static void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int swapIndex = UnityEngine.Random.Range(0, i + 1);
            T temp = items[i];
            items[i] = items[swapIndex];
            items[swapIndex] = temp;
        }
    }

    private enum StartSide
    {
        Left,
        Right,
        Bottom,
        Top,
    }

    private GameObject CreateDroneVisual(GameObject droneModelPrefab, int nonSensedLayer)
    {
        GameObject visual;
        if (droneModelPrefab != null)
        {
            visual = UnityEngine.Object.Instantiate(droneModelPrefab);
            visual.name = "Drone Model";
            visual.AddComponent<DroneModelAnimationPlayer>().Configure(droneModelPrefab, DroneAnimationController);
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
