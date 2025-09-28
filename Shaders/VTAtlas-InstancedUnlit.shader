Shader "VT/VTAtlas-InstancedUnlit"
{
    Properties { 
        _AtlasTex ("Atlas", 2D) = "white" 
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:SetupProc
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            void SetupProc() { }

            Texture2D _AtlasTex;
            SamplerState sampler_AtlasTex;

            CBUFFER_START(InstanceCBuffer)
                float4 _InstanceCBuffer[256*4]; // 256 实例 × 每实例 4 个 float4
            CBUFFER_END

            struct VSIn {
                float3 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                uint   instanceID : SV_InstanceID;
            };
            struct VSOut {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR0;
            };

            VSOut vert (VSIn v)
            {
                int idx = v.instanceID * 4;
                float2 pos   = _InstanceCBuffer[idx    ].xy;
                float2 size  = _InstanceCBuffer[idx    ].zw;
                float4 rect  = _InstanceCBuffer[idx + 1];
                float4 col   = _InstanceCBuffer[idx + 2];
                float2 pivot = _InstanceCBuffer[idx + 3].xy;
                float  rot   = _InstanceCBuffer[idx + 3].z;
                float  z     = _InstanceCBuffer[idx + 3].w;

                float2 local01 = v.uv;
                float2 local   = (local01 - pivot) * size;
                float s = sin(rot), c = cos(rot);
                float2 rotated = float2(local.x*c - local.y*s, local.x*s + local.y*c);

                float3 world = float3(pos + rotated, z);

                VSOut o;
                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                o.uv  = rect.xy + local01 * rect.zw;
                o.col = col;
                return o;
            }

            half4 frag (VSOut i) : SV_Target
            {
                half tex = _AtlasTex.Sample(sampler_AtlasTex, i.uv).r;
                return  half4(i.col.xyz,i.col.w * tex);
            }
            ENDHLSL
        }
    }
}
