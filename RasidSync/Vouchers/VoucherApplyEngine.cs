using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;

namespace RasidSync.Vouchers;

/// <summary>
/// ينسخ رأس السند وقيوده الفعلية دون توليد قيود أو أرقام جديدة.
/// MyISAM: إعادة المحاولة تعيد بناء القيود كاملة ولا تعتمد على Rollback.
/// </summary>
internal static class VoucherApplyEngine
{
    private static readonly string[] HeaderColumns =
    [
        "entry_type", "entry_gl_id", "entry_gl_number", "entry_gl_genral",
        "entry_credit", "entry_debit", "entry_disc", "entry_total",
        "entry_acc_def", "entry_acc2", "entry_acc_disc", "entry_acc_tax",
        "entry_b_id", "entry_save_b_id", "entry_user_id", "entry_desc", "entry_ref",
        "entry_date", "entry_time", "entry_datetime", "gl_uuid",
        "entry_value_tax", "entry_percent_tax", "entry_net_after_tax",
        "entry_debit_profit", "entry_credit_profit", "entry_cashc_id"
    ];
    private static readonly string[] DetailColumns =
    [
        "gl_id_branch", "gl_ac_id", "gl_ac_id_app", "gl_debit", "gl_credit",
        "gl_desc", "gl_datetime", "gl_date", "gl_time", "gl_inv_id",
        "gl_num_general", "gl_user_id", "gl_inv_set_id", "gl_set_idinvo",
        "gl_savein_b_id", "gl_b_id"
    ];

