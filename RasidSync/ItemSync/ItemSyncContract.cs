using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Rasid.ItemSync
{
    /// <summary>عقد بطاقة الصنف. الوحدات الواردة تضاف أو تعدل، والحذف صريح فقط.</summary>
    public static class ItemSyncContract
    {
        public static readonly string[] ItemFields = { "it_id", "it_code", "it_name", "cat_id", "it_latin", "it_note", "it_desc", "it_tax", "it_enabletax", "it_activity", "show_pos", "it_enable_minus", "it_enable_date", "ck_num", "it_enable_scales", "it_type_scales" };
        public static readonly string[] UnitFields = { "un_id", "un_code", "un_unitid", "un_unitname", "un_unitequals", "un_barcode", "un_sales", "un_purchas", "un_price_a", "un_price_b", "un_price_c", "un_unit_defult", "un_cost" };
        public static readonly string[] CategoryFields = { "cat_id", "cat_name", "cat_latin", "cat_pos", "cat_state", "cat_print", "ck_num" };
        public static readonly string[] DefinitionFields = { "unit_id", "unit_name" };
        public static long Number(JToken value)
        {
            decimal n;
            if (value == null || !decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out n) || n != decimal.Truncate(n) || n < 1 || n > int.MaxValue)
                throw Error("INVALID_ITEM_PAYLOAD", "معرف غير صحيح أو خارج مجال INT.");
            return (long)n;
        }
        public static string Uuid(long id) { return "4954454d-0000-0000-0000-" + id.ToString("D12", CultureInfo.InvariantCulture); }
        public static InvalidOperationException Error(string code, string message) { return new InvalidOperationException(code + "|" + message); }
        public static JObject Row(JObject source, string[] fields)
        {
            if (source == null) throw Error("INVALID_ITEM_PAYLOAD", "بيانات البطاقة ناقصة.");
            JObject row = new JObject();
            foreach (string field in fields)
            {
                if (source.Property(field) == null) throw Error("INVALID_ITEM_PAYLOAD", "حقل مفقود: " + field);
                if (source[field] is JContainer) throw Error("INVALID_ITEM_PAYLOAD", "قيمة غير صحيحة: " + field);
                row[field] = source[field].DeepClone();
            }
            return row;
        }
        public static JObject Parse(string uuid, int operation, string json)
        {
            if (operation < 1 || operation > 4) throw Error("INVALID_ITEM_PAYLOAD", "نوع عملية الصنف غير صحيح.");
            JObject p;
            try { p = JObject.Parse(json); } catch (JsonException ex) { throw Error("INVALID_ITEM_PAYLOAD", ex.Message); }
            if ((int?)p["Version"] != 1) throw Error("INVALID_ITEM_PAYLOAD", "إصدار بطاقة الصنف غير مدعوم.");
            p["Item"] = Row(p["Item"] as JObject, ItemFields);
            var item = (JObject)p["Item"];
            long id = Number(item["it_id"]), code = Number(item["it_code"]);
            if (!Uuid(id).Equals(uuid, StringComparison.OrdinalIgnoreCase)) throw Error("ITEM_ID_CONFLICT", "هوية الصنف لا تطابق معرّفه.");
            if (string.IsNullOrWhiteSpace((string)item["it_name"])) throw Error("INVALID_ITEM_PAYLOAD", "اسم الصنف فارغ.");
            Number(item["cat_id"]);
            var units = p["Units"] as JArray; var removed = p["RemovedUnitIds"] as JArray;
            if (units == null || removed == null || units.Count > 1000 || removed.Count > 1000) throw Error("INVALID_ITEM_PAYLOAD", "قائمة الوحدات غير صحيحة.");
            HashSet<long> ids = new HashSet<long>(), definitions = new HashSet<long>();
            for (int i = 0; i < units.Count; i++)
            {
                var row = Row(units[i] as JObject, UnitFields); units[i] = row;
                if (Number(row["un_code"]) != code || !ids.Add(Number(row["un_id"])) || !definitions.Add(Number(row["un_unitid"])))
                    throw Error("ITEM_UNIT_CONFLICT", "وحدة مكررة أو مرتبطة بصنف مختلف.");
                if (Number(row["un_unitequals"]) < 1) throw Error("INVALID_ITEM_PAYLOAD", "تعادل الوحدة يجب أن يكون موجبًا.");
                if (string.IsNullOrWhiteSpace((string)row["un_unitname"])) throw Error("INVALID_ITEM_PAYLOAD", "اسم الوحدة فارغ.");
            }
            foreach (var r in removed) if (!ids.Add(Number(r))) throw Error("INVALID_ITEM_PAYLOAD", "معرف الوحدة مكرر بين الحفظ والحذف.");
            p["Category"] = Row(p["Category"] as JObject, CategoryFields);
            if (Number(p["Category"]["cat_id"]) != Number(item["cat_id"])) throw Error("ITEM_DEPENDENCY_CONFLICT", "الفئة لا تطابق البطاقة.");
            var defs = p["UnitDefinitions"] as JArray;
            if (defs == null) throw Error("INVALID_ITEM_PAYLOAD", "تعريفات الوحدات ناقصة.");
            HashSet<long> defIds = new HashSet<long>();
            for (int i = 0; i < defs.Count; i++) { var d = Row(defs[i] as JObject, DefinitionFields); defs[i] = d; if (!defIds.Add(Number(d["unit_id"]))) throw Error("ITEM_UNIT_CONFLICT", "تعريف وحدة مكرر."); }
            foreach (JObject u in units)
                if (!defs.OfType<JObject>().Any(d => Number(d["unit_id"]) == Number(u["un_unitid"]) && (string)d["unit_name"] == (string)u["un_unitname"]))
                    throw Error("ITEM_DEPENDENCY_CONFLICT", "اسم أو تعريف الوحدة لا يطابق البطاقة.");
            return p;
        }
    }
}
