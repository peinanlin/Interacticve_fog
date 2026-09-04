using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveFog
{
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class FluidSimulation : MonoBehaviour
    {
        private const int MaxSources = 64;
        private static readonly ProfilerMarker SimulationStepMarker = new ProfilerMarker("Interactive Fog Fluid Step");
        private static readonly ProfilerMarker VelocityAdvectionMarker = new ProfilerMarker("Interactive Fog Fluid Velocity Advection");
        private static readonly ProfilerMarker DensityAdvectionMarker = new ProfilerMarker("Interactive Fog Fluid Density Advection");
        private static readonly ProfilerMarker DensityMaskMarker = new ProfilerMarker("Interactive Fog Fluid Density Mask");
        private static readonly ProfilerMarker SourcesMarker = new ProfilerMarker("Interactive Fog Fluid Sources");
        private static readonly ProfilerMarker BoundaryMarker = new ProfilerMarker("Interactive Fog Fluid Boundary");
        private static readonly ProfilerMarker EnvironmentMarker = new ProfilerMarker("Interactive Fog Fluid Environment");
        private static readonly ProfilerMarker BuoyancyMarker = new ProfilerMarker("Interactive Fog Fluid Buoyancy");
        private static readonly ProfilerMarker VorticityMarker = new ProfilerMarker("Interactive Fog Fluid Vorticity");
        private static readonly ProfilerMarker PressureMarker = new ProfilerMarker("Interactive Fog Fluid Pressure Projection");

        [Header("Scrolling")]
        [Tooltip("跟随目标，建议设置为角色 Root。留空时模拟区域固定在本节点。")]
        public Transform following;
        public Vector3 followOffset;

        [Header("DensityMask")]
        public Texture2D densityMask2D;
        public Vector3 densityMaskCenterWS;
        public Vector3 densityMaskSize = new Vector3(20f, 2f, 20f);
        public Vector3 scrollSpeed;
        [Range(0f, 1f)] public float densityMaskScale = 0.01f;

        [Header("Boundary")]
        [Range(0f, 1f)] public float boundaryDensity;
        public FluidBoundaryVelocityType boundaryVelocityType = FluidBoundaryVelocityType.None;
        public Vector3 boundaryVelocity;
        [Range(0f, 10f)] public float boundaryWindStrength = 0.2f;

        [Header("Solver Parameters")]
        public FluidSolverType solverType = FluidSolverType.K2DEulerian;
        public FluidControlMode controlMode = FluidControlMode.Advanced;
        public Vector3 solverRange = new Vector3(20f, 8f, 20f);
        [Range(1, 40)] public int resolutionScale = 20;
        public FluidSolverContent solverContent =
            FluidSolverContent.SolveVorticity | FluidSolverContent.SolvePressure;

        [Header("DeltaTime Parameters")]
        [Range(0.001f, 0.1f)] public float deltaTime = 0.02f;

        [Header("Quality Scheduling")]
        [Tooltip("关闭时保持旧行为：每个渲染帧执行一次模拟。")]
        public bool useFixedUpdateRate;
        [Range(1, 120)] public int simulationUpdatesPerSecond = 50;
        [Range(1, 4)] public int maximumCatchUpSteps = 2;
        [Range(1, 8)] public int vorticityCadence = 1;

        [Header("Density Parameters")]
        [Range(0.01f, 4f)] public float densityLimit = 1f;
        [Range(0.9f, 1f)] public float densityDissipation = 0.9966f;

        [Header("Velocity Parameters")]
        [Range(0.01f, 50f)] public float velocityLimit = 10f;
        [Range(0.9f, 1f)] public float velocityDissipation = 0.9992f;
        [Range(0f, 20f)] public float externalForceScale = 5f;

        [Header("3D Gravity And Wind")]
        [Tooltip("施加到有密度流体上的世界空间重力加速度。仅 K3D Eulerian 生效。")]
        public Vector3 gravity = new Vector3(0f, -2.5f, 0f);
        [Tooltip("目标世界空间风速；向量方向即风向，长度即目标速度。仅 K3D Eulerian 生效。")]
        public Vector3 environmentWind = new Vector3(1.4f, 0f, 0.35f);
        [Tooltip("流体追随目标风速的速度。")]
        [Range(0f, 10f)] public float environmentWindResponse = 0.7f;

        [Header("Vorticity Parameters")]
        [Range(0f, 20f)] public float vorticity = 0.92f;

        [Header("Pressure Parameters")]
        [Range(1, 80)] public int pressureIteration = 4;

        [Header("3D Performance")]
        [Tooltip("3D 纹理单轴安全上限。不会改变 Resolution Scale 的含义，只在显存风险过高时等比缩放。")]
        [Range(24, 160)] public int max3DResolution = 96;

        [Header("Resources")]
        [SerializeField] private ComputeShader fluid2D;
        [SerializeField] private ComputeShader fluid3D;

        private RenderTexture densityA;
        private RenderTexture densityB;
        private RenderTexture velocityA;
        private RenderTexture velocityB;
        private RenderTexture pressureA;
        private RenderTexture pressureB;
        private RenderTexture divergence;
        private RenderTexture curl;
        private ComputeBuffer sourceBuffer;
        private readonly List<FluidSourceGpuData> sourceData = new List<FluidSourceGpuData>(MaxSources);
        private Vector3 simulationCenter;   
        private Vector3Int allocatedResolution;
        private FluidSolverType allocatedType;
        private bool resourcesReady;
        private bool resetRequested = true;
        private Vector3Int pendingScrollShift;  
        private double simulationAccumulator;
        private int completedSimulationSteps;
        private int executedStepsLastFrame;
        private double droppedSimulationTime;
        private int vorticityDispatches;
        private int pressureProjectionDispatches;

        private const float ReferenceDeltaTime = 0.02f;

        public bool Is3D => solverType == FluidSolverType.K3DEulerian;
        public bool State => isActiveAndEnabled && resourcesReady;  
        public string Scope => following != null ? "kGlobal" : "kLocal";
        public RenderTexture DensityTexture => densityA;
        public RenderTexture VelocityTexture => velocityA;
        public RenderTexture PressureTexture => pressureA;
        public RenderTexture DivergenceTexture => divergence;
        public RenderTexture CurlTexture => curl;
        public Vector3 SimulationCenter => simulationCenter;
        public Vector3 SimulationRange => SafeRange();
        public Vector3Int Resolution => allocatedResolution;
        public int ExecutedStepsLastFrame => executedStepsLastFrame;
        public int CompletedSimulationSteps => completedSimulationSteps;
        public double DroppedSimulationTime => droppedSimulationTime;
        public int VorticityDispatches => vorticityDispatches;
        public int PressureProjectionDispatches => pressureProjectionDispatches;
        public string DensityFormat => densityA != null ? densityA.format.ToString() : "Unallocated";
        public string VelocityFormat => velocityA != null ? velocityA.format.ToString() : "Unallocated";
        public string PressureFormat => pressureA != null ? pressureA.format.ToString() : "Unallocated";
        public float ActiveSimulationDeltaTime => useFixedUpdateRate
            ? 1f / Mathf.Max(1, simulationUpdatesPerSecond)
            : (controlMode == FluidControlMode.Advanced ? deltaTime : ReferenceDeltaTime);

        private void OnEnable()
        {
            LoadResources();
            simulationCenter = GetDesiredCenter();
            resetRequested = true;
        }

        private void OnDisable()
        {
            ReleaseResources();
        }

        private void OnDestroy()
        {
            ReleaseResources();
        }

        private void OnValidate()
        {
            solverRange.x = Mathf.Max(0.5f, solverRange.x);
            solverRange.y = Mathf.Max(0.5f, solverRange.y);
            solverRange.z = Mathf.Max(0.5f, solverRange.z);
            pressureIteration = Mathf.Max(1, pressureIteration);
            resolutionScale = Mathf.Max(1, resolutionScale);
            max3DResolution = Mathf.Max(24, max3DResolution);
            simulationUpdatesPerSecond = Mathf.Clamp(simulationUpdatesPerSecond, 1, 120);
            maximumCatchUpSteps = Mathf.Clamp(maximumCatchUpSteps, 1, 4);
            vorticityCadence = Mathf.Clamp(vorticityCadence, 1, 8);
            resetRequested = true;
        }

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            LoadResources();
            EnsureResources();
            if (!resourcesReady)
                return;

            executedStepsLastFrame = 0;
            if (!useFixedUpdateRate)
            {
                ScrollSimulationIfNeeded();
                StepSimulation(ActiveSimulationDeltaTime);
                executedStepsLastFrame = 1;
                return;
            }

            var fixedDelta = 1.0 / Mathf.Max(1, simulationUpdatesPerSecond);
            executedStepsLastFrame = CalculateFixedStepSchedule(simulationAccumulator, Time.deltaTime,
                simulationUpdatesPerSecond, maximumCatchUpSteps, out simulationAccumulator,
                out var droppedThisFrame);
            droppedSimulationTime += droppedThisFrame;
            for (var step = 0; step < executedStepsLastFrame; step++)
            {
                ScrollSimulationIfNeeded();
                StepSimulation((float)fixedDelta);
            }
        }

        public static int CalculateFixedStepSchedule(double previousAccumulator, float renderDeltaTime,
            int updatesPerSecond, int catchUpLimit, out double remainingAccumulator,
            out double droppedTime)
        {
            var rate = Mathf.Clamp(updatesPerSecond, 1, 120);
            var limit = Mathf.Clamp(catchUpLimit, 1, 4);
            var fixedDelta = 1.0 / rate;
            var accumulated = Math.Max(0.0, previousAccumulator) +
                              Math.Min(Math.Max(0f, renderDeltaTime), 0.25f);
            var availableSteps = (int)Math.Floor((accumulated + 1e-9) / fixedDelta);
            var executedSteps = Math.Min(availableSteps, limit);
            accumulated -= executedSteps * fixedDelta;
            droppedTime = 0.0;
            if (accumulated + 1e-9 >= fixedDelta)
            {
                var retained = accumulated % fixedDelta;
                droppedTime = accumulated - retained;
                accumulated = retained;
            }
            remainingAccumulator = Math.Max(0.0, accumulated);
            return executedSteps;
        }

        public static float CalculateEffectiveRetention(float referenceStepRetention, float stepDeltaTime,
            bool timeCorrect)
        {
            var retention = Mathf.Clamp01(referenceStepRetention);
            if (!timeCorrect)
                return retention;

            var exponent = Mathf.Max(0.0001f, stepDeltaTime) / ReferenceDeltaTime;
            return Mathf.Pow(retention, exponent);
        }

        public static bool ShouldRunVorticity(int completedSteps, int cadence)
        {
            return Mathf.Max(0, completedSteps) % Mathf.Clamp(cadence, 1, 8) == 0;
        }

        public void ConfigureQualityScheduling(bool fixedRate, int updatesPerSecond,
            int catchUpSteps, int requestedVorticityCadence)
        {
            var sanitizedRate = Mathf.Clamp(updatesPerSecond, 1, 120);
            if (useFixedUpdateRate != fixedRate || simulationUpdatesPerSecond != sanitizedRate)
                simulationAccumulator = 0.0;
            useFixedUpdateRate = fixedRate;
            simulationUpdatesPerSecond = sanitizedRate;
            maximumCatchUpSteps = Mathf.Clamp(catchUpSteps, 1, 4);
            vorticityCadence = Mathf.Clamp(requestedVorticityCadence, 1, 8);
        }

        public void ResetSimulation()
        {
            resetRequested = true;
            if (Application.isPlaying)
            {
                EnsureResources();
                if (resourcesReady)
                    ClearAllFields();
            }
        }

        private void LoadResources()
        {
            if (fluid2D == null)
                fluid2D = Resources.Load<ComputeShader>("InteractiveFog/Fluid2D");
            if (fluid3D == null)
                fluid3D = Resources.Load<ComputeShader>("InteractiveFog/Fluid3D");
        }

        private Vector3 SafeRange()
        {
            return new Vector3(
                Mathf.Max(0.5f, solverRange.x),
                Mathf.Max(0.5f, solverRange.y),
                Mathf.Max(0.5f, solverRange.z));
        }

        private Vector3 GetDesiredCenter()
        {
            return (following != null ? following.position : transform.position) + followOffset;
        }

        private Vector3Int CalculateResolution()
        {
            var range = SafeRange();
            if (!Is3D)
            {
                return new Vector3Int(
                    Mathf.Clamp(Mathf.CeilToInt(range.x * resolutionScale), 16, 1024),
                    Mathf.Clamp(Mathf.CeilToInt(range.z * resolutionScale), 16, 1024),
                    1);
            }

            var raw = new Vector3(
                Mathf.Max(8f, range.x * resolutionScale),
                Mathf.Max(8f, range.y * resolutionScale),
                Mathf.Max(8f, range.z * resolutionScale));
            var scale = Mathf.Min(1f, max3DResolution / Mathf.Max(raw.x, Mathf.Max(raw.y, raw.z)));
            return new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(raw.x * scale), 8, max3DResolution),
                Mathf.Clamp(Mathf.CeilToInt(raw.y * scale), 8, max3DResolution),
                Mathf.Clamp(Mathf.CeilToInt(raw.z * scale), 8, max3DResolution));
        }

        private void EnsureResources()
        {
            var shader = Is3D ? fluid3D : fluid2D;
            if (shader == null || !SystemInfo.supportsComputeShaders)
            {
                resourcesReady = false;
                return;
            }

            var wanted = CalculateResolution();
            if (resourcesReady && wanted == allocatedResolution && allocatedType == solverType)
            {
                if (resetRequested)
                    ClearAllFields();
                return;
            }

            ReleaseResources();
            allocatedResolution = wanted;
            allocatedType = solverType;
            simulationCenter = GetDesiredCenter();

            if (Is3D)
            {
                densityA = Create3DField("Density A", RenderTextureFormat.RHalf);
                densityB = Create3DField("Density B", RenderTextureFormat.RHalf);
                velocityA = Create3DField("Velocity A", RenderTextureFormat.ARGBHalf);
                velocityB = Create3DField("Velocity B", RenderTextureFormat.ARGBHalf);
                pressureA = Create3DField("Pressure A", RenderTextureFormat.RHalf);
                pressureB = Create3DField("Pressure B", RenderTextureFormat.RHalf);
                divergence = Create3DField("Divergence", RenderTextureFormat.RHalf);
                curl = Create3DField("Curl", RenderTextureFormat.ARGBHalf);
            }
            else
            {
                densityA = Create2DField("Density A", RenderTextureFormat.RHalf);
                densityB = Create2DField("Density B", RenderTextureFormat.RHalf);
                velocityA = Create2DField("Velocity A", RenderTextureFormat.RGHalf);
                velocityB = Create2DField("Velocity B", RenderTextureFormat.RGHalf);
                pressureA = Create2DField("Pressure A", RenderTextureFormat.RHalf);
                pressureB = Create2DField("Pressure B", RenderTextureFormat.RHalf);
                divergence = Create2DField("Divergence", RenderTextureFormat.RHalf);
                curl = Create2DField("Curl", RenderTextureFormat.RHalf);
            }

            sourceBuffer = new ComputeBuffer(MaxSources, Marshal.SizeOf<FluidSourceGpuData>(), ComputeBufferType.Structured);
            resourcesReady = true;
            ClearAllFields();
        }

        private RenderTexture Create2DField(string fieldName, RenderTextureFormat format)
        {
            var texture = new RenderTexture(allocatedResolution.x, allocatedResolution.y, 0, format,
                RenderTextureReadWrite.Linear)
            {
                name = $"{name} {fieldName}",
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                autoGenerateMips = false
            };
            texture.Create();
            return texture;
        }

        private RenderTexture Create3DField(string fieldName, RenderTextureFormat format)
        {
            var texture = new RenderTexture(allocatedResolution.x, allocatedResolution.y, 0, format,
                RenderTextureReadWrite.Linear)
            {
                name = $"{name} {fieldName}",
                dimension = TextureDimension.Tex3D,
                volumeDepth = allocatedResolution.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                autoGenerateMips = false
            };
            texture.Create();
            return texture;
        }

        private void ReleaseResources()
        {
            ReleaseTexture(ref densityA);
            ReleaseTexture(ref densityB);
            ReleaseTexture(ref velocityA);
            ReleaseTexture(ref velocityB);
            ReleaseTexture(ref pressureA);
            ReleaseTexture(ref pressureB);
            ReleaseTexture(ref divergence);
            ReleaseTexture(ref curl);
            sourceBuffer?.Release();
            sourceBuffer = null;
            resourcesReady = false;
        }

        private static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null)
                return;
            texture.Release();
            if (Application.isPlaying)
                Destroy(texture);
            else
                DestroyImmediate(texture);
            texture = null;
        }

        private void ClearAllFields()
        {
            if (!resourcesReady)
                return;

            var shader = Is3D ? fluid3D : fluid2D;
            var kernel = shader.FindKernel("Clear");
            SetCommon(shader, ActiveSimulationDeltaTime);
            BindClearTargets(shader, kernel, densityA, velocityA, pressureA);
            Dispatch(shader, kernel);
            BindClearTargets(shader, kernel, densityB, velocityB, pressureB);
            Dispatch(shader, kernel);
            resetRequested = false;
        }

        private void BindClearTargets(ComputeShader shader, int kernel, RenderTexture density,
            RenderTexture velocity, RenderTexture pressure)
        {
            shader.SetTexture(kernel, "_DensityWrite", density);
            shader.SetTexture(kernel, "_VelocityWrite", velocity);
            shader.SetTexture(kernel, "_PressureWrite", pressure);
            shader.SetTexture(kernel, "_DivergenceWrite", divergence);
            shader.SetTexture(kernel, "_CurlWrite", curl);
        }

        private void ScrollSimulationIfNeeded()
        {
            pendingScrollShift = Vector3Int.zero;
            if (following == null)
                return;

            var desired = GetDesiredCenter();
            var range = SafeRange();
            var cellSize = Is3D
                ? new Vector3(range.x / allocatedResolution.x, range.y / allocatedResolution.y,
                    range.z / allocatedResolution.z)
                : new Vector3(range.x / allocatedResolution.x, 1f, range.z / allocatedResolution.y);
            var displacement = desired - simulationCenter;
            var shift = Is3D
                ? new Vector3Int(
                    Mathf.RoundToInt(displacement.x / cellSize.x),
                    Mathf.RoundToInt(displacement.y / cellSize.y),
                    Mathf.RoundToInt(displacement.z / cellSize.z))
                : new Vector3Int(
                    Mathf.RoundToInt(displacement.x / cellSize.x),
                    Mathf.RoundToInt(displacement.z / cellSize.z),
                    0);
            if (shift == Vector3Int.zero)
                return;

            if (!Is3D)
            {
                // The 2D advection kernels consume this shift directly, avoiding
                // two extra full-grid ShiftDensity/ShiftVelocity dispatches.
                pendingScrollShift = shift;
                simulationCenter += new Vector3(shift.x * cellSize.x, 0f, shift.y * cellSize.z);
                return;
            }

            var shader = Is3D ? fluid3D : fluid2D;
            SetCommon(shader, ActiveSimulationDeltaTime);
            shader.SetInts("_Shift", shift.x, shift.y, shift.z);
            var densityKernel = shader.FindKernel("ShiftDensity");
            shader.SetTexture(densityKernel, "_DensityRead", densityA);
            shader.SetTexture(densityKernel, "_DensityWrite", densityB);
            Dispatch(shader, densityKernel);
            Swap(ref densityA, ref densityB);

            var velocityKernel = shader.FindKernel("ShiftVelocity");
            shader.SetTexture(velocityKernel, "_VelocityRead", velocityA);
            shader.SetTexture(velocityKernel, "_VelocityWrite", velocityB);
            Dispatch(shader, velocityKernel);
            Swap(ref velocityA, ref velocityB);

            simulationCenter += Is3D
                ? new Vector3(shift.x * cellSize.x, shift.y * cellSize.y, shift.z * cellSize.z)
                : new Vector3(shift.x * cellSize.x, 0f, shift.y * cellSize.z);
        }

        private void StepSimulation(float stepDeltaTime)
        {
            using (SimulationStepMarker.Auto())
            {
                GatherSources();
                var shader = Is3D ? fluid3D : fluid2D;
                SetCommon(shader, stepDeltaTime);

                using (VelocityAdvectionMarker.Auto()) RunVelocityAdvection(shader);
                using (DensityAdvectionMarker.Auto()) RunDensityAdvection(shader);
                using (DensityMaskMarker.Auto()) RunDensityMask(shader);
                using (SourcesMarker.Auto()) RunSources(shader);
                using (BoundaryMarker.Auto()) RunBoundary(shader);

                if (Is3D)
                    using (EnvironmentMarker.Auto()) RunEnvironmentForces(shader);

                if ((solverContent & FluidSolverContent.SolveBuoyancy) != 0 && Is3D)
                    using (BuoyancyMarker.Auto()) RunBuoyancy(shader);
                if ((solverContent & FluidSolverContent.SolveVorticity) != 0 &&
                    ShouldRunVorticity(completedSimulationSteps, vorticityCadence))
                {
                    using (VorticityMarker.Auto()) RunVorticity(shader);
                    vorticityDispatches++;
                }
                if ((solverContent & FluidSolverContent.SolvePressure) != 0)
                {
                    using (PressureMarker.Auto()) RunPressureProjection(shader);
                    pressureProjectionDispatches++;
                }
                completedSimulationSteps++;
            }
        }

        private void GatherSources()
        {
            sourceData.Clear();
            var sources = FluidSimulationInteractiveSource.ActiveSources;
            for (var i = 0; i < sources.Count && sourceData.Count < MaxSources; i++)
            {
                var source = sources[i];
                if (source == null ||
                    (source.targetSimulation != null && source.targetSimulation != this))
                    continue;
                if (source.TryBuildGpuData(out var data))
                    sourceData.Add(data);
            }
            if (sourceData.Count > 0)
                sourceBuffer.SetData(sourceData, 0, 0, sourceData.Count);
        }

        private void SetCommon(ComputeShader shader, float stepDeltaTime)
        {
            var range = SafeRange();
            shader.SetInts("_Resolution", allocatedResolution.x, allocatedResolution.y, allocatedResolution.z);
            shader.SetVector("_SimulationCenter", simulationCenter);
            shader.SetVector("_SolverRange", range);
            var effectiveDeltaTime = Mathf.Max(0.0001f, stepDeltaTime);
            shader.SetFloat("_DeltaTime", effectiveDeltaTime);
            shader.SetFloat("_DensityDissipation",
                CalculateEffectiveRetention(densityDissipation, effectiveDeltaTime, useFixedUpdateRate));
            shader.SetFloat("_VelocityDissipation",
                CalculateEffectiveRetention(velocityDissipation, effectiveDeltaTime, useFixedUpdateRate));
            shader.SetFloat("_DensityLimit", densityLimit);
            shader.SetFloat("_VelocityLimit", velocityLimit);
            shader.SetFloat("_ExternalForceScale", externalForceScale);
            shader.SetFloat("_Vorticity", vorticity);
            shader.SetFloat("_BoundaryDensity", boundaryDensity);
            shader.SetInt("_BoundaryVelocityType", (int)boundaryVelocityType);
            var wind = boundaryVelocity;
            if (boundaryVelocityType == FluidBoundaryVelocityType.Wind ||
                boundaryVelocityType == FluidBoundaryVelocityType.Hybrid)
                wind += transform.forward * boundaryWindStrength;
            shader.SetVector("_BoundaryVelocity", wind);
            shader.SetInt("_SourceCount", sourceData.Count);
            shader.SetVector("_DensityMaskCenter", densityMaskCenterWS);
            shader.SetVector("_DensityMaskSize", densityMaskSize);
            shader.SetVector("_DensityMaskScroll", scrollSpeed * Time.time);
            shader.SetFloat("_DensityMaskScale", densityMaskScale);
            if (!Is3D)
            {
                shader.SetVector("_ScrollOffset", new Vector4(
                    pendingScrollShift.x / (float)Mathf.Max(allocatedResolution.x, 1),
                    pendingScrollShift.y / (float)Mathf.Max(allocatedResolution.y, 1), 0f, 0f));
            }
            if (Is3D)
            {
                shader.SetVector("_EnvironmentGravity", gravity);
                shader.SetVector("_EnvironmentWind", environmentWind);
                shader.SetFloat("_EnvironmentWindResponse", environmentWindResponse);
            }
        }

        private void RunVelocityAdvection(ComputeShader shader)
        {
            var kernel = shader.FindKernel("AdvectVelocity");
            shader.SetTexture(kernel, "_VelocityRead", velocityA);
            shader.SetTexture(kernel, "_VelocityWrite", velocityB);
            Dispatch(shader, kernel);
            Swap(ref velocityA, ref velocityB);
        }

        private void RunDensityAdvection(ComputeShader shader)
        {
            var kernel = shader.FindKernel("AdvectDensity");
            shader.SetTexture(kernel, "_VelocityRead", velocityA);
            shader.SetTexture(kernel, "_DensityRead", densityA);
            shader.SetTexture(kernel, "_DensityWrite", densityB);
            Dispatch(shader, kernel);
            Swap(ref densityA, ref densityB);
            pendingScrollShift = Vector3Int.zero;
        }

        private void RunDensityMask(ComputeShader shader)
        {
            if (densityMask2D == null)
                return;
            var kernel = shader.FindKernel("InjectDensityMask");
            shader.SetTexture(kernel, "_DensityMask2D", densityMask2D);
            shader.SetTexture(kernel, "_DensityRead", densityA);
            shader.SetTexture(kernel, "_DensityWrite", densityB);
            Dispatch(shader, kernel);
            Swap(ref densityA, ref densityB);
        }

        private void RunSources(ComputeShader shader)
        {
            if (sourceData.Count == 0)
                return;
            var kernel = shader.FindKernel("ApplySources");
            shader.SetBuffer(kernel, "_Sources", sourceBuffer);
            shader.SetTexture(kernel, "_DensityRead", densityA);
            shader.SetTexture(kernel, "_DensityWrite", densityB);
            shader.SetTexture(kernel, "_VelocityRead", velocityA);
            shader.SetTexture(kernel, "_VelocityWrite", velocityB);
            Dispatch(shader, kernel);
            Swap(ref densityA, ref densityB);
            Swap(ref velocityA, ref velocityB);
        }

        private void RunBoundary(ComputeShader shader)
        {
            if (!Is3D)
            {
                var edgeKernel = shader.FindKernel("ApplyBoundaryEdges");
                shader.SetTexture(edgeKernel, "_DensityInOut", densityA);
                shader.SetTexture(edgeKernel, "_VelocityInOut", velocityA);
                shader.Dispatch(edgeKernel,
                    Mathf.CeilToInt(Mathf.Max(allocatedResolution.x, allocatedResolution.y) / 64f), 1, 1);
                return;
            }

            var kernel = shader.FindKernel("ApplyBoundary");
            shader.SetTexture(kernel, "_DensityRead", densityA);
            shader.SetTexture(kernel, "_DensityWrite", densityB);
            shader.SetTexture(kernel, "_VelocityRead", velocityA);
            shader.SetTexture(kernel, "_VelocityWrite", velocityB);
            Dispatch(shader, kernel);
            Swap(ref densityA, ref densityB);
            Swap(ref velocityA, ref velocityB);
        }

        private void RunBuoyancy(ComputeShader shader)
        {
            var kernel = shader.FindKernel("ApplyBuoyancy");
            shader.SetTexture(kernel, "_DensityRead", densityA);
            shader.SetTexture(kernel, "_VelocityRead", velocityA);
            shader.SetTexture(kernel, "_VelocityWrite", velocityB);
            Dispatch(shader, kernel);
            Swap(ref velocityA, ref velocityB);
        }

        private void RunEnvironmentForces(ComputeShader shader)
        {
            var kernel = shader.FindKernel("ApplyEnvironmentForces");
            shader.SetTexture(kernel, "_DensityRead", densityA);
            shader.SetTexture(kernel, "_VelocityRead", velocityA);
            shader.SetTexture(kernel, "_VelocityWrite", velocityB);
            Dispatch(shader, kernel);
            Swap(ref velocityA, ref velocityB);
        }

        private void RunVorticity(ComputeShader shader)
        {
            var curlKernel = shader.FindKernel("ComputeCurl");
            shader.SetTexture(curlKernel, "_VelocityRead", velocityA);
            shader.SetTexture(curlKernel, "_CurlWrite", curl);
            Dispatch(shader, curlKernel);

            var forceKernel = shader.FindKernel("ApplyVorticity");
            shader.SetTexture(forceKernel, "_CurlRead", curl);
            shader.SetTexture(forceKernel, "_VelocityRead", velocityA);
            shader.SetTexture(forceKernel, "_VelocityWrite", velocityB);
            Dispatch(shader, forceKernel);
            Swap(ref velocityA, ref velocityB);
        }

        private void RunPressureProjection(ComputeShader shader)
        {
            var divergenceKernel = shader.FindKernel("ComputeDivergence");
            shader.SetTexture(divergenceKernel, "_VelocityRead", velocityA);
            shader.SetTexture(divergenceKernel, "_DivergenceWrite", divergence);
            Dispatch(shader, divergenceKernel);

            var jacobiKernel = shader.FindKernel("JacobiPressure");
            for (var i = 0; i < pressureIteration; i++)
            {
                shader.SetTexture(jacobiKernel, "_PressureRead", pressureA);
                shader.SetTexture(jacobiKernel, "_PressureWrite", pressureB);
                shader.SetTexture(jacobiKernel, "_DivergenceRead", divergence);
                Dispatch(shader, jacobiKernel);
                Swap(ref pressureA, ref pressureB);
            }

            var gradientKernel = shader.FindKernel("SubtractGradient");
            shader.SetTexture(gradientKernel, "_PressureRead", pressureA);
            shader.SetTexture(gradientKernel, "_VelocityRead", velocityA);
            shader.SetTexture(gradientKernel, "_VelocityWrite", velocityB);
            Dispatch(shader, gradientKernel);
            Swap(ref velocityA, ref velocityB);
        }

        private void Dispatch(ComputeShader shader, int kernel)
        {
            if (Is3D)
            {
                shader.Dispatch(kernel,
                    Mathf.CeilToInt(allocatedResolution.x / 4f),
                    Mathf.CeilToInt(allocatedResolution.y / 4f),
                    Mathf.CeilToInt(allocatedResolution.z / 4f));
            }
            else
            {
                shader.Dispatch(kernel,
                    Mathf.CeilToInt(allocatedResolution.x / 8f),
                    Mathf.CeilToInt(allocatedResolution.y / 8f),
                    1);
            }
        }

        private static void Swap(ref RenderTexture first, ref RenderTexture second)
        {
            var temporary = first;
            first = second;
            second = temporary;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.15f, 0.7f, 1f, 0.45f);
            var center = Application.isPlaying ? simulationCenter : GetDesiredCenter();
            var range = SafeRange();
            if (!Is3D)
                range.y = 0.1f;
            Gizmos.DrawWireCube(center, range);
        }
    }
}
