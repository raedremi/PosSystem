using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.CustomerSupplierSync;

public sealed class LocalCustomerApplyService
{
    private readonly SyncSettings _settings;
    public LocalCustomerApplyService(SyncSettings settings) => _settings = settings;

    public async Task<string> ApplyAsync(PulledSyncEvent item)
    {
        await using var connection = CustomerPackageService.CreateConnection(_settings);
        await connection.OpenAsync();
        try { return await CustomerApplyEngine.ApplyAsync(connection, item.EventUuid, item.EntityUuid, item.OperationType, item.Payload); }
        catch (MySqlException ex) when (ex.Number is not (0 or 1042 or 1045 or 1049 or 2002 or 2003 or 2006 or 2013))
        { throw new InvalidOperationException("CUSTOMER_APPLY_ERROR|" + ex.Message, ex); }
    }
}
