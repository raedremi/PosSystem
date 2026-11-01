using System.Data;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.CustomerSupplierSync;

/// <summary>تجهيز نسخة حالية للاستبدال من شاشة السجلات.</summary>
public sealed class CustomerPackageService
{
    private readonly SyncSettings _settings;
    public CustomerPackageService(SyncSettings settings) => _settings = settings;

    public async Task<long> BuildAndQueueAsync(long customerId, int operation, string? expectedUuid = null)
    {
        if (operation is not (1 or 2 or 4)) throw new InvalidOperationException("الحذف يسجل من رصيد بعد نجاحه.");
        string uuid = CustomerSyncContract.Uuid(customerId);
        if (expectedUuid != null && !uuid.Equals(expectedUuid.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("هوية البطاقة لا تطابق حركة السجل.");
        await using var connection = CreateConnection(_settings);
        await connection.OpenAsync();
        var customer = await ReadRowAsync(connection, "tbl_cust", "cust_id", customerId, CustomerSyncContract.CustomerFields);
        var account = await ReadRowAsync(connection, "tbl_account", "acc_id", CustomerSyncContract.Number(customer["acc_id"]), CustomerSyncContract.AccountFields);
        string payload = JsonSerializer.Serialize(new { Version = 1, Customer = customer, Account = account,
            Metadata = new { DocumentName = (CustomerSyncContract.Number(customer["cust_type"]) == 2 ? "مورد: " : "عميل: ") + customer["cust_name"], DocumentNumber = customerId } });
        using var document = JsonDocument.Parse(payload);
        CustomerSyncContract.Parse(uuid, operation, document.RootElement);
        const string sql = @"INSERT INTO tbl_sync_send
            (event_uuid,source_device_uuid,entity_type,entity_uuid,local_id,operation_type,payload,sync_status,retry_count,created_at)
            VALUES (@event,@device,'CustomerSupplier',@uuid,@id,@operation,@payload,0,0,NOW()); SELECT LAST_INSERT_ID();";
        return await connection.ExecuteScalarAsync<long>(sql, new { @event = Guid.NewGuid().ToString(), device = _settings.DeviceUuid, uuid, id = customerId, operation, payload });
    }

    /// <summary>لا نرسل الحذف المحضر إلا إذا تأكد حذف البطاقة والحساب؛ لا نكمل حذفًا جزئيًا تلقائيًا.</summary>
    public async Task RecoverPreparedDeletesAsync()
    {
        await using var db = CreateConnection(_settings);
        await db.OpenAsync();
        var rows = await db.QueryAsync<SyncSendRow>(@"SELECT sync_id SyncId,event_uuid EventUuid,
            source_device_uuid SourceDeviceUuid,entity_type EntityType,entity_uuid EntityUuid,
            local_id LocalId,operation_type OperationType,payload Payload,created_at CreatedAt
            FROM tbl_sync_send WHERE entity_type='CustomerSupplier' AND operation_type=3 AND sync_status=1
            ORDER BY sync_id LIMIT 100");
        var repository = new RasidSync.Services.LocalSyncRepository(_settings);
        foreach (var row in rows)
        {
            CustomerPackage p;
            try
            {
                using var doc = JsonDocument.Parse(row.Payload);
                p = CustomerSyncContract.Parse(row.EntityUuid, 3, doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                await repository.MarkBlockedAsync(row, "INVALID_CUSTOMER_PAYLOAD", ex.Message);
                continue;
            }
            int customers = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_cust WHERE cust_id=@id", new { id = p.CustomerId });
            int accounts = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_account WHERE acc_id=@id", new { id = p.AccountId });
            if (customers == 0 && accounts == 0)
                await db.ExecuteAsync("UPDATE tbl_sync_send SET sync_status=0,last_error=NULL WHERE sync_id=@id AND sync_status=1", new { id = row.SyncId });
            else if (customers == 1 && accounts == 1)
                await db.ExecuteAsync("UPDATE tbl_sync_send SET sync_status=4,last_error='لم ينفذ الحذف محليًا؛ أعد الحذف من رصيد عند الحاجة.' WHERE sync_id=@id AND sync_status=1", new { id = row.SyncId });
            else
                await repository.MarkBlockedAsync(row, "CUSTOMER_PARTIAL_DELETE", "حذف محلي جزئي؛ يجب فحص البطاقة والحساب قبل إعادة المحاولة.");
        }
    }

    private static async Task<Dictionary<string, object?>> ReadRowAsync(IDbConnection db, string table, string key, long id, string[] fields)
    {
        var rows = (await db.QueryAsync("SELECT " + string.Join(",", fields.Select(x => "`" + x + "`")) + " FROM " + table + " WHERE " + key + "=@id LIMIT 2", new { id })).ToList();
        if (rows.Count != 1) throw new InvalidOperationException("البطاقة أو حسابها غير موجود أو مكرر محليًا.");
        var row = new Dictionary<string, object?>((IDictionary<string, object?>)rows[0], StringComparer.OrdinalIgnoreCase);
        foreach (string field in fields)
            if (row[field] is DateTime date) row[field] = date.ToString("yyyy-MM-ddTHH:mm:ss");
        return row;
    }

    public static MySqlConnection CreateConnection(SyncSettings settings) => new(new MySqlConnectionStringBuilder
    {
        Server = settings.LocalServer, Port = settings.LocalPort, Database = settings.LocalDatabase,
        UserID = settings.LocalUsername, Password = settings.LocalPassword,
        ConnectionTimeout = 10, DefaultCommandTimeout = 60, ConvertZeroDateTime = true
    }.ConnectionString);
}