    internal static async Task<string> ApplyAsync(
        IDbConnection connection, string entityUuid, int operationType, JsonElement payload)
    {
        string uuid = entityUuid.Trim();
        if (!Guid.TryParse(uuid, out _) || operationType is not (1 or 2 or 3 or 4))
            throw Invalid("هوية السند أو عملية المزامنة غير صحيحة.");

        // فحص الحزمة كاملة قبل أي كتابة، والحذف يعتمد على UUID فقط.
        VoucherSnapshot? snapshot = operationType == 3 ? null : Validate(payload, uuid);
        string database = await connection.ExecuteScalarAsync<string>("SELECT DATABASE();");
        string lockName = "voucher:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(database)))[..48];
        int? locked = await connection.ExecuteScalarAsync<int?>(
            "SELECT GET_LOCK(@lockName, 10);", new { lockName });
        if (locked != 1)
            throw new TimeoutException("تعذر الحصول على قفل تطبيق السند؛ ستعاد المحاولة.");

        try
        {
            ExistingVoucher? old = await connection.QuerySingleOrDefaultAsync<ExistingVoucher>(
                """
                SELECT entry_id EntryId, entry_gl_number Number,
                       entry_gl_genral GeneralNumber, entry_gl_id SetId, entry_type Type
                FROM tbl_gl_entry WHERE gl_uuid=@uuid;
                """, new { uuid });

            if (operationType == 3)
            {
                if (old == null) return "السند محذوف مسبقًا؛ اعتبرت الحركة ناجحة.";
                if (old.Type is not (901 or 902 or 903 or 904))
                    throw Invalid("حذف هذا النوع من السندات غير مدعوم حاليًا.");
                await EnsureIdentityAvailableAsync(connection, old, old.EntryId);
                // التفاصيل أولًا حتى يبقى الرأس مرجعًا لإعادة المحاولة عند فشل الحذف.
                await DeleteDetailsAsync(connection, old);
                await connection.ExecuteAsync(
                    "DELETE FROM tbl_gl_entry WHERE entry_id=@EntryId AND gl_uuid=@uuid;",
                    new { old.EntryId, uuid });
                return $"تم حذف السند {old.Number} وقيوده.";
            }

            VoucherSnapshot data = snapshot!;
            await EnsureIdentityAvailableAsync(connection, data.Identity, old?.EntryId);
            if (old != null)
            {
                if (old.Type is not (901 or 902 or 903 or 904))
                    throw Invalid("UUID يعود إلى نوع سند آخر غير مدعوم.");
                await EnsureIdentityAvailableAsync(connection, old, old.EntryId);
            }
            if (old == null || old.Number != data.Identity.Number ||
                old.SetId != data.Identity.SetId || old.Type != data.Identity.Type)
            {
                // لا نحذف قيودًا موجودة لا يوجد رأس يثبت ملكيتها لهذا UUID.
                long orphanCount = await connection.ExecuteScalarAsync<long>(
                    """
                    SELECT COUNT(*) FROM tbl_gl
                    WHERE gl_inv_id=@Number AND gl_inv_set_id=@SetId AND gl_set_idinvo=@Type;
                    """, data.Identity);
                if (orphanCount != 0)
                    throw new InvalidOperationException(
                        "VOUCHER_NUMBER_CONFLICT|توجد قيود على رقم السند دون رأس مطابق للهوية.");
            }

            long setCount = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_setgl WHERE set_gl_id=@SetId;", data.Identity);
            long counterCount = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_number WHERE num_id=1;");
            if (setCount != 1 || counterCount != 1)
                throw Invalid("إعداد السند أو العداد العام غير موجود في الجهة المستقبلة.");

            // إزالة قيود الهوية السابقة قبل تغيير الرأس كي يمكن إصلاح أي فشل جزئي.
            if (old != null) await DeleteDetailsAsync(connection, old);
            if (old == null)
                await InsertRowAsync(connection, "tbl_gl_entry", HeaderColumns, data.Header);
            else
                await UpdateHeaderAsync(connection, old.EntryId, data.Header);

            // يحذف ما قد تبقى من محاولة سابقة ثم ينصب نسخة التفاصيل كاملة.
            await DeleteDetailsAsync(connection, data.Identity);
            foreach (JsonElement detail in data.Details)
                await InsertRowAsync(connection, "tbl_gl", DetailColumns, detail);

            // نحرك العدادات للأمام فقط، ولا نخصص أرقامًا جديدة أثناء المزامنة.
            await connection.ExecuteAsync(
                """
                UPDATE tbl_setgl SET set_gl_number=GREATEST(set_gl_number,@NextNumber)
                WHERE set_gl_id=@SetId;
                UPDATE tbl_number SET num_gl_general=GREATEST(num_gl_general,@NextGeneral)
                WHERE num_id=1;
                """, new
                {
                    data.Identity.SetId,
                    NextNumber = (decimal)data.Identity.Number + 1,
                    NextGeneral = (decimal)data.Identity.GeneralNumber + 1
                });
            return $"تم تطبيق السند {data.Identity.Number} مع {data.Details.Count} قيد.";
        }
        finally
        {
            // إغلاق الاتصال يحرر القفل أيضًا إذا انقطع الاتصال أثناء التطبيق.
            try { await connection.ExecuteScalarAsync<int?>("SELECT RELEASE_LOCK(@lockName);", new { lockName }); }
            catch { /* نحافظ على نتيجة التطبيق أو الخطأ الأصلي. */ }
        }
    }

