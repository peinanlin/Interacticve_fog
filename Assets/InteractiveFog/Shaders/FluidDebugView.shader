Shader "Hidden/InteractiveFog/DebugField"
{
    Properties
    {
        _MainTex("Field", 2D) = "black" {}
        _VolumeTex("Volume Field", 3D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            int _Mode;
            float _Exposure;

            fixed4 frag(v2f_img input) : SV_Target
            {
                float4 field = tex2D(_MainTex, input.uv);
                if (_Mode == 0)
                {
                    float value = field.r;
                    float magnitude = 1.0 - exp(-abs(value) * _Exposure);
                    return value >= 0.0 ? float4(magnitude, 0, 0, 1) : float4(0, 0, magnitude, 1);
                }
                if (_Mode == 1)
                {
                    float2 velocity = field.xy;
                    float2 mapped = 1.0 - exp(-abs(velocity) * _Exposure);
                    float speed = 1.0 - exp(-length(velocity) * _Exposure);
                    return float4(mapped.x, speed, mapped.y, 1);
                }
                float signedValue = field.r;
                float signedMagnitude = 1.0 - exp(-abs(signedValue) * _Exposure);
                return signedValue >= 0.0
                    ? float4(signedMagnitude, 0, 0, 1)
                    : float4(0, 0, signedMagnitude, 1);
            }
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag3d
            #include "UnityCG.cginc"

            sampler3D _VolumeTex;
            int _Mode;
            float _Exposure;
            float _Slice;

            fixed4 frag3d(v2f_img input) : SV_Target
            {
                float4 field = tex3D(_VolumeTex, float3(input.uv.x, _Slice, input.uv.y));
                if (_Mode == 0)
                {
                    float value = field.r;
                    float magnitude = 1.0 - exp(-abs(value) * _Exposure);
                    return value >= 0.0 ? float4(magnitude, 0, 0, 1) : float4(0, 0, magnitude, 1);
                }
                if (_Mode == 1)
                {
                    float3 velocity = field.xyz;
                    float2 mapped = 1.0 - exp(-abs(velocity.xz) * _Exposure);
                    float speed = 1.0 - exp(-length(velocity) * _Exposure);
                    return float4(mapped.x, speed, mapped.y, 1);
                }
                float signedValue = field.r;
                float signedMagnitude = 1.0 - exp(-abs(signedValue) * _Exposure);
                return signedValue >= 0.0
                    ? float4(signedMagnitude, 0, 0, 1)
                    : float4(0, 0, signedMagnitude, 1);
            }
            ENDHLSL
        }
    }
}
