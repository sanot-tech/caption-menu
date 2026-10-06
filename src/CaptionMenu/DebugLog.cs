// DebugLog.cs — тихий лог в файл. Нужен, потому что тулз работает без окна консоли: если что-то
// «не сработало», единственный способ понять почему — прочитать лог. Пишем ТОЛЬКО при CAPTIONMENU_DEBUG=1,
// иначе на каждый клик в системе создавался бы файл (а это тормоза).
using System.Diagnostics; // Debug для условного вывода в отладчике
using System.Text;       // StringBuilder, чтобы не дёргать файловую систему на каждый вызов

namespace CaptionMenu; // То же пространство имён

internal static class DebugLog
{
    // Считаем «сколько раз писали» в буфере и сбрасываем на диск одним махом — иначе на каждый
    // клик мыши был бы open/close файла (а хук ловит тысячи событий в секунду).
    private static readonly StringBuilder Buffer = new(1024); // Накопитель строк
    private static readonly object Sync = new();              // Замок: хук и UI-поток пишут параллельно
    private static bool _enabled;                             // Включено ли логирование (из переменной окружения)

    /// <summary> Логирование включено? Дешёвая проверка bool'а, безопасна для вызова из горячего пути.</summary>
    public static bool IsEnabled => _enabled; // Детектор хука спрашивает это, чтобы не строить строки впустую

    /// <summary> Один раз при старте: включаем лог, если выставлена переменная окружения CAPTIONMENU_DEBUG=1.</summary>
    public static void Init()
    {
        var v = Environment.GetEnvironmentVariable("CAPTIONMENU_DEBUG"); // Читаем флаг
        _enabled = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); // Включаем
        if (_enabled) Write($"=== CaptionMenu старт, pid={Environment.ProcessId} ==="); // Стартовая метка в файл
    }

    /// <summary> Записать строку в лог. Метод безопасен для вызова из хука.</summary>
    /// <remarks>
    /// Здесь пишем НА ДИСК сразу, а не буферизуем. Изначально была буферизация «по 1 КБ» ради
    /// скорости, но она оказалась коварной: тулз без единого полного буфера писал в файл ТОЛЬКО
    /// при выходе — и при падении/зависании диагностировать было нечем. Лог включается лишь
    /// через CAPTIONMENU_DEBUG=1, поэтому цена записи не влияет на обычную работу.
    /// </remarks>
    public static void Write(string message)
    {
        // Если лог выключен — выходим мгновенно (это самый горячий путь, тут нельзя ничего лишнего).
        if (!_enabled) return;

        lock (Sync) // Хук и UI могут писать одновременно — защищаемся
        {
            Buffer.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').AppendLine(message); // Время + текст
            FlushLocked(); // Сразу на диск: события редкие, а диагностика дороже всего
        }
    }

    /// <summary> Сбросить буфер в файл. Вызывается из трея при выходе и по таймеру-тику.</summary>
    public static void Flush()
    {
        lock (Sync) { FlushLocked(); } // Под тем же замком
    }

    /// <summary> Собственно запись в файл (вызывается ТОЛЬКО под замком).</summary>
    private static void FlushLocked()
    {
        if (Buffer.Length == 0) return; // Нечего писать
        try
        {
            // Путь: %TEMP%\CaptionMenu.log — гарантированно доступен и не требует прав админа.
            var path = Path.Combine(Path.GetTempPath(), "CaptionMenu.log"); // Файл в temp
            File.AppendAllText(path, Buffer.ToString(), Encoding.UTF8); // Дописываем (лог не перетираем)
            Buffer.Clear(); // Буфер очищен
        }
        catch (IOException) { /*  Файл занят антивирусом — молча теряем строку, не роняем тулз */ }
        catch (UnauthorizedAccessException) { /*  Нет прав — аналогично */ }
    }
}
