using System;
using System.Linq;
using InteractiveFog;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace InteractiveFogEditor
{
    public static class InteractiveFogRendererFeatureInstaller
    {
        private static readonly string[] RendererPaths =
        {
            "Assets/Settings/URP-Performant-Renderer.asset",
            "Assets/Settings/URP-Balanced-Renderer.asset",
            "Assets/Settings/URP-HighFidelity-Renderer.asset"
        };

        [MenuItem("Tools/Interactive Fog/Install and Validate Renderer Features")]
        public static void InstallAndValidate()
        {
            foreach (var path in RendererPaths)
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null)
                    throw new InvalidOperationException($"UniversalRendererData not found: {path}");
                data.rendererFeatures.RemoveAll(feature => feature == null);
                if (!data.rendererFeatures.OfType<InteractiveFogRendererFeature>().Any())
                {
                    var feature = ScriptableObject.CreateInstance<InteractiveFogRendererFeature>();
                    feature.name = "Interactive Fog Renderer Feature";
                    AssetDatabase.AddObjectToAsset(feature, data);
                    data.rendererFeatures.Add(feature);
                    EditorUtility.SetDirty(feature);
                    EditorUtility.SetDirty(data);
                }
            }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            if (!Validate(logSuccess: true))
                throw new InvalidOperationException("Interactive Fog renderer feature validation failed.");
        }

        [MenuItem("Tools/Interactive Fog/Validate Renderer Features")]
        public static void ValidateMenu() => Validate(logSuccess: true);

        public static bool Validate(bool logSuccess)
        {
            var valid = true;
            foreach (var path in RendererPaths)
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                var count = data != null
                    ? data.rendererFeatures.OfType<InteractiveFogRendererFeature>().Count()
                    : 0;
                if (count != 1)
                {
                    Debug.LogError($"{path} contains {count} Interactive Fog renderer features; expected exactly one.");
                    valid = false;
                }
            }
            if (valid && logSuccess)
                Debug.Log("Interactive Fog renderer features are installed exactly once in all quality renderers.");
            return valid;
        }
    }
}
