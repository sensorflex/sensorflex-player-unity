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
//           _EnvironmentDepth – RFloat texture, metric depth in metres
//           _IsOcclusionOn    – 1 when active, 0 otherwise
//     → this shader samples _EnvironmentDepth and outputs SV_Depth
//
// Depth conversion (identical to Apple's ARKit background shader):
//   Uses Unity's built-in _ZBufferParams (always in sync with the active
//   projection matrix) and _ProjectionParams.y (near clip plane, metres).
//   bufferDepth = (1 / _ZBufferParams.z) * (1/metricDepth - _ZBufferParams.w)
//   This formula maps [near, far] → [1, 0] on reversed-Z platforms (Metal,
//   D3D) and [0, 1] on OpenGL — no UNITY_REVERSED_Z branching required.
//
// Pixels with metricDepth == 0 (invalid / no LiDAR return), or closer than
// the near clip plane, write the far-plane depth so virtual objects there
// are never incorrectly occluded.
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
        // 0 = normal RGB, 1 = depth heat-map visualization
        [IntRange] _DepthVizMode ("Depth Viz Mode", Range(0,1)) = 0
        // Metric depth range used to normalize the heat-map (metres)
        _DepthVizMaxMeters ("Depth Viz Max Meters", Float) = 10.0
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

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

            // 1 when ARShaderOcclusion is active and depth data is valid, 0 otherwise
            int _IsOcclusionOn;

            // 0 = RGB, 1 = depth heat-map
            int   _DepthVizMode;
            float _DepthVizMaxMeters;

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

            // Depth texture texel size (256x192 fixed for SensorFlex LiDAR depth).
            static const float2 DepthTexelSize = float2(1.0 / 256.0, 1.0 / 192.0);

            // Far-plane depth constant (objects beyond this are never occluded).
            float FarDepth()
            {
                #if UNITY_REVERSED_Z
                    return 0.0;
                #else
                    return 1.0;
                #endif
            }

            // Converts metric linear eye depth (metres) to the platform depth buffer value.
            // Identical to Apple's ARKit background shader ConvertDistanceToDepth formula.
            // _ZBufferParams is set by Unity's rendering pipeline from the active projection
            // matrix, so it is always in sync — no dependency on ARShaderOcclusion params.
            // Works correctly for both UNITY_REVERSED_Z and non-reversed-Z without branching.
            float MetricDepthToBufferDepth(float metricDepth)
            {
                // Distances below the near clip plane have undefined projection; push to far.
                if (metricDepth < _ProjectionParams.y) return FarDepth();
                return (1.0 / _ZBufferParams.z) * ((1.0 / metricDepth) - _ZBufferParams.w);
            }

            // Samples metric depth at uv, filling invalid pixels (0.0) from the nearest
            // valid cardinal neighbour. LiDAR drops returns at surface edges; this prevents
            // those invalid texels from punching holes in otherwise-occluded virtual objects.
            // Returns 0.0 if the centre and all four neighbours are invalid.
            float SampleDepthFilled(float2 uv)
            {
                float d = SAMPLE_TEXTURE2D(_EnvironmentDepth, sampler_EnvironmentDepth, uv).r;
                if (d > 0.0) return d;

                float d1 = SAMPLE_TEXTURE2D(_EnvironmentDepth, sampler_EnvironmentDepth, uv + float2( DepthTexelSize.x, 0)).r;
                float d2 = SAMPLE_TEXTURE2D(_EnvironmentDepth, sampler_EnvironmentDepth, uv + float2(-DepthTexelSize.x, 0)).r;
                float d3 = SAMPLE_TEXTURE2D(_EnvironmentDepth, sampler_EnvironmentDepth, uv + float2(0,  DepthTexelSize.y)).r;
                float d4 = SAMPLE_TEXTURE2D(_EnvironmentDepth, sampler_EnvironmentDepth, uv + float2(0, -DepthTexelSize.y)).r;

                // Use the minimum valid neighbour depth (closest real surface wins).
                float minD = 1e10;
                if (d1 > 0.0) minD = min(minD, d1);
                if (d2 > 0.0) minD = min(minD, d2);
                if (d3 > 0.0) minD = min(minD, d3);
                if (d4 > 0.0) minD = min(minD, d4);
                return minD < 1e9 ? minD : 0.0;
            }

            // Jet heat-map: t=0 (close) → red, t=0.5 → green, t=1 (far) → blue.
            // Invalid depth (metricDepth == 0) → black.
            half4 DepthToHeatMap(float metricDepth)
            {
                if (metricDepth <= 0.0) return half4(0, 0, 0, 1);
                float t = saturate(metricDepth / max(_DepthVizMaxMeters, 0.01));
                // Piecewise jet: r, g, b each peak at a different t.
                half r = saturate(1.5 - abs(4.0 * t - 1.0));
                half g = saturate(1.5 - abs(4.0 * t - 2.0));
                half b = saturate(1.5 - abs(4.0 * t - 3.0));
                return half4(r, g, b, 1.0);
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

                float metricDepth = 0.0;
                if (_IsOcclusionOn != 0)
                    metricDepth = SampleDepthFilled(input.uv);

                // Color output: heat-map in depth-viz mode, RGB otherwise.
                if (_DepthVizMode != 0)
                {
                    float rawDepth = SAMPLE_TEXTURE2D(_EnvironmentDepth, sampler_EnvironmentDepth, input.uv).r;
                    output.color = DepthToHeatMap(rawDepth);
                }
                else
                {
                    output.color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                }

                // Depth buffer: always written from metric depth when occlusion is on.
                output.depth = (metricDepth > 0.0)
                    ? MetricDepthToBufferDepth(metricDepth)
                    : FarDepth();

                return output;
            }
            ENDHLSL
        }
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "IgnoreProjector" = "True" }

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
