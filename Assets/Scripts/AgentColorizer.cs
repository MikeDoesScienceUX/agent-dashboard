using UnityEngine;

/// <summary>
/// Tints the agent's renderer each frame to reflect its current behavioural state.
/// Uses MaterialPropertyBlock — zero draw-call overhead, no material instancing.
///
/// Add this component to your agent prefab alongside AgentController.
/// Requires the renderer to use a shader that reads the _Color property (Standard, URP Lit, etc.).
/// </summary>
[RequireComponent(typeof(AgentController))]
[AddComponentMenu("Conference Sim/Agent Colorizer")]
public class AgentColorizer : MonoBehaviour
{
    [Header("State Colours")]
    public Color colorTransit     = new Color(0.30f, 0.65f, 1.00f); // blue
    public Color colorDwelling    = new Color(0.15f, 0.90f, 0.35f); // green
    public Color colorRoaming     = new Color(1.00f, 0.85f, 0.15f); // yellow
    public Color colorSocializing = new Color(1.00f, 0.45f, 0.80f); // pink
    public Color colorResting     = new Color(0.60f, 0.35f, 1.00f); // purple
    public Color colorExiting     = new Color(1.00f, 0.30f, 0.30f); // red

    private AgentController    _agent;
    private Renderer           _rend;
    private MaterialPropertyBlock _mpb;
    private AgentController.AgentState _lastState = (AgentController.AgentState)(-1);

    static readonly int ColorPropId = Shader.PropertyToID("_Color");
    static readonly int BaseColorPropId = Shader.PropertyToID("_BaseColor"); // URP

    void Awake()
    {
        _agent = GetComponent<AgentController>();
        _rend  = GetComponentInChildren<Renderer>();
        _mpb   = new MaterialPropertyBlock();

        if (_rend == null)
            Debug.LogWarning($"[AgentColorizer] No Renderer found on '{name}' or its children — " +
                             "state colours will not be visible. Add a MeshRenderer to the agent prefab.", this);
    }

    void LateUpdate()
    {
        if (_agent == null || _rend == null) return;

        // Only update when state actually changes
        if (_agent.CurrentState == _lastState) return;
        _lastState = _agent.CurrentState;

        // Base colour from state, slightly tinted by persona index for visual variety
        Color c = StateColor(_agent.CurrentState);
        c = Color.Lerp(c, PersonaTint(_agent.PersonaIndex), 0.18f);

        _rend.GetPropertyBlock(_mpb);
        _mpb.SetColor(ColorPropId,     c);
        _mpb.SetColor(BaseColorPropId, c); // URP
        _rend.SetPropertyBlock(_mpb);
    }

    private static Color PersonaTint(int idx) => idx switch
    {
        0 => Color.white,                       // Researcher  — neutral
        1 => new Color(1.0f, 0.8f, 0.0f),      // Networker   — gold
        2 => new Color(0.5f, 1.0f, 0.5f),      // Student     — light green
        3 => new Color(0.7f, 0.7f, 1.0f),      // Industry    — light blue
        4 => new Color(1.0f, 0.5f, 0.2f),      // BoothStaff  — orange
        _ => Color.white
    };

    private Color StateColor(AgentController.AgentState s) => s switch
    {
        AgentController.AgentState.Transit     => colorTransit,
        AgentController.AgentState.Dwelling    => colorDwelling,
        AgentController.AgentState.Roaming     => colorRoaming,
        AgentController.AgentState.Socializing => colorSocializing,
        AgentController.AgentState.Resting     => colorResting,
        AgentController.AgentState.Exiting     => colorExiting,
        _                                      => Color.white
    };
}
