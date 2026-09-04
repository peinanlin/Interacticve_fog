using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace InteractiveFog
{
    public sealed class InteractiveFogRendererFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class FeatureSettings
        {
            public Shader compositeShader;
            public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        [SerializeField] private FeatureSettings settings = new FeatureSettings();
        private Material compositeMaterial;
        private InteractiveFogPass pass;

        public override void Create()
        {
            if (settings.compositeShader == null)
                settings.compositeShader = Shader.Find("Hidden/InteractiveFog/Reconstruction");
            CoreUtils.Destroy(compositeMaterial);
            if (settings.compositeShader != null)
                compositeMaterial = CoreUtils.CreateEngineMaterial(settings.compositeShader);
            pass = new InteractiveFogPass(compositeMaterial) { renderPassEvent = settings.passEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            InteractiveFogRenderDiagnostics.BeginCamera(renderingData.cameraData.camera);
            var controller = InteractiveFogQualityController.Active;
            var profile = controller != null ? controller.ActiveProfile : null;
            if (pass == null || compositeMaterial == null)
            {
                InteractiveFogRenderDiagnostics.SkipReason = "Feature pass or composite material is missing";
                pass?.Prepare(null, false);
                return;
            }
            if (controller == null || profile == null)
            {
                InteractiveFogRenderDiagnostics.SkipReason = "No active quality controller/profile";
                pass.Prepare(null, false);
                return;
            }
            if (!controller.ReconstructedRenderingActive)
            {
                InteractiveFogRenderDiagnostics.SkipReason = "Active profile uses the High/legacy path";
                pass.Prepare(null, false);
                return;
            }
            if (!CameraAllowed(profile, ref renderingData.cameraData))
            {
                InteractiveFogRenderDiagnostics.SkipReason = "Camera type is disabled by the active profile";
                pass?.Prepare(null, false);
                return;
            }
            if (controller.development.temporalResolve && profile.temporal.enabled)
            {
                InteractiveFogRenderDiagnostics.TemporalState = "Requested; current-frame fallback";
                InteractiveFogRenderDiagnostics.HistoryResetReason = "Temporal resolve is not enabled in this milestone";
            }
            pass.Prepare(profile, controller.development.bakedNoise);
            InteractiveFogRenderDiagnostics.EnqueuedPasses = 1;
            InteractiveFogRenderDiagnostics.SkipReason = "None";
            renderer.EnqueuePass(pass);
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            // URP 14 creates the camera targets after AddRenderPasses. Reading the target there logs an
            // error every frame and leaves the reconstructed pass without a valid destination.
            pass?.SetCameraTarget(renderer.cameraColorTargetHandle);
            InteractiveFogRenderDiagnostics.SetupTargetBindings++;
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Dispose();
            pass = null;
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = null;
        }

        private static bool CameraAllowed(InteractiveFogQualityProfile profile, ref CameraData cameraData)
        {
            var camera = cameraData.camera;
            if (camera == null)
                return false;
            if (cameraData.isSceneViewCamera)
                return profile.compatibility.renderSceneView;
            return camera.cameraType switch
            {
                CameraType.Game => profile.compatibility.renderGameCameras,
                CameraType.Preview => profile.compatibility.renderPreviewCameras,
                CameraType.Reflection => profile.compatibility.renderReflectionCameras,
                _ => false
            };
        }

        private sealed class InteractiveFogPass : ScriptableRenderPass
        {
            private sealed class CameraResources
            {
                public RTHandle fogColor;
                public RTHandle fogDepth;
                public RTHandle sceneDepth;
                public Vector2Int targetSize;
                public int lastUsedFrame;

                public void Release()
                {
                    fogColor?.Release(); fogColor = null;
                    fogDepth?.Release(); fogDepth = null;
                    sceneDepth?.Release(); sceneDepth = null;
                }
            }

            private const int InactiveCameraFrameLifetime = 120;
            private static readonly ProfilingSampler RayMarchSampler = new ProfilingSampler("Interactive Fog Ray March");
            private static readonly ProfilingSampler DepthSampler = new ProfilingSampler("Interactive Fog Depth Downsample");
            private static readonly ProfilingSampler CompositeSampler = new ProfilingSampler("Interactive Fog Bilateral Composite");
            private static readonly int LowDepthId = Shader.PropertyToID("_InteractiveFogLowDepth");
            private static readonly int LowSceneDepthId = Shader.PropertyToID("_InteractiveFogLowSceneDepth");
            private static readonly int DepthThresholdId = Shader.PropertyToID("_InteractiveFogDepthThreshold");
            private static readonly int BilateralRadiusId = Shader.PropertyToID("_InteractiveFogBilateralRadius");
            private static readonly int BakedNoiseId = Shader.PropertyToID("_BakedNoise");
            private static readonly int BakedDetailNoiseId = Shader.PropertyToID("_BakedDetailNoise");
            private static readonly int InteractiveId = Shader.PropertyToID("_Interactive");
            private static readonly int Density2DId = Shader.PropertyToID("_Density2D");
            private static readonly int Velocity2DId = Shader.PropertyToID("_Velocity2D");
            private readonly MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
            private readonly Plane[] frustumPlanes = new Plane[6];
            private readonly RenderTargetIdentifier[] fogMrt = new RenderTargetIdentifier[2];
            private readonly Dictionary<int, CameraResources> cameraResources =
                new Dictionary<int, CameraResources>();
            private readonly List<int> staleCameraIds = new List<int>();
            private readonly Material compositeMaterial;
            private RTHandle cameraColor;
            private InteractiveFogQualityProfile profile;
            private CameraResources activeResources;
            private bool bakedNoiseEnabled;

            public InteractiveFogPass(Material material)
            {
                compositeMaterial = material;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Prepare(InteractiveFogQualityProfile qualityProfile, bool enableBakedNoise)
            {
                cameraColor = null;
                profile = qualityProfile;
                bakedNoiseEnabled = enableBakedNoise;
            }

            public void SetCameraTarget(RTHandle colorTarget)
            {
                cameraColor = colorTarget;
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                if (profile == null)
                    return;
                var camera = renderingData.cameraData.camera;
                if (camera == null)
                    return;
                ReleaseInactiveCameraResources(Time.frameCount);
                var cameraId = camera.GetInstanceID();
                if (!cameraResources.TryGetValue(cameraId, out activeResources))
                {
                    activeResources = new CameraResources();
                    cameraResources.Add(cameraId, activeResources);
                }
                activeResources.lastUsedFrame = Time.frameCount;
                var descriptor = renderingData.cameraData.cameraTargetDescriptor;
                var fraction = Mathf.Clamp(profile.render.resolutionFraction, 0.25f, 0.5f);
                descriptor.width = Mathf.Max(1, Mathf.CeilToInt(descriptor.width * fraction));
                descriptor.height = Mathf.Max(1, Mathf.CeilToInt(descriptor.height * fraction));
                descriptor.depthBufferBits = 0; descriptor.msaaSamples = 1; descriptor.bindMS = false;
                descriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref activeResources.fogColor, descriptor,
                    FilterMode.Bilinear, TextureWrapMode.Clamp,
                    name: $"_InteractiveFogLowColor_{cameraId}");
                descriptor.graphicsFormat = GraphicsFormat.R16_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref activeResources.fogDepth, descriptor,
                    FilterMode.Point, TextureWrapMode.Clamp,
                    name: $"_InteractiveFogLowDepth_{cameraId}");
                RenderingUtils.ReAllocateIfNeeded(ref activeResources.sceneDepth, descriptor,
                    FilterMode.Point, TextureWrapMode.Clamp,
                    name: $"_InteractiveFogLowSceneDepth_{cameraId}");
                activeResources.targetSize = new Vector2Int(descriptor.width, descriptor.height);
                InteractiveFogRenderDiagnostics.TargetSize = activeResources.targetSize;
                InteractiveFogRenderDiagnostics.CameraName = camera.name;
                InteractiveFogRenderDiagnostics.FogColorTarget = activeResources.fogColor.rt;
                InteractiveFogRenderDiagnostics.FogDepthTarget = activeResources.fogDepth.rt;
                InteractiveFogRenderDiagnostics.SceneDepthTarget = activeResources.sceneDepth.rt;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (profile == null || activeResources == null ||
                    activeResources.fogColor == null || activeResources.fogDepth == null ||
                    activeResources.sceneDepth == null || cameraColor == null)
                    return;
                var fogColor = activeResources.fogColor;
                var fogDepth = activeResources.fogDepth;
                var sceneDepth = activeResources.sceneDepth;
                var cmd = CommandBufferPool.Get("Interactive Fog Reconstructed Rendering");
                try
                {
                    using (new ProfilingScope(cmd, DepthSampler))
                    {
                        CoreUtils.SetRenderTarget(cmd, sceneDepth, ClearFlag.Color, Color.black);
                        CoreUtils.DrawFullScreen(cmd, compositeMaterial, shaderPassId: 1);
                    }

                    using (new ProfilingScope(cmd, RayMarchSampler))
                    {
                        InteractiveFogRenderDiagnostics.RayMarchPasses++;
                        // RT0 stores uncomposited premultiplied scattering and remaining
                        // transmittance. RT1 stores representative linear fog depth.
                        fogMrt[0] = fogColor.nameID;
                        fogMrt[1] = fogDepth.nameID;
                        cmd.SetRenderTarget(fogMrt, new RenderTargetIdentifier(BuiltinRenderTextureType.None));
                        cmd.SetViewport(new Rect(0f, 0f,
                            activeResources.targetSize.x, activeResources.targetSize.y));
                        // Clear after the MRT is bound. Clearing each attachment first and then
                        // rebinding the MRT can discard those clears on some render-pass paths.
                        // RGB=0/A=1 is both the identity fog value (zero scattering, full
                        // transmittance) and a valid zero representative depth in RT1's R channel.
                        cmd.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 1f));
                        var camera = renderingData.cameraData.camera;
                        GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);
                        var volumes = LocalVolumetricFog.ActiveVolumes;
                        InteractiveFogRenderDiagnostics.RegisteredVolumes = volumes.Count;
                        for (var index = 0; index < volumes.Count; index++)
                        {
                            var volume = volumes[index];
                            if (volume == null || !volume.isActiveAndEnabled || !volume.ReconstructedRendering ||
                                volume.RenderMesh == null || volume.RenderMaterial == null)
                                continue;
                            InteractiveFogRenderDiagnostics.EligibleVolumes++;
                            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, volume.WorldBounds))
                                continue;
                            volume.FillRenderPropertyBlock(propertyBlock);
                            InteractiveFogRenderDiagnostics.InteractionEnabled =
                                propertyBlock.GetFloat(InteractiveId);
                            InteractiveFogRenderDiagnostics.DensityBinding =
                                DescribeTexture(propertyBlock.GetTexture(Density2DId));
                            InteractiveFogRenderDiagnostics.VelocityBinding =
                                DescribeTexture(propertyBlock.GetTexture(Velocity2DId));
                            var fogPass = 1;
                            if (bakedNoiseEnabled && profile.shading.noiseMode != InteractiveFogNoiseMode.ProceduralFbm)
                            {
                                propertyBlock.SetTexture(BakedNoiseId, profile.shading.baseNoise);
                                propertyBlock.SetTexture(BakedDetailNoiseId,
                                    profile.shading.detailNoise != null
                                        ? profile.shading.detailNoise
                                        : profile.shading.baseNoise);
                                fogPass = profile.shading.noiseMode == InteractiveFogNoiseMode.Baked3DWithDetail
                                    ? 2
                                    : 3;
                            }
                            cmd.DrawMesh(volume.RenderMesh, volume.RenderMatrix, volume.RenderMaterial,
                                0, fogPass, propertyBlock);
                            InteractiveFogRenderDiagnostics.DrawnVolumes++;
                        }
                    }

                    using (new ProfilingScope(cmd, CompositeSampler))
                    {
                        InteractiveFogRenderDiagnostics.CompositePasses++;
                        cmd.SetGlobalTexture(LowDepthId, fogDepth.nameID);
                        cmd.SetGlobalTexture(LowSceneDepthId, sceneDepth.nameID);
                        compositeMaterial.SetFloat(DepthThresholdId, profile.reconstruction.depthThreshold);
                        compositeMaterial.SetFloat(BilateralRadiusId, profile.reconstruction.bilateralRadius);
                        // The composite shader alpha-blends fog over the existing camera color,
                        // so the destination must be loaded instead of treated as disposable.
                        Blitter.BlitCameraTexture(cmd, fogColor, cameraColor,
                            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                            compositeMaterial, 0);
                    }
                    context.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    cmd.Clear(); CommandBufferPool.Release(cmd);
                }
            }

            private static string DescribeTexture(Texture texture)
            {
                if (texture == null)
                    return "None";
                return $"{texture.name}[{texture.width}x{texture.height}]";
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                cameraColor = null; profile = null; activeResources = null; bakedNoiseEnabled = false;
            }

            public void Dispose()
            {
                foreach (var pair in cameraResources)
                    pair.Value.Release();
                cameraResources.Clear();
                staleCameraIds.Clear();
                activeResources = null;
            }

            private void ReleaseInactiveCameraResources(int currentFrame)
            {
                staleCameraIds.Clear();
                foreach (var pair in cameraResources)
                {
                    if (currentFrame - pair.Value.lastUsedFrame > InactiveCameraFrameLifetime)
                        staleCameraIds.Add(pair.Key);
                }
                for (var index = 0; index < staleCameraIds.Count; index++)
                {
                    var cameraId = staleCameraIds[index];
                    cameraResources[cameraId].Release();
                    cameraResources.Remove(cameraId);
                }
            }
        }
    }
}
