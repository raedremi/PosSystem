using System.Text.Json;
using Dapper;
using MySqlConnector;
using RasidSync.CustomerSupplierSync;

// MySQL 5.7 + MyISAM، عبر قاعدة تجريبية منفصلة تنشئها اختبارات CI فقط.
string connectionString = Environment.GetEnvironmentVariable("CUSTOMER_SYNC_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set CUSTOMER_SYNC_TEST_CONNECTION to a disposable test database.");
using var db = new MySqlConnection(connectionString);
await db.OpenAsync();
if (db.Database != "customer_sync_test") throw new InvalidOperationException("Tests only run in customer_sync_test.");
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }

var numeric = new HashSet<string> { "cust_id", "acc_id", "cust_typeprice", "cust_discount", "cust_limitdebt", "cust_disenblecredit", "cust_cash", "cust_disenablesales", "cust_type", "cust_free_vat" };
foreach (string table in new[] { "tbl_sync_customer_applied", "tbl_invoice", "tbl_gl", "tbl_cust", "tbl_account", "tbl_typeprice" })
    await db.ExecuteAsync("DROP TABLE IF EXISTS " + table);
await db.ExecuteAsync("CREATE TABLE tbl_account (acc_id INT PRIMARY KEY, acc_no BIGINT, acc_perent_no BIGINT, acc_Aname VARCHAR(200), acc_type INT, acc_report INT, acc_level INT, acc_customer INT, acc_debt DECIMAL(20,3), acc_credt DECIMAL(20,3), acc_balance DECIMAL(20,3)) ENGINE=MyISAM DEFAULT CHARSET=utf8");
await db.ExecuteAsync("CREATE TABLE tbl_cust (" + string.Join(",", CustomerSyncContract.CustomerFields.Select(f => "`" + f + "` " + (numeric.Contains(f) ? "DECIMAL(20,3)" : "VARCHAR(300)") + (f == "cust_id" ? " PRIMARY KEY" : ""))) + ") ENGINE=MyISAM DEFAULT CHARSET=utf8");
await db.ExecuteAsync("CREATE TABLE tbl_gl (gl_id INT PRIMARY KEY, gl_ac_id INT) ENGINE=MyISAM DEFAULT CHARSET=utf8; CREATE TABLE tbl_typeprice (t_p_id INT PRIMARY KEY) ENGINE=MyISAM DEFAULT CHARSET=utf8; INSERT INTO tbl_typeprice VALUES (1); INSERT INTO tbl_account VALUES (1,1200,0,'Parent',1,1,2,0,0,0,0)");

JsonElement Payload(long id, long accountId, long number, int type = 1, string name = "First", long parent = 1200, int price = 1)
{
    var customer = CustomerSyncContract.CustomerFields.ToDictionary(f => f, f => numeric.Contains(f) ? (object?)0 : "");
    customer["cust_id"] = id; customer["acc_id"] = accountId; customer["cust_type"] = type;
    customer["cust_name"] = name; customer["cust_typeprice"] = price; customer["cust_adddate"] = "2026-10-13";
    var account = new Dictionary<string, object?> { ["acc_id"] = accountId, ["acc_no"] = number, ["acc_perent_no"] = parent,
        ["acc_Aname"] = name, ["acc_type"] = 2, ["acc_report"] = 1, ["acc_level"] = 3, ["acc_customer"] = type,
        ["acc_balance"] = 999999 }; // أي رصيد في JSON يجب تجاهله.
    return JsonSerializer.SerializeToElement(new { Version = 1, Customer = customer, Account = account });
}
async Task Apply(long id, int operation, JsonElement payload, string? eventId = null) =>
    await CustomerApplyEngine.ApplyAsync(db, eventId ?? Guid.NewGuid().ToString(), CustomerSyncContract.Uuid(id), operation, payload);
