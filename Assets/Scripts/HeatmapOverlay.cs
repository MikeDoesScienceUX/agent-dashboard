using UnityEngine;

/// <summary>
/// Real-time density heatmap rendered onto a floor plane.
/// Samples all active agent positions every N seconds and writes a Texture2D
/// onto the target floor renderer's material.
///
/// Setup:
///   1. Add this component to ___SimController (or the floor plane itself).
///   2. Assign the floor plane's Renderer to targetRenderer.
///   3. Set floorBoundsMin/Max to match the walkable area.
///   4. The floor material must support _MainTex or _BaseMap (URP).
///
/// Press M to toggle the heatmap overlay at runtime.
/// </summary>
[AddComponentMenu("Conference Sim/Heatmap Overlay")]
public class HeatmapOverlay : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Renderer of the floor plane that will show the heatmap.")]
    public Renderer targetRenderer;

    public CrowdManager crowdManager;

    [Header("Floor Bounds (World Space)")]
    public Vector3 floorBoundsMin = new Vector3(-25f, 0f, -25f);
    public Vector3 floorBoundsMax = new Vector3( 25f, 0f,  25f);

    [Header("Heatmap Settings")]
    [Tooltip("Texture resolution in pixels. Higher = sharper but slower.")]
    [Range(64, 512)]
    public int resolution = 128;

    [Tooltip("How often the heatmap texture updates (seconds real-time).")]
    [Range(0.5f, 10f)]
    public float updateInterval = 2f;

    [Tooltip("Density value (persons/m²) that maps to the hottest colour.")]
    public float maxDensity = 3f;

    [Header("Colour Gradient")]
    public Gradient heatGradient;

    [Header("Controls")]
    public bool     showHeatmap = true;
    public KeyCode  toggleKey   = KeyCode.M;

    // ── Internals ───────────────────────────────────────────────────

    private Texture2D _tex;
    private Color[]   _pixels;
    private float     _timer;

    private float _cellW, _cellH;
    private float _floorW, _floorH;

    static readonly int MainTexProp = Shader.PropertyToID("_MainTex");
    static readonly int BaseMapProp = Shader.PropertyToID("_BaseMap");   // URP

    private Texture _originalTex;

    // ── Lifecycle ───────────────────────────────────────────────────

    void Start()
    {
        if (crowdManager == null) crowdManager = FindFirstObjectByType<CrowdManager>();

        _floorW = floorBoundsMax.x - floorBoundsMin.x;
        _floorH = floorBoundsMax.z - floorBoundsMin.z;
        _cellW  = _floorW / resolution;
        _cellH  = _floorH / resolution;

        _tex    = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
        _tex.filterMode = FilterMode.Bilinear;
        _tex.name = "HeatmapOverlay";
        _pixels = new Color[resolution * resolution];

        // Build a default gradient if none assigned
        if (heatGradient == null || heatGradient.colorKeys.Length == 0)
            BuildDefaultGradient();

        // Cache original texture so we can restore it
        if (targetRenderer != null)
        {
            _originalTex = targetRenderer.material.GetTexture(MainTexProp);
            if (_originalTex == null)
                _originalTex = targetRenderer.material.GetTexture(BaseMapProp);
        }

        RefreshHeatmap();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            showHeatmap = !showHeatmap;
            ApplyOrRemoveTexture();
        }

        if (!showHeatmap) return;

        _timer += Time.deltaTime;
        if (_timer >= updateInterval)
        {
            _timer = 0f;
            RefreshHeatmap();
        }
    }

    // ── Core ────────────────────────────────────────────────────────

    private void RefreshHeatmap()
    {
        if (targetRenderer == null) return;

        // Accumulate density per cell
        var counts = new float[resolution, resolution];

        if (crowdManager != null)
        {
            foreach (var agent in crowdManager.GetAllActiveAgents())
            {
                if (agent == null) continue;
                Vector3 p = agent.transform.position;

                int px = Mathf.FloorToInt((p.x - floorBoundsMin.x) / _cellW);
                int pz = Mathf.FloorToInt((p.z - floorBoundsMin.z) / _cellH);

                if (px >= 0 && px < resolution && pz >= 0 && pz < resolution)
                    counts[px, pz]++;
            }
        }

        // Cell area in m²
        float cellArea = Mathf.Max(_cellW * _cellH, 0.01f);

        // Write pixels
        for (int x = 0; x < resolution; x++)
        for (int z = 0; z < resolution; z++)
        {
            float density    = counts[x, z] / cellArea;
            float normalised = Mathf.Clamp01(density / Mathf.Max(maxDensity, 0.01f));
            Color c          = heatGradient.Evaluate(normalised);
            c.a = normalised > 0.01f ? Mathf.Lerp(0.0f, 0.85f, normalised) : 0f;
            _pixels[z * resolution + x] = c;
        }

        _tex.SetPixels(_pixels);
        _tex.Apply(false);

        ApplyOrRemoveTexture();
    }

    private void ApplyOrRemoveTexture()
    {
        if (targetRenderer == null) return;

        if (showHeatmap)
        {
            targetRenderer.material.SetTexture(MainTexProp, _tex);
            targetRenderer.material.SetTexture(BaseMapProp, _tex);
        }
        else
        {
            targetRenderer.material.SetTexture(MainTexProp, _originalTex);
            targetRenderer.material.SetTexture(BaseMapProp, _originalTex);
        }
    }

    private void BuildDefaultGradient()
    {
        heatGradient = new Gradient();
        heatGradient.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(new Color(0.0f, 0.0f, 0.5f), 0.00f),
                new GradientColorKey(Color.cyan,                   0.25f),
                new GradientColorKey(Color.green,                  0.50f),
                new GradientColorKey(Color.yellow,                 0.75f),
                new GradientColorKey(Color.red,                    1.00f),
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 1f),
            });
    }

    void OnDestroy()
    {
        // Restore original floor texture
        if (targetRenderer != null)
        {
            targetRenderer.material.SetTexture(MainTexProp, _originalTex);
            targetRenderer.material.SetTexture(BaseMapProp, _originalTex);
        }
        if (_tex != null) Destroy(_tex);
    }
}
