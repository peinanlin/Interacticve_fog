using System;
using UnityEngine;

namespace InteractiveFog
{
    public enum InteractiveFogRenderPath { LegacyFullResolution, Reconstructed }
    public enum InteractiveFogNoiseMode { ProceduralFbm, Baked3DWithDetail, Baked3D }
    public enum InteractiveFogLightingMode { Full, Simplified }

    [Serializable]
    public sealed class InteractiveFogRenderQualitySettings
    {
        public InteractiveFogRenderPath path = InteractiveFogRenderPath.LegacyFullResolution;
        [Range(0.25f, 1f)] public float resolutionFraction = 1f;
        [Range(16, 160)] public int maximumStepCount = 60;
        [Range(8, 64)] public int minimumStepCount = 24;
        [Range(0f, 1f)] public float jitterStrength;
        public void Sanitize() { resolutionFraction = Mathf.Clamp(resolutionFraction, 0.25f, 1f); maximumStepCount = Mathf.Clamp(maximumStepCount, 16, 160); minimumStepCount = Mathf.Clamp(minimumStepCount, 8, Mathf.Min(64, maximumStepCount)); jitterStrength = Mathf.Clamp01(jitterStrength); }
    }

    [Serializable]
    public sealed class InteractiveFogReconstructionQualitySettings
    {
        [Range(0, 3)] public int bilateralRadius = 1;
        [Range(0.001f, 5f)] public float depthThreshold = 0.2f;
        public void Sanitize() { bilateralRadius = Mathf.Clamp(bilateralRadius, 0, 3); depthThreshold = Mathf.Clamp(depthThreshold, 0.001f, 5f); }
    }

    [Serializable]
    public sealed class InteractiveFogTemporalQualitySettings
    {
        public bool enabled;
        [Range(0f, 0.98f)] public float historyWeight = 0.82f;
        [Min(0.01f)] public float cameraCutDistance = 2f;
        [Range(0f, 1f)] public float neighborhoodClamp = 0.2f;
        [Range(0.001f, 1f)] public float reactiveOpacityThreshold = 0.08f;
        public void Sanitize() { historyWeight = Mathf.Clamp(historyWeight, 0f, 0.98f); cameraCutDistance = Mathf.Max(0.01f, cameraCutDistance); neighborhoodClamp = Mathf.Clamp01(neighborhoodClamp); reactiveOpacityThreshold = Mathf.Clamp(reactiveOpacityThreshold, 0.001f, 1f); }
    }

    [Serializable]
    public sealed class InteractiveFogShadingQualitySettings
    {
        public InteractiveFogNoiseMode noiseMode = InteractiveFogNoiseMode.ProceduralFbm;
        [Range(0, 4)] public int detailOctaves = 4;
        public InteractiveFogLightingMode lightingMode = InteractiveFogLightingMode.Full;
        public Texture3D baseNoise;
        public Texture3D detailNoise;
        public void Sanitize() { detailOctaves = Mathf.Clamp(detailOctaves, 0, 4); }
    }

    [Serializable]
    public sealed class InteractiveFogFluidQualitySettings
    {
        [Range(1, 120)] public int updatesPerSecond = 60;
        [Range(1, 4)] public int maximumCatchUpSteps = 2;
        [Range(1, 40)] public int resolutionScale = 6;
        [Range(1, 80)] public int pressureIterations = 4;
        [Range(1, 8)] public int vorticityCadence = 1;
        public bool interpolateDisplay;
        public void Sanitize() { updatesPerSecond = Mathf.Clamp(updatesPerSecond, 1, 120); maximumCatchUpSteps = Mathf.Clamp(maximumCatchUpSteps, 1, 4); resolutionScale = Mathf.Clamp(resolutionScale, 1, 40); pressureIterations = Mathf.Clamp(pressureIterations, 1, 80); vorticityCadence = Mathf.Clamp(vorticityCadence, 1, 8); }
    }

