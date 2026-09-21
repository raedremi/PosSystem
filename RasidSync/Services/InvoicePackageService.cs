using System.Text.Json;
using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Services;

public sealed class InvoicePackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly SyncSettings _settings;

    public InvoicePackageService(SyncSettings settings)
    {
        _settings = settings;
    }

    public async Task<InvoiceQueueResult> BuildAndQueueAsync(long invoiceId, int operationType)
    {
        // في هذه المرحلة ندعم: إضافة، تعديل، واستبدال كامل.
        if (operationType is not (1 or 2 or 4))
            throw new InvalidOperationException("عملية المزامنة يجب أن تكون 1 أو 2 أو 4.");

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();

        Dictionary<string, object?>? header = await ReadSingleAsync(
            connection,
            "SELECT * FROM tbl_invoice WHERE inv_id = @invoice_id LIMIT 1;",
            ("@invoice_id", invoiceId));

        if (header is null)
            throw new InvalidOperationException($"لم يتم العثور على فاتورة برقم ID = {invoiceId}.");

        string invoiceUuid = Convert.ToString(header["inv_uuid"])?.Trim() ?? string.Empty;
        if (!Guid.TryParse(invoiceUuid, out _))
            throw new InvalidOperationException("الفاتورة لا تحتوي على inv_uuid صحيح.");

        long invoiceNumber = Convert.ToInt64(header["inv_num"]);
        int invoiceSetId = Convert.ToInt32(header["inv_set_id"]);

        List<Dictionary<string, object?>> details = await ReadListAsync(
            connection,
            """
            SELECT *
            FROM tbl_invoice_det
            WHERE det_inv_num = @inv_num
              AND det_inv_set_id = @inv_set_id
            ORDER BY det_id;
            """,
            ("@inv_num", invoiceNumber),
            ("@inv_set_id", invoiceSetId));

        if (details.Count == 0)
            throw new InvalidOperationException("رأس الفاتورة موجود لكن لم يتم العثور على تفاصيلها.");

        List<Dictionary<string, object?>> payments = await ReadListAsync(
            connection,
            """
            SELECT *
            FROM tbl_invoice_pay
            WHERE invpay_invoice_uuid = @invoice_uuid
            ORDER BY invpay_id;
            """,
            ("@invoice_uuid", invoiceUuid));

        Dictionary<string, object?>? due = await ReadSingleAsync(
            connection,
            """
            SELECT *
            FROM tbl_invoice_due
            WHERE due_uuid = @invoice_uuid
            LIMIT 1;
            """,
            ("@invoice_uuid", invoiceUuid));

        var package = new InvoiceSyncPackage
        {
            Header = header,
            Details = details,
            Payments = payments,
            Due = due
        };

        string payload = JsonSerializer.Serialize(package, JsonOptions);
        long syncId = await InsertQueueEventAsync(
            connection,
            invoiceId,
            invoiceUuid,
            payload,
            operationType);

        return new InvoiceQueueResult
        {
            SyncId = syncId,
            InvoiceId = invoiceId,
            InvoiceUuid = invoiceUuid,
            DetailCount = details.Count,
            PaymentCount = payments.Count,
            HasDue = due is not null
        };
    }

    private async Task<long> InsertQueueEventAsync(
        MySqlConnection connection,
        long invoiceId,
        string invoiceUuid,
        string payload,
        int operationType)
    {
        const string sql = """
            INSERT INTO tbl_sync_send
            (
                event_uuid, source_device_uuid, entity_type, entity_uuid,
                local_id, operation_type, payload, sync_status,
                retry_count, created_at
            )
            VALUES
            (
                @event_uuid, @source_device_uuid, 'Invoice', @entity_uuid,
                @local_id, @operation_type, @payload, 0, 0, NOW()
            );
            SELECT LAST_INSERT_ID();
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@event_uuid", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@source_device_uuid", _settings.DeviceUuid);
        command.Parameters.AddWithValue("@entity_uuid", invoiceUuid);
        command.Parameters.AddWithValue("@local_id", invoiceId);
        command.Parameters.AddWithValue("@operation_type", operationType);
        command.Parameters.AddWithValue("@payload", payload);

        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task<Dictionary<string, object?>?> ReadSingleAsync(
        MySqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync();

        return await reader.ReadAsync() ? ReadCurrentRow(reader) : null;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadListAsync(
        MySqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var result = new List<Dictionary<string, object?>>();

        await using var command = CreateCommand(connection, sql, parameters);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            result.Add(ReadCurrentRow(reader));

        return result;
    }

    private static MySqlCommand CreateCommand(
        MySqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = new MySqlCommand(sql, connection);

        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        return command;
    }

    private static Dictionary<string, object?> ReadCurrentRow(MySqlDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < reader.FieldCount; index++)
        {
            object? value = reader.IsDBNull(index) ? null : reader.GetValue(index);

            if (value is DateTime dateTime)
                value = dateTime.ToString("yyyy-MM-ddTHH:mm:ss");
            else if (value is TimeSpan time)
                value = time.ToString(@"hh\:mm\:ss");
            else if (value is byte[] bytes)
                value = Convert.ToBase64String(bytes);

            row[reader.GetName(index)] = value;
        }

        return row;
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
            ConvertZeroDateTime = true
        };

        return new MySqlConnection(builder.ConnectionString);
    }
}
