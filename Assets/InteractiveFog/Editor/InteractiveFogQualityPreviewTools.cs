using System.IO;
using InteractiveFog;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace InteractiveFogEditor
{
    public static class InteractiveFogQualityPreviewTools
    {
        [MenuItem("Tools/Interactive Fog/Open PolygonTown Demo")]
        public static void OpenPolygonTownDemo()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (active.isDirty)
                throw new System.InvalidOperationException("Current scene has unsaved changes; save or discard them before opening PolygonTown Demo.");
            EditorSceneManager.OpenScene("Assets/PolygonTown/Scenes/Demo.unity");
        }

        [MenuItem("Tools/Interactive Fog/Preview High Quality")]
        public static void PreviewHigh() => SetTier(2);

        [MenuItem("Tools/Interactive Fog/Preview Medium Quality")]
        public static void PreviewMedium() => SetTier(1);

        [MenuItem("Tools/Interactive Fog/Preview Low Quality")]
        public static void PreviewLow() => SetTier(0);

        [MenuItem("Tools/Interactive Fog/Capture Game View")]
        public static void CaptureGameView()
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../.codex_work"));
            Directory.CreateDirectory(directory);
            var controller = Object.FindObjectOfType<InteractiveFogQualityController>();
            var tier = controller != null ? controller.ActiveTier.ToString() : "Unknown";
            var path = Path.Combine(directory, $"interactive-fog-{tier.ToLowerInvariant()}.png");
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"Interactive Fog Game capture requested: {path}");
            Debug.Log($"Interactive Fog render diagnostics: {InteractiveFogRenderDiagnostics.Snapshot}");
            CaptureRenderTarget(InteractiveFogRenderDiagnostics.FogColorTarget,
                Path.Combine(directory, $"interactive-fog-{tier.ToLowerInvariant()}-low-color.png"));
        }

        private static void SetTier(int qualityLevel)
        {
            QualitySettings.SetQualityLevel(qualityLevel, true);
            var controllers = Object.FindObjectsOfType<InteractiveFogQualityController>(true);
            foreach (var controller in controllers)
                controller.ApplyQualityNow();
            Debug.Log($"Interactive Fog preview quality: {QualitySettings.names[qualityLevel]}");
        }

        private static void CaptureRenderTarget(RenderTexture source, string path)
        {
            if (source == null)
            {
                Debug.LogWarning("Interactive Fog low-color capture skipped: no game-camera fog target is available.");
                return;
            }
            if (!source.IsCreated())
            {
                Debug.LogWarning("Interactive Fog low-color capture skipped: the game-camera fog target is not created.");
                return;
            }

            var copy = RenderTexture.GetTemporary(source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, copy);
                RenderTexture.active = copy;
                readable = new Texture2D(copy.width, copy.height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, copy.width, copy.height), 0, 0, false);
                readable.Apply(false, false);
                File.WriteAllBytes(path, readable.EncodeToPNG());
                Debug.Log($"Interactive Fog low-color capture: {path}");
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null)
                    Object.DestroyImmediate(readable);
                RenderTexture.ReleaseTemporary(copy);
            }
        }
    }
}
