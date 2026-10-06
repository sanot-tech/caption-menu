// WindowOps.cs — ДЕЙСТВИЯ над окном. Всё, что пункт меню может сделать с окном, живёт здесь и только здесь.
// Ключевая идея (WS_EX_TOPMOST): Windows хранит «поверх всех» прямо в расширенном стиле окна.
// Значит переключение = добавить/убрать бит 0x8 и попросить систему пересобрать Z-порядок через SetWindowPos.
namespace CaptionMenu; // То же пространство имён

/// <summary> Идентификаторы пунктов меню. Значения выбраны с запасом (100+), чтобы не конфликтовать с системными.</summary>
internal enum MenuId : uint
{
    None = 0,          // Ничего не выбрано / меню закрыто Esc
    ToggleTopmost = 1001, // «Поверх всех окон» — ГЛАВНАЯ просьба юзера
    Move = 1002,          // Начать перемещение мышью (как в Plasma: окно едет за курсором)
    Size = 1003,          // Начать изменение размера мышью
    Minimize = 1004,      // Свернуть
    Maximize = 1005,      // Развернуть / Восстановить
    Center = 1006,        // Центрировать на экране (в рабочей области, без перекрытия таскбара)
    Close = 1007,         // Закрыть
}

internal static class WindowOps
{
    /// <summary> Сейчас окно «поверх всех»? Читаем бит из расширенного стиля — надёжнее, чем проверять поведение.</summary>
    public static bool IsTopmost(IntPtr hwnd) =>
        (Native.GetWindowLongAuto(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0; // Бит 0x8

    /// <summary> ПЕРЕКЛЮЧИТЬ «поверх всех». Возвращает новое состояние — чтобы трей/меню показали галочку.</summary>
    /// <param name="hwnd"> Окно.</param>
    /// <param name="notifyOthers"> Сообщить ли другим программам (потом пригодится для «поверх всех рабочих столов»).</param>
    public static bool ToggleTopmost(IntPtr hwnd, bool notifyOthers = true)
    {
        // 1 Читаем текущее состояние — переключаем, а не ставим наугад (юзер жмёт «ПКМ снять галочку»).
        var wasTopmost = IsTopmost(hwnd);

        // 2 Выбираем «передняя/обычная» позицию Z-порядка: HWND_NOTOPMOST снимает флаг ВСЕГДА,
        // даже если какая-то программа раньше поставила окно в topmost (WS_EX_TOPMOST сбросится).
        var after = wasTopmost ? Native.HWND_NOTOPMOST : Native.HWND_TOPMOST;

        // 3 SetWindowPos с флагами «не двигать, не менять размер, не активировать» — меняется ТОЛЬКО Z-порядок.
        // SWP_NOOWNERZORDER нужен, чтобы не переставить хозяина окна (диалоги и т.п. любят ломать порядок).
        var ok = Native.SetWindowPos(hwnd, after, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER); // Меняем только Z

        // 4 Контрольная проверка: HTOPMOST не всегда применяется (например, к UWP-окнам без прав).
        // Если система проигнорировала просьбу — честно вернём реальное состояние, а не выдуманное.
        var nowTopmost = IsTopmost(hwnd);
        if (notifyOthers && ok && nowTopmost != wasTopmost) BroadcastTopmostChanged(hwnd, nowTopmost); // Пусть другие знают
        return nowTopmost; // Итоговое состояние для обновления галочки
    }

    /// <summary> Тихо сообщаем «своим» (будущим фичам): тут будет переключение «поверх всех рабочих столов».</summary>
    private static void BroadcastTopmostChanged(IntPtr hwnd, bool topmost) =>
        DebugLog.Write($"topmost {(topmost ? "ON" : "OFF")} for {hwnd}"); // Пока только лог — точка расширения

    /// <summary> Центрировать окно по РАБОЧЕЙ области монитора (без панели задач) — как «Центрировать» в Plasma.</summary>
    public static void CenterOnScreen(IntPtr hwnd)
    {
        // 1 Какой монитор у окна? Если окно на втором мониторе — центрируем там, а не на основном.
        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST); // Ближайший монитор
        var mi = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() }; // cbSize обязателен
        if (!Native.GetMonitorInfo(monitor, ref mi)) return; // Не получили — выходим тихо

        // 2 Каких размеров окно сейчас? Свёрнутые окна дают мусорные координаты, поэтому сперва «восстановим».
        if (!Native.GetWindowRect(hwnd, out var rect)) return; // Не смогли — выходим
        var width = rect.Right - rect.Left;  // Ширина
        var height = rect.Bottom - rect.Top; // Высота

        // 3 Считаем левый-верхний угол: центр рабочей области минус половина окна.
        // work-центр: (Left+Right)/2. Отступы: center - width/2. Всё в целых — окно не «дрожит».
        var x = mi.rcWork.Left + ((mi.rcWork.Right - mi.rcWork.Left) - width) / 2;  // X центра
        var y = mi.rcWork.Top + ((mi.rcWork.Bottom - mi.rcWork.Top) - height) / 2;   // Y центра

        // 4 Двигаем без активации — юзер сначала «наводит» окно, а уже потом кликает. Так интуитивнее.
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE); // Только позиция
    }

    /// <summary> Запустить штатный режим перемещения (SC_MOVE + HTCAPTION). Windows дальше ведёт окно за мышью:
    /// в младших 16 битах wParam для SC_MOVE передаётся «зона захвата» — 2 = HTCAPTION (заголовок),
    /// поэтому рамка с курсором появляется в естественном месте, как при обычном перетаскивании. Не путать с WMSZ_*. </summary>
    public static void BeginMove(IntPtr hwnd)
    {
        Native.BringWindowToTop(hwnd); // Сначала наверх, чтобы окно было видно при перетаскивании
        Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MOVE | 2), IntPtr.Zero); // «Двигайся»
    }

    /// <summary> Запустить штатный режим изменения размера (SC_SIZE + WMSZ_TOPLEFT).
    /// Младшие 16 бит wParam для SC_SIZE = «на каком краю тянуть» (значения WMSZ_* из документации):
    /// 13 = WMSZ_TOPLEFT (верхний левый угол) — рамка ресайза рисуется на естественном углу окна. Не путать с HTCAPTION.</summary>
    public static void BeginSize(IntPtr hwnd)
    {
        Native.BringWindowToTop(hwnd); // Наверх
        Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_SIZE | 13), IntPtr.Zero); // «Растянись»
    }

    /// <summary> Свернуть. SC_MINIMIZE не активирует другие окна — в отличие от ShowWindow(SW_MINIMIZE) в некоторых случаях.</summary>
    public static void Minimize(IntPtr hwnd) =>
        Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MINIMIZE), IntPtr.Zero); // Команда

    /// <summary> Развернуть или восстановить — решаем по текущему состоянию (максимизировано или нет).</summary>
    public static void ToggleMaximize(IntPtr hwnd)
    {
        var cmd = IsZoomed(hwnd) ? Native.SC_RESTORE : Native.SC_MAXIMIZE; // Выбор команды по факту
        Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(cmd), IntPtr.Zero); // Отправляем
    }

    /// <summary> Окно развёрнуто на весь экран? WS,WM,ZC — наш стиль окна.</summary>
    public static bool IsZoomed(IntPtr hwnd) =>
        (Native.GetWindowLongAuto(hwnd, Native.GWL_STYLE).ToInt64() & WsMaximized) != 0; // WS_MAXIMIZE = 0x01000000

    /// <summary> Бит максимизации в обычном стиле окна.</summary>
    private const long WsMaximized = 0x01000000;

    /// <summary> Закрыть окно. Используем WM_SYSCOMMAND/SC_CLOSE — приложение само корректно спросит «сохранить?».</summary>
    public static void Close(IntPtr hwnd) =>
        Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_CLOSE), IntPtr.Zero); // Команда «закройся»
}
