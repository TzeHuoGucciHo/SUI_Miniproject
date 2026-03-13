using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class TonfaSticky : MonoBehaviour
{
    [Header("Tip (spike point)")]
    public Transform tip;
    public float indentDistance = 0.06f;

    [Header("Tags")]
    public string stickTag = "Building";
    public string playerTag = "Player";

    Rigidbody rb;
    Collider col;
    bool stuck;

    // --- NEW: info about what we stuck to ---
    Transform stuckTo;
    Vector3 stuckPoint;
    Vector3 stuckNormal; // points OUT of the surface

    void Awake() => EnsureRefs();

    void EnsureRefs()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (col == null) col = GetComponent<Collider>();
    }

    public bool IsStuck => stuck;
    public Transform StuckTo => stuckTo;
    public Vector3 StuckPoint => stuckPoint;
    public Vector3 StuckNormal => stuckNormal;

    void OnCollisionEnter(Collision c)
    {
        EnsureRefs();
        if (rb.isKinematic) return;
        if (stuck) return;
        if (c.contactCount == 0) return;

        if (!string.IsNullOrEmpty(playerTag) && c.collider.CompareTag(playerTag))
            return;

        if (!string.IsNullOrEmpty(stickTag) && !c.collider.CompareTag(stickTag))
            return;

        var contact = c.GetContact(0);
        StickTo(c.transform, contact.point, contact.normal);
    }

    void StickTo(Transform hitTransform, Vector3 hitPoint, Vector3 surfaceNormal)
    {
        EnsureRefs();
        stuck = true;

        stuckTo = hitTransform;
        stuckPoint = hitPoint;
        stuckNormal = surfaceNormal; // OUT of surface

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;

        Vector3 intoSurface = -surfaceNormal;

        // spike axis is local +Y
        transform.rotation = Quaternion.FromToRotation(transform.up, intoSurface) * transform.rotation;

        if (tip != null)
        {
            Vector3 desiredTipPos = hitPoint + intoSurface * indentDistance;
            Vector3 tipOffset = tip.position - transform.position;
            transform.position = desiredTipPos - tipOffset;
        }
        else
        {
            transform.position = hitPoint + intoSurface * indentDistance;
        }

        transform.SetParent(hitTransform, true);
        col.enabled = false;
    }

    public void ResetStick()
    {
        EnsureRefs();
        stuck = false;

        stuckTo = null;
        stuckPoint = default;
        stuckNormal = Vector3.up;

        transform.SetParent(null, true);
        col.enabled = true;

        rb.isKinematic = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }
}