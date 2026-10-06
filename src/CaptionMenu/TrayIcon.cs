// 🌐 TrayIcon.cs — иконка в трее + меню управления. Это «пульт» тулза: вкл/выкл, автозапуск, выход.
// 🧠 Почему WinForms NotifyIcon, а не ручной Shell_NotifyIcon: NotifyIcon сам умеет контекстное меню,
//    Balloon-подсказки и DPI-скейлинг, а нам важна надёжность, а не 200 строк P/Invoke.
//    Меню тайтлов и Z-порядок при этом делаются строго нативно (Win32) — см. CaptionMenuPresenter.
using System.Drawing;        // 🖼️ SystemIcons — иконка трея
using System.Windows.Forms; // 🪟 NotifyIcon + ContextMenuStrip (окон тут нет — только трей)

namespace CaptionMenu; // 📦 То же пространство имён

/// <summary>🎛️ Управляющий слой трея: держит иконку, обрабатывает клики и корректно гасит тулз при выходе.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;              // 🪧 Иконка в системном трее
    private readonly ToolStripMenuItem _toggleItem; // 🔘 Пункт «Перехват: вкл/выкл»
    private readonly ToolStripMenuItem _autoStartItem; // 🚀 Пункт «Автозапуск»
    private readonly MouseHook _hook;               // 🪝 Хук мыши (владелец гасит его при выходе)
    private readonly ApplicationContext _context;   // 🔄 Цикл сообщений: держит процесс живым
    private bool _enabled;                          // ⚡ Текущее состояние перехвата

    /// <summary>🎨 Читаем иконку приложения из исполняемого файла.
    ///
    /// 🧠 Зачем не SystemIcons.Application: тот даёт общую иконку «приложения», и в трее рядом
    ///    с часами тулз неотличим от десятка других служебных программ. Своя иконка (см.
    ///    assets/ICON-PROMPT.md) делает его узнаваемым. Если файла с иконкой нет или он битый —
    ///    молча откатываемся на системную: функциональность важнее красоты, лишних крашей не надо. ✅
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;                 // 📍 Путь к нашему .exe
            if (string.IsNullOrEmpty(exe)) return SystemIcons.Application; // 🚫 Не знаем путь — системная
            var icon = Icon.ExtractAssociatedIcon(exe);        // 🎨 Иконка, вшитая в файл (ApplicationIcon в .csproj)
            return icon ?? (Icon)SystemIcons.Application;      // 🛟 Не нашлась — системная
        }
        catch (Exception ex)
        {
            DebugLog.Write("icon load failed: " + ex.Message); // 🧯 Красивая иконка не важнее живости тулза
            return SystemIcons.Application;                    // 🛟 Откат на системную
        }
    }

    /// <summary>🎛️ Создать иконку трея. Хук сюда не ставим — Program уже поставил его и знает результат;
    ///        нам нужен только готовый MouseHook, чтобы уметь включать/выключать и гасить его при выходе.</summary>
    public TrayIcon(MouseHook hook)
    {
        _hook = hook;            // 🪝 Сохраняем хук, чтобы вкл/выкл и гашение шли через него
        _enabled = hook.IsInstalled; // ⚡ Исходное состояние = реальное (если хук не встал — сразу видно в меню)
        _context = new ApplicationContext(); // 🔄 Цикл сообщений (без него процесс сразу завершится)

        // 🪧 Контекстное меню трея: сначала собираем пункты, потом вешаем на иконку.
        var menu = new ContextMenuStrip(); // 📋 Меню трея

        _toggleItem = new ToolStripMenuItem("Intercept: on") // 🔘 Надпись состояния (EN — публичный проект)
        {
            CheckOnClick = false, // 🚫 Не даём WinForms менять галочку самому — обработаем вручную (нужен реальный хук)
        };
        _toggleItem.Click += (_, _) => ToggleHook(); // 👆 Клик по пункту = вкл/выкл перехвата
        menu.Items.Add(_toggleItem); // ➕ В меню

        _autoStartItem = new ToolStripMenuItem("Start with Windows") // 🚀 Режим автозапуска
        {
            CheckOnClick = true, // ✅ WinForms сам нарисует галочку — нам остаётся записать в реестр
        };
        _autoStartItem.Checked = AutoStart.IsEnabledForCurrentUser(); // 🔄 Читаем реальное состояние из реестра
        _autoStartItem.CheckedChanged += (_, _) => AutoStart.SetEnabled(_autoStartItem.Checked); // 💾 Сохраняем выбор
        menu.Items.Add(_autoStartItem); // ➕ В меню

        menu.Items.Add(new ToolStripSeparator()); // ───────

        var aboutItem = new ToolStripMenuItem("How to use"); // ❓ Подсказка
        aboutItem.Click += (_, _) => ShowUsageBalloon(); // 💬 Показать balloon
        menu.Items.Add(aboutItem); // ➕

        var exitItem = new ToolStripMenuItem("Exit"); // 🚪 Выход
        exitItem.Click += (_, _) => Shutdown(); // 🛑 Аккуратно гасим всё
        menu.Items.Add(exitItem); // ➕

        // 🪧 Иконка: системная «оконная» = сразу понятно, что тулз про окна, и мы не воруем чужие .ico.
        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),                    // 🖼️ Своя иконка (ExtractAssociatedIcon) с откатом на системную
            Text = "CaptionMenu — right-click a title bar", // 📝 Tooltip (Windows обрезает до 127 символов)
            ContextMenuStrip = menu,                 // 📋 Правая кнопка по иконке = меню
            Visible = true,                          // 👁️ Показать в трее
        };

        // 🖱️ Двойной клик по иконке — тоже подсказка (быстрый доступ к «как пользоваться»).
        _icon.DoubleClick += (_, _) => ShowUsageBalloon(); // 💬

        RefreshToggleLabel(); // 🎨 Ставим правильный текст/цвет в зависимости от того, встал ли хук
    }

    /// <summary>▶️ Запустить цикл сообщений (блокирует поток, пока не вызовут Shutdown).</summary>
    public void Run() => Application.Run(_context); // 🔄 Крутимся, пока жив TrayIcon

    /// <summary>🔄 Вкл/выкл перехват ПКМ по тайтлу. Честно ставит/снимает настоящий хук через MouseHook.</summary>
    private void ToggleHook()
    {
        // 🔀 Если сейчас включено — снимаем хук. Если выключено (или не встал) — ставим заново.
        var newState = !_enabled; // 🧮 Куда хотим переключиться
        if (newState)
        {
            var ok = _hook.Start(); // 🔛✅ Пробуем поставить
            _enabled = ok;           // ⚡ Состояние = результат (если не встал — останется «выключено»)
        }
        else
        {
            _hook.Stop(); // 🔛 Снимаем
            _enabled = false; // ⚡ Состояние
        }

        RefreshToggleLabel(); // 🎨 Обновить надпись/цвет
        DebugLog.Write($"toggle -> enabled={_enabled}"); // 📝 В лог

        // 💬 Честно сообщаем юзеру, что получилось (особенно если хук не встал из-за прав).
        Notify("CaptionMenu", _enabled
            ? "Intercept on: right-click a title bar → Always on Top."
            : "Intercept off: the stock Windows menu is used."); // 💬
    }

    /// <summary>🎨 Обновить надпись и цвет пункта «Перехват» под текущее состояние.</summary>
    private void RefreshToggleLabel()
    {
        _toggleItem.Text = _enabled ? "Intercept: on" : "Intercept: off"; // 📝 Надпись
        // 🎨 Зелёный = работает, серый = выключено. Цвет берём из ControlText, чтобы не спорить с темой.
        _toggleItem.ForeColor = _enabled ? Color.FromArgb(0, 128, 0) : SystemColors.GrayText; // 🎨
    }

    /// <summary>❓ Показать balloon-подсказку «как пользоваться» (одна строка, чтобы не грузить чтением).</summary>
    private void ShowUsageBalloon() =>
        Notify("CaptionMenu", "Right-click a title bar → Always on Top. Also: Move, Resize, Center, Close.");

    /// <summary>💬 Показать системное уведомление (balloon живёт несколько секунд, потом исчезает).</summary>
    private void Notify(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); // 💬 4 секунды, иконка инфо

    /// <summary>🛑 Аккуратный выход: сбросить лог → снять хук → убрать иконку → закрыть цикл сообщений.</summary>
    private void Shutdown()
    {
        DebugLog.Flush();     // 💾 Сбросить лог на диск перед смертью (иначе потеряем хвост)
        _hook.Stop();         // ✂️ Хук ОБЯЗАТЕЛЬНО снимаем (иначе после краша останется «мёртвый» хук в системе)
        _icon.Visible = false;// 🙈 Убрать иконку из трея
        _icon.Dispose();      // 🗑️ Освободить ресурсы иконки
        _context.ExitThread();// 🔄 Закрыть цикл сообщений → Run вернётся → процесс завершится
    }

    /// <summary>🧹 Если кто-то забудет вызвать Shutdown, финализатор вызовет это при сборке.</summary>
    public void Dispose() => Shutdown(); // ♻️ Безопасная уборка
}
