// 🌐 NATIVE.cs — тонкий слой над Win32 API. Никакой логики, только P/Invoke и константы.
// 📚 Смысл: держать все «магические числа» в одном месте, чтобы остальной код читался как обычный C#.
using System.Runtime.InteropServices; // 🧱 InteropServices — чтобы объявить extern-методы и структуры Win32

namespace CaptionMenu; // 📦 Пространство имён проекта

/// <summary>📦 Точка на экране в пикселях (координаты мыши из хука).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Pt
{
    public int X; // 📐 X — пиксели от левого края экрана (виртуальные, т.е. с учётом второго монитора)
    public int Y; // 📐 Y — пиксели от верхнего края экрана
}

/// <summary>📦 Прямоугольник окна в экранных координатах.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;   // 📐 Левая граница
    public int Top;    // 📐 Верхняя граница
    public int Right;  // 📐 Правая граница
    public int Bottom; // 📐 Нижняя граница
}

/// <summary>📦 Данные события мыши из глобального хука WH_MOUSE_LL.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MsLlHookStruct
{
    public Pt pt;                  // 🖱️ Где был курсор в момент события (нас интересует только это)
    public uint mouseData;         // 🔢 Код кнопки/колеса — нам не нужен, но структура обязана быть полной
    public uint flags;             // 🚩 LLKHF_INJECTED и т.п. — не используем
    public uint time;              // ⏱️ Метка времени — не используем
    public IntPtr dwExtraInfo;     // 📦 Указатель на данные приложения — не используем
}

/// <summary>📦 Информация о мониторе (для центрирования окна по экрану).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int cbSize;   // 📏 Размер самой структуры — Windows требует заполнить это поле
    public Rect rcMonitor; // 🖥️ Вся область монитора (в т.ч. taskbar)
    public Rect rcWork;    // 📐 Рабочая область (без taskbar) — сюда центрируем окно
    public uint dwFlags;    // 🚩 PRIMARY = основной монитор
}

internal static class Native
{
    // ═══════════════════════════ 🪟 ОКНА ═══════════════════════════

