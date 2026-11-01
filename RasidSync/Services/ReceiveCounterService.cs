using System.Net;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using RasidSync.Models;
using RasidSync.CustomerSupplierSync;

namespace RasidSync.Services;

public sealed class ReceiveCounterSnapshot
{
    public int ProtocolVersion { get; set; }
    public bool ItemApplySupported { get; set; }
    public long ServerMaximum { get; set; }
    public string? StreamAnchor { get; set; }
    public string? CursorEventUuid { get; set; }
    public long? BaselineId { get; set; }
    public long LocalCursor { get; set; }
}

/// <summary>يختار نقطة الاستقبال تلقائيًا ويتحقق من هوية سجل السيرفر، دون تعديل ترقيم المستندات.</summary>
public sealed class ReceiveCounterService
{
    private readonly SyncSettings settings;
    private readonly Func<long,string?,Task<ReceiveCounterSnapshot>>? readServer;
    public ReceiveCounterService(SyncSettings value, Func<long,string?,Task<ReceiveCounterSnapshot>>? reader = null)
    { settings=value; readServer=reader; }

    public async Task<ReceiveCounterSnapshot> InspectAsync()
    {
        await using var db=CustomerPackageService.CreateConnection(settings);
        await db.OpenAsync();
        long cursor=await ReadCursorAsync(db);
        string? baseline=await db.QuerySingleOrDefaultAsync<string?>(
            "SELECT event_uuid FROM tbl_sync_send WHERE source_device_uuid<>@device ORDER BY sync_id DESC LIMIT 1",
            new { device=settings.DeviceUuid });
        var state=await ReadServerAsync(cursor,baseline);
        state.LocalCursor=cursor;
        return state;
    }

    public async Task EnsureAutomaticAsync()
    {
        await using var db=CustomerPackageService.CreateConnection(settings);
        await db.OpenAsync();
        long cursor=await ReadCursorAsync(db);
        var candidate=await db.QuerySingleOrDefaultAsync<Boundary>(
            "SELECT sync_id Id,event_uuid EventUuid FROM tbl_sync_send WHERE source_device_uuid<>@device ORDER BY sync_id DESC LIMIT 1",
            new { device=settings.DeviceUuid });
        var state=await ReadServerAsync(cursor,candidate?.EventUuid);
        string scope=settings.OnlineDatabase.Trim().ToLowerInvariant()+"|"+(state.StreamAnchor??"");
        string? prior=await db.QuerySingleOrDefaultAsync<string?>("SELECT state_value FROM tbl_sync_state WHERE state_key='ReceiveCounterScope'");
        // تغيّر عنوان IP وحده لا يغير النطاق؛ الهوية مرتبطة بالقاعدة وسجلها الفعلي.
        if(prior!=null && prior!=scope && !prior.EndsWith("|",StringComparison.Ordinal))
            throw new InvalidOperationException("تغيرت قاعدة السيرفر أو أُعيد إنشاؤها. افتح «عداد الاستقبال» لمراجعة البداية؛ لم تتغير بياناتك أو العدادات.");
        long target=cursor;
        if(prior==null)
        {
            string? receipt=await db.QuerySingleOrDefaultAsync<string?>("SELECT event_uuid FROM tbl_sync_receive WHERE server_event_id=@cursor",new {cursor});
            bool trustedCursor=cursor<=state.ServerMaximum && receipt!=null && string.Equals(receipt,state.CursorEventUuid,StringComparison.OrdinalIgnoreCase);
            // سجل إرسال منسوخ من السيرفر يحدد آخر حركة في نسخة الفرع، وليس آخر حركة الآن.
            if(!trustedCursor && candidate!=null && state.BaselineId==candidate.Id)
                target=Math.Max(0,candidate.Id);
            else if(cursor>state.ServerMaximum || (cursor>0 && state.CursorEventUuid==null))
                target=0;
        }
        else if(cursor>state.ServerMaximum)
            target=0;
        if(target!=cursor) await SaveCursorAsync(db,target,"Automatic");
        await SetStateAsync(db,"ReceiveCounterScope",scope);
    }

    public async Task SetManualStartAsync(long firstEvent)
    {
        var state=await InspectAsync();
        if(firstEvent<1 || firstEvent>state.ServerMaximum+1)
            throw new InvalidOperationException("اختر بداية بين 1 والحركة التالية لآخر حركة في السيرفر.");
        await using var db=CustomerPackageService.CreateConnection(settings); await db.OpenAsync();
        await SaveCursorAsync(db,firstEvent-1,"Manual");
        await SetStateAsync(db,"ReceiveCounterScope",settings.OnlineDatabase.Trim().ToLowerInvariant()+"|"+(state.StreamAnchor??""));
    }

    private async Task<ReceiveCounterSnapshot> ReadServerAsync(long cursor,string? baseline)
    {
        if(readServer!=null)return await readServer(cursor,baseline);
        using var http=new HttpClient { BaseAddress=new Uri(settings.ApiUrl.TrimEnd('/')+"/"),Timeout=TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("X-Database",settings.OnlineDatabase);
        DeviceAuthorizationClient.AddDeviceHeaders(http);
        using var response=await http.GetAsync("api/sync2/receive-counter?deviceUuid="+Uri.EscapeDataString(settings.DeviceUuid)+
            "&afterId="+cursor+"&baselineEventUuid="+Uri.EscapeDataString(baseline??""));
        string body=await response.Content.ReadAsStringAsync();
        if(response.StatusCode==HttpStatusCode.NotFound)
            throw new InvalidOperationException("نسخة API على السيرفر قديمة. ركّب تحديث 1.2.0 قبل المزامنة؛ لم تُرسل أو تُتجاوز أي حركة.");
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException("تعذر فحص عداد السيرفر: "+body);
        var result=JsonSerializer.Deserialize<ReceiveCounterSnapshot>(body,new JsonSerializerOptions { PropertyNameCaseInsensitive=true });
        if(result==null || result.ProtocolVersion<2 || !result.ItemApplySupported)
            throw new InvalidOperationException("نسخة API لا تؤكد دعم تطبيق الأصناف. حدّث السيرفر قبل المزامنة.");
        return result;
    }
    private sealed class Boundary { public long Id { get; set; } public string EventUuid { get; set; }=""; }
    private static async Task<long> ReadCursorAsync(MySqlConnection db)=>
        await db.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(CAST(state_value AS UNSIGNED)),0) FROM tbl_sync_state WHERE state_key='LastReceivedId'");
    private static async Task SaveCursorAsync(MySqlConnection db,long cursor,string mode)
    {
        // نحفظ قيمة المؤشر السابقة للمراجعة؛ لا نحذف سجلات الاستقبال أو منع التكرار.
        await db.ExecuteAsync("INSERT INTO tbl_sync_state(state_key,state_value,updated_at) SELECT 'ReceiveCounterPrevious',state_value,NOW() FROM tbl_sync_state WHERE state_key='LastReceivedId' ON DUPLICATE KEY UPDATE state_value=VALUES(state_value),updated_at=NOW()");
        await SetStateAsync(db,"LastReceivedId",cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await SetStateAsync(db,"ReceiveCounterMode",mode);
    }
    private static Task SetStateAsync(MySqlConnection db,string key,string value)=>
        db.ExecuteAsync("INSERT INTO tbl_sync_state(state_key,state_value,updated_at) VALUES(@key,@value,NOW()) ON DUPLICATE KEY UPDATE state_value=@value,updated_at=NOW()",new {key,value});
}
