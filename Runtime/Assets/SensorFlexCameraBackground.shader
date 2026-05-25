// SensorFlexCameraBackground.shader
//
// Renders the replay color frame as the AR camera background and, when
// environment depth is available, writes real-world metric depth into the
// GPU depth buffer so that all 3D scene objects are occluded automatically
// through standard depth testing — no keyword support required in object shaders.
//
// Occlusion data flow:
//   OcclusionSubsystem (TryGetFrame / GetTextureDescriptors)
//     → AROcclusionManager (frameReceived event)
//       → ARShaderOcclusion (sets globals each frame):
//           _EnvironmentDepth              – RFloat texture, metric depth in metres
//           _NdcLinearConversionParameters – (x=invDepthFactor, y=depthOffset)
//           _IsOcclusionOn                 – 1 when active, 0 otherwise
//     → this shader samples _EnvironmentDepth and outputs SV_Depth
//
// Depth conversion (matching AR Foundation Utils include):
//   symmetricNDC = _NdcLinearConversionParameters.x / metricDepth
//                - _NdcLinearConversionParameters.y
//   ndc01        = (symmetricNDC + 1.0) * 0.5
//   bufferDepth  = UNITY_REVERSED_Z ? (1 - ndc01) : ndc01
//
// Pixels with metricDepth == 0 (invalid / no LiDAR return) write the far
// plane depth so virtual objects there are never incorrectly occluded.
//
// Requires:
//   • ARBackgroundRendererFeature in the active URP renderer asset
//   • ARCameraBackground component in BeforeOpaques rendering mode
//   • AROcclusionManager + ARShaderOcclusion components on the camera
//   • Depth enabled on ARSensorFlexSession and a valid depth source (.bin files)

Shader "SensorFlex/CameraBackground"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Background"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On       // Must be On for SV_Depth to reach the depth buffer
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // Set each frame by ARShaderOcclusion when AROcclusionManager has data
            TEXTURE2D(_EnvironmentDepth);
            SAMPLER(sampler_EnvironmentDepth);

            // x = -2*far*near/(far-near), y = -(far+near)/(far-near)
            // Set globally by ARShaderOcclusion from the XROcclusionFrame near/far values
            float4 _NdcLinearConversionParameters;

            // 1 when ARShaderOcclusion is active and depth data is valid, 0 otherwise
            int _IsOcclusionOn;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            // Convert metric linear eye depth (metres) to the platform depth buffer value.
            float MetricDepthToBufferDepth(float metricDepth)
            {
                float symmetricNDC = _NdcLinearConversionParameters.x / metricDepth
                                   - _NdcLinearConversionParameters.y;
                float ndc01 = (symmetricNDC + 1.0) * 0.5;
                #if UNITY_REVERSED_Z
                    return 1.0 - ndc01;
                #else
                    return ndc01;
                #endif
            }

            // Far-plane depth constant (objects beyond this are never occluded).
            float FarDepth()
            {
                #if UNITY_REVERSED_Z
                    return 0.0;
                #else
                    return 1.0;
                #endif
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            FragOutput frag(Varyings input)
            {
                FragOutput output;
                output.color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                if (_IsOcclusionOn != 0)
                {
                    // _EnvironmentDepth is RFloat: .r holds metric depth in metres.
                    // 0.0 means invalid (no LiDAR return) — push to far plane.
                    float metricDepth = SAMPLE_TEXTURE2D(_EnvironmentDepth, sampler_EnvironmentDepth, input.uv).r;
                    output.depth = metricDepth > 0.0
                        ? MetricDepthToBufferDepth(metricDepth)
                        : FarDepth();
                }
                else
                {
                    output.depth = FarDepth();
                }

                return output;
            }
            ENDHLSL
        }
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                return tex2D(_MainTex, input.uv);
            }
            ENDCG
        }
    }
}