    [DllImport("user32.dll")] // 🪟 Из пользовательской 32-битной библиотеки (подсистема GUI)
    public static extern IntPtr WindowFromPoint(Pt p); // 🎯 Какое окно под точкой (может вернуть дочерний контрол)

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags); // ⬆️ Поднять по дереву родителей до корня

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    [return: MarshalAs(UnmanagedType.Bool)] // 📦 MarshalAs — чтобы C#-bool стал Win32-BOOL (4 байта), а не 1 байт
    public static extern bool IsWindowVisible(IntPtr hwnd); // 👁️ Окно вообще видимо (не скрыто, не свёрнуто в трей)

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd); // 🔽 Окно свёрнуто (тогда в titlebar кликать бессмысленно)

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp,
        uint flags, uint timeoutMs, out IntPtr result); // 📨 Отправить сообщение с таймаутом (чтобы не висеть на «мёртвом» окне)

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect); // 📐 Где окно на экране

    // ═══════════════════════════ 📐 РАЗМЕРЫ И СТИЛИ ═══════════════════════════

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] // 🎯 Явно указываем W-вариант (юникод) — иначе на x64 попадём в GetWindowLong
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] // 🪟 32-битный вариант — нужен только чтобы код одинаково собирался под x86
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    /// <summary>🧠 Универсальный GetWindowLongPtr: сам выбирает 32/64-битную функцию по размеру указателя.
    ///         Стили окна нужны только НА ЧТЕНИЕ (проверяем WS_EX_TOPMOST, WS_CAPTION), поэтому
    ///         сеттера (SetWindowLong) в проекте нет — лишний P/Invoke = лишний шанс выстрелить в ногу. ✂️</summary>
    public static IntPtr GetWindowLongAuto(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    // ═══════════════════════════ 🖱️ ГЛОБАЛЬНЫЙ ХУК МЫШИ ═══════════════════════════

    /// <summary>🧠 Делегат низкоуровневого хука: вызывается на НАШЕМ потоке при каждом событии мыши в системе.</summary>
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] // 🪟 Подсистема GUI; SetLastError — чтобы читать код ошибки
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr hMod, uint threadId); // 🪝 Повесить хук

    [DllImport("user32.dll", SetLastError = true)] // 🪟 Подсистема GUI
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook); // ✂️ Снять хук при выходе

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam); // ⏭️ Передать событие дальше по цепочке

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] // 📨 Обычный SendMessage (без таймаута) — нужен для трюка с WM_NULL ниже
    public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    // ═══════════════════════════ 🍽️ МЕНЮ ═══════════════════════════

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu")] // 🍽️ Создать нативное меню — оно само умеет тёмную тему и DPI
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] // 🍽️ Добавить пункт (W = юникод)
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint flags, IntPtr id, string text);

    /// <summary>⭐ Отметить пункт как «по умолчанию» (жирный + Enter/двойной клик срабатывает по нему).</summary>
    [DllImport("user32.dll", EntryPoint = "SetMenuDefaultItem")] // 🏷️ Windows сама подсветит пункт как главный
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenuDefaultItem(IntPtr hMenu, int item, uint byPosition);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx")] // 📋 Показать меню и дождаться выбора; TPM_RETURNCMD = вернёт id выбранного пункта
    public static extern IntPtr TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DestroyMenu")] // 🗑️ Освободить меню — иначе течёт GDI-ресурс
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hwnd); // ⬆️ Поднять окно над остальными без активации

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] // 🎖️ Стать активным окном (нужно перед показом меню)
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")] // 👁️ Показать/скрыть окно
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)] // 📨 Отправить сообщение БЕЗ ожидания ответа
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    // ═══════════════════════════ 🏗️ ОКНА ═══════════════════════════

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] // 🏗️ Создать окно-приёмник сообщений
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid); // 🧵 Поток и PID окна (нужен PID, чтобы не ловить сами себя)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] // ⚙️ Ядро ОС: GetModuleHandle нужен для SetWindowsHookEx
    public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags); // 📦 Переместить/сменить Z-порядок

    [DllImport("user32.dll")] // 🪟 Подсистема GUI
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags); // 🖥️ Какой монитор у окна

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] // 🪟 Подсистема GUI
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info); // 📐 Параметры монитора

    [DllImport("dwmapi.dll")] // 💠 DWM — нужна, чтобы отсечь «призрачные» окна UWP/XAML (DWMWA_CLOAKED)
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attr, out int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] // 🪟 Подсистема GUI
    public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder buf, int max); // 🏷️ Имя класса окна (Progman, WorkerW — оболочка)

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] // 🪟 Подсистема GUI
    public static extern int GetWindowTextLength(IntPtr hwnd); // 📝 Длина заголовка окна

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] // 🪟 Подсистема GUI
    public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder buf, int max); // 📝 Заголовок окна

        // ═══════════════════════════ 🔢 КОНСТАНТЫ ═══════════════════════════

    public const int WH_MOUSE_LL = 14;                // 🪝 Низкоуровневый хук мыши (глобальный, все приложения)
    public const int HC_ACTION = 0;                    // 🎬 Событие дошло до обработчика (не «съедено» системой)
    public const uint WM_RBUTTONDOWN = 0x0204;         // 🖱️ Правая кнопка вниз — НАШ главный интерес
    public const uint WM_RBUTTONUP = 0x0205;           // 🖱️ Правая кнопка вверх — его пропускаем, иначе меню закроется криво
    public const uint WM_NCHITTEST = 0x0084;           // 🎯 «Что под курсором?» — так узнаём заголовок
    public const uint WM_SYSCOMMAND = 0x0112;          // 📟 Системные команды окна (свернуть/закрыть/восстановить)
    public const uint WM_NCDESTROY = 0x0082;           // 💀 Окно уничтожено — чистим хэндл
    public const uint WM_APP = 0x8000;                 // 📬 Наши собственные сообщения ( WM_APP + n )
    public const uint WM_NULL = 0x0000;                // 🕳️ Пустое сообщение — им выбиваем право на foreground (см. AllowForegroundFor)

    public const uint GA_ROOT = 2;                     // ⬆️ Корень дерева окон (верхнеуровневое окно)

    public const int HTCAPTION = 2;                    // 📝 Заголовок окна
    public const int HTMINBUTTON = 8;                  // 🔽 Кнопка «свернуть»
    public const int HTMAXBUTTON = 9;                  // 🔼 Кнопка «развернуть»
    public const int HTCLOSE = 20;                     // ❌ Кнопка «закрыть»
    public const int HTTOP = 12;                       // ⬆️ Верхняя кромка (зона перетаскивания)
    public const int HTTOPLEFT = 13;                   // ↖️ Угол сверху-слева
    public const int HTTOPRIGHT = 14;                  // ↗️ Угол сверху-справа

    public const int GWL_STYLE = -16;                  // 🎨 Индекс обычного стиля окна
    public const int GWL_EXSTYLE = -20;                // 🎨 Индекс расширенного стиля (там живёт WS_EX_TOPMOST)
    public const int WS_CAPTION = 0x00C00000;          // 🎨 Есть заголовок
    public const int WS_SYSMENU = 0x00080000;          // 🎨 Есть системное меню
    public const int WS_EX_TOOLWINDOW = 0x00000080;    // 🎨 Окно-утилита (плавающие панели — их не трогаем)
    public const int WS_EX_TOPMOST = 0x00000008;       // 🎨 «Поверх всех» — наш главный пункт меню

    public const uint MF_STRING = 0x00000000;          // 🍽️ Обычный текстовый пункт
    public const uint MF_SEPARATOR = 0x00000800;        // ➖ Разделительная линия
    public const uint MF_CHECKED = 0x00000008;         // ✔️ Галочка

    public const uint TPM_RETURNCMD = 0x0100;          // 📋 Вернуть id выбранного пункта, а не слать WM_COMMAND
    public const uint TPM_RIGHTBUTTON = 0x0002;        // 🖱️ Работать и по правой кнопке
    public const uint TPM_NONOTIFY = 0x0080;           // 🔕 Не слать уведомление о выборе

    public const uint MONITOR_DEFAULTTONEAREST = 2;    // 🖥️ Ближайший монитор к окну
    public const uint SMTO_ABORTIFHUNG = 0x0002;       // ⏱️ Не ждать ответа, если окно «висит»
    public const int DWMWA_CLOAKED = 14;               // 💠 Окно «одето в мантию» (UWP свёрнутые окна — невидимы для нас)

    // 🧠 Сендбоксы Z-порядка: специальные значения hWndInsertAfter. Это НЕ окна, а «инструкции системе».
    public static readonly IntPtr HWND_TOPMOST = new(-1);     // 📦 «Положить поверх всех»
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);   // 📦 «Вернуть в обычный Z-порядок» (снимет и чужой topmost)
    public static readonly IntPtr HWND_MESSAGE = new(-3);     // 📬 Спецродитель: окно-призрак без GUI

    public const uint SWP_NOSIZE = 0x0001;             // 📐 Не менять размер
    public const uint SWP_NOMOVE = 0x0002;             // 📐 Не менять позицию
    public const uint SWP_NOZORDER = 0x0004;           // 📐 Не менять Z-порядок (для «просто сдвинуть окно»)
    public const uint SWP_NOACTIVATE = 0x0010;         // 😴 Не активировать окно
    public const uint SWP_NOOWNERZORDER = 0x0200;      // 🙅 Не трогать Z-порядок владельцев

    public const int SW_SHOWNOACTIVATE = 4;            // 👻 Показать окно БЕЗ активации — для нашего 1×1 приёмника
    public const int SW_HIDE = 0;                      // 🙈 Скрыть окно (возвращаем после показа меню)

    public const uint SC_CLOSE = 0xF060;               // ❌ Системная команда «закрыть»
    public const uint SC_MINIMIZE = 0xF020;            // 🔽 «свернуть»
    public const uint SC_MAXIMIZE = 0xF030;            // 🔼 «развернуть»
    public const uint SC_RESTORE = 0xF120;             // ↩️ «восстановить»
    public const uint SC_MOVE = 0xF010;                // ↕️ «переместить» (стрелками с клавиатуры)
    public const uint SC_SIZE = 0xF000;                // ↔️ «изменить размер»

    // ═══════════════════════════ 🧰 МЕЛОЧИ ═══════════════════════════

    /// <summary>📦 Собрать LPARAM из координат: младшие 16 бит = X, старшие 16 бит = Y.</summary>
    public static IntPtr MakeLParam(int x, int y) => new((y << 16) | (x & 0xFFFF));

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] // 🎯 Кто сейчас активен — для диагностики меню
    public static extern IntPtr GetForegroundWindow();

    // ═════════════ 🧵 ATTACHTHREADINPUT — «лестница» к foreground ═════════════
    //
    // 🧠 ЗАЧЕМ (ключевой трюк проекта, найден 06.10.2026 в 21:25): SetForegroundWindow возвращает FALSE,
    //    потому что Windows жёстко ограничивает, кто может забрать foreground. Трюк с WM_NULL помогает
    //    не всегда (и точно не работает, когда кликнул по окну, а активным было другое).
    //    Надёжный путь — AttachThreadInput: мы временно «привязываем» потоки нашего окна и текущего
    //    foreground-окна, и внутри этой привязки SetForegroundWindow подчиняется нам.
    //    Это стандартный приём из ExplorerPatcher/WindowTop — не хак, а документированное поведение.

    [DllImport("user32.dll")] // 🪟 Какой UI-поток у окна
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);

    [DllImport("kernel32.dll")] // ⚙️ Поток нашего процесса
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)] // 🔗 Склеить/разсклеить потоки (TRUE = привязать)
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    /// <summary>🔼 Принудительно сделать наше окно foreground — «через лестницу» AttachThreadInput.</summary>
    public static bool ForceForeground(IntPtr ourWindow)
    {
        var fg = GetForegroundWindow();                    // 🎯 Кто активен сейчас
        var fgThread = fg == IntPtr.Zero ? 0u : GetWindowThreadProcessId(fg, IntPtr.Zero); // 🧵 Его поток
        var ourThread = GetCurrentThreadId();               // 🧵 Наш поток

        // 🛡️ Склеиваем потоки. Если fg == наше окно, склеивать не с чем — просто активируем.
        var attached = fgThread != 0 && fgThread != ourThread && AttachThreadInput(ourThread, fgThread, true);
        try
        {
            // 📦 BringWindowToTop ВАЖЕН не меньше SetForegroundWindow: без него окно может стать
            //    активным, но остаться ПОД меню в Z-порядке, и меню тут же закроется.
            BringWindowToTop(ourWindow);                    // ⬆️ Наверх
            var ok = SetForegroundWindow(ourWindow);        // ⬆️ Активным
            DebugLog.Write("ForceForeground: attached=" + attached + " ok=" + ok + " fg=" + GetForegroundWindow()); // 🩺
            return ok;
        }
        finally
        {
            // 🧹 Расклеиваем ВСЕГДА (даже если SetForegroundWindow бросил) — иначе потоки «слипнутся» навсегда.
            if (attached) AttachThreadInput(ourThread, fgThread, false); // 🔓 Отпускаем
        }
    }

    /// <summary>🎖️ ПОСЛЕДНИЙ ШТРИХ, без которого меню мигнёт и закроется.
    ///
    /// ⚠️ Проблема (проверено на этой машине, 06.10.2026): TrackPopupMenuEx показывает меню ТОЛЬКО если
    ///    foreground принадлежит вызывающему потоку. Но мы перехватили ПКМ у чужого окна — foreground не наш,
    ///    и меню открывалось на 1 кадр и исчезало (в логе стабильно «menu choice = None»).
    ///
    /// 🧠 Решение в два шага:
    ///    1) СИНХРОННЫЙ SendMessage(целевому окну, WM_NULL) — документированный трюк Windows: система
    ///       расценивает это как «процесс получил ввод от пользователя» и выдаёт право на foreground.
    ///       Именно SendMessage, а не PostMessage — иначе флаг не выставится.
    ///    2) SetForegroundWindow(НАШЕ окно-приёмник) — переводим foreground на себя. Только теперь
    ///       Windows согласится показать меню.
    ///
    /// 📌 Почему шаг 1 нужен при живом клике мыши: он работает и когда ввод синтезирован (тест),
    ///    и когда клик настоящий. Дёшево (пустое сообщение) и не ломает приложение.
    /// </summary>
    /// <param name="targetWindow">🪟 Окно приложения, по которому кликнули (для диагностики; право берём у foreground).</param>
    /// <param name="ourWindow">🪟 Наше скрытое окно — оно станет foreground-владельцем меню.</param>
    public static void AllowForegroundFor(IntPtr targetWindow, IntPtr ourWindow)
    {
        // 🩺 Диагностика: без неё невозможно понять, ПОЧЕМУ меню не держится. Снимаем foreground ДО и ПОСЛЕ.
        DebugLog.Write("fg before = " + GetForegroundWindow() + " (target=" + targetWindow + " our=" + ourWindow + ")"); // 🎯

        // 1️⃣ «Мы тут, дай права»: пустое СИНХРОННОЕ сообщение текущему foreground-окну.
        //    Это документированный трюк Windows (см. MSDN, SetForegroundWindow): система расценивает
        //    WM_NULL как «процесс получил ввод» и разрешает ему забрать foreground.
        //
        //    ⚠️ ВАЖНАЯ ТОНКОСТЬ (найдена экспериментально 06.10.2026, 21:24): WM_NULL надо слать
        //    ИМЕННО текущему foreground-окну, а НЕ тому, по которому кликнули. В нашем случае кликнули
        //    по окну Блокнота, но foreground принадлежал Chrome — и право не выдавалось, SetForegroundWindow
        //    возвращал False, а меню показывалось на 1 кадр и исчезало. Шлём в GetForegroundWindow(). 🎯
        var fg = GetForegroundWindow(); // 🎯 Кто активен ПРЯМО СЕЙЧАС
        if (fg != IntPtr.Zero) SendMessage(fg, WM_NULL, IntPtr.Zero, IntPtr.Zero); // 📨 Синхронно (важно!)

        // 2️⃣ Активируем НАШЕ окно принудительно (AttachThreadInput). Оно 1×1 за экраном (X=-32000)
        //    и WS_EX_TOOLWINDOW, поэтому визуально ничего не происходит — но для Windows это
        //    «полноценный» foreground-владелец меню, без которого меню живёт один кадр.
        ShowWindow(ourWindow, SW_SHOWNOACTIVATE);       // 👻 Показать без активации
        ForceForeground(ourWindow);                     // 🔼 Принудительно стать активным

        // 3️⃣ СРАЗУ прячем окно обратно. Пока оно «видно», оно может мигнуть в таскбаре; 1×1 за экраном
        //    не видно, но Windows всё равно считает его активированным — а это нам и нужно на время меню.
        //    Прячем ПОСЛЕ вызова меню (в Presenter), иначе окно перестанет быть валидным владельцем.
    }
}
