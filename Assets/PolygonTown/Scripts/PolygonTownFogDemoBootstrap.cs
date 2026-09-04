using InteractiveFog;
using UnityEngine;

namespace PolygonTown.Demo
{
    [DefaultExecutionOrder(-500)]
    public sealed class PolygonTownFogDemoBootstrap : MonoBehaviour
    {
        [Header("Scene Fog References")]
        [SerializeField] private LocalVolumetricFog fogVolume;
        [SerializeField] private FluidSimulation simulation;
        [SerializeField] private bool followFogVolumeOnXZ = true;
        [SerializeField] private bool followSimulationOnXZ = true;

        private Transform player;
        private bool fogVisible = true;
        private float fogVolumeCenterY;
        private float simulationCenterY;

        private void Start()
        {
            SetupDemo();
        }

        private void Update()
        {
            if (followSimulationOnXZ)
                FollowInteractionSimulation();
            if (followFogVolumeOnXZ)
                FollowFogVolume();

            if (Input.GetKeyDown(KeyCode.F) && fogVolume != null)
            {
                fogVisible = !fogVisible;
                fogVolume.gameObject.SetActive(fogVisible);
            }

            if (Input.GetKeyDown(KeyCode.R) && simulation != null)
                simulation.ResetSimulation();
        }

        private void SetupDemo()
        {
            player = FindPlayer();
            if (player == null)
            {
                Debug.LogError("PolygonTown Fog Demo: an active player Animator was not found.");
                enabled = false;
                return;
            }

            RenderSettings.fog = false;
            ResolveSceneFogReferences();
            if (simulation == null || fogVolume == null)
            {
                Debug.LogError("PolygonTown Fog Demo: the serialized fog volume or fluid simulation is missing from the scene.");
                enabled = false;
                return;
            }

            SetupPlayer(player);
            SetupSimulation(player);
            SetupFogVolume(player);
            FollowInteractionSimulation();
            FollowFogVolume();
            SetupCamera(player);

            Debug.Log("PolygonTown Fog Demo ready. WASD move, Space jump, Shift sprint, mouse orbit, F fog, R reset.");
        }

        private void ResolveSceneFogReferences()
        {
            if (simulation == null)
                simulation = GetComponentInChildren<FluidSimulation>(true);

            if (fogVolume == null)
                fogVolume = GetComponentInChildren<LocalVolumetricFog>(true);

            if (fogVolume != null)
                fogVisible = fogVolume.gameObject.activeSelf;
        }

        private static Transform FindPlayer()
        {
            var animators = FindObjectsOfType<Animator>(true);
            foreach (var animator in animators)
            {
                if (animator.gameObject.name == "SciFiWarrior_Player" && animator.gameObject.activeInHierarchy)
                    return animator.transform;
            }

            foreach (var animator in animators)
            {
                if (animator.gameObject.name == "Character_Daughter_01" && animator.gameObject.activeInHierarchy)
                    return animator.transform;
            }

            foreach (var animator in animators)
            {
                if (animator.gameObject.activeInHierarchy)
                    return animator.transform;
            }

            return null;
        }

        private void SetupPlayer(Transform character)
        {
            var animator = character.GetComponent<Animator>();
            if (animator != null)
                animator.applyRootMotion = false;

            var characterController = character.GetComponent<CharacterController>();
            if (characterController == null)
                characterController = character.gameObject.AddComponent<CharacterController>();
            characterController.height = 1.7f;
            characterController.radius = 0.3f;
            characterController.center = new Vector3(0f, 0.85f, 0f);
            characterController.stepOffset = 0.3f;
            characterController.slopeLimit = 50f;

            var controller = character.GetComponent<ThirdPersonFogController>();
            if (controller == null)
                controller = character.gameObject.AddComponent<ThirdPersonFogController>();
            controller.characterController = characterController;
            controller.animator = animator;

            var sourceTransform = character.Find("Fog Interaction Source");
            if (sourceTransform == null)
            {
                var sourceObject = new GameObject("Fog Interaction Source");
                sourceTransform = sourceObject.transform;
                sourceTransform.SetParent(character, false);
                sourceTransform.localPosition = new Vector3(0f, 0.9f, 0f);
            }

            var source = sourceTransform.GetComponent<FluidSimulationInteractiveSource>();
            if (source == null)
                source = sourceTransform.gameObject.AddComponent<FluidSimulationInteractiveSource>();
            source.shapeType = FluidSourceShape.Sphere;
            source.radius = 0.8f;
            source.sourceType = FluidSourceType.Velocity | FluidSourceType.Density;
            source.forceStrength = 1.35f;
            source.addDensity = 1f;
            source.killIfStill = false;
            source.additionalVelocityType = FluidAdditionalVelocityType.Wind;
            source.windStrength = 0.25f;
            source.jetEmission = false;
            source.controlMode = FluidControlMode.Advanced;
            source.diffusion = 0.55f;
            source.targetSimulation = simulation;
        }

        private void SetupSimulation(Transform character)
        {
            simulationCenterY = simulation.transform.position.y;
            simulation.following = character;
        }

        private void SetupFogVolume(Transform character)
        {
            fogVolume.simulation = simulation;
            var sourceTransform = character.Find("Fog Interaction Source");
            fogVolume.directClearTarget = sourceTransform != null ? sourceTransform : character;
            fogVolumeCenterY = fogVolume.transform.position.y;
        }

        private void FollowFogVolume()
        {
            if (player == null || fogVolume == null)
                return;

            var fogPosition = fogVolume.transform.position;
            fogPosition.x = player.position.x;
            fogPosition.y = fogVolumeCenterY;
            fogPosition.z = player.position.z;
            fogVolume.transform.position = fogPosition;
        }

        private void FollowInteractionSimulation()
        {
            if (player == null || simulation == null)
                return;

            var simulationOffset = simulation.followOffset;
            simulationOffset.x = 0f;
            simulationOffset.y = simulationCenterY - player.position.y;
            simulationOffset.z = 0f;
            simulation.followOffset = simulationOffset;
        }

        private static void SetupCamera(Transform character)
        {
            var cameras = FindObjectsOfType<Camera>(true);
            Camera sceneCamera = null;
            foreach (var candidate in cameras)
            {
                if (candidate.isActiveAndEnabled)
                {
                    sceneCamera = candidate;
                    break;
                }
            }

            if (sceneCamera == null)
            {
                var cameraObject = new GameObject("Third Person Camera");
                sceneCamera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
            }

            sceneCamera.gameObject.tag = "MainCamera";
            sceneCamera.fieldOfView = 60f;

            var cameraRig = sceneCamera.GetComponent<ThirdPersonFogCamera>();
            if (cameraRig == null)
                cameraRig = sceneCamera.gameObject.AddComponent<ThirdPersonFogCamera>();
            cameraRig.target = character;
        }

        private void OnGUI()
        {
            const float width = 430f;
            GUILayout.BeginArea(new Rect(16f, 16f, width, 170f), GUI.skin.box);
            GUILayout.Label("PolygonTown - Interactive Fog 3D");
            GUILayout.Label("WASD: Move    Space: Jump    Left Shift: Sprint");
            GUILayout.Label("Mouse: Orbit camera    Wheel: Zoom    Esc: Release cursor");
            GUILayout.Label("F: Toggle fog    R: Reset fluid simulation");
            GUILayout.Label("Player-following fog + soft cylindrical interaction area");
            GUILayout.Label("Character animation: Sci-Fi Warrior idle / run / jump");
            GUILayout.EndArea();
        }
    }
}
