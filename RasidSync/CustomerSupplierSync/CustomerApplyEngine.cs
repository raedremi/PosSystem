using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;

namespace RasidSync.CustomerSupplierSync;

/// <summary>نفس الكود في API والاستقبال المحلي. لا يعيد إرسال الحركة ولا ينقل الرصيد.</summary>
public static class CustomerApplyEngine
{
    public static async Task<string> ApplyAsync(IDbConnection connection, string eventUuid, string uuid, int operation, JsonElement payload)
    {
        if (!Guid.TryParse(eventUuid, out _)) throw CustomerSyncContract.Error("INVALID_CUSTOMER_PAYLOAD", "event_uuid غير صحيح.");
        CustomerPackage package = CustomerSyncContract.Parse(uuid, operation, payload);
        string lockName = "custsync:" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(connection.Database)));
        int? locked = await connection.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@name, 10)", new { name = lockName });
        if (locked != 1) throw new TimeoutException("تعذر الحصول على قفل مزامنة العملاء؛ أعد المحاولة.");
        try
        {
            await connection.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS tbl_sync_customer_applied (
                event_uuid CHAR(36) NOT NULL PRIMARY KEY, customer_id BIGINT NOT NULL,
                operation_type INT NOT NULL, payload_hash CHAR(64) NOT NULL, applied_at DATETIME NOT NULL)
                ENGINE=MyISAM DEFAULT CHARSET=utf8;");
            string payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
            var receipt = await connection.QuerySingleOrDefaultAsync<AppliedReceipt>(
                "SELECT customer_id CustomerId, operation_type OperationType, payload_hash PayloadHash FROM tbl_sync_customer_applied WHERE event_uuid=@eventUuid", new { eventUuid });
            if (receipt != null)
            {
                if (receipt.CustomerId != package.CustomerId || receipt.OperationType != operation || receipt.PayloadHash != payloadHash)
                    throw CustomerSyncContract.Error("CUSTOMER_ID_CONFLICT", "event_uuid مستخدم لحزمة مختلفة.");
                return "حركة العميل مطبقة سابقًا؛ لم تنفذ مرة ثانية.";
            }
            await CheckSchemaAsync(connection, "tbl_cust", CustomerSyncContract.CustomerFields);
            await CheckSchemaAsync(connection, "tbl_account", CustomerSyncContract.AccountFields.Concat(
                new[] { "acc_debt", "acc_credt", "acc_balance" }));
            var customer = await ReadAsync(connection, "tbl_cust", "cust_id", package.CustomerId);
            var account = await ReadAsync(connection, "tbl_account", "acc_id", package.AccountId);
            CheckExisting(package, customer, account);
            if (operation == 3)
            {
                await DeleteAsync(connection, package, account);
                await RecordAppliedAsync(connection, eventUuid, package.CustomerId, operation, payloadHash);
                return "تم حذف البطاقة وحسابها أو تأكيد حذفهما السابق.";
            }

            // جميع الفحوص تسبق أي كتابة؛ تكرار المحاولة يعالج الإدخال الجزئي مع MyISAM.
            await CheckNumbersAsync(connection, package);
            await CheckPriceAsync(connection, package);
            await WriteAsync(connection, "tbl_account", "acc_id", package.AccountId, package.Account, account != null,
                new Dictionary<string, object?> { ["acc_debt"] = 0, ["acc_credt"] = 0, ["acc_balance"] = 0 });
            await WriteAsync(connection, "tbl_cust", "cust_id", package.CustomerId, package.Customer, customer != null);
            await RecordAppliedAsync(connection, eventUuid, package.CustomerId, operation, payloadHash);
            return "تم تطبيق بطاقة العميل أو المورد وحسابها مع الحفاظ على المعرفات والرصيد المحلي.";
        }
        finally
        {
            // عدم إخفاء نتيجة التطبيق بسبب انتهاء الاتصال أثناء تحرير القفل.
            try { await connection.ExecuteScalarAsync<int?>("SELECT RELEASE_LOCK(@name)", new { name = lockName }); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); }
        }
    }

    private static void CheckExisting(CustomerPackage p, Dictionary<string, object?>? customer, Dictionary<string, object?>? account)
    {
        if (customer != null && (CustomerSyncContract.Number(customer["acc_id"]) != p.AccountId ||
            CustomerSyncContract.Number(customer["cust_type"]) != CustomerSyncContract.Number(p.Customer["cust_type"])))
            throw CustomerSyncContract.Error("CUSTOMER_ID_CONFLICT", "معرف البطاقة مستخدم بربط أو نوع مختلف.");
        // هوية الحساب وتسلسل الدليل ثابتان؛ تغييرهما يحتاج معالجة مستقلة.
        if (account != null)
            foreach (string key in new[] { "acc_no", "acc_perent_no", "acc_level", "acc_type", "acc_customer" })
                if (CustomerSyncContract.Number(account[key]) != CustomerSyncContract.Number(p.Account[key]))
                    throw CustomerSyncContract.Error("CUSTOMER_ACCOUNT_CONFLICT", "معرف الحساب مستخدم ببيانات دليل مختلفة: " + key);
    }

    private static async Task CheckNumbersAsync(IDbConnection db, CustomerPackage p)
    {
        var args = new { id = p.AccountId, customer = p.CustomerId, number = p.Account["acc_no"], parent = p.Account["acc_perent_no"] };
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_account WHERE acc_no=@number AND acc_id<>@id", args) != 0)
            throw CustomerSyncContract.Error("CUSTOMER_ACCOUNT_CONFLICT", "رقم دليل الحساب مستخدم لحساب آخر.");
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_account WHERE acc_no=@parent AND acc_id<>@id", args) != 1)
            throw CustomerSyncContract.Error("CUSTOMER_PARENT_MISSING", "الحساب الأب مفقود أو مكرر في القاعدة المستقبلة.");
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_cust WHERE acc_id=@id AND cust_id<>@customer", args) != 0)
            throw CustomerSyncContract.Error("CUSTOMER_ACCOUNT_CONFLICT", "الحساب مربوط ببطاقة أخرى.");
    }

    private static async Task CheckPriceAsync(IDbConnection db, CustomerPackage p)
    {
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_typeprice WHERE t_p_id=@price", new { price = p.Customer["cust_typeprice"] }) != 1)
            throw CustomerSyncContract.Error("CUSTOMER_PRICE_MISSING", "نوع السعر غير موجود في القاعدة المستقبلة.");
    }

    private static async Task DeleteAsync(IDbConnection db, CustomerPackage p, Dictionary<string, object?>? account)
    {
        if (account != null && new[] { "acc_debt", "acc_credt", "acc_balance" }.Any(key => Convert.ToDecimal(account[key]) != 0))
            throw CustomerSyncContract.Error("CUSTOMER_DELETE_IN_USE", "الحساب يحمل رصيدًا محليًا؛ لم يتم الحذف.");
        // نفحص القيود حتى عند غياب الحساب بسبب حذف جزئي سابق.
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_gl WHERE gl_ac_id=@id", new { id = p.AccountId }) != 0 ||
            await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_cust WHERE acc_id=@id AND cust_id<>@customer", new { id = p.AccountId, customer = p.CustomerId }) != 0)
            throw CustomerSyncContract.Error("CUSTOMER_DELETE_IN_USE", "الحساب مستخدم في قيود أو بطاقة أخرى؛ لم يتم الحذف.");
        if (account != null && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_account WHERE acc_perent_no=@number AND acc_id<>@id",
            new { number = account["acc_no"], id = p.AccountId }) != 0)
            throw CustomerSyncContract.Error("CUSTOMER_DELETE_IN_USE", "الحساب أب لحسابات أخرى.");
        await CheckReferencesAsync(db, p);
        await db.ExecuteAsync("DELETE FROM tbl_cust WHERE cust_id=@customer AND acc_id=@id", new { customer = p.CustomerId, id = p.AccountId });
        await db.ExecuteAsync("DELETE FROM tbl_account WHERE acc_id=@id", new { id = p.AccountId });
    }

    private static async Task CheckReferencesAsync(IDbConnection db, CustomerPackage p)
    {
        // أسماء مراجع رصيد المعروفة + أي FK معلن. لا نحذف السجلات التابعة المالية.
        const string sql = @"SELECT DISTINCT c.TABLE_NAME TableName, c.COLUMN_NAME ColumnName,
            CASE WHEN c.COLUMN_NAME='cust_id' OR k.REFERENCED_COLUMN_NAME='cust_id' THEN 1 ELSE 0 END IsCustomer
            FROM information_schema.COLUMNS c
            LEFT JOIN information_schema.KEY_COLUMN_USAGE k ON k.TABLE_SCHEMA=c.TABLE_SCHEMA
              AND k.TABLE_NAME=c.TABLE_NAME AND k.COLUMN_NAME=c.COLUMN_NAME
            WHERE c.TABLE_SCHEMA=DATABASE() AND c.TABLE_NAME NOT IN ('tbl_cust','tbl_account')
              AND (c.COLUMN_NAME IN ('acc_id','cust_id','gl_ac_id','inv_acc_id','inv_acc','inv_acc2',
                   'inv_acc_cash','inv_acc_disc','inv_acc_tax','entry_acc_def','entry_acc2','entry_acc_disc','entry_acc_tax')
                   OR c.COLUMN_NAME LIKE '%\\_acc\\_id' OR c.COLUMN_NAME LIKE '%\\_accountid'
                   OR (k.REFERENCED_TABLE_NAME='tbl_cust' AND k.REFERENCED_COLUMN_NAME='cust_id')
                   OR (k.REFERENCED_TABLE_NAME='tbl_account' AND k.REFERENCED_COLUMN_NAME='acc_id'));";
        var references = await db.QueryAsync<ReferenceColumn>(sql);
        foreach (var reference in references)
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM " + Quote(reference.TableName) + " WHERE " +
                Quote(reference.ColumnName) + "=@id", new { id = reference.IsCustomer == 1 ? p.CustomerId : p.AccountId }) != 0)
                throw CustomerSyncContract.Error("CUSTOMER_DELETE_IN_USE", "يوجد ارتباط في " + reference.TableName + "." + reference.ColumnName);
    }

    private static async Task CheckSchemaAsync(IDbConnection db, string table, IEnumerable<string> fields)
    {
        var columns = (await db.QueryAsync<string>("SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table",
            new { table })).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string field in fields)
            if (!columns.Contains(field)) throw CustomerSyncContract.Error("CUSTOMER_SCHEMA_MISMATCH", "حقل مفقود: " + table + "." + field);
    }

    private static async Task<Dictionary<string, object?>?> ReadAsync(IDbConnection db, string table, string key, long id)
    {
        var rows = (await db.QueryAsync("SELECT * FROM " + Quote(table) + " WHERE " + Quote(key) + "=@id LIMIT 2", new { id })).ToList();
        if (rows.Count > 1) throw CustomerSyncContract.Error("CUSTOMER_ID_CONFLICT", "معرف مكرر في " + table);
        return rows.Count == 0 ? null : new Dictionary<string, object?>((IDictionary<string, object?>)rows[0], StringComparer.OrdinalIgnoreCase);
    }

    private static Task<int> WriteAsync(IDbConnection db, string table, string key, long id, Dictionary<string, object?> row,
        bool exists, Dictionary<string, object?>? insertOnly = null)
    {
        var values = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
        if (!exists && insertOnly != null) foreach (var pair in insertOnly) values.Add(pair.Key, pair.Value);
        var args = new DynamicParameters();
        var columns = values.Keys.ToList();
        for (int i = 0; i < columns.Count; i++) args.Add("p" + i, values[columns[i]]);
        args.Add("id", id);
        string sql = exists
            ? "UPDATE " + Quote(table) + " SET " + string.Join(",", columns.Select((name, i) => (name, i)).Where(x => !x.name.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(x => Quote(x.name) + "=@p" + x.i)) + " WHERE " + Quote(key) + "=@id"
            : "INSERT INTO " + Quote(table) + " (" + string.Join(",", columns.Select(Quote)) + ") VALUES (" + string.Join(",", columns.Select((_, i) => "@p" + i)) + ")";
        return db.ExecuteAsync(sql, args);
    }

    private static Task<int> RecordAppliedAsync(IDbConnection db, string eventUuid, long id, int operation, string hash) =>
        db.ExecuteAsync("INSERT INTO tbl_sync_customer_applied (event_uuid,customer_id,operation_type,payload_hash,applied_at) VALUES (@eventUuid,@id,@operation,@hash,NOW())",
            new { eventUuid, id, operation, hash });

    private sealed class AppliedReceipt
    {
        public long CustomerId { get; set; }
        public int OperationType { get; set; }
        public string PayloadHash { get; set; } = "";
    }

    private static string Quote(string name) => "`" + name.Replace("`", "``") + "`";
    private sealed class ReferenceColumn
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public int IsCustomer { get; set; }
    }
}
