using System;
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
        var worldObject = Spawn("Drone Demo Grid World");
        var world = worldObject.AddComponent<DroneDemoGridWorld>();
        world.Configure(
            new Vector3(-width * cellSize * 0.5f, 0f, -depth * cellSize * 0.5f),
            cellSize,
            width,
            1,
            depth,
            1 << obstacleLayer,
            1 << targetLayer
        );

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

    private void BuildTarget(DroneDemoGridWorld world, float cellSize, int targetLayer, DroneNative.DroneVec3i cell)
    {
        var target = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Register(target);
        target.name = "Target";
        target.layer = targetLayer;
        target.transform.position = world.GridToWorld(cell, 0.25f);
        target.transform.localScale = new Vector3(cellSize * 0.55f, 0.25f, cellSize * 0.55f);
        DroneDemoVisualUtility.SetRendererColor(target, new Color(1f, 0.8f, 0.05f));
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
