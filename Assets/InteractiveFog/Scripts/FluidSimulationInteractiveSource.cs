using System.Collections.Generic;
using UnityEngine;

namespace InteractiveFog
{
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public sealed class FluidSimulationInteractiveSource : MonoBehaviour
    {
        private static readonly List<FluidSimulationInteractiveSource> ActiveSourcesInternal =
            new List<FluidSimulationInteractiveSource>();

        [Header("Simulation Routing")]
        [Tooltip("可选。指定后只向该 FluidSimulation 注入，避免同一场景中的多个 Demo 相互干扰。")]
        public FluidSimulation targetSimulation;

        [Header("Geometry")]
        public FluidSourceShape shapeType = FluidSourceShape.Sphere;
        [Min(0.001f)] public float radius = 1.5f;
        public Vector3 cubeSize = Vector3.one;

        [Header("Source")]
        public FluidSourceType sourceType = FluidSourceType.Velocity | FluidSourceType.Density;
        [Range(0f, 10f)] public float forceStrength = 1f;
        [Range(-1f, 1f)] public float addDensity = 1f;
        public bool killIfStill;

        [Header("Additional Velocity")]
        public FluidAdditionalVelocityType additionalVelocityType = FluidAdditionalVelocityType.None;
        public Vector3 constantVelocity;
        [Range(0f, 10f)] public float windStrength = 0.168f;

        [Header("Jet Emission")]
        [Tooltip("从 Geometry 中心持续施加定向喷射速度。关闭时保持文档原始扰动源行为。")]
        public bool jetEmission;
        [Tooltip("喷射的本地空间方向；火箭尾气通常使用 (0,-1,0)。")]
        public Vector3 localJetDirection = Vector3.down;
        [Range(0f, 20f)] public float jetSpeed = 5f;
        [Tooltip("喷流离开喷口后的径向展开速度，控制锥形张角。")]
        [Range(0f, 8f)] public float jetSpread = 1.1f;

        [Header("Advanced")]
        public FluidControlMode controlMode = FluidControlMode.Advanced;
        [Range(-4f, 4f)] public float centerFlow2DCoeff = 1f;
        [Range(-1f, 1f)] public float diffusion;

        private Vector3 previousPosition;
        private Vector3 measuredVelocity;
        private bool hasPreviousPosition;   

        public static IReadOnlyList<FluidSimulationInteractiveSource> ActiveSources => ActiveSourcesInternal;
        public Vector3 CurrentVelocity => measuredVelocity;

        private void OnEnable()
        {
            if (!ActiveSourcesInternal.Contains(this))
                ActiveSourcesInternal.Add(this);
            previousPosition = transform.position;
            measuredVelocity = Vector3.zero;
            hasPreviousPosition = true;
        }

        private void OnDisable()
        {
            ActiveSourcesInternal.Remove(this);
        }

        private void Update()
        {
            // Enter Play Mode options may preserve the static list while scene
            // objects are recreated. Self-heal the registration so a stationary
            // source is still gathered even when no transform change occurs.
            if (!ActiveSourcesInternal.Contains(this))
                ActiveSourcesInternal.Add(this);
            var deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
            var current = transform.position;
            measuredVelocity = hasPreviousPosition ? (current - previousPosition) / deltaTime : Vector3.zero;
            previousPosition = current;
            hasPreviousPosition = true;
        }

        internal bool TryBuildGpuData(out FluidSourceGpuData data)
        {
            data = default;
            if (!isActiveAndEnabled)
                return false;
            if (killIfStill && measuredVelocity.sqrMagnitude < 0.0001f)
                return false;

            var velocity = measuredVelocity;
            var wind = transform.forward * windStrength;
            switch (additionalVelocityType)
            {
                case FluidAdditionalVelocityType.Constant:
                    velocity += constantVelocity;
                    break;
                case FluidAdditionalVelocityType.Wind:
                    velocity += wind;
                    break;
                case FluidAdditionalVelocityType.Hybrid:
                    velocity += constantVelocity + wind;
                    break;
            }

            var safeCubeSize = new Vector3(
                Mathf.Max(0.001f, cubeSize.x),
                Mathf.Max(0.001f, cubeSize.y),
                Mathf.Max(0.001f, cubeSize.z));
            data.worldToLocal = Matrix4x4.TRS(transform.position, transform.rotation, safeCubeSize).inverse;
            data.positionRadius = new Vector4(transform.position.x, transform.position.y, transform.position.z,
                Mathf.Max(0.001f, radius));
            data.velocityDensity = new Vector4(
                velocity.x * forceStrength,
                velocity.y * forceStrength,
                velocity.z * forceStrength,
                addDensity);
            data.shapeDiffusionFlags = new Vector4(
                (float)shapeType,
                controlMode == FluidControlMode.Advanced ? diffusion : 0f,
                (float)(int)sourceType,
                1f);
            data.centerFlow = new Vector4(
                0f,
                0f,
                0f,
                controlMode == FluidControlMode.Advanced ? centerFlow2DCoeff : 0f);
            var jetDirection = localJetDirection.sqrMagnitude > 0.0001f
                ? transform.TransformDirection(localJetDirection.normalized)
                : -transform.up;
            data.jetDirectionSpeed = new Vector4(
                jetDirection.x,
                jetDirection.y,
                jetDirection.z,
                jetSpeed * forceStrength);
            data.jetSpreadEnabled = new Vector4(
                jetSpread * forceStrength,
                jetEmission ? 1f : 0f,
                0f,
                0f);
            return true;
        }

        private void OnDrawGizmosSelected()
        {
            var previousMatrix = Gizmos.matrix;
            Gizmos.color = new Color(0.15f, 1f, 0.35f, 0.45f);
            switch (shapeType)
            {
                case FluidSourceShape.Sphere:
                    Gizmos.DrawWireSphere(transform.position, radius);
                    break;
                case FluidSourceShape.Cube:
                    Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, cubeSize);
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                    break;
            }
            Gizmos.matrix = previousMatrix;
        }
    }
}
