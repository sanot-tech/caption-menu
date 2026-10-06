// MessageWindow.cs — скрытое «окно-почтальон» для задач, которые обязаны выполняться на UI-потоке.
//
// ЗАЧЕМ: низкоуровневый хук мыши ВЫЗЫВАЕТСЯ на потоке, который его установил (наш UI-поток),
// и обязан вернуться за микросекунды. Показывать TrackPopupMenu прямо в хуке нельзя — меню блокирует
// поток, а Windows убьёт хук по таймауту (LowLevelHooksTimeout, ~1 c). Поэтому хук шлёт PostMessage
// СЮДА, и уже обработчик окна спокойно показывает меню. Это классический приём «defer to message pump».
//
// ВЫУЧЕННАЯ ОШИБКА (06.10.2026, этот проект): первая версия регистрировала класс окна вручную
// (RegisterClassEx + CreateWindowEx + свой WndProc через Marshal.GetFunctionPointerForDelegate).
// Она падала с ERROR_INVALID_PARAMETER (87) на RegisterClassEx, потом с 1407 на CreateWindowEx,
// и в итоге PostMessage уходил в пустоту — «меню никогда не появлялось», а хук при этом работал
// идеально. Отладка заняла несколько итераций, потому что причина была неочевидной.
//
// РЕШЕНИЕ: используем System.Windows.Forms.NativeWindow — он САМ регистрирует класс окна через
// Application.RegisterMessageClass и гарантированно корректен. Нам остаётся только переопределить
// WndProc. Никакого ручного P/Invoke, никаких структур WNDCLASSEX, ноль шансов выстрелить в ногу.
using System.Windows.Forms; // NativeWindow — абстракция WinForms поверх обычного HWND

namespace CaptionMenu; // То же пространство имён

/// <summary> Скрытое окно-приёмник: принимает WM_APP+1 от хука, показывает по нему меню
/// и служит ВЛАДЕЛЬЦЕМ этого меню (это обязательно — см. пояснение в CreateParams).</summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    // Наше собственное сообщение: WM_APP + 1. Диапазон WM_APP (0x8000..0xBFFF) зарезервирован для
    // приложений, поэтому конфликта с системой и другими тулзами не будет.
    public const uint WmRequestMenu = Native.WM_APP + 1; // «Покажи меню: окно в wParam, точка в lParam»

    // --- Стили окна-приёмника (числами — не тянем лишние P/Invoke) ---

    /// <summary> WS_POPUP — окно без заголовка и рамки, но ЖИВОЕ верхнеуровневое (в отличие от message-only):
    /// только такие окна Windows разрешает активировать, а без активации меню не покажется.</summary>
    private const int WsPopup = unchecked((int)0x80000000); // 

    /// <summary> WS_EX_TOOLWINDOW — окно не появляется в панели задач и не листается Alt+Tab. Вместе с
    /// WS_POPUP делает окно полностью невидимым для пользователя, но пригодным для foreground.</summary>
    private const int WsExToolWindow = 0x00000080; // 

    // Что делать, когда пришло сообщение (ставится один раз из Program).
    private readonly Action<IntPtr, Pt> _onRequestMenu; // Обработчик запроса меню

    private bool _disposed; // Флаг «уже освобождено», чтобы Dispose был идемпотентным

    /// <summary> Создать скрытое окно-приёмник (1×1 за экраном, вне таскбара и Alt+Tab).</summary>
    public MessageWindow(Action<IntPtr, Pt> onRequestMenu)
    {
        _onRequestMenu = onRequestMenu; // Запоминаем обработчик

        // ПАРАМЕТРЫ СОЗДАНИЯ. Тут была вторая неочевидная ошибка проекта (06.10.2026).

        // Что мы выяснили экспериментально:
        // 1) Message-only окно (Parent = HWND_MESSAGE) НЕЛЬЗЯ сделать foreground. А Windows
        // показывает TrackPopupMenuEx ТОЛЬКО если foreground принадлежит вызывающему потоку.
        // Результат: меню открывалось на 1 кадр и сразу закрывалось (menu choice = None).
        // 2) Значит нужен ОБЫЧНЫЙ верхнеуровневый окно-приёмник, который можно активировать.
        //
        // Как сделать его невидимым и нераздражающим:
        // • WS_EX_TOOLWINDOW — окно не показывается в таскбаре и не листается Alt+Tab.
        // • Размер 1×1 и позиция за пределами экрана (-32000) — «визуально» окна нет.
        // • Родитель = HWND_MESSAGE — окно невидимо для оболочки (нет рамки/заголовка).
        // При этом HWND-окно продолжает жить как обычное (можно SetForegroundWindow).

        var cp = new CreateParams
        {
            Caption = "CaptionMenuSink",        // Имя окна
            Parent = Native.HWND_MESSAGE,       // Родитель-призрак: не перечислить через оболочку
            Style = WsPopup,                 // WS_POPUP: без заголовка, но живое верхнеуровневое окно
            ExStyle = WsExToolWindow,           // Скрыто из таскбара/Alt+Tab
            X = -32000, Y = -32000,             // Уводим за пределы экрана — «визуально» окна нет
            Width = 1, Height = 1,              // 1×1 пиксель
        };

        // CreateHandle сам регистрирует класс окна (Application.RegisterMessageClass) и создаёт HWND.
        CreateHandle(cp);

        // Диагностика: Handle == 0 означает, что окна нет PostMessage из хука уйдут в пустоту.
        DebugLog.Write("MessageWindow created: hwnd=" + Handle); // Проверяем
        if (Handle == IntPtr.Zero) DebugLog.Write("FATAL: sink window creation failed, menu will never show"); // 
    }

    /// <summary> Обработчик сообщений. Вызывается на UI-потоке из цикла сообщений WinForms.</summary>
    protected override void WndProc(ref Message m)
    {
        // Наше сообщение: показать меню.
        if (m.Msg == (int)WmRequestMenu)
        {
            // try/catch: любая ошибка в меню не должна «уронить» скрытое окно (а с ним — весь тулз).
            try
            {
                // Распаковываем: wParam = хэндл целевого окна, lParam = координаты клика.
                // LPARAM: младшие 16 бит = X, старшие 16 бит = Y.
                var target = new IntPtr(m.WParam.ToInt64()); // Какое окно
                var l = m.LParam.ToInt64();                  // Сырое LPARAM
                var x = (int)(l & 0xFFFF);                   // X
                var y = (int)((l >> 16) & 0xFFFF);           // Y

                _onRequestMenu(target, new Pt { X = x, Y = y }); // Показываем (блокирует UI — ТЕПЕРЬ безопасно)
            }
            catch (Exception ex)
            {
                DebugLog.Write("menu failed: " + ex); // Логируем — меню не критично для жизни тулза
            }

            m.Result = IntPtr.Zero; // Сообщение обработано (не передаём в DefWindowProc)
            return;                 // Выход — дальше обработчик не идёт
        }

        // Всё остальное (WM_NCDESTROY, WM_PAINT и т.д.) — штатной обработке WinForms.
        base.WndProc(ref m); // Классическая цепочка
    }

    /// <summary> Уничтожить окно. Вызывается из Program при выходе (страховка от утечки HWND).</summary>
    public void Dispose()
    {
        if (_disposed) return; // Уже уничтожено
        _disposed = true;       // Помечаем
        if (Handle != IntPtr.Zero) DestroyHandle(); // Освобождаем HWND (иначе утечка оконного ресурса)
    }
}
