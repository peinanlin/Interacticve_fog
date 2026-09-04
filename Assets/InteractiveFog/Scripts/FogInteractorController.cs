using System;
using System.IO;
using UnityEngine;

namespace InteractiveFog
{
    [DisallowMultipleComponent]
    public sealed class FogInteractorController : MonoBehaviour
    {
        public FluidSimulation simulation;
        public FluidSimulationInteractiveSource source;
        public bool threeDimensional;
        public bool sourceGeneratedMode;
        public bool autoMotion;
        [Header("Mouse Drag")]
        public bool mouseDragEnabled;
        public float dragPlaneHeight = 1.1f;
        [Range(1f, 40f)] public float dragFollowSpeed = 24f;

        [Header("Directional Trail")]
        public Transform trailAnchor;
        [Range(0.1f, 8f)] public float trailLength = 2.8f;

        [Header("Geometry Demo")]
        public bool geometryHotkeys;
        [Range(0.1f, 5f)] public float windAdjustSpeed = 0.8f;
        [Range(0.1f, 12f)] public float manualSpeed = 4f;
        [Range(0.1f, 3f)] public float autoSpeed = 0.65f;
        public Vector3 autoCenter = new Vector3(0f, 1.2f, 0f);
        public Vector3 autoExtents = new Vector3(6f, 1.6f, 5f);

        private bool erasing;
        private bool mouseDragging;
        private Vector3 lastMotionDirection = Vector3.forward;
        private string geometryFeedback;
        private float geometryFeedbackUntil;

        private void Reset()
        {
            source = GetComponent<FluidSimulationInteractiveSource>();
        }

        private void Start()
        {
            // The 3D interaction demo is intended for direct Scene-view manipulation.
            // Keep this runtime guard so an already-open, unsaved copy of the scene
            // cannot restore the legacy serialized auto-motion value on Play.
            if (threeDimensional && !sourceGeneratedMode)
                autoMotion = false;
        }

