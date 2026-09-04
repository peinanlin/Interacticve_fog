#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InteractiveFog.Editor
{
    public static class PolygonTownInteractiveFogInstaller
    {
        private const string SourceScenePath = "Assets/InteractiveFog/Demo/InteractiveFog3D.unity";
        private const string TargetScenePath = "Assets/PolygonTown/Scenes/Demo.unity";
        private const string DisplayRootName = "3D交互展示";

        [MenuItem("Tools/Interactive Fog/Install 3D Display Into PolygonTown Demo")]
        public static void InstallIntoOpenPolygonTownDemo()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play Mode before installing the Interactive Fog 3D display.");
                return;
            }

            var targetScene = SceneManager.GetSceneByPath(TargetScenePath);
            if (!targetScene.IsValid() || !targetScene.isLoaded)
            {
                Debug.LogWarning("Open Assets/PolygonTown/Scenes/Demo.unity before installing the 3D display.");
                return;
            }

            var existing = FindRoot(targetScene, DisplayRootName);
            if (existing != null)
            {
                EnsureQualityController(existing);
                EditorSceneManager.MarkSceneDirty(targetScene);
                EditorSceneManager.SaveScene(targetScene);
                Selection.activeGameObject = existing;
                Debug.Log("Interactive Fog 3D display already exists; quality controller is ready.");
                return;
            }

            var sourceScene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Additive);
            try
            {
                var sourceSimulation = FindRoot(sourceScene, "Global Fluid Simulation");
                var sourceFog = FindRoot(sourceScene, "Local Volumetric Fog (Interactive)");
                var sourceBall = FindRoot(sourceScene, "Interactive Source (Sphere)");
                if (sourceSimulation == null || sourceFog == null || sourceBall == null)
                    throw new System.InvalidOperationException(
                        "InteractiveFog3D.unity is missing the simulation, fog volume, or interactive sphere.");

                var displayRoot = new GameObject(DisplayRootName);
                SceneManager.MoveGameObjectToScene(displayRoot, targetScene);
                displayRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var simulationObject = CloneUnder(sourceSimulation, displayRoot.transform, targetScene);
                var fogObject = CloneUnder(sourceFog, displayRoot.transform, targetScene);
                var ballObject = CloneUnder(sourceBall, displayRoot.transform, targetScene);

                var simulation = simulationObject.GetComponent<FluidSimulation>();
                var fog = fogObject.GetComponent<LocalVolumetricFog>();
                var source = ballObject.GetComponent<FluidSimulationInteractiveSource>();
                var controller = ballObject.GetComponent<FogInteractorController>();
                if (simulation == null || fog == null || source == null || controller == null)
                    throw new System.InvalidOperationException("A copied 3D display object is missing a required component.");

                fog.simulation = simulation;
                controller.simulation = simulation;
                controller.source = source;
                controller.autoMotion = false;
                source.targetSimulation = simulation;
                var qualityController = displayRoot.AddComponent<InteractiveFogQualityController>();
                qualityController.fog = fog;
                qualityController.simulation = simulation;
                ConfigureQualityController(qualityController);
                qualityController.ApplyQualityNow();

                EditorUtility.SetDirty(fog);
                EditorUtility.SetDirty(controller);
                EditorUtility.SetDirty(source);
                EditorUtility.SetDirty(qualityController);
                EditorSceneManager.MarkSceneDirty(targetScene);
                EditorSceneManager.SaveScene(targetScene);
                Selection.activeGameObject = displayRoot;
                EditorGUIUtility.PingObject(displayRoot);
                Debug.Log("Installed Interactive Fog 3D display under '3D交互展示' in PolygonTown Demo.");
            }
            finally
            {
                if (sourceScene.IsValid() && sourceScene.isLoaded)
                    EditorSceneManager.CloseScene(sourceScene, true);
            }
        }

        private static GameObject CloneUnder(GameObject source, Transform parent, Scene targetScene)
        {
            var clone = Object.Instantiate(source);
            clone.name = source.name;
            SceneManager.MoveGameObjectToScene(clone, targetScene);
            clone.transform.SetParent(parent, true);
            return clone;
        }

        private static void EnsureQualityController(GameObject displayRoot)
        {
            var qualityController = displayRoot.GetComponent<InteractiveFogQualityController>();
            if (qualityController == null)
                qualityController = displayRoot.AddComponent<InteractiveFogQualityController>();
            qualityController.fog = displayRoot.GetComponentInChildren<LocalVolumetricFog>(true);
            qualityController.simulation = displayRoot.GetComponentInChildren<FluidSimulation>(true);
            ConfigureQualityController(qualityController);
            qualityController.ApplyQualityNow();
            EditorUtility.SetDirty(qualityController);
        }

        private static void ConfigureQualityController(InteractiveFogQualityController controller)
        {
            controller.lowProfile = AssetDatabase.LoadAssetAtPath<InteractiveFogQualityProfile>(
                InteractiveFogEditor.InteractiveFogQualityAssetFactory.LowPath);
            controller.mediumProfile = AssetDatabase.LoadAssetAtPath<InteractiveFogQualityProfile>(
                InteractiveFogEditor.InteractiveFogQualityAssetFactory.MediumPath);
            controller.highProfile = AssetDatabase.LoadAssetAtPath<InteractiveFogQualityProfile>(
                InteractiveFogEditor.InteractiveFogQualityAssetFactory.HighPath);
            controller.development.reconstructedRendering = true;
            controller.development.temporalResolve = false;
            controller.development.bakedNoise = false;
            controller.development.fixedRateFluid = false;
            controller.development.reducedVorticityCadence = false;
            controller.development.statePreservingResize = false;
        }

        private static GameObject FindRoot(Scene scene, string objectName)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == objectName)
                    return root;
            }
            return null;
        }
    }
}
#endif
