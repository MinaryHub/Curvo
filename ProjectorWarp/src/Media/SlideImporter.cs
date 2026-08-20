using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Data.Pdf;
using Windows.Storage;

namespace Curvo.Media;

/// <summary>
/// PPT / PDF / 이미지를 "슬라이드 이미지 목록"으로 변환한다.
/// - 이미지: 그대로 사용
/// - PDF: Windows 내장 PDF 렌더러(Windows.Data.Pdf)로 페이지별 PNG 생성 — 외부 프로그램 불필요
/// - PPT: PowerPoint COM 자동화로 PNG 내보내기(가져올 때 한 번만 필요, 재생 중에는 불필요)
/// 변환 결과는 캐시되어 같은 파일을 다시 열면 즉시 로드된다.
/// </summary>
internal static class SlideImporter
{
    public static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public static readonly string[] PresentationExtensions =
        { ".pptx", ".pptm", ".ppt", ".ppsx", ".pps" };

    public const string PdfExtension = ".pdf";

    private const string SlideFileSearchPattern = "*.png";
    private const string CacheCompleteMarker = "_complete.txt";
    private const int PowerPointMsoFalse = 0;
    private const int PowerPointMsoTrue = -1;

    public static bool IsImage(string path) => HasExtension(path, ImageExtensions);

    public static bool IsPresentation(string path) => HasExtension(path, PresentationExtensions);

    public static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(PdfExtension, StringComparison.OrdinalIgnoreCase);

    public static bool IsSupported(string path) => IsImage(path) || IsPresentation(path) || IsPdf(path);