    [Serializable]
    public sealed class InteractiveFogCompatibilitySettings
    {
        public bool renderGameCameras = true;
        public bool renderSceneView;
        public bool renderPreviewCameras;
        public bool renderReflectionCameras;
        public bool requireComputeShaders = true;
        public bool requireDepthTexture = true;
        public bool allowTemporalFallback = true;
        public bool allowFormatFallback = true;
        [Tooltip("请求档位不可用时按顺序尝试的完整档位。空列表表示保留当前档位并报告失败。")]
        public InteractiveFogQualityTier[] fallbackOrder = Array.Empty<InteractiveFogQualityTier>();

        public void Sanitize()
        {
            fallbackOrder ??= Array.Empty<InteractiveFogQualityTier>();
        }
    }

    [Serializable]
    public sealed class InteractiveFogPerformanceBudget
    {
        [Min(0f)] public float targetMedianGpuMilliseconds = 3.53f;
        [Min(0f)] public float targetP95GpuMilliseconds = 4.5f;
        public void Sanitize() { targetMedianGpuMilliseconds = Mathf.Max(0f, targetMedianGpuMilliseconds); targetP95GpuMilliseconds = Mathf.Max(targetMedianGpuMilliseconds, targetP95GpuMilliseconds); }
    }

    [CreateAssetMenu(fileName = "InteractiveFogQualityProfile", menuName = "Interactive Fog/Quality Profile")]
    public sealed class InteractiveFogQualityProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int schemaVersion = 1;
        public InteractiveFogQualityTier tier = InteractiveFogQualityTier.High;
        public InteractiveFogRenderQualitySettings render = new InteractiveFogRenderQualitySettings();
        public InteractiveFogReconstructionQualitySettings reconstruction = new InteractiveFogReconstructionQualitySettings();
        public InteractiveFogTemporalQualitySettings temporal = new InteractiveFogTemporalQualitySettings();
        public InteractiveFogShadingQualitySettings shading = new InteractiveFogShadingQualitySettings();
        public InteractiveFogFluidQualitySettings fluid = new InteractiveFogFluidQualitySettings();
        public InteractiveFogCompatibilitySettings compatibility = new InteractiveFogCompatibilitySettings();
        public InteractiveFogPerformanceBudget budget = new InteractiveFogPerformanceBudget();
        public int SchemaVersion => schemaVersion;

        private void OnValidate() { Sanitize(); }
        public void Sanitize()
        {
            schemaVersion = Mathf.Max(1, schemaVersion);
            render ??= new InteractiveFogRenderQualitySettings(); reconstruction ??= new InteractiveFogReconstructionQualitySettings(); temporal ??= new InteractiveFogTemporalQualitySettings(); shading ??= new InteractiveFogShadingQualitySettings(); fluid ??= new InteractiveFogFluidQualitySettings(); compatibility ??= new InteractiveFogCompatibilitySettings(); budget ??= new InteractiveFogPerformanceBudget();
            render.Sanitize(); reconstruction.Sanitize(); temporal.Sanitize(); shading.Sanitize(); fluid.Sanitize(); compatibility.Sanitize(); budget.Sanitize();
        }

        public bool TryValidate(out string reason)
        {
            Sanitize();
            if (render.path == InteractiveFogRenderPath.LegacyFullResolution && !Mathf.Approximately(render.resolutionFraction, 1f)) { reason = "Legacy full-resolution profiles must use a resolution fraction of 1."; return false; }
            if (render.path == InteractiveFogRenderPath.Reconstructed && render.resolutionFraction >= 1f) { reason = "Reconstructed profiles must use a resolution fraction below 1."; return false; }
            if (tier == InteractiveFogQualityTier.High && temporal.enabled) { reason = "The High compatibility profile cannot enable temporal accumulation."; return false; }
            if (shading.noiseMode != InteractiveFogNoiseMode.ProceduralFbm && shading.baseNoise == null) { reason = "A baked-noise profile requires a base Texture3D."; return false; }
            reason = string.Empty; return true;
        }
    }
}
