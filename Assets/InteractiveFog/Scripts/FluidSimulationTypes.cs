using System;
using UnityEngine;

namespace InteractiveFog
{
    public enum FluidSolverType
    {
        K3DEulerian,
        K2DEulerian,
        K2DShallowWater
    }

    public enum FluidControlMode
    {
        Default,
        Advanced
    }

    [Flags]
    public enum FluidSolverContent
    {
        None = 0,
        SolveVorticity = 1 << 0,
        SolvePressure = 1 << 1,
        SolveBuoyancy = 1 << 2
    }

    public enum FluidBoundaryVelocityType
    {
        None,
        Constant,
        Wind,
        Hybrid
    }

    public enum FluidSourceShape
    {
        Sphere,
        Cube
    }

    [Flags]
    public enum FluidSourceType
    {
        Velocity = 1 << 0,
        Density = 1 << 1,
        Temperature = 1 << 2
    }

    public enum FluidAdditionalVelocityType
    {
        None,
        Constant,
        Wind,
        Hybrid
    }

    [Serializable]
    internal struct FluidSourceGpuData
    {
        public Matrix4x4 worldToLocal;
        public Vector4 positionRadius;
        public Vector4 velocityDensity;
        public Vector4 shapeDiffusionFlags;
        public Vector4 centerFlow;
        public Vector4 jetDirectionSpeed;
        public Vector4 jetSpreadEnabled;
    }
}
