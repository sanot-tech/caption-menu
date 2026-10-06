# Icon prompt — Windows 11 Fluent style app icon for CaptionMenu

> Paste the block below straight into Leonardo AI.
> Recommended: **Leonardo Phoenix** or **DreamShaper v8 / Diffusion XL**,
> Alchemy enhancer ON, Contrast and Detail high, square aspect 1:1, 1024×1024.

---

## Prompt 1 — main icon (recommended)

```
A modern Windows 11 Fluent Design application icon for a window management utility.

Single centered symbol: a minimalist rounded-rectangle window outline drawn in clean white,
with a bold upward-pointing arrow piercing through its top edge, meaning "always on top".

Design language: strict Microsoft Fluent Design, Segoe Fluent Icons aesthetic,
Windows 11 Mica acrylic look, layered translucent depth, subtle soft shadow,
tint gradient background from deep blue #0F6CBD to teal #14B8A6.

Icon must stay legible at 16x16 pixels, readable silhouette, no text, no letters,
no title bar decorations, no cartoon style, no 3D realism, no skeuomorphism.
Centered composition, generous padding, square canvas, 1024x1024.
```

## Negative prompt

```
text, letters, words, watermark, signature, user interface, screenshot, multiple icons,
sketch, hand-drawn, photorealistic, heavy drop shadow, gradient mesh noise, cluttered,
busy background, low contrast, blurry, jagged edges, off-center, macOS style, rainbow colors
```

---

## Prompt 2 — flat vector, best for the tray

```
Flat vector app icon, Windows 11 Fluent style, square icon with rounded corners,
gradient background deep blue to teal. Centered: white minimalist window outline
with a bold upward arrow piercing through its top edge, meaning "pin window on top".
Fluent Design, Segoe UI geometry, crisp lines, equal stroke weight, minimal,
high contrast, legible at 16x16 px, centered, generous padding, 1024x1024.
```

## Prompt 3 — monochrome Fluent

```
Monochrome Fluent Design app icon, single pure white symbol on a transparent background:
a simple window rectangle outline with an upward chevron arrow above its top edge.
Stroke 6% of canvas width, perfectly geometric, balanced, centered, Windows 11 style,
minimal vector, crisp, no gradients, no shadows, no text, square canvas 1024x1024.
```

---

## What the result must satisfy

| Requirement | Why |
|-------------|-----|
| Window + upward arrow | Window = "we work with windows", arrow = "always on top" |
| Legible at 16×16 | It lives in the tray next to the clock |
| Fluent / Windows 11 | It's a Windows tool and should look native |
| No text | Text is unreadable in the tray and in Explorer |

## Wiring the result into the app

1. Generate in Leonardo and download the 1024×1024 PNG.
2. Resize to 256×256 and save as `src/CaptionMenu/assets/icon-256.png`.
3. Convert to a multi-size `.ico` (16/32/48/64/128/256) at `src/CaptionMenu/assets/CaptionMenu.ico`.
 PowerShell cannot do this on its own — any of these works:
 - an online converter (e.g. `icotool` via WSL, or a web tool),
 - ImageMagick: `magick icon-256.png -define icon:auto-resize=256,128,64,48,32,16 CaptionMenu.ico`,
 - a tiny C# snippet using `System.Drawing.Icon` (see below).
4. Uncomment `ApplicationIcon` in `src/CaptionMenu/CaptionMenu.csproj`:

```xml
<ApplicationIcon>assets\CaptionMenu.ico</ApplicationIcon>
```

5. That is all. `TrayIcon.LoadAppIcon()` already reads the icon embedded in the exe via
 `Icon.ExtractAssociatedIcon`, with a fallback to the system icon, so no code change is needed.

### Optional: build the .ico from PNGs with C#

```powershell
# Собирает многослойный .ico из набора PNG. Запускать из pwsh 7 с Windows Desktop SDK.
Add-Type -AssemblyName System.Drawing
$files = @('16','32','48','64','128','256') | ForEach-Object { "assets\icon-$_.png" } | Where-Object { Test-Path $_ }
if ($files.Count -eq 0) { throw 'no icon-*.png files found' }
$tmp = New-Item -ItemType Directory -Path (Join-Path $env:TEMP 'ico-build') -Force
$files | ForEach-Object {
    $img = [System.Drawing.Image]::FromFile((Resolve-Path $_))
    $out = Join-Path $tmp "ico-$([IO.Path]::GetFileNameWithoutExtension($_)).ico"
    $fs = [IO.File]::Create($out)
    $img.Save($fs, [System.Drawing.Imaging.ImageFormat]::Icon)   #  Один размер на файл
    $fs.Close(); $img.Dispose()
    $out
}
"Merge with an .ico tool: iconforge, icotool, or IcoLibrary"
```

> Until the `.ico` exists the tool simply uses the default application icon. Nothing breaks,
> this is cosmetic only. The tray icon code already handles a missing or broken icon gracefully.

## Palette

| Colour | Hex | Where |
|--------|-----|-------|
| Windows 11 accent blue | `#0F6CBD` | gradient start |
| Fluent teal | `#14B8A6` | gradient end |
| Pure white | `#FFFFFF` | the symbol |

These are the real Windows 11 accent colours, so the icon sits naturally next to system icons.
