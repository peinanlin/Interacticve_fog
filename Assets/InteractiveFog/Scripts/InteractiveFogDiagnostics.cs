using System.Text;
using UnityEngine;

namespace InteractiveFog
{
    public static class InteractiveFogRenderDiagnostics
    {
        public static Vector2Int TargetSize { get; internal set; }
        public static string CameraName { get; internal set; } = "None";
        public static int RayMarchPasses { get; internal set; }
        public static int CompositePasses { get; internal set; }
        public static int EnqueuedPasses { get; internal set; }
        public static int SetupTargetBindings { get; internal set; }
        public static int RegisteredVolumes { get; internal set; }
        public static int EligibleVolumes { get; internal set; }
        public static int DrawnVolumes { get; internal set; }
        public static float InteractionEnabled { get; internal set; }
        public static string DensityBinding { get; internal set; } = "None";
        public static string VelocityBinding { get; internal set; } = "None";
        public static string SkipReason { get; internal set; } = "Not evaluated";
        public static RenderTexture FogColorTarget { get; internal set; }
        public static RenderTexture FogDepthTarget { get; internal set; }
        public static RenderTexture SceneDepthTarget { get; internal set; }
        public static string TemporalState { get; internal set; } = "Disabled";
        public static string HistoryResetReason { get; internal set; } = "Not implemented";

        public static string Snapshot =>
            $"camera={CameraName}, target={TargetSize.x}x{TargetSize.y}, enqueued={EnqueuedPasses}, " +
            $"targetBindings={SetupTargetBindings}, registered={RegisteredVolumes}, " +
            $"eligible={EligibleVolumes}, drawn={DrawnVolumes}, interactive={InteractionEnabled:F0}, " +
            $"density={DensityBinding}, velocity={VelocityBinding}, rayMarch={RayMarchPasses}, " +
            $"composite={CompositePasses}, skip={SkipReason}";

        internal static void BeginCamera(Camera camera)
        {
            TargetSize = Vector2Int.zero;
            CameraName = camera != null ? camera.name : "None";
            RayMarchPasses = 0;
            CompositePasses = 0;
            EnqueuedPasses = 0;
            SetupTargetBindings = 0;
            RegisteredVolumes = 0;
            EligibleVolumes = 0;
            DrawnVolumes = 0;
            SkipReason = "Not evaluated";
            // Keep the last successfully allocated game-camera targets available to
            // editor diagnostics. Preview/inspector cameras are rendered afterwards
            // and must not erase the references before the capture command runs.
            TemporalState = "Disabled";
            HistoryResetReason = "No history allocated";
        }
    }

    [DisallowMultipleComponent]
    public sealed class InteractiveFogDiagnostics : MonoBehaviour
    {
        public InteractiveFogQualityController qualityController;
        public FluidSimulation simulation;
        public LocalVolumetricFog fog;
        public bool showRuntimeOverlay;
        public Vector2 overlayPosition = new Vector2(12f, 12f);
        [TextArea(8, 16), SerializeField] private string currentState;
        private readonly StringBuilder builder = new StringBuilder(768);

        public string CurrentState => currentState;

        private void OnEnable() { ResolveReferences(); Refresh(); }
        private void Update() { Refresh(); }

        private void ResolveReferences()
        {
            if (qualityController == null) qualityController = GetComponent<InteractiveFogQualityController>();
            if (qualityController == null) qualityController = FindObjectOfType<InteractiveFogQualityController>();
            if (simulation == null && qualityController != null) simulation = qualityController.simulation;
            if (fog == null && qualityController != null) fog = qualityController.fog;
        }

        private void Refresh()
        {
            ResolveReferences(); builder.Clear();
            if (qualityController != null)
            {
                var profile = qualityController.ActiveProfile;
                builder.Append("Quality: ").Append(qualityController.RequestedTier).Append(" -> ")
                    .Append(qualityController.ActiveTier).Append(" fallback=").Append(qualityController.UsingFallback).AppendLine();
                if (!string.IsNullOrEmpty(qualityController.TransitionMessage)) builder.Append("Reason: ").AppendLine(qualityController.TransitionMessage);
                if (profile != null)
                    builder.Append("Fog: ").Append(profile.render.path).Append(" scale=").Append(profile.render.resolutionFraction)
                        .Append(" steps=").Append(profile.render.minimumStepCount).Append('-').Append(profile.render.maximumStepCount)
                        .Append(" fluidTargetScale=").Append(profile.fluid.resolutionScale).AppendLine();
            }
            builder.Append("Render target: ").Append(InteractiveFogRenderDiagnostics.TargetSize.x).Append('x')
                .Append(InteractiveFogRenderDiagnostics.TargetSize.y).Append(" camera=").Append(InteractiveFogRenderDiagnostics.CameraName)
                .Append(" rayMarch=").Append(InteractiveFogRenderDiagnostics.RayMarchPasses)
                .Append(" composite=").Append(InteractiveFogRenderDiagnostics.CompositePasses)
                .Append(" enqueued=").Append(InteractiveFogRenderDiagnostics.EnqueuedPasses)
                .Append(" bound=").Append(InteractiveFogRenderDiagnostics.SetupTargetBindings)
                .Append(" volumes=").Append(InteractiveFogRenderDiagnostics.DrawnVolumes).Append('/')
                .Append(InteractiveFogRenderDiagnostics.EligibleVolumes).Append('/')
                .Append(InteractiveFogRenderDiagnostics.RegisteredVolumes)
                .Append(" skip=").Append(InteractiveFogRenderDiagnostics.SkipReason).AppendLine();
            builder.Append("Temporal: ").Append(InteractiveFogRenderDiagnostics.TemporalState)
                .Append(" reset=").Append(InteractiveFogRenderDiagnostics.HistoryResetReason).AppendLine();
            if (simulation != null)
                builder.Append("Fluid: ").Append(simulation.solverType).Append(" resolution=").Append(simulation.Resolution)
                    .Append(" scale=").Append(simulation.resolutionScale)
                    .Append(" format=").Append(simulation.DensityFormat).Append('/')
                    .Append(simulation.VelocityFormat).Append('/').Append(simulation.PressureFormat)
                    .Append(" fixed=").Append(simulation.useFixedUpdateRate).Append(" hz=").Append(simulation.simulationUpdatesPerSecond)
                    .Append(" dt=").Append(simulation.ActiveSimulationDeltaTime.ToString("F4"))
                    .Append(" steps=").Append(simulation.ExecutedStepsLastFrame).Append(" dropped=").Append(simulation.DroppedSimulationTime.ToString("F3"))
                    .Append(" pressure=").Append(simulation.pressureIteration)
                    .Append(" pressureRuns=").Append(simulation.PressureProjectionDispatches)
                    .Append(" vorticityEvery=").Append(simulation.vorticityCadence)
                    .Append(" vorticityRuns=").Append(simulation.VorticityDispatches).AppendLine();
            currentState = builder.ToString();
        }

        private void OnGUI()
        {
            if (!showRuntimeOverlay || string.IsNullOrEmpty(currentState)) return;
            GUI.Box(new Rect(overlayPosition.x, overlayPosition.y, 620f, 145f), currentState);
        }
    }
}
