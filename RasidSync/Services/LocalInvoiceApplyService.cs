using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using RasidSync.Models;
using Dapper;
using MySqlConnector;

namespace RasidSync.Services;

/// <summary>
/// تطبيق مستقل لفواتير المزامنة. لا يستدعي حفظ Android أو ZATCA.
/// </summary>
public sealed class LocalInvoiceApplyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly SyncSettings _settings;

    public LocalInvoiceApplyService(SyncSettings settings) => _settings = settings;

    public async Task<InvoiceApplyResult> ApplyAsync(
        PulledSyncEvent syncRequest)
    {
        InvoicePackage package = BuildPackage(syncRequest);
        InvoiceResyncHeader header = package.Model.Invoice;
        string uuid = header.p_uuid!.Trim();
        long invNum = GetRequiredInt64(package.Header, "inv_num");

        using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            ExistingInvoice? existing = await FindExistingAsync(connection, transaction, uuid);
            await EnsureNumberAvailableAsync(
                connection, transaction, existing?.InvoiceId,
                invNum, header.p_set_id, header.p_inv_set_idinvo);

            // نحتفظ بالأصناف القديمة حتى يعاد حسابها بعد إزالة التفاصيل.
            List<AffectedStock> affected = await ReadAffectedStocksAsync(
                connection, transaction, existing);
            AddNewAffectedStocks(affected, package.Model.Details);

            long invoiceId;
            if (existing == null)
            {
                await DeleteIdentityRowsAsync(
                    connection, transaction, invNum, header.p_set_id,
                    header.p_inv_set_idinvo, uuid);

                await InsertJsonRowAsync(
                    connection, transaction, "tbl_invoice",
                    package.Header, new Dictionary<string, object?>(), "inv_id");

                invoiceId = await connection.ExecuteScalarAsync<long>(
                    "SELECT LAST_INSERT_ID();", transaction: transaction);
            }
            else
            {
                invoiceId = existing.InvoiceId;
                await DeleteOldEffectsAsync(connection, transaction, existing, uuid);

                await UpdateJsonRowAsync(
                    connection, transaction, "tbl_invoice", package.Header,
                    new Dictionary<string, object?>(), "inv_id", invoiceId, "inv_id");
            }

            await InsertDetailsAsync(
                connection, transaction, package.Details,
                invNum, header.p_set_id, header.p_inv_set_idinvo);

            await InsertPaymentsAndDueAsync(
                connection, transaction, package,
                uuid, invNum, header.p_set_id, header.p_inv_set_idinvo);

            await RebuildPurchaseHistoryAsync(
                connection, transaction, invoiceId, header, package.Model.Details);

            // إعادة الحساب الكامل تجعل تكرار المحاولة آمناً مع MyISAM.
            await RecalculateStocksAsync(connection, transaction, affected);

            await LocalInvoiceJournalService.CreateAsync(
                connection, transaction, CreateJournalData(header, invNum));

            await transaction.CommitAsync();

            return new InvoiceApplyResult
            {
                InvoiceId = invoiceId,
                InvNum = Convert.ToInt32(invNum),
                Message = "تم نسخ الفاتورة وإعادة بناء التفاصيل والمخزون والسند بنجاح."
            };
        }
        catch
        {
            // MyISAM لا يضمن Rollback؛ المحاولة القادمة تعيد البناء من UUID نفسه.
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static InvoicePackage BuildPackage(PulledSyncEvent request)
    {
        if (!string.Equals(request.EntityType, "Invoice", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("نوع الحركة ليس فاتورة.");
        // الإضافة والتعديل والاستبدال تستخدم JSON كاملاً ونفس إعادة البناء الآمنة.
        if (request.OperationType is not (1 or 2 or 4))
            throw new InvalidOperationException("نوع عملية الفاتورة غير مدعوم حاليًا.");

        JsonElement headerJson = Required(request.Payload, "Header");
        JsonElement detailsJson = Required(request.Payload, "Details");
        if (headerJson.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("رأس الفاتورة غير صحيح.");
        if (detailsJson.ValueKind != JsonValueKind.Array || detailsJson.GetArrayLength() == 0)
            throw new InvalidOperationException("تفاصيل الفاتورة غير موجودة.");

        var model = new InvoiceResyncRequest
        {
            Invoice = MapHeader(headerJson),
            Details = detailsJson.EnumerateArray().Select(MapDetail).ToList()
        };
        Validate(model, request.EntityUuid);

        return new InvoicePackage
        {
            Header = headerJson,
            Details = detailsJson,
            Payments = Optional(request.Payload, "Payments"),
            Due = Optional(request.Payload, "Due"),
            Model = model
        };
    }

    private static async Task InsertDetailsAsync(
        IDbConnection conn, IDbTransaction tran, JsonElement details,
        long invNum, int setId, int invoiceType)
    {
        var overrides = new Dictionary<string, object?>
        {
            ["det_inv_num"] = invNum,
            ["det_inv_set_id"] = setId.ToString(CultureInfo.InvariantCulture),
            ["det_set_idinv"] = invoiceType
        };

        foreach (JsonElement detail in details.EnumerateArray())
            await InsertJsonRowAsync(
                conn, tran, "tbl_invoice_det", detail, overrides, "det_id");
    }

    private static async Task InsertPaymentsAndDueAsync(
        IDbConnection conn, IDbTransaction tran, InvoicePackage package,
        string uuid, long invNum, int setId, int invoiceType)
    {
        if (package.Payments.ValueKind == JsonValueKind.Array)
        {
            var overrides = new Dictionary<string, object?>
            {
                ["invpay_invoice_uuid"] = uuid,
                ["inv_invoice_num"] = invNum.ToString(CultureInfo.InvariantCulture),
                ["inv_pay_inv_set_id"] = setId,
                ["inv_set_idinvo"] = invoiceType
            };
            foreach (JsonElement payment in package.Payments.EnumerateArray())
                await InsertJsonRowAsync(
                    conn, tran, "tbl_invoice_pay", payment, overrides, "invpay_id");
        }

        if (package.Due.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            await InsertJsonRowAsync(
                conn, tran, "tbl_invoice_due", package.Due,
                new Dictionary<string, object?>
                {
                    ["due_uuid"] = uuid,
                    ["due_inv_num"] = invNum.ToString(CultureInfo.InvariantCulture)
                }, "due_id");
    }

    private static Task DeleteOldEffectsAsync(
        IDbConnection conn, IDbTransaction tran,
        ExistingInvoice old, string uuid) =>
        conn.ExecuteAsync(
            @"DELETE FROM tbl_invoice_det
              WHERE det_inv_num=@InvNum AND det_inv_set_id=@InvoiceSetId
                AND det_set_idinv=@InvoiceType;
              DELETE FROM tbl_gl
              WHERE gl_inv_id=@InvNum AND gl_inv_set_id=@InvoiceSetId
                AND gl_set_idinvo=@InvoiceType;
              DELETE FROM item_purchase_history WHERE invoice_id=@InvoiceId;
              DELETE FROM tbl_invoice_pay WHERE invpay_invoice_uuid=@uuid;
              DELETE FROM tbl_invoice_due WHERE due_uuid=@uuid;",
            new { old.InvNum, old.InvoiceSetId, old.InvoiceType, old.InvoiceId, uuid }, tran);

    private static Task DeleteIdentityRowsAsync(
        IDbConnection conn, IDbTransaction tran,
        long invNum, int setId, int invoiceType, string uuid) =>
        conn.ExecuteAsync(
            @"DELETE FROM tbl_invoice_det
              WHERE det_inv_num=@invNum AND det_inv_set_id=@setId
                AND det_set_idinv=@invoiceType;
              DELETE FROM tbl_gl
              WHERE gl_inv_id=@invNum AND gl_inv_set_id=@setId
                AND gl_set_idinvo=@invoiceType;
              DELETE FROM tbl_invoice_pay WHERE invpay_invoice_uuid=@uuid;
              DELETE FROM tbl_invoice_due WHERE due_uuid=@uuid;",
            new { invNum, setId, invoiceType, uuid }, tran);

    private static async Task RebuildPurchaseHistoryAsync(
        IDbConnection conn, IDbTransaction tran, long invoiceId,
        InvoiceResyncHeader header, IEnumerable<InvoiceResyncDetail> details)
    {
        if (header.p_inv_set_idinvo is not (103 or 203 or 601)) return;

        foreach (InvoiceResyncDetail d in details)
        {
            decimal price = Math.Round(
                d.det_price_after_disc / d.det_un_unitequals, 3);
            await conn.ExecuteAsync(
                @"CALL InsertOrUpdateLastPurchasePrice(
                    @ItemId,@UnitId,@Qty,@Price,@At,@InvoiceId,@Type,@SetId);",
                new
                {
                    ItemId = d.det_it_id,
                    UnitId = Convert.ToInt32(d.det_un_id),
                    Qty = d.det_qty_in + d.det_qty_out,
                    Price = price,
                    At = d.det_datetime,
                    InvoiceId = invoiceId,
                    Type = header.p_inv_set_idinvo,
                    SetId = header.p_set_id
                }, tran);
        }
    }

    private static async Task RecalculateStocksAsync(
        IDbConnection conn, IDbTransaction tran, IEnumerable<AffectedStock> stocks)
    {
        foreach (AffectedStock stock in stocks.DistinctBy(x => new { x.ItemId, x.StoreId }))
        {
            await conn.ExecuteAsync("CALL RecalculateItemCost(@ItemId);", new { stock.ItemId }, tran);
            await conn.ExecuteAsync("CALL RecalculateItemProfit(@ItemId);", new { stock.ItemId }, tran);
            await conn.ExecuteAsync(
                "CALL UpdateItemStoreStock(@ItemId,@StoreId);",
                new { stock.ItemId, stock.StoreId }, tran);
        }
    }

    private static InvoiceJournalData CreateJournalData(
        InvoiceResyncHeader h, long invNum) => new()
    {
        InvoiceNumber = invNum,
        InvoiceSetId = h.p_set_id,
        InvoiceType = h.p_inv_set_idinvo,
        PaymentTypeId = h.p_inv_type_pay,
        ItemAccountId = Convert.ToInt64(h.p_inv_acc_item),
        CashAccountId = Convert.ToInt64(h.p_inv_acc_cash),
        CustomerAccountId = h.p_cus_id,
        TaxAccountId = Convert.ToInt64(h.p_inv_acc_tax),
        DiscountAccountId = Convert.ToInt64(h.p_inv_acc_disc),
        BeforeTax = h.p_inv_befortax,
        TaxValue = h.p_inv_tax,
        Net = h.p_inv_net,
        Discount = h.p_inv_disc_total,
        PaidUp = h.p_inv_paidup,
        InvoiceDateTime = h.p_inv_datetime,
        UserId = long.TryParse(h.p_inv_users, out long userId) ? userId : 0,
        BranchId = h.p_inv_b_id,
        SaveBranchId = h.p_inv_savein_b_id
    };

    // يحفظ فقط الأعمدة الموجودة فعلياً في الجدول، ولا يسمح لأسماء JSON بتكوين SQL.
    private static async Task InsertJsonRowAsync(
        IDbConnection conn, IDbTransaction tran, string table,
        JsonElement source, IReadOnlyDictionary<string, object?> overrides,
        params string[] excluded)
    {
        RowCommand row = await BuildRowAsync(conn, tran, table, source, overrides, excluded);
        string columns = string.Join(",", row.Columns.Select(Quote));
        string values = string.Join(",", row.Columns.Select((_, i) => "@p" + i));
        await conn.ExecuteAsync(
            $"INSERT INTO {Quote(table)} ({columns}) VALUES ({values});",
            row.Parameters, tran);
    }

    private static async Task UpdateJsonRowAsync(
        IDbConnection conn, IDbTransaction tran, string table,
        JsonElement source, IReadOnlyDictionary<string, object?> overrides,
        string keyColumn, object keyValue, params string[] excluded)
    {
        RowCommand row = await BuildRowAsync(conn, tran, table, source, overrides, excluded);
        string set = string.Join(",", row.Columns.Select((x, i) => $"{Quote(x)}=@p{i}"));
        row.Parameters.Add("row_key", keyValue);
        await conn.ExecuteAsync(
            $"UPDATE {Quote(table)} SET {set} WHERE {Quote(keyColumn)}=@row_key;",
            row.Parameters, tran);
    }

    private static async Task<RowCommand> BuildRowAsync(
        IDbConnection conn, IDbTransaction tran, string table,
        JsonElement source, IReadOnlyDictionary<string, object?> overrides,
        IReadOnlyCollection<string> excluded)
    {
        var actual = (await conn.QueryAsync<string>(
            @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table
              ORDER BY ORDINAL_POSITION;", new { table }, tran))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocked = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var json = source.EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
        List<string> columns = actual.Where(x =>
            !blocked.Contains(x) && (json.ContainsKey(x) || overrides.ContainsKey(x))).ToList();

        if (columns.Count == 0)
            throw new InvalidOperationException($"لا توجد أعمدة صالحة في {table}.");

        var parameters = new DynamicParameters();
        for (int i = 0; i < columns.Count; i++)
        {
            string column = columns[i];
            object? value = overrides.TryGetValue(column, out object? replacement)
                ? replacement : DbValue(column, json[column]);
            parameters.Add("p" + i, value);
        }
        return new RowCommand(columns, parameters);
    }

    private static object? DbValue(string column, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (column.EndsWith("settled_at", StringComparison.OrdinalIgnoreCase) &&
                text?.StartsWith("0001-", StringComparison.Ordinal) == true) return null;
            return text;
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out long integer)) return integer;
            if (value.TryGetDecimal(out decimal number)) return number;
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return value.GetRawText();
    }

    private static string Quote(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(x => !char.IsLetterOrDigit(x) && x != '_'))
            throw new InvalidOperationException("اسم جدول أو عمود غير صحيح.");
        return $"`{name}`";
    }

    private static async Task EnsureNumberAvailableAsync(
        IDbConnection conn, IDbTransaction tran, long? currentId,
        long invNum, int setId, int type)
    {
        ConflictInvoice? conflict = await conn.QuerySingleOrDefaultAsync<ConflictInvoice>(
            @"SELECT inv_id InvoiceId, inv_uuid InvoiceUuid
              FROM tbl_invoice
              WHERE inv_num=@invNum AND inv_set_id=@setId AND inv_set_idinvo=@type
                AND (@currentId IS NULL OR inv_id<>@currentId)
              LIMIT 1;",
            new { currentId, invNum, setId, type }, tran);
        if (conflict != null)
            throw new InvalidOperationException(
                $"INVOICE_NUMBER_CONFLICT|InvoiceNumber={invNum}|" +
                $"ExistingInvoiceId={conflict.InvoiceId}|ExistingUuid={conflict.InvoiceUuid}");
    }

    private static Task<ExistingInvoice?> FindExistingAsync(
        IDbConnection conn, IDbTransaction tran, string uuid) =>
        conn.QuerySingleOrDefaultAsync<ExistingInvoice>(
            @"SELECT inv_id InvoiceId,inv_num InvNum,inv_set_id InvoiceSetId,
                     inv_set_idinvo InvoiceType
              FROM tbl_invoice WHERE inv_uuid=@uuid LIMIT 1;",
            new { uuid }, tran);

    private static async Task<List<AffectedStock>> ReadAffectedStocksAsync(
        IDbConnection conn, IDbTransaction tran, ExistingInvoice? old)
    {
        if (old == null) return new();
        return (await conn.QueryAsync<AffectedStock>(
            @"SELECT DISTINCT det_it_id ItemId,CAST(det_store_id AS SIGNED) StoreId
              FROM tbl_invoice_det
              WHERE det_inv_num=@InvNum AND det_inv_set_id=@InvoiceSetId
                AND det_set_idinv=@InvoiceType;", old, tran)).ToList();
    }

    private static void AddNewAffectedStocks(
        ICollection<AffectedStock> stocks, IEnumerable<InvoiceResyncDetail> details)
    {
        foreach (InvoiceResyncDetail d in details)
            stocks.Add(new AffectedStock
            {
                ItemId = d.det_it_id,
                StoreId = Convert.ToInt32(d.det_store_id)
            });
    }

    private static InvoiceResyncHeader MapHeader(JsonElement json)
    {
        var target = new InvoiceResyncHeader();
        Fill(target, json, name => name switch
        {
            nameof(InvoiceResyncHeader.p_set_id) => "inv_set_id",
            nameof(InvoiceResyncHeader.p_cus_id) => "cus_id",
            nameof(InvoiceResyncHeader.p_uuid) => "inv_uuid",
            nameof(InvoiceResyncHeader.p_ck_num) => "ck_num",
            nameof(InvoiceResyncHeader.p_inv_number_order) => "inv_number_order",
            _ when name.StartsWith("p_", StringComparison.Ordinal) => name[2..],
            _ => name
        });
        return target;
    }

    private static InvoiceResyncDetail MapDetail(JsonElement json)
    {
        var target = new InvoiceResyncDetail();
        Fill(target, json, x => x);
        return target;
    }

    private static void Fill<T>(T target, JsonElement json, Func<string, string> map)
    {
        foreach (PropertyInfo property in typeof(T).GetProperties())
        {
            if (!property.CanWrite || !TryProperty(json, map(property.Name), out JsonElement value) ||
                value.ValueKind == JsonValueKind.Null) continue;
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? converted = type == typeof(string)
                ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
                : type == typeof(DateTime)
                    ? DateTime.Parse(value.ToString(), CultureInfo.InvariantCulture)
                    : type == typeof(TimeSpan)
                        ? TimeSpan.Parse(value.ToString(), CultureInfo.InvariantCulture)
                        : JsonSerializer.Deserialize(value.GetRawText(), type, JsonOptions);
            property.SetValue(target, converted);
        }
    }

    private static void Validate(InvoiceResyncRequest request, string entityUuid)
    {
        string uuid = request.Invoice.p_uuid?.Trim() ?? "";
        if (uuid.Length == 0) throw new InvalidOperationException("inv_uuid غير موجود.");
        if (!string.Equals(uuid, entityUuid?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UUID الرأس مختلف عن UUID الحركة.");
        if (request.Invoice.p_inv_set_idinvo <= 0 || request.Invoice.p_set_id <= 0)
            throw new InvalidOperationException("نوع الفاتورة أو إعدادها غير صحيح.");
        if (request.Details.Any(x => x.det_set_idinv != request.Invoice.p_inv_set_idinvo))
            throw new InvalidOperationException("يوجد بند نوعه مختلف عن الرأس.");
        if (request.Details.Any(x => x.det_un_unitequals <= 0))
            throw new InvalidOperationException("يوجد معامل وحدة غير صحيح.");
    }

    private static long GetRequiredInt64(JsonElement json, string name)
    {
        JsonElement value = Required(json, name);
        return value.ValueKind == JsonValueKind.Number
            ? value.GetInt64() : long.Parse(value.ToString(), CultureInfo.InvariantCulture);
    }

    private static JsonElement Required(JsonElement json, string name) =>
        TryProperty(json, name, out JsonElement value)
            ? value : throw new InvalidOperationException($"الحقل {name} غير موجود.");

    private static JsonElement Optional(JsonElement json, string name) =>
        TryProperty(json, name, out JsonElement value) ? value : default;

    private static bool TryProperty(JsonElement json, string name, out JsonElement value)
    {
        if (json.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in json.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private MySqlConnection CreateConnection()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = _settings.LocalServer,
            Port = _settings.LocalPort,
            Database = _settings.LocalDatabase,
            UserID = _settings.LocalUsername,
            Password = _settings.LocalPassword,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 60,
            AllowUserVariables = true
        };
        return new MySqlConnection(builder.ConnectionString);
    }

    private sealed class InvoicePackage
    {
        public JsonElement Header { get; init; }
        public JsonElement Details { get; init; }
        public JsonElement Payments { get; init; }
        public JsonElement Due { get; init; }
        public InvoiceResyncRequest Model { get; init; } = new();
    }
    private sealed class ExistingInvoice
    {
        public long InvoiceId { get; init; }
        public long InvNum { get; init; }
        public int InvoiceSetId { get; init; }
        public int InvoiceType { get; init; }
    }
    private sealed class ConflictInvoice
    {
        public long InvoiceId { get; init; }
        public string InvoiceUuid { get; init; } = string.Empty;
    }
    private sealed class AffectedStock
    {
        public int ItemId { get; init; }
        public int StoreId { get; init; }
    }
    private sealed record RowCommand(List<string> Columns, DynamicParameters Parameters);
}

public sealed class InvoiceApplyResult
{
    public bool AlreadyExists { get; init; }
    public long InvoiceId { get; init; }
    public int InvNum { get; init; }
    public string Message { get; init; } = string.Empty;
}
