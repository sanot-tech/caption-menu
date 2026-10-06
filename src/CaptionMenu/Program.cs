// Program.cs — ТОЧКА ВХОДА. Собирает все детали в правильном порядке и держит процесс живым.
// Порядок ЗАЧИМ ТАКОЙ (это главная часть «инженерии», а не кода):
// 1) Мьютекс — чтобы запущен был ОДИН экземпляр (двойной хук = двойные меню = лажа).
// 2) STAThread — нужно WinForms (иконка трея). Хук тоже требует, чтобы у него был цикл сообщений.
// 3) Лог скрытое окно (нужно для PostMessage из хука) хук трей цикл сообщений.
// Ключевая связка: MouseHook.Start() вызывается ДО Application.Run, но хук требует цикла сообщений,
// поэтому мы ставим хук после создания скрытого окна и запускаем Run() — цикл оживает и хук оживает вместе.
using System.Windows.Forms; // Для ApplicationContext/TrayIcon (через TrayIcon.cs), здесь — только STAThread атрибут

namespace CaptionMenu; // То же пространство имён

internal static class Program
{
    // Имя мьютекса. Если такой уже есть — другой экземпляр запущен, мы вежливо выходим.
    // Local\ = только в этой сессии пользователя (не лезем в глобальное пространство сессий).
    private const string SingleInstanceMutexName = @"Local\CaptionMenu.SingleInstance";

    // Хэндл окна-приёмника. Хранится в поле статического класса, потому что обработчик меню
    // (ShowCaptionMenu) — статический метод, и ему нужен и хэндл окна-владельца меню, и цель клика.
    private static IntPtr sinkHandle; // Заполняется сразу после создания MessageWindow

    /// <summary> Main. Возвращает код: 0 = всё хорошо, 1 = уже запущен, 2 = критическая ошибка.</summary>
    [STAThread] // Single-Threaded Apartment: WinForms (иконка трея) требует STA. Потом UI-цикл крутится здесь же.
    private static int Main()
    {
        // 1 Защита от второго экземпляра. Создаём мьютекс; если он уже существует — выходим (код 1).
        using var mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var isFirstInstance); // 
        if (!isFirstInstance)
        {
            // Сообщаем юзеру, что тулз УЖЕ работает (иначе он подумает, что клик «не работает»).
            MessageBox.Show("CaptionMenu is already running — look for it in the tray, next to the clock.", // 
                "CaptionMenu", MessageBoxButtons.OK, MessageBoxIcon.Information); // 
            return 1; // Мягкий выход
        }

        // 2 Лог (включается только при CAPTIONMENU_DEBUG=1) — до всего, чтобы ловить ранние сбои.
        DebugLog.Init(); // Готовим логгер

        try
        {
            // 3 Наш PID — по нему хук игнорирует наши же окна (меню, трей).
            var selfPid = (uint)Environment.ProcessId; // 

            // 4 Объект хука (пока НЕ ставим — стартует шагом 6). Второй аргумент: аксессор к хэндлу
            // окна-приёмника, который создаётся следующим шагом. Замыкание () => sinkHandle читает
            // СТАТИЧЕСКОЕ поле, заполняемое сразу после создания окна, поэтому
            // «использовать до инициализации» здесь невозможно.
            var hook = new MouseHook(selfPid, () => sinkHandle, ShowCaptionMenu); // 

            // 5 СКРЫТОЕ ОКНО — ДО установки хука. Порядок важен: хук сразу шлёт PostMessage в это окно,
            // и если окна ещё нет — запрос уйдёт в пустоту (Handle=0) и меню просто не откроется.
            //
            // Анонимный обработчик ниже НЕ упоминает переменную sink напрямую (иначе C# ругается
            // «нельзя использовать переменную до объявления»). Вместо этого берём хэндл из
            // статического поля sinkHandle, которое заполняется сразу после создания окна —
            // к моменту прихода сообщения оно уже точно ненулевое.
            var sink = new MessageWindow((hwnd, pt) =>
            {
                // Показываем нативное меню (блокирует UI-поток — тут это БЕЗОПАСНО, мы уже не в хуке).
                try
                {
                    CaptionMenuPresenter.ShowAndExecute(sinkHandle, hwnd, pt); // Строим, показываем, выполняем
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"menu failed: {ex}"); // Меню сломалось — но тулз обязан жить
                }
                finally
                {
                    hook.ClearSuppress(); // Меню закрылось (выбрали/отменили) — снова ловим ПКМ по тайтлам
                    DebugLog.Flush();      // Сразу на диск: событие редкое, а лог нужен для диагностики ПРЯМО СЕЙЧАС
                }
            }); // Приёмник сообщений готов

            // Запоминаем хэндл окна: он нужен обработчику меню как ВЛАДЕЛЕЦ (без него меню не покажется).
            sinkHandle = sink.Handle; // Поле для ShowCaptionMenu

            // Если окно не создалось — дальше работать бессмысленно: хук будет ловить клики,
            // а меню показать не сможет. Честно говорим и выходим, вместо «мёртвого» тулза в трее.
            if (sinkHandle == IntPtr.Zero)
            {
                MessageBox.Show("Failed to create the internal message window — the menu cannot work.", // 
                    "CaptionMenu — error", MessageBoxButtons.OK, MessageBoxIcon.Error); // 
                return 2; // Выход с кодом ошибки
            }

            // Финализатор окна: освобождаем HWND при выходе (иначе утечка оконного ресурса в USER32).
            using var sinkGuard = sink; // using на IDisposable вызовет Dispose() в конце Main

            // 6 Ставим хук (окно-приёмник уже есть, значит PostMessage не потеряется).
            // Если хук не встал — тулз всё равно жив (меню трея работает), но перехвата не будет:
            // так мы не падаем, а честно показываем «выключено».
            hook.Start(); // Попытка #1
            DebugLog.Write($"started, hook={hook.IsInstalled}"); // В лог факт установки

            // 7 Иконка в трее: показывает состояние хука, автозапуск, выход. И СПЕЦИАЛЬНО держит процесс живым
            // (Application.Run внутри). Без неё Main вышел бы и процесс умер.
            using var tray = new TrayIcon(hook); // Собрали трей
            tray.Run(); // Крутим цикл сообщений, пока юзер не выберет «Выход»

            // 8 Сюда попадём только после Shutdown (ExitThread). Финальная уборка.
            DebugLog.Write("exiting cleanly"); // 
            return 0; // Успех
        }
        catch (Exception ex)
        {
            // Что-то сломалось на старте — показываем честную ошибку, а не молча исчезаем.
            MessageBox.Show($"CaptionMenu failed to start:\n\n{ex}", // 
                "CaptionMenu — error", MessageBoxButtons.OK, MessageBoxIcon.Error); // 
            DebugLog.Write($"fatal: {ex}"); // И в лог
            return 2; // С кодом ошибки
        }
    }

    /// <summary> Вызывается с UI-потока (из окна-приёмника), когда хук поймал ПКМ по заголовку.
    /// Смысл вынесен отдельно: читаемость — Program про запуск, здесь — про «что делать по клику».</summary>
    /// <param name="target"> Окно приложения, по которому кликнули.</param>
    /// <param name="pt"> Точка клика.</param>
    private static void ShowCaptionMenu(IntPtr target, Pt pt) =>
        // Хэндл окна-приёмника берём из поля sink — он же будет владельцем меню.
        CaptionMenuPresenter.ShowAndExecute(sinkHandle, target, pt); // Прямая передача в докладчик меню
}
