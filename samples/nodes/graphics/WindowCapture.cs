using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

// 汎用のウィンドウキャプチャ実装(プロジェクト非依存)。対象アプリには一切手を入れず、
// Win32のP/Invokeだけで完結する。
//
// 主眼は「対象ウィンドウ以外を写さないこと」。
//   デスクトップDCからの取得は画面に合成された結果を撮るため、他のウィンドウが被っていれば
//   そちらが写り込む。撮ってしまってからでは取り消せないので、
//     1)被っているかを撮る前に判定して中止する (WindowFromPoint)
//     2)PrintWindow(PW_RENDERFULLCONTENT)ならDWM経由でウィンドウ自身の内容を取るため被りの影響を受けない
//   の2段構えにしている。2)はDirectX/Vulkanウィンドウでは黒画像になることがあるため、
//   NonBlackRatioで成否を機械的に判定できるようにしてある(目視に頼らない)。
//
// ノードから切り出してあるのは、**撮る瞬間を決める側が自分で撮れるようにする**ため。
//    撮る合図を外部へ出して撮らせる形にすると、往復にかかる時間が撮影の窓を超えて撮り逃す。
//    締め切りのある処理は、経路のいちばん遅い部品に任せない。
internal static class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint GA_ROOT = 2;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint cLines,
                                                                byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint usage);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    internal struct Result
    {
        public bool Saved;
        // 実際に書いた先。拡張子が .bmp でなければこちらで付け替えるため、
        //    呼び出し側が指定したパスと一致しないことがある。
        public string SavedPath;
        public string UsedMethod;
        public bool Occluded;
        public string OccludedBy;
        public float NonBlack;
        public int Width;
        public int Height;

        // **実際に撮った相手。** これを返さないと、指定に一致した別のウィンドウを
        //    撮っていても呼び出し側からは区別が付かない。
        public string MatchedTitle;
        // 複数一致して中止したときの一覧。空でなければ「撮っていない」。
        public string Ambiguous;
    }

    /// <summary>
    /// 指定タイトルのウィンドウを画像へ保存する。
    /// method: printwindow / desktopdc / auto(printwindowを試し黒ければdesktopdc)。
    /// processId: 0以外ならそのプロセスのウィンドウに限る。
    /// </summary>
    internal static Result Capture(string titleContains, string outPath, string method, bool allowOccluded,
                                    int processId = 0)
    {
        var result = new Result { UsedMethod = "none", OccludedBy = "", MatchedTitle = "", Ambiguous = "" };
        method = (method ?? "auto").ToLowerInvariant();

        var hwnd = FindWindowByTitleSubstring(titleContains ?? "", processId,
                                               out var matchedTitle, out var ambiguity);
        result.MatchedTitle = matchedTitle;
        result.Ambiguous = ambiguity;
        if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return result;

        GetClientRect(hwnd, out var client);
        var w = client.Right - client.Left;
        var h = client.Bottom - client.Top;
        if (w <= 0 || h <= 0) return result;
        result.Width = w;
        result.Height = h;

        // 撮る前に被りを判定する。撮ってから気づいても送信は取り消せない。
        var occludedBy = FindOccluder(hwnd, w, h);
        result.Occluded = occludedBy != null;
        result.OccludedBy = occludedBy ?? "";

        byte[] pixels = null;
        var used = "none";

        if (method == "printwindow" || method == "auto")
        {
            pixels = CapturePrintWindow(hwnd, w, h);
            if (pixels != null)
            {
                used = "printwindow";
                // 黒一色ならPrintWindowが空振りしている(GPU直描画のウィンドウでよくある)。
                if (method == "auto" && NonBlackRatio(pixels) < 0.01)
                {
                    pixels = null;
                    used = "none";
                }
            }
        }

        if (pixels == null && method != "printwindow")
        {
            // デスクトップDCは画面の合成結果なので、被っていたら相手が写る。
            if (result.Occluded && !allowOccluded) return result;
            pixels = CaptureDesktopDc(hwnd, w, h);
            used = "desktopdc";
        }

        if (pixels == null) return result;

        result.NonBlack = NonBlackRatio(pixels);

        // 拡張子と中身は一致させる。中身がBMPなのに名前が .png だと、読む側は
        //    「壊れている」と誤診する。=> 拡張子で形式を決め、書いた先を呼び出し側へ返す。
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) WritePng24(outPath, pixels, w, h);
        else WriteBmp24(outPath, pixels, w, h);
        result.Saved = true;
        result.SavedPath = outPath;
        result.UsedMethod = used;
        return result;
    }

    // クライアント領域の代表点でWindowFromPointを引き、対象(またはその子孫)以外が返ったら被り。
    private static string FindOccluder(IntPtr hwnd, int w, int h)
    {
        for (var gy = 0; gy < 3; gy++)
        {
            for (var gx = 0; gx < 3; gx++)
            {
                var pt = new POINT { X = w * (gx * 2 + 1) / 6, Y = h * (gy * 2 + 1) / 6 };
                ClientToScreen(hwnd, ref pt);
                var at = WindowFromPoint(pt);
                if (at == IntPtr.Zero) return "(none)";
                if (at == hwnd || GetAncestor(at, GA_ROOT) == hwnd) continue;

                var root = GetAncestor(at, GA_ROOT);
                var len = GetWindowTextLength(root);
                var sb = new StringBuilder(len + 1);
                if (len > 0) GetWindowText(root, sb, sb.Capacity);
                return sb.Length > 0 ? sb.ToString() : "(untitled window)";
            }
        }
        return null;
    }

    // PrintWindowはウィンドウ全体を描くため、クライアント領域を切り出して返す。
    private static byte[] CapturePrintWindow(IntPtr hwnd, int clientW, int clientH)
    {
        GetWindowRect(hwnd, out var wr);
        var winW = wr.Right - wr.Left;
        var winH = wr.Bottom - wr.Top;
        if (winW <= 0 || winH <= 0) return null;

        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        var offsetX = origin.X - wr.Left;
        var offsetY = origin.Y - wr.Top;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmp = CreateCompatibleBitmap(screenDc, winW, winH);
        var old = SelectObject(memDc, bmp);

        var ok = PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
        byte[] result = null;
        if (ok)
        {
            var full = ReadDiBits(memDc, bmp, winW, winH);
            if (full != null) result = CropRgb(full, winW, winH, offsetX, offsetY, clientW, clientH);
        }

        SelectObject(memDc, old);
        DeleteObject(bmp);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        return result;
    }

    private static byte[] CaptureDesktopDc(IntPtr hwnd, int w, int h)
    {
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmp = CreateCompatibleBitmap(screenDc, w, h);
        var old = SelectObject(memDc, bmp);
        BitBlt(memDc, 0, 0, w, h, screenDc, origin.X, origin.Y, SRCCOPY);
        var result = ReadDiBits(memDc, bmp, w, h);
        SelectObject(memDc, old);
        DeleteObject(bmp);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        return result;
    }

    // 32bpp BGRAで読み出し、RGB(3バイト/px, top-down)へ詰め直す。
    private static byte[] ReadDiBits(IntPtr hdc, IntPtr hbmp, int w, int h)
    {
        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,       // 負でtop-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,   // BI_RGB
        };
        var raw = new byte[w * h * 4];
        var scanned = GetDIBits(hdc, hbmp, 0, (uint)h, raw, ref bi, DIB_RGB_COLORS);
        if (scanned == 0) return null;

        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3 + 0] = raw[i * 4 + 2]; // R
            rgb[i * 3 + 1] = raw[i * 4 + 1]; // G
            rgb[i * 3 + 2] = raw[i * 4 + 0]; // B
        }
        return rgb;
    }

    private static byte[] CropRgb(byte[] src, int srcW, int srcH, int x, int y, int w, int h)
    {
        if (x < 0 || y < 0 || x + w > srcW || y + h > srcH) return null;
        var dst = new byte[w * h * 3];
        for (var row = 0; row < h; row++)
            Array.Copy(src, ((y + row) * srcW + x) * 3, dst, row * w * 3, w * 3);
        return dst;
    }

    private static float NonBlackRatio(byte[] rgb)
    {
        var count = 0;
        var total = rgb.Length / 3;
        for (var i = 0; i < total; i++)
        {
            if (rgb[i * 3] > 8 || rgb[i * 3 + 1] > 8 || rgb[i * 3 + 2] > 8) count++;
        }
        return total == 0 ? 0f : (float)count / total;
    }


    // 24bit PNG を外部ライブラリなしで書く。BMPと違い、そのまま画像として読める形式なので
    // 検証結果を人にも道具にも渡しやすい(BMPは対応していない読み取り側がある)。
    // pixels は上から下へ並んだ RGB(1画素3バイト)。
    private static void WritePng24(string path, byte[] rgb, int width, int height)
    {
        // 走査線ごとに「フィルタ種別」を1バイト先頭へ置くのがPNGの規約。0 = フィルタなし。
        var raw = new byte[(width * 3 + 1) * height];
        var pos = 0;
        for (var y = 0; y < height; y++)
        {
            raw[pos++] = 0;
            Buffer.BlockCopy(rgb, y * width * 3, raw, pos, width * 3);
            pos += width * 3;
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78); ms.WriteByte(0x9C);   // zlibヘッダ(deflate/32KB窓)
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw, 0, raw.Length);
            var adler = Adler32(raw);
            ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
            compressed = ms.ToArray();
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;    // ビット深度
        ihdr[9] = 2;    // カラータイプ 2 = トゥルーカラー(RGB)
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", compressed);
        WriteChunk(fs, "IEND", new byte[0]);
    }

    private static void WriteBigEndian(byte[] buf, int offset, int value)
    {
        buf[offset + 0] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream fs, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, data.Length);
        fs.Write(len, 0, 4);

        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        fs.Write(typeBytes, 0, 4);
        fs.Write(data, 0, data.Length);

        // CRCは「チャンク種別 -> データ」の順に掛ける(長さは含めない)。
        // 順序を逆にすると、署名もサイズも妥当なのに読み込みだけが失敗する。
        var crc = Crc32(data, Crc32(typeBytes, 0xFFFFFFFFu, false), true);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        fs.Write(crcBytes, 0, 4);
    }

    private static uint[] s_crcTable;

    private static uint Crc32(byte[] data, uint seed, bool finish)
    {
        if (s_crcTable == null)
        {
            s_crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                s_crcTable[n] = c;
            }
        }
        var crc = seed;
        foreach (var b in data) crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return finish ? crc ^ 0xFFFFFFFFu : crc;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static void WriteBmp24(string path, byte[] rgb, int width, int height)
    {
        var rowSize = (width * 3 + 3) & ~3;
        var imageSize = rowSize * height;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(14 + 40 + imageSize);
        w.Write(0); w.Write(14 + 40);
        w.Write(40); w.Write(width); w.Write(height);
        w.Write((short)1); w.Write((short)24);
        w.Write(0); w.Write(imageSize);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);

        var row = new byte[rowSize];
        for (var y = height - 1; y >= 0; y--)
        {
            Array.Clear(row, 0, row.Length);
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 3;
                row[x * 3 + 0] = rgb[o + 2];
                row[x * 3 + 1] = rgb[o + 1];
                row[x * 3 + 2] = rgb[o + 0];
            }
            w.Write(row);
        }
    }

    /// <summary>
    /// タイトルの部分一致でウィンドウを探す。processId が 0 でなければ、そのプロセスのものに限る。
    /// </summary>
    /// <remarks>
    /// **複数一致したら選ばずに中止する。**
    ///   「1つ目を黙って選ぶ」と、意図と違う相手を撮っても気づけない。
    ///     画像は撮ってしまってからでは取り消せないので、迷う余地があるなら撮らない。
    ///   被り判定(FindOccluder)は「重なっていない」しか言わない。
    ///     **撮った相手が誰かは何も保証していない。**
    /// </remarks>
    private static IntPtr FindWindowByTitleSubstring(string substring, int processId,
                                                      out string matchedTitle, out string ambiguity)
    {
        var matches = new List<(IntPtr Hwnd, string Title, int Pid)>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (!title.Contains(substring, StringComparison.OrdinalIgnoreCase)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (processId != 0 && pid != processId) return true;

            matches.Add((hWnd, title, (int)pid));
            return true;
        }, IntPtr.Zero);

        matchedTitle = "";
        ambiguity = "";

        if (matches.Count == 0) return IntPtr.Zero;
        if (matches.Count > 1)
        {
            // どれを撮るか決められないので撮らない。一覧を返して呼び出し側が絞れるようにする。
            var titles = new List<string>();
            foreach (var m in matches) titles.Add($"pid {m.Pid}: {m.Title}");
            ambiguity = string.Join(" / ", titles);
            return IntPtr.Zero;
        }

        matchedTitle = matches[0].Title;
        return matches[0].Hwnd;
    }
}
