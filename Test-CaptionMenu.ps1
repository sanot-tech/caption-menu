#  ═══════════════════════════════════════════════════════════════════════════
#  Test-CaptionMenu.ps1 — end-to-end test for the caption right-click interception.
#  ═══════════════════════════════════════════════════════════════════════════
#
#  WHY THIS TEST EXISTS. The whole risk of this tool is "the hook installs, but the menu never
#  appears / appears in the wrong place / the chosen item does nothing". You can eyeball that,
#  but you can only PROVE it by synthesizing a real right-click and checking the observable
#  effect (the WS_EX_TOPMOST bit of the target window).
#
#  WHAT IT ASSERTS (5 things):
#  1) CaptionMenu.exe starts and stays alive.
#  2) The global mouse hook is installed and RECEIVES the click.
#  3) The caption hit test ACCEPTS the title bar (log line "HIT caption").
#  4) The menu is REALLY shown, and "Always on Top" flips WS_EX_TOPMOST on the target.
#  5) Doing it again flips the bit BACK OFF — the toggle works in both directions.
#
#  RUN: pwsh -File .\Test-CaptionMenu.ps1 -> exit 0 = PASS, exit 1 = FAIL
#
#  NO Set-StrictMode HERE ON PURPOSE. Known PowerShell 7 bug: under
#  Set-StrictMode -Version Latest, types created by Add-Type stop resolving
#  ("type not found") even though compilation succeeded. Verified 06.10.2026. 

$ErrorActionPreference = 'Stop'  #  Any error in the test is a failure, not a silent "looks fine"
$script:Failures = 0              #  Failure counter (drives the exit code)
$script:Passes = 0                #  Success counter
$logPath = Join-Path $env:TEMP 'CaptionMenu.log' #  Where the tool writes its log

#  ═══════════════════════════════ Reporting helpers ═══════════════════════════════

function Step([string]$msg) { Write-Host "[..] $msg" -ForegroundColor Cyan }                     # Step
function Pass([string]$msg) { $script:Passes++; Write-Host "[ok] $msg" -ForegroundColor Green }   # Pass
function Fail([string]$msg) { $script:Failures++; Write-Host "[!!] $msg" -ForegroundColor Red }   # Fail
function Note([string]$msg) { Write-Host "[--] $msg" -ForegroundColor DarkGray }                # Info

function Get-Log {
    #  Whole log as one string, or '' when the tool has not written anything yet.
    if (Test-Path $logPath) { return (Get-Content $logPath -Raw -ErrorAction SilentlyContinue) }
    return ''
}

function Get-LogLength { if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 } } #  Размер лога

function Wait-Log {
    #  Why wait for log lines instead of sleeping a fixed 800 ms: fixed pauses proved flaky.
    #  Under load the menu appeared later than the test expected, so the follow-up click went
    #  into the void and the test reported a failure while the tool had actually worked.
    #  Waiting for a SPECIFIC event is deterministic. 
    #
    #  $FromOffset is mandatory, learned the hard way at 22:33: the SECOND cycle called
    #  Wait-Log 'menu choice = ToggleTopmost', but that line was ALREADY in the log from the
    #  FIRST cycle — so the wait returned instantly, the test read WS_EX_TOPMOST before the
    #  tool's SetWindowPos had run, and it declared a false failure. We now only look at
    #  log bytes written AFTER the mark. 
    param([string]$Pattern, [int]$FromOffset = -1, [int]$TimeoutMs = 6000)

    if ($FromOffset -lt 0) { $FromOffset = 0 } #  По умолчанию — весь лог
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs) #  Сдаться в это время
    while ((Get-Date) -lt $deadline) {                #  Опрашиваем
        if (Test-Path $logPath) {
            $len = (Get-Item $logPath).Length #  Текущий размер лога
            if ($len -gt $FromOffset) {              #  Лог вырос — читаем ТОЛЬКО новый хвост
                $fs = [System.IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite') #  Shared read: файл держит открытым тулз
                try {
                    $fs.Seek($FromOffset, 'Begin') | Out-Null          #  К метке
                    $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8) #  Читаем
                    $chunk = $sr.ReadToEnd()                          #  Хвост
                    $sr.Dispose()
                    if ($chunk -match $Pattern) { return $true }      #  Нашли в НОВОМ тексте
                } finally { $fs.Dispose() }                             #  Всегда освобождаем
            }
        }
        Start-Sleep -Milliseconds 60                                    #  Не грузим диск
    }
    return $false                                                       #  Таймаут
}

