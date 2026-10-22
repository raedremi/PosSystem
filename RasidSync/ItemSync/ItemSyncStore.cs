using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Rasid.ItemSync
{
    /// <summary>
    /// محرك واحد في رصيد وAPI والمزامنة. SQL بمعاملات، وقفل مستقل، وسجل استكمال لـ MyISAM.
    /// لا تنقل كميات المخزون أو متوسط الكلفة أو آخر شراء بين الفروع.
    /// </summary>
    public sealed class ItemSyncStore
    {
        private readonly IDbConnection db;
        public ItemSyncStore(IDbConnection connection) { db = connection; }
        public object Scalar(string sql, params object[] values)
        {
            using (var cmd = Command(sql, values)) return cmd.ExecuteScalar();
        }
        public void Execute(string sql, params object[] values)
        {
            using (var cmd = Command(sql, values)) cmd.ExecuteNonQuery();
        }
        private IDbCommand Command(string sql, object[] values)
        {
            var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 60;
            for (int i = 0; i < values.Length; i++) { var p = cmd.CreateParameter(); p.ParameterName = "@p" + i; p.Value = values[i] ?? DBNull.Value; cmd.Parameters.Add(p); }
            return cmd;
        }
        public List<JObject> Rows(string sql, params object[] values)
        {
            var rows = new List<JObject>();
            using (var cmd = Command(sql, values)) using (var r = cmd.ExecuteReader())
                while (r.Read()) { var row = new JObject(); for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? JValue.CreateNull() : JToken.FromObject(r.GetValue(i)); rows.Add(row); }
            return rows;
        }
        public IDisposable Lock()
        {
            string name = "itemsync:" + Hash(db.Database.ToLowerInvariant(), true);
            if (Convert.ToInt32(Scalar("SELECT GET_LOCK(@p0,10)", name)) != 1) throw new TimeoutException("مزامنة الأصناف مشغولة؛ أعد المحاولة.");
            return new LockScope(this, name);
        }
        private sealed class LockScope : IDisposable
        {
            private ItemSyncStore store; private readonly string name;
            public LockScope(ItemSyncStore value, string key) { store = value; name = key; }
            public void Dispose() { if (store == null) return; try { store.Scalar("SELECT RELEASE_LOCK(@p0)", name); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); } store = null; }
        }
        private static string Hash(string text, bool md5 = false)
        {
            using (HashAlgorithm h = md5 ? (HashAlgorithm)MD5.Create() : SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }
        public void EnsureCreated()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS tbl_sync_item_counter (
                branch_id INT NOT NULL, entity_name VARCHAR(20) NOT NULL, last_sequence INT NOT NULL,
                PRIMARY KEY(branch_id,entity_name)) ENGINE=MyISAM DEFAULT CHARSET=utf8mb4");
            Execute(@"CREATE TABLE IF NOT EXISTS tbl_sync_item_jobs (
                event_uuid VARCHAR(36) NOT NULL PRIMARY KEY, item_id INT NOT NULL, operation_type INT NOT NULL,
                payload LONGTEXT NOT NULL, payload_hash VARCHAR(64) NOT NULL, publish_local TINYINT NOT NULL,
                device_uuid VARCHAR(36) NOT NULL, job_state TINYINT NOT NULL DEFAULT 0,
                created_at DATETIME NOT NULL, applied_at DATETIME NULL, KEY pending(job_state)) ENGINE=MyISAM DEFAULT CHARSET=utf8mb4");
            Execute(@"CREATE TABLE IF NOT EXISTS tbl_sync_item_deleted (
                entity_kind VARCHAR(10) NOT NULL, entity_id INT NOT NULL, item_id INT NOT NULL,
                PRIMARY KEY(entity_kind,entity_id)) ENGINE=MyISAM DEFAULT CHARSET=utf8mb4");
        }
        /// <summary>حجز دائم لا يعاد استعماله بعد حذف السجل. يلزم رمز فرع مختلف لكل قاعدة مستقلة.</summary>
        public int Reserve(int branch, string entity)
        {
            if (branch < 1 || branch > 999) throw ItemSyncContract.Error("ITEM_ID_RANGE", "رمز الفرع يجب أن يكون من 1 إلى 999.");
            string table, key;
            switch (entity)
            {
                case "Item": table = "tbl_item"; key = "it_id"; break;
                case "ItemCode": table = "tbl_item"; key = "it_code"; break;
                case "UnitRow": table = "tbl_unititem"; key = "un_id"; break;
                case "Category": table = "tbl_category"; key = "cat_id"; break;
                case "UnitDef": table = "tbl_units"; key = "unit_id"; break;
                default: throw new ArgumentException("نوع عداد غير معروف.");
            }
            using (Lock())
            {
                EnsureCreated();
                long start = 1000000000L + branch * 1000000L;
                long existing = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(" + key + ")-@p0,0) FROM " + table + " WHERE " + key + ">@p0 AND " + key + "<@p1", start, start + 1000000));
                Execute("INSERT IGNORE INTO tbl_sync_item_counter VALUES(@p0,@p1,0)", branch, entity);
                long sequence = Math.Max(existing, Convert.ToInt64(Scalar("SELECT last_sequence FROM tbl_sync_item_counter WHERE branch_id=@p0 AND entity_name=@p1", branch, entity))) + 1;
                if (sequence > 999999) throw ItemSyncContract.Error("ITEM_ID_RANGE", "انتهى نطاق معرّفات هذا الفرع.");
                Execute("UPDATE tbl_sync_item_counter SET last_sequence=@p2 WHERE branch_id=@p0 AND entity_name=@p1", branch, entity, sequence);
                return checked((int)(start + sequence));
            }
        }
        public JObject ReadItem(long id)
        {
            var rows = Rows("SELECT * FROM tbl_item WHERE it_id=@p0", id);
            if (rows.Count != 1) throw ItemSyncContract.Error("ITEM_NOT_FOUND", "الصنف غير موجود.");
            return ItemSyncContract.Row(rows[0], ItemSyncContract.ItemFields);
        }
        /// <summary>يبني رسالة كاملة أو رسالة تعديلات وحدات؛ الغائب لا يحذف من الفروع.</summary>
        public JObject Package(JObject item, IEnumerable<JObject> units, IEnumerable<long> removed)
        {
            var category = Rows("SELECT * FROM tbl_category WHERE cat_id=@p0", ItemSyncContract.Number(item["cat_id"]));
            if (category.Count != 1) throw ItemSyncContract.Error("ITEM_DEPENDENCY_CONFLICT", "الفئة غير موجودة.");
            var list = units.ToList(); var defs = new JArray();
            foreach (long id in list.Select(x => ItemSyncContract.Number(x["un_unitid"])).Distinct())
            {
                var found = Rows("SELECT * FROM tbl_units WHERE unit_id=@p0", id);
                if (found.Count != 1) throw ItemSyncContract.Error("ITEM_DEPENDENCY_CONFLICT", "تعريف الوحدة غير موجود.");
                defs.Add(ItemSyncContract.Row(found[0], ItemSyncContract.DefinitionFields));
            }
            return new JObject { ["Version"] = 1, ["Item"] = item.DeepClone(), ["Units"] = new JArray(list.Select(x => x.DeepClone())),
                ["RemovedUnitIds"] = new JArray(removed), ["Category"] = ItemSyncContract.Row(category[0], ItemSyncContract.CategoryFields),
                ["UnitDefinitions"] = defs, ["Metadata"] = new JObject { ["DocumentName"] = "صنف: " + (string)item["it_name"], ["DocumentNumber"] = item["it_code"] } };
        }
        public JObject FullPackage(long id)
        {
            var item = ReadItem(id);
            return Package(item, Rows("SELECT * FROM tbl_unititem WHERE un_code=@p0 ORDER BY un_id", ItemSyncContract.Number(item["it_code"])).Select(u => ItemSyncContract.Row(u, ItemSyncContract.UnitFields)), Rows("SELECT entity_id FROM tbl_sync_item_deleted WHERE entity_kind='Unit' AND item_id=@p0", id).Select(x => (long)x["entity_id"]));
        }
        public string Apply(string eventId, string uuid, int operation, string payload, bool publishLocal = false, string device = "")
        {
            Guid parsed;
            if (!Guid.TryParse(eventId, out parsed) || (publishLocal && !Guid.TryParse(device, out parsed))) throw ItemSyncContract.Error("INVALID_ITEM_PAYLOAD", "هوية الحركة أو الجهاز غير صحيحة.");
            var p = ItemSyncContract.Parse(uuid, operation, payload);
            payload = p.ToString(Formatting.None);
            using (Lock())
            {
                EnsureCreated();
                var prior = Rows("SELECT * FROM tbl_sync_item_jobs WHERE event_uuid=@p0", eventId);
                if (prior.Count != 0 && ((string)prior[0]["payload_hash"] != Hash(payload) || (int)prior[0]["operation_type"] != operation))
                    throw ItemSyncContract.Error("ITEM_ID_CONFLICT", "هوية الحركة مستخدمة لبيانات مختلفة.");
                if (prior.Count != 0 && (int)prior[0]["job_state"] == 1) return "حركة الصنف مطبقة سابقًا.";
                // لا نسمح لعملية لاحقة بتجاوز كتابة جزئية؛ تستكمل القديمة أولًا تحت القفل نفسه.
                RecoverLocked();
                if (prior.Count != 0) return "تم استكمال حركة الصنف السابقة.";
                ValidateDatabase(p, operation);
                Execute(@"INSERT INTO tbl_sync_item_jobs(event_uuid,item_id,operation_type,payload,payload_hash,publish_local,device_uuid,job_state,created_at)
                    VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,0,NOW())", eventId, ItemSyncContract.Number(p["Item"]["it_id"]), operation, payload, Hash(payload), publishLocal ? 1 : 0, device);
                Finish(eventId, p, operation, publishLocal, device);
                return operation == 3 ? "تم حذف الصنف ووحداته." : "تم حفظ الصنف ووحداته بنفس المعرّفات.";
            }
        }
        public void Recover()
        {
            using (Lock()) { EnsureCreated(); RecoverLocked(); }
        }
        private void RecoverLocked()
        {
            foreach (var job in Rows("SELECT * FROM tbl_sync_item_jobs WHERE job_state=0 ORDER BY created_at,event_uuid"))
            {
                int op = (int)job["operation_type"];
                var p = ItemSyncContract.Parse(ItemSyncContract.Uuid((long)job["item_id"]), op, (string)job["payload"]);
                ValidateDatabase(p, op);
                Finish((string)job["event_uuid"], p, op, (int)job["publish_local"] == 1, (string)job["device_uuid"]);
            }
        }
        private void ValidateDatabase(JObject p, int operation)
        {
            var item = (JObject)p["Item"]; long id = ItemSyncContract.Number(item["it_id"]), code = ItemSyncContract.Number(item["it_code"]);
            var existing = Rows("SELECT it_id,it_code FROM tbl_item WHERE it_id=@p0 OR it_code=@p1", id, code);
            if (existing.Any(x => ItemSyncContract.Number(x["it_id"]) != id || ItemSyncContract.Number(x["it_code"]) != code))
                throw ItemSyncContract.Error("ITEM_ID_CONFLICT", "رمز أو معرف الصنف مرتبط بصنف آخر.");
            if (operation != 3 && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM tbl_sync_item_deleted WHERE entity_kind='Item' AND entity_id=@p0", id)) > 0)
                throw ItemSyncContract.Error("ITEM_DELETED", "الصنف محذوف؛ لن تعيده رسالة تعديل متأخرة.");
            if (operation != 3 && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM tbl_item WHERE it_name=@p0 AND it_id<>@p1", (string)item["it_name"], id)) > 0)
                throw ItemSyncContract.Error("ITEM_NAME_CONFLICT", "اسم الصنف مستخدم لصنف آخر؛ لم يتم دمج البطاقات.");
            if (operation == 3) { CheckReferences(id, null); return; }
            CheckDefinition("tbl_category", "cat_id", "cat_name", (JObject)p["Category"]);
            foreach (JObject d in (JArray)p["UnitDefinitions"]) CheckDefinition("tbl_units", "unit_id", "unit_name", d);
            var removed = ((JArray)p["RemovedUnitIds"]).Select(ItemSyncContract.Number).ToHashSetCompat();
            foreach (long unit in removed)
            {
                if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM tbl_unititem WHERE un_id=@p0 AND un_code<>@p1", unit, code)) > 0)
                    throw ItemSyncContract.Error("ITEM_UNIT_CONFLICT", "الوحدة المطلوب حذفها تابعة لصنف آخر.");
                CheckReferences(id, unit);
            }
            foreach (JObject u in (JArray)p["Units"])
            {
                long unitId = ItemSyncContract.Number(u["un_id"]), def = ItemSyncContract.Number(u["un_unitid"]);
                if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM tbl_sync_item_deleted WHERE entity_kind='Unit' AND entity_id=@p0", unitId)) > 0)
                    throw ItemSyncContract.Error("ITEM_DELETED", "وحدة محذوفة؛ لن يعيدها تعديل قديم.");
                var found = Rows("SELECT un_id,un_code,un_unitid,un_unitequals FROM tbl_unititem WHERE un_id=@p0 OR (un_code=@p1 AND un_unitid=@p2)", unitId, code, def);
                foreach (var f in found)
                {
                    if ((long)f["un_id"] != unitId || (long)f["un_code"] != code)
                        throw ItemSyncContract.Error("ITEM_UNIT_CONFLICT", "معرف الوحدة مستخدم أو توجد الوحدة نفسها بمعرف آخر.");
                    if ((long)f["un_unitid"] != def || (long)f["un_unitequals"] != (long)u["un_unitequals"]) CheckReferences(id, unitId);
                }
            }
            // فحوص الطول تسبق الكتابة، حتى لا نترك نصف بطاقة عند إدخال نص طويل.
            CheckLengths("tbl_item", new[] { item }); CheckLengths("tbl_unititem", ((JArray)p["Units"]).Cast<JObject>());
        }
        private void CheckLengths(string table, IEnumerable<JObject> rows)
        {
            var lengths = Rows("SELECT COLUMN_NAME,CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@p0 AND CHARACTER_MAXIMUM_LENGTH IS NOT NULL", table);
            foreach (var row in rows) foreach (var l in lengths) { var v = row[(string)l["COLUMN_NAME"]]; if (v != null && v.Type != JTokenType.Null && v.ToString().Length > (long)l["CHARACTER_MAXIMUM_LENGTH"]) throw ItemSyncContract.Error("INVALID_ITEM_PAYLOAD", "النص أطول من الحقل: " + (string)l["COLUMN_NAME"]); }
        }
        private void CheckDefinition(string table, string key, string name, JObject row)
        {
            var found = Rows("SELECT * FROM " + table + " WHERE " + key + "=@p0", ItemSyncContract.Number(row[key]));
            if (found.Count != 0 && !string.Equals((string)found[0][name], (string)row[name], StringComparison.Ordinal))
                throw ItemSyncContract.Error("ITEM_DEPENDENCY_CONFLICT", "رقم فئة أو تعريف وحدة مرتبط باسم مختلف: " + table);
            CheckLengths(table, new[] { row });
        }
        private void CheckReferences(long itemId, long? unitId)
        {
            // الجداول الفعلية فقط، وليس العروض. لا نعتبر كاش العرض حركة مخزنية.
            var refs = new List<JObject>();
            var tables = Rows("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_TYPE='BASE TABLE'");
            foreach (var table in tables)
            {
                string name = (string)table["TABLE_NAME"];
                if (name == "tbl_item" || name == "tbl_unititem" || name == "item_stock_cachetest" || name.StartsWith("tbl_sync_", StringComparison.OrdinalIgnoreCase)) continue;
                // الاستعلام لكل جدول يتجنب مسح metadata لكل قواعد السيرفر أو تشغيل العروض.
                refs.AddRange(Rows(@"SELECT TABLE_NAME,COLUMN_NAME FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@p0
                    AND COLUMN_NAME IN ('it_id','det_it_id','un_id','det_un_id')", name));
            }
            foreach (var r in refs)
            {
                string column = (string)r["COLUMN_NAME"];
                bool isUnit = column == "un_id" || column == "det_un_id";
                if (unitId.HasValue && !isUnit) continue;
                if (!unitId.HasValue && isUnit) continue;
                if (Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM " + Quote((string)r["TABLE_NAME"]) + " WHERE " + Quote(column) + "=@p0)", unitId.HasValue ? unitId.Value : itemId)) != 0)
                    throw ItemSyncContract.Error("ITEM_DELETE_IN_USE", "الصنف أو الوحدة مستخدم في " + (string)r["TABLE_NAME"] + "." + column);
            }
        }
        private void Finish(string eventId, JObject p, int operation, bool publish, string device)
        {
            var item = (JObject)p["Item"]; long id = ItemSyncContract.Number(item["it_id"]), code = ItemSyncContract.Number(item["it_code"]);
            if (operation == 3)
            {
                Execute("INSERT IGNORE INTO tbl_sync_item_deleted VALUES('Item',@p0,@p0)", id);
                Execute("DELETE FROM item_stock_cachetest WHERE it_id=@p0", id);
                Execute("DELETE FROM tbl_unititem WHERE un_code=@p0", code);
                Execute("DELETE FROM tbl_item WHERE it_id=@p0 AND it_code=@p1", id, code);
            }
            else
            {
                InsertDefinition("tbl_category", "cat_id", (JObject)p["Category"]);
                foreach (JObject d in (JArray)p["UnitDefinitions"]) InsertDefinition("tbl_units", "unit_id", d);
                Upsert("tbl_item", "it_id", item, null);
                foreach (var r in (JArray)p["RemovedUnitIds"])
                {
                    long uid = ItemSyncContract.Number(r);
                    Execute("INSERT IGNORE INTO tbl_sync_item_deleted VALUES('Unit',@p0,@p1)", uid, id);
                    Execute("DELETE FROM item_stock_cachetest WHERE it_id=@p0 AND un_id=@p1", id, uid);
                    Execute("DELETE FROM tbl_unititem WHERE un_code=@p0 AND un_id=@p1", code, uid);
                }
                foreach (JObject u in (JArray)p["Units"])
                {
                    // تبديل تعريف وحدة غير مستخدمة يحتفظ بمعرّف صفها وبقيم الكاش المحلية.
                    Execute("UPDATE item_stock_cachetest SET un_unitid=@p0 WHERE it_id=@p1 AND un_id=@p2 AND un_unitid<>@p0",
                        ItemSyncContract.Number(u["un_unitid"]), id, ItemSyncContract.Number(u["un_id"]));
                    Upsert("tbl_unititem", "un_id", u, new JObject { ["un_averagecost"] = 0, ["un_LastCost"] = 0 });
                }
                // نحدث وصف وأسعار الكاش فقط؛ الكمية ومتوسط الكلفة الموجودان يبقيان محليين.
                foreach (var u in Rows("SELECT * FROM tbl_unititem WHERE un_code=@p0", code))
                {
                    var cache = ItemSyncContract.Row(u, ItemSyncContract.UnitFields); cache["it_id"] = id;
                    cache.Remove("un_LastCost"); cache["updated_at"] = DateTime.Now;
                    bool exists = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM item_stock_cachetest WHERE it_id=@p0 AND un_unitid=@p1", id, ItemSyncContract.Number(u["un_unitid"]))) > 0;
                    if (exists) Update("item_stock_cachetest", cache, "it_id=@p0 AND un_unitid=@p1", id, ItemSyncContract.Number(u["un_unitid"]));
                    else
                    {
                        cache["un_averagecost"] = Convert.ToDecimal(Scalar("SELECT COALESCE(MAX(un_averagecost),0) FROM item_stock_cachetest WHERE it_id=@p0", id));
                        cache["un_qty_available"] = Convert.ToDecimal(Scalar("SELECT COALESCE(MAX(un_qty_available),0) FROM item_stock_cachetest WHERE it_id=@p0", id));
                        Insert("item_stock_cachetest", cache);
                    }
                }
            }
            if (publish)
            {
                // إذا انقطع التنفيذ بعد الإدخال فـ event_uuid نفسه يمنع تكرار الطابور.
                if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM tbl_sync_send WHERE event_uuid=@p0", eventId)) == 0)
                    Execute(@"INSERT INTO tbl_sync_send(event_uuid,source_device_uuid,entity_type,entity_uuid,local_id,operation_type,payload,sync_status,retry_count,created_at)
                        VALUES(@p0,@p1,'Item',@p2,@p3,@p4,@p5,0,0,NOW())", eventId, device, ItemSyncContract.Uuid(id), id, operation, p.ToString(Formatting.None));
            }
            Execute("UPDATE tbl_sync_item_jobs SET job_state=1,applied_at=NOW() WHERE event_uuid=@p0", eventId);
        }
        private void InsertDefinition(string table, string key, JObject row)
        {
            if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + table + " WHERE " + key + "=@p0", ItemSyncContract.Number(row[key]))) == 0) Insert(table, row);
        }
        private void Upsert(string table, string key, JObject row, JObject insertDefaults)
        {
            long id = ItemSyncContract.Number(row[key]);
            if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM " + table + " WHERE " + key + "=@p0", id)) > 0) Update(table, row, key + "=@p0", id);
            else { var copy = (JObject)row.DeepClone(); if (insertDefaults != null) foreach (var prop in insertDefaults.Properties()) copy[prop.Name] = prop.Value.DeepClone(); Insert(table, copy); }
        }
        private static string Quote(string value) { return "`" + value.Replace("`", "``") + "`"; }
        private static object Value(JToken token) { return token == null || token.Type == JTokenType.Null ? null : ((JValue)token).Value; }
        private void Insert(string table, JObject row)
        {
            var props = row.Properties().ToList();
            Execute("INSERT INTO " + Quote(table) + " (" + string.Join(",", props.Select(x => Quote(x.Name))) + ") VALUES(" + string.Join(",", props.Select((x, i) => "@p" + i)) + ")", props.Select(x => Value(x.Value)).ToArray());
        }
        private void Update(string table, JObject row, string where, params object[] args)
        {
            var props = row.Properties().ToList();
            Execute("UPDATE " + Quote(table) + " SET " + string.Join(",", props.Select((x, i) => Quote(x.Name) + "=@p" + (i + args.Length))) + " WHERE " + where, args.Concat(props.Select(x => Value(x.Value))).ToArray());
        }
    }
    internal static class ItemSetExtensions
    {
        internal static HashSet<T> ToHashSetCompat<T>(this IEnumerable<T> values) { return new HashSet<T>(values); }
    }
}
