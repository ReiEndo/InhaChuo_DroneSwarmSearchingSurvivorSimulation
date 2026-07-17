using System;
using StarterAssets;
using UnityEngine;

public sealed class DroneDemoWorldBuilder
{
    private readonly Action<GameObject> registerSpawned;

    public DroneDemoWorldBuilder(Action<GameObject> registerSpawned)
    {
        this.registerSpawned = registerSpawned;
    }

    public DroneDemoWorldBuildResult Build(int width, int depth, float cellSize, int obstacleLayer, int targetLayer, int nonSensedLayer)
    {
        Transform targetTransform = FindTargetTransform();
        Terrain terrain = FindTerrainForTarget(targetTransform);

        // Keep the configured grid size to avoid stalling on large terrains.
        width = Mathf.Max(4, width);
        depth = Mathf.Max(4, depth);
        Vector3 gridOrigin = CalculateGridOrigin(terrain, targetTransform, width, depth, cellSize);

        var worldObject = Spawn("Drone Demo Grid World");
        var world = worldObject.AddComponent<DroneDemoGridWorld>();
        world.Configure(
            gridOrigin,
            cellSize,
            width,
            1,
            depth,
            1 << obstacleLayer,
            1 << targetLayer
        );

        if (terrain != null)
        {
            terrain.gameObject.layer = nonSensedLayer;
            world.SetTerrainTreeAvoidance(terrain, 0.75f);
        }
        else
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Register(ground);
            ground.name = "Demo Ground";
            ground.layer = nonSensedLayer;
            ground.transform.position = world.GridOrigin + new Vector3((width - 1) * cellSize, -0.06f, (depth - 1) * cellSize) * 0.5f;
            ground.transform.localScale = new Vector3(width * cellSize, 0.08f, depth * cellSize);
            DroneDemoVisualUtility.SetRendererColor(ground, new Color(0.09f, 0.1f, 0.105f));

            BuildObstacleLine(world, cellSize, obstacleLayer, 5, 2, 8, 2);
            BuildObstacleLine(world, cellSize, obstacleLayer, 10, 4, 10, 10);
            BuildObstacleLine(world, cellSize, obstacleLayer, 13, 1, 15, 1);
            BuildObstacleLine(world, cellSize, obstacleLayer, 3, 8, 8, 8);
        }

        BuildTarget(world, cellSize, targetLayer, new DroneNative.DroneVec3i(width - 3, 0, depth - 3));

