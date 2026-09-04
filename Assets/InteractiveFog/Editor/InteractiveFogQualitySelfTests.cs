using System;
using System.IO;
using InteractiveFog;
using UnityEditor;
using UnityEngine;

namespace InteractiveFogEditor
{
    public static class InteractiveFogQualitySelfTests
    {
        [MenuItem("Tools/Interactive Fog/Run Quality Self Tests")]
        public static void Run()
        {
            TestSanitization(); TestFixedStepScheduling(); TestDevelopmentToggleDiagnostics();
            TestTimeCorrectFluidCoefficients(); TestVorticityCadence();
            TestHighCompatibilityPath(); TestSharedRenderPropertyBlock(); TestVolumeRegistryLifecycle();
            TestReconstructedShaderContract(); TestHighPassIsolation();
            TestRejectedProfilePreservesActiveState();
            TestDeterministicCapabilityFallback();
            if (!InteractiveFogQualityAssetFactory.ValidateQualityAssets(false)) throw new InvalidOperationException("Default quality asset validation failed.");
            Debug.Log("Interactive Fog quality self tests passed.");
        }

        private static void TestHighPassIsolation()
        {
            const string shaderPath = "Assets/InteractiveFog/Shaders/LocalVolumetricFog.shader";
            var source = File.ReadAllText(shaderPath);
            Assert(source.Contains("LocalVolumetricFogLegacy.hlsl") &&
                   source.Contains("\"LightMode\" = \"UniversalForward\""),
                "High no longer uses the isolated legacy UniversalForward pass.");
            Assert(!source.Contains("\"LightMode\" = \"SRPDefaultUnlit\""),
                "A reconstructed pass can still be selected by URP's normal transparent list.");
            var explicitPassCount = source.Split(
                new[] { "\"LightMode\" = \"InteractiveFogReconstructed\"" },
                StringSplitOptions.None).Length - 1;
            Assert(explicitPassCount == 3,
                "The reconstructed passes are not all isolated behind their custom LightMode tag.");
        }

        private static void TestFixedStepScheduling()
        {
            var steps = FluidSimulation.CalculateFixedStepSchedule(0.0, 0.25f, 60, 2,
                out var remaining, out var dropped);
            Assert(steps == 2, "A long frame exceeded or failed to use the configured catch-up limit.");
            Assert(dropped > 0.19 && remaining >= 0.0 && remaining < 1.0 / 60.0,
                "Excess fixed-step time was not dropped and retained deterministically.");
            steps = FluidSimulation.CalculateFixedStepSchedule(remaining, 1f / 120f, 60, 2,
                out remaining, out dropped);
            Assert(steps <= 1 && dropped == 0.0,
                "A short render frame produced an unexpected dispatch burst or dropped time.");
        }

        private static void TestDevelopmentToggleDiagnostics()
        {
            var root = new GameObject("Interactive Fog Toggle Diagnostics Test");
            var controller = root.AddComponent<InteractiveFogQualityController>();
            controller.development.reconstructedRendering = true;
            controller.development.temporalResolve = true;
            controller.development.bakedNoise = true;
            controller.development.fixedRateFluid = true;
            controller.development.reducedVorticityCadence = true;
            controller.development.statePreservingResize = true;
            var diagnostics = controller.DiagnosticsSummary;
            Assert(diagnostics.Contains("Reconstructed=True") && diagnostics.Contains("Temporal=True") &&
                   diagnostics.Contains("BakedNoise=True") && diagnostics.Contains("FixedFluid=True") &&
                   diagnostics.Contains("VorticityCadence=True") && diagnostics.Contains("PreserveResize=True"),
                "One or more independent development toggles are missing from diagnostics.");
            UnityEngine.Object.DestroyImmediate(root);
        }