function Show-LogTail {
    #  On failure, the log tail is usually the whole answer — print it instead of guessing.
    $tail = (Get-Log) -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 8
    if ($tail) { Write-Host '   --- log tail ---' -ForegroundColor DarkGray; $tail | ForEach-Object { Write-Host "   $_" -ForegroundColor DarkGray } }
}

function Cleanup {
    #  Close the tool and the target so a failed run leaves nothing invisible behind.
    if ($tool) { Stop-Process -Id $tool.Id -ErrorAction SilentlyContinue }  #  Тулз
    Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue        #  Блокнот-мишень
}

#  ═══════════════════════════════ Win32 bridge for the test ═══════════════════════════════

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

//  / <summary> Window rectangle. Declared at top level (not nested) on purpose: PowerShell resolves
//  / type literals like [TestRect] only for PUBLIC types, never for internal ones. </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TestRect
{
    public int Left;   //  Left edge
    public int Top;    //  Top edge
    public int Right;  //  Right edge
    public int Bottom; //  Bottom edge
}

//  / <summary> Win32 bridge for the TEST (read-only queries + synthetic input).
//  / The class MUST be public or PowerShell will not find it by name.</summary>
public static class TestNative
{
    //  ─────────────── Window styles/flags we need ───────────────

    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000; //  Обычное окно С заголовком и системным меню
    private const int WS_EX_TOPMOST = 0x8;             //  «Поверх всех»
    private const int GWL_EXSTYLE = -20;               //  Индекс расширенного стиля
    private const int SW_SHOWNORMAL = 1;               //  Обычное состояние
    private const int SW_SHOW = 5;                     //  Показать
    private const int WM_NCHITTEST = 0x0084;           //  «Что под курсором?»
    private const int HTCAPTION = 2;                   //  Заголовок окна
    private const uint SMTO_ABORTIFHUNG = 0x0002;      //  Не ждать, если окно «висит»

