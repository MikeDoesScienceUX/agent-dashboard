using UnityEngine;

/// <summary>
/// Attach this to any GameObject with a BoxCollider (IsTrigger = true)
/// placed at a hallway cross-section. It reports agent pass-throughs
/// to AnalyticsManager for flow rate measurement.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class FlowSensorTrigger : MonoBehaviour
{
    [Tooltip("Must match the sensorName in AnalyticsManager.flowSensors.")]
    public string sensorName;

    private AnalyticsManager _analytics;

    void Start()
    {
        _analytics = FindObjectOfType<AnalyticsManager>();
        if (_analytics == null)
            Debug.LogWarning($"[FlowSensor:{sensorName}] No AnalyticsManager found in scene.");

        // Ensure collider is a trigger
        var col = GetComponent<BoxCollider>();
        col.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponent<AgentController>() != null && _analytics != null)
        {
            _analytics.RecordFlowEvent(sensorName);
        }
    }
}
