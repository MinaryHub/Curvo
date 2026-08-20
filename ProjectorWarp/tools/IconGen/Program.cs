using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Curvo 전용 아이콘 생성기.
// 굽은 벽면에 투사된 화면(휘어진 격자 사각형) + 프로젝터 광선을 여러 해상도로 그려
// 하나의 .ico 로 묶는다. 작은 크기는 DIB, 256px 만 PNG 로 넣는다
// (GDI+ 의 System.Drawing.Icon 은 PNG 항목을 읽지 못한다).
internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

    /// <summary>
    /// MSIX 타일 자산. (파일 이름, 너비, 높이) — 아이콘은 정사각형 기준으로 그리고
    /// 와이드 타일은 캔버스 가운데에 배치한다.
    /// </summary>
    private static readonly (string Name, int Width, int Height)[] MsixAssets =
    {
        ("StoreLogo.png", 50, 50),
        ("Square44x44Logo.png", 44, 44),
        ("Square71x71Logo.png", 71, 71),
        ("Square150x150Logo.png", 150, 150),
        ("Square310x310Logo.png", 310, 310),
        ("Wide310x150Logo.png", 310, 150),
        ("SplashScreen.png", 620, 300),
        ("Square44x44Logo.targetsize-16_altform-unplated.png", 16, 16),
        ("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 24),
        ("Square44x44Logo.targetsize-44_altform-unplated.png", 44, 44),
        ("Square44x44Logo.targetsize-256_altform-unplated.png", 256, 256),
    };

    private static int Main(string[] args)
    {
        // --msix <출력폴더> : MSIX 타일 자산만 생성한다.
        if (args.Length >= 1 && args[0] == "--msix")
        {
            string assetDirectory = args.Length > 1 ? args[1] : Path.Combine("packaging", "msix", "Assets");
            Directory.CreateDirectory(assetDirectory);
            foreach ((string name, int width, int height) in MsixAssets)
            {
                using Bitmap tile = DrawTile(width, height);
                tile.Save(Path.Combine(assetDirectory, name), ImageFormat.Png);
            }
            Console.WriteLine($"{assetDirectory}  ({MsixAssets.Length} assets)");
            return 0;
        }

        // 기본값은 저장소 안의 상대 경로다. 절대 경로를 박아 두면 폴더를 옮길 때 함께 깨진다.
        string outPath = args.Length > 0
            ? args[0]
            : Path.Combine("assets", "Curvo.ico");
        string? previewDirectory = args.Length > 1 ? args[1] : null;

        var entries = new List<(int Size, byte[] Bytes, bool Png)>();
        foreach (int size in Sizes)
        {
            using Bitmap bitmap = Draw(size);
            if (size >= 256)
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                entries.Add((size, stream.ToArray(), true));
            }
            else
            {
                entries.Add((size, ToDib(bitmap), false));
            }

            if (previewDirectory is not null)
                bitmap.Save(Path.Combine(previewDirectory, $"icon-{size}.png"), ImageFormat.Png);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        WriteIcon(outPath, entries);

        Console.WriteLine($"{outPath}  ({new FileInfo(outPath).Length:N0} bytes, {entries.Count} sizes, " +
                          $"DIB {entries.Count(e => !e.Png)} + PNG {entries.Count(e => e.Png)})");
        return 0;
    }

    /// <summary>
    /// 타일 하나를 그린다. 정사각형이면 아이콘을 그대로, 와이드/스플래시면
    /// 짧은 변에 맞춘 아이콘을 투명 캔버스 가운데에 놓는다(스토어가 요구하는 크기를 맞추기 위해).
    /// </summary>
    private static Bitmap DrawTile(int width, int height)
    {
        if (width == height) return Draw(width);

        int side = (int)(Math.Min(width, height) * 0.86);
        var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(canvas))
        using (Bitmap icon = Draw(side))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, (width - side) / 2, (height - side) / 2, side, side);
        }
        return canvas;
    }

    private static Bitmap Draw(int size)
    {
        // 32px 미만에서는 프로젝터를 빼고 화면만 크게 그려 실루엣을 살린다.
        // 격자는 24px 부터 넣는다(그 아래에서는 뭉개진다).
        bool full = size >= 32;
        bool detailed = size >= 24;

        float s = size;
        PointF P(double x, double y) => new((float)(x * s), (float)(y * s));

        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // ---- 배경: 둥근 사각형 + 세로 그라데이션 ---------------------------
        float radius = s * 0.22f;
        using var background = new GraphicsPath();
        background.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
        background.AddArc(s - radius * 2, 0, radius * 2, radius * 2, 270, 90);
        background.AddArc(s - radius * 2, s - radius * 2, radius * 2, radius * 2, 0, 90);
        background.AddArc(0, s - radius * 2, radius * 2, radius * 2, 90, 90);
        background.CloseFigure();

        using var backgroundBrush = new LinearGradientBrush(
            new PointF(0, 0), new PointF(0, s),
            Color.FromArgb(255, 34, 47, 66), Color.FromArgb(255, 11, 16, 24));
        g.FillPath(backgroundBrush, background);

        // ---- 광선(화면보다 먼저 깔아 뒤로 보낸다) --------------------------
        if (full)
        {
            using var beam = new GraphicsPath();
            beam.AddPolygon(new[] { P(0.100, 0.870), P(0.250, 0.330), P(0.250, 0.688) });
            using var beamBrush = new SolidBrush(Color.FromArgb(125, 255, 208, 104));
            g.FillPath(beamBrush, beam);
        }

        // ---- 투사된 화면: 위/아래 변이 휜 사각형 ---------------------------
        double left = full ? 0.235 : 0.130;
        double right = full ? 0.825 : 0.870;
        double top = full ? 0.320 : 0.245;
        double bottom = full ? 0.688 : 0.755;
        double bow = full ? 0.072 : 0.090;
        double control1 = left + (right - left) * 0.33;
        double control2 = left + (right - left) * 0.67;

        using var screen = new GraphicsPath();
        screen.AddBezier(P(left, top), P(control1, top - bow), P(control2, top - bow), P(right, top));
        screen.AddLine(P(right, top), P(right, bottom));
        screen.AddBezier(P(right, bottom), P(control2, bottom + bow), P(control1, bottom + bow), P(left, bottom));
        screen.CloseFigure();

        using var screenBrush = new LinearGradientBrush(
            new PointF(0, s * 0.24f), new PointF(0, s * 0.79f),
            Color.FromArgb(240, 104, 230, 255), Color.FromArgb(215, 22, 130, 200));
        g.FillPath(screenBrush, screen);

        using var edge = new Pen(Color.FromArgb(255, 232, 251, 255), Math.Max(1.0f, s * 0.026f))
        {
            LineJoin = LineJoin.Round,
        };
        g.DrawPath(edge, screen);

        // ---- 격자선 -------------------------------------------------------
        if (detailed)
        {
            using var grid = new Pen(Color.FromArgb(200, 10, 46, 72), Math.Max(1.0f, s * 0.016f));
            double height = bottom - top;

            // 가로선은 위/아래 변과 같은 방향으로 휜다.
            foreach (double fraction in new[] { 0.34, 0.67 })
            {
                double t = top + height * fraction;
                double inner = bow * 0.62;
                g.DrawBezier(grid, P(left, t), P(control1, t - inner), P(control2, t - inner), P(right, t));
            }

            // 세로선은 굽은 면에서 직선으로 보여야 하므로 직선. 변의 휨만큼 길게 뺀다.
            foreach (double fraction in new[] { 0.34, 0.67 })
            {
                double u = left + (right - left) * fraction;
                double bulge = bow * 0.75 * (1.0 - Math.Abs(fraction * 2.0 - 1.0));
                g.DrawLine(grid, P(u, top - bulge), P(u, bottom + bulge));
            }
        }

        // ---- 프로젝터 본체 -------------------------------------------------
        if (full)
        {
            float bodyWidth = s * 0.150f;
            float bodyHeight = s * 0.092f;
            float bodyX = s * 0.058f;
            float bodyY = s * 0.828f;
            float bodyRadius = bodyHeight * 0.42f;

            using var body = new GraphicsPath();
            body.AddArc(bodyX, bodyY, bodyRadius * 2, bodyRadius * 2, 180, 90);
            body.AddArc(bodyX + bodyWidth - bodyRadius * 2, bodyY, bodyRadius * 2, bodyRadius * 2, 270, 90);
            body.AddArc(bodyX + bodyWidth - bodyRadius * 2, bodyY + bodyHeight - bodyRadius * 2,
                bodyRadius * 2, bodyRadius * 2, 0, 90);
            body.AddArc(bodyX, bodyY + bodyHeight - bodyRadius * 2, bodyRadius * 2, bodyRadius * 2, 90, 90);
            body.CloseFigure();

            using var bodyBrush = new SolidBrush(Color.FromArgb(255, 255, 205, 92));
            g.FillPath(bodyBrush, body);
        }

        return bitmap;
    }

    /// <summary>32bpp DIB(BITMAPINFOHEADER + XOR + AND 마스크). ICO 항목 형식이다.</summary>
    private static byte[] ToDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[data.Stride * height];
        int stride = data.Stride;
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        int maskStride = (width + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // BITMAPINFOHEADER — 높이는 XOR + AND 를 합쳐 2배로 적는다.
        writer.Write(40u);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0u);
        writer.Write((uint)(width * 4 * height + maskStride * height));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0u);
        writer.Write(0u);

        // XOR 비트맵 — 아래에서 위로
        for (int y = height - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, width * 4);

        // AND 마스크 — 투명도는 알파가 처리하므로 0 으로 둔다.
        writer.Write(new byte[maskStride * height]);

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteIcon(string path, List<(int Size, byte[] Bytes, bool Png)> entries)
    {
        using FileStream file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type = icon
        writer.Write((ushort)entries.Count);

        int offset = 6 + 16 * entries.Count;
        foreach ((int size, byte[] bytes, _) in entries)
        {
            byte dimension = size >= 256 ? (byte)0 : (byte)size;
            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((byte)0);            // palette
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // color planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write((uint)bytes.Length);
            writer.Write((uint)offset);
            offset += bytes.Length;
        }

        foreach ((_, byte[] bytes, _) in entries) writer.Write(bytes);
    }
}