    private static bool HasExtension(string path, string[] extensions)
    {
        string extension = Path.GetExtension(path);
        return extensions.Any(candidate => extension.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>파일 대화상자 필터.</summary>
    public static string BuildFileFilter()
    {
        string images = string.Join(";", ImageExtensions.Select(extension => "*" + extension));
        string presentations = string.Join(";", PresentationExtensions.Select(extension => "*" + extension));
        return $"Files that can be shown as slides|{presentations};*{PdfExtension};{images}" +
               $"|PowerPoint ({presentations})|{presentations}" +
               $"|PDF (*{PdfExtension})|*{PdfExtension}" +
               $"|Images ({images})|{images}" +
               "|All files (*.*)|*.*";
    }

    /// <summary>
    /// 슬라이드 이미지 경로 목록을 만든다. 오래 걸릴 수 있으므로 백그라운드에서 호출한다.
    /// </summary>
    public static List<string> Import(string path, Action<string>? progress = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The file could not be found.", path);

        if (IsImage(path)) return new List<string> { path };
        if (IsPdf(path)) return ImportPdf(path, progress);
        if (IsPresentation(path)) return ImportPresentation(path, progress);

        throw new NotSupportedException($"Unsupported format: {Path.GetExtension(path)}");
    }

    /// <summary>여러 이미지를 한 번에 슬라이드로 만든다(자연 정렬).</summary>
    public static List<string> ImportImages(IEnumerable<string> paths)
        => paths.Where(File.Exists).OrderBy(NaturalKey, StringComparer.Ordinal).ToList();

    // ---- PDF --------------------------------------------------------------
    private static List<string> ImportPdf(string path, Action<string>? progress)
    {
        string cacheDirectory = GetCacheDirectory(path);
        if (TryUseCache(cacheDirectory, out List<string> cached)) return cached;

        progress?.Invoke("Converting PDF pages to images…");
        Directory.CreateDirectory(cacheDirectory);

        RenderPdfAsync(path, cacheDirectory, progress).GetAwaiter().GetResult();

        MarkCacheComplete(cacheDirectory);
        return CollectSlides(cacheDirectory);
    }

    private static async Task RenderPdfAsync(string path, string cacheDirectory, Action<string>? progress)
    {
        StorageFile source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        PdfDocument document = await PdfDocument.LoadFromFileAsync(source);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(cacheDirectory);

        for (uint index = 0; index < document.PageCount; index++)
        {
            progress?.Invoke($"Converting PDF… {index + 1}/{document.PageCount}");
            using PdfPage page = document.GetPage(index);
            var options = new PdfPageRenderOptions { DestinationWidth = (uint)AppConfig.SlideRenderWidth };

            StorageFile target = await folder.CreateFileAsync(
                FormatSlideFileName((int)index + 1), CreationCollisionOption.ReplaceExisting);
            using Windows.Storage.Streams.IRandomAccessStream stream =
                await target.OpenAsync(FileAccessMode.ReadWrite);
            await page.RenderToStreamAsync(stream, options);
            await stream.FlushAsync();
        }
    }

    // ---- PowerPoint -------------------------------------------------------
    private static List<string> ImportPresentation(string path, Action<string>? progress)
    {
        string cacheDirectory = GetCacheDirectory(path);
        if (TryUseCache(cacheDirectory, out List<string> cached)) return cached;

        Type? applicationType = Type.GetTypeFromProgID("PowerPoint.Application");
        if (applicationType is null)
        {
            throw new InvalidOperationException(
                "PowerPoint is not installed, so PPT files cannot be converted.\n" +
                "In PowerPoint, use File > Export > PDF or Save As > PNG, then open that file instead.");
        }

        progress?.Invoke("Exporting slides to images with PowerPoint…");
        Directory.CreateDirectory(cacheDirectory);

        object? application = null;
        try
        {
            application = Activator.CreateInstance(applicationType)
                ?? throw new InvalidOperationException("PowerPoint could not be started.");
            ExportWithPowerPoint(application, Path.GetFullPath(path), cacheDirectory);
        }
        finally
        {
            if (application is not null)
            {
                TryQuit(application);
                Marshal.FinalReleaseComObject(application);
            }
        }

        List<string> slides = CollectSlides(cacheDirectory);
        if (slides.Count == 0)
            throw new InvalidOperationException("The PowerPoint export produced nothing. The file may be damaged or have no slides.");

        MarkCacheComplete(cacheDirectory);
        return slides;
    }

    private static void ExportWithPowerPoint(object application, string path, string cacheDirectory)
    {
        dynamic app = application;
        dynamic? presentation = null;
        try
        {
            // WithWindow:false 로 창을 띄우지 않고 연다.
            presentation = app.Presentations.Open(path, PowerPointMsoTrue, PowerPointMsoFalse, PowerPointMsoFalse);

            // 슬라이드 비율을 유지하도록 높이를 계산한다(PageSetup 단위는 포인트).
            double slideWidth = (double)presentation.PageSetup.SlideWidth;
            double slideHeight = (double)presentation.PageSetup.SlideHeight;
            int width = AppConfig.SlideRenderWidth;
            int height = slideWidth > 0
                ? (int)Math.Round(width * slideHeight / slideWidth)
                : (int)Math.Round(width * 9.0 / 16.0);

            presentation.Export(cacheDirectory, "PNG", width, height);
        }
        finally
        {
            if (presentation is not null)
            {
                try
                {
                    presentation.Close();
                }
                catch (Exception)
                {
                    // 닫기 실패는 무시하고 종료 처리로 넘어간다.
                }
                Marshal.FinalReleaseComObject(presentation);
            }
        }
    }

    private static void TryQuit(object application)
    {
        try
        {
            ((dynamic)application).Quit();
        }
        catch (Exception)
        {
            // 이미 종료된 경우
        }
    }

    // ---- 캐시 -------------------------------------------------------------
    private static string GetCacheDirectory(string path)
    {
        var info = new FileInfo(path);
        string key = $"{Path.GetFullPath(path).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string name = Convert.ToHexString(hash, 0, 8);
        string safeTitle = string.Concat(Path.GetFileNameWithoutExtension(path)
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        return Path.Combine(AppConfig.SlideCacheDirectory, $"{safeTitle}-{name}");
    }

    private static bool TryUseCache(string cacheDirectory, out List<string> slides)
    {
        slides = new List<string>();
        if (!File.Exists(Path.Combine(cacheDirectory, CacheCompleteMarker))) return false;
        slides = CollectSlides(cacheDirectory);
        return slides.Count > 0;
    }

    private static void MarkCacheComplete(string cacheDirectory)
        => File.WriteAllText(Path.Combine(cacheDirectory, CacheCompleteMarker),
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

    private static string FormatSlideFileName(int number) => $"slide{number:D4}.png";

    /// <summary>변환 폴더의 이미지를 자연 정렬(슬라이드2 &lt; 슬라이드10)로 모은다.</summary>
    private static List<string> CollectSlides(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory)) return new List<string>();
        return Directory.GetFiles(cacheDirectory, SlideFileSearchPattern)
            .OrderBy(NaturalKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>파일 이름 안의 숫자를 0 채움으로 바꿔 자연 정렬용 키를 만든다.</summary>
    private static string NaturalKey(string path)
    {
        string name = Path.GetFileName(path);
        var builder = new StringBuilder(name.Length + 16);
        int index = 0;
        while (index < name.Length)
        {
            if (char.IsDigit(name[index]))
            {
                int start = index;
                while (index < name.Length && char.IsDigit(name[index])) index++;
                builder.Append(name[start..index].PadLeft(10, '0'));
            }
            else
            {
                builder.Append(char.ToLowerInvariant(name[index]));
                index++;
            }
        }
        return builder.ToString();
    }
}
