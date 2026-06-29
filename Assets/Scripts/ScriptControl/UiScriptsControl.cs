using UnityEngine;

public class UiScriptsControl : MonoBehaviour
{
    public DroneCameraUiController droneCameraUiController;
    public SurvivorCameraView survivorCameraView;
    public SimpleTerrainMapCamera simpleTerrainMapCamera;
    public MapTargetMarkerController mapTargetMarkerController;
    public ExplorerStaminaPanelUI explorerStaminaPanelUI;
    public ElapsedTimeUI elapsedTimeUI;

    public void UI_Start()
    {
        droneCameraUiController.DroneCameraUI_Start();
        survivorCameraView.ExplorerCamera_Setup();
        simpleTerrainMapCamera.MapCamera_Start();
        mapTargetMarkerController.MapCameraMarker_Start();
        explorerStaminaPanelUI.sutaminaUI_Start();
        elapsedTimeUI.ElapsedTimeUI_Start();
    }
}
