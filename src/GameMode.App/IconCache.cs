using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameMode.App;

/// <summary>
/// 取 exe 的系统图标，带缓存。
///
/// 取图顺序固定两条：
/// 1. 先要 shell 的 jumbo（256px）列表，拿到 256px 原图后自己逐级缩到目标尺寸。
///    直接按目标尺寸向 shell 要图会拿到小图硬拉伸，发糊。
/// 2. shell 取不到（没登记进图标库、无 jumbo 列表、图标句柄为空）时，退到
///    ExtractAssociatedIcon 抽 exe 里的图标资源。这条路最高只有 32px，只当兜底。
/// 两路都失败就返回 null，调用方画首字母。
/// </summary>
internal sealed class IconCache
{
    /// <summary>jumbo 图标的边长。整条链路都以它作为缩放源，别再往下取小图。</summary>
    private const int JumboSize = 256;

    private const int ShgfiSysIconIndex = 0x000000100;
    private const int ShilJumbo = 4;   // 256px

    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    /// <summary>取源图（优先 jumbo 256px）。同一 exe 只取一次，缩放结果另行缓存。</summary>
    public Bitmap? GetLarge(string exePath)
    {
        string key = "big|" + exePath;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        Bitmap? bitmap = null;
        try
        {
            bitmap = LoadJumbo(exePath) ?? LoadFromExe(exePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"取图标失败 {exePath}: {ex.Message}");
        }

        _cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>按目标尺寸取缩放好的图。</summary>
    public Bitmap? Get(string exePath, int size)
    {
        string key = $"{exePath}|{size}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var source = GetLarge(exePath);
        if (source is null)
        {
            _cache[key] = null;
            return null;
        }

        var scaled = Resize(source, size);
        _cache[key] = scaled;
        return scaled;
    }

    /// <summary>从 shell 的 jumbo 列表取 256px 图标：SHGetFileInfo 拿索引，SHGetImageList 拿图。</summary>
    private static Bitmap? LoadJumbo(string exePath)
    {
        if (!File.Exists(exePath)) return null;

        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(exePath, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiSysIconIndex);
        if (result == IntPtr.Zero) return null;

        Guid iid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
        // HRESULT：0 才是 S_OK
        if (SHGetImageList(ShilJumbo, ref iid, out IntPtr pv) != 0 || pv == IntPtr.Zero) return null;

        var list = (IImageList)Marshal.GetObjectForIUnknown(pv);
        if (list.GetIcon(info.iIcon, 0x1, out IntPtr hIcon) != 0 || hIcon == IntPtr.Zero) return null;

        try
        {
            using var icon = Icon.FromHandle(hIcon);
            using var source = icon.ToBitmap();
            return Normalize(source);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>兜底：直接从 exe 抽图标资源。有些程序没登记进 shell 图标库，这条路只有 32px。</summary>
    private static Bitmap? LoadFromExe(string exePath)
    {
        if (!File.Exists(exePath)) return null;

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            return Normalize(bitmap);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 裁掉透明边并居中到正方形。
    /// 有些图标资源本身带大片留白，不裁的话在格子里"特别小"。
    /// </summary>
    private static Bitmap Normalize(Bitmap source)
    {
        var crop = FindOpaqueBounds(source) ?? new Rectangle(0, 0, source.Width, source.Height);

        int side = Math.Max(crop.Width, crop.Height);
        var result = new Bitmap(side, side, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source,
                new Rectangle((side - crop.Width) / 2, (side - crop.Height) / 2, crop.Width, crop.Height),
                crop, GraphicsUnit.Pixel);
        }

        return result;
    }

    /// <summary>找出非透明区域。整张全透明时返回 null。</summary>
    private static Rectangle? FindOpaqueBounds(Bitmap bitmap)
    {
        int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;

        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (buffer[row + x * 4 + 3] <= 8) continue;   // 透明像素跳过
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (maxX < 0) return null;
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>从 256px 源图逐级折半缩到目标尺寸。一次性大比例缩会糊。</summary>
    private static Bitmap Resize(Bitmap source, int size)
    {
        var current = source;
        bool owned = false;

        while (current.Width / 2 >= size && current.Width > size)
        {
            int next = current.Width / 2;
            var step = new Bitmap(next, next, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(step))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(current, new Rectangle(0, 0, next, next));
            }
            if (owned) current.Dispose();
            current = step;
            owned = true;
        }

        if (owned && current.Width == size) return current;

        var final = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(final))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(current, new Rectangle(0, 0, size, size));
        }

        if (owned) current.Dispose();
        return final;
    }
}
