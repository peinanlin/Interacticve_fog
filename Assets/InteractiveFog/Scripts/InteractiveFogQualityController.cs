using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace InteractiveFog
{
    public enum InteractiveFogQualityTier { Low, Medium, High }

    [Serializable]
    public struct InteractiveFogQualityPreset
    {
        [Range(16, 160)] public int fogStepCount;
        [Range(8, 64)] public int fogMinimumStepCount;
        [Range(1, 40)] public int fluidResolutionScale;
        [Range(1, 80)] public int fluidPressureIterations;
        public InteractiveFogQualityPreset(int fogSteps, int minimumFogSteps, int fluidScale, int pressureIterations) { fogStepCount = fogSteps; fogMinimumStepCount = minimumFogSteps; fluidResolutionScale = fluidScale; fluidPressureIterations = pressureIterations; }
        public InteractiveFogQualityPreset Sanitized() { fogStepCount = Mathf.Clamp(fogStepCount, 16, 160); fogMinimumStepCount = Mathf.Clamp(fogMinimumStepCount, 8, Mathf.Min(64, fogStepCount)); fluidResolutionScale = Mathf.Clamp(fluidResolutionScale, 1, 40); fluidPressureIterations = Mathf.Clamp(fluidPressureIterations, 1, 80); return this; }
    }

    [Serializable]
    public sealed class InteractiveFogDevelopmentToggles
    {
        [Tooltip("Medium/Low 使用低分辨率重建路径。关闭时安全回退到原透明雾。")]
        public bool reconstructedRendering;
        public bool temporalResolve;
        public bool bakedNoise;
        public bool fixedRateFluid;
        public bool reducedVorticityCadence;
        public bool statePreservingResize;
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class InteractiveFogQualityController : MonoBehaviour
    {
        [Header("Selection")]
        [Tooltip("开启后自动映射 Unity 的 Performant / Balanced / High Fidelity。")]
        public bool followUnityQualityLevel = true;
        [Tooltip("关闭自动映射后使用该档位。")]
        public InteractiveFogQualityTier manualQuality = InteractiveFogQualityTier.High;

        [Header("Targets")]
        public LocalVolumetricFog fog;
        public FluidSimulation simulation;

        [Header("Architecture Profiles")]
        public InteractiveFogQualityProfile lowProfile;
        public InteractiveFogQualityProfile mediumProfile;
        public InteractiveFogQualityProfile highProfile;

        [Header("Development A/B Toggles")]
        public InteractiveFogDevelopmentToggles development = new InteractiveFogDevelopmentToggles();

        [Header("Legacy Embedded Presets")]
        [Tooltip("Profile 尚未绑定时使用，保留旧场景序列化兼容性。")]
        public InteractiveFogQualityPreset lowQuality = new InteractiveFogQualityPreset(32, 16, 3, 2);
        public InteractiveFogQualityPreset mediumQuality = new InteractiveFogQualityPreset(44, 20, 4, 3);
        public InteractiveFogQualityPreset highQuality = new InteractiveFogQualityPreset(60, 24, 6, 4);

        [SerializeField, HideInInspector] private InteractiveFogQualityTier requestedTier;
        [SerializeField, HideInInspector] private InteractiveFogQualityTier activeTier;
        [SerializeField, HideInInspector] private bool usingFallback;
        [SerializeField, HideInInspector] private string transitionMessage = string.Empty;
        private int lastUnityQualityLevel = -1;
        private InteractiveFogQualityTier lastAppliedTier = (InteractiveFogQualityTier)(-1);

        public static InteractiveFogQualityController Active { get; private set; }
        public InteractiveFogQualityTier RequestedTier => requestedTier;
        public InteractiveFogQualityTier ActiveTier => activeTier;
        public InteractiveFogQualityProfile ActiveProfile { get; private set; }
        public bool UsingFallback => usingFallback;
        public string TransitionMessage => transitionMessage;
        public bool ReconstructedRenderingActive => ActiveProfile != null && ActiveProfile.render.path == InteractiveFogRenderPath.Reconstructed && development.reconstructedRendering;
        public string DiagnosticsSummary => $"Requested={requestedTier}, Active={activeTier}, Fallback={usingFallback}, Reconstructed={development.reconstructedRendering}, Temporal={development.temporalResolve}, BakedNoise={development.bakedNoise}, FixedFluid={development.fixedRateFluid}, VorticityCadence={development.reducedVorticityCadence}, PreserveResize={development.statePreservingResize}";

#if UNITY_EDITOR
        public static Func<InteractiveFogQualityProfile, string> CapabilityEvaluatorOverrideForTests { get; set; }
#endif

        private void Reset() { ResolveReferences(); ApplyQualityNow(); }
        private void OnEnable() { Active = this; ResolveReferences(); ApplyQualityNow(); }
        private void OnDisable() { if (Active == this) Active = null; if (fog != null) fog.SetReconstructedRendering(false); }
        private void OnValidate() { lowQuality = lowQuality.Sanitized(); mediumQuality = mediumQuality.Sanitized(); highQuality = highQuality.Sanitized(); development ??= new InteractiveFogDevelopmentToggles(); ResolveReferences(); ApplyQualityNow(); }
        private void Update()
        {
            if (!Application.isPlaying) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // These are runtime Game-view controls, not Unity Editor global shortcuts.
            // Keeping them as plain letters avoids Shortcut Manager conflicts entirely.
            if (Input.GetKeyDown(KeyCode.C))
                QualitySettings.SetQualityLevel(2, true);
            if (Input.GetKeyDown(KeyCode.X))
                QualitySettings.SetQualityLevel(1, true);
            if (Input.GetKeyDown(KeyCode.Z))
                QualitySettings.SetQualityLevel(0, true);
#endif
            var unityQualityLevel = QualitySettings.GetQualityLevel();
            var tier = ResolveTier();
            if (tier != lastAppliedTier || (followUnityQualityLevel && unityQualityLevel != lastUnityQualityLevel)) ApplyQualityNow();
        }
        private void OnTransformChildrenChanged() { ResolveReferences(); ApplyQualityNow(); }

        [ContextMenu("Apply Quality Now")]
        public void ApplyQualityNow()
        {
            ResolveReferences(); requestedTier = ResolveTier();
            var requestedProfile = GetProfile(requestedTier);
            usingFallback = false; transitionMessage = string.Empty;
            if (requestedProfile != null && !requestedProfile.TryValidate(out var validationReason))
            {
                RejectTransition($"Profile rejected: {validationReason}");
                return;
            }

            var selectedProfile = requestedProfile;
            var selectedTier = requestedTier;
            if (requestedProfile != null && TryGetCapabilityIssue(requestedProfile, out var capabilityIssue))
            {
                selectedProfile = null;
                var fallbackOrder = requestedProfile.compatibility.fallbackOrder;
                for (var index = 0; index < fallbackOrder.Length; index++)
                {
                    var fallbackTier = fallbackOrder[index];
                    if (fallbackTier == requestedTier)
                        continue;
                    var candidate = GetProfile(fallbackTier);
                    if (candidate == null || !candidate.TryValidate(out _) || TryGetCapabilityIssue(candidate, out _))
                        continue;
                    selectedProfile = candidate;
                    selectedTier = fallbackTier;
                    break;
                }
                if (selectedProfile == null)
                {
                    RejectTransition($"Capability fallback failed: {capabilityIssue}");
                    return;
                }
                usingFallback = true;
                transitionMessage = $"{capabilityIssue} Falling back from {requestedTier} to {selectedTier}.";
            }

            ActiveProfile = selectedProfile; activeTier = selectedTier;
            if (selectedProfile == null)
            {
                ApplyLegacyPreset(GetPreset(activeTier).Sanitized()); usingFallback = true;
                transitionMessage = "No profile asset is bound; using the legacy embedded preset.";
            }
            else
            {
                ApplyProfile(selectedProfile);
                if (selectedProfile.render.path == InteractiveFogRenderPath.Reconstructed && !development.reconstructedRendering) { usingFallback = true; AppendTransitionMessage("Reconstructed rendering is disabled; using the legacy renderer."); }
            }
            lastUnityQualityLevel = QualitySettings.GetQualityLevel(); lastAppliedTier = requestedTier;
        }

        private void RejectTransition(string reason)
        {
            usingFallback = true;
            transitionMessage = reason;
            lastUnityQualityLevel = QualitySettings.GetQualityLevel();
            lastAppliedTier = requestedTier;
        }

        private bool TryGetCapabilityIssue(InteractiveFogQualityProfile profile, out string reason)
        {
#if UNITY_EDITOR
            if (CapabilityEvaluatorOverrideForTests != null)
            {
                reason = CapabilityEvaluatorOverrideForTests(profile) ?? string.Empty;
                return !string.IsNullOrEmpty(reason);
            }
#endif
            if (profile.compatibility.requireComputeShaders && !SystemInfo.supportsComputeShaders)
            {
                reason = "Compute shaders are not supported.";
                return true;
            }
            if (simulation != null)
            {
                var densitySupported = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RHalf);
                var velocityFormat = simulation.Is3D ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.RGHalf;
                if (!densitySupported || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(velocityFormat))
                {
                    reason = $"Required fluid random-write formats RHalf/{velocityFormat} are not supported.";
                    return true;
                }
            }
            if (profile.render.path == InteractiveFogRenderPath.Reconstructed)
            {
                if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Render) ||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, FormatUsage.Render))
                {
                    reason = "Required reconstructed-fog render-target formats are not supported.";
                    return true;
                }
            }
            reason = string.Empty;
            return false;
        }

        private void ApplyProfile(InteractiveFogQualityProfile profile)
        {
            if (fog != null) { fog.stepCount = profile.render.maximumStepCount; fog.adaptiveStepCount = true; fog.minimumStepCount = profile.render.minimumStepCount; fog.SetReconstructedRendering(profile.render.path == InteractiveFogRenderPath.Reconstructed && development.reconstructedRendering); }
            if (simulation != null)
            {
                // Changing resolution on the legacy resource owner releases every field before
                // reallocating it. Keep the current grid until allocate-resample-swap is available,
                // otherwise a quality switch visibly erases the sphere wake.
                if (development.statePreservingResize && simulation.resolutionScale != profile.fluid.resolutionScale)
                {
                    usingFallback = true;
                    AppendTransitionMessage($"State-preserving resize is not active yet; keeping fluid scale {simulation.resolutionScale} instead of clearing the field for {profile.fluid.resolutionScale}.");
                }
                simulation.pressureIteration = profile.fluid.pressureIterations;
                simulation.ConfigureQualityScheduling(development.fixedRateFluid, profile.fluid.updatesPerSecond,
                    profile.fluid.maximumCatchUpSteps,
                    development.reducedVorticityCadence ? profile.fluid.vorticityCadence : 1);
            }
        }

        private void AppendTransitionMessage(string message)
        {
            transitionMessage = string.IsNullOrEmpty(transitionMessage)
                ? message
                : transitionMessage + " " + message;
        }

        private void ApplyLegacyPreset(InteractiveFogQualityPreset preset)
        {
            if (fog != null) { fog.stepCount = preset.fogStepCount; fog.adaptiveStepCount = true; fog.minimumStepCount = preset.fogMinimumStepCount; fog.SetReconstructedRendering(false); }
            if (simulation != null) { simulation.resolutionScale = preset.fluidResolutionScale; simulation.pressureIteration = preset.fluidPressureIterations; simulation.ConfigureQualityScheduling(false, 50, 1, 1); }
        }

        private void ResolveReferences() { if (fog == null) fog = GetComponentInChildren<LocalVolumetricFog>(true); if (simulation == null) simulation = GetComponentInChildren<FluidSimulation>(true); if (simulation == null && fog != null) simulation = fog.simulation; }
        private InteractiveFogQualityProfile GetProfile(InteractiveFogQualityTier tier) => tier switch { InteractiveFogQualityTier.Low => lowProfile, InteractiveFogQualityTier.Medium => mediumProfile, _ => highProfile };
        private InteractiveFogQualityPreset GetPreset(InteractiveFogQualityTier tier) => tier switch { InteractiveFogQualityTier.Low => lowQuality, InteractiveFogQualityTier.Medium => mediumQuality, _ => highQuality };

        private InteractiveFogQualityTier ResolveTier()
        {
            if (!followUnityQualityLevel) return manualQuality;
            var qualityLevel = QualitySettings.GetQualityLevel(); var qualityNames = QualitySettings.names;
            if (qualityLevel >= 0 && qualityLevel < qualityNames.Length)
            {
                var qualityName = qualityNames[qualityLevel];
                if (qualityName.IndexOf("performant", StringComparison.OrdinalIgnoreCase) >= 0 || qualityName.IndexOf("low", StringComparison.OrdinalIgnoreCase) >= 0) return InteractiveFogQualityTier.Low;
                if (qualityName.IndexOf("balanced", StringComparison.OrdinalIgnoreCase) >= 0 || qualityName.IndexOf("medium", StringComparison.OrdinalIgnoreCase) >= 0) return InteractiveFogQualityTier.Medium;
                if (qualityName.IndexOf("high", StringComparison.OrdinalIgnoreCase) >= 0) return InteractiveFogQualityTier.High;
            }
            if (qualityLevel <= 0) return InteractiveFogQualityTier.Low;
            return qualityLevel >= qualityNames.Length - 1 ? InteractiveFogQualityTier.High : InteractiveFogQualityTier.Medium;
        }
    }
}
