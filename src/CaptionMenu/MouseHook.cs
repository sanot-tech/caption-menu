// MouseHook.cs — ГЛОБАЛЬНЫЙ ХУК МЫШИ (WH_MOUSE_LL). Это «мозг» тулза и его главный риск.
// Зачем хук, а не «у каждой программы своё меню»: в Windows нет глобального хука правого клика по тайтлу.
// Низкоуровневый хук ловит ВСЕ события мыши в системе, и это единственный способ сделать «жми по заголовку меню».
// Правила выживания низкоуровневого хука (нарушишь — Windows отключит хук тихо, тулз «умерт»):
// 1) Возвращаться НАДО БЫСТРО (< 1 мс). Мы НЕ ВЫЗЫВАЕМ TrackPopupMenu из хука, а просим главный поток
// показать меню через PostMessage. Иначе меню съест watchdog Windows (LowLevelHooksTimeout).
// 2) Хук живёт только пока крутится цикл сообщений, и делегат (Proc) обязан быть в поле — иначе GC его съест.
// 3) wParam/lParam — НЕ указатели в обычном смысле: это упакованные значения, читаем их через Marshal.
using System.Runtime.InteropServices; // Нужен для разбора структуры MsLlHookStruct, которую Windows пишет в память

namespace CaptionMenu; // То же пространство имён

/// <summary> Обёртка над WH_MOUSE_LL: установка/снятие + маршрутизация событий в обработчик приложения.
/// Умеет включаться/выключаться на лету (пункт меню трея), поэтому хранит хэндл в поле и снимает честно.</summary>
internal sealed class MouseHook : IDisposable
{
    // Ссылку на делегат держим В ПОЛЕ: если сборщик мусора уберёт delegate, Windows вызовет мусор crash (0xE0000008).
    private readonly Native.LowLevelMouseProc _proc; // Якорь, спасающий делегат от GC
    private readonly uint _selfPid;                // Наш PID: окна самого тулза игнорируем
    private IntPtr _hook;                           // Хэндл хука, который вернул SetWindowsHookEx (0 = не установлен)

    /// <summary> Как узнать хэндл окна-приёмника. Передаётся делегатом, потому что MessageWindow —
    /// экземпляр (NativeWindow), а не статический класс: хук не должен знать про конкретный тип.</summary>
    private readonly Func<IntPtr> _sinkHandle; // Аксессор к Handle окна-почтальона

    /// <summary> Что делать, когда кликнули ПКМ по заголовку. Вызывается через PostMessage на UI-потоке (НЕ из хука).</summary>
    private readonly Action<IntPtr, Pt> _onRightClickTitleBar;

    /// <summary> Флаг «мы сейчас внутри показа меню» — чтобы не открыть меню дважды (ПКМ-вверх тоже приходит).</summary>
    private volatile bool _suppress;

    /// <summary> Снять флаг «в меню» после закрытия. Вызывает UI-поток из Program, когда меню показано и закрыто.</summary>
    public void ClearSuppress() => _suppress = false; // Меню закрылось — снова ловим всё

    /// <summary> Конструктор: создаём делегат-хук (сам пока НЕ ставим — это делает Start, чтобы можно было вкл/выкл).</summary>
    /// <param name="selfPid"> Наш PID — окна тулза игнорируем.</param>
    /// <param name="sinkHandle"> Аксессор к хэндлу скрытого окна-приёмника.</param>
    /// <param name="onRightClickTitleBar"> Что делать при попадании в заголовок.</param>
    public MouseHook(uint selfPid, Func<IntPtr> sinkHandle, Action<IntPtr, Pt> onRightClickTitleBar)
    {
        _selfPid = selfPid;                           _sinkHandle = sinkHandle;                     // Аксессор к окну-почтальону
        _onRightClickTitleBar = onRightClickTitleBar; // Что делать при попадании
        _proc = HookCallback;                         // Привязываем метод (делегат уходит в поле _proc)
    }

    /// <summary> Хук установлен и работает?</summary>
    public bool IsInstalled => _hook != IntPtr.Zero; // Проверка для трея/лога

    /// <summary> Снять хук (выключение из трея). Идемпотентно: повторный вызов — no-op.</summary>
    public void Stop()
    {
        if (_hook == IntPtr.Zero) return; // Уже снят — идём дальше
        Native.UnhookWindowsHookEx(_hook); // Снимаем с системы
        _hook = IntPtr.Zero;              // Чистим поле
        _suppress = false;                // Сбрасываем флаг «в меню» (меню уже не наше)
        DebugLog.Write("hook stopped");   // В лог
    }

    /// <summary> Поставить хук (включение из трея или старт программы).</summary>
    /// <returns> true, если хук встал; false — если Windows отказала (код ошибки уходит в лог).</returns>
    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true; // Уже работает — ничего не делаем

