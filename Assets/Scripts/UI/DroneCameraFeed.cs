using UnityEngine;

[DisallowMultipleComponent]
public class DroneCameraFeed : MonoBehaviour
{
    [Header("Camera")]
    [SerializeField] private Camera droneCamera;

    [Header("View Mode")]
    [SerializeField] private bool topDownView = true;

    [Header("Top Down Camera Settings")]
    [SerializeField] private float fieldOfView = 75f;
    [SerializeField] private float minimumCameraHeight = 1.5f;
    [SerializeField] private float extraFarClipDistance = 2f;

    [Header("Manual Camera Settings")]
    [SerializeField] private Vector3 manualLocalPosition = new Vector3(0f, 0.35f, 0.6f);
    [SerializeField] private Vector3 manualLocalEulerAngles = new Vector3(15f, 0f, 0f);

    [Header("Render Texture")]
    [SerializeField] private int textureWidth = 512;
    [SerializeField] private int textureHeight = 512;
    [SerializeField] private int depthBuffer = 16;

    public Camera Camera => droneCamera;
    public RenderTexture Texture { get; private set; }

    public string DisplayName
    {
        get
        {
            DroneSwarmAgentState state = GetComponent<DroneSwarmAgentState>();

            if (state != null)
            {
                return $"Drone {state.DroneId:00}";
            }

            return gameObject.name;
        }
    }

    private void Awake()
    {
        Setup();
    }

    public void Setup()
    {
        EnsureCameraExists();
        ApplyCameraTransformAndRange();
        ConnectToSensor();
        CreateTextureIfNeeded();

        if (droneCamera != null)
        {
            droneCamera.targetTexture = Texture;
        }
    }

    private void EnsureCameraExists()
    {
        if (droneCamera != null)
        {
            return;
        }

        Transform existing = transform.Find("DroneCamera");

        if (existing != null)
        {
            droneCamera = existing.GetComponent<Camera>();
        }

        if (droneCamera == null)
        {
            GameObject cameraObject = new GameObject("DroneCamera");
            cameraObject.transform.SetParent(transform, false);
            droneCamera = cameraObject.AddComponent<Camera>();
        }

        droneCamera.enabled = true;
        droneCamera.clearFlags = CameraClearFlags.Skybox;
        droneCamera.nearClipPlane = 0.03f;

        AudioListener listener = droneCamera.GetComponent<AudioListener>();

        if (listener != null)
        {
            Destroy(listener);
        }
    }

    private void ApplyCameraTransformAndRange()
    {
        if (droneCamera == null)
        {
            return;
        }

        if (topDownView)
        {
            float cameraHeight = CalculateTopDownCameraHeight();

            droneCamera.transform.localPosition = new Vector3(0f, cameraHeight, 0f);
            droneCamera.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            droneCamera.fieldOfView = fieldOfView;
            droneCamera.farClipPlane = 1000f;
        }
        else
        {
            droneCamera.transform.localPosition = manualLocalPosition;
            droneCamera.transform.localRotation = Quaternion.Euler(manualLocalEulerAngles);

            droneCamera.fieldOfView = fieldOfView;
            droneCamera.farClipPlane = 1000f;
        }
    }

    private float CalculateTopDownCameraHeight()
    {
        DroneGridSensor sensor = GetComponent<DroneGridSensor>();

        float cellSize = 1f;
        float sensorRadius = 2f;

        if (sensor != null)
        {
            sensorRadius = Mathf.Max(1f, sensor.SensorRadius);

            if (sensor.World != null)
            {
                cellSize = Mathf.Max(0.1f, sensor.World.CellSize);
            }
        }

        float groundHalfRange = Mathf.Max(cellSize, sensorRadius * cellSize);
        float halfFovRadians = fieldOfView * 0.5f * Mathf.Deg2Rad;

        return Mathf.Max(minimumCameraHeight, groundHalfRange / Mathf.Tan(halfFovRadians));
    }

    private void ConnectToSensor()
    {
        DroneGridSensor sensor = GetComponent<DroneGridSensor>();

        if (sensor != null && droneCamera != null)
        {
            sensor.ConfigureCamera(droneCamera);
        }
    }

    private void CreateTextureIfNeeded()
    {
        if (Texture != null)
        {
            return;
        }

        Texture = new RenderTexture(
            Mathf.Max(1, textureWidth),
            Mathf.Max(1, textureHeight),
            depthBuffer,
            RenderTextureFormat.ARGB32
        );

        Texture.name = $"{DisplayName}_CameraTexture";
        Texture.Create();
    }

    private void OnDestroy()
    {
        if (droneCamera != null && droneCamera.targetTexture == Texture)
        {
            droneCamera.targetTexture = null;
        }

        if (Texture != null)
        {
            Texture.Release();
            Destroy(Texture);
        }
    }
}