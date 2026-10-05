using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Vouchers;

/// <summary>يطبق السند المستلم دون إضافة حدث جديد في tbl_sync_send.</summary>
public sealed class LocalVoucherApplyService
{
    private readonly SyncSettings _settings;
    public LocalVoucherApplyService(SyncSettings settings) => _settings = settings;

    public async Task<string> ApplyAsync(PulledSyncEvent item)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = _settings.LocalServer,
            Port = _settings.LocalPort,
            Database = _settings.LocalDatabase,
            UserID = _settings.LocalUsername,
            Password = _settings.LocalPassword,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 60
        };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        try
        {
            return await VoucherApplyEngine.ApplyAsync(
                connection, item.EntityUuid, item.OperationType, item.Payload);
        }
        catch (MySqlException ex) when (ex.Number is not
            (0 or 1042 or 1045 or 1049 or 2002 or 2003 or 2006 or 2013))
        {
            throw new InvalidOperationException("VOUCHER_APPLY_ERROR|" + ex.Message, ex);
        }
    }
}