        private void Update()
        {
            if (source == null)
                source = GetComponent<FluidSimulationInteractiveSource>();
            UpdateGeometrySelection();
            UpdateWindControls();
            if (Input.GetKeyDown(KeyCode.T))
                autoMotion = !autoMotion;
            if (Input.GetKeyDown(KeyCode.R) && simulation != null)
                simulation.ResetSimulation();
            if (Input.GetKeyDown(KeyCode.Space) && source != null)
            {
                erasing = !erasing;
                source.addDensity = erasing ? -1f : 1f;
            }
            if (Input.GetKeyDown(KeyCode.P))
            {
                var outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../.codex_work"));
                Directory.CreateDirectory(outputDirectory);
                var dimension = threeDimensional ? "3d" : "2d";
                var output = Path.Combine(outputDirectory,
                    $"interactive-fog-{dimension}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                ScreenCapture.CaptureScreenshot(output, 1);
                Debug.Log("Interactive Fog screenshot: " + output);
            }

            if (autoMotion)
            {
                var previous = transform.position;
                var time = Time.time * autoSpeed;
                var position = autoCenter + new Vector3(
                    Mathf.Sin(time * 1.07f) * autoExtents.x,
                    threeDimensional ? Mathf.Sin(time * 1.71f) * autoExtents.y : 0f,
                    Mathf.Cos(time * 0.83f) * autoExtents.z);
                transform.position = position;
                UpdateDirectionalTrail(previous);
                return;
            }

            var positionBeforeInput = transform.position;
            if (mouseDragEnabled)
                UpdateMouseDrag();

            var movement = sourceGeneratedMode
                ? new Vector3(
                    (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f),
                    0f,
                    (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f))
                : new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            if (threeDimensional)
            {
                if (Input.GetKey(KeyCode.E)) movement.y += 1f;
                if (Input.GetKey(KeyCode.Q)) movement.y -= 1f;
            }
            if (movement.sqrMagnitude > 1f)
                movement.Normalize();
            if (!mouseDragging)
                transform.position += movement * (manualSpeed * Time.deltaTime);
            UpdateDirectionalTrail(positionBeforeInput);
        }

        private void UpdateGeometrySelection()
        {
            if (!geometryHotkeys || source == null)
                return;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                return;
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
                SetSourceShape(FluidSourceShape.Sphere);
            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
                SetSourceShape(FluidSourceShape.Cube);
        }

        public void SetSourceShape(FluidSourceShape shape)
        {
            if (source == null)
                return;
            source.shapeType = shape;
            if (simulation != null)
                simulation.ResetSimulation();
            geometryFeedback = $"Shape changed to {shape}; simulation reset";
            geometryFeedbackUntil = Time.unscaledTime + 2f;
            Debug.Log("Interactive Fog: " + geometryFeedback);
        }

        private void UpdateWindControls()
        {
            if (!sourceGeneratedMode || simulation == null)
                return;
            var windInput = new Vector3(
                Input.GetKey(KeyCode.RightArrow) ? 1f : Input.GetKey(KeyCode.LeftArrow) ? -1f : 0f,
                0f,
                Input.GetKey(KeyCode.UpArrow) ? 1f : Input.GetKey(KeyCode.DownArrow) ? -1f : 0f);
            simulation.environmentWind += windInput * (windAdjustSpeed * Time.deltaTime);
        }

        private void UpdateMouseDrag()
        {
            var camera = Camera.main;
            if (camera == null)
                return;
            if (Input.GetMouseButtonDown(0))
            {
                var ballScreen = camera.WorldToScreenPoint(transform.position);
                var pointer = (Vector2)Input.mousePosition;
                mouseDragging = ballScreen.z > 0f &&
                                Vector2.Distance(pointer, new Vector2(ballScreen.x, ballScreen.y)) <= 90f;
            }
            if (Input.GetMouseButtonUp(0))
                mouseDragging = false;
            if (!mouseDragging)
                return;

            var ray = camera.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(Vector3.up, new Vector3(0f, dragPlaneHeight, 0f));
            if (!plane.Raycast(ray, out var distance))
                return;
            var target = ray.GetPoint(distance);
            target.y = dragPlaneHeight;
            var blend = 1f - Mathf.Exp(-dragFollowSpeed * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, target, blend);
        }

        private void UpdateDirectionalTrail(Vector3 previousPosition)
        {
            var displacement = transform.position - previousPosition;
            displacement.y = threeDimensional ? displacement.y : 0f;
            if (displacement.sqrMagnitude > 0.00001f)
            {
                lastMotionDirection = displacement.normalized;
                if (lastMotionDirection.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.LookRotation(lastMotionDirection, Vector3.up);
            }
            if (trailAnchor != null)
                trailAnchor.position = transform.position - lastMotionDirection * trailLength;
        }

        private void OnGUI()
        {
            var panel = new Rect(18f, 18f, 540f, sourceGeneratedMode ? 225f : 162f);
            GUI.Box(panel, GUIContent.none);
            GUILayout.BeginArea(new Rect(panel.x + 14f, panel.y + 10f, panel.width - 28f, panel.height - 20f));
            GUILayout.Label(sourceGeneratedMode
                ? threeDimensional
                    ? "Continuous Rocket Exhaust - 3D Eulerian"
                    : "Source Generated Fluid Fog - 2D Eulerian"
                : threeDimensional
                    ? "Interactive Volumetric Fog - 3D Eulerian"
                    : "Interactive Volumetric Fog - 2D Eulerian");
            GUILayout.Label("T: toggle auto motion   WASD: move   R: reset simulation");
            if (mouseDragEnabled) GUILayout.Label("Hold LMB on the sphere and drag: reposition the airborne exhaust source");
            if (threeDimensional) GUILayout.Label("Q/E: vertical move");
            GUILayout.Label("Quality: Z Low   X Medium   C High");
            if (sourceGeneratedMode && source != null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("1  Sphere")) SetSourceShape(FluidSourceShape.Sphere);
                if (GUILayout.Button("2  Cube")) SetSourceShape(FluidSourceShape.Cube);
                GUILayout.EndHorizontal();
                GUILayout.Label($"Number row or numpad 1/2   Current: {source.shapeType}");
                if (Time.unscaledTime < geometryFeedbackUntil)
                    GUILayout.Label(geometryFeedback);
                GUILayout.Label("Arrow keys: adjust global wind X/Z");
                if (simulation != null)
                    GUILayout.Label($"Gravity: {simulation.gravity}   Wind: {simulation.environmentWind}");
            }
            GUILayout.Label("Space: switch source density +1 / -1   P: capture screenshot");
            if (simulation != null)
                GUILayout.Label($"Resolution: {simulation.Resolution}   Sources: {FluidSimulationInteractiveSource.ActiveSources.Count}");
            GUILayout.EndArea();
        }
    }
}
