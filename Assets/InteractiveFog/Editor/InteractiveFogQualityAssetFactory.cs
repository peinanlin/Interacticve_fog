using System;
using System.Security.Cryptography;
using InteractiveFog;
using UnityEditor;
using UnityEngine;

namespace InteractiveFogEditor
{
    public static class InteractiveFogQualityAssetFactory
    {
        public const string ProfileFolder = "Assets/InteractiveFog/QualityProfiles";
        public const string HighPath = ProfileFolder + "/InteractiveFogHigh.asset";
        public const string MediumPath = ProfileFolder + "/InteractiveFogMedium.asset";
        public const string LowPath = ProfileFolder + "/InteractiveFogLow.asset";
        public const string BaseNoisePath = ProfileFolder + "/InteractiveFogBaseNoise.asset";
        public const string DetailNoisePath = ProfileFolder + "/InteractiveFogDetailNoise.asset";

        [MenuItem("Tools/Interactive Fog/Create or Refresh Quality Assets")]
        public static void CreateOrRefreshQualityAssets()
        {
            EnsureFolder();
            var baseNoise = EnsureNoise(BaseNoisePath, 64, 0x1f2e3d4c, 3);
            var detailNoise = EnsureNoise(DetailNoisePath, 32, 0x5a6b7c8d, 5);
            ConfigureHigh(EnsureProfile(HighPath));
            ConfigureMedium(EnsureProfile(MediumPath), baseNoise, detailNoise);
            ConfigureLow(EnsureProfile(LowPath), baseNoise);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ValidateQualityAssets(logSuccess: true);
            InteractiveFogQualitySelfTests.Run();
        }

        [MenuItem("Tools/Interactive Fog/Validate Quality Assets")]
        public static void ValidateQualityAssetsMenu()
        {
            ValidateQualityAssets(logSuccess: true);
        }

        public static bool ValidateQualityAssets(bool logSuccess)
        {
            var valid = true;
            valid &= ValidateProfile(HighPath, InteractiveFogQualityTier.High);
            valid &= ValidateProfile(MediumPath, InteractiveFogQualityTier.Medium);
            valid &= ValidateProfile(LowPath, InteractiveFogQualityTier.Low);

            var baseNoise = AssetDatabase.LoadAssetAtPath<Texture3D>(BaseNoisePath);
            var detailNoise = AssetDatabase.LoadAssetAtPath<Texture3D>(DetailNoisePath);
            if (baseNoise == null || detailNoise == null)
            {
                Debug.LogError("Interactive Fog quality assets are missing generated Texture3D noise.");
                valid = false;
            }

            if (valid && logSuccess)
            {
                Debug.Log($"Interactive Fog quality assets valid. BaseNoise SHA256={ComputeTextureHash(baseNoise)}, DetailNoise SHA256={ComputeTextureHash(detailNoise)}");
            }
            return valid;
        }

