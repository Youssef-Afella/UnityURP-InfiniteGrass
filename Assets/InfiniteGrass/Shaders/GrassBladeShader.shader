Shader "InfiniteGrass/GrassBladeShader"
{

    Properties
    {
        [MainTexture] _BaseColorTexture("BaseColor Texture", 2D) = "white" {}
        _ColorA("ColorA", Color) = (0,0,0,1)
        _ColorB("ColorB", Color) = (1,1,1,1)
        _AOColor("AO Color", Color) = (0.5,0.5,0.5)

        [Header(Grass Shape)][Space]
        _GrassWidth("Grass Width", Float) = 1
        _GrassHeight("Grass Height", Float) = 1
        _GrassWidthRandomness("Grass Width Randomness", Range(0, 1)) = 0.25
        _GrassHeightRandomness("Grass Height Randomness", Range(0, 1)) = 0.5

        _GrassCurving("Grass Curving", Float) = 0.1
        [Space]
        _ExpandDistantGrassWidth("Expand Distant Grass Width", Float) = 1
        _ExpandDistantGrassRange("Expand Distant Grass Range", Vector) = (50, 200, 0, 0)

        [Header(Wind)][Space]
        _WindTexture("Wind Texture", 2D) = "white" {}
        _WindScroll("Wind Scroll", Vector) = (1, 1, 0, 0)
        _WindStrength("Wind Strength", Float) = 1

        [Header(Lighting)][Space]
        _RandomNormal("Random Normal", Range(0, 1)) = 0.1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue"="Geometry"}

        Pass
        {
            Cull Back
            ZTest Less
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                half3 normalWS     : TEXCOORD1;
                half3 albedo       : TEXCOORD2;
                float fogFactor    : TEXCOORD3;
                half colorMask     : TEXCOORD4;
                half posY          : TEXCOORD5;
            };

            CBUFFER_START(UnityPerMaterial)
                half3 _ColorA;
                half3 _ColorB;
                float4 _BaseColorTexture_ST;
                half3 _AOColor;

                float _GrassWidth;
                float _GrassHeight;
                float _GrassCurving;
                float _GrassWidthRandomness;
                float _GrassHeightRandomness;

                float _ExpandDistantGrassWidth;
                float2 _ExpandDistantGrassRange;

                float4 _WindTexture_ST;
                float _WindStrength;
                float2 _WindScroll;

                half _RandomNormal;

                float2 _CenterPos;

                float _DrawDistance;
                float _TextureUpdateThreshold;
            CBUFFER_END

            StructuredBuffer<float3> _GrassPositions;

            sampler2D _BaseColorTexture;
            sampler2D _WindTexture;

            sampler2D _GrassColorRT;
            sampler2D _GrassSlopeRT;

            half3 ApplySingleDirectLight(Light light, half3 N, half3 V, half3 albedo, half mask, half positionY)
            {
                half3 H = normalize(light.direction + V);

                half directDiffuse = dot(N, light.direction) * 0.5 + 0.5;

                float directSpecular = saturate(dot(N,H));
                directSpecular *= directSpecular;
                directSpecular *= directSpecular;
                directSpecular *= directSpecular;
                directSpecular *= directSpecular;

                directSpecular *= positionY * 0.12;

                half3 lighting = light.color * (light.shadowAttenuation * light.distanceAttenuation);
                half3 result = (albedo * directDiffuse + directSpecular * (1-mask)) * lighting;

                return result; 
            }

            uint murmurHash3(float input) {
                uint h = abs(input);
                h ^= h >> 16;
                h *= 0x85ebca6b;
                h ^= h >> 13;
                h *= 0xc2b2ae3d;
                h ^= h >> 16;
                return h;
            }

            float random(float input) {
                return murmurHash3(input) / 4294967295.0;
            }

            float srandom(float input) {
                return (murmurHash3(input) / 4294967295.0) * 2 - 1;
            }

            float Remap(float In, float2 InMinMax, float2 OutMinMax)
            {
                return OutMinMax.x + (In - InMinMax.x) * (OutMinMax.y - OutMinMax.x) / (InMinMax.y - InMinMax.x);
            }

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;

                float3 pivot = _GrassPositions[instanceID];

                float2 uv = (pivot.xz - _CenterPos) / (_DrawDistance + _TextureUpdateThreshold);
                uv = uv * 0.5 + 0.5;

                float grassWidth = _GrassWidth * (1 - random(pivot.x * 950 + pivot.z * 10) * _GrassWidthRandomness);

                float distanceFromCamera = length(_WorldSpaceCameraPos - pivot);
                grassWidth += saturate(Remap(distanceFromCamera, float2(_ExpandDistantGrassRange.x, _ExpandDistantGrassRange.y), float2(0, 1))) * _ExpandDistantGrassWidth;
                grassWidth *= (1 - IN.positionOS.y);

                float grassHeight = _GrassHeight * (1 - random(pivot.x * 230 + pivot.z * 10) * _GrassHeightRandomness);
                
                float3 cameraTransformForwardWS = -UNITY_MATRIX_V[2].xyz;

                float4 slope = tex2Dlod(_GrassSlopeRT, float4(uv, 0, 0));
                float xSlope = slope.r * 2 - 1;
                float zSlope = slope.g * 2 - 1;

                float3 slopeDirection = normalize(float3(xSlope, 1 - (max(abs(xSlope), abs(zSlope)) * 0.5), zSlope));
                float3 bladeDirection = normalize(lerp(float3(0, 1, 0), slopeDirection, slope.a));

                half3 windTex = tex2Dlod(_WindTexture, float4(TRANSFORM_TEX(pivot.xz, _WindTexture) + _WindScroll * _Time.y,0,0));
                float2 wind = (windTex.rg * 2 - 1) * _WindStrength * (1-slope.a);

                bladeDirection.xz += wind * IN.positionOS.y;
                bladeDirection = normalize(bladeDirection);
                
                float3 rightTangent = normalize(cross(bladeDirection, cameraTransformForwardWS));

                float3 positionOS = bladeDirection * IN.positionOS.y * grassHeight 
                                    + rightTangent * IN.positionOS.x * grassWidth;

                positionOS.xz += (IN.positionOS.y * IN.positionOS.y) * float2(srandom(pivot.x * 851 + pivot.z * 10), srandom(pivot.z * 647 + pivot.x * 10)) * _GrassCurving;

                float3 positionWS = positionOS + pivot;
                
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.positionWS = positionWS;

                half3 baseColor = lerp(_ColorA, _ColorB, tex2Dlod(_BaseColorTexture, float4(TRANSFORM_TEX(pivot.xz, _BaseColorTexture),0,0)).r);
                half3 albedo = lerp(_AOColor, baseColor, IN.positionOS.y);

                float4 color = tex2Dlod(_GrassColorRT, float4(uv, 0, 0));
                albedo = lerp(albedo, color.rgb, color.a);

                OUT.albedo = albedo;
                OUT.colorMask = color.a;
                OUT.posY = IN.positionOS.y;

                OUT.normalWS = normalize(bladeDirection + cameraTransformForwardWS * -0.5 + _RandomNormal * half3(srandom(pivot.x * 314 + pivot.z * 10), 0, srandom(pivot.z * 677 + pivot.x * 10)));

                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half3 N = normalize(IN.normalWS);
                half3 V = normalize(_WorldSpaceCameraPos - IN.positionWS);
                half3 albedo = IN.albedo;
                half mask = IN.colorMask;
                half posY = IN.posY;

                half3 result = SampleSH(N) * albedo;

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                result += ApplySingleDirectLight(mainLight, N, V, albedo, mask, posY);

                #if defined(_ADDITIONAL_LIGHTS)
                int additionalLightsCount = GetAdditionalLightsCount();
                for (int i = 0; i < additionalLightsCount; ++i)
                {
                    Light light = GetAdditionalLight(i, IN.positionWS);
                    result += ApplySingleDirectLight(light, N, V, albedo, mask, posY);
                }
                #endif

                result = MixFog(result, IN.fogFactor);
                return half4(result, 1);
            }
            ENDHLSL
        }
    }
}
