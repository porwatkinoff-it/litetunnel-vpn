using LiteTunnel.Core;

namespace LiteTunnel.App;

/// <summary>Numbers and dates as the phone apps show them. The app runs without culture data, so Russian is spelled out here.</summary>
public static class Format
{
    private static readonly string[] Months = ["янв.", "февр.", "мар.", "апр.", "мая", "июн.", "июл.", "авг.", "сент.", "окт.", "нояб.", "дек."];

    /// <summary>Binary units: "1,5 ГБ".</summary>
    public static string Size(long value)
    {
        string[] units = ["КБ", "МБ", "ГБ", "ТБ"];
        double v = value / 1024.0;
        int unit = 0;
        while (v >= 1024 && unit < units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        var number = v < 10 ? v.ToString("0.#").Replace('.', ',') : v.ToString("0");
        return $"{number} {units[unit]}";
    }

    /// <summary>"1,5 МБ/с".</summary>
    public static string Speed(long bytesPerSecond) => Size(bytesPerSecond) + "/с";

    /// <summary>"05:07", or "1:05:07" past an hour.</summary>
    public static string Duration(TimeSpan span)
    {
        long s = Math.Max(0, (long)span.TotalSeconds);
        long h = s / 3600, m = s % 3600 / 60, sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
    }

    public static string Date(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return $"{local.Day} {Months[local.Month - 1]} {local.Year}";
    }

    public static string Relative(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var minutes = (long)((now ?? DateTimeOffset.Now) - date).TotalMinutes;
        return minutes switch
        {
            < 1 => "только что",
            < 60 => $"{minutes} мин. назад",
            < 24 * 60 => $"{minutes / 60} ч. назад",
            < 48 * 60 => "вчера",
            < 7 * 24 * 60 => $"{minutes / (24 * 60)} дн. назад",
            _ => Date(date),
        };
    }

    /// <summary>"до 12 нояб. 2026 · 12 дн.", "истекает сегодня" or "истекла …".</summary>
    public static string Expiry(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        if (date <= current) return $"истекла {Date(date)}";
        var days = (long)(date - current).TotalDays;
        return days < 1 ? "истекает сегодня" : $"до {Date(date)} · {days} дн.";
    }

    /// <summary>"1 сервер", "3 сервера", "5 серверов".</summary>
    public static string Servers(int count)
    {
        int tens = count % 100, ones = count % 10;
        var word = tens is >= 11 and <= 14 ? "серверов" : ones == 1 ? "сервер" : ones is >= 2 and <= 4 ? "сервера" : "серверов";
        return $"{count} {word}";
    }

    public static string Label(this PingResult ping) => ping is PingMs ms ? $"{ms.Value} мс" : "нет связи";

    /// <summary>How full the signal bars are, 0 to 4.</summary>
    public static int Level(this PingResult ping) => ping switch
    {
        PingMs { Value: < 100 } => 4,
        PingMs { Value: < 200 } => 3,
        PingMs { Value: < 400 } => 2,
        PingMs => 1,
        _ => 0,
    };

    /// <summary>The colour token of a ping: connected, busy or danger.</summary>
    public static string Tone(this PingResult ping) => ping switch
    {
        PingMs { Value: < 150 } => "Connected",
        PingMs { Value: < 400 } => "Busy",
        _ => "Danger",
    };

    public static string Title(this PingMode mode) => mode switch
    {
        PingMode.Real => "Реальный",
        PingMode.ProxyGet => "Через сервер, GET",
        PingMode.ProxyHead => "Через сервер, HEAD",
        PingMode.Tcp => "TCP",
        _ => "ICMP",
    };

    public static string Footer(this PingMode mode) => mode switch
    {
        PingMode.Real => "Запрос к адресу проверки через каждый сервер. Показывает, что сервер пропускает трафик; время приведено к обычному пингу (делится на 3,3), чтобы его было с чем сравнить.",
        PingMode.ProxyGet or PingMode.ProxyHead => "Запрос к адресу проверки через каждый сервер. Показано полное время запроса: оно в несколько раз больше обычного пинга.",
        PingMode.Tcp => "Только соединение с сервером, без запроса через него. Быстро, но не покажет сервер, который не пропускает трафик.",
        _ => "Обычный ping. Многие серверы на него не отвечают, тогда будет «нет связи».",
    };

    public static string Title(this PingDisplay display) => display switch
    {
        PingDisplay.Time => "Цифры",
        PingDisplay.Bars => "Полоски",
        PingDisplay.Both => "Цифры и полоски",
        _ => "Точки",
    };

    public static string Title(this RoutingMode mode) => mode == RoutingMode.Global ? "Всё через VPN" : "Россия напрямую";

    public static string Title(this DnsKind kind) => kind switch { DnsKind.Udp => "UDP", DnsKind.Tls => "DoT", _ => "DoH" };

    public static string Title(this SortMode mode) => mode switch
    {
        SortMode.None => "Как в подписке",
        SortMode.Ping => "По пингу",
        _ => "По имени",
    };

    public static string Title(this TunStack stack) => stack switch
    {
        TunStack.System => "system",
        TunStack.Gvisor => "gVisor",
        _ => "mixed",
    };

    public static string Title(this ConnectionMode mode) => mode == ConnectionMode.Tun ? "VPN (TUN)" : "Системный прокси";
}
