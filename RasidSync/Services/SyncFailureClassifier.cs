using System.Net.Http;
using System.Text.Json;
using MySqlConnector;

namespace RasidSync.Services;

/// <summary>
/// يميز بين عطل عام يوقف دورة المزامنة وخطأ خاص بحركة واحدة.
/// خطأ الحركة يعزل في شاشة الأخطاء حتى تتابع بقية الفواتير.
/// </summary>
public static class SyncFailureClassifier
{
    public static bool IsInfrastructureFailure(Exception exception)
    {
        foreach (Exception current in Enumerate(exception))
        {
            if (current is HttpRequestException or TimeoutException or TaskCanceledException)
                return true;

            if (current is MySqlException mysql && IsMySqlConnectionFailure(mysql.Number))
                return true;

            string message = current.Message;
            if (message.Contains("السيرفر أعاد 500", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("السيرفر أعاد 502", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("السيرفر أعاد 503", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("السيرفر أعاد 504", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("غير مفعل للمزامنة", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("غير مسموح", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string GetErrorCode(Exception exception)
    {
        string message = GetFullMessage(exception);

        if (message.Contains("مستخدم لفاتورة أخرى", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("INVOICE_NUMBER_CONFLICT", StringComparison.OrdinalIgnoreCase))
            return "INVOICE_NUMBER_CONFLICT";

        if (message.Contains("السيرفر أعاد 400", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("السيرفر أعاد 409", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("السيرفر أعاد 422", StringComparison.OrdinalIgnoreCase) ||
            exception is JsonException or FormatException)
            return "INVALID_EVENT_PAYLOAD";

        if (IsInfrastructureFailure(exception))
            return "CONNECTION_OR_SERVER_ERROR";

        return "INVOICE_APPLY_ERROR";
    }

    public static string GetFullMessage(Exception exception)
    {
        return string.Join(" --> ", Enumerate(exception).Select(x => x.Message));
    }

    private static IEnumerable<Exception> Enumerate(Exception exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            yield return current;
            current = current.InnerException;
        }
    }

    private static bool IsMySqlConnectionFailure(int number) => number is
        0 or       // تعذر فتح الاتصال أو انقطع قبل وصول رقم من MySQL
        1042 or    // Unable to connect
        1045 or    // Access denied
        1049 or    // Unknown database
        2002 or 2003 or 2006 or 2013;
}