        private static void TestTimeCorrectFluidCoefficients()
        {
            const float densityReferenceRetention = 0.9966f;
            const float velocityReferenceRetention = 0.9992f;
            var expectedDensityAfterOneSecond = Mathf.Pow(densityReferenceRetention, 50f);
            var expectedVelocityAfterOneSecond = Mathf.Pow(velocityReferenceRetention, 50f);
            foreach (var rate in new[] { 20, 30, 60 })
            {
                var stepDelta = 1f / rate;
                var densityStep = FluidSimulation.CalculateEffectiveRetention(
                    densityReferenceRetention, stepDelta, true);
                var velocityStep = FluidSimulation.CalculateEffectiveRetention(
                    velocityReferenceRetention, stepDelta, true);
                Assert(Mathf.Abs(Mathf.Pow(densityStep, rate) - expectedDensityAfterOneSecond) < 0.0001f,
                    $"Density decay changed at {rate} Hz.");
                Assert(Mathf.Abs(Mathf.Pow(velocityStep, rate) - expectedVelocityAfterOneSecond) < 0.0001f,
                    $"Velocity decay changed at {rate} Hz.");

                var integratedUnitForce = rate * stepDelta;
                Assert(Mathf.Abs(integratedUnitForce - 1f) < 0.0001f,
                    $"Per-second source or force integration changed at {rate} Hz.");
            }

            Assert(Mathf.Approximately(
                    FluidSimulation.CalculateEffectiveRetention(densityReferenceRetention, 0.02f, false),
                    densityReferenceRetention),
                "Legacy scheduling no longer uses the serialized reference-step density retention.");
            Assert(Mathf.Approximately(
                    FluidSimulation.CalculateEffectiveRetention(velocityReferenceRetention, 0.02f, false),
                    velocityReferenceRetention),
                "Legacy scheduling no longer uses the serialized reference-step velocity retention.");
        }

        private static void TestVorticityCadence()
        {
            var runs = 0;
            for (var completedStep = 0; completedStep < 8; completedStep++)
                if (FluidSimulation.ShouldRunVorticity(completedStep, 2))
                    runs++;
            Assert(runs == 4, "A cadence of two did not skip every alternate vorticity dispatch.");
            Assert(FluidSimulation.ShouldRunVorticity(0, 8) &&
                   !FluidSimulation.ShouldRunVorticity(1, 8) &&
                   FluidSimulation.ShouldRunVorticity(8, 8),
                "Vorticity cadence did not use completed solver-step order deterministically.");
        }

        private static void TestSanitization()
        {
            var profile = ScriptableObject.CreateInstance<InteractiveFogQualityProfile>();
            profile.render.maximumStepCount = -5; profile.render.minimumStepCount = 200; profile.fluid.updatesPerSecond = 0; profile.fluid.maximumCatchUpSteps = 99; profile.Sanitize();
            Assert(profile.render.maximumStepCount == 16, "Maximum steps were not sanitized.");
            Assert(profile.render.minimumStepCount == 16, "Minimum steps were not bounded by maximum steps.");
            Assert(profile.fluid.updatesPerSecond == 1 && profile.fluid.maximumCatchUpSteps == 4, "Fluid schedule was not sanitized.");
            UnityEngine.Object.DestroyImmediate(profile);
        }

        private static void TestHighCompatibilityPath()
        {
            var registryCount = LocalVolumetricFog.ActiveVolumes.Count;
            var root = new GameObject("Interactive Fog High Compatibility Test");
            var fogObject = new GameObject("Fog"); fogObject.transform.SetParent(root.transform); fogObject.AddComponent<MeshFilter>(); var renderer = fogObject.AddComponent<MeshRenderer>(); fogObject.AddComponent<BoxCollider>(); var fog = fogObject.AddComponent<LocalVolumetricFog>();
            var controller = root.AddComponent<InteractiveFogQualityController>(); var profile = ScriptableObject.CreateInstance<InteractiveFogQualityProfile>();
            profile.tier = InteractiveFogQualityTier.High; profile.render.path = InteractiveFogRenderPath.LegacyFullResolution; profile.render.resolutionFraction = 1f; profile.render.maximumStepCount = 60; profile.render.minimumStepCount = 24;
            controller.followUnityQualityLevel = false; controller.manualQuality = InteractiveFogQualityTier.High; controller.fog = fog; controller.highProfile = profile; controller.ApplyQualityNow();
            Assert(renderer.enabled && !fog.ReconstructedRendering, "High unexpectedly suppressed the legacy renderer.");
            Assert(fog.stepCount == 60 && fog.minimumStepCount == 24, "High did not preserve reference step settings.");
            Assert(LocalVolumetricFog.ActiveVolumes.Count == registryCount + 1, "Enabled fog was not registered exactly once.");
            fog.SetReconstructedRendering(true); Assert(!renderer.enabled && fog.ReconstructedRendering, "Reconstructed path did not suppress the legacy renderer.");
            fog.SetReconstructedRendering(false); Assert(renderer.enabled && !fog.ReconstructedRendering, "Legacy renderer was not restored immediately.");
            fog.SetReconstructedRendering(true);
            var serializedFog = new SerializedObject(fog);
            serializedFog.FindProperty("rendererSuppressed").boolValue = false;
            serializedFog.ApplyModifiedPropertiesWithoutUndo();
            fog.SetReconstructedRendering(false);
            Assert(renderer.enabled && !fog.ReconstructedRendering,
                "A script/domain reload stranded the High renderer in its suppressed state.");
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(profile);
            Assert(LocalVolumetricFog.ActiveVolumes.Count == registryCount, "Destroyed fog left a stale registry entry.");
        }