    //  ─────────────── Imports ───────────────

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out TestRect r); //  Рамка окна
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); //  Курсор в точку
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags); //  Позиция/Z
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd); //  Показать/восстановить
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title); //  Найти по заголовку
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result); //  NCHITTEST с таймаутом
    [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyWindow(IntPtr h); //  Уничтожить окно
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr extra); //  Синтез мыши
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr h, int i); //  Стиль (x64)
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr h, int i); //  Стиль (x86)

    //  / <summary> Заголовок окна — для отчётов, когда окно найдено по классу, а не по имени.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h); // 
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max); // 

    //  ─────────────── Test window lifecycle ───────────────

    //  / <summary> Найти окно БЛОКНОТА — единственная надёжная мишень.
    //  /
    //  / ПОЧЕМУ НЕ СОЗДАЁМ СВОЁ ОКНО (выяснено экспериментально 06.10.2026, три попытки):
    //  / Ручной CreateWindowExW из PowerShell (Add-Type) стабильно роняет процесс с 0xC0000409 —
    //  / фастфайл при создании оконного класса. Проверено и на главном потоке, и на отдельном
    //  / STA-потоке с собственным message queue: падает в обоих случаях, причём ПОСЛЕ успешного
    //  / RegisterClassExW. Отдельная попытка через WinForms Form невозможна: сборка
    //  / System.Windows.Forms не подключается к Add-Type без явных ссылок, что тянет за собой
    //  / отдельную сборку под .NET 9. Вывод: не изобретать окно, а использовать Блокнот —
    //  / у него гарантированно есть настоящий WS_CAPTION | WS_SYSMENU и штатный WM_NCHITTEST. </summary>
    public static IntPtr FindNotepad()
    {
        //  Ищем по ЗАГОЛОВКУ, а не по PID: у notepad.exe в Windows 11 несколько окон (заставка,
        //  главное окно, окно «Параметры»), и MainWindowHandle иногда указывает не на то, что нужно.
        var h = FindWindowW(null, "Безымянный – Блокнот");   //  Типовое название русского Блокнота
        if (h == IntPtr.Zero) h = FindWindowW(null, "Untitled - Notepad"); //  Английский вариант
        if (h == IntPtr.Zero) h = FindWindowW(null, "New Text Document - Notepad"); //  С документом
        return h; //  Хэндл (0 = не нашли)
    }

    //  / <summary> Показать окно в любом состоянии (снять свёрнутость/развёрнутость) и поставить
    //  / в верхний Z-порядок на фиксированную позицию.</summary>
    public static void RestoreAndPlace(IntPtr h)
    {
        //  Порядок важен: одного SW_RESTORE мало — окно остаётся «восстанавливающимся», и
        //  SetWindowPos применяется ДО конца анимации, после чего позиция снова уезжает на -32000.
        //  Проверено: показываем -> восстанавливаем -> окончательно обычное -> ставим TOPMOST.
        ShowWindow(h, 5);                                            //  SW_SHOW
        ShowWindow(h, 9);                                            //  SW_RESTORE
        System.Threading.Thread.Sleep(200);                           //  Дать Windows закончить анимацию
        ShowWindow(h, 1);                                            //  SW_SHOWNORMAL
        SetWindowPos(h, new IntPtr(-1), 100, 100, 800, 500, 0x0040);  //  HWND_TOPMOST + SWP_SHOWWINDOW
    }


    //  / <summary> Окно уехало за экран (то есть свёрнуто)? Windows прячет свёрнутые окна на -32000.</summary>
    public static bool IsOffScreen(IntPtr h)
    {
        var r = new TestRect();                                       //  Рамка
        if (!GetWindowRect(h, out r)) return true;                    //  Не прочитали — считаем плохо
        return r.Right < 0 || r.Bottom < 0 || r.Left < -1000 || r.Top < -1000; // 
    }

    //  ─────────────── Queries ───────────────

    //  / <summary> Стиль окна: x64 -> GetWindowLongPtr, иначе -> GetWindowLong.</summary>
    private static int GetStyle(IntPtr h, int idx) =>
        IntPtr.Size == 8 ? (int)GetWindowLongPtr64(h, idx).ToInt64() : GetWindowLong32(h, idx);

    //  / <summary> Окно сейчас «поверх всех»? Читаем бит 0x8 из расширенного стиля.</summary>
    public static bool IsTopmost(IntPtr h) => (GetStyle(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0; // 

    //  / <summary> Найти точку ЗАГОЛОВКА: сканируем сверху вниз и спрашиваем у самого окна
    //  / WM_NCHITTEST до первого HTCAPTION.
    //  / Зачем: жёсткая координата «верх окна + 8 px» ломалась при перемещении/развёртывании
    //  / окна. Такой поиск находит настоящий тайтл при любом положении. Сканируем несколько
    //  / колонок по X, чтобы одна неудачная попытка не провалила весь тест.</summary>
    public static bool TryFindCaptionPoint(IntPtr h, out int px, out int py)
    {
        var r = new TestRect();                                       //  Рамка окна
        if (!GetWindowRect(h, out r)) { px = py = 0; return false; }   //  Без рамки не найти

        var xs = new[] { r.Left + 150, r.Left + 300, r.Left + 60 };   //  Три колонки-кандидата
        foreach (var x in xs)
        {
            if (x < r.Left || x > r.Right) continue;                  //  Колонка вне окна
            for (var dy = 1; dy <= 80; dy++)                           //  От верхней кромки вниз
            {
                var y = r.Top + dy;                                   //  Y
                var lp = new IntPtr((y << 16) | (x & 0xFFFF));         //  LPARAM: младшие 16 бит X, старшие Y
                if (SendMessageTimeout(h, WM_NCHITTEST, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 100, out var res) == IntPtr.Zero)
                    continue;                                           //  Не ответило — ниже
                if ((int)res.ToInt64() == HTCAPTION) { px = x; py = y; return true; } //  Нашли
            }
        }
        px = py = 0;
        return false;                                                 //  Заголовка нет
    }

    //  / <summary> Диагностика: что окно отвечает на WM_NCHITTEST в верхних точках.
    //  / Зачем: если заголовок не найден, нужно ПОНЯТЬ почему — окно без заголовка, не отвечает,
    //  / или мы сканируем не там. Без этого диагностика упирается в «попробуй ещё раз».</summary>
    public static string DumpHitTest(IntPtr h, int lines)
    {
        var r = new TestRect();                                       //  Рамка окна
        if (!GetWindowRect(h, out r)) return "GetWindowRect FAILED";   // 
        var sb = new System.Text.StringBuilder();                     //  Отчёт
        sb.Append("rect=(").Append(r.Left).Append(',').Append(r.Top).Append(")-(")
          .Append(r.Right).Append(',').Append(r.Bottom).Append(") ");
        for (var dy = 1; dy <= lines; dy++)                            //  Только верхние N точек
        {
            var x = r.Left + 150;                                     //  Та же колонка, что и в поиске
            var y = r.Top + dy;                                       //  Y
            var lp = new IntPtr((y << 16) | (x & 0xFFFF));            //  LPARAM
            if (SendMessageTimeout(h, WM_NCHITTEST, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 100, out var res) == IntPtr.Zero)
                { sb.Append("y").Append(dy).Append("=TIMEOUT "); continue; } // 
            sb.Append("y").Append(dy).Append('=').Append((int)res.ToInt64()).Append(' '); //  Код зоны
        }
        return sb.ToString();                                         //  Готово
    }

    //  ─────────────── Synthetic input ───────────────

    //  / <summary> Правый клик: MOVE=0x1, RIGHTDOWN=0x8, RIGHTUP=0x10.</summary>
    public static bool RightClick(int x, int y)
    {
        if (!SetCursorPos(x, y)) return false;                        //  Координаты вне экрана
        System.Threading.Thread.Sleep(100);                            //  Дать окну заметить курсор
        mouse_event(0x0008, 0, 0, 0, IntPtr.Zero);                    //  RIGHTDOWN
        System.Threading.Thread.Sleep(70);                             //  Пауза между down/up — как у человека
        mouse_event(0x0010, 0, 0, 0, IntPtr.Zero);                    //  RIGHTUP
        return true;
    }

    //  / <summary> Левый клик (выбор пункта меню). Меню выбирают МЫШЬЮ, поэтому тестируем так:
    //  / это честнее синтетического Enter и проверяет реальный пользовательский путь.</summary>
    public static bool LeftClick(int x, int y)
    {
        if (!SetCursorPos(x, y)) return false;                        //  Курсор не поставился
        System.Threading.Thread.Sleep(140);                           //  Дать меню заметить курсор (hover)
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);                    //  LEFTDOWN
        System.Threading.Thread.Sleep(70);                             //  Пауза
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);                    //  LEFTUP
        return true;
    }
}
'@

