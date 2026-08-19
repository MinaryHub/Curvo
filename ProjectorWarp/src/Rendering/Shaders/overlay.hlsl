// 편집 오버레이(제어점, 격자선, 참조 그리드)와 블랙 마스크를 그리는 셰이더.
// 정점은 이미 정규화 출력 좌표(0~1)로 계산되어 들어온다.

struct VSInput
{
    float2 Position : POSITION;
    float4 Color : COLOR0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
};

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.Position = float4(input.Position.x * 2.0 - 1.0, 1.0 - input.Position.y * 2.0, 0.0, 1.0);
    output.Color = input.Color;
    return output;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    return input.Color;
}
