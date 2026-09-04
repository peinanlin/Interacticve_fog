#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace InteractiveFog.Editor
{
    public static class InteractiveFogDemoBuilder
    {
        private const string DemoDirectory = "Assets/InteractiveFog/Demo";
        private const string MaterialDirectory = DemoDirectory + "/Materials";
        private const string PendingDebugRebuildKey = "InteractiveFog.PendingDebugRebuild";

        [InitializeOnLoadMethod]
        private static void ResumePendingDebugRebuild()
        {
            if (!SessionState.GetBool(PendingDebugRebuildKey, false))
                return;
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    RebuildSourceGeneratedDebugDemo();
            };
        }

        [MenuItem("Tools/Interactive Fog/Create Demo Scenes")]
        public static void CreateDemoScenes()
        {
            Directory.CreateDirectory(DemoDirectory);
            Directory.CreateDirectory(MaterialDirectory);
            var fogMaterial = GetOrCreateMaterial(MaterialDirectory + "/InteractiveFogVolume.mat",
                Shader.Find("InteractiveFog/LocalVolumetricFog"), Color.white);
            var groundMaterial = GetOrCreateMaterial(MaterialDirectory + "/Ground.mat",
                Shader.Find("Universal Render Pipeline/Lit"), new Color(0.16f, 0.2f, 0.24f, 1f));
            var obstacleMaterial = GetOrCreateMaterial(MaterialDirectory + "/Obstacle.mat",
                Shader.Find("Universal Render Pipeline/Lit"), new Color(0.3f, 0.23f, 0.18f, 1f));
            var sourceMaterial = GetOrCreateMaterial(MaterialDirectory + "/InteractiveSource.mat",
                Shader.Find("Universal Render Pipeline/Lit"), new Color(0.08f, 0.9f, 0.2f, 0.3f));
            ConfigureTransparentSourceMaterial(sourceMaterial);

            var scene2D = CreateScene(false, fogMaterial, groundMaterial, obstacleMaterial, sourceMaterial,
                DemoDirectory + "/InteractiveFog2D.unity");
            var scene3D = CreateScene(true, fogMaterial, groundMaterial, obstacleMaterial, sourceMaterial,
                DemoDirectory + "/InteractiveFog3D.unity");
            var fluidDebug2D = CreateScene(false, fogMaterial, groundMaterial, obstacleMaterial, sourceMaterial,
                DemoDirectory + "/FluidSimulationDebug2D.unity");
            ConfigureSourceGeneratedDemo(fluidDebug2D);
            AddToBuildSettings(scene2D, scene3D, fluidDebug2D);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorSceneManager.OpenScene(scene2D);
            Debug.Log("Interactive Fog demo scenes created: " + scene2D + ", " + scene3D +
                      " and " + fluidDebug2D);
        }

        [MenuItem("Tools/Interactive Fog/Open 2D Demo")]
        public static void Open2DDemo()
        {
            EditorSceneManager.OpenScene(DemoDirectory + "/InteractiveFog2D.unity");
        }

        [MenuItem("Tools/Interactive Fog/Open 3D Demo")]
        public static void Open3DDemo()
        {
            EditorSceneManager.OpenScene(DemoDirectory + "/InteractiveFog3D.unity");
        }

        [MenuItem("Tools/Interactive Fog/Open Source Generated Debug Demo")]
        public static void OpenSourceGeneratedDebugDemo()
        {
            EditorSceneManager.OpenScene(DemoDirectory + "/FluidSimulationDebug2D.unity");
        }

        [MenuItem("Tools/Interactive Fog/Rebuild Source Generated Debug Demo")]
        public static void RebuildSourceGeneratedDebugDemo()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.SetBool(PendingDebugRebuildKey, true);
                EditorApplication.isPlaying = false;
                Debug.Log("Exiting Play Mode before rebuilding FluidSimulationDebug2D...");
                return;
            }
            SessionState.SetBool(PendingDebugRebuildKey, false);
            Directory.CreateDirectory(DemoDirectory);
            Directory.CreateDirectory(MaterialDirectory);
            var fogMaterial = GetOrCreateMaterial(MaterialDirectory + "/InteractiveFogVolume.mat",
                Shader.Find("InteractiveFog/LocalVolumetricFog"), Color.white);
            var groundMaterial = GetOrCreateMaterial(MaterialDirectory + "/Ground.mat",
                Shader.Find("Universal Render Pipeline/Lit"), new Color(0.16f, 0.2f, 0.24f, 1f));
            var obstacleMaterial = GetOrCreateMaterial(MaterialDirectory + "/Obstacle.mat",
                Shader.Find("Universal Render Pipeline/Lit"), new Color(0.3f, 0.23f, 0.18f, 1f));
            var sourceMaterial = GetOrCreateMaterial(MaterialDirectory + "/InteractiveSource.mat",
                Shader.Find("Universal Render Pipeline/Lit"), new Color(0.08f, 0.9f, 0.2f, 0.3f));
            ConfigureTransparentSourceMaterial(sourceMaterial);
            var path = CreateScene(false, fogMaterial, groundMaterial, obstacleMaterial, sourceMaterial,
                DemoDirectory + "/FluidSimulationDebug2D.unity");
            ConfigureSourceGeneratedDemo(path);
            AddToBuildSettings(path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorSceneManager.OpenScene(path);
            Debug.Log("Rebuilt verified K2D source-generated debug demo: " + path);
        }

        private static void ConfigureSourceGeneratedDemo(string path)
        {
            var fog = Object.FindObjectOfType<LocalVolumetricFog>();
            var simulation = Object.FindObjectOfType<FluidSimulation>();
            var source = Object.FindObjectOfType<FluidSimulationInteractiveSource>();
            var controller = Object.FindObjectOfType<FogInteractorController>();
            var camera = Camera.main;
            if (camera != null)
            {
                camera.transform.position = new Vector3(0f, 7f, -14f);
                camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.5f, 0f) - camera.transform.position);
            }
            if (fog != null)
            {
                fog.gameObject.name = "Source Generated Fluid Fog";
                // HDR colours: dilute edges glow while the high-density core remains dark.
                fog.thinFogColor = new Color(1.8f, 0.25f, 0.06f, 1f);
                fog.denseFogColor = new Color(0.28f, 0.004f, 0.001f, 1f);
                fog.densityColorStrength = 0.65f;
                fog.densityColorContrast = 0.8f;
                fog.lightAbsorption = 0.3f;
                fog.initialDensity = 0f;
                fog.densityScale = 2.2f;
                fog.interactionResponse = 1.4f;
                fog.densityReverseThreshold = 0f;
                fog.velocityScale = 2.2f;
                fog.baseDensity = 0.8f;
                fog.noiseStrength = 0.7f;
                fog.heightFalloff = 1.15f;
                fog.stepCount = 72;
            }
            if (simulation != null)
            {
                // This scene is explicitly the 2D debug sample. Keeping it K2D
                // also makes every documented debug field directly visible,
                // without depending on a 3D slice intersecting the source.
                simulation.transform.position = Vector3.zero;
                simulation.solverType = FluidSolverType.K2DEulerian;
                simulation.solverRange = new Vector3(20f, 5f, 20f);
                simulation.resolutionScale = 14;
                simulation.solverContent = FluidSolverContent.SolveVorticity |
                                           FluidSolverContent.SolvePressure;
                // Roughly a two-second half-life at 60 Hz: a readable wake that
                // still clears instead of becoming a permanent painted ribbon.
                simulation.densityDissipation = 0.995f;
                simulation.velocityDissipation = 0.995f;
                simulation.externalForceScale = 2f;
                simulation.vorticity = 1.35f;
                simulation.pressureIteration = 4;
                simulation.gravity = Vector3.zero;
                simulation.environmentWind = Vector3.zero;
                simulation.environmentWindResponse = 0f;
                if (fog != null)
                {
                    // The 2D solver stores density on the XZ plane. Keep the render volume
                    // centred on the draggable source instead of extruding it into a 5 m wall.
                    fog.transform.position = simulation.transform.position + Vector3.up * 1.2f;
                    fog.transform.localScale = new Vector3(
                        simulation.solverRange.x + 12f,
                        1.4f,
                        simulation.solverRange.z + 12f);
                }
            }
            if (source != null)
            {
                // Emit only at the sphere centre. The wake is formed by density
                // left in the Eulerian field instead of a precomputed trail volume.
                source.shapeType = FluidSourceShape.Sphere;
                source.gameObject.name = "Interactive Source (Sphere)";
                source.transform.position = new Vector3(-5f, 1.1f, -3f);
                source.transform.localScale = Vector3.one * 1.3f;
                source.radius = 0.55f;
                source.cubeSize = Vector3.one * 1.1f;
                source.addDensity = 1f;
                source.forceStrength = 0.22f;
                source.killIfStill = false;
                source.additionalVelocityType = FluidAdditionalVelocityType.None;
                source.jetEmission = true;
                source.localJetDirection = Vector3.back;
                source.jetSpeed = 5f;
                source.jetSpread = 0.35f;
            }
            if (controller != null)
            {
                controller.sourceGeneratedMode = true;
                controller.threeDimensional = false;
                controller.autoMotion = false;
                controller.mouseDragEnabled = true;
                controller.dragPlaneHeight = controller.transform.position.y;
                controller.dragFollowSpeed = 24f;
                controller.trailAnchor = null;
                controller.geometryHotkeys = true;
                controller.windAdjustSpeed = 0.8f;
            }
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path);
        }

        private static string CreateScene(bool is3D, Material fogMaterial, Material groundMaterial,
            Material obstacleMaterial, Material sourceMaterial, string path)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = is3D ? "InteractiveFog3D" : "InteractiveFog2D";

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = 55f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 120f;
            cameraObject.transform.position = is3D ? new Vector3(0f, 8.5f, -15f) : new Vector3(0f, 7f, -14f);
            cameraObject.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 2f, 0f) - cameraObject.transform.position);
            var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.requiresDepthTexture = true;
            cameraData.renderPostProcessing = true;
            cameraObject.AddComponent<AudioListener>();

            var lightObject = new GameObject("Sun");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.color = new Color(1f, 0.91f, 0.78f);
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.19f, 0.24f, 0.31f);
            RenderSettings.ambientEquatorColor = new Color(0.12f, 0.13f, 0.16f);
            RenderSettings.ambientGroundColor = new Color(0.05f, 0.055f, 0.065f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(2.6f, 1f, 2.6f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMaterial;

            CreateObstacle(new Vector3(-7f, 1.2f, 2f), new Vector3(2.5f, 2.4f, 2f), obstacleMaterial);
            CreateObstacle(new Vector3(6.5f, 0.8f, 4f), new Vector3(3f, 1.6f, 2f), obstacleMaterial);
            CreateObstacle(new Vector3(3.8f, 1.8f, -2.5f), new Vector3(1.4f, 3.6f, 1.4f), obstacleMaterial);

            var simulationObject = new GameObject("Global Fluid Simulation");
            simulationObject.transform.position = new Vector3(0f, is3D ? 3.5f : 0f, 0f);
            var simulation = simulationObject.AddComponent<FluidSimulation>();
            simulation.solverType = is3D ? FluidSolverType.K3DEulerian : FluidSolverType.K2DEulerian;
            simulation.controlMode = FluidControlMode.Advanced;
            simulation.solverRange = is3D ? new Vector3(16f, 7f, 16f) : new Vector3(20f, 5f, 20f);
            simulation.resolutionScale = is3D ? 6 : 20;
            simulation.solverContent = FluidSolverContent.SolveVorticity | FluidSolverContent.SolvePressure;
            if (is3D) simulation.solverContent |= FluidSolverContent.SolveBuoyancy;
            simulation.deltaTime = 0.02f;
            simulation.densityLimit = 1f;
            simulation.densityDissipation = 0.9966f;
            simulation.velocityLimit = 10f;
            simulation.velocityDissipation = 0.9992f;
            simulation.externalForceScale = 5f;
            simulation.vorticity = is3D ? 1.38f : 0.92f;
            simulation.pressureIteration = 4;
            simulation.boundaryDensity = 0f;
            simulation.boundaryVelocityType = FluidBoundaryVelocityType.None;

            var fogObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fogObject.name = "Local Volumetric Fog (Interactive)";
            fogObject.transform.position = simulationObject.transform.position;
            fogObject.transform.localScale = is3D
                ? simulation.solverRange + new Vector3(6f, 2f, 6f)
                : simulation.solverRange + new Vector3(12f, 0f, 12f);
            var fogRenderer = fogObject.GetComponent<MeshRenderer>();
            fogRenderer.sharedMaterial = fogMaterial;
            fogRenderer.shadowCastingMode = ShadowCastingMode.Off;
            fogRenderer.receiveShadows = false;
            var fog = fogObject.AddComponent<LocalVolumetricFog>();
            fog.volumeMaterial = fogMaterial;
            fog.simulation = simulation;
            fog.interactive = true;
            fog.thinFogColor = is3D
                ? new Color(0.82f, 0.26f, 0.3f, 1f)
                : new Color(0.76f, 0.2f, 0.28f, 1f);
            fog.denseFogColor = is3D
                ? new Color(0.24f, 0.035f, 0.05f, 1f)
                : new Color(0.2f, 0.025f, 0.045f, 1f);
            fog.densityColorStrength = 1.35f;
            fog.densityColorContrast = 1f;
            fog.lightAbsorption = 0.55f;
            fog.initialDensity = 1f;
            fog.densityScale = -1.4f;
            fog.interactionResponse = is3D ? 2.6f : 3.5f;
            fog.minimumInteractionDensity = 0f;
            fog.directClearResidualDensity = 0f;
            fog.densityReverseThreshold = 0.245f;
            fog.densityReverseMaxValue = -0.519f;
            fog.velocityScale = 4.65f;
            fog.lerpRange = 0.2f;
            // 3D rays cross much more fog volume than the thin 2D layer, so the
            // optical-density multiplier must be lower even though the documented
            // interactive Initial Density remains exactly 1.
            fog.baseDensity = is3D ? 0.18f : 0.72f;
            fog.stepCount = is3D ? 86 : 72;
            fog.noiseScale = is3D ? 0.52f : 0.34f;
            fog.noiseStrength = 0.74f;
            fog.heightFalloff = is3D ? 0.72f : 1.55f;

            var sourceObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sourceObject.name = "Interactive Source (Sphere)";
            sourceObject.transform.position = is3D ? new Vector3(-5f, 3.5f, -3f) : new Vector3(-5f, 1.1f, -3f);
            sourceObject.transform.localScale = Vector3.one * 1.3f;
            sourceObject.GetComponent<MeshRenderer>().sharedMaterial = sourceMaterial;
            var source = sourceObject.AddComponent<FluidSimulationInteractiveSource>();
            source.targetSimulation = simulation;
            source.shapeType = FluidSourceShape.Sphere;
            source.radius = 0.8f;
            source.sourceType = FluidSourceType.Velocity | FluidSourceType.Density;
            source.forceStrength = 1f;
            source.addDensity = 1f;
            source.killIfStill = false;
            source.additionalVelocityType = FluidAdditionalVelocityType.Wind;
            source.windStrength = is3D ? 0.38f : 0.2f;
            source.controlMode = FluidControlMode.Advanced;
            source.centerFlow2DCoeff = 1f;
            source.diffusion = 0f;
            var controller = sourceObject.AddComponent<FogInteractorController>();
            controller.simulation = simulation;
            controller.source = source;
            controller.threeDimensional = is3D;
            controller.autoMotion = !is3D;
            controller.autoCenter = new Vector3(0f, is3D ? 3.5f : 1.1f, 0f);
            controller.autoExtents = is3D ? new Vector3(5.5f, 1.8f, 4.2f) : new Vector3(6f, 0f, 4.8f);

            var qualityController = simulationObject.AddComponent<global::InteractiveFog.InteractiveFogQualityController>();
            qualityController.fog = fog;
            qualityController.simulation = simulation;
            qualityController.lowProfile = AssetDatabase.LoadAssetAtPath<global::InteractiveFog.InteractiveFogQualityProfile>(
                global::InteractiveFogEditor.InteractiveFogQualityAssetFactory.LowPath);
            qualityController.mediumProfile = AssetDatabase.LoadAssetAtPath<global::InteractiveFog.InteractiveFogQualityProfile>(
                global::InteractiveFogEditor.InteractiveFogQualityAssetFactory.MediumPath);
            // The standalone 3D demo uses a denser 86-step High reference than PolygonTown.
            // Keep its embedded High value rather than silently replacing that look with the shared 60-step profile.
            qualityController.highProfile = null;
            qualityController.highQuality = new global::InteractiveFog.InteractiveFogQualityPreset(
                fog.stepCount, Mathf.Min(fog.minimumStepCount, fog.stepCount),
                simulation.resolutionScale, simulation.pressureIteration);
            qualityController.development.reconstructedRendering = true;
            qualityController.ApplyQualityNow();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        private static void CreateObstacle(Vector3 position, Vector3 scale, Material material)
        {
            var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.name = "Fog Occluder";
            obstacle.transform.position = position;
            obstacle.transform.localScale = scale;
            obstacle.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static Material GetOrCreateMaterial(string path, Shader shader, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void ConfigureTransparentSourceMaterial(Material material)
        {
            if (material == null)
                return;

            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Back);
            material.SetFloat("_Smoothness", 0.55f);
            material.SetFloat("_QueueOffset", 30f);
            material.renderQueue = (int)RenderQueue.Transparent + 30;
            material.SetShaderPassEnabled("DepthOnly", false);
            material.SetShaderPassEnabled("ShadowCaster", false);
            EditorUtility.SetDirty(material);
        }

        private static void AddToBuildSettings(params string[] scenePaths)
        {
            var paths = new HashSet<string>();
            var scenes = new List<EditorBuildSettingsScene>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (paths.Add(scene.path)) scenes.Add(scene);
            }
            foreach (var path in scenePaths)
            {
                if (paths.Add(path)) scenes.Add(new EditorBuildSettingsScene(path, true));
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
#endif