#  ═══════════════════════════════ Preconditions ═══════════════════════════════

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path #  Script folder
$exePath = Join-Path $scriptDir 'out\CaptionMenu.exe'       #  Expected single file
$tool = $null                                               #  No tool yet — Cleanup must survive this
$script:TargetHwnd = [IntPtr]::Zero                        #  Same for the target window

if (-not (Test-Path $exePath)) {
    Fail "CaptionMenu.exe not found at $exePath"
    Note 'Build it first: dotnet publish src\CaptionMenu\CaptionMenu.csproj -c Release -r win-x64 -o out'
    exit 1
}
Pass "Found $exePath"

#  ═══════════════════════════════ Create the target window ═══════════════════════════════

Step 'Opening Notepad as the test target…'
#  Убиваем прошлый экземпляр: он мог остаться СВЁРНУТЫМ (Windows прячет такие на -32000),
#  и новый запуск переиспользует уже «уехавшее» окно.
Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue #  Чистый старт
Start-Sleep -Milliseconds 400                                   #  Дать исчезнуть

$notepad = Start-Process notepad -PassThru                      #  Запускаем
Start-Sleep -Milliseconds 2000                                  #  Дать окну появиться

#  Ищем окно по заголовку, а НЕ берём MainWindowHandle: у notepad.exe в Windows 11 несколько
#  окон (заставка, главное, «Параметры»), и MainWindowHandle периодически указывал не на то.
$script:TargetHwnd = [TestNative]::FindNotepad()                 #  Ищем настоящее окно Блокнота
if ($script:TargetHwnd -eq [IntPtr]::Zero) {
    Fail 'Could not find the Notepad window'
    Note 'Open Notepad manually and check its exact title, then add it to FindNotepad().'
    Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue
    exit 1
}
Pass "Target window found: hwnd=$($script:TargetHwnd)"

