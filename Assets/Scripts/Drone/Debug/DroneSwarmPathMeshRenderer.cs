using System.Collections.Generic;
using UnityEngine;

public sealed class DroneSwarmPathMeshRenderer
{
    private readonly Transform parent;
    private GameObject meshObject;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh;
    private Material material;

    public DroneSwarmPathMeshRenderer(Transform parent)
    {
        this.parent = parent;
    }

    public void Update(DroneDemoGridWorld world, List<DroneFrontierExplorer> explorers, bool enabled, Color color, float heightOffset, float widthScale)
    {
        EnsureRenderObjects();

        bool hasPath = enabled && world != null && explorers != null && BuildMesh(world, explorers, color, heightOffset, widthScale);
        if (meshRenderer != null)
        {
            meshRenderer.enabled = hasPath;
        }
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
    }

    private void EnsureRenderObjects()
    {
        if (meshObject != null)
        {
            return;
        }

        meshObject = new GameObject("Drone Return Path Mesh");

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
                name = "Drone Return Path Vertex Color Material"
            };
            material.renderQueue = 3050;
        }

        meshRenderer.sharedMaterial = material;
    }

    private bool BuildMesh(DroneDemoGridWorld world, List<DroneFrontierExplorer> explorers, Color color, float heightOffset, float widthScale)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var colors = new List<Color>();
        float halfWidth = Mathf.Max(0.03f, world.CellSize * widthScale * 0.5f);

        foreach (var explorer in explorers)
        {
            if (explorer == null || !explorer.ReturningHome || explorer.Path == null || explorer.UsablePathCount <= 1)
            {
                continue;
            }

            int start = Mathf.Clamp(explorer.PathIndex, 0, explorer.UsablePathCount - 1);
            for (int i = start; i < explorer.UsablePathCount - 1; i++)
            {
                Vector3 a = world.GridToWorld(explorer.Path[i], explorer.transform.position.y + heightOffset);
                Vector3 b = world.GridToWorld(explorer.Path[i + 1], explorer.transform.position.y + heightOffset);
                Vector3 direction = b - a;
                direction.y = 0f;
                if (direction.sqrMagnitude <= Mathf.Epsilon)
                {
                    continue;
                }

                Vector3 side = Vector3.Cross(Vector3.up, direction.normalized) * halfWidth;
                int vertexStart = vertices.Count;
                vertices.Add(a - side);
                vertices.Add(a + side);
                vertices.Add(b + side);
                vertices.Add(b - side);
                colors.Add(color);
                colors.Add(color);
                colors.Add(color);
                colors.Add(color);
                triangles.Add(vertexStart);
                triangles.Add(vertexStart + 1);
                triangles.Add(vertexStart + 2);
                triangles.Add(vertexStart);
                triangles.Add(vertexStart + 2);
                triangles.Add(vertexStart + 3);
            }
        }

        if (vertices.Count == 0)
        {
            if (meshFilter != null)
            {
                meshFilter.sharedMesh = null;
            }
            return false;
        }

        if (mesh == null)
        {
            mesh = new Mesh { name = "Drone Return Path Mesh" };
        }
        else
        {
            mesh.Clear();
        }

        mesh.indexFormat = vertices.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetColors(colors);
        mesh.RecalculateBounds();
        meshFilter.sharedMesh = mesh;
        return true;
    }
}
