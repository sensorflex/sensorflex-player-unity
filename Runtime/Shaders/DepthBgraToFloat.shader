Shader "SensorFlex/DepthBgraToFloat"
{
    // Converts a BGRA32 RenderTexture (output of Unity VideoPlayer) into a single-channel
    // RFloat depth texture.
    //
    // Encoding (see SFVideoEncoder.mm):
    //   depth_metres (float32) -> float16 -> uint16 bits
    //   B channel = bits & 0xFF   (low byte)
    //   G channel = bits >> 8     (high byte)
    //   R = 0, A = 0xFF
    //
    // Decode:
    //   bits = (G_byte << 8) | B_byte -> reinterpret as float16 -> float32
    //
    // Channel mapping from VideoPlayer RenderTexture to HLSL sampler:
    //   px.r = R (encoder R = 0, unused)
    //   px.g = G (encoder G = high byte of float16)
    //   px.b = B (encoder B = low byte of float16)

    Properties { _MainTex ("Texture", 2D) = "white" {} }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma exclude_renderers d3d11_9x
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f    { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 px = tex2D(_MainTex, i.uv);
                uint low  = (uint)(px.b * 255.0 + 0.5);
                uint high = (uint)(px.g * 255.0 + 0.5);
                uint bits = (high << 8u) | low;
                float depth = f16tof32(bits);
                return float4(depth, 0, 0, 1);
            }
            ENDCG
        }
    }
}