        private static void TestSharedRenderPropertyBlock()
        {
            var shader = Shader.Find("InteractiveFog/LocalVolumetricFog");
            Assert(shader != null, "Interactive fog shader is missing.");
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = "Interactive Fog Shared Property Test";
            var renderer = root.GetComponent<MeshRenderer>();
            var material = new Material(shader);
            renderer.sharedMaterial = material;
            var fog = root.AddComponent<LocalVolumetricFog>();
            fog.volumeMaterial = material;
            fog.baseDensity = 2.37f;
            fog.stepCount = 47;
            fog.minimumStepCount = 19;
            fog.noiseScale = 0.31f;
            fog.noiseStrength = 0.64f;
            fog.heightFalloff = 2.8f;
            fog.thinFogColor = new Color(0.1f, 0.2f, 0.3f, 1f);
            fog.denseFogColor = new Color(0.7f, 0.4f, 0.2f, 1f);
            fog.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);

            var legacy = new MaterialPropertyBlock();
            var explicitPass = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(legacy);
            fog.FillRenderPropertyBlock(explicitPass);
            var scalarNames = new[]
            {
                "_BaseDensity", "_StepCount", "_AdaptiveSteps", "_MinimumStepCount",
                "_MaximumStepLength", "_NoiseScale", "_NoiseStrength", "_HeightFalloff",
                "_DensityColorStrength", "_DensityColorContrast", "_LightAbsorption",
                "_InteractionEdgeFade", "_InteractionFeather", "_DensityScale",
                "_InteractionResponse", "_VelocityScale", "_LerpRange"
            };
            foreach (var propertyName in scalarNames)
            {
                var id = Shader.PropertyToID(propertyName);
                Assert(Mathf.Approximately(legacy.GetFloat(id), explicitPass.GetFloat(id)),
                    $"Legacy and explicit fog paths disagree on {propertyName}.");
            }
            Assert(Approximately(legacy.GetVector(Shader.PropertyToID("_ThinFogColor")),
                    explicitPass.GetVector(Shader.PropertyToID("_ThinFogColor"))) &&
                   Approximately(legacy.GetVector(Shader.PropertyToID("_DenseFogColor")),
                    explicitPass.GetVector(Shader.PropertyToID("_DenseFogColor"))) &&
                   Approximately(legacy.GetVector(Shader.PropertyToID("_NoiseVelocity")),
                    explicitPass.GetVector(Shader.PropertyToID("_NoiseVelocity"))),
                "Legacy and explicit fog paths disagree on a color or vector property.");
            var legacyMatrix = legacy.GetMatrix(Shader.PropertyToID("_VolumeWorldToLocal"));
            var explicitMatrix = explicitPass.GetMatrix(Shader.PropertyToID("_VolumeWorldToLocal"));
            for (var index = 0; index < 16; index++)
                Assert(Mathf.Approximately(legacyMatrix[index], explicitMatrix[index]),
                    "Legacy and explicit fog paths disagree on the volume matrix.");

            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(material);
        }

        private static void TestVolumeRegistryLifecycle()
        {
            var baseline = LocalVolumetricFog.ActiveVolumes.Count;
            var first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            first.name = "Interactive Fog Registry Test";
            first.AddComponent<LocalVolumetricFog>();
            Assert(LocalVolumetricFog.ActiveVolumes.Count == baseline + 1,
                "Enabled volume was not registered exactly once.");
            var duplicate = UnityEngine.Object.Instantiate(first);
            Assert(LocalVolumetricFog.ActiveVolumes.Count == baseline + 2,
                "Duplicated volume was not registered independently.");
            duplicate.SetActive(false);
            Assert(LocalVolumetricFog.ActiveVolumes.Count == baseline + 1,
                "Disabled duplicate left a stale registry entry.");
            duplicate.SetActive(true);
            Assert(LocalVolumetricFog.ActiveVolumes.Count == baseline + 2,
                "Re-enabled duplicate was not restored to the registry.");
            UnityEngine.Object.DestroyImmediate(first);
            UnityEngine.Object.DestroyImmediate(duplicate);
            Assert(LocalVolumetricFog.ActiveVolumes.Count == baseline,
                "Destroyed volumes left stale registry entries.");
        }