async Task Reject(string code, Func<Task> action)
{
    try { await action(); throw new Exception("Expected rejection: " + code); }
    catch (InvalidOperationException ex) { Assert(ex.Message.StartsWith(code + "|"), ex.Message); }
}
long customerId = 1001000001, accountId = 1001000002;
Assert(Guid.TryParse(CustomerSyncContract.Uuid(customerId), out _), "Stable entity identity must be a GUID.");
Assert(CustomerSyncContract.Branch(1999999999) == 999, "Maximum synchronized range.");
await Reject("CUSTOMER_ID_RANGE", () => { CustomerSyncContract.Uuid(2000000001); return Task.CompletedTask; });
await Reject("CUSTOMER_ID_RANGE", () => { CustomerSyncContract.Uuid(7); return Task.CompletedTask; });
await Reject("CUSTOMER_ID_RANGE", () => { CustomerSyncContract.Uuid(1001000000); return Task.CompletedTask; });
await Reject("INVALID_CUSTOMER_PAYLOAD", () => CustomerApplyEngine.ApplyAsync(db, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), 1, Payload(customerId, accountId, 120001)));
await Reject("INVALID_CUSTOMER_PAYLOAD", () => Apply(customerId, 1, Payload(customerId, 1002000001, 120001)));
await Reject("INVALID_CUSTOMER_PAYLOAD", () => Apply(customerId, 1, JsonSerializer.SerializeToElement(new { Version = 1 })));
await Reject("CUSTOMER_PARENT_MISSING", () => Apply(customerId, 1, Payload(customerId, accountId, 120001, parent: 999)));
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_account WHERE acc_id=@id", new { id = accountId }) == 0, "Parent failure must precede writing.");
await Reject("CUSTOMER_PRICE_MISSING", () => Apply(customerId, 1, Payload(customerId, accountId, 120001, price: 999)));
string firstEvent = Guid.NewGuid().ToString();
JsonElement first = Payload(customerId, accountId, 120001);
await Apply(customerId, 1, first, firstEvent);
Assert(await db.ExecuteScalarAsync<long>("SELECT acc_id FROM tbl_cust WHERE cust_id=@id", new { id = customerId }) == accountId, "Preserved account linkage.");
Assert(await db.ExecuteScalarAsync<decimal>("SELECT acc_balance FROM tbl_account WHERE acc_id=@id", new { id = accountId }) == 0, "Never import balances.");
await db.ExecuteAsync("UPDATE tbl_account SET acc_balance=55.125,acc_debt=80,acc_credt=24.875 WHERE acc_id=@id", new { id = accountId });
await Apply(customerId, 2, Payload(customerId, accountId, 120001, name: "Edited ' Arabic عميل"));
Assert(await db.ExecuteScalarAsync<decimal>("SELECT acc_balance FROM tbl_account WHERE acc_id=@id", new { id = accountId }) == 55.125m, "Update must preserve financial fields.");
await Apply(customerId, 1, first, firstEvent);
Assert(await db.ExecuteScalarAsync<string>("SELECT cust_name FROM tbl_cust WHERE cust_id=@id", new { id = customerId }) == "Edited ' Arabic عميل", "Replay of an older event must not undo a newer edit.");
await Reject("CUSTOMER_ID_CONFLICT", () => Apply(customerId, 1, Payload(customerId, accountId, 120001, name: "different"), firstEvent));
await Reject("CUSTOMER_ACCOUNT_CONFLICT", () => Apply(customerId, 4, Payload(customerId, accountId, 120002)));
await Reject("CUSTOMER_ID_CONFLICT", () => Apply(customerId, 4, Payload(customerId, 1001000099, 120003)));
await Reject("CUSTOMER_ACCOUNT_CONFLICT", () => Apply(1002000001, 1, Payload(1002000001, 1002000002, 120001)));
await Reject("CUSTOMER_DELETE_IN_USE", () => Apply(customerId, 3, first));
await db.ExecuteAsync("UPDATE tbl_account SET acc_balance=0,acc_debt=0,acc_credt=0 WHERE acc_id=@id; INSERT INTO tbl_gl VALUES (1,@id)", new { id = accountId });
await Reject("CUSTOMER_DELETE_IN_USE", () => Apply(customerId, 3, first));
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_cust WHERE cust_id=@id", new { id = customerId }) == 1, "Rejected delete must keep card.");
await db.ExecuteAsync("DELETE FROM tbl_gl; CREATE TABLE tbl_invoice (inv_id INT PRIMARY KEY,inv_acc_id INT) ENGINE=MyISAM DEFAULT CHARSET=utf8; INSERT INTO tbl_invoice VALUES (1,@id)", new { id = accountId });
await Reject("CUSTOMER_DELETE_IN_USE", () => Apply(customerId, 3, first));
await db.ExecuteAsync("DROP TABLE tbl_invoice");
string deleteEvent = Guid.NewGuid().ToString();
await Apply(customerId, 3, first, deleteEvent);
await Apply(customerId, 3, first, deleteEvent);
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_cust WHERE cust_id=@id", new { id = customerId }) == 0, "Repeated delete is idempotent.");
await Apply(customerId, 4, Payload(customerId, accountId, 120001, name: "Replacement"));
Assert(await db.ExecuteScalarAsync<string>("SELECT acc_Aname FROM tbl_account WHERE acc_id=@id", new { id = accountId }) == "Replacement", "Replacement may create missing card and account.");
await Apply(1001000003, 1, Payload(1001000003, 1001000004, 120002, type: 2, name: "Supplier"));
Assert(await db.ExecuteScalarAsync<int>("SELECT cust_type FROM tbl_cust WHERE cust_id=1001000003") == 2, "Supplier path.");

