using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace InteractiveFog
{
    [DisallowMultipleComponent]
    public sealed class InteractiveFogBenchmarkRunner : MonoBehaviour
    {
        [Header("Deterministic Targets")]
        public Camera targetCamera;
        public Transform interactionSphere;
        public FluidSimulation simulation;
        public Vector3 lockedCameraPosition;
        public Vector3 lockedCameraEuler;
        public Vector3 spherePathCenter;
        public Vector3 spherePathRadius = new Vector3(8f, 0.6f, 5f);
        [Min(0.01f)] public float pathCyclesPerSecond = 0.08f;

        [Header("Capture")]
        public bool runOnStart;
        [Min(0f)] public float warmupSeconds = 5f;
        [Min(30)] public int sampleFrameCount = 300;
        [Range(1, 4)] public int repeatedRuns = 2;
        [Range(0.1f, 50f)] public float comparableTolerancePercent = 10f;
        public bool quitPlayerWhenComplete;

        private readonly List<double> gpuSamples = new List<double>(512);
        private readonly List<double> cpuSamples = new List<double>(512);
        private readonly List<BenchmarkRun> completedRuns = new List<BenchmarkRun>(4);
        private FrameTiming[] latestTimings = new FrameTiming[1];
        private float runElapsed;
        private int capturedFrames;
        private bool running;

        [Serializable]
        private sealed class BenchmarkRun
        {
            public int run;
            public int frames;
            public double gpuMedianMs;
            public double gpuP95Ms;
            public double cpuMedianMs;
            public double cpuP95Ms;
        }

        [Serializable]
        private sealed class BenchmarkReport
        {
            public string utcTimestamp;
            public string unityVersion;
            public string scene;
            public int width;
            public int height;
            public string quality;
            public string qualityDiagnostics;
            public string fluidConfiguration;
            public float warmupSeconds;
            public int requestedFrames;
            public float comparableTolerancePercent;
            public bool runsComparable;
            public BenchmarkRun[] runs;
        }

        private void Start()
        {
            if (runOnStart)
                Begin();
        }

        [ContextMenu("Begin Deterministic Benchmark")]
        public void Begin()
        {
            if (targetCamera == null)
                targetCamera = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
            if (simulation == null)
                simulation = FindObjectOfType<FluidSimulation>();
            gpuSamples.Clear(); cpuSamples.Clear(); completedRuns.Clear();
            BeginRun(); running = targetCamera != null && interactionSphere != null;
            if (!running)
                Debug.LogError("Interactive Fog benchmark requires a camera and interaction sphere.", this);
        }

        private void BeginRun()
        {
            runElapsed = 0f; capturedFrames = 0; gpuSamples.Clear(); cpuSamples.Clear();
            if (simulation != null)
                simulation.ResetSimulation();
        }

        private void Update()
        {
            if (!running)
                return;
            targetCamera.transform.SetPositionAndRotation(lockedCameraPosition, Quaternion.Euler(lockedCameraEuler));
            var phase = runElapsed * pathCyclesPerSecond * Mathf.PI * 2f;
            interactionSphere.position = spherePathCenter + new Vector3(
                Mathf.Sin(phase) * spherePathRadius.x,
                Mathf.Sin(phase * 2f + 0.35f) * spherePathRadius.y,
                Mathf.Cos(phase) * spherePathRadius.z);
            runElapsed += Time.unscaledDeltaTime;
            if (runElapsed < warmupSeconds)
                return;
            FrameTimingManager.CaptureFrameTimings();
        }

        private void LateUpdate()
        {
            if (!running || runElapsed < warmupSeconds)
                return;
            var count = FrameTimingManager.GetLatestTimings(1, latestTimings);
            if (count == 0)
                return;
            cpuSamples.Add(latestTimings[0].cpuFrameTime);
            gpuSamples.Add(latestTimings[0].gpuFrameTime);
            capturedFrames++;
            if (capturedFrames < sampleFrameCount)
                return;

            completedRuns.Add(new BenchmarkRun
            {
                run = completedRuns.Count + 1, frames = capturedFrames,
                gpuMedianMs = Percentile(gpuSamples, 0.5), gpuP95Ms = Percentile(gpuSamples, 0.95),
                cpuMedianMs = Percentile(cpuSamples, 0.5), cpuP95Ms = Percentile(cpuSamples, 0.95)
            });
            if (completedRuns.Count < repeatedRuns)
                BeginRun();
            else
                CompleteBenchmark();
        }

        private void CompleteBenchmark()
        {
            running = false;
            var comparable = true;
            if (completedRuns.Count > 1)
            {
                var baseline = Math.Max(completedRuns[0].gpuMedianMs, 0.0001);
                for (var i = 1; i < completedRuns.Count; i++)
                    comparable &= Math.Abs(completedRuns[i].gpuMedianMs - baseline) / baseline * 100.0 <= comparableTolerancePercent;
            }

            var controller = InteractiveFogQualityController.Active;
            var report = new BenchmarkReport
            {
                utcTimestamp = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
                width = Screen.width, height = Screen.height, quality = QualitySettings.names[QualitySettings.GetQualityLevel()],
                qualityDiagnostics = controller != null ? controller.DiagnosticsSummary : "No quality controller",
                fluidConfiguration = simulation != null ? $"Type={simulation.solverType}, Range={simulation.SimulationRange}, Resolution={simulation.Resolution}, FixedRate={simulation.useFixedUpdateRate}, Hz={simulation.simulationUpdatesPerSecond}, Pressure={simulation.pressureIteration}, VorticityCadence={simulation.vorticityCadence}" : "No simulation",
                warmupSeconds = warmupSeconds, requestedFrames = sampleFrameCount,
                comparableTolerancePercent = comparableTolerancePercent, runsComparable = comparable,
                runs = completedRuns.ToArray()
            };
            var path = Path.Combine(Application.persistentDataPath, "InteractiveFogBenchmark.json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, "InteractiveFogHighReference.png"));
            Debug.Log($"Interactive Fog benchmark complete. Comparable={comparable}. Report={path}");
            if (quitPlayerWhenComplete)
                Application.Quit(comparable ? 0 : 2);
        }

        private static double Percentile(List<double> source, double percentile)
        {
            if (source.Count == 0)
                return 0.0;
            var sorted = source.ToArray(); Array.Sort(sorted);
            var index = Math.Min(sorted.Length - 1, Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1));
            return sorted[index];
        }
    }
}
