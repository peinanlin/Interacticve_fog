using UnityEngine;

namespace PolygonTown.Demo
{
    [DisallowMultipleComponent]
    public sealed class ThirdPersonFogCamera : MonoBehaviour
    {
        public Transform target;
        public Vector3 targetOffset = new Vector3(0f, 1.35f, 0f);
        public float distance = 5.2f;
        public float minimumDistance = 1.2f;
        public float maximumDistance = 8f;
        public float mouseSensitivity = 3f;
        public float positionSmoothTime = 0.045f;
        public float collisionRadius = 0.22f;

        private float yaw;
        private float pitch = 18f;
        private Vector3 positionVelocity;

        private void Start()
        {
            yaw = transform.eulerAngles.y;
            LockCursor();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (Input.GetMouseButtonDown(0))
            {
                LockCursor();
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
                pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
                pitch = Mathf.Clamp(pitch, -10f, 70f);
            }

            distance = Mathf.Clamp(distance - Input.mouseScrollDelta.y * 0.6f,
                minimumDistance, maximumDistance);
        }

        private void LateUpdate()
        {
            if (target == null)
                return;

            var focus = target.position + targetOffset;
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            var direction = rotation * Vector3.back;
            var cameraDistance = distance;

            if (Physics.SphereCast(focus, collisionRadius, direction, out var hit, distance,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                cameraDistance = Mathf.Max(minimumDistance, hit.distance - collisionRadius);
            }

            var desiredPosition = focus + direction * cameraDistance;
            transform.position = Vector3.SmoothDamp(transform.position, desiredPosition,
                ref positionVelocity, positionSmoothTime);
            transform.rotation = rotation;
        }

        private static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