// يحاكي فشل إدخال البطاقة بعد إدخال الحساب مع MyISAM.
await db.ExecuteAsync("CREATE TRIGGER fail_customer_insert BEFORE INSERT ON tbl_cust FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected insert failure'");
string partialEvent = Guid.NewGuid().ToString();
JsonElement partial = Payload(1001000005, 1001000006, 120003);
try { await Apply(1001000005, 1, partial, partialEvent); throw new Exception("Expected trigger failure."); }
catch (MySqlException) { passed++; }
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_account WHERE acc_id=1001000006") == 1, "MyISAM partial insert.");
await db.ExecuteAsync("DROP TRIGGER fail_customer_insert");
await Apply(1001000005, 1, partial, partialEvent);
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_cust WHERE cust_id=1001000005") == 1, "Retry repairs partial insertion.");
await db.ExecuteAsync("CREATE TRIGGER fail_account_delete BEFORE DELETE ON tbl_account FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected delete failure'");
string partialDelete = Guid.NewGuid().ToString();
try { await Apply(1001000005, 3, partial, partialDelete); throw new Exception("Expected delete failure."); }
catch (MySqlException) { passed++; }
await db.ExecuteAsync("DROP TRIGGER fail_account_delete");
await Apply(1001000005, 3, partial, partialDelete);
Assert(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_account WHERE acc_id=1001000006") == 0, "Retry completes partial delete.");

// حركتان لنفس رقم الدليل: القفل يسمح بواحدة ويرفض الأخرى قبل الكتابة.
async Task<bool> Concurrent(long id, long aid)
{
    using var other = new MySqlConnection(connectionString); await other.OpenAsync();
    try { await CustomerApplyEngine.ApplyAsync(other, Guid.NewGuid().ToString(), CustomerSyncContract.Uuid(id), 1, Payload(id, aid, 120004)); return true; }
    catch (InvalidOperationException ex) when (ex.Message.StartsWith("CUSTOMER_ACCOUNT_CONFLICT|")) { return false; }
}
bool[] results = await Task.WhenAll(Concurrent(1001000010, 1001000011), Concurrent(1002000010, 1002000011));
Assert(results.Count(x => x) == 1, "Concurrent number conflict must have exactly one winner.");
Console.WriteLine($"PASS: {passed} assertions on actual MySQL/MyISAM customer apply code.");
