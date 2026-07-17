using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(DroneGridSensor))]
public sealed class DroneFoundSignalMarker : MonoBehaviour
{
    [Header("Signal Marker")]
    [SerializeField] private string markerLayerName = "Marker";
    [SerializeField] private Vector3 localPosition = new Vector3(0f, 0.4f, 0f);
    [SerializeField] private float antennaHeight = 0.01f;
    [SerializeField] private float antennaRadius = 0.005f;
    [SerializeField] private float lightRadius = 0.025f;
    [SerializeField] private Color signalColor = Color.red;

    [Header("Light Settings")]
    [SerializeField] private float pointLightIntensity = 0.15f;
    [SerializeField] private float pointLightRange = 0.3f;
    [SerializeField] private float emissionStrength = 0.5f;

    [Header("Behavior")]
    [SerializeField] private bool showOnlyWhenThisDroneDirectlyFindsTarget = true;

    private DroneGridSensor sensor;
    private DroneSwarmAgentState agentState;
    private GameObject signalRoot;
    private Material signalMaterial;
    private bool signalShown;

    private void Awake()
    {
        sensor = GetComponent<DroneGridSensor>();
        agentState = GetComponent<DroneSwarmAgentState>();

        EnsureSignalObject();
        SetSignalVisible(false);
    }

    private void OnEnable()
    {
        if (sensor != null)
        {
            sensor.TargetSensed += HandleTargetSensed;
        }
    }

    private void OnDisable()
    {
        if (sensor != null)
        {
            sensor.TargetSensed -= HandleTargetSensed;
        }
    }

    private void Update()
    {
        if (signalShown || showOnlyWhenThisDroneDirectlyFindsTarget || agentState == null || agentState.LocalMap == null)
        {
            return;
        }

        if (agentState.LocalMap.TryGetLatestTargetReport(out _))
        {
            ShowSignal();
        }
    }

    private void HandleTargetSensed(DroneGridSensor changedSensor, DroneNative.DroneVec3i targetCell)
    {
        ShowSignal();
    }

    public void ShowSignal()
    {
        signalShown = true;
        EnsureSignalObject();
        SetSignalVisible(true);
    }

    public void HideSignal()
    {
        signalShown = false;
        SetSignalVisible(false);
    }

    private void EnsureSignalObject()
    {
        if (signalRoot != null)
        {
            return;
        }

        signalRoot = new GameObject("Found Signal Antenna");
        signalRoot.transform.SetParent(transform, false);
        signalRoot.transform.localPosition = localPosition;
        signalRoot.transform.localRotation = Quaternion.identity;

        int markerLayer = LayerMask.NameToLayer(markerLayerName);
        if (markerLayer < 0)
        {
            markerLayer = gameObject.layer;
            Debug.LogWarning($"{markerLayerName} レイヤーが見つかりません。発見アンテナはドローン本体と同じLayerにします。");
        }

        signalRoot.layer = markerLayer;

        signalMaterial = CreateSignalMaterial();

        GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "Signal Pole";
        pole.layer = markerLayer;
        pole.transform.SetParent(signalRoot.transform, false);
        pole.transform.localPosition = new Vector3(0f, antennaHeight * 0.5f, 0f);
        pole.transform.localScale = new Vector3(antennaRadius, antennaHeight * 0.5f, antennaRadius);
        pole.GetComponent<Renderer>().sharedMaterial = signalMaterial;
        DestroyColliderIfExists(pole);

        GameObject light = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        light.name = "Signal Light";
        light.layer = markerLayer;
        light.transform.SetParent(signalRoot.transform, false);
        light.transform.localPosition = new Vector3(0f, antennaHeight, 0f);
        light.transform.localScale = Vector3.one * lightRadius;
        light.GetComponent<Renderer>().sharedMaterial = signalMaterial;
        DestroyColliderIfExists(light);

        Light pointLight = light.AddComponent<Light>();
        pointLight.type = LightType.Point;
        pointLight.color = signalColor;
        pointLight.range = pointLightRange;
        pointLight.intensity = pointLightIntensity;

    }

    private Material CreateSignalMaterial()
    {
        Material material = new Material(RuntimeUnlitShader.Get())
        {
            name = "Drone Found Signal Material",
            color = signalColor
        };

        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", signalColor * emissionStrength);
        }

        return material;
    }

    private static void DestroyColliderIfExists(GameObject target)
    {
        Collider collider = target.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }
    }

    private void SetSignalVisible(bool visible)
    {
        if (signalRoot != null)
        {
            signalRoot.SetActive(visible);
        }
    }

    private void OnDestroy()
    {
        if (signalMaterial != null)
        {
            Destroy(signalMaterial);
        }
    }
}