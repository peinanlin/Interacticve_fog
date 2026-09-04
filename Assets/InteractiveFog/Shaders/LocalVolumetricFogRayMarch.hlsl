#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float4 screenPosition : TEXCOORD1;
    UNITY_VERTEX_OUTPUT_STEREO
};

CBUFFER_START(UnityPerMaterial)
    float4 _ThinFogColor;
    float4 _DenseFogColor;
    float _DensityColorStrength;
    float _DensityColorContrast;
    float _LightAbsorption;
    float _BaseDensity;
    float _StepCount;
    float _AdaptiveSteps;
    float _MinimumStepCount;
    float _MaximumStepLength;
    float _NoiseScale;
    float _NoiseStrength;
    float _HeightFalloff;
    float3 _NoiseVelocity;
    float4x4 _VolumeWorldToLocal;
    float _VolumeShape;
    float _FrustumNearWidthScale;
    float _FrustumNearHeightScale;
    float _Interactive;
    float _InteractionAreaShape;
    float _InteractionEdgeFade;
    float _InteractionFeather;
    float _InteractionBoundaryNoise;
    float _InteractionBoundaryNoiseScale;
    float _InitialDensity;
    float _MinimumInteractionDensity;
    float _DensityScale;
    float _InteractionResponse;
    float _DensityReverseThreshold;
    float _DensityReverseMaxValue;
    float _VelocityScale;
    float _LerpRange;
    float _DirectClearEnabled;
    float3 _DirectClearPosition;
    float _DirectClearRadius;
    float _DirectClearResidualDensity;
    float _DirectClearFeather;
    float _DirectClearNoise;
    float3 _SimulationCenter;
    float3 _SolverRange;
    float _SimulationIs3D;
CBUFFER_END

TEXTURE2D(_Density2D);
SAMPLER(sampler_Density2D);
TEXTURE2D(_Velocity2D);
SAMPLER(sampler_Velocity2D);
TEXTURE3D(_Density3D);
SAMPLER(sampler_Density3D);
TEXTURE3D(_Velocity3D);
SAMPLER(sampler_Velocity3D);
TEXTURE3D(_BakedNoise);
SAMPLER(sampler_BakedNoise);
TEXTURE3D(_BakedDetailNoise);
SAMPLER(sampler_BakedDetailNoise);

Varyings Vert(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = positionInputs.positionCS;
    output.positionWS = positionInputs.positionWS;
    output.screenPosition = ComputeScreenPos(output.positionCS);
    return output;
}

float Hash31(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float ValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash31(i + float3(0, 0, 0));
    float n100 = Hash31(i + float3(1, 0, 0));
    float n010 = Hash31(i + float3(0, 1, 0));
    float n110 = Hash31(i + float3(1, 1, 0));
    float n001 = Hash31(i + float3(0, 0, 1));
    float n101 = Hash31(i + float3(1, 0, 1));
    float n011 = Hash31(i + float3(0, 1, 1));
    float n111 = Hash31(i + float3(1, 1, 1));
    float x00 = lerp(n000, n100, f.x);
    float x10 = lerp(n010, n110, f.x);
    float x01 = lerp(n001, n101, f.x);
    float x11 = lerp(n011, n111, f.x);
    return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
}

float Fbm(float3 p)
{
    float value = 0;
    float amplitude = 0.55;
    [unroll]
    for (int octave = 0; octave < 4; octave++)
    {
        value += ValueNoise(p) * amplitude;
        p = p * 2.03 + 17.17;
        amplitude *= 0.48;
    }
    return value;
}

float FogNoise(float3 p)
{
    #if defined(INTERACTIVE_FOG_BAKED_BASE)
        float baseNoise = SAMPLE_TEXTURE3D_LOD(_BakedNoise, sampler_BakedNoise, frac(p), 0).r;
        #if defined(INTERACTIVE_FOG_BAKED_DETAIL)
            float detailNoise = SAMPLE_TEXTURE3D_LOD(_BakedDetailNoise,
                sampler_BakedDetailNoise, frac(p * 2.03 + 0.173), 0).r;
            return baseNoise * 0.76 + detailNoise * 0.24;
        #else
            return baseNoise;
        #endif
    #else
        return Fbm(p);
    #endif
}

float DirectClearMask(float3 worldPosition)
{
    if (_DirectClearEnabled < 0.5)
        return 0.0;

    float radius = max(_DirectClearRadius, 0.001);
    float noise = ValueNoise(float3(worldPosition.xz * 0.37, _Time.y * 0.035) + 19.31);
    float noisyDistance = length(worldPosition.xz - _DirectClearPosition.xz)
        + (noise - 0.5) * 2.0 * radius * _DirectClearNoise;
    float innerRadius = radius * (1.0 - saturate(_DirectClearFeather));
    return 1.0 - smoothstep(innerRadius, radius, noisyDistance);
}

bool IntersectUnitBox(float3 rayOrigin, float3 rayDirection, out float nearDistance, out float farDistance)
{
    float3 safeDirection = sign(rayDirection) * max(abs(rayDirection), 1e-6);
    float3 inverseDirection = rcp(safeDirection);
    float3 first = (-0.5 - rayOrigin) * inverseDirection;
    float3 second = (0.5 - rayOrigin) * inverseDirection;
    float3 minimum = min(first, second);
    float3 maximum = max(first, second);
    nearDistance = max(minimum.x, max(minimum.y, minimum.z));
    farDistance = min(maximum.x, min(maximum.y, maximum.z));
    return farDistance > max(nearDistance, 0.0);
}

bool InsideVolumeShape(float3 localPosition)
{
    if (_VolumeShape < 0.5)
        return true;

    float depth = saturate(localPosition.z + 0.5);
    float halfWidth = 0.5 * lerp(_FrustumNearWidthScale, 1.0, depth);
    float halfHeight = 0.5 * lerp(_FrustumNearHeightScale, 1.0, depth);
    return abs(localPosition.x) <= halfWidth && abs(localPosition.y) <= halfHeight;
}

float SceneDistance(float2 uv, float3 cameraPosition)
{
    float rawDepth = SampleSceneDepth(uv);
    #if UNITY_REVERSED_Z
        if (rawDepth <= 0.00001) return 1e6;
    #else
        if (rawDepth >= 0.99999) return 1e6;
        rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
    #endif
    float3 scenePosition = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
    return distance(cameraPosition, scenePosition);
}

float SimulationEdge(float3 uvw)
{
    float3 edge3 = min(uvw, 1.0 - uvw);
    return _SimulationIs3D > 0.5 ? min(edge3.x, min(edge3.y, edge3.z)) : min(edge3.x, edge3.z);
}

void SampleSimulation(float3 uvw, out float density, out float3 velocity, out float blend)
{
    bool inside = all(uvw >= 0.0) && all(uvw <= 1.0);
    if (!inside || _Interactive < 0.5)
    {
        density = 0;
        velocity = 0;
        blend = 0;
        return;
    }

    float areaBlend = 1.0;
    if (_InteractionAreaShape > 0.5)
    {
        float2 radialPosition = (uvw.xz - 0.5) * 2.0;
        float radialDistance = length(radialPosition);
        if (radialDistance >= 1.0)
        {
            density = 0;
            velocity = 0;
            blend = 0;
            return;
        }
        areaBlend = 1.0 - smoothstep(1.0 - _InteractionEdgeFade, 1.0, radialDistance);
    }
    if (_SimulationIs3D > 0.5)
    {
        density = SAMPLE_TEXTURE3D_LOD(_Density3D, sampler_Density3D, uvw, 0).r;
        velocity = SAMPLE_TEXTURE3D_LOD(_Velocity3D, sampler_Velocity3D, uvw, 0).xyz;
    }
    else
    {
        density = SAMPLE_TEXTURE2D_LOD(_Density2D, sampler_Density2D, uvw.xz, 0).r;
        float2 velocityXZ = SAMPLE_TEXTURE2D_LOD(_Velocity2D, sampler_Velocity2D, uvw.xz, 0).xy;
        velocity = float3(velocityXZ.x, 0, velocityXZ.y);
    }
    blend = smoothstep(0.0, max(_LerpRange, 0.0001), SimulationEdge(uvw)) * areaBlend;
}

struct FogRayMarchResult
{
    float3 scattering;
    float transmittance;
    float representativeDepth;
};

FogRayMarchResult RayMarchFog(Varyings input)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float2 screenUv = input.screenPosition.xy / input.screenPosition.w;
    float3 rayOriginWorld = _WorldSpaceCameraPos;
    float3 rayDirectionWorld = normalize(input.positionWS - rayOriginWorld);
    float3 rayOriginLocal = mul(_VolumeWorldToLocal, float4(rayOriginWorld, 1)).xyz;
    float3 rayDirectionLocal = mul((float3x3)_VolumeWorldToLocal, rayDirectionWorld);
    float nearDistance;
    float farDistance;
    if (!IntersectUnitBox(rayOriginLocal, rayDirectionLocal, nearDistance, farDistance))
        discard;

    nearDistance = max(nearDistance, 0.0);
    farDistance = min(farDistance, SceneDistance(screenUv, rayOriginWorld));
    if (farDistance <= nearDistance)
        discard;

    int maximumSteps = (int)clamp(_StepCount, 16.0, 160.0);
    int steps = maximumSteps;
    float rayLength = farDistance - nearDistance;
    if (_AdaptiveSteps > 0.5)
    {
        int distanceSteps = (int)ceil(rayLength / max(_MaximumStepLength, 0.001));
        int minimumSteps = (int)clamp(_MinimumStepCount, 1.0, (float)maximumSteps);
        steps = min(maximumSteps, max(minimumSteps, distanceSteps));
    }
    float stepLength = rayLength / steps;
    float jitter = Hash31(float3(input.positionCS.xy, frac(_Time.y))) - 0.5;
    float distanceAlongRay = nearDistance + stepLength * (0.5 + jitter * 0.7);
    float transmittance = 1.0;
    float3 accumulated = 0;
    float representativeDistanceSum = 0;
    float representativeWeight = 0;
    float3 inverseSolverRange = rcp(max(_SolverRange, 0.001));
    float3 simulationRayOrigin = (rayOriginWorld - _SimulationCenter) * inverseSolverRange + 0.5;
    float3 simulationRayDirection = rayDirectionWorld * inverseSolverRange;
    float3 noiseRayOrigin = rayOriginWorld * _NoiseScale + _NoiseVelocity * _Time.y;
    float3 noiseRayDirection = rayDirectionWorld * _NoiseScale;
    float3 boundaryNoiseRayOrigin = rayOriginWorld * _InteractionBoundaryNoiseScale
        + float3(11.7, _Time.y * 0.025, 23.9);
    float3 boundaryNoiseRayDirection = rayDirectionWorld * _InteractionBoundaryNoiseScale;
    float initialFogDensity = max(0.0, _InitialDensity);
    float residualFog = initialFogDensity * saturate(_MinimumInteractionDensity);
    float directResidual = initialFogDensity * saturate(_DirectClearResidualDensity);

    [loop]
    for (int stepIndex = 0; stepIndex < 160; stepIndex++)
    {
        if (stepIndex >= steps || distanceAlongRay >= farDistance || transmittance < 0.01)
            break;
        float3 localPosition = rayOriginLocal + rayDirectionLocal * distanceAlongRay;
        if (!InsideVolumeShape(localPosition))
        {
            distanceAlongRay += stepLength;
            continue;
        }
        float simulationDensity;
        float3 simulationVelocity;
        float simulationBlend;
        float3 simulationUVW = simulationRayOrigin + simulationRayDirection * distanceAlongRay;
        SampleSimulation(simulationUVW, simulationDensity, simulationVelocity, simulationBlend);

        simulationDensity = clamp(simulationDensity * _InteractionResponse, -1.0, 1.0);
        float reversedDensity = simulationDensity;
        if (simulationDensity > 0.0 && simulationDensity < _DensityReverseThreshold)
        {
            float reversedValue = max(-simulationDensity, _DensityReverseMaxValue);
            float reverseBlend = smoothstep(0.0, max(_DensityReverseThreshold, 0.0001), simulationDensity);
            reversedDensity = lerp(reversedValue, simulationDensity, reverseBlend);
        }
        float boundaryNoise = ValueNoise(boundaryNoiseRayOrigin
            + boundaryNoiseRayDirection * distanceAlongRay);
        boundaryNoise = (boundaryNoise - 0.5) * 2.0 * _InteractionBoundaryNoise;
        float rawInteractionMultiplier = max(0.0,
            _InitialDensity + reversedDensity * _DensityScale + boundaryNoise);
        float feather = smoothstep(0.0, max(_InteractionFeather, 0.0001), rawInteractionMultiplier);
        float interactionMultiplier = max(residualFog, rawInteractionMultiplier * feather);
        float densityMultiplier = lerp(initialFogDensity, interactionMultiplier, simulationBlend);
        if (_DirectClearEnabled > 0.5)
        {
            float3 worldPosition = rayOriginWorld + rayDirectionWorld * distanceAlongRay;
            float directClear = DirectClearMask(worldPosition);
            densityMultiplier = lerp(densityMultiplier,
                min(densityMultiplier, directResidual), directClear);
        }

        float3 noisePosition = noiseRayOrigin + noiseRayDirection * distanceAlongRay
            - simulationVelocity * (_VelocityScale * 0.025);
        float noise = FogNoise(noisePosition);
        float shapedNoise = lerp(0.55, smoothstep(0.32, 0.78, noise) * 1.8, _NoiseStrength);
        float height = saturate(localPosition.y + 0.5);
        float heightDensity = exp(-height * _HeightFalloff);
        // A source-generated 2D field has no Y information. Shape its
        // thin extrusion around the volume centre so a spherical source
        // leaves a soft, tube-like wake instead of a hard rectangular wall.
        if (_SimulationIs3D < 0.5 && _InitialDensity <= 0.001)
        {
            float centreDistance = abs(localPosition.y * 2.0);
            heightDensity = pow(saturate(1.0 - centreDistance),
                max(_HeightFalloff * 0.65, 0.25));
        }
        float sampleDensity = _BaseDensity * densityMultiplier * shapedNoise * heightDensity;
        float extinction = max(sampleDensity, 0.0) * stepLength * 0.46;
        float alpha = 1.0 - exp(-extinction);
        #if defined(INTERACTIVE_FOG_SIMPLIFIED_LIGHTING)
            float lighting = 0.67 + noise * 0.34;
        #else
            float lighting = 0.58 + noise * 0.52 + saturate(localPosition.y + 0.25) * 0.12;
        #endif
        float densityColorFactor = saturate(max(sampleDensity, 0.0) * _DensityColorStrength);
        densityColorFactor = max(densityColorFactor, 1e-4);
        if (abs(_DensityColorContrast - 1.0) > 1e-4)
            densityColorFactor = pow(densityColorFactor, max(_DensityColorContrast, 0.1));
        float3 densityColor = lerp(_ThinFogColor.rgb, _DenseFogColor.rgb, densityColorFactor);
        float absorbedLight = exp(-max(sampleDensity, 0.0) * _LightAbsorption);
        float3 sampleColor = densityColor * lighting * absorbedLight;
        float contribution = transmittance * alpha;
        accumulated += contribution * sampleColor;
        representativeDistanceSum += contribution * distanceAlongRay;
        representativeWeight += contribution;
        transmittance *= 1.0 - alpha;
        distanceAlongRay += stepLength;
    }

    FogRayMarchResult result;
    result.scattering = accumulated;
    result.transmittance = saturate(transmittance);
    result.representativeDepth = 0.0;
    if (representativeWeight > 1e-5)
    {
        float representativeDistance = representativeDistanceSum / representativeWeight;
        float3 representativePositionWS = rayOriginWorld + rayDirectionWorld * representativeDistance;
        result.representativeDepth = max(0.0, -TransformWorldToView(representativePositionWS).z);
    }
    return result;
}

#ifndef INTERACTIVE_FOG_RECONSTRUCTED
half4 Frag(Varyings input) : SV_Target
{
    FogRayMarchResult fog = RayMarchFog(input);
    float opacity = saturate(1.0 - fog.transmittance);
    return half4(fog.scattering, opacity);
}
#else
struct FogReconstructedOutput
{
    half4 scatteringTransmittance : SV_Target0;
    half representativeDepth : SV_Target1;
};

FogReconstructedOutput FragReconstructed(Varyings input)
{
    FogRayMarchResult fog = RayMarchFog(input);
    FogReconstructedOutput output;
    output.scatteringTransmittance = half4(fog.scattering, fog.transmittance);
    output.representativeDepth = (half)fog.representativeDepth;
    return output;
}
#endif
