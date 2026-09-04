Shader "Hidden/InteractiveFog/Reconstruction"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Bilateral Composite"
            ZTest Always ZWrite Off Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragComposite
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            TEXTURE2D(_InteractiveFogLowDepth);
            SAMPLER(sampler_InteractiveFogLowDepth);
            TEXTURE2D(_InteractiveFogLowSceneDepth);
            SAMPLER(sampler_InteractiveFogLowSceneDepth);
            float _InteractiveFogDepthThreshold;
            float _InteractiveFogBilateralRadius;

            float LinearDepthAt(float2 uv)
            {
                float raw = SampleSceneDepth(uv);
                return LinearEyeDepth(raw, _ZBufferParams);
            }

            half4 CompositeFog(float4 scatteringTransmittance)
            {
                // The low-resolution target is intentionally uncomposited:
                // RGB is premultiplied in-scattering and A is remaining transmittance.
                // With Blend One OneMinusSrcAlpha the returned opacity evaluates to
                // scattering + cameraColor * transmittance.
                return half4(scatteringTransmittance.rgb,
                    saturate(1.0 - scatteringTransmittance.a));
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                if (_InteractiveFogBilateralRadius < 0.5)
                    return CompositeFog(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv));
                float2 texel = rcp(max(_BlitTextureSize, 1.0));
                float fullDepth = LinearDepthAt(uv);
                float2 offsets[4] = { float2(-0.5,-0.5), float2(0.5,-0.5), float2(-0.5,0.5), float2(0.5,0.5) };
                float4 sum = 0;
                float weightSum = 0;
                float bestDelta = 1e20;
                float4 nearestValid = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
                [unroll] for (int i = 0; i < 4; i++)
                {
                    float2 sampleUv = uv + offsets[i] * texel * _InteractiveFogBilateralRadius;
                    float representativeDepth = SAMPLE_TEXTURE2D(_InteractiveFogLowDepth,
                        sampler_InteractiveFogLowDepth, sampleUv).r;
                    float lowSceneDepth = SAMPLE_TEXTURE2D(_InteractiveFogLowSceneDepth,
                        sampler_InteractiveFogLowSceneDepth, sampleUv).r;
                    float sceneDelta = abs(lowSceneDepth - fullDepth);
                    float fogBehindForeground = representativeDepth > 0.0
                        ? max(representativeDepth - fullDepth, 0.0)
                        : 0.0;
                    float depthDelta = sceneDelta + fogBehindForeground;
                    float weight = exp2(-depthDelta / max(_InteractiveFogDepthThreshold, 0.001));
                    float4 fog = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, sampleUv);
                    sum += fog * weight;
                    weightSum += weight;
                    if (depthDelta < bestDelta)
                    {
                        bestDelta = depthDelta;
                        nearestValid = fog;
                    }
                }
                float4 reconstructed = weightSum > 1e-4 ? sum / weightSum : nearestValid;
                return CompositeFog(reconstructed);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Depth Downsample"
            ZTest Always ZWrite Off Cull Off
            Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDepth
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            half FragDepth(Varyings input) : SV_Target
            {
                return (half)LinearEyeDepth(SampleSceneDepth(input.texcoord), _ZBufferParams);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
