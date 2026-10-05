using System.Text.Json;
using MySqlConnector;
using RasidSync.Models;
using RasidSync.Services;

namespace RasidSync.Vouchers;

public sealed class VoucherPackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly SyncSettings _settings;

    public VoucherPackageService(SyncSettings settings)
    {
        _settings = settings;
    }

    public async Task<VoucherQueueResult> BuildAndQueueAsync(long entryId, int operationType, string? expectedUuid = null)
    {
        // العمليات المدعومة: إضافة، تعديل، حذف، واستبدال كامل.
        if (operationType is not (1 or 2 or 3 or 4))
            throw new InvalidOperationException("عملية المزامنة يجب أن تكون من 1 إلى 4.");

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();

        Dictionary<string, object?>? header = await ReadSingleAsync(
            connection, "SELECT * FROM tbl_gl_entry WHERE entry_id=@entry_id LIMIT 1;",
            ("@entry_id", entryId));
        if (header is null)
            throw new InvalidOperationException($"لم يتم العثور على سند ID={entryId}.");

        string voucherUuid = Convert.ToString(header["gl_uuid"])?.Trim() ?? string.Empty;
        if (expectedUuid is not null && !string.Equals(voucherUuid, expectedUuid.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("تغيرت هوية المستند المحلي؛ لم تسجل حركة إعادة المزامنة.");
        if (!Guid.TryParse(voucherUuid, out _))
            throw new InvalidOperationException("السند لا يحتوي على gl_uuid صحيح.");
        int type = Convert.ToInt32(header["entry_type"]);
        if (type is not (901 or 902 or 903 or 904))
            throw new InvalidOperationException("الأنواع المدعومة: قبض 901، دفع 902، قيد 903، وقيد افتتاحي 904.");

        List<Dictionary<string, object?>> details = [];
        if (operationType != 3)
        {
            details = await ReadListAsync(connection,
                """
                SELECT * FROM tbl_gl
                WHERE gl_inv_id=@number AND gl_inv_set_id=@setId
                  AND gl_set_idinvo=@type AND gl_num_general=@general
                  AND gl_b_id=@branch AND gl_savein_b_id=@saveBranch
                ORDER BY gl_id;
                """,
                ("@number", header["entry_gl_number"]),
                ("@setId", header["entry_gl_id"]), ("@type", type),
                ("@general", header["entry_gl_genral"]),
                ("@branch", header["entry_b_id"]), ("@saveBranch", header["entry_save_b_id"]));
        }

        var metadata = await SyncDocumentDisplay.ReadMetadataAsync(connection, true, header["entry_gl_id"]!, header["entry_gl_number"]!, type);
        string payload = JsonSerializer.Serialize(new { Header = header, Details = details, Metadata = metadata }, JsonOptions);
        if (operationType != 3)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            VoucherApplyEngine.Validate(document.RootElement, voucherUuid);
        }
        // الحذف لا يتطلب وجود التفاصيل. نحفظ UUID قبل حذف الرأس في رصيد لاحقًا.
        long syncId = await InsertQueueEventAsync(connection, entryId, voucherUuid, payload, operationType);
        return new VoucherQueueResult(syncId, entryId, voucherUuid, details.Count);
    }

    private async Task<long> InsertQueueEventAsync(
        MySqlConnection connection,
        long entryId,
        string voucherUuid,
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
                @event_uuid, @source_device_uuid, 'Voucher', @entity_uuid,
                @local_id, @operation_type, @payload, 0, 0, NOW()
            );
            SELECT LAST_INSERT_ID();
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@event_uuid", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@source_device_uuid", _settings.DeviceUuid);
        command.Parameters.AddWithValue("@entity_uuid", voucherUuid);
        command.Parameters.AddWithValue("@local_id", entryId);
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


public sealed record VoucherQueueResult(long SyncId, long EntryId, string VoucherUuid, int DetailCount);