        var hubObject = Spawn("Drone Communication Hub");
        var communicationHub = hubObject.AddComponent<DroneSwarmCommunicationHub>();
        return new DroneDemoWorldBuildResult(world, communicationHub);
    }

    private void BuildObstacleLine(DroneDemoGridWorld world, float cellSize, int obstacleLayer, int x0, int z0, int x1, int z1)
    {
        int dx = x1 == x0 ? 0 : (x1 > x0 ? 1 : -1);
        int dz = z1 == z0 ? 0 : (z1 > z0 ? 1 : -1);
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(z1 - z0));
        for (int i = 0; i <= steps; i++)
        {
            BuildObstacle(world, cellSize, obstacleLayer, new DroneNative.DroneVec3i(x0 + dx * i, 0, z0 + dz * i));
        }
    }

    private void BuildObstacle(DroneDemoGridWorld world, float cellSize, int obstacleLayer, DroneNative.DroneVec3i cell)
    {
        var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Register(obstacle);
        obstacle.name = $"Obstacle {cell.x},{cell.z}";
        obstacle.layer = obstacleLayer;
        obstacle.transform.position = world.GridToWorld(cell, 0.45f);
        obstacle.transform.localScale = new Vector3(cellSize * 0.86f, 0.9f, cellSize * 0.86f);
        DroneDemoVisualUtility.SetRendererColor(obstacle, new Color(0.72f, 0.16f, 0.12f));
    }

    private Terrain FindTerrainForTarget(Transform targetTransform)
    {
        Terrain[] terrains = UnityEngine.Object.FindObjectsByType<Terrain>();
        if (terrains == null || terrains.Length == 0)
        {
            return Terrain.activeTerrain;
        }

        if (targetTransform == null)
        {
            return Terrain.activeTerrain != null ? Terrain.activeTerrain : terrains[0];
        }

        Vector3 targetPosition = targetTransform.position;
        Terrain containingTerrain = null;
        float bestDistanceSquared = float.PositiveInfinity;
        foreach (Terrain terrain in terrains)
        {
            if (terrain == null || terrain.terrainData == null)
            {
                continue;
            }

            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            bool contains = targetPosition.x >= origin.x && targetPosition.x <= origin.x + size.x
                && targetPosition.z >= origin.z && targetPosition.z <= origin.z + size.z;
            Vector3 closest = new Vector3(
                Mathf.Clamp(targetPosition.x, origin.x, origin.x + size.x),
                targetPosition.y,
                Mathf.Clamp(targetPosition.z, origin.z, origin.z + size.z)
            );
            float distanceSquared = (closest - targetPosition).sqrMagnitude;
            if (contains)
            {
                return terrain;
            }

            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                containingTerrain = terrain;
            }
        }

        return containingTerrain != null ? containingTerrain : (Terrain.activeTerrain != null ? Terrain.activeTerrain : terrains[0]);
    }

    private Transform FindTargetTransform()
    {
        var playerArmature = GameObject.Find("PlayerArmature");
        if (playerArmature != null)
        {
            return playerArmature.transform;
        }

        var controller = UnityEngine.Object.FindAnyObjectByType<ThirdPersonController>();
        return controller != null ? controller.transform : null;
    }

    private Vector3 CalculateGridOrigin(Terrain terrain, Transform targetTransform, int width, int depth, float cellSize)
    {
        if (terrain == null || terrain.terrainData == null)
        {
            return new Vector3(-width * cellSize * 0.5f, 0f, -depth * cellSize * 0.5f);
        }

        Vector3 terrainOrigin = terrain.transform.position;
        Vector3 terrainSize = terrain.terrainData.size;
        Vector3 desiredOrigin = terrainOrigin;

        if (targetTransform != null)
        {
            Vector3 targetPosition = targetTransform.position;
            desiredOrigin = new Vector3(
                targetPosition.x - Mathf.Max(1, width - 3) * cellSize,
                terrainOrigin.y,
                targetPosition.z - Mathf.Max(1, depth - 3) * cellSize
            );
        }

        float gridWorldWidth = (width - 1) * cellSize;
        float gridWorldDepth = (depth - 1) * cellSize;
        float maxX = Mathf.Max(terrainOrigin.x, terrainOrigin.x + terrainSize.x - gridWorldWidth);
        float maxZ = Mathf.Max(terrainOrigin.z, terrainOrigin.z + terrainSize.z - gridWorldDepth);
        desiredOrigin.x = Mathf.Clamp(desiredOrigin.x, terrainOrigin.x, maxX);
        desiredOrigin.y = terrainOrigin.y;
        desiredOrigin.z = Mathf.Clamp(desiredOrigin.z, terrainOrigin.z, maxZ);
        return desiredOrigin;
    }

    private void BuildTarget(DroneDemoGridWorld world, float cellSize, int targetLayer, DroneNative.DroneVec3i cell)
    {
        Transform targetTransform = FindTargetTransform();
        if (targetTransform != null)
        {
            SetLayerRecursively(targetTransform.gameObject, targetLayer);
            return;
        }

        var target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Register(target);
        target.name = "Target";
        target.layer = targetLayer;
        target.transform.position = world.GridToWorld(cell, 0.25f);
        target.transform.localScale = new Vector3(cellSize * 0.55f, 0.25f, cellSize * 0.55f);
        DroneDemoVisualUtility.SetRendererColor(target, new Color(1f, 0.8f, 0.05f));
    }

    private void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        foreach (Transform child in root.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    private GameObject Spawn(string objectName)
    {
        var instance = new GameObject(objectName);
        Register(instance);
        return instance;
    }

    private void Register(GameObject instance) => registerSpawned?.Invoke(instance);
}

public readonly struct DroneDemoWorldBuildResult
{
    public DroneDemoWorldBuildResult(DroneDemoGridWorld world, DroneSwarmCommunicationHub communicationHub)
    {
        World = world;
        CommunicationHub = communicationHub;
    }

    public DroneDemoGridWorld World { get; }
    public DroneSwarmCommunicationHub CommunicationHub { get; }
}