#  Ставим в TOPMOST на фиксированную позицию: иначе окно окажется ПОД Chrome/Telegram,
#  клик уйдёт в чужой тайтл, и тест будет врать (в 20:45 так и вышло — попали в Chrome_WidgetWin_1).
$placed = $false
for ($attempt = 1; $attempt -le 3 -and -not $placed; $attempt++) {
    [TestNative]::RestoreAndPlace($script:TargetHwnd)                     #  Восстановить + TOPMOST + (100,100)
    Start-Sleep -Milliseconds 700                                         #  Дать Windows применить
    if ([TestNative]::IsOffScreen($script:TargetHwnd)) {                   #  Всё ещё уехало?
        Note "Attempt ${attempt}: window still off-screen, retrying…"     # [--] Честно говорим
        continue                                                          #  Ещё раз
    }
    $placed = $true                                                       #  Встало
}
if (-not $placed) { Fail 'Could not place the target window on screen'; Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue; exit 1 }
Pass 'Target window placed on screen at a known position'

#  ═══════════════════════════════ Start the tool ═══════════════════════════════

Step 'Starting CaptionMenu with logging…'
$env:CAPTIONMENU_DEBUG = '1'                            #  Log is opt-in via this env var
Remove-Item $logPath -ErrorAction SilentlyContinue      #  Fresh log — stale matches would lie
$tool = Start-Process -FilePath $exePath -PassThru       #  Launch
Start-Sleep -Seconds 3                                  #  Let the hook install and the tray appear

if ($tool.HasExited) {
    Fail "CaptionMenu exited immediately (code $($tool.ExitCode)). Run it manually and read the error dialog."
    Cleanup; exit 1
}
Pass "CaptionMenu is alive (pid=$($tool.Id))"

if ((Get-Log) -match 'hook started') { Pass 'Global mouse hook installed' }
else { Fail 'Hook was NOT installed — the tool cannot intercept anything'; Show-LogTail; Cleanup; exit 1 }

#  ═══════════════════════════════ Locate the caption ═══════════════════════════════

