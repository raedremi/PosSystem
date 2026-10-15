using System.Globalization;
using System.Text.Json;

namespace RasidSync.CustomerSupplierSync;

/// <summary>حزمة واحدة للبطاقة وحسابها. هويتها ثابتة من cust_id المحجوز للفرع.</summary>
public static class CustomerSyncContract
{
    public static readonly string[] AccountFields =
    ["acc_id", "acc_no", "acc_perent_no", "acc_Aname", "acc_type", "acc_report", "acc_level", "acc_customer"];

    public static readonly string[] CustomerFields =
    ["cust_id", "acc_id", "cust_name", "cust_mobile", "cust_taxnumber", "cust_typeprice",
     "cust_discount", "cust_limitdebt", "cust_address", "cust_email", "cust_note",
     "cust_disenblecredit", "cust_adddate", "cust_cash", "cust_disenablesales", "cust_type",
     "cust_crn", "cust_street", "cust_build", "cust_city", "cust_postalzone", "cust_Secoundcode",
     "cust_district", "cust_activity", "cust_free_vat", "cust_Id_card", "cust_age",
     "cust_male_fmale", "cust_nationlity", "cust_history"];

    public static int Branch(long id)
    {
        long suffix = id - 1000000000L;
        long branch = suffix / 1000000L;
        if (branch < 1 || branch > 999 || suffix % 1000000L < 1 || suffix % 1000000L > 999999)
            throw Error("CUSTOMER_ID_RANGE", "المزامنة الحالية للمعرفات الجديدة في نطاق الفروع 1..999 فقط.");
        return (int)branch;
    }

    public static string Uuid(long id)
    {
        Branch(id);
        return "43555354-0000-0000-0000-" + id.ToString("D12", CultureInfo.InvariantCulture);
    }

    public static CustomerPackage Parse(string uuid, int operation, JsonElement payload)
    {
        if (operation is not (1 or 2 or 3 or 4))
            throw Error("INVALID_CUSTOMER_PAYLOAD", "العملية يجب أن تكون 1 أو 2 أو 3 أو 4.");
        if (Number(Property(payload, "Version")) != 1)
            throw Error("INVALID_CUSTOMER_PAYLOAD", "إصدار حزمة العميل غير مدعوم.");
        var customer = Row(Property(payload, "Customer"), CustomerFields);
        var account = Row(Property(payload, "Account"), AccountFields);
        long id = Number(customer["cust_id"]);
        long accountId = Number(account["acc_id"]);
        if (Branch(id) != Branch(accountId) || Number(customer["acc_id"]) != accountId ||
            !Uuid(id).Equals(uuid.Trim(), StringComparison.OrdinalIgnoreCase))
            throw Error("INVALID_CUSTOMER_PAYLOAD", "هوية البطاقة أو ربط الحساب لا يطابق الحزمة.");
        long type = Number(customer["cust_type"]);
        if (type is not (1 or 2) || Number(account["acc_customer"]) != type ||
            Number(account["acc_type"]) != 2 || Number(account["acc_no"]) <= 0 ||
            Number(account["acc_perent_no"]) <= 0 || Number(account["acc_level"]) < 1 ||
            string.IsNullOrWhiteSpace(Convert.ToString(customer["cust_name"])))
            throw Error("INVALID_CUSTOMER_PAYLOAD", "نوع البطاقة أو بيانات الحساب غير صحيحة.");
        return new CustomerPackage(id, accountId, customer, account);
    }

    private static Dictionary<string, object?> Row(JsonElement row, string[] fields)
    {
        if (row.ValueKind != JsonValueKind.Object)
            throw Error("INVALID_CUSTOMER_PAYLOAD", "بيانات البطاقة أو الحساب غير موجودة.");
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        // نقرأ القائمة المعتمدة فقط؛ الأرصدة والحقول المالية لا تدخل في الحزمة.
        foreach (string field in fields)
        {
            JsonElement value = Property(row, field);
            result[field] = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => throw Error("INVALID_CUSTOMER_PAYLOAD", "قيمة غير صالحة: " + field)
            };
        }
        return result;
    }

    private static JsonElement Property(JsonElement row, string name)
    {
        if (row.ValueKind == JsonValueKind.Object)
            foreach (var property in row.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw Error("INVALID_CUSTOMER_PAYLOAD", "حقل مطلوب: " + name);
    }

    public static long Number(object? value)
    {
        string? text = value is JsonElement element ? element.ToString() : Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) ||
            number != decimal.Truncate(number) || number < long.MinValue || number > long.MaxValue)
            throw Error("INVALID_CUSTOMER_PAYLOAD", "معرف أو رقم حساب غير صحيح.");
        return (long)number;
    }

    public static InvalidOperationException Error(string code, string text) => new(code + "|" + text);
}

public sealed record CustomerPackage(long CustomerId, long AccountId,
    Dictionary<string, object?> Customer, Dictionary<string, object?> Account);
