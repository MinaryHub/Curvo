// 캡처 텍스처를 베지어 곡면 + 코너 핀 메시에 매핑하고
// 테스트 패턴 / 색상 보정 / 엣지 블렌딩을 적용하는 메인 워핑 셰이더.

cbuffer WarpConstants : register(b0)
{
    // x=밝기, y=대비, z=감마, w=색상 보정 사용 여부(0/1)
    float4 ColorParams;
    // 각 변의 블렌드 폭 (좌, 우, 상, 하) — 0~0.5 정규화
    float4 EdgeBlendWidth;
    // x=블렌드 감마, y=엣지 블렌딩 사용 여부(0/1)
    float4 EdgeBlendParams;
    // x=패턴 모드, y=격자 분할, z=체커 분할, w=링 개수
    float4 PatternParams;
    // x,y=텍스처 UV 스케일(콘텐츠/텍스처 크기), z=출력 종횡비, w=예약
    float4 SourceParams;
};

Texture2D SourceTexture : register(t0);
SamplerState LinearClampSampler : register(s0);

struct VSInput
{
    float2 Position : POSITION;   // 정규화 출력 좌표(0~1)
    float3 TexCoord : TEXCOORD0;  // 투영 좌표 (u*w, v*w, w)
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float3 TexCoord : TEXCOORD0;
};

PSInput VSMain(VSInput input)
{
    PSInput output;
    // 정규화 좌표(좌상단 원점)를 NDC 로 변환
    output.Position = float4(input.Position.x * 2.0 - 1.0, 1.0 - input.Position.y * 2.0, 0.0, 1.0);
    output.TexCoord = input.TexCoord;
    return output;
}

// 선폭이 화면에서 일정하게 보이도록 화면 도함수를 이용한 안티에일리어싱 선.
float AntiAliasedLine(float2 coordinate, float lineWidthPixels)
{
    float2 derivative = fwidth(coordinate);
    float2 distanceToLine = abs(frac(coordinate - 0.5) - 0.5) / max(derivative, 1e-6);
    float minimumDistance = min(distanceToLine.x, distanceToLine.y);
    return 1.0 - saturate(minimumDistance / lineWidthPixels);
}

float3 SmpteColorBar(float u)
{
    int index = (int)floor(saturate(u) * 8.0);
    if (index <= 0) return float3(1.0, 1.0, 1.0);
    if (index == 1) return float3(1.0, 1.0, 0.0);
    if (index == 2) return float3(0.0, 1.0, 1.0);
    if (index == 3) return float3(0.0, 1.0, 0.0);
    if (index == 4) return float3(1.0, 0.0, 1.0);
    if (index == 5) return float3(1.0, 0.0, 0.0);
    if (index == 6) return float3(0.0, 0.0, 1.0);
    return float3(0.0, 0.0, 0.0);
}

float3 EvaluateTestPattern(int mode, float2 uv, float3 sourceColor)
{
    if (mode == 1) // 격자
    {
        float gridLine = AntiAliasedLine(uv * PatternParams.y, 1.0);
        return lerp(float3(0.0, 0.0, 0.0), float3(1.0, 1.0, 1.0), gridLine);
    }
    if (mode == 2) // 체커보드
    {
        float2 cell = floor(uv * PatternParams.z);
        float checker = fmod(cell.x + cell.y, 2.0);
        return float3(checker, checker, checker);
    }
    if (mode == 3) // 원형 링
    {
        float2 centered = (uv - 0.5) * float2(SourceParams.z, 1.0);
        float radius = length(centered) * PatternParams.w;
        float ring = 1.0 - saturate(abs(frac(radius) - 0.5) / max(fwidth(radius), 1e-6));
        float crossHair = AntiAliasedLine(uv * 2.0, 1.0);
        return saturate(float3(ring, ring, ring) + crossHair * float3(0.0, 1.0, 0.0));
    }
    if (mode == 4) return SmpteColorBar(uv.x);       // 컬러바
    if (mode == 5) return float3(1.0, 1.0, 1.0);     // 화이트 풀필드
    if (mode == 6) return float3(0.0, 0.0, 0.0);     // 블랙 풀필드
    return sourceColor;
}

float EdgeFactor(float distanceToEdge, float blendWidth)
{
    if (blendWidth <= 0.0) return 1.0;
    return saturate(distanceToEdge / blendWidth);
}

float4 PSMain(PSInput input) : SV_TARGET
{
    // 코너 핀이 적용된 경우 원근 보간 왜곡을 없애기 위해 직접 나눈다.
    float2 uv = input.TexCoord.xy / max(input.TexCoord.z, 1e-6);

    float3 color = SourceTexture.Sample(LinearClampSampler, uv * SourceParams.xy).rgb;

    int patternMode = (int)PatternParams.x;
    if (patternMode != 0)
        color = EvaluateTestPattern(patternMode, uv, color);

    if (ColorParams.w > 0.5)
    {
        color = (color - 0.5) * ColorParams.y + 0.5;   // 대비
        color = color * ColorParams.x;                 // 밝기
        color = pow(saturate(color), 1.0 / max(ColorParams.z, 1e-3)); // 감마
    }

    if (EdgeBlendParams.y > 0.5)
    {
        float blend = 1.0;
        blend *= EdgeFactor(uv.x, EdgeBlendWidth.x);
        blend *= EdgeFactor(1.0 - uv.x, EdgeBlendWidth.y);
        blend *= EdgeFactor(uv.y, EdgeBlendWidth.z);
        blend *= EdgeFactor(1.0 - uv.y, EdgeBlendWidth.w);
        color *= pow(saturate(blend), max(EdgeBlendParams.x, 1e-3));
    }

    return float4(saturate(color), 1.0);
}