#  Re-confirm the position RIGHT NOW, right before the click. Observed 06.10.2026: the window
#  could slip to (-32000,-32000) between placement and the click (3 seconds of waiting for the
#  hook), and the test then reported a "caption point found at (-31850, -31999)" — nothing to
#  click on. Now we verify and re-place immediately before use. 
if ([TestNative]::IsOffScreen($script:TargetHwnd)) {
    Note 'Target window slipped off-screen, re-placing it right before the click…' # [--]
    [TestNative]::RestoreAndPlace($script:TargetHwnd)                                     #  Ставим обратно
    Start-Sleep -Milliseconds 400                                                        #  Дать примениться
}

$clickX = 0; $clickY = 0
if ([TestNative]::TryFindCaptionPoint($script:TargetHwnd, [ref]$clickX, [ref]$clickY)) {
    Pass "Caption point found: ($clickX, $clickY)"
} else {
    Fail "No caption found on window $($script:TargetHwnd) — test cannot run"
    Note ('NCHITTEST dump: ' + [TestNative]::DumpHitTest($script:TargetHwnd, 24)) #  Что ответило окно
    Cleanup; exit 1
}

#  Stop condition: the found point must be on screen, otherwise there is nothing to click.
if ($clickX -lt 0 -or $clickY -lt 0) {
    Fail "Caption point ($clickX, $clickY) is off-screen — the window is minimized. Test aborted."
    Cleanup; exit 1
}

#  Menu item geometry, tuned on Windows 10 at 96 DPI (≈32 px per item).
#  It used to be +18 px in Y, which landed on the SECOND item ("Move") and made the test fail
#  even though the tool was perfectly correct. Now we aim at the top edge of the first item.
$itemX = $clickX + 60   #  Just right of the click point (inside the menu frame)
$itemY = $clickY + 14   #  FIRST item ("Always on Top")

#  ═══════════════════════════════ Cycle 1: right-click -> menu -> pick the item ═══════════════════════════════

$wasTopmost = [TestNative]::IsTopmost($script:TargetHwnd) #  Состояние ДО действия
Note "topmost before = $wasTopmost"

#  Метка в логе: дальше ждем ТОЛЬКО новых строк, иначе второй цикл найдёт строки первого.
$mark1 = Get-LogLength #  Отсюда читаем новьё

Step 'Right-clicking the title bar…'
if (-not [TestNative]::RightClick($clickX, $clickY)) { Fail 'Could not move the cursor to the caption'; Show-LogTail; Cleanup; exit 1 }

#  Assertions 1–2: the hook got the click AND the hit test accepted the title bar.
if (Wait-Log 'HIT caption' -FromOffset $mark1) { Pass 'Hook received the click and the hit test accepted the title bar' }
else { Fail 'No "HIT caption" in the log — hook missed it, or the hit test rejected the window'; Show-LogTail; Cleanup; exit 1 }

#  Assertion 3: the menu is really shown. The tool logs BEFORE the blocking TrackPopupMenuEx,
#  so the line appears while the menu is still open — that is what makes it a valid probe.
if (Wait-Log 'menu shown' -FromOffset $mark1) { Pass 'Menu is shown' }
else { Fail 'No "menu shown" in the log — the menu never appeared'; Show-LogTail }

Step 'Clicking the first item ("Always on Top")…'
if (-not [TestNative]::LeftClick($itemX, $itemY)) {
    Fail "Could not move the cursor onto the menu item ($itemX, $itemY) — off-screen coordinates?"
    Show-LogTail; Cleanup; exit 1
}

if (Wait-Log 'menu choice = ToggleTopmost' -FromOffset $mark1) { Pass 'The topmost item was the one selected (not a neighbour)' }
else {
    $seen = ([regex]::Matches((Get-Log), 'menu choice = (\w+)') | ForEach-Object { $_.Groups[1].Value })
    Fail "Menu item was not ToggleTopmost — the test likely missed the item. Log says: $($seen -join ', ')"
}

