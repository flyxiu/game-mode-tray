# 从 design/icon.svg 生成 app/game-mode.ico（9 个尺寸，含 256）
# 必须用 Windows PowerShell 5.1 跑：pwsh 7 的 Add-Type 里 System.Drawing 要额外引用
#   powershell.exe -ExecutionPolicy Bypass -File tools\make-icon.ps1
#
# 做法：浏览器只渲染一张 512 大图（Edge 无头模式对小于约 200px 的视口不渲染），
# 其余尺寸靠 GDI+ 逐级折半缩小，避免一次性大比例缩放产生的锯齿。
param(
    [string]$EdgePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$proj  = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$svgBig   = Join-Path $proj 'design\icon.svg'
$svgSmall = Join-Path $proj 'design\icon-small.svg'
$ico   = Join-Path $proj 'app\game-mode.ico'
$tmp   = Join-Path $env:TEMP 'gm-icon'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$base  = 512

if (-not (Test-Path $svgBig))   { throw "找不到图标源：$svgBig" }
if (-not (Test-Path $svgSmall)) { throw "找不到小尺寸图标源：$svgSmall" }
if (-not (Test-Path $EdgePath)) { throw "找不到 Edge：$EdgePath" }
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null

# 把 SVG 的固定宽高改成 100%，让浏览器按视口缩放
function Render-Base {
    param([string]$svgPath, [string]$tag)

    $markup = Get-Content -LiteralPath $svgPath -Raw
    $markup = $markup -replace '<svg([^>]*?)\s+width="\d+"\s+height="\d+"', '<svg$1 width="100%" height="100%"'
    $page = @"
<!doctype html><meta charset="utf-8">
<style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block;width:100vw;height:100vh}</style>
$markup
"@
    $pagePath = Join-Path $tmp "render-$tag.html"
    [IO.File]::WriteAllText($pagePath, $page, (New-Object Text.UTF8Encoding($false)))
    $pageUrl = 'file:///' + ($pagePath -replace '\\', '/')

    $png = Join-Path $tmp "base-$tag.png"
    & $EdgePath --headless=new --disable-gpu --hide-scrollbars `
        --default-background-color=00000000 --virtual-time-budget=1200 `
        --window-size="$base,$base" --screenshot="$png" $pageUrl | Out-Null
    if (-not (Test-Path $png)) { throw "渲染失败：$tag" }
    Write-Host ("  基准图 {0} {1}px，{2} 字节" -f $tag, $base, (Get-Item $png).Length)
    return $png
}

$baseBig   = Render-Base -svgPath $svgBig   -tag 'big'
$baseSmall = Render-Base -svgPath $svgSmall -tag 'small'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IcoBuilder
{
    public static void Build(string outPath, string baseBig, string baseSmall, int[] sizes, int smallMax)
    {
        var blobs = new List<byte[]>();
        using (var big = new Bitmap(baseBig))
        using (var small = new Bitmap(baseSmall))
        {
            foreach (int s in sizes)
            {
                using (var bmp = Resize(s <= smallMax ? small : big, s))
                    blobs.Add(s >= 256 ? ToPng(bmp) : ToDib(bmp, s));
            }
        }

        using (var fs = new FileStream(outPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)sizes.Length);

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                bw.Write(dim); bw.Write(dim);
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(blobs[i].Length);
                bw.Write(offset);
                offset += blobs[i].Length;
            }
            foreach (var b in blobs) bw.Write(b);
        }
    }

    // 逐级折半到接近目标，最后一步再双三次缩到精确尺寸
    static Bitmap Resize(Bitmap src, int target)
    {
        Bitmap cur = src;
        bool owned = false;
        while (cur.Width / 2 >= target && cur.Width > target)
        {
            int n = cur.Width / 2;
            var next = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(next))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(cur, new Rectangle(0, 0, n, n));
            }
            if (owned) cur.Dispose();
            cur = next; owned = true;
        }
        if (cur.Width == target && owned) return cur;

        var final = new Bitmap(target, target, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(final))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(cur, new Rectangle(0, 0, target, target));
        }
        if (owned) cur.Dispose();
        return final;
    }

    static byte[] ToPng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // 32bpp DIB：BITMAPINFOHEADER + 自下而上的 BGRA + 全零 AND 掩码
    static byte[] ToDib(Bitmap bmp, int s)
    {
        var rect = new Rectangle(0, 0, s, s);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * s];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);

        int maskRow = ((s + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40);
            bw.Write(s);
            bw.Write(s * 2);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = s - 1; y >= 0; y--) bw.Write(px, y * data.Stride, s * 4);
            bw.Write(new byte[maskRow * s]);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
'@ -ReferencedAssemblies System.Drawing

$outDir = Split-Path -Parent $ico
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[IcoBuilder]::Build($ico, $baseBig, $baseSmall, $sizes, 24)

$f = Get-Item $ico
Write-Host ("已生成 {0}  {1} 字节" -f $f.FullName, $f.Length)
