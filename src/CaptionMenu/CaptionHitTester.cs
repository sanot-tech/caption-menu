// CaptionHitTester.cs — «детектор заголовка». Единственная задача: понять, кликнул ли юзер по ПАНЕЛИ ЗАГОЛОВКА окна.
// Идея (как в KDE): мы глобально ловим правую кнопку, спрашиваем у окна «ты что под курсором?» (WM_NCHITTEST).
// Если ответ «заголовок» (HTCAPTION) — значит клик по тайтлу, и мы показываем СВОЁ меню, а не системное.
// Важно: WM_NCHITTEST шлём с таймаутом, иначе клик по «мёртвому» окну (у которого UI-поток завис) нас подвесит.
using System.Text; // StringBuilder для чтения класса окна без лишних аллокаций

namespace CaptionMenu; // То же пространство имён

/// <summary> Результат анализа клика: окно найдено, это заголовок — и всё нужное для меню.</summary>
internal readonly record struct HitInfo(IntPtr Hwnd, Pt Point, string ClassName, string Title)
{
    /// <summary> Удачный хит по тайтлу — можно открывать меню.</summary>
    public bool IsValid => Hwnd != IntPtr.Zero; // Окно найдено и прошло все проверки
}

internal static class CaptionHitTester
{
    /// <summary> Максимальная высота «шапки», которую мы считаем заголовком, в логических пикселях.
    /// Нужна как страховка: некоторые приложения (Electron, XAML) отвечают HTCAPTION на весь клиент,
    /// и без этой проверки наше меню всплывало бы при клике в середину окна.</summary>
    private const int MaxCaptionHeightLogical = 64;