#  Ждём подтверждения от САМОГО тулза («topmost ON/OFF for …»), а не читаем стиль сразу:
#  TrackPopupMenuEx возвращается чуть раньше, чем SetWindowPos долетает до окна. Гонка была бы
#  гарантированным «провалом» на быстрых машинах.
if (-not (Wait-Log 'topmost (ON|OFF) for' -FromOffset $mark1)) { Note 'No "topmost … for" line yet — checking the style bit directly' }

$isTopmostNow = [TestNative]::IsTopmost($script:TargetHwnd) #  Читаем бит обратно из стиля окна
if ($isTopmostNow -ne $wasTopmost) {
    Pass "topmost actually changed: $wasTopmost -> $isTopmostNow (the window moved in Z-order)"
} else {
    Fail "topmost did NOT change (was $wasTopmost, still $isTopmostNow) — SetWindowPos had no effect"
    Show-LogTail
}

#  ═══════════════════════════════ Cycle 2: toggling back MUST clear the flag ═══════════════════════════════
#  The request was "enable AND disable, like Plasma does" — so BOTH directions have to work.

#  Новая метка: дальше ждём только то, что появится ПОСЛЕ первого цикла.
$mark2 = Get-LogLength #  Отсюда читаем новьё

Step 'Second cycle: right-click again and toggle it back…'
if (-not [TestNative]::RightClick($clickX, $clickY)) { Fail 'Could not repeat the right-click' }
if (Wait-Log 'menu shown' -FromOffset $mark2) { Step 'Menu reopened' } else { Fail 'Menu did not reopen on the second right-click' }

if (-not [TestNative]::LeftClick($itemX, $itemY)) { Fail 'Could not click the item on the second round' }
if (Wait-Log 'menu choice = ToggleTopmost' -FromOffset $mark2) { Step 'Second selection also hit the topmost item' }

#  Снова ждём подтверждения от тулза, а не читаем стиль мгновенно — та же гонка, что и в цикле 1.
if (-not (Wait-Log 'topmost (ON|OFF) for' -FromOffset $mark2)) { Note 'No "topmost … for" line yet — checking the style bit directly' }

$isTopmostAfter = [TestNative]::IsTopmost($script:TargetHwnd) #  Check again
if ($isTopmostAfter -eq $wasTopmost) {
    Pass "Second click restored the original state ($wasTopmost) — the toggle works both ways"
} else {
    Fail "Second click did NOT restore the state (now $isTopmostAfter, expected $wasTopmost)"
    Show-LogTail
}

#  ═══════════════════════════════ Cleanup ═══════════════════════════════

$toolPid = $tool.Id
Cleanup #  Закрыть тулз и мишень
Note "CaptionMenu (pid $toolPid) and Notepad closed."

#  ═══════════════════════════════ Ручная проверка видимости меню ═══════════════════════════════
#  Автотест доказывает, что бит WS_EX_TOPMOST переключается, но НЕ проверяет, что меню
#  выглядит как меню (тексты, галочка, порядок пунктов). Один раз откроем его и снимем экран.

if ($env:CAPTIONMENU_SHOW_MENU -eq '1') {
    Step 'Opening the menu for a manual eyeball check (CAPTIONMENU_SHOW_MENU=1)…'
    Write-Host '    The menu is now open. Screenshot it, check the texts and the check mark.' -ForegroundColor Yellow
    [TestNative]::RightClick($clickX, $clickY)   #  Открываем меню
    Start-Sleep -Seconds 12                       #  Держим открытым, чтобы успеть снять скриншот
    [void][TestNative]::LeftClick($clickX, $clickY) #  Закрываем кликом мимо
}

#  ═══════════════════════════════ Verdict ═══════════════════════════════

Write-Host ''
Write-Host "Passed: $script:Passes   Failed: $script:Failures" -ForegroundColor DarkGray #  Counts
if ($script:Failures -gt 0) {
    Write-Host "TEST FAILED — full log: $logPath" -ForegroundColor Red # 
    exit 1
}
Write-Host 'TEST PASSED: interception, menu and the both-way topmost toggle all work.' -ForegroundColor Green
exit 0
