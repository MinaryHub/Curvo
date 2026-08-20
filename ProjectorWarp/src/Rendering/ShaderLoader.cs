using System.Reflection;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace Curvo.Rendering;

/// <summary>
/// 별도 .hlsl 파일로 관리되는 셰이더를 임베디드 리소스에서 읽어 컴파일한다.
/// 한글 주석이 포함되므로 소스는 문자열이 아니라 UTF-8 바이트 그대로 넘긴다
/// (문자 수와 바이트 수가 달라 소스가 잘리는 것을 막는다).
/// </summary>
internal static class ShaderLoader
{
    private const string ResourcePrefix = "Curvo.src.Rendering.Shaders.";
    private const string VertexShaderProfile = "vs_5_0";
    private const string PixelShaderProfile = "ps_5_0";

    private static readonly Dictionary<string, byte[]> SourceCache = new(StringComparer.OrdinalIgnoreCase);

    public static ReadOnlyMemory<byte> CompileVertexShader(string fileName, string entryPoint)
        => Compile(fileName, entryPoint, VertexShaderProfile);

    public static ReadOnlyMemory<byte> CompilePixelShader(string fileName, string entryPoint)
        => Compile(fileName, entryPoint, PixelShaderProfile);

    private static ReadOnlyMemory<byte> Compile(string fileName, string entryPoint, string profile)
    {
        byte[] source = LoadSource(fileName);

        ShaderFlags flags = ShaderFlags.OptimizationLevel3;
#if DEBUG
        flags = ShaderFlags.Debug | ShaderFlags.SkipOptimization;
#endif

        Result result = Compiler.Compile(
            source, null!, null!, entryPoint, fileName, profile, flags, EffectFlags.None,
            out Blob? blob, out Blob? errors);

        try
        {
            if (result.Failure || blob is null)
            {
                string message = errors is null ? $"HRESULT 0x{result.Code:X8}" : errors.AsString();
                throw new InvalidOperationException($"Shader compilation failed ({fileName}:{entryPoint})\n{message}");
            }
            return blob.AsMemory().ToArray();
        }
        finally
        {
            blob?.Dispose();
            errors?.Dispose();
        }
    }

    private static byte[] LoadSource(string fileName)
    {
        lock (SourceCache)
        {
            if (SourceCache.TryGetValue(fileName, out byte[]? cached)) return cached;
        }

        Assembly assembly = typeof(ShaderLoader).Assembly;
        string resourceName = ResourcePrefix + fileName;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Shader resource not found: {resourceName}");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] source = buffer.ToArray();

        lock (SourceCache)
        {
            SourceCache[fileName] = source;
        }
        return source;
    }
}
