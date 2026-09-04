#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveFog.Editor
{
    public enum InteractiveSourceDebugType
    {
        None,
        Intersection,
        All
    }

    [CustomEditor(typeof(FluidSimulation))]
    public sealed class FluidSimulationEditor : UnityEditor.Editor
    {
        private static bool forcesFoldout;
        private readonly BoxBoundsHandle boundsHandle = new BoxBoundsHandle();
        private GUIStyle boundsLabelStyle;

        public override void OnInspectorGUI()
        {
            var simulation = (FluidSimulation)target;
            EditorGUILayout.LabelField("Scope", simulation.Scope);
            EditorGUILayout.LabelField("State", simulation.State ? "True" : "False");
            if (simulation.State)
                EditorGUILayout.LabelField("Resolution", simulation.Resolution.ToString());

            EditorGUILayout.Space();
            DrawDefaultInspector();
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "Scene 视图中的青色编辑框就是流体交互区域。选中该物体后，可直接拖动六个面调整 Solver Range。",
                MessageType.Info);

            forcesFoldout = EditorGUILayout.Foldout(forcesFoldout, "Forces", true);
            if (forcesFoldout)
            {
                EditorGUI.indentLevel++;
                var intersecting = FluidSimulationDebugWindow.GetVisibleSources(simulation,
                    InteractiveSourceDebugType.Intersection);
                EditorGUILayout.LabelField("Intersecting Sources", intersecting.Count.ToString());
                foreach (var source in intersecting)
                    EditorGUILayout.ObjectField(source, typeof(FluidSimulationInteractiveSource), true);
                if (intersecting.Count == 0)
                    EditorGUILayout.HelpBox("当前没有与模拟区域相交的交互源。", MessageType.Info);
                EditorGUI.indentLevel--;
            }

            if (GUILayout.Button("Reset Simulation"))
                simulation.ResetSimulation();
            if (GUILayout.Button("Show Debug Window"))
                FluidSimulationDebugWindow.Open(simulation);
        }

        private void OnSceneGUI()
        {
            var simulation = (FluidSimulation)target;
            if (simulation == null)
                return;

            var center = Application.isPlaying
                ? simulation.SimulationCenter
                : (simulation.following != null ? simulation.following.position : simulation.transform.position) +
                  simulation.followOffset;
            var range = new Vector3(
                Mathf.Max(0.5f, simulation.solverRange.x),
                Mathf.Max(0.5f, simulation.solverRange.y),
                Mathf.Max(0.5f, simulation.solverRange.z));
            var displayRange = range;
            if (!simulation.Is3D)
                displayRange.y = 0.15f;

            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;

            var cyan = new Color(0.05f, 0.95f, 1f, 1f);
            Handles.color = cyan;
            Handles.DrawWireCube(center, displayRange);

            if (boundsLabelStyle == null)
            {
                boundsLabelStyle = new GUIStyle(EditorStyles.boldLabel);
                boundsLabelStyle.normal.textColor = cyan;
                boundsLabelStyle.alignment = TextAnchor.MiddleCenter;
            }

            var labelOffset = HandleUtility.GetHandleSize(center) * 0.18f;
            var labelPosition = center + Vector3.up * (displayRange.y * 0.5f + labelOffset);
            var sizeLabel = simulation.Is3D
                ? $"交互区域  {range.x:0.##} × {range.y:0.##} × {range.z:0.##}"
                : $"交互区域  {range.x:0.##} × {range.z:0.##}";
            Handles.Label(labelPosition, sizeLabel, boundsLabelStyle);

            if (!Application.isPlaying && simulation.Is3D)
            {
                boundsHandle.center = center;
                boundsHandle.size = range;
                boundsHandle.wireframeColor = cyan;
                boundsHandle.handleColor = new Color(0.2f, 1f, 1f, 1f);

                EditorGUI.BeginChangeCheck();
                boundsHandle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(simulation, "Resize Fluid Simulation Area");

                    var centerDelta = boundsHandle.center - center;
                    if (simulation.following != null)
                    {
                        simulation.followOffset += centerDelta;
                    }
                    else
                    {
                        Undo.RecordObject(simulation.transform, "Move Fluid Simulation Area");
                        simulation.transform.position += centerDelta;
                    }

                    var newSize = boundsHandle.size;
                    simulation.solverRange = new Vector3(
                        Mathf.Max(0.5f, newSize.x),
                        Mathf.Max(0.5f, newSize.y),
                        Mathf.Max(0.5f, newSize.z));
                    EditorUtility.SetDirty(simulation);
                    SceneView.RepaintAll();
                }
            }

            Handles.zTest = previousZTest;
        }
    }

    public sealed class FluidSimulationDebugWindow : EditorWindow
    {
        private const int ForceTextureSize = 256;
        private FluidSimulation simulation;
        private Material debugMaterial;
        private Texture2D externalForcePreview;
        private Color[] externalForcePixels;
        private RenderTexture densityPreview;
        private RenderTexture velocityPreview;
        private RenderTexture pressurePreview;
        private RenderTexture curlPreview;
        private Vector2 scroll;
        private float previewSize = 205f;
        private float previewSlice = 0.5f;
        private bool followSourceHeight = true;
        private double nextForceUpdate;
        private InteractiveSourceDebugType sourceDebugType = InteractiveSourceDebugType.Intersection;

        internal static void Open(FluidSimulation target)
        {
            var window = GetWindow<FluidSimulationDebugWindow>("Fluid Simulation");
            window.simulation = target;
            window.minSize = new Vector2(660f, 560f);
            window.Show();
        }

        [MenuItem("Tools/Interactive Fog/Show Debug Window")]
        private static void OpenFromMenu()
        {
            var target = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<FluidSimulation>()
                : null;
            if (target == null)
                target = Object.FindObjectOfType<FluidSimulation>();
            Open(target);
        }

        [MenuItem("Tools/Interactive Fog/Capture Debug Snapshot")]
        private static void CaptureFromMenu()
        {
            var window = GetWindow<FluidSimulationDebugWindow>("Fluid Simulation");
            if (window.simulation == null)
                window.simulation = Object.FindObjectOfType<FluidSimulation>();
            window.CaptureSnapshot();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += DrawSceneSources;
            if (simulation == null)
                simulation = Object.FindObjectOfType<FluidSimulation>();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DrawSceneSources;
            if (debugMaterial != null)
                DestroyImmediate(debugMaterial);
            if (externalForcePreview != null)
                DestroyImmediate(externalForcePreview);
            ReleasePreview(ref densityPreview);
            ReleasePreview(ref velocityPreview);
            ReleasePreview(ref pressurePreview);
            ReleasePreview(ref curlPreview);
        }

        private void OnInspectorUpdate()
        {
            // Editor windows continue receiving inspector ticks while docked behind another tab.
            // Do not spend GPU work on four preview blits unless the debug window is actually focused.
            if (!hasFocus)
                return;
            if (EditorApplication.timeSinceStartup < nextForceUpdate)
                return;
            nextForceUpdate = EditorApplication.timeSinceStartup + 0.15;

            if (simulation == null)
                simulation = Object.FindObjectOfType<FluidSimulation>();
            if (simulation != null)
            {
                EnsureResources();
                UpdatePreviewSliceFromSource();
                if (sourceDebugType != InteractiveSourceDebugType.None)
                    UpdateExternalForcePreview();
                UpdateFieldPreviews();
            }
            Repaint();
        }

        private void EnsureResources()
        {
            if (debugMaterial == null)
            {
                var shader = Shader.Find("Hidden/InteractiveFog/DebugField");
                if (shader != null)
                    debugMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (externalForcePreview == null)
            {
                externalForcePreview = new Texture2D(ForceTextureSize, ForceTextureSize,
                    TextureFormat.RGBA32, false, true)
                {
                    name = "Interactive Fog External Force Preview",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                externalForcePixels = new Color[ForceTextureSize * ForceTextureSize];
            }
            EnsurePreview(ref densityPreview, "Density Debug Preview");
            EnsurePreview(ref velocityPreview, "Velocity Debug Preview");
            EnsurePreview(ref pressurePreview, "Pressure Debug Preview");
            EnsurePreview(ref curlPreview, "Curl Debug Preview");
        }

        private void OnGUI()
        {
            HandleGeometryHotkeys();
            if (simulation == null)
                simulation = Object.FindObjectOfType<FluidSimulation>();
            simulation = (FluidSimulation)EditorGUILayout.ObjectField("Fluid Simulation", simulation,
                typeof(FluidSimulation), true);
            if (simulation == null)
            {
                EditorGUILayout.HelpBox("Select a FluidSimulation component.", MessageType.Info);
                return;
            }

            EnsureResources();
            EditorGUILayout.LabelField("Scope", simulation.Scope);
            EditorGUILayout.LabelField("State", simulation.State ? "True" : "False");
            EditorGUILayout.LabelField("Resolution", simulation.Resolution.ToString());
            var fieldsReady = simulation.DensityTexture != null && simulation.VelocityTexture != null &&
                              simulation.PressureTexture != null && simulation.CurlTexture != null;
            EditorGUILayout.LabelField("GPU Fields", fieldsReady ? "Ready" : "Not allocated");
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode to allocate and update GPU fields.", MessageType.Info);
            else if (fieldsReady && FluidSimulationInteractiveSource.ActiveSources.Count == 0)
                EditorGUILayout.HelpBox("No active interactive source is registered.", MessageType.Warning);
            else if (fieldsReady)
                EditorGUILayout.HelpBox(
                    "Fields are live. With Kill If Still enabled, drag or move the source before Density/Velocity become visible.",
                    MessageType.None);
            previewSize = EditorGUILayout.Slider("Preview Size", previewSize, 96f, 320f);
            if (simulation.Is3D)
            {
                followSourceHeight = EditorGUILayout.Toggle("Follow Source Height", followSourceHeight);
                using (new EditorGUI.DisabledScope(followSourceHeight))
                previewSlice = EditorGUILayout.Slider("3D Y Slice", previewSlice, 0f, 1f);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginHorizontal();
            DrawField("Density", densityPreview);
            DrawField("Velocity", velocityPreview);
            DrawExternalForcePanel();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawField("Pressure", pressurePreview);
            DrawField("Curl", curlPreview);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();

            var previous = sourceDebugType;
            sourceDebugType = (InteractiveSourceDebugType)EditorGUILayout.EnumPopup(
                "Interactive Source Type", sourceDebugType);
            if (previous != sourceDebugType)
            {
                UpdateExternalForcePreview();
                SceneView.RepaintAll();
            }
            EditorGUILayout.HelpBox(
                "None：隐藏扰动源；Intersection：仅显示与当前模拟区域相交的扰动源；All：显示全部扰动源。",
                MessageType.None);
            if (GUILayout.Button("Capture Debug Snapshot"))
                CaptureSnapshot();
        }

        private static void HandleGeometryHotkeys()
        {
            if (!Application.isPlaying || Event.current.type != EventType.KeyDown ||
                Event.current.control || Event.current.alt)
                return;

            FluidSourceShape? shape = null;
            switch (Event.current.keyCode)
            {
                case KeyCode.Alpha1:
                case KeyCode.Keypad1:
                    shape = FluidSourceShape.Sphere;
                    break;
                case KeyCode.Alpha2:
                case KeyCode.Keypad2:
                    shape = FluidSourceShape.Cube;
                    break;
            }

            if (!shape.HasValue)
                return;
            var controller = Object.FindObjectOfType<FogInteractorController>();
            if (controller == null)
                return;
            controller.SetSourceShape(shape.Value);
            Event.current.Use();
        }

        private void CaptureSnapshot()
        {
            if (simulation == null)
                simulation = Object.FindObjectOfType<FluidSimulation>();
            if (simulation == null || !Application.isPlaying)
            {
                Debug.LogWarning("Interactive Fog debug snapshot requires Play Mode and a FluidSimulation.");
                return;
            }

            EnsureResources();
            UpdateExternalForcePreview();
            UpdateFieldPreviews();
            var composite = new Texture2D(ForceTextureSize * 3, ForceTextureSize * 2,
                TextureFormat.RGBA32, false, true);
            var clear = new Color[composite.width * composite.height];
            for (var i = 0; i < clear.Length; i++) clear[i] = Color.black;
            composite.SetPixels(clear);

            CopyPreview(composite, densityPreview, 0, ForceTextureSize);
            CopyPreview(composite, velocityPreview, ForceTextureSize, ForceTextureSize);
            if (externalForcePreview != null)
                composite.SetPixels(ForceTextureSize * 2, ForceTextureSize,
                    ForceTextureSize, ForceTextureSize, externalForcePreview.GetPixels());
            CopyPreview(composite, pressurePreview, 0, 0);
            CopyPreview(composite, curlPreview, ForceTextureSize, 0);
            composite.Apply(false, false);

            var outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../.codex_work"));
            Directory.CreateDirectory(outputDirectory);
            var output = Path.Combine(outputDirectory,
                $"fluid-debug-{System.DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(output, composite.EncodeToPNG());
            DestroyImmediate(composite);
            Debug.Log("Interactive Fog debug snapshot: " + output);
        }

        private static void CopyPreview(Texture2D destination, RenderTexture source, int x, int y)
        {
            if (source == null || !source.IsCreated())
                return;
            var previous = RenderTexture.active;
            RenderTexture.active = source;
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            pixels.Apply(false, false);
            RenderTexture.active = previous;
            destination.SetPixels(x, y, source.width, source.height, pixels.GetPixels());
            DestroyImmediate(pixels);
        }

        private void DrawField(string label, Texture texture)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(previewSize + 8f));
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            var rect = GUILayoutUtility.GetRect(previewSize, previewSize,
                GUILayout.Width(previewSize), GUILayout.Height(previewSize));
            EditorGUI.DrawRect(rect, Color.black);
            if (texture != null)
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
            EditorGUILayout.EndVertical();
        }

        private void UpdateFieldPreviews()
        {
            if (simulation == null || debugMaterial == null)
                return;
            if (simulation.Is3D)
            {
                BlitField3D(simulation.DensityTexture, densityPreview, 0, 2.2f);
                BlitField3D(simulation.VelocityTexture, velocityPreview, 1, 2.5f);
                BlitField3D(simulation.PressureTexture, pressurePreview, 2, 40f);
                BlitField3D(simulation.CurlTexture, curlPreview, 3, 8f);
                return;
            }
            BlitField(simulation.DensityTexture, densityPreview, 0, 2.2f);
            BlitField(simulation.VelocityTexture, velocityPreview, 1, 2.5f);
            BlitField(simulation.PressureTexture, pressurePreview, 2, 40f);
            BlitField(simulation.CurlTexture, curlPreview, 3, 8f);
        }

        private void UpdatePreviewSliceFromSource()
        {
            if (simulation == null || !simulation.Is3D || !followSourceHeight)
                return;

            var sources = GetVisibleSources(simulation, InteractiveSourceDebugType.Intersection);
            if (sources.Count == 0)
                return;

            var rangeY = Mathf.Max(simulation.SimulationRange.y, 0.0001f);
            previewSlice = Mathf.Clamp01(
                (sources[0].transform.position.y - simulation.SimulationCenter.y) / rangeY + 0.5f);
        }

        private void BlitField3D(Texture source, RenderTexture destination, int mode, float exposure)
        {
            if (source == null || destination == null)
                return;
            debugMaterial.SetTexture("_VolumeTex", source);
            debugMaterial.SetInt("_Mode", mode);
            debugMaterial.SetFloat("_Exposure", exposure);
            debugMaterial.SetFloat("_Slice", previewSlice);
            Graphics.Blit(null, destination, debugMaterial, 1);
        }

        private void BlitField(Texture source, RenderTexture destination, int mode, float exposure)
        {
            if (source == null || destination == null)
                return;
            // Bind compute-generated random-write textures explicitly. Passing them
            // as Graphics.Blit's source can collapse some RHalf/RGHalf fields to a
            // single constant sample on DX11, which made every debug tile look flat.
            debugMaterial.SetTexture("_MainTex", source);
            debugMaterial.SetInt("_Mode", mode);
            debugMaterial.SetFloat("_Exposure", exposure);
            Graphics.Blit(null, destination, debugMaterial, 0);
        }

        private static void EnsurePreview(ref RenderTexture texture, string textureName)
        {
            if (texture != null && texture.IsCreated())
                return;
            if (texture != null)
                DestroyImmediate(texture);
            texture = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name = textureName,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
        }

        private static void ReleasePreview(ref RenderTexture texture)
        {
            if (texture == null)
                return;
            if (RenderTexture.active == texture)
                RenderTexture.active = null;
            texture.Release();
            DestroyImmediate(texture);
            texture = null;
        }

        private void DrawExternalForcePanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(previewSize + 8f));
            EditorGUILayout.LabelField("ExternalForce", EditorStyles.boldLabel);
            var rect = GUILayoutUtility.GetRect(previewSize, previewSize,
                GUILayout.Width(previewSize), GUILayout.Height(previewSize));
            EditorGUI.DrawRect(rect, Color.black);
            if (externalForcePreview != null)
                GUI.DrawTexture(rect, externalForcePreview, ScaleMode.ScaleToFit, false);
            EditorGUILayout.EndVertical();
        }

        private void UpdateExternalForcePreview()
        {
            if (externalForcePreview == null || simulation == null)
                return;
            for (var i = 0; i < externalForcePixels.Length; i++)
                externalForcePixels[i] = Color.black;

            var range = simulation.SimulationRange;
            var center = simulation.SimulationCenter;
            var sources = GetVisibleSources(simulation, sourceDebugType);
            foreach (var source in sources)
            {
                var uv = new Vector2(
                    (source.transform.position.x - center.x) / range.x + 0.5f,
                    (source.transform.position.z - center.z) / range.z + 0.5f);
                var centerPixel = new Vector2(uv.x * (ForceTextureSize - 1), uv.y * (ForceTextureSize - 1));
                var radiusPixels = Mathf.Max(2f, source.radius / Mathf.Max(range.x, range.z) * ForceTextureSize);
                var minX = Mathf.Clamp(Mathf.FloorToInt(centerPixel.x - radiusPixels), 0, ForceTextureSize - 1);
                var maxX = Mathf.Clamp(Mathf.CeilToInt(centerPixel.x + radiusPixels), 0, ForceTextureSize - 1);
                var minY = Mathf.Clamp(Mathf.FloorToInt(centerPixel.y - radiusPixels), 0, ForceTextureSize - 1);
                var maxY = Mathf.Clamp(Mathf.CeilToInt(centerPixel.y + radiusPixels), 0, ForceTextureSize - 1);
                var velocity = source.CurrentVelocity * source.forceStrength;
                if (source.jetEmission)
                {
                    var localDirection = source.localJetDirection.sqrMagnitude > 0.0001f
                        ? source.localJetDirection.normalized
                        : Vector3.down;
                    velocity += source.transform.TransformDirection(localDirection) *
                                (source.jetSpeed * source.forceStrength);
                }
                var normalizedSpeed = Mathf.Clamp01(velocity.magnitude / 10f);
                var sourceColor = new Color(
                    Mathf.Clamp01(Mathf.Abs(velocity.x) / 10f),
                    Mathf.Max(0.15f, normalizedSpeed),
                    Mathf.Clamp01(Mathf.Abs(source.addDensity)), 1f);

                for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), centerPixel) / radiusPixels;
                    if (distance >= 1f) continue;
                    var influence = Mathf.SmoothStep(1f, 0f, distance);
                    var index = y * ForceTextureSize + x;
                    externalForcePixels[index] = Color.Lerp(externalForcePixels[index], sourceColor, influence);
                }
            }

            externalForcePreview.SetPixels(externalForcePixels);
            externalForcePreview.Apply(false, false);
        }

        internal static List<FluidSimulationInteractiveSource> GetVisibleSources(
            FluidSimulation target, InteractiveSourceDebugType mode)
        {
            var result = new List<FluidSimulationInteractiveSource>();
            if (target == null || mode == InteractiveSourceDebugType.None)
                return result;
            foreach (var source in FluidSimulationInteractiveSource.ActiveSources)
            {
                if (source == null) continue;
                if (source.targetSimulation != null && source.targetSimulation != target) continue;
                if (mode == InteractiveSourceDebugType.All || Intersects(target, source))
                    result.Add(source);
            }
            return result;
        }

        private static bool Intersects(FluidSimulation target, FluidSimulationInteractiveSource source)
        {
            var center = target.SimulationCenter;
            var halfRange = target.SimulationRange * 0.5f;
            var radius = source.shapeType == FluidSourceShape.Cube
                ? source.cubeSize.magnitude * 0.5f
                : source.radius;
            var delta = source.transform.position - center;
            var horizontal = Mathf.Abs(delta.x) <= halfRange.x + radius &&
                             Mathf.Abs(delta.z) <= halfRange.z + radius;
            return horizontal && (!target.Is3D || Mathf.Abs(delta.y) <= halfRange.y + radius);
        }

        private void DrawSceneSources(SceneView sceneView)
        {
            if (simulation == null || sourceDebugType == InteractiveSourceDebugType.None)
                return;
            Handles.color = new Color(0.15f, 1f, 0.4f, 0.9f);
            foreach (var source in GetVisibleSources(simulation, sourceDebugType))
            {
                if (source.shapeType == FluidSourceShape.Cube)
                {
                    var old = Handles.matrix;
                    Handles.matrix = Matrix4x4.TRS(source.transform.position, source.transform.rotation,
                        source.cubeSize);
                    Handles.DrawWireCube(Vector3.zero, Vector3.one);
                    Handles.matrix = old;
                }
                else
                {
                    Handles.DrawWireDisc(source.transform.position, Vector3.up, source.radius);
                    Handles.DrawWireDisc(source.transform.position, Vector3.right, source.radius);
                    Handles.DrawWireDisc(source.transform.position, Vector3.forward, source.radius);
                }
            }
        }
    }
}
#endif