    private static async Task EnsureIdentityAvailableAsync(
        IDbConnection connection, ExistingVoucher identity, long? currentId)
    {
        long count = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM tbl_gl_entry
            WHERE entry_gl_number=@Number AND entry_gl_id=@SetId AND entry_type=@Type
              AND (@currentId IS NULL OR entry_id<>@currentId);
            """, new { identity.Number, identity.SetId, identity.Type, currentId });
        if (count > 0)
            throw new InvalidOperationException(
                $"VOUCHER_NUMBER_CONFLICT|Number={identity.Number}|SetId={identity.SetId}|Type={identity.Type}");
    }

    private static Task DeleteDetailsAsync(IDbConnection connection, ExistingVoucher identity) =>
        connection.ExecuteAsync(
            """
            DELETE FROM tbl_gl
            WHERE gl_inv_id=@Number AND gl_inv_set_id=@SetId AND gl_set_idinvo=@Type;
            """, identity);

    private static async Task InsertRowAsync(
        IDbConnection connection, string table, string[] columns, JsonElement row)
    {
        DynamicParameters parameters = Parameters(columns, row);
        string names = string.Join(",", columns.Select(x => "`" + x + "`"));
        string values = string.Join(",", columns.Select((_, i) => "@p" + i));
        // أسماء الجدول والأعمدة ثابتة داخل الخدمة؛ لا تؤخذ من JSON.
        await connection.ExecuteAsync($"INSERT INTO `{table}` ({names}) VALUES ({values});", parameters);
    }

    private static async Task UpdateHeaderAsync(IDbConnection connection, long entryId, JsonElement row)
    {
        DynamicParameters parameters = Parameters(HeaderColumns, row);
        parameters.Add("entryId", entryId);
        string assignments = string.Join(",", HeaderColumns.Select((name, i) => $"`{name}`=@p{i}"));
        await connection.ExecuteAsync(
            $"UPDATE tbl_gl_entry SET {assignments} WHERE entry_id=@entryId;", parameters);
    }

    private static DynamicParameters Parameters(string[] columns, JsonElement row)
    {
        var parameters = new DynamicParameters();
        for (int i = 0; i < columns.Length; i++)
        {
            JsonElement value = Required(row, columns[i]);
            object? dbValue = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => columns[i] == "gl_uuid" ? value.GetString()!.Trim() : value.GetString(),
                JsonValueKind.Number when value.TryGetInt64(out long n) => n,
                JsonValueKind.Number when value.TryGetDecimal(out decimal d) => d,
                _ => throw Invalid($"قيمة غير صالحة للحقل {columns[i]}.")
            };
            parameters.Add("p" + i, dbValue);
        }
        return parameters;
    }

    // يستخدم أيضًا عند تجهيز الحزمة، حتى لا نسجل سندًا ناقصًا في طابور الإرسال.
    internal static VoucherSnapshot Validate(JsonElement payload, string uuid)
    {
        JsonElement header = Required(payload, "Header");
        JsonElement details = Required(payload, "Details");
        if (header.ValueKind != JsonValueKind.Object || details.ValueKind != JsonValueKind.Array)
            throw Invalid("رأس السند أو تفاصيله غير صحيحة.");
        RequireColumns(header, HeaderColumns);
        if (!string.Equals(Required(header, "gl_uuid").GetString()?.Trim(), uuid, StringComparison.OrdinalIgnoreCase))
            throw Invalid("gl_uuid في الرأس مختلف عن هوية الحركة.");
        var identity = new ExistingVoucher
        {
            Number = Integer(header, "entry_gl_number"),
            GeneralNumber = Integer(header, "entry_gl_genral"),
            SetId = Integer(header, "entry_gl_id"),
            Type = Integer(header, "entry_type")
        };
        if (identity.Type is not (901 or 902 or 903 or 904) || identity.Number <= 0 ||
            identity.SetId <= 0 || identity.SetId > int.MaxValue ||
            identity.GeneralNumber <= 0 || identity.Number == long.MaxValue || identity.GeneralNumber == long.MaxValue)
            throw Invalid("نوع السند أو رقمه أو إعداده أو رقمه العام غير صحيح.");
        var rows = details.EnumerateArray().ToList();
        if (rows.Count < 2) throw Invalid("السند يحتاج قيودًا مدينة ودائنة مكتملة.");
        decimal debit = 0, credit = 0;
        foreach (JsonElement row in rows)
        {
            RequireColumns(row, DetailColumns);
            if (Integer(row, "gl_inv_id") != identity.Number ||
                Integer(row, "gl_inv_set_id") != identity.SetId ||
                Integer(row, "gl_set_idinvo") != identity.Type ||
                Integer(row, "gl_num_general") != identity.GeneralNumber ||
                Integer(row, "gl_b_id") != Integer(header, "entry_b_id") ||
                Integer(row, "gl_savein_b_id") != Integer(header, "entry_save_b_id"))
                throw Invalid("يوجد قيد لا يطابق رقم السند أو نوعه أو إعداداته أو بيانات فرعه.");
            decimal d = Amount(row, "gl_debit"), c = Amount(row, "gl_credit");
            if (Integer(row, "gl_ac_id") <= 0 || d < 0 || c < 0 || (d > 0 && c > 0 && identity.Type is (901 or 902)))
                throw Invalid("يوجد حساب أو مبلغ غير صحيح في قيود السند.");
            debit += d;
            credit += c;
        }
        if (debit != credit) throw Invalid("قيود السند غير متوازنة؛ مجموع المدين لا يساوي الدائن.");
        return new VoucherSnapshot(header, rows, identity);
    }

    private static void RequireColumns(JsonElement row, IEnumerable<string> columns)
    {
        foreach (string column in columns)
        {
            JsonElement value = Required(row, column);
            if (value.ValueKind == JsonValueKind.Null && column is not
                ("entry_desc" or "entry_ref" or "gl_desc" or "gl_debit" or "gl_credit"))
                throw Invalid($"الحقل {column} لا يقبل قيمة فارغة.");
            if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number))
                throw Invalid($"قيمة غير صالحة للحقل {column}.");
            if (column is "entry_desc" or "entry_ref" or "gl_desc" or "gl_uuid")
            {
                if (value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.String ||
                    value.GetString()!.Length > (column == "gl_uuid" ? 150 : 255))
                    throw Invalid($"النص في {column} غير صحيح أو أطول من سعة الحقل.");
            }
            else if (column.EndsWith("_datetime", StringComparison.Ordinal))
            {
                if (!DateTime.TryParseExact(value.ToString(),
                    ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid($"التاريخ والوقت في {column} غير صحيحين.");
            }
            else if (column.EndsWith("_date", StringComparison.Ordinal))
            {
                if (!DateTime.TryParseExact(value.ToString(),
                    ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid($"التاريخ في {column} غير صحيح.");
            }
            else if (column.EndsWith("_time", StringComparison.Ordinal))
            {
                if (!TimeSpan.TryParseExact(value.ToString(), @"hh\:mm\:ss",
                    CultureInfo.InvariantCulture, out _))
                    throw Invalid($"الوقت في {column} غير صحيح.");
            }
            else if (column is "entry_credit" or "entry_debit" or "entry_disc" or "entry_total" or
                "entry_value_tax" or "entry_percent_tax" or "entry_net_after_tax" or
                "entry_debit_profit" or "entry_credit_profit" or "gl_debit" or "gl_credit")
            {
                decimal amount = Amount(row, column);
                if (column == "entry_percent_tax" && decimal.Round(amount, 2) != amount)
                    throw Invalid("نسبة الضريبة تقبل منزلتين عشريتين كحد أقصى.");
            }
            else
            {
                long number = Integer(row, column);
                if (number < 0)
                    throw Invalid($"الحقل {column} لا يقبل رقمًا سالبًا.");
                if ((column is "entry_type" or "entry_gl_id" or "entry_user_id" or "entry_cashc_id" or
                    "gl_inv_set_id" or "gl_set_idinvo" or "gl_savein_b_id" or "gl_b_id") && number > int.MaxValue)
                    throw Invalid($"الحقل {column} يتجاوز سعة INT.");
            }
        }
    }

    private static decimal Amount(JsonElement row, string name)
    {
        JsonElement value = Required(row, name);
        if (value.ValueKind == JsonValueKind.Null) return 0;
        if (!decimal.TryParse(value.ToString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal number) || decimal.Round(number, 3) != number)
            throw Invalid($"الحقل {name} يجب أن يكون مبلغًا بثلاث منازل عشرية كحد أقصى.");
        return number;
    }

    private static long Integer(JsonElement row, string name)
    {
        decimal value = Amount(row, name);
        if (value != decimal.Truncate(value) || value < long.MinValue || value > long.MaxValue)
            throw Invalid($"الحقل {name} يجب أن يكون رقمًا صحيحًا ضمن المجال المسموح.");
        return (long)value;
    }

    private static JsonElement Required(JsonElement row, string name)
    {
        if (row.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in row.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw Invalid($"الحقل {name} غير موجود في حزمة السند.");
    }

    private static InvalidOperationException Invalid(string message) =>
        new("INVALID_VOUCHER_PAYLOAD|" + message);

    internal sealed record VoucherSnapshot(JsonElement Header, List<JsonElement> Details, ExistingVoucher Identity);
    internal sealed class ExistingVoucher
    {
        public long EntryId { get; set; }
        public long Number { get; set; }
        public long GeneralNumber { get; set; }
        public long SetId { get; set; }
        public long Type { get; set; }
    }
}
