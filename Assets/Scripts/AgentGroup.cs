using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight group record. A group of agents spawn together, maintain cohesion
/// via ApplySocialForces in AgentController, and dissolve when all members exit.
///
/// Created and owned by CrowdManager. AgentController holds a reference to its group.
/// </summary>
public class AgentGroup
{
    public int GroupId { get; }
    public readonly List<AgentController> Members = new List<AgentController>(6);

    public AgentGroup(int id) { GroupId = id; }

    public void Add(AgentController agent) => Members.Add(agent);

    public void Remove(AgentController agent) => Members.Remove(agent);

    public bool IsActive => Members.Count > 0;

    /// <summary>Returns the average position of all active members.</summary>
    public Vector3 GetCentroid()
    {
        Vector3 sum   = Vector3.zero;
        int     count = 0;
        foreach (var m in Members)
        {
            if (m != null && m.gameObject.activeSelf)
            { sum += m.transform.position; count++; }
        }
        return count > 0 ? sum / count : Vector3.zero;
    }

    /// <summary>
    /// Notifies all OTHER group members to start socializing (contagion behaviour).
    /// Called when one member enters the Socializing state.
    /// </summary>
    public void TriggerSocializeContagion(AgentController initiator, float probability)
    {
        foreach (var m in Members)
        {
            if (m == null || m == initiator || !m.gameObject.activeSelf) continue;
            if (m.CurrentState == AgentController.AgentState.Roaming ||
                m.CurrentState == AgentController.AgentState.Dwelling)
            {
                if (Random.value < probability)
                    m.JoinGroupSocialize();
            }
        }
    }
}