        // Модуль для хука: null = «текущий процесс». Для WH_MOUSE_LL (глобального, не привязанного к потоку) hMod = 0.
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, IntPtr.Zero, 0);

        // 0 = хук не поставился (нет прав, или процесс не GUI). Молча продолжаем: тулз не должен падать.
        if (_hook == IntPtr.Zero)
        {
            DebugLog.Write($"SetWindowsHookEx failed, error={Marshal.GetLastWin32Error()}"); // В лог
            return false; // Честно сообщаем вызывающему
        }

        DebugLog.Write("hook started"); // Успех в лог
        return true; // Готово
    }

    /// <summary> Колбэк хука. Вызывается СИНХРОННО на UI-потоке при каждом событии мыши.</summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 1 nCode != HC_ACTION система сама обработала событие (например, хук-инъекция). Мы тут ни при чём.
        if (nCode != Native.HC_ACTION) return Native.CallNextHookEx(_hook, nCode, wParam, lParam); // Дальше по цепочке

        var msg = (uint)wParam.ToInt64(); // Какое событие пришло

        // 2 Нас интересует ТОЛЬКО ПКМ-вниз. Всё остальное (движения мыши!) летит дальше без единой проверки —
        // это критично для скорости: иначе тулз «душил» бы систему на 1000+ событиях в секунду.
        if (msg != Native.WM_RBUTTONDOWN && msg != Native.WM_RBUTTONUP)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam); // Быстрый выход, ноль работы

        // 3 ПКМ-ВВЕРХ, пока показывается наше меню — ГЛОТАЕМ (возвращаем 1), а не пропускаем.
        //
        // ЭТО КЛЮЧЕВОЙ МОМЕНТ (найдено 06.10.2026, 21:28). Если ПКМ-вверх пропустить, он уйдёт
        // в окно, оказавшееся под курсором — то есть В НАШЕ МЕНЮ. Меню воспримет это как
        // «клик мимо / отмена» и закроется, вернув MenuId.None. Именно так выглядело «меню
        // открывается и сразу исчезает» — при том, что foreground уже удалось перехватить.
        // Съедая ПКМ-вниз, обязано съесть и ПКМ-вверх: клик не должен «проткнуть» меню насквозь.
        if (msg == Native.WM_RBUTTONUP && _suppress)
            return new IntPtr(1); // Поглотили: меню не увидит «свой» отпускание

        // 4 Читаем структуру события. Структура лежит по адресу lParam — читаем копию (безопасно).
        var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam); // Копируем данные события
        var pt = data.pt; // Где курсор

        // 5 ПКМ-вниз: спрашиваем оболочку Windows — это тайтл? (внутри: NCHITTEST с таймаутом + фильтры).
        // Если не тайтл — отпускаем событие, система ведёт себя как обычно (контекстное меню приложения).
        var isTitle = CaptionHitTester.TryHitTitleBar(pt, _selfPid, out var hit);

        // ДИАГНОСТИКА: лог включается только через CAPTIONMENU_DEBUG=1, поэтому писать можно безопасно.
        // Без этой строки при отладке невозможно понять: «хук не пришёл» или «детектор отверг окно».
        var direction = msg == Native.WM_RBUTTONDOWN ? "DOWN" : "UP"; // Какая фаза (вниз/вверх)
        var verdict = isTitle ? "HIT " + hit.Hwnd + " cls=" + hit.ClassName + " title=" + hit.Title : "miss"; // Вердикт
        DebugLog.Write("RBTN " + direction + " at " + pt.X + "," + pt.Y + " -> " + verdict); // Диагностика

        if (!isTitle)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam); // Не наше

        // 6 ЭТО ТАЙТЛ. ПКМ-вверх пропускаем (он придёт из нашего меню и должен обработаться там).
        if (msg == Native.WM_RBUTTONUP)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam); // Вверх не трогаем

        // 7 Пометили «мы в меню», чтобы на время показа отключить повторное срабатывание (ПКМ-ап/даун ходят в паре).
        _suppress = true; // Флаг поднят

        // ПОДСТРАХОВКА: окно могло закрыться между кликом и обработкой (или вообще перестать быть окном).
        // Без проверки TrackPopupMenuEx выстрелит в мусорный хэндл поведение непредсказуемо.
        if (!Native.IsWindowVisible(hit.Hwnd)) // Окно исчезло — тихо откатываемся
        {
            _suppress = false; // Откат флага, ведём себя как обычно
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam); // Пусть приложение обработает само
        }

        // 8 Отложенный вызов: НЕ показываем меню прямо здесь (хук обязан вернуться за микросекунды!).
        // Просим UI-поток обработать через PostMessage в наше скрытое окно — там уже безопасно блокировать.
        // Запрос: «покажи меню». СПЕЦИАЛЬНО логируем хэндл-получатель: если MessageWindow.Create()
        // когда-то не сработала, Handle будет 0 — и PostMessage молча уйдёт в пустоту
        // (типичный баг, который невозможно заметить без трассировки).
        var sink = _sinkHandle();                                            // Хэндл окна-приёмника
        DebugLog.Write("posting WM_APP+1 to sink=" + sink + " target=" + hit.Hwnd); // Диагностика

        // Если окна-приёмника нет (Handle==0) — PostMessage ушёл бы в пустоту, и меню не показалось бы
        // НИКОГДА, при этом хук продолжал бы работать и выглядел «живым». Ловим это явно.
        if (sink == IntPtr.Zero)
        {
            DebugLog.Write("FATAL: sink handle is 0, cannot post menu request"); // 
            _suppress = false; // Откат флага
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam); // Пусть обработает приложение
        }

        Native.PostMessage(sink, MessageWindow.WmRequestMenu,
            new IntPtr(hit.Hwnd.ToInt64()), Native.MakeLParam(pt.X, pt.Y)); // Запрос: «покажи меню»

        // 9 Возвращаем 1 = «событие обработано, НЕ передавать в окно». Вот так блокируется штатное меню Windows
        // (иначе мы бы получили ДВА меню: системное + наше = каша).
        return new IntPtr(1); // Поглотили клик
    }

    /// <summary> Снять хук (при выходе). Вызывается из TrayIcon.Shutdown и из Program как страховка.</summary>
    public void Dispose() => Stop(); // Dispose = Stop, здесь это одно и то же
}
