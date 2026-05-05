using UnityEngine;

/// <summary>
/// A simple free-fly camera script for the conference simulation.
/// Controls: 
/// - WASD / Arrows: Move
/// - Mouse (Right Click): Rotate
/// - Left Shift: Boost speed
/// </summary>
[AddComponentMenu("Conference Sim/Free Fly Camera")]
public class FreeFlyCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 15f;
    public float boostMultiplier = 3f;

    [Header("Rotation")]
    public float lookSpeed = 2f;

    private float _yaw = 0f;
    private float _pitch = 0f;

    void Start()
    {
        // Initialize rotation from current orientation
        Vector3 rot = transform.eulerAngles;
        _yaw = rot.y;
        _pitch = rot.x;
        
        // Ensure pitch is in -180 to 180 range for clamping
        if (_pitch > 180f) _pitch -= 360f;
    }

    void Update()
    {
        // ── Rotation (Right Click) ───────────────────────────────────────
        if (Input.GetMouseButton(1))
        {
            _yaw += Input.GetAxis("Mouse X") * lookSpeed;
            _pitch -= Input.GetAxis("Mouse Y") * lookSpeed;
            _pitch = Mathf.Clamp(_pitch, -89f, 89f);

            transform.eulerAngles = new Vector3(_pitch, _yaw, 0f);
        }

        // ── Movement ─────────────────────────────────────────────────────
        float currentSpeed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? boostMultiplier : 1f);

        float horizontal = Input.GetAxis("Horizontal");
        float vertical = Input.GetAxis("Vertical");
        float up = 0f;

        if (Input.GetKey(KeyCode.E)) up = 1f;
        if (Input.GetKey(KeyCode.Q)) up = -1f;

        Vector3 move = (transform.right * horizontal + transform.forward * vertical + Vector3.up * up).normalized;
        transform.position += move * currentSpeed * Time.deltaTime;
    }
}
