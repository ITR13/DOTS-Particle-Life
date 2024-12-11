Shader "Unlit/CircleShader"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // make fog work
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float2 _MainTex_TexelSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                const float innerRadius = 0.5;
                const int outerRadius = 5;
                const float divisor = (outerRadius-innerRadius)+1;
                
                half4 maxColor = half4(0, 0, 0, 1); 
                for (int x = -outerRadius; x <= outerRadius; x++)
                {
                    for (int y = -outerRadius; y <= outerRadius; y++)
                    {
                        const float2 offset = float2(x, y) * _MainTex_TexelSize;

                        half4 sampleColor = tex2D(_MainTex, i.uv + offset);
                        const float xN = max(abs(x)-innerRadius, 0) / divisor;
                        const float yN = max(abs(y)-innerRadius, 0) / divisor;

                        float strength = 1 - xN*xN - yN*yN;
                        strength = clamp(strength, 0, 1);
                        sampleColor *= strength * strength * strength;

                        maxColor.r = max(maxColor.r, sampleColor.r);
                        maxColor.g = max(maxColor.g, sampleColor.g);
                        maxColor.b = max(maxColor.b, sampleColor.b);
                    }
                }

                return maxColor;
            }
            ENDCG
        }
    }
}
