using System;
using UnityEngine;

public sealed class DroneSwarmMapTileRenderer
{
    private readonly Transform parent;
    private GameObject meshObject;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh;
    private Material material;
    private Color[] vertexColors = Array.Empty<Color>();
    private DroneCellState[] previousStates = Array.Empty<DroneCellState>();
    private bool[] previousVisibility = Array.Empty<bool>();
    private int cachedWidth;
    private int cachedDepth;
    private bool forceUpdate;

    public DroneSwarmMapTileRenderer(Transform parent)
    {
        this.parent = parent;
    }

    public void Rebuild(
        DroneDemoGridWorld world,
        bool enabled,
        float cellHeightOffset,
        float cellFillScale,
        bool drawUnknownCells,
        Func<DroneNative.DroneVec3i, DroneCellState> getCellState,
        Func<DroneCellState, Color> getCellColor)
    {
        Clear();

        if (!enabled || world == null)
        {
            return;
        }

        EnsureRenderObjects();
        BuildMesh(world, cellHeightOffset, cellFillScale);
        Update(world, enabled, drawUnknownCells, getCellState, getCellColor);
    }

    public void Update(
        DroneDemoGridWorld world,
        bool enabled,
        bool drawUnknownCells,
        Func<DroneNative.DroneVec3i, DroneCellState> getCellState,
        Func<DroneCellState, Color> getCellColor)
    {
        if (meshRenderer != null)
        {
            meshRenderer.enabled = enabled && world != null && mesh != null;
        }

        if (!enabled || world == null || mesh == null || vertexColors.Length == 0)
        {
            return;
        }

        if (world.Width != cachedWidth || world.Depth != cachedDepth)
        {
            forceUpdate = true;
            BuildMesh(world, 0.02f, 0.86f);
        }

        bool changed = forceUpdate;
        int index = 0;
        for (int z = 0; z < world.Depth; z++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new DroneNative.DroneVec3i(x, 0, z);
                DroneCellState state = getCellState(cell);
                bool visible = state != DroneCellState.Unknown || drawUnknownCells;

                if (forceUpdate || previousStates[index] != state || previousVisibility[index] != visible)
                {
                    Color color = visible ? getCellColor(state) : Color.clear;
                    int vertexStart = index * 4;
                    vertexColors[vertexStart] = color;
                    vertexColors[vertexStart + 1] = color;
                    vertexColors[vertexStart + 2] = color;
                    vertexColors[vertexStart + 3] = color;
                    previousStates[index] = state;
                    previousVisibility[index] = visible;
                    changed = true;
                }

                index++;
            }
        }

        if (changed)
        {
            mesh.colors = vertexColors;
        }

        forceUpdate = false;
    }

    public void MarkDirty()
    {
        forceUpdate = true;
    }

    public void Clear()
    {
        if (meshObject != null)
        {
            UnityEngine.Object.Destroy(meshObject);
        }

        if (mesh != null)
        {
            UnityEngine.Object.Destroy(mesh);
        }

        meshObject = null;
        meshFilter = null;
        meshRenderer = null;
        mesh = null;
        vertexColors = Array.Empty<Color>();
        previousStates = Array.Empty<DroneCellState>();
        previousVisibility = Array.Empty<bool>();
        cachedWidth = 0;
        cachedDepth = 0;
        forceUpdate = false;
    }

    private void EnsureRenderObjects()
    {
        if (meshObject != null)
        {
            return;
        }

        meshObject = new GameObject("Drone Map Tiles Mesh");

        if (parent != null)
        {
            meshObject.layer = parent.gameObject.layer;
        }

        meshObject.transform.SetParent(parent, false);
        meshFilter = meshObject.AddComponent<MeshFilter>();
        meshRenderer = meshObject.AddComponent<MeshRenderer>();

        if (material == null)
        {
            Shader shader = Shader.Find("Sprites/Default")
                ?? Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color");
            material = new Material(shader)
            {
                name = "Drone Map Tile Vertex Color Material"
            };
            material.renderQueue = 3000;
        }

        meshRenderer.sharedMaterial = material;
    }

    private void BuildMesh(DroneDemoGridWorld world, float cellHeightOffset, float cellFillScale)
    {
        EnsureRenderObjects();

        int cellCount = world.Width * world.Depth;
        var vertices = new Vector3[cellCount * 4];
        var triangles = new int[cellCount * 6];
        vertexColors = new Color[cellCount * 4];
        previousStates = new DroneCellState[cellCount];
        previousVisibility = new bool[cellCount];

        float halfSize = world.CellSize * cellFillScale * 0.5f;
        int cellIndex = 0;
        for (int z = 0; z < world.Depth; z++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new DroneNative.DroneVec3i(x, 0, z);
                Vector3 center = world.GridToWorld(cell, cellHeightOffset + 0.012f);
                int vertexStart = cellIndex * 4;
                int triangleStart = cellIndex * 6;

                vertices[vertexStart] = center + new Vector3(-halfSize, 0f, -halfSize);
                vertices[vertexStart + 1] = center + new Vector3(-halfSize, 0f, halfSize);
                vertices[vertexStart + 2] = center + new Vector3(halfSize, 0f, halfSize);
                vertices[vertexStart + 3] = center + new Vector3(halfSize, 0f, -halfSize);

                triangles[triangleStart] = vertexStart;
                triangles[triangleStart + 1] = vertexStart + 1;
                triangles[triangleStart + 2] = vertexStart + 2;
                triangles[triangleStart + 3] = vertexStart;
                triangles[triangleStart + 4] = vertexStart + 2;
                triangles[triangleStart + 5] = vertexStart + 3;

                vertexColors[vertexStart] = Color.clear;
                vertexColors[vertexStart + 1] = Color.clear;
                vertexColors[vertexStart + 2] = Color.clear;
                vertexColors[vertexStart + 3] = Color.clear;

                cellIndex++;
            }
        }

        if (mesh != null)
        {
            UnityEngine.Object.Destroy(mesh);
        }

        mesh = new Mesh
        {
            name = "Drone Map Tiles Mesh",
            indexFormat = vertices.Length > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.colors = vertexColors;
        mesh.RecalculateBounds();
        meshFilter.sharedMesh = mesh;

        cachedWidth = world.Width;
        cachedDepth = world.Depth;
        forceUpdate = true;
    }
}