        private static void TestReconstructedShaderContract()
        {
            var fogShader = Shader.Find("InteractiveFog/LocalVolumetricFog");
            var reconstructionShader = Shader.Find("Hidden/InteractiveFog/Reconstruction");
            Assert(fogShader != null && reconstructionShader != null,
                "A required reconstructed fog shader is missing.");
            var fogMaterial = new Material(fogShader);
            var reconstructionMaterial = new Material(reconstructionShader);
            Assert(fogMaterial.FindPass("Interactive Volumetric Fog") == 0,
                "The High compatibility pass moved or was renamed.");
            Assert(fogMaterial.FindPass("Interactive Volumetric Fog Reconstructed") == 1,
                "The dedicated scattering/transmittance MRT pass is missing.");
            Assert(fogMaterial.FindPass("Interactive Volumetric Fog Reconstructed Baked Detail") == 2 &&
                   fogMaterial.FindPass("Interactive Volumetric Fog Reconstructed Baked Low") == 3,
                "The dedicated Medium/Low baked-noise passes are missing or reordered.");
            Assert(reconstructionMaterial.FindPass("Bilateral Composite") == 0 &&
                   reconstructionMaterial.FindPass("Depth Downsample") == 1,
                "The reconstruction pass contract is incomplete.");
            UnityEngine.Object.DestroyImmediate(fogMaterial);
            UnityEngine.Object.DestroyImmediate(reconstructionMaterial);
        }

        private static void TestRejectedProfilePreservesActiveState()
        {
            var root = new GameObject("Interactive Fog Rejection Test"); var controller = root.AddComponent<InteractiveFogQualityController>(); controller.followUnityQualityLevel = false; controller.manualQuality = InteractiveFogQualityTier.High;
            var valid = ScriptableObject.CreateInstance<InteractiveFogQualityProfile>(); valid.tier = InteractiveFogQualityTier.High; valid.render.path = InteractiveFogRenderPath.LegacyFullResolution; valid.render.resolutionFraction = 1f; controller.highProfile = valid; controller.ApplyQualityNow(); var previous = controller.ActiveProfile;
            var invalid = ScriptableObject.CreateInstance<InteractiveFogQualityProfile>(); invalid.tier = InteractiveFogQualityTier.High; invalid.render.path = InteractiveFogRenderPath.LegacyFullResolution; invalid.render.resolutionFraction = 0.5f; controller.highProfile = invalid; controller.ApplyQualityNow();
            Assert(controller.ActiveProfile == previous && controller.UsingFallback && controller.TransitionMessage.StartsWith("Profile rejected", StringComparison.Ordinal), "Rejected profile replaced active state.");
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(valid); UnityEngine.Object.DestroyImmediate(invalid);
        }

        private static void TestDeterministicCapabilityFallback()
        {
            var root = new GameObject("Interactive Fog Capability Fallback Test");
            var controller = root.AddComponent<InteractiveFogQualityController>();
            var high = ScriptableObject.CreateInstance<InteractiveFogQualityProfile>();
            high.tier = InteractiveFogQualityTier.High;
            high.render.path = InteractiveFogRenderPath.LegacyFullResolution;
            high.render.resolutionFraction = 1f;
            var medium = ScriptableObject.CreateInstance<InteractiveFogQualityProfile>();
            medium.tier = InteractiveFogQualityTier.Medium;
            medium.render.path = InteractiveFogRenderPath.Reconstructed;
            medium.render.resolutionFraction = 0.5f;
            medium.compatibility.fallbackOrder = new[] { InteractiveFogQualityTier.High };
            controller.followUnityQualityLevel = false;
            controller.manualQuality = InteractiveFogQualityTier.Medium;
            controller.highProfile = high;
            controller.mediumProfile = medium;
            try
            {
                InteractiveFogQualityController.CapabilityEvaluatorOverrideForTests = profile =>
                    profile.tier == InteractiveFogQualityTier.Medium ? "Forced unsupported capability." : string.Empty;
                controller.ApplyQualityNow();
                Assert(controller.RequestedTier == InteractiveFogQualityTier.Medium &&
                       controller.ActiveTier == InteractiveFogQualityTier.High &&
                       controller.ActiveProfile == high && controller.UsingFallback &&
                       controller.TransitionMessage.Contains("Forced unsupported capability.") &&
                       controller.TransitionMessage.Contains("Medium to High"),
                    "Capability fallback did not select and report the first supported profile deterministically.");
            }
            finally
            {
                InteractiveFogQualityController.CapabilityEvaluatorOverrideForTests = null;
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(high);
                UnityEngine.Object.DestroyImmediate(medium);
            }
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static bool Approximately(Vector4 left, Vector4 right) =>
            Mathf.Approximately(left.x, right.x) && Mathf.Approximately(left.y, right.y) &&
            Mathf.Approximately(left.z, right.z) && Mathf.Approximately(left.w, right.w);
    }
}
