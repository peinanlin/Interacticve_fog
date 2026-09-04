using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace InteractiveFog
{
    public enum LocalFogVolumeShape
    {
        Box = 0,
        Frustum = 1
    }

    public enum FogInteractionAreaShape
    {
        Box = 0,
        Cylinder = 1
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter), typeof(BoxCollider))]
    public sealed class LocalVolumetricFog : MonoBehaviour
    {
        private static readonly List<LocalVolumetricFog> RegisteredVolumes =
            new List<LocalVolumetricFog>(4);

        [Header("Shape")]
        public LocalFogVolumeShape volumeShape = LocalFogVolumeShape.Box;
        [Range(0.01f, 1f)] public float frustumNearWidthScale = 0.15f;
        [Range(0.01f, 1f)] public float frustumNearHeightScale = 1f;

        [Header("Volume")]
        public Material volumeMaterial;
        [Range(0f, 4f)] public float baseDensity = 1.15f;
        [Range(16, 160)] public int stepCount = 72;
        [Tooltip("根据射线在雾体中的实际长度减少短射线采样。关闭后始终使用 Step Count，便于对比旧效果。")]
        public bool adaptiveStepCount = true;
        [Tooltip("自适应采样时每条射线至少执行的步数。")]
        [Range(8, 64)] public int minimumStepCount = 24;
        [Range(0.01f, 4f)] public float noiseScale = 0.42f;
        [Range(0f, 1f)] public float noiseStrength = 0.72f;
        [Range(0f, 8f)] public float heightFalloff = 1.35f;
        public Vector3 noiseVelocity = new Vector3(0.06f, 0.015f, 0.035f);

        [Header("Density Color")]
        [FormerlySerializedAs("fogColor")]
        [Tooltip("低密度与尾迹边缘的颜色。")]
        [ColorUsage(false, true)]
        public Color thinFogColor = new Color(0.82f, 0.9f, 1f, 1f);
        [Tooltip("高密度与尾迹核心的颜色。")]
        [ColorUsage(false, true)]
        public Color denseFogColor = new Color(0.2f, 0.3f, 0.44f, 1f);
        [Tooltip("密度映射到颜色渐变的强度。数值越大，越容易进入浓雾颜色。")]
        [Range(0f, 8f)] public float densityColorStrength = 1.5f;
        [Tooltip("密度颜色曲线。小于 1 更早变深，大于 1 保留更多浅色边缘。")]
        [Range(0.1f, 4f)] public float densityColorContrast = 1f;
        [Tooltip("浓雾对光的近似吸收强度，只改变明暗，不改变密度。")]
        [Range(0f, 4f)] public float lightAbsorption = 0.6f;

        [Header("Interactive")]
        public bool interactive = true;
        public FluidSimulation simulation;
        public FogInteractionAreaShape interactionAreaShape = FogInteractionAreaShape.Box;
        [Range(0.01f, 0.5f)] public float interactionEdgeFade = 0.12f;
        [Range(0.01f, 1f)] public float interactionFeather = 0.3f;
        [Range(0f, 0.5f)] public float interactionBoundaryNoise = 0.08f;
        [Range(0.01f, 2f)] public float interactionBoundaryNoiseScale = 0.35f;
        [Range(0f, 2f)] public float initialDensity = 1f;
        [Tooltip("清雾区域保留的最低密度。透明清雾应保持为 0；非零值会在长射线中累计成暗色。")]
        [Range(0f, 0.25f)] public float minimumInteractionDensity;
        [Range(-4f, 4f)] public float densityScale = -1.4f;
        [Tooltip("放大模拟密度到体积雾的响应。文档中的 Density Scale 保持不变；交互通道不明显时优先提高本值。")]
        [Range(0.1f, 8f)] public float interactionResponse = 3f;
        [Range(0f, 1f)] public float densityReverseThreshold = 0.245f;
        [Range(-1f, 0f)] public float densityReverseMaxValue = -0.519f;
        [Range(0f, 12f)] public float velocityScale = 4.65f;
        [Range(0.001f, 0.5f)] public float lerpRange = 0.2f;

        [Header("Direct Player Clearing")]
        [Tooltip("即使流体模拟尚未就绪，也会在该目标周围形成稳定的清雾区域。")]
        public Transform directClearTarget;
        [Min(0.01f)] public float directClearRadius = 3.2f;
        [Tooltip("直接清雾球内保留的密度。透明清雾应保持为 0。")]
        [Range(0f, 0.25f)] public float directClearResidualDensity;
        [Range(0.05f, 0.95f)] public float directClearFeather = 0.7f;
        [Range(0f, 0.25f)] public float directClearNoise = 0.06f;

        private MeshRenderer meshRenderer;
        private MeshFilter meshFilter;
        private Mesh sourceVolumeMesh;
        private Mesh generatedFrustumMesh;
        private float generatedNearWidthScale = -1f;
        private float generatedNearHeightScale = -1f;
        private Material runtimeMaterial;
        private MaterialPropertyBlock propertyBlock;
        [SerializeField, HideInInspector] private bool rendererSuppressed;
        [SerializeField, HideInInspector] private bool rendererEnabledBeforeSuppression = true;

        public static IReadOnlyList<LocalVolumetricFog> ActiveVolumes => RegisteredVolumes;
        public Mesh RenderMesh => meshFilter != null ? meshFilter.sharedMesh : null;
        public Material RenderMaterial => meshRenderer != null ? meshRenderer.sharedMaterial : null;
        public Matrix4x4 RenderMatrix => transform.localToWorldMatrix;
        public Bounds WorldBounds => meshRenderer != null ? meshRenderer.bounds : new Bounds(transform.position, Vector3.zero);
        public bool ReconstructedRendering => rendererSuppressed;

        private static readonly int ThinFogColorId = Shader.PropertyToID("_ThinFogColor");
        private static readonly int DenseFogColorId = Shader.PropertyToID("_DenseFogColor");
        private static readonly int DensityColorStrengthId = Shader.PropertyToID("_DensityColorStrength");
        private static readonly int DensityColorContrastId = Shader.PropertyToID("_DensityColorContrast");
        private static readonly int LightAbsorptionId = Shader.PropertyToID("_LightAbsorption");
        private static readonly int BaseDensityId = Shader.PropertyToID("_BaseDensity");
        private static readonly int StepCountId = Shader.PropertyToID("_StepCount");
        private static readonly int AdaptiveStepsId = Shader.PropertyToID("_AdaptiveSteps");
        private static readonly int MinimumStepCountId = Shader.PropertyToID("_MinimumStepCount");
        private static readonly int MaximumStepLengthId = Shader.PropertyToID("_MaximumStepLength");
        private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
        private static readonly int NoiseStrengthId = Shader.PropertyToID("_NoiseStrength");
        private static readonly int HeightFalloffId = Shader.PropertyToID("_HeightFalloff");
        private static readonly int NoiseVelocityId = Shader.PropertyToID("_NoiseVelocity");
        private static readonly int VolumeWorldToLocalId = Shader.PropertyToID("_VolumeWorldToLocal");
        private static readonly int VolumeShapeId = Shader.PropertyToID("_VolumeShape");
        private static readonly int FrustumNearWidthScaleId = Shader.PropertyToID("_FrustumNearWidthScale");
        private static readonly int FrustumNearHeightScaleId = Shader.PropertyToID("_FrustumNearHeightScale");
        private static readonly int InteractiveId = Shader.PropertyToID("_Interactive");
        private static readonly int InteractionAreaShapeId = Shader.PropertyToID("_InteractionAreaShape");
        private static readonly int InteractionEdgeFadeId = Shader.PropertyToID("_InteractionEdgeFade");
        private static readonly int InteractionFeatherId = Shader.PropertyToID("_InteractionFeather");
        private static readonly int InteractionBoundaryNoiseId = Shader.PropertyToID("_InteractionBoundaryNoise");
        private static readonly int InteractionBoundaryNoiseScaleId = Shader.PropertyToID("_InteractionBoundaryNoiseScale");
        private static readonly int InitialDensityId = Shader.PropertyToID("_InitialDensity");
        private static readonly int MinimumInteractionDensityId = Shader.PropertyToID("_MinimumInteractionDensity");
        private static readonly int DensityScaleId = Shader.PropertyToID("_DensityScale");
        private static readonly int InteractionResponseId = Shader.PropertyToID("_InteractionResponse");
        private static readonly int ReverseThresholdId = Shader.PropertyToID("_DensityReverseThreshold");
        private static readonly int ReverseMaxId = Shader.PropertyToID("_DensityReverseMaxValue");
        private static readonly int VelocityScaleId = Shader.PropertyToID("_VelocityScale");
        private static readonly int LerpRangeId = Shader.PropertyToID("_LerpRange");
        private static readonly int DirectClearEnabledId = Shader.PropertyToID("_DirectClearEnabled");
        private static readonly int DirectClearPositionId = Shader.PropertyToID("_DirectClearPosition");
        private static readonly int DirectClearRadiusId = Shader.PropertyToID("_DirectClearRadius");
        private static readonly int DirectClearResidualDensityId = Shader.PropertyToID("_DirectClearResidualDensity");
        private static readonly int DirectClearFeatherId = Shader.PropertyToID("_DirectClearFeather");
        private static readonly int DirectClearNoiseId = Shader.PropertyToID("_DirectClearNoise");
        private static readonly int SimulationCenterId = Shader.PropertyToID("_SimulationCenter");
        private static readonly int SolverRangeId = Shader.PropertyToID("_SolverRange");
        private static readonly int SimulationIs3DId = Shader.PropertyToID("_SimulationIs3D");
        private static readonly int Density2DId = Shader.PropertyToID("_Density2D");
        private static readonly int Velocity2DId = Shader.PropertyToID("_Velocity2D");
        private static readonly int Density3DId = Shader.PropertyToID("_Density3D");
        private static readonly int Velocity3DId = Shader.PropertyToID("_Velocity3D");

        private void OnEnable()
        {
            if (!RegisteredVolumes.Contains(this))
                RegisteredVolumes.Add(this);
            EnsureComponents();
            ApplyProperties();
        }

        private void OnDisable()
        {
            RegisteredVolumes.Remove(this);
            SetReconstructedRendering(false);
            if (runtimeMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(runtimeMaterial);
                else
                    DestroyImmediate(runtimeMaterial);
                runtimeMaterial = null;
            }

            ReleaseGeneratedFrustumMesh();
        }

        private void OnValidate()
        {
            stepCount = Mathf.Clamp(stepCount, 16, 160);
            minimumStepCount = Mathf.Clamp(minimumStepCount, 8, Mathf.Min(64, stepCount));
            lerpRange = Mathf.Max(0.001f, lerpRange);
            interactionEdgeFade = Mathf.Clamp(interactionEdgeFade, 0.01f, 0.5f);
            interactionFeather = Mathf.Clamp(interactionFeather, 0.01f, 1f);
            interactionBoundaryNoise = Mathf.Clamp(interactionBoundaryNoise, 0f, 0.5f);
            interactionBoundaryNoiseScale = Mathf.Clamp(interactionBoundaryNoiseScale, 0.01f, 2f);
            minimumInteractionDensity = Mathf.Clamp(minimumInteractionDensity, 0f, 0.25f);
            directClearRadius = Mathf.Max(0.01f, directClearRadius);
            directClearResidualDensity = Mathf.Clamp(directClearResidualDensity, 0f, 0.25f);
            directClearFeather = Mathf.Clamp(directClearFeather, 0.05f, 0.95f);
            directClearNoise = Mathf.Clamp(directClearNoise, 0f, 0.25f);
            frustumNearWidthScale = Mathf.Clamp(frustumNearWidthScale, 0.01f, 1f);
            frustumNearHeightScale = Mathf.Clamp(frustumNearHeightScale, 0.01f, 1f);
            EnsureComponents();
            ApplyProperties();
        }

        private void LateUpdate()
        {
            EnsureComponents();
            if (simulation == null && Application.isPlaying)
                simulation = FindObjectOfType<FluidSimulation>();
            ApplyProperties();
        }

        public void SetReconstructedRendering(bool active)
        {
            EnsureComponents();
            if (meshRenderer == null)
                return;

            if (active)
            {
                if (!rendererSuppressed)
                    rendererEnabledBeforeSuppression = meshRenderer.enabled || rendererEnabledBeforeSuppression;
                meshRenderer.enabled = false;
                rendererSuppressed = true;
            }
            else
            {
                // Keep this idempotent so a domain/script reload between suppression and
                // restoration cannot strand the legacy High renderer in the disabled state.
                meshRenderer.enabled = rendererEnabledBeforeSuppression;
                rendererSuppressed = false;
            }
        }

        public void FillRenderPropertyBlock(MaterialPropertyBlock target)
        {
            if (target == null)
                return;
            EnsureComponents();
            target.Clear();
            PopulateRenderProperties(target);
        }

        private void EnsureComponents()
        {
            if (meshRenderer == null)
                meshRenderer = GetComponent<MeshRenderer>();
            if (meshFilter == null)
                meshFilter = GetComponent<MeshFilter>();
            EnsureVolumeMesh();
            var box = GetComponent<BoxCollider>();
            if (box != null)
                box.isTrigger = true;
            if (meshRenderer == null)
                return;

            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            var material = volumeMaterial;
            if (material == null)
            {
                if (runtimeMaterial == null)
                {
                    var shader = Shader.Find("InteractiveFog/LocalVolumetricFog");
                    if (shader != null)
                    {
                        runtimeMaterial = new Material(shader)
                        {
                            name = "Interactive Fog Runtime Material",
                            hideFlags = HideFlags.HideAndDontSave
                        };
                    }
                }
                material = runtimeMaterial;
            }
            if (material != null && meshRenderer.sharedMaterial != material)
                meshRenderer.sharedMaterial = material;
        }

        private void EnsureVolumeMesh()
        {
            if (meshFilter == null)
                return;

            if (sourceVolumeMesh == null && meshFilter.sharedMesh != generatedFrustumMesh)
                sourceVolumeMesh = meshFilter.sharedMesh;

            if (volumeShape != LocalFogVolumeShape.Frustum)
            {
                if (generatedFrustumMesh != null && meshFilter.sharedMesh == generatedFrustumMesh)
                    meshFilter.sharedMesh = sourceVolumeMesh;
                return;
            }

            if (generatedFrustumMesh == null)
            {
                generatedFrustumMesh = new Mesh
                {
                    name = "Interactive Fog Frustum (Generated)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                generatedFrustumMesh.MarkDynamic();
                generatedNearWidthScale = -1f;
                generatedNearHeightScale = -1f;
            }

            if (!Mathf.Approximately(generatedNearWidthScale, frustumNearWidthScale) ||
                !Mathf.Approximately(generatedNearHeightScale, frustumNearHeightScale))
            {
                RebuildFrustumMesh();
                generatedNearWidthScale = frustumNearWidthScale;
                generatedNearHeightScale = frustumNearHeightScale;
            }

            if (meshFilter.sharedMesh != generatedFrustumMesh)
                meshFilter.sharedMesh = generatedFrustumMesh;
        }

        private void RebuildFrustumMesh()
        {
            var nearHalfWidth = 0.5f * frustumNearWidthScale;
            var nearHalfHeight = 0.5f * frustumNearHeightScale;
            const float farHalfWidth = 0.5f;
            const float farHalfHeight = 0.5f;
            const float nearZ = -0.5f;
            const float farZ = 0.5f;

            generatedFrustumMesh.Clear();
            generatedFrustumMesh.vertices = new[]
            {
                new Vector3(-nearHalfWidth, -nearHalfHeight, nearZ),
                new Vector3( nearHalfWidth, -nearHalfHeight, nearZ),
                new Vector3( nearHalfWidth,  nearHalfHeight, nearZ),
                new Vector3(-nearHalfWidth,  nearHalfHeight, nearZ),
                new Vector3(-farHalfWidth, -farHalfHeight, farZ),
                new Vector3( farHalfWidth, -farHalfHeight, farZ),
                new Vector3( farHalfWidth,  farHalfHeight, farZ),
                new Vector3(-farHalfWidth,  farHalfHeight, farZ)
            };
            generatedFrustumMesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 4, 7, 0, 7, 3,
                1, 2, 6, 1, 6, 5,
                0, 1, 5, 0, 5, 4,
                3, 7, 6, 3, 6, 2
            };
            generatedFrustumMesh.RecalculateNormals();
            generatedFrustumMesh.bounds = new Bounds(Vector3.zero, Vector3.one);
        }

        private void ReleaseGeneratedFrustumMesh()
        {
            if (generatedFrustumMesh == null)
                return;

            if (meshFilter != null && meshFilter.sharedMesh == generatedFrustumMesh)
                meshFilter.sharedMesh = sourceVolumeMesh;
            if (Application.isPlaying)
                Destroy(generatedFrustumMesh);
            else
                DestroyImmediate(generatedFrustumMesh);
            generatedFrustumMesh = null;
        }

        private void ApplyProperties()
        {
            if (meshRenderer == null || meshRenderer.sharedMaterial == null)
                return;
            if (propertyBlock == null)
                propertyBlock = new MaterialPropertyBlock();
            // Preserve any renderer-owned values exactly as the pre-tier legacy path did.
            // The explicit reconstructed path clears its caller-owned block separately.
            meshRenderer.GetPropertyBlock(propertyBlock);
            PopulateRenderProperties(propertyBlock);
            meshRenderer.SetPropertyBlock(propertyBlock);
        }

        private void PopulateRenderProperties(MaterialPropertyBlock target)
        {
            target.SetColor(ThinFogColorId, thinFogColor);
            target.SetColor(DenseFogColorId, denseFogColor);
            target.SetFloat(DensityColorStrengthId, densityColorStrength);
            target.SetFloat(DensityColorContrastId, densityColorContrast);
            target.SetFloat(LightAbsorptionId, lightAbsorption);
            target.SetFloat(BaseDensityId, baseDensity);
            target.SetFloat(StepCountId, stepCount);
            target.SetFloat(AdaptiveStepsId, adaptiveStepCount ? 1f : 0f);
            target.SetFloat(MinimumStepCountId, Mathf.Min(minimumStepCount, stepCount));
            var worldScale = transform.lossyScale;
            var referenceLength = Mathf.Max(Mathf.Abs(worldScale.x),
                Mathf.Max(Mathf.Abs(worldScale.y), Mathf.Abs(worldScale.z)));
            target.SetFloat(MaximumStepLengthId,
                Mathf.Max(referenceLength / Mathf.Max(stepCount, 1), 0.001f));
            target.SetFloat(NoiseScaleId, noiseScale);
            target.SetFloat(NoiseStrengthId, noiseStrength);
            target.SetFloat(HeightFalloffId, heightFalloff);
            target.SetVector(NoiseVelocityId, noiseVelocity);
            target.SetMatrix(VolumeWorldToLocalId, transform.worldToLocalMatrix);
            target.SetFloat(VolumeShapeId, (float)volumeShape);
            target.SetFloat(FrustumNearWidthScaleId, frustumNearWidthScale);
            target.SetFloat(FrustumNearHeightScaleId, frustumNearHeightScale);
            var simulationReady = interactive && simulation != null && simulation.State &&
                                  simulation.DensityTexture != null && simulation.VelocityTexture != null;
            target.SetFloat(InteractiveId, simulationReady ? 1f : 0f);
            target.SetFloat(InteractionAreaShapeId, (float)interactionAreaShape);
            target.SetFloat(InteractionEdgeFadeId, interactionEdgeFade);
            target.SetFloat(InteractionFeatherId, interactionFeather);
            target.SetFloat(InteractionBoundaryNoiseId, interactionBoundaryNoise);
            target.SetFloat(InteractionBoundaryNoiseScaleId, interactionBoundaryNoiseScale);
            target.SetFloat(InitialDensityId, initialDensity);
            target.SetFloat(MinimumInteractionDensityId, minimumInteractionDensity);
            target.SetFloat(DensityScaleId, densityScale);
            target.SetFloat(InteractionResponseId, interactionResponse);
            target.SetFloat(ReverseThresholdId, densityReverseThreshold);
            target.SetFloat(ReverseMaxId, densityReverseMaxValue);
            target.SetFloat(VelocityScaleId, velocityScale);
            target.SetFloat(LerpRangeId, lerpRange);
            target.SetFloat(DirectClearEnabledId, directClearTarget != null ? 1f : 0f);
            target.SetVector(DirectClearPositionId,
                directClearTarget != null ? directClearTarget.position : Vector3.zero);
            target.SetFloat(DirectClearRadiusId, directClearRadius);
            target.SetFloat(DirectClearResidualDensityId, directClearResidualDensity);
            target.SetFloat(DirectClearFeatherId, directClearFeather);
            target.SetFloat(DirectClearNoiseId, directClearNoise);

            if (simulation != null)
            {
                target.SetVector(SimulationCenterId, simulation.SimulationCenter);
                target.SetVector(SolverRangeId, simulation.SimulationRange);
                target.SetFloat(SimulationIs3DId, simulation.Is3D ? 1f : 0f);
                if (simulation.Is3D)
                {
                    if (simulation.DensityTexture != null)
                        target.SetTexture(Density3DId, simulation.DensityTexture);
                    if (simulation.VelocityTexture != null)
                        target.SetTexture(Velocity3DId, simulation.VelocityTexture);
                }
                else
                {
                    if (simulation.DensityTexture != null)
                        target.SetTexture(Density2DId, simulation.DensityTexture);
                    if (simulation.VelocityTexture != null)
                        target.SetTexture(Velocity2DId, simulation.VelocityTexture);
                }
            }
        }
    }
}
