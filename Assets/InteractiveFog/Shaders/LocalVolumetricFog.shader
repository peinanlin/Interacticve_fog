Shader "InteractiveFog/LocalVolumetricFog"
{
    Properties
    {
        [HDR] _ThinFogColor("Thin Fog Color", Color) = (0.82, 0.9, 1, 1)
        [HDR] _DenseFogColor("Dense Fog Color", Color) = (0.2, 0.3, 0.44, 1)
        _DensityColorStrength("Density Color Strength", Range(0, 8)) = 1.5
        _DensityColorContrast("Density Color Contrast", Range(0.1, 4)) = 1
        _LightAbsorption("Light Absorption", Range(0, 4)) = 0.6
        _BaseDensity("Base Density", Range(0, 4)) = 1.15
        _StepCount("Step Count", Range(16, 160)) = 72
        _NoiseScale("Noise Scale", Range(0.01, 4)) = 0.42
        _NoiseStrength("Noise Strength", Range(0, 1)) = 0.72
        _HeightFalloff("Height Falloff", Range(0, 8)) = 1.35
        [NoScaleOffset] _BakedNoise("Baked Base Noise", 3D) = "white" {}
        [NoScaleOffset] _BakedDetailNoise("Baked Detail Noise", 3D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+20"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Interactive Volumetric Fog"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            // High is the compatibility reference. Keep its original implementation
            // isolated from the reconstructed Medium/Low shader code.
            #include "LocalVolumetricFogLegacy.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Interactive Volumetric Fog Reconstructed"
            // Explicit CommandBuffer.DrawMesh pass only. A custom LightMode keeps
            // URP's normal transparent DrawObjects pass from selecting it in High.
            Tags { "LightMode" = "InteractiveFogReconstructed" }
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragReconstructed
            #pragma multi_compile_instancing
            #define INTERACTIVE_FOG_RECONSTRUCTED 1
            #include "LocalVolumetricFogRayMarch.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Interactive Volumetric Fog Reconstructed Baked Detail"
            Tags { "LightMode" = "InteractiveFogReconstructed" }
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragReconstructed
            #pragma multi_compile_instancing
            #define INTERACTIVE_FOG_RECONSTRUCTED 1
            #define INTERACTIVE_FOG_BAKED_BASE 1
            #define INTERACTIVE_FOG_BAKED_DETAIL 1
            #include "LocalVolumetricFogRayMarch.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Interactive Volumetric Fog Reconstructed Baked Low"
            Tags { "LightMode" = "InteractiveFogReconstructed" }
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragReconstructed
            #pragma multi_compile_instancing
            #define INTERACTIVE_FOG_RECONSTRUCTED 1
            #define INTERACTIVE_FOG_BAKED_BASE 1
            #define INTERACTIVE_FOG_SIMPLIFIED_LIGHTING 1
            #include "LocalVolumetricFogRayMarch.hlsl"
            ENDHLSL
        }
    }
    FallBack Off
}
