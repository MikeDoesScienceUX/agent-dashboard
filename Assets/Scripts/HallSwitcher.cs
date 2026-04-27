using UnityEngine;

/// <summary>
/// Draws a hall-switcher panel in the top-left corner of the screen.
/// Assign one entry per hall: a display name and the Camera to activate.
/// Keyboard shortcuts: 1–9 select halls by index.
///
/// Drop this component on any scene GameObject (e.g. the CrowdManager or a
/// dedicated UI GameObject). Drag each hall's Camera into the entries array
/// in the Inspector — the switcher will enable the active camera and disable
/// all others.
/// </summary>
[AddComponentMenu("Conference Sim/Hall Switcher")]
public class HallSwitcher : MonoBehaviour
{
    [System.Serializable]
    public class HallEntry
    {
        [Tooltip("Label shown on the button (e.g. 'Hall 7', 'Hall 8.1 Mezzanine').")]
        public string displayName = "Hall";

        [Tooltip("The Camera to activate when this hall is selected.")]
        public Camera camera;
    }

    [Header("Hall Cameras")]
    public HallEntry[] halls = new HallEntry[0];

    [Header("Layout")]
    [Tooltip("Pixel margin from the top-left corner of the screen.")]
    public float marginX = 12f;
    public float marginY = 12f;

    [Tooltip("Width of each hall button in pixels.")]
    public float buttonWidth = 140f;

    [Tooltip("Height of each hall button in pixels.")]
    public float buttonHeight = 28f;

    [Tooltip("Gap between buttons in pixels.")]
    public float buttonGap = 4f;

    [Header("Controls")]
    [Tooltip("Cycles to the next hall.")]
    public KeyCode cycleKey = KeyCode.Tab;

    [Tooltip("Cycles to the previous hall.")]
    public KeyCode cyclePrevKey = KeyCode.BackQuote;

    [Tooltip("Hides / shows the switcher panel.")]
    public KeyCode togglePanelKey = KeyCode.F1;

    public bool showPanel = true;

    // ── Internal ────────────────────────────────────────────────────────
    private int _activeIndex = 0;

    private GUIStyle _btnActive;
    private GUIStyle _btnInactive;
    private GUIStyle _headerLabel;
    private Texture2D _white;
    private bool _stylesReady;

    // ── Lifecycle ────────────────────────────────────────────────────────

    void Start()
    {
        // Activate the first camera, disable the rest
        if (halls.Length > 0)
            SwitchTo(0);
    }

    void Update()
    {
        if (halls == null || halls.Length == 0) return;

        // F1 → toggle panel visibility (safe in Update; not consumed by IMGUI)
        if (Input.GetKeyDown(togglePanelKey))
            showPanel = !showPanel;

        // Number-key shortcuts: 1 = hall[0], 2 = hall[1], …
        for (int i = 0; i < halls.Length && i < 9; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                SwitchTo(i);
        }
    }

    // ── OnGUI ────────────────────────────────────────────────────────────

    void OnGUI()
    {
        if (halls == null || halls.Length == 0) return;

        // Intercept Tab / BackQuote here — IMGUI consumes both before Update ever sees them.
        if (Event.current.type == EventType.KeyDown)
        {
            if (Event.current.keyCode == cycleKey)
            {
                SwitchTo((_activeIndex + 1) % halls.Length);
                Event.current.Use();
                return;
            }
            if (Event.current.keyCode == cyclePrevKey)
            {
                SwitchTo((_activeIndex - 1 + halls.Length) % halls.Length);
                Event.current.Use();
                return;
            }
        }

        if (!showPanel) return;
        if (Event.current.type == EventType.Layout) return;

        EnsureStyles();

        float panelW = buttonWidth + 16f;
        float panelH = 28f + halls.Length * (buttonHeight + buttonGap) + 10f;
        float px = marginX;
        float py = marginY;

        // Background panel
        DrawPanel(px, py, panelW, panelH);

        float cx = px + 8f;
        float cy = py + 8f;

        // Header
        GUI.Label(new Rect(px, cy, panelW, 20f), "HALLS  [Tab] ▶  [` ] ◀", _headerLabel);
        cy += 24f;

        // Hall buttons
        for (int i = 0; i < halls.Length; i++)
        {
            bool active = (i == _activeIndex);
            GUIStyle style = active ? _btnActive : _btnInactive;

            string label = halls[i].displayName;
            // Prefix with number shortcut hint if within 1–9 range
            if (i < 9) label = $"[{i + 1}]  {label}";

            if (GUI.Button(new Rect(cx, cy, buttonWidth, buttonHeight), label, style))
                SwitchTo(i);

            cy += buttonHeight + buttonGap;
        }
    }

    // ── Switch Logic ─────────────────────────────────────────────────────

    public void SwitchTo(int index)
    {
        if (index < 0 || index >= halls.Length) return;

        _activeIndex = index;

        for (int i = 0; i < halls.Length; i++)
        {
            if (halls[i].camera == null) continue;
            halls[i].camera.enabled = (i == index);
        }

        Debug.Log($"[HallSwitcher] Switched to: {halls[index].displayName}");
    }

    /// <summary>Returns the currently active hall index.</summary>
    public int ActiveIndex => _activeIndex;

    /// <summary>Returns the currently active hall entry, or null if none.</summary>
    public HallEntry ActiveHall => halls != null && _activeIndex < halls.Length
        ? halls[_activeIndex] : null;

    // ── Drawing Helpers ──────────────────────────────────────────────────

    private void DrawPanel(float x, float y, float w, float h)
    {
        Color prev = GUI.color;
        GUI.color = new Color(0.03f, 0.03f, 0.09f, 0.90f);
        GUI.DrawTexture(new Rect(x, y, w, h), _white);
        GUI.color = new Color(0.25f, 0.55f, 1.00f, 0.45f);
        GUI.DrawTexture(new Rect(x,         y,         w, 1), _white);
        GUI.DrawTexture(new Rect(x,         y + h - 1, w, 1), _white);
        GUI.DrawTexture(new Rect(x,         y,         1, h), _white);
        GUI.DrawTexture(new Rect(x + w - 1, y,         1, h), _white);
        GUI.color = prev;
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        _white = new Texture2D(1, 1);
        _white.SetPixel(0, 0, Color.white);
        _white.Apply();

        _headerLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = new Color(0.55f, 0.75f, 1f) }
        };

        // Active button — bright blue tint
        Texture2D activeBg = new Texture2D(1, 1);
        activeBg.SetPixel(0, 0, new Color(0.15f, 0.40f, 0.85f, 0.95f));
        activeBg.Apply();

        _btnActive = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset(10, 6, 0, 0),
            normal    = { background = activeBg, textColor = Color.white },
            hover     = { background = activeBg, textColor = Color.white },
            active    = { background = activeBg, textColor = Color.white },
        };

        // Inactive button — dim
        Texture2D inactiveBg = new Texture2D(1, 1);
        inactiveBg.SetPixel(0, 0, new Color(0.10f, 0.10f, 0.18f, 0.90f));
        inactiveBg.Apply();

        Texture2D inactiveHover = new Texture2D(1, 1);
        inactiveHover.SetPixel(0, 0, new Color(0.18f, 0.28f, 0.55f, 0.90f));
        inactiveHover.Apply();

        _btnInactive = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset(10, 6, 0, 0),
            normal    = { background = inactiveBg,  textColor = new Color(0.75f, 0.75f, 0.75f) },
            hover     = { background = inactiveHover, textColor = Color.white },
            active    = { background = activeBg,    textColor = Color.white },
        };
    }
}