        private static bool ValidateProfile(string path, InteractiveFogQualityTier expectedTier)
        {
            var profile = AssetDatabase.LoadAssetAtPath<InteractiveFogQualityProfile>(path);
            if (profile == null)
            {
                Debug.LogError($"Missing Interactive Fog profile: {path}");
                return false;
            }
            var reason = string.Empty;
            if (profile.tier != expectedTier || !profile.TryValidate(out reason))
            {
                Debug.LogError($"Invalid Interactive Fog profile {path}: expected {expectedTier}. {reason}");
                return false;
            }
            return true;
        }

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProfileFolder))
                AssetDatabase.CreateFolder("Assets/InteractiveFog", "QualityProfiles");
        }

        private static InteractiveFogQualityProfile EnsureProfile(string path)
        {
            var profile = AssetDatabase.LoadAssetAtPath<InteractiveFogQualityProfile>(path);
            if (profile != null)
                return profile;
            profile = ScriptableObject.CreateInstance<InteractiveFogQualityProfile>();
            AssetDatabase.CreateAsset(profile, path);
            return profile;
        }

        private static Texture3D EnsureNoise(string path, int size, int seed, int octaves)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
            if (texture != null && texture.width == size)
                return texture;

            if (texture != null)
                AssetDatabase.DeleteAsset(path);
            var data = GenerateTileableNoise(size, seed, octaves);
            texture = new Texture3D(size, size, size, TextureFormat.R8, false)
            {
                name = System.IO.Path.GetFileNameWithoutExtension(path),
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };
            texture.SetPixelData(data, 0);
            texture.Apply(false, false);
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        private static byte[] GenerateTileableNoise(int size, int seed, int octaves)
        {
            var data = new byte[size * size * size];
            var random = new System.Random(seed);
            var phase = new Vector3[octaves];
            for (var octave = 0; octave < octaves; octave++)
                phase[octave] = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * Mathf.PI * 2f;

            var index = 0;
            for (var z = 0; z < size; z++)
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var value = 0f;
                var weight = 0f;
                for (var octave = 0; octave < octaves; octave++)
                {
                    var frequency = 1 << octave;
                    var amplitude = 1f / frequency;
                    var ax = Mathf.PI * 2f * frequency * x / size + phase[octave].x;
                    var ay = Mathf.PI * 2f * frequency * y / size + phase[octave].y;
                    var az = Mathf.PI * 2f * frequency * z / size + phase[octave].z;
                    value += (Mathf.Sin(ax) * Mathf.Sin(ay) * Mathf.Sin(az) * 0.5f + 0.5f) * amplitude;
                    weight += amplitude;
                }
                value = Mathf.SmoothStep(0f, 1f, value / Mathf.Max(weight, 0.0001f));
                data[index++] = (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
            }
            return data;
        }

        private static string ComputeTextureHash(Texture3D texture)
        {
            if (texture == null)
                return "missing";
            var bytes = texture.GetPixelData<byte>(0).ToArray();
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void ConfigureHigh(InteractiveFogQualityProfile profile)
        {
            profile.tier = InteractiveFogQualityTier.High;
            profile.render.path = InteractiveFogRenderPath.LegacyFullResolution;
            profile.render.resolutionFraction = 1f; profile.render.maximumStepCount = 60; profile.render.minimumStepCount = 24; profile.render.jitterStrength = 0f;
            profile.temporal.enabled = false;
            profile.shading.noiseMode = InteractiveFogNoiseMode.ProceduralFbm; profile.shading.detailOctaves = 4; profile.shading.lightingMode = InteractiveFogLightingMode.Full;
            profile.fluid.updatesPerSecond = 60; profile.fluid.maximumCatchUpSteps = 2; profile.fluid.resolutionScale = 6; profile.fluid.pressureIterations = 4; profile.fluid.vorticityCadence = 1;
            profile.compatibility.fallbackOrder = Array.Empty<InteractiveFogQualityTier>();
            profile.budget.targetMedianGpuMilliseconds = 3.53f; profile.budget.targetP95GpuMilliseconds = 4.5f;
            profile.Sanitize(); EditorUtility.SetDirty(profile);
        }

        private static void ConfigureMedium(InteractiveFogQualityProfile profile, Texture3D baseNoise, Texture3D detailNoise)
        {
            profile.tier = InteractiveFogQualityTier.Medium;
            profile.render.path = InteractiveFogRenderPath.Reconstructed;
            profile.render.resolutionFraction = 0.5f; profile.render.maximumStepCount = 44; profile.render.minimumStepCount = 20; profile.render.jitterStrength = 0.5f;
            // Keep the first reconstructed milestone on a deterministic nearest-neighbour
            // composite. Bilateral reconstruction is enabled only after this baseline passes
            // camera-motion and camera-inside-volume validation.
            profile.reconstruction.bilateralRadius = 0; profile.reconstruction.depthThreshold = 0.2f;
            profile.temporal.enabled = true; profile.temporal.historyWeight = 0.78f; profile.temporal.reactiveOpacityThreshold = 0.08f;
            profile.shading.noiseMode = InteractiveFogNoiseMode.Baked3DWithDetail; profile.shading.detailOctaves = 1; profile.shading.lightingMode = InteractiveFogLightingMode.Full; profile.shading.baseNoise = baseNoise; profile.shading.detailNoise = detailNoise;
            profile.fluid.updatesPerSecond = 30; profile.fluid.maximumCatchUpSteps = 2; profile.fluid.resolutionScale = 4; profile.fluid.pressureIterations = 3; profile.fluid.vorticityCadence = 1;
            profile.compatibility.renderSceneView = true;
            profile.compatibility.fallbackOrder = new[] { InteractiveFogQualityTier.High };
            profile.budget.targetMedianGpuMilliseconds = 2f; profile.budget.targetP95GpuMilliseconds = 2.5f;
            profile.Sanitize(); EditorUtility.SetDirty(profile);
        }

        private static void ConfigureLow(InteractiveFogQualityProfile profile, Texture3D baseNoise)
        {
            profile.tier = InteractiveFogQualityTier.Low;
            profile.render.path = InteractiveFogRenderPath.Reconstructed;
            profile.render.resolutionFraction = 0.25f; profile.render.maximumStepCount = 32; profile.render.minimumStepCount = 16; profile.render.jitterStrength = 0.75f;
            profile.reconstruction.bilateralRadius = 0; profile.reconstruction.depthThreshold = 0.3f;
            profile.temporal.enabled = true; profile.temporal.historyWeight = 0.88f; profile.temporal.reactiveOpacityThreshold = 0.06f;
            profile.shading.noiseMode = InteractiveFogNoiseMode.Baked3D; profile.shading.detailOctaves = 0; profile.shading.lightingMode = InteractiveFogLightingMode.Simplified; profile.shading.baseNoise = baseNoise; profile.shading.detailNoise = null;
            profile.fluid.updatesPerSecond = 20; profile.fluid.maximumCatchUpSteps = 1; profile.fluid.resolutionScale = 3; profile.fluid.pressureIterations = 2; profile.fluid.vorticityCadence = 2;
            profile.compatibility.renderSceneView = true;
            profile.compatibility.fallbackOrder = new[] { InteractiveFogQualityTier.Medium, InteractiveFogQualityTier.High };
            profile.budget.targetMedianGpuMilliseconds = 1.2f; profile.budget.targetP95GpuMilliseconds = 1.6f;
            profile.Sanitize(); EditorUtility.SetDirty(profile);
        }
    }
}
