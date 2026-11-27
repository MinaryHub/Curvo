using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Curvo.Rendering;

namespace Curvo.Media;

/// <summary>
/// 슬라이드 이미지 묶음. 현재 슬라이드를 D3D11 텍스처로 올려 워핑 파이프라인에 넣는다.
/// 정지 이미지이므로 슬라이드가 바뀔 때만 한 번 업로드한다.
/// </summary>
internal sealed class SlideDeck : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly List<string> _slides = new();
    private readonly Dictionary<int, ID3D11Texture2D> _textures = new();
    private readonly Dictionary<int, SizeInt32> _sizes = new();
    private readonly List<int> _loadOrder = new();
    /// <summary>
    /// 다음 장을 미리 디코딩하는 작업. 1920px 이미지 디코딩은 수십~수백 ms 가 걸려
    /// 넘길 때 UI 스레드에서 하면 화면 전환이 그만큼 멈춘다. 텍스처 생성(빠름)만 넘길 때 한다.
    /// 이 클래스의 다른 상태와 마찬가지로 UI 스레드에서만 접근한다.
    /// </summary>
    private readonly Dictionary<int, Task<(byte[] Pixels, int Width, int Height)>> _prefetch = new();

    private int _currentIndex = -1;
    private bool _disposed;

    public SlideDeck(GraphicsDevice graphics) => _graphics = graphics;

    /// <summary>원본 파일 이름(표시용).</summary>
    public string? SourceLabel { get; private set; }

    public int Count => _slides.Count;

    public int CurrentIndex => _currentIndex;

    public string? CurrentPath => _currentIndex >= 0 && _currentIndex < _slides.Count ? _slides[_currentIndex] : null;

    public ID3D11Texture2D? CurrentTexture { get; private set; }

    public SizeInt32 CurrentSize { get; private set; }

    /// <summary>슬라이드 목록을 설정하고 첫 장을 올린다.</summary>
    public void Load(IReadOnlyList<string> slidePaths, string? sourceLabel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Clear();
        _slides.AddRange(slidePaths.Where(File.Exists));
        SourceLabel = sourceLabel;
        if (_slides.Count == 0) return;

        GoTo(0);
    }

    public bool GoTo(int index)
    {
        if (_slides.Count == 0) return false;
        index = Math.Clamp(index, 0, _slides.Count - 1);

        ID3D11Texture2D? texture = EnsureTexture(index);
        if (texture is null) return false;

        _currentIndex = index;
        CurrentTexture = texture;
        CurrentSize = _sizes[index];
        PrefetchNext();
        return true;
    }

    /// <summary>앞으로 넘길 장을 백그라운드에서 디코딩해 둔다.</summary>
    private void PrefetchNext()
    {
        if (_slides.Count < 2) return;
        int next = (_currentIndex + 1) % _slides.Count;
        if (_textures.ContainsKey(next) || _prefetch.ContainsKey(next)) return;

        // 오래된 예약은 버린다(한 장만 앞서 읽는다).
        _prefetch.Clear();
        string path = _slides[next];
        _prefetch[next] = DecodeImageAsync(path);
    }

    /// <summary>다음 장. wrap 이 true 면 마지막에서 처음으로 돌아간다.</summary>
    public bool Next(bool wrap = true)
    {
        if (_slides.Count == 0) return false;
        int next = _currentIndex + 1;
        if (next >= _slides.Count)
        {
            if (!wrap) return false;
            next = 0;
        }
        return GoTo(next);
    }

    public bool Previous(bool wrap = true)
    {
        if (_slides.Count == 0) return false;
        int previous = _currentIndex - 1;
        if (previous < 0)
        {
            if (!wrap) return false;
            previous = _slides.Count - 1;
        }
        return GoTo(previous);
    }

    private ID3D11Texture2D? EnsureTexture(int index)
    {
        if (_textures.TryGetValue(index, out ID3D11Texture2D? cached))
        {
            Touch(index);
            return cached;
        }

        (byte[] pixels, int width, int height) = TakePrefetched(index) ?? DecodeImage(_slides[index]);
        if (width <= 0 || height <= 0) return null;

        ID3D11Texture2D texture = CreateTexture(pixels, width, height);
        _textures[index] = texture;
        _sizes[index] = new SizeInt32 { Width = width, Height = height };
        Touch(index);
        TrimCache();
        return texture;
    }

    /// <summary>미리 디코딩한 결과가 있으면 꺼낸다. 실패했으면 null 을 돌려 동기 디코딩으로 다시 시도한다.</summary>
    private (byte[] Pixels, int Width, int Height)? TakePrefetched(int index)
    {
        if (!_prefetch.Remove(index, out Task<(byte[] Pixels, int Width, int Height)>? task)) return null;
        try
        {
            // 보통 이미 끝나 있다. 아직이면 남은 시간만 기다린다.
            return task.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Touch(int index)
    {
        _loadOrder.Remove(index);
        _loadOrder.Add(index);
    }

    /// <summary>가장 오래 쓰지 않은 슬라이드 텍스처를 정리한다(현재 장은 유지).</summary>
    private void TrimCache()
    {
        while (_loadOrder.Count > AppConfig.MaxCachedSlideTextures)
        {
            int oldest = _loadOrder[0];
            if (oldest == _currentIndex && _loadOrder.Count > 1) oldest = _loadOrder[1];

            _loadOrder.Remove(oldest);
            if (!_textures.TryGetValue(oldest, out ID3D11Texture2D? texture)) continue;
            _textures.Remove(oldest);
            _sizes.Remove(oldest);
            if (!ReferenceEquals(texture, CurrentTexture)) texture.Dispose();
        }
    }

    private ID3D11Texture2D CreateTexture(byte[] pixels, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(width * 4));
            lock (_graphics.RenderLock)
            {
                return _graphics.Device.CreateTexture2D(description, data);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Windows 내장 이미지 디코더(WIC)로 BGRA8 픽셀을 얻는다.
    /// 정지 이미지 1회 업로드이므로 CPU 경유가 허용된다(동영상/캡처 경로는 GPU 상주 유지).
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) DecodeImage(string path)
        => DecodeImageAsync(path).GetAwaiter().GetResult();

    /// <summary>스레드 풀에서 디코딩한다(UI 스레드의 동기화 컨텍스트로 돌아오지 않는다).</summary>
    private static Task<(byte[] Pixels, int Width, int Height)> DecodeImageAsync(string path)
        => Task.Run(async () =>
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            PixelDataProvider provider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return (provider.DetachPixelData(), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        });

    public void Clear()
    {
        CurrentTexture = null;
        CurrentSize = default;
        _currentIndex = -1;
        SourceLabel = null;

        foreach (ID3D11Texture2D texture in _textures.Values) texture.Dispose();
        _textures.Clear();
        _sizes.Clear();
        _loadOrder.Clear();
        _prefetch.Clear(); // 진행 중인 디코딩은 결과만 버려진다.
        _slides.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}
