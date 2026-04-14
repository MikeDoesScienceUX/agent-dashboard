using UnityEngine;

/// <summary>
/// Attach to any GameObject with a BoxCollider (IsTrigger = true) at a hallway
/// cross-section to count agent pass-throughs and report them to AnalyticsManager.
///
/// The sensorName must match an entry in AnalyticsManager.flowSensors[].sensorName.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
[AddComponentMenu("Conference Sim/Flow Sensor Trigger")]
public class FlowSensorTrigger : MonoBehaviour
{
    [Tooltip("Must match the sensorName in AnalyticsManager.flowSensors.")]
    public string sensorName;

    private AnalyticsManager _analytics;

    void Start()
    {
        if (string.IsNullOrEmpty(sensorName))
            Debug.LogWarning($"[FlowSensor] sensorName is empty on '{name}'. Flow events will be lost.", this);

        _analytics = FindFirstObjectByType<AnalyticsManager>();
        if (_analytics == null)
            Debug.LogWarning($"[FlowSensor:{sensorName}] No AnalyticsManager found in scene.", this);

        GetComponent<BoxCollider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponent<AgentController>() != null && _analytics != null)
            _analytics.RecordFlowEvent(sensorName);
    }
}