    /// <summary> Классы окон оболочки Explorer: рабочий стол, панель задач, окна-предпросмотры (XAML, Aero).
    /// У них есть рамка, но «заголовок» = вся панель задач. Наше меню там только раздражало бы.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",          // Рабочий стол
        "WorkerW",          // Слой рабочего стола (иконки/виджеты)
        "Shell_TrayWnd",    // Панель задач
        "Shell_SecondaryTrayWnd", // Панель задач на втором мониторе
        "Windows.UI.Core.CoreWindow", // Системный UI: меню «Пуск», Action Center, календарь
        "Xaml_WindowedPopupClass",   // Всплывашки WinUI (тосты, центры уведомлений)
        "MultitaskingViewFrame",      // Снимок экрана (Win+Shift+S)
        "ForegroundStaging",          // Подложка «Снимка экрана»
        "TaskListThumbnailWnd",       // Превью окон таскбара
        "TaskListThumbnailWnd32",     // Превью окон таскбара (x86)
        "SysShadow",                  // Тень от окна (невидима, но с WS_CAPTION!)
    };

    /// <summary> Главный метод: был ли этот клик по заголовку верхнеуровневого окна?</summary>
    /// <param name="pt"> Экранные координаты клика.</param>
    /// <param name="selfPid"> Наш PID — окна самого тулза мы игнорируем (иначе меню вызовет само себя).</param>
    /// <param name="info"> Найденное окно (или IsValid=false).</param>
    public static bool TryHitTitleBar(Pt pt, uint selfPid, out HitInfo info)
    {
        info = default; // Начальное значение — «ничего не нашли», чтобы out всегда был валиден

        // ДИАГНОСТИКА. Каждый отказ логируем с причиной. Без этого «меню не появляется» — это одно
    // предложение на 6 разных багов. Лог включается только через CAPTIONMENU_DEBUG=1.
    // В проде этот метод — горячий путь (для каждого ПКМ в системе), поэтому все строки трассировки
    // стоят за if (trace). Проверка bool'а дешевле любой конкатенации строк.
    var trace = DebugLog.IsEnabled; // Логирование включено? (чтение поля bool — почти бесплатно)

    // 1 Окно под точкой. WindowFromPoint может вернуть дочерний контрол (кнопку, Edit) — поднимаем к корню.
        var child = Native.WindowFromPoint(pt);
        if (child == IntPtr.Zero) { if (trace) DebugLog.Write("  reject: WindowFromPoint=0"); return false; } // Пусто
        var hwnd = Native.GetAncestor(child, Native.GA_ROOT);
        if (hwnd == IntPtr.Zero) { if (trace) DebugLog.Write("  reject: GA_ROOT=0"); return false; } // Не поднялся

        // 2 Наше собственное окно/меню? Меню (класс #32768) имеет наш PID — иначе мы бы ловили сами себя.
        if (Native.GetWindowThreadProcessId(hwnd, out var pid) == 0) { if (trace) DebugLog.Write("  reject: no thread id"); return false; } // 
        if (pid == selfPid) { if (trace) DebugLog.Write("  reject: own pid"); return false; } // Стоп — это наше окно

        // 3 Окно должно быть видимым и НЕ свёрнутым: по свёрнутому окно тайтл не нарисовано.
        if (!Native.IsWindowVisible(hwnd)) { if (trace) DebugLog.Write("  reject: not visible"); return false; } // 
        if (Native.IsIconic(hwnd)) { if (trace) DebugLog.Write("  reject: minimized"); return false; } // Свёрнуто

        // 4 Отсекаем «призрачные» окна UWP/XAML (DWMWA_CLOAKED=1). Они невидимы, но NCHITTEST по ним отвечает.
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        { if (trace) DebugLog.Write("  reject: cloaked=" + cloaked); return false; } // Клоун-окно

        // 5 Плавающие утилиты (панельки, тултипы) не имеют тайтла в смысле Plasma — не трогаем.
        var exStyle = Native.GetWindowLongAuto(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) { if (trace) DebugLog.Write("  reject: TOOLWINDOW"); return false; } // 

        // 6 Окно должно иметь заголовок и системное меню — иначе правый клик по нему не про тайтл.
        var style = Native.GetWindowLongAuto(hwnd, Native.GWL_STYLE).ToInt64();
        if ((style & Native.WS_CAPTION) == 0) { if (trace) DebugLog.Write("  reject: no WS_CAPTION style=0x" + style.ToString("X")); return false; } // 
        if ((style & Native.WS_SYSMENU) == 0) { if (trace) DebugLog.Write("  reject: no WS_SYSMENU style=0x" + style.ToString("X")); return false; } // 

        // 6 Классы оболочки (рабочий стол, taskbar, теневые окна) — это не «окна приложений», не трогаем.
        var className = GetClassName(hwnd); // Узнаём класс один раз и переиспользуем дальше
        if (ShellClasses.Contains(className)) { if (trace) DebugLog.Write("  reject: shell class " + className); return false; } // Explorer

        // 7 СПРОС У ОКНА: что под курсором? С таймаутом 60 мс, чтобы не зависнуть на зомби-процессе.
        if (Native.SendMessageTimeout(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, Native.MakeLParam(pt.X, pt.Y),
                Native.SMTO_ABORTIFHUNG, 60, out var hitRaw) == IntPtr.Zero)
        { if (trace) DebugLog.Write("  reject: NCHITTEST timeout"); return false; } // Окно не ответило

        var hit = (int)hitRaw.ToInt64();
        var isCaption = hit is Native.HTCAPTION or Native.HTTOP or Native.HTTOPLEFT or Native.HTTOPRIGHT
            or Native.HTMINBUTTON or Native.HTMAXBUTTON or Native.HTCLOSE; // Заголовок ИЛИ его кнопки
        if (!isCaption) { if (trace) DebugLog.Write("  reject: hit=" + hit + " (not caption)"); return false; } // Клик по содержимому

        // 8 Страховка по геометрии: клик должен быть в верхней полосе окна. Обрезаем по DPI монитора.
        if (!Native.GetWindowRect(hwnd, out var rect)) { if (trace) DebugLog.Write("  reject: no rect"); return false; } // 
        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST); // Монитор окна
        var dpi = GetDpiForMonitorSafe(monitor); // Плотность пикселей (96 = 100%)
        var maxHeight = (int)(MaxCaptionHeightLogical * dpi / 96.0); // Пересчёт логических пикселей в физические
        var fromTop = pt.Y - rect.Top; // Насколько клик ниже верхней кромки окна
        if (fromTop > maxHeight)
        { if (trace) DebugLog.Write("  reject: too low, fromTop=" + fromTop + " > " + maxHeight); return false; } // 

        if (trace) DebugLog.Write("  HIT caption cls=" + className + " fromTop=" + fromTop + "/" + maxHeight + "dpi=" + dpi); // 
        info = new HitInfo(hwnd, pt, className, GetTitle(hwnd)); // Всё сошлось — отдаём результат
        return true; // Это тайтл!
    }

    /// <summary> Плотность пикселей монитора. GetDpiForMonitor лежит в shcore.dll и есть с Windows 8.1 —
    /// если библиотеки нет (редко), возвращаем 96, и тайтл считается 100% — безопасно.</summary>
    private static int GetDpiForMonitorSafe(IntPtr monitor)
    {
        try
        {
            var hr = ShcoreGetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var x, out _);
            return hr == 0 && x > 0 ? x : 96; // Есть ответ — берём; иначе дефолт
        }
        catch (DllNotFoundException) { return 96; } // Нет shcore.dll (e.g. Windows 7) — дефолт
        catch (EntryPointNotFoundException) { return 96; } // Нет функции — дефолт
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int ShcoreGetDpiForMonitor(IntPtr monitor, int type, out int dpiX, out int dpiY);

    /// <summary> Класс окна (Progman/WorkerW/#32768...). Нужен и для проверок, и для отладки в логе.</summary>
    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128); // Буфер на 128 символов — имена классов короче
        var len = Native.GetClassName(hwnd, sb, sb.Capacity); // Windows сама скопирует имя
        return len > 0 ? sb.ToString() : string.Empty; // Пусто пустая строка
    }

    /// <summary> Заголовок окна — покажем его первым пунктом меню (как «KWin» показывает имя окна).</summary>
    private static string GetTitle(IntPtr hwnd)
    {
        var len = Native.GetWindowTextLength(hwnd); // Сколько символов
        if (len <= 0) return string.Empty; // Окно без заголовка (диалоги, «безымянные»)
        var sb = new StringBuilder(len + 1); // Буфер +1 под '\0'
        Native.GetWindowText(hwnd, sb, sb.Capacity); // Копируем текст
        return sb.ToString(); // Готово
    }
}
