// CaptionMenuPresenter.cs — показ нашего меню на месте клика и обработка выбора пункта.
// Почему нативное меню (CreatePopupMenu), а не WinForms ContextMenuStrip:
// 1) Оно УЖЕ умеет тёмную тему Windows 11 и High-DPI без наших танцев.
// 2) Оно наследует системные шрифты/размеры — выглядит «родным», как меню Plasma.
// 3) Оно не мигает и не требует STA/диспетчера сообщений, поэтому его безопасно звать из хука.
namespace CaptionMenu; // То же пространство имён

internal static class CaptionMenuPresenter
{
    /// <summary> Точка входа: показать меню и выполнить выбранное действие.</summary>
    /// <param name="owner"> Окно-приёмник НАШЕГО процесса: оно станет владельцем меню и foreground.</param>
    /// <param name="target"> Окно приложения, по тайтлу которого кликнули (его и меняем).</param>
    /// <param name="screenPoint"> Где показать меню (в пикселях от левого верхнего угла экрана).</param>
    public static void ShowAndExecute(IntPtr owner, IntPtr target, Pt screenPoint)
    {
        // ШАГ 0 (ОБЯЗАТЕЛЬНЫЙ, иначе меню живёт 1 кадр и исчезает):
        // TrackPopupMenuEx показывает меню ТОЛЬКО если foreground принадлежит вызывающему потоку.
        // Мы перехватили ПКМ у чужого окна, значит foreground не наш. AllowForegroundFor делает
        // два шага: WM_NULL текущему foreground-окну (система считает, что «процесс получил ввод»)
        // и принудительный SetForegroundWindow нашего окна через AttachThreadInput.
        Native.AllowForegroundFor(target, owner); // Забираем foreground себе

        // 1 Строим HMENU. При любой ошибке (null) — просто выходим, окно не пострадает.
        var menu = Native.CreatePopupMenu(); // Создаём
        if (menu == IntPtr.Zero)
        {
            // Откатываем foreground: меню не покажется, а фокус не должен остаться на невидимом окне.
            Native.ShowWindow(owner, Native.SW_HIDE);      // Прячем приёмник
            Native.SetForegroundWindow(target);             // Возвращаем фокус окну пользователя
            return;                                        // Мягкий выход
        }

        try
        {
            // 2 Наполняем. Галочку ставим по РЕАЛЬНОМУ состоянию окна (окно могло поменяться с прошлого раза).
            // Menu labels are in ENGLISH on purpose: the project is public on GitHub, English reads
            // for everyone, and it matches KWin/Plasma. Russian stays in the code comments.
            AppendChecked(menu, (uint)MenuId.ToggleTopmost, "Always on Top", WindowOps.IsTopmost(target)); // Главный пункт
            AppendSeparator(menu); // ---

            Append(menu, (uint)MenuId.Move, "Move");                  // Окно поедет за мышью
            Append(menu, (uint)MenuId.Size, "Resize");                // Рамка ресайза
            Append(menu, (uint)MenuId.Center, "Center on Screen");    // По центру рабочей области
            AppendSeparator(menu); // ---

            Append(menu, (uint)MenuId.Minimize, "Minimize");          // 
            Append(menu, (uint)MenuId.Maximize, WindowOps.IsZoomed(target) ? "Restore" : "Maximize"); // /
            Append(menu, (uint)MenuId.Close, "Close");                // 

            // 3 «Поверх всех окон» — пункт по умолчанию: жирный, и Enter на всём окне включает его.
            // Это ровно то поведение, которое юзер описал: «жму по заголовку и делаю поверх других».
            Native.SetMenuDefaultItem(menu, (int)MenuId.ToggleTopmost, 0u); // byPosition=0 пункт задаётся по ID

            // ВАЖНО: логируем ДО вызова TrackPopupMenuEx, а не после.
            // Раньше строка «menu shown» писалась ПОСЛЕ — но вызов блокирует поток до выбора пункта,
            // поэтому в логе не появлялось ничего, пока меню открыто. Диагностика была бесполезной:
            // нельзя было отличить «меню не показалось» от «меню показалось и висит». Теперь — сначала факт.
            DebugLog.Write("menu shown at " + screenPoint.X + "," + screenPoint.Y + " for hwnd=" + target); // 

            // 4 Показываем меню. TPM_RETURNCMD = вернуть id выбранного пункта (без WM_COMMAND — нам проще).
            // Координаты = точка клика, поэтому меню всплывает ровно под курсором, как в Plasma.
            var cmd = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY,
                screenPoint.X, screenPoint.Y, owner, IntPtr.Zero); // Блокирует поток, пока меню открыто; owner = наше окно

            DebugLog.Write("menu choice = " + (MenuId)cmd); // Что выбрали (None = Esc/мимо)

            // 5 Юзер выбрал пункт — выполняем. MenuId.None = Esc/клик мимо просто ничего.
            Execute((MenuId)cmd, target); // Действие
        }
        finally
        {
            // Прячем окно-приёмник обратно. Пока оно активировано, Windows не даст юзеру спокойно
            // работать с другими окнами: фокус «застрянет» на невидимом окне тулза. Возвращаем фокус.
            Native.ShowWindow(owner, Native.SW_HIDE); // Окно снова невидимо
            Native.SetForegroundWindow(target);       // Возвращаем foreground окну, по которому кликнули

            Native.DestroyMenu(menu); // ВСЕГДА освобождаем HMENU — иначе утечка GDI-ресурсов
        }
    }

    /// <summary> Выполнить действие по id. Здесь будущая точка роста: «поверх всех рабочих столов», «всегда на видимом» и т.п.</summary>
    private static void Execute(MenuId id, IntPtr hwnd)
    {
        switch (id)
        {
            case MenuId.ToggleTopmost:
                // ГЛАВНОЕ: переключаем и СРАЗУ обновляем галочку — но меню уже закрыто,
                // поэтому «отклик» пользователь увидит сразу на самом окне (оно поднимется над всеми).
                WindowOps.ToggleTopmost(hwnd); // WS_EX_TOPMOST вкл/выкл
                break; // Выходим

            case MenuId.Move: WindowOps.BeginMove(hwnd); break;             // 
            case MenuId.Size: WindowOps.BeginSize(hwnd); break;             // 
            case MenuId.Center: WindowOps.CenterOnScreen(hwnd); break;       // 
            case MenuId.Minimize: WindowOps.Minimize(hwnd); break;          // 
            case MenuId.Maximize: WindowOps.ToggleMaximize(hwnd); break;     // 
            case MenuId.Close: WindowOps.Close(hwnd); break;                 // 

            default: break; // Esc/мимо/закрыто — ничего не делаем (важно: не «падаем» в default с ошибкой)
        }
    }

    /// <summary> Пункт с текстом.</summary>
    private static void Append(IntPtr menu, uint id, string text) =>
        Native.AppendMenu(menu, Native.MF_STRING, new IntPtr(id), text); // Добавить в меню

    /// <summary> Пункт с галочкой (checked) — состояние читается из окна каждый раз заново.</summary>
    private static void AppendChecked(IntPtr menu, uint id, string text, bool isChecked) =>
        Native.AppendMenu(menu, Native.MF_STRING | (isChecked ? Native.MF_CHECKED : 0u), new IntPtr(id), text); // 

    /// <summary> Разделитель.</summary>
    private static void AppendSeparator(IntPtr menu) =>
        Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, string.Empty); // ---
}
