using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Singleton MonoBehaviour that makes async calls to the Claude API during agent
/// Socializing states. Generates short, contextually grounded conversation snippets
/// that reflect the agents' personas, nearby zone topics, and the current conference.
///
/// Disabled by default — enable via SimConfig.llmEnabled = true and set SimConfig.llmApiKey.
/// Alternatively, set the ANTHROPIC_API_KEY environment variable.
///
/// Budget control: SimConfig.llmCallsPerSimMinute caps the call rate regardless of how
/// many agents are socializing simultaneously. Calls beyond budget are silently dropped.
///
/// Usage: LLMConversationClient.Instance.RequestConversation(agentA, agentB, callback)
/// </summary>
[AddComponentMenu("Conference Sim/LLM Conversation Client")]
public class LLMConversationClient : MonoBehaviour
{
    // ── Singleton ────────────────────────────────────────────────────

    public static LLMConversationClient Instance { get; private set; }

    [Header("References")]
    public SimConfig    config;
    public CrowdManager crowdManager;

    // ── Internal ─────────────────────────────────────────────────────

    private const string ApiEndpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion  = "2023-06-01";

    // Budget: tracked per simulated minute
    private float _lastBudgetResetSimTime = 0f;
    private int   _callsThisSimMinute     = 0;

    // Conversation log (in-memory, up to 200 entries)
    private readonly Queue<ConversationRecord> _log = new Queue<ConversationRecord>(200);

    [Serializable]
    public struct ConversationRecord
    {
        public float  simTime;
        public string personaA;
        public string personaB;
        public string zone;
        public string topic;
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        if (config       == null) config       = ScriptableObject.CreateInstance<SimConfig>();
        if (crowdManager == null) crowdManager = FindFirstObjectByType<CrowdManager>();

        if (!config.llmEnabled)
            Debug.Log("[LLMClient] LLM conversation generation is disabled (SimConfig.llmEnabled = false).");
    }

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Requests a conversation snippet for two socializing agents.
    /// The callback receives the generated topic string (or null on failure/budget).
    /// Fire-and-forget: returns immediately; callback is invoked asynchronously.
    /// </summary>
    public void RequestConversation(AgentController agentA, AgentController agentB,
                                    Action<string> callback)
    {
        if (!config.llmEnabled) return;
        if (!CheckBudget())     return;

        string apiKey = ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogWarning("[LLMClient] No API key found. Set SimConfig.llmApiKey or " +
                             "ANTHROPIC_API_KEY environment variable.");
            return;
        }

        string prompt   = BuildPrompt(agentA, agentB);
        string zoneId   = agentA != null ? agentA.TargetSensorId : "unknown";
        string personaA = agentA != null ? agentA.PersonaName : "Unknown";
        string personaB = agentB != null ? agentB.PersonaName : "Unknown";

