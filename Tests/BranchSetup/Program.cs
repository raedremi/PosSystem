using Dapper;
using MySqlConnector;
using RasidSync.Models;
using RasidSync.Services;

var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CUSTOMER_SYNC_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Disposable test database required."));
if (builder.Database != "customer_sync_test") throw new InvalidOperationException("Test database only.");
await using var db = new MySqlConnection(builder.ConnectionString);
await db.OpenAsync();
var settings = new SyncSettings { LocalServer = builder.Server, LocalPort = builder.Port, LocalDatabase = builder.Database,
    LocalUsername = builder.UserID, LocalPassword = builder.Password };
var service = new BranchSetupService(settings, () => new MySqlConnection(builder.ConnectionString));
int passed = 0;
void Assert(bool ok, string message) { if (!ok) throw new Exception(message); passed++; }
async Task Reject(Func<Task> action)
{
    try { await action(); throw new Exception("Expected rejection"); }
    catch (InvalidOperationException) { passed++; }
}
async Task Fixture()
{
    foreach (string t in new[] { "tbl_sync_send", "tbl_sync_receive", "tbl_sync_errors", "tbl_sync_state",
        "tbl_sync_send_branch_backup", "tbl_sync_receive_branch_backup", "tbl_sync_errors_branch_backup", "tbl_sync_id_counter" })
        await db.ExecuteAsync("DROP TABLE IF EXISTS " + t);
    await db.ExecuteAsync(@"CREATE TABLE tbl_sync_send(sync_id BIGINT PRIMARY KEY, sync_status INT) ENGINE=MyISAM;
        CREATE TABLE tbl_sync_receive(receive_id BIGINT PRIMARY KEY,server_event_id BIGINT) ENGINE=MyISAM;
        CREATE TABLE tbl_sync_state(state_key VARCHAR(60) PRIMARY KEY,state_value VARCHAR(255),updated_at DATETIME) ENGINE=MyISAM;
        CREATE TABLE tbl_sync_id_counter(branch_id INT PRIMARY KEY,sequence_number BIGINT) ENGINE=MyISAM;
        INSERT INTO tbl_sync_send VALUES(100,2);
        INSERT INTO tbl_sync_receive VALUES(49,55);
        INSERT INTO tbl_sync_state VALUES('LastReceivedId','4',NOW());
        INSERT INTO tbl_sync_id_counter VALUES(1,900);");
    await new SyncInfrastructureService(settings).EnsureCreatedAsync(db);
}
await Fixture();
Assert(await service.PreviewAsync() == 100, "Baseline comes from copied server send table, not old cursor.");
await Reject(async () => { await service.InitializeAsync(99); });
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_send") == 1, "Stale preview must not delete.");
await db.ExecuteAsync("INSERT INTO tbl_sync_send VALUES(101,0)");
await Reject(async () => { await service.InitializeAsync(101); });
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_send") == 2, "Pending local movement preserved.");
await db.ExecuteAsync("DELETE FROM tbl_sync_send WHERE sync_id=101");
decimal balance = await db.ExecuteScalarAsync<decimal>("SELECT acc_balance FROM tbl_account WHERE acc_id=1001000002");
Assert(await service.InitializeAsync(100) == 100, "Initialization baseline.");
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_send") == 0, "Copied send events cleared.");
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_receive") == 0, "Copied receive events cleared.");
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_send_branch_backup") == 1, "Send backup retained.");
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_receive_branch_backup") == 1, "Receive backup retained.");
Assert(await db.ExecuteScalarAsync<long>("SELECT state_value FROM tbl_sync_state WHERE state_key='LastReceivedId'") == 100, "Start after snapshot baseline.");
Assert(await db.ExecuteScalarAsync<long>("SELECT sequence_number FROM tbl_sync_id_counter WHERE branch_id=1") == 900, "ID counters untouched.");
Assert(await db.ExecuteScalarAsync<decimal>("SELECT acc_balance FROM tbl_account WHERE acc_id=1001000002") == balance, "Financial data untouched.");
await Reject(async () => { await service.InitializeAsync(100); });

// MyISAM: fail after deleting send events; retry must use the saved baseline, never zero.
await Fixture();
await db.ExecuteAsync("CREATE TRIGGER fail_branch_delete BEFORE DELETE ON tbl_sync_receive FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected failure'");
try { await service.InitializeAsync(100); throw new Exception("Expected injected failure"); }
catch (MySqlException) { passed++; }
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_send") == 0, "Injected failure follows partial cleanup.");
Assert(await service.PreviewAsync() == 100, "Interrupted setup retains original baseline.");
await db.ExecuteAsync("DROP TRIGGER fail_branch_delete");
Assert(await service.InitializeAsync(100) == 100, "Interrupted initialization can resume.");
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_sync_send_branch_backup") == 1, "Resume does not overwrite backup.");
Console.WriteLine($"PASS: {passed} branch setup assertions on MySQL/MyISAM.");
