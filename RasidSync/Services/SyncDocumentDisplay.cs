using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;

namespace RasidSync.Services;

/// <summary>معلومات عرض اختيارية؛ لا تدخل في هوية المستند أو تطبيقه.</summary>
public static class SyncDocumentDisplay
{
    public static async Task<object> ReadMetadataAsync(MySqlConnection connection,
        bool voucher, object setId, object number, object type)
    {
        string? name = null;
        try
        {
            name = await connection.QueryFirstOrDefaultAsync<string>(voucher
                ? "SELECT set_gl_name FROM tbl_setgl WHERE set_gl_id=@id LIMIT 1"
                : "SELECT set_nameinvo FROM tbl_setinvoice WHERE set_id=@id LIMIT 1",
                new { id = setId }, commandTimeout: 3);
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Sync display name: " + ex.Message); }
        return new { DocumentName = Clean(name), DocumentNumber = number, DocumentType = type, SettingsId = setId };
    }

    public static (int Type, int SetId, string Number, string Name) Read(string? payload, bool voucher)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload ?? "{}");
            JsonElement root = doc.RootElement;
            if (Property(root, "Payload") is JsonElement nested)
            {
                if (nested.ValueKind == JsonValueKind.String) return Read(nested.GetString(), voucher);
                root = nested;
            }
            JsonElement meta = Property(root, "Metadata") ?? default;
            JsonElement head = Property(root, "Header") ?? default;
            string Field(string metaName, string headerName) => Text(Property(meta, metaName)) is string s && s.Length > 0
                ? s : Text(Property(head, headerName));
            int type = Integer(Field("DocumentType", voucher ? "entry_type" : "inv_set_idinvo"));
            int setId = Integer(Field("SettingsId", voucher ? "entry_gl_id" : "inv_set_id"));
            return (type, setId, Field("DocumentNumber", voucher ? "entry_gl_number" : "inv_num"),
                Clean(Text(Property(meta, "DocumentName"))));
        }
        catch (JsonException) { return default; }
    }

    public static string Title(string? payload, string entityType, string uuid)
    {
        // لا نخمن أسماء السجلات القديمة ولا نجري أي تحديث عليها.
        try
        {
            using var doc = JsonDocument.Parse(payload ?? "{}");
            if (Property(doc.RootElement, "Metadata") is not JsonElement) return "";
        }
        catch (JsonException) { return ""; }
        bool voucher = entityType.Equals("Voucher", StringComparison.OrdinalIgnoreCase);
        var info = Read(payload, voucher);
        string name = info.Name ?? "";
        if (name.Length == 0) name = info.Type switch
        {
            901 => "سند قبض", 902 => "سند دفع", 903 => "سند قيد", 904 => "قيد افتتاحي",
            102 => "فاتورة مبيع", 202 => "مرتجع مبيع", 103 => "فاتورة شراء", 203 => "مرتجع شراء",
            501 => "مناقلة", 502 => "إدخال", 503 => "إخراج", 601 => "أول المدة", 701 => "عرض سعر",
            _ => voucher ? "سند" : "فاتورة"
        };
        return name + (string.IsNullOrWhiteSpace(info.Number) ? "" : " رقم " + info.Number);
    }

    private static int Integer(string value) => decimal.TryParse(value, NumberStyles.Number,
        CultureInfo.InvariantCulture, out decimal number) && number == decimal.Truncate(number)
        && number >= int.MinValue && number <= int.MaxValue ? (int)number : 0;
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "0" ? "" : value.Trim();
    private static string Text(JsonElement? value) => value is JsonElement v && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
    private static JsonElement? Property(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty p in row.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
}