        StartCoroutine(CallClaudeAPI(apiKey, prompt, (topic) =>
        {
            if (!string.IsNullOrEmpty(topic))
                RecordConversation(crowdManager?.SimClock ?? 0f, personaA, personaB, zoneId, topic);
            callback?.Invoke(topic);
        }));
    }

    /// <summary>Returns recent conversation records for debugging/analytics.</summary>
    public IEnumerable<ConversationRecord> GetLog() => _log;

    // ── Budget Control ───────────────────────────────────────────────

    private bool CheckBudget()
    {
        float simTime = crowdManager != null ? crowdManager.SimClock : 0f;

        // Reset budget each simulated minute
        if (simTime - _lastBudgetResetSimTime >= 60f)
        {
            _lastBudgetResetSimTime = simTime;
            _callsThisSimMinute     = 0;
        }

        if (_callsThisSimMinute >= config.llmCallsPerSimMinute)
        {
            Debug.LogWarning($"[LLMClient] Budget exceeded ({config.llmCallsPerSimMinute}/sim-min). " +
                             "Increase SimConfig.llmCallsPerSimMinute or disable LLM for large crowds.");
            return false;
        }
        _callsThisSimMinute++;
        return true;
    }

    // ── Prompt Builder ───────────────────────────────────────────────

    private string BuildPrompt(AgentController agentA, AgentController agentB)
    {
        string personaA = agentA != null ? agentA.PersonaName : "Conference attendee";
        string personaB = agentB != null ? agentB.PersonaName : "Conference attendee";
        string zone     = agentA != null ? agentA.TargetSensorId : "the conference floor";

        // Fetch zone topics from CrowdManager
        string topicStr = "liver disease";
        if (crowdManager != null && agentA != null)
        {
            string[] topics = crowdManager.GetZoneTopics(agentA.TargetSensorId);
            if (topics != null && topics.Length > 0)
                topicStr = string.Join(", ", topics);
        }

        // Day phase context
        string phaseContext = "";
        if (crowdManager != null && config.dayPhases != null)
        {
            int idx = crowdManager.CurrentDayPhaseIndex;
            if (idx >= 0 && idx < config.dayPhases.Length)
                phaseContext = $" It is currently the {config.dayPhases[idx].name} session.";
        }

        return $"You are generating a brief, realistic conference hallway conversation at EASL 2026 " +
               $"(a liver disease research conference).{phaseContext}\n\n" +
               $"Person A: {personaA}\n" +
               $"Person B: {personaB}\n" +
               $"Location: near exhibit zone '{zone}' covering topics: {topicStr}\n\n" +
               $"Write ONE sentence (max 20 words) that captures what they are talking about. " +
               $"Be specific to the conference context. No quotation marks. No speaker labels.";
    }

    // ── API Call ─────────────────────────────────────────────────────

    private IEnumerator CallClaudeAPI(string apiKey, string prompt, Action<string> callback)
    {
        // Build JSON payload using simple string formatting to avoid a JSON library dependency
        string escapedPrompt = prompt
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");

        string jsonBody = "{" +
            $"\"model\":\"{config.llmModel}\"," +
            "\"max_tokens\":60," +
            "\"messages\":[{\"role\":\"user\",\"content\":\"" + escapedPrompt + "\"}]" +
            "}";

        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

        using var request = new UnityWebRequest(ApiEndpoint, "POST");
        request.uploadHandler   = new UploadHandlerRaw(bodyBytes);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type",        "application/json");
        request.SetRequestHeader("x-api-key",           apiKey);
        request.SetRequestHeader("anthropic-version",   ApiVersion);
        request.timeout = 10;

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[LLMClient] API error: {request.error}");
            callback?.Invoke(null);
            yield break;
        }

        string responseText = ExtractContentText(request.downloadHandler.text);
        callback?.Invoke(responseText);
    }

    /// <summary>Minimal JSON parser for the Anthropic response content[0].text field.</summary>
    private static string ExtractContentText(string json)
    {
        try
        {
            // Find "content":[{"type":"text","text":"<value>"}]
            int contentIdx = json.IndexOf("\"content\"", StringComparison.Ordinal);
            if (contentIdx < 0) return null;

            int textKeyIdx = json.IndexOf("\"text\"", contentIdx, StringComparison.Ordinal);
            if (textKeyIdx < 0) return null;

            int colonIdx = json.IndexOf(':', textKeyIdx);
            if (colonIdx < 0) return null;

            int quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return null;

            int quoteEnd = quoteStart + 1;
            while (quoteEnd < json.Length)
            {
                if (json[quoteEnd] == '"' && json[quoteEnd - 1] != '\\') break;
                quoteEnd++;
            }

            if (quoteEnd >= json.Length) return null;
            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1)
                       .Replace("\\n", " ")
                       .Replace("\\\"", "\"")
                       .Trim();
        }
        catch { return null; }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private string ResolveApiKey()
    {
        if (!string.IsNullOrEmpty(config.llmApiKey)) return config.llmApiKey;
        return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    }

    private void RecordConversation(float t, string pA, string pB, string zone, string topic)
    {
        if (_log.Count >= 200) _log.Dequeue();
        _log.Enqueue(new ConversationRecord
        {
            simTime  = t,
            personaA = pA,
            personaB = pB,
            zone     = zone,
            topic    = topic
        });
    }
}
