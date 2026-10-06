# 🎨 Icon Prompt — Windows 11 Fluent Style, app icon for CaptionMenu

> Copy the block below straight into Leonardo AI. 🔥
> Recommended model: **Leonardo Phoenix** or **Leonardo Diffusion XL / DreamShaper v8**,
> Alchemy enhancer ON, **Contrast/Detail high**, square aspect 1:1, 1024×1024.

---

## Prompt (EN — use this one) 🇬🇧

```
A modern Windows 11 Fluent Design style application icon for a window management utility.

Single centered symbol: a minimalist rounded-rectangle window outline drawn in clean white,
floating slightly above a thin horizontal line, with a small upward-pointing arrow
symbolizing "always on top" passing through the top edge of the window.

Design language: strict Microsoft Fluent Design, Segoe Fluent Icons aesthetic,
Windows 11 Mica acrylic material look, layered translucent depth, subtle soft shadow,
tint gradient background from deep blue #0F6CBD to teal #14B8A6, smooth and calm.

Icon must be legible at 16x16 pixels, silhouette readable, no text, no letters,
no window title bar decorations, no cartoon style, no 3D realism, no skeuomorphism.
Centered composition, generous padding around the symbol, square canvas, 1024x1024.
```

## Negative prompt 🚫

```
text, letters, words, watermark, signature, user interface, screenshot, multiple icons,
sketch, hand-drawn, 3d render, photorealistic, drop shadow heavy, gradient mesh noise,
cluttered, busy background, low contrast, blurry, jagged edges, off-center, macOS style,
Linux Tux mascot, rainbow colors
```

---

## Вариант 2 — квадратная (для трея, рекомендую) 🟦

```
Flat vector app icon, Windows 11 Fluent style, square app icon with rounded corners,
background gradient deep blue to teal. Centered: white minimalist window outline
with a bold upward arrow piercing through its top edge, meaning "pin window on top".
Fluent Design, Segoe UI geometry, crisp lines, equal stroke weight, minimal,
high contrast, legible at 16x16 px, centered, generous padding, 1024x1024.
```

## Вариант 3 — минималистичный (Fluent monochrome, как в Windows 11) ⬛

```
Monochrome Fluent Design app icon, single color pure white symbol on transparent
background: a simple window rectangle outline with an upward chevron arrow above its
top edge. Stroke 6% of canvas width, perfectly geometric, balanced, centered,
Windows 11 style, minimal vector, crisp, no gradients, no shadows, no text,
1024x1024, square canvas.
```

---

## Что должно получиться ✅

| Требование | Зачем |
|------------|-------|
| Окно + стрелка вверх | Окно = «работаем с окнами», стрелка = «поверх всех» |
| Читается в 16×16 | Иконка живёт в трее рядом с часами — там мелко |
| Fluent / Windows 11 | Тулз для Windows, должен выглядеть «своим» |
| Без текста | Текст на иконке нечитаем в трее и в проводнике |

## Как использовать результат 🛠️

1. Сгенерируйте в Leonardo → скачайте PNG 1024×1024. 🎨
2. Откройте **Image → resize → 256×256**, сохраните как `assets/icon-256.png`. 🖼️
3. Сконвертируйте в `.ico` (multi-size 16/32/48/64/128/256), положите в `src/CaptionMenu/`. 🧱
4. В `.csproj` добавьте строки (раскомментируйте в `ApplicationIcon`):

```xml
<ApplicationIcon>assets\CaptionMenu.ico</ApplicationIcon>
```

5. В `TrayIcon.cs` замените `SystemIcons.Application` на загруженную иконку:

```csharp
Icon = LoadAppIcon() // 🎨 своя иконка вместо системной
```

> 💡 Пока `.ico` не готов — тулз работает с системной иконкой приложения. Это нормально,
> функционально ничего не меняется.
