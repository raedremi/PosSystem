using System.Globalization;
using Dapper;
using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Services;

/// <summary>تهيئة نسخة السيرفر المحلية مرة واحدة، قبل تسجيل أي حركة للفرع.</summary>
public sealed class BranchSetupService
{
    private readonly SyncSettings _settings;
    private static readonly string[] Tables = { "tbl_sync_send", "tbl_sync_receive", "tbl_sync_errors" };
    public BranchSetupService(SyncSettings settings) => _settings = settings;

    public async Task<long> PreviewAsync()
    {
        await using var db = CreateConnection();
        await db.OpenAsync();
        return await ReadBaselineAsync(db);
    }

    public async Task<long> InitializeAsync(long expectedBaseline)
    {
        await new SyncInfrastructureService(_settings).EnsureCreatedAsync();
        await using var db = CreateConnection();
        await db.OpenAsync();
        // تبقى نسخة السجلات القديمة في القاعدة، ويمكن مراجعتها عند الحاجة.
        foreach (string table in Tables)
            await db.ExecuteAsync($"CREATE TABLE IF NOT EXISTS {table}_branch_backup LIKE {table}");
        string locks = string.Join(",", Tables.SelectMany(t => new[] { t + " WRITE", t + "_branch_backup WRITE" })) + ",tbl_sync_state WRITE";
        await db.ExecuteAsync("LOCK TABLES " + locks);
        try
        {
            long baseline = await ReadBaselineAsync(db);
            if (baseline != expectedBaseline)
                throw new InvalidOperationException("تغير رقم الحركة منذ فتح الشاشة. أعد فتح التهيئة.");
            string? pending = await StateAsync(db, "BranchSetupPending");
            if (pending == null)
            {
                if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_send WHERE sync_status<>2") > 0)
                    throw new InvalidOperationException("توجد حركات محلية غير مرسلة. التهيئة مخصصة لنسخة سيرفر جديدة قبل بدء العمل.");
                foreach (string table in Tables)
                {
                    if (await db.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}_branch_backup") != 0)
                        throw new InvalidOperationException("توجد نسخة سابقة لسجلات التهيئة؛ لم يتم حذف شيء.");
                    await db.ExecuteAsync($"INSERT INTO {table}_branch_backup SELECT * FROM {table}");
                }
                // MyISAM لا يدعم rollback: نحفظ نقطة البداية قبل أي حذف، لتبقى صالحة عند إعادة المحاولة.
                await SetStateAsync(db, "BranchSetupPending", baseline.ToString(CultureInfo.InvariantCulture));
            }
            foreach (string table in Tables)
                await db.ExecuteAsync($"DELETE FROM {table}");
            await db.ExecuteAsync("DELETE FROM tbl_sync_state WHERE state_key<>'BranchSetupPending'");
            await SetStateAsync(db, "LastReceivedId", baseline.ToString(CultureInfo.InvariantCulture));
            await SetStateAsync(db, "BranchSetupCompleted", baseline.ToString(CultureInfo.InvariantCulture));
            await db.ExecuteAsync("DELETE FROM tbl_sync_state WHERE state_key='BranchSetupPending'");
            return baseline;
        }
        finally { await db.ExecuteAsync("UNLOCK TABLES"); }
    }

    private static async Task<long> ReadBaselineAsync(MySqlConnection db)
    {
        string? pending = await StateAsync(db, "BranchSetupPending");
        if (pending != null) return long.Parse(pending, CultureInfo.InvariantCulture);
        if (await StateAsync(db, "BranchSetupCompleted") != null)
            throw new InvalidOperationException("تمت تهيئة هذه القاعدة سابقًا. لا تستخدم التهيئة أثناء العمل اليومي.");
        return await db.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(sync_id),0) FROM tbl_sync_send");
    }
    private static Task<string?> StateAsync(MySqlConnection db, string key) =>
        db.QuerySingleOrDefaultAsync<string?>("SELECT state_value FROM tbl_sync_state WHERE state_key=@key", new { key });
    private static Task SetStateAsync(MySqlConnection db, string key, string value) =>
        db.ExecuteAsync("INSERT INTO tbl_sync_state(state_key,state_value,updated_at) VALUES (@key,@value,NOW()) ON DUPLICATE KEY UPDATE state_value=@value,updated_at=NOW()", new { key, value });
    private MySqlConnection CreateConnection() => new(new MySqlConnectionStringBuilder
    {
        Server = _settings.LocalServer, Port = _settings.LocalPort, Database = _settings.LocalDatabase,
        UserID = _settings.LocalUsername, Password = _settings.LocalPassword
    }.ConnectionString);
}
