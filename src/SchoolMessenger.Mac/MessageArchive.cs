using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchoolMessenger.Mobile;

// This Mac copy is the message's home: the server drops a body once every party's PC has saved it.
// Copies are sealed with a key from the login keychain; that does not protect a compromised signed-in account.
public sealed class MessageArchive
{
    const string KeyName="message-archive-key";
    static Task<byte[]>? keyLoading;
    readonly MessengerApi api;
    readonly byte[] key;
    readonly string directory;
    readonly HashSet<string> recalled=[];
    // One synchronization at a time, even when a reconnection replaced the archive object mid-run.
    static readonly SemaphoreSlim synchronizing=new(1,1);
    public MessengerApi Owner=>api;
    MessageArchive(MessengerApi api,byte[] key)
    {
        this.api=api;this.key=key;
        directory=Path.Combine(FileSystem.AppDataDirectory,"messages",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(api.Address.AbsoluteUri+"|"+api.Session.GetProperty("user").GetProperty("id").GetString()))));
    }
    // Two first-run callers must share one key; a failed attempt is retried on the next call.
    public static async Task<MessageArchive> Open(MessengerApi api)
    {
        var loading=keyLoading??=Key();
        try{return new(api,await loading);}
        catch{if(keyLoading==loading)keyLoading=null;throw;}
    }
    static async Task<byte[]> Key()
    {
        var root=Path.Combine(FileSystem.AppDataDirectory,"messages");Directory.CreateDirectory(root);
        var file=Path.Combine(root,"archive.key");var marker=Path.Combine(root,"archive.keychain");
        if(File.Exists(file))return File.ReadAllBytes(file);
        if(await SecureStorage.Default.GetAsync(KeyName) is{} stored)return Convert.FromBase64String(stored);
        // A key the keychain cannot hand back right now must not be replaced: the copies sealed with it would be lost.
        if(File.Exists(marker))throw new InvalidOperationException("키체인에서 메시지 보관 열쇠를 읽지 못했습니다.");
        var created=RandomNumberGenerator.GetBytes(64);
        try{await SecureStorage.Default.SetAsync(KeyName,Convert.ToBase64String(created));File.WriteAllText(marker,"");return created;}
        catch(Exception error)when(error is not OperationCanceledException){}
        // Builds without the keychain entitlement (local and verification builds) keep the key in the app's own container.
        File.WriteAllBytes(file,created);File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);return created;
    }
    byte[] Seal(byte[] plain)
    {
        using var aes=Aes.Create();aes.Key=key[..32];var iv=RandomNumberGenerator.GetBytes(16);var cipher=aes.EncryptCbc(plain,iv);
        var sealedBytes=new byte[17+cipher.Length+32];sealedBytes[0]=1;iv.CopyTo(sealedBytes,1);cipher.CopyTo(sealedBytes,17);
        HMACSHA256.HashData(key.AsSpan(32),sealedBytes.AsSpan(0,17+cipher.Length),sealedBytes.AsSpan(17+cipher.Length));return sealedBytes;
    }
    byte[]? Unseal(byte[] data)
    {
        if(data.Length<17+16+32||data[0]!=1)return null;
        if(!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key.AsSpan(32),data.AsSpan(0,data.Length-32)),data.AsSpan(data.Length-32)))return null;
        using var aes=Aes.Create();aes.Key=key[..32];return aes.DecryptCbc(data.AsSpan(17,data.Length-17-32),data.AsSpan(1,16));
    }
    // Chat rooms, anonymous answers and to-do suggestions live beside the messages, sealed the same way, under their own
    // extensions so the message search never reads them.
    string Named(string kind,string id){if(!Guid.TryParseExact(id,"N",out _))throw new InvalidOperationException("번호가 올바르지 않습니다.");return Path.Combine(directory,id+"."+kind);}
    void WriteSealed(string path,string json)
    {
        Directory.CreateDirectory(directory);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,Seal(Encoding.UTF8.GetBytes(json)));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    JsonElement? ReadSealed(string path)
    {
        if(!File.Exists(path))return null;
        try{if(Unseal(File.ReadAllBytes(path)) is not{} plain)return null;using var document=JsonDocument.Parse(plain);return document.RootElement.Clone();}
        catch(Exception error)when(error is IOException or JsonException or CryptographicException){return null;}
    }
    static string ArrayText(IEnumerable<JsonElement> items)=>"["+string.Join(",",items.Select(i=>i.GetRawText()))+"]";
    static StringContent Body(JsonObject value)=>new(value.ToJsonString(),Encoding.UTF8,"application/json");
    readonly Dictionary<string,JsonElement[]> rooms=[];
    static readonly SemaphoreSlim keeping=new(1,1);
    static long Sequence(JsonElement entry)=>entry.GetProperty("sequence").GetInt64();
    // ponytail: one file per room rewritten on every save, as on Windows; split by month if rooms grow past a few MB.
    public JsonElement[] ChatEntries(string room)
    {
        if(!rooms.TryGetValue(room,out var entries))rooms[room]=entries=ReadSealed(Named("chat",room)) is{ValueKind:JsonValueKind.Array} kept?kept.EnumerateArray().ToArray():[];
        return entries;
    }
    public string? ChatPreview(string room)=>ChatEntries(room).LastOrDefault() is{ValueKind:JsonValueKind.Object} last?SchoolMessenger.Shared.MessageText.Excerpt(last.GetProperty("body").GetString(),80):null;
    // A room's number counts what can be read here: messages only another PC holds would stay unread for good.
    public async Task<JsonElement[]> LocalRooms(JsonElement server)
    {
        var me=api.Session.GetProperty("user").GetProperty("id").GetString();var result=new List<JsonElement>();
        foreach(var room in server.EnumerateArray())
        {
            var id=room.GetProperty("id").GetString()!;
            if(room.GetProperty("unread").GetInt32()>0)try{await KeepRoom(id);}catch(MessengerApiRefusal){}
            var lastRead=room.TryGetProperty("lastRead",out var read)&&read.TryGetInt64(out var value)?value:0;
            var node=JsonNode.Parse(room.GetRawText())!;
            node["unread"]=ChatEntries(id).Count(e=>Sequence(e)>lastRead&&e.GetProperty("senderId").GetString()!=me);
            if(ChatPreview(id) is{} preview)node["preview"]=preview;
            using var document=JsonDocument.Parse(node.ToJsonString());result.Add(document.RootElement.Clone());
        }
        return result.ToArray();
    }
    public void DropRoom(string room){rooms.Remove(room);File.Delete(Named("chat",room));}
    // Saved first and confirmed after, so a crash in between only means fetching the same messages again.
    public async Task KeepRoom(string room)
    {
        await keeping.WaitAsync();
        try
        {
            var kept=ChatEntries(room).ToDictionary(Sequence);long newest=0;long? before=null;
            while(true)
            {
                var page=(await api.Get($"api/chats/{room}/messages"+(before is{} older?"?before="+older.ToString(CultureInfo.InvariantCulture):""))).EnumerateArray().ToArray();
                foreach(var entry in page){kept[Sequence(entry)]=entry;newest=Math.Max(newest,Sequence(entry));}
                if(page.Length<100)break;before=Sequence(page[0]);
            }
            if(newest==0)return;
            var all=kept.Values.OrderBy(Sequence).ToArray();WriteSealed(Named("chat",room),ArrayText(all));rooms[room]=all;
            // A server not yet updated cannot take the confirmation; it hands the same messages out again until it can.
            try{using var content=Body(new JsonObject{["sequence"]=newest});await api.Send($"api/chats/{room}/stored",content:content);}catch(MessengerApiRefusal){}
        }
        finally{keeping.Release();}
    }
    public int BallotCount(string survey)=>ReadSealed(Named("ballots",survey)) is{ValueKind:JsonValueKind.Array} kept?kept.GetArrayLength():0;
    // Anonymous answers move here from the server. The answers kept here are all the owner will ever see.
    public async Task<JsonElement> KeepBallots(JsonElement server,string survey)
    {
        await keeping.WaitAsync();
        try
        {
            var path=Named("ballots",survey);var fresh=server.GetProperty("responses").EnumerateArray().ToArray();
            JsonElement[] old=ReadSealed(path) is{ValueKind:JsonValueKind.Array} kept?kept.EnumerateArray().ToArray():[];
            var merged=old.Concat(fresh).DistinctBy(r=>r.GetProperty("id").GetString()).OrderBy(r=>r.GetProperty("id").GetString(),StringComparer.Ordinal).ToArray();
            if(fresh.Length>0)
            {
                WriteSealed(path,ArrayText(merged));
                foreach(var part in fresh.Select(r=>r.GetProperty("id").GetString()).Chunk(500))
                {using var content=Body(new JsonObject{["ids"]=new JsonArray(part.Select(id=>(JsonNode?)JsonValue.Create(id)).ToArray())});await api.Send($"api/surveys/{survey}/ballots/stored",content:content);}
            }
            var node=JsonNode.Parse(server.GetRawText())!;node["responses"]=JsonNode.Parse(ArrayText(merged));
            using var document=JsonDocument.Parse(node.ToJsonString());return document.RootElement.Clone();
        }
        finally{keeping.Release();}
    }
    // To-do suggestions are worked out on this Mac from its own copies, so their wording never reaches the server.
    // Messages from before suggestions moved to the PCs are not looked through again.
    static readonly long SuggestionsFrom=new DateTimeOffset(2026,11,4,0,0,0,TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
    JsonObject? suggestions;
    string SuggestionsPath=>Path.Combine(directory,"suggestions.book");
    JsonObject Book()=>suggestions??=ReadSealed(SuggestionsPath) is{ValueKind:JsonValueKind.Object} kept?(JsonObject)JsonNode.Parse(kept.GetRawText())!:new JsonObject();
    // Decided suggestions stay as an empty entry so the same message is not suggested twice.
    void Suggest(JsonElement detail)
    {
        var me=api.Session.GetProperty("user").GetProperty("id").GetString();var m=detail.GetProperty("message");var id=m.GetProperty("id").GetString()!;var book=Book();
        var body=m.GetProperty("body").GetString()??"";var created=m.GetProperty("createdAt").GetInt64();
        if(Purged(detail)||m.GetProperty("senderId").GetString()==me||created<SuggestionsFrom||book.ContainsKey(id)||detail.GetProperty("recipients").EnumerateArray().All(r=>r.GetProperty("id").GetString()!=me)||body.StartsWith("[시스템 자동 알림]",StringComparison.Ordinal))return;
        book[id]=new JsonArray(SchoolMessenger.Desktop.TaskCandidates.Candidates(body,created).Select(c=>(JsonNode?)new JsonObject{["id"]=Guid.NewGuid().ToString("N"),["messageId"]=id,["title"]=c.Title,["evidence"]=c.Evidence,["dueDate"]=c.DueDate,["dueTime"]=c.DueTime,["createdAt"]=created}).ToArray());
        WriteSealed(SuggestionsPath,book.ToJsonString());
    }
    public JsonElement[] AllSuggestions()
    {
        using var document=JsonDocument.Parse(Book().ToJsonString());
        return document.RootElement.EnumerateObject().SelectMany(p=>p.Value.EnumerateArray()).Select(s=>s.Clone())
            .OrderBy(s=>s.GetProperty("dueDate").ValueKind!=JsonValueKind.String).ThenBy(s=>s.GetProperty("dueDate").ValueKind==JsonValueKind.String?s.GetProperty("dueDate").GetString():"",StringComparer.Ordinal)
            .ThenBy(s=>s.GetProperty("dueTime").ValueKind!=JsonValueKind.String).ThenBy(s=>s.GetProperty("dueTime").ValueKind==JsonValueKind.String?s.GetProperty("dueTime").GetString():"",StringComparer.Ordinal).ToArray();
    }
    public void Decide(string message,string suggestion)
    {
        var book=Book();if(book[message] is not JsonArray rows)return;
        book[message]=new JsonArray(rows.Where(s=>(string?)s?["id"]!=suggestion).Select(s=>s?.DeepClone()).ToArray());WriteSealed(SuggestionsPath,book.ToJsonString());
    }
    // Messages being written are kept sealed here too, one file each, as the Windows 임시저장 does.
    public void SaveDraft(string id,JsonObject draft){draft["savedAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();WriteSealed(Named("draft",id),draft.ToJsonString());}
    public void DeleteDraft(string id){var path=Named("draft",id);if(File.Exists(path))File.Delete(path);}
    public JsonElement? Draft(string id)=>ReadSealed(Named("draft",id));
    public JsonElement[] Drafts()=>!Directory.Exists(directory)?[]:Directory.EnumerateFiles(directory,"*.draft").Select(p=>Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(p),"N",out _)?ReadSealed(p):null)
        .OfType<JsonElement>().Where(d=>d.ValueKind==JsonValueKind.Object).OrderByDescending(d=>d.TryGetProperty("savedAt",out var at)&&at.TryGetInt64(out var value)?value:0).ToArray();
    static string Id(JsonElement detail)=>detail.GetProperty("message").GetProperty("id").GetString()!;
    public static bool Purged(JsonElement detail)=>detail.GetProperty("message").TryGetProperty("purged",out var purged)&&purged.ValueKind==JsonValueKind.True;
    string FilePath(string id){if(!Guid.TryParseExact(id,"N",out _))throw new InvalidOperationException("메시지 번호가 올바르지 않습니다.");return Path.Combine(directory,id+".sealed");}
    public void Save(JsonElement detail)
    {
        var id=Id(detail);if(recalled.Contains(id)||Purged(detail))return;
        Directory.CreateDirectory(directory);var path=FilePath(id);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,Seal(Encoding.UTF8.GetBytes(detail.GetRawText())));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        // A suggestion that cannot be worked out never stops the copy from being kept.
        try{Suggest(detail);}catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or JsonException or IOException or System.Text.RegularExpressions.RegexMatchTimeoutException){}
    }
    public void Remove(string id){recalled.Add(id);File.Delete(FilePath(id));if(Book().Remove(id))WriteSealed(SuggestionsPath,Book().ToJsonString());}
    public JsonElement? Load(string id)
    {
        var path=FilePath(id);if(!File.Exists(path))return null;
        try{if(Unseal(File.ReadAllBytes(path)) is not{} plain)return null;using var document=JsonDocument.Parse(plain);return document.RootElement.Clone();}
        catch(Exception error)when(error is IOException or JsonException or CryptographicException){return null;}
    }
    // Server answers carry fresh receipts and attachment state; the body comes from here once the server let it go.
    public JsonElement Merge(JsonElement server)
    {
        if(!Purged(server)){Save(server);return server;}
        if(Load(Id(server)) is not{} local)return server;
        var merged=JsonNode.Parse(server.GetRawText())!;var message=merged["message"]!;var copy=local.GetProperty("message");
        message["body"]=copy.GetProperty("body").GetString();
        // The server drops the title as well once every PC saved the message; this Mac's copy still has it.
        if(string.IsNullOrEmpty((string?)message["title"])&&copy.TryGetProperty("title",out var title))message["title"]=title.GetString();
        message["format"]=copy.TryGetProperty("format",out var format)?JsonNode.Parse(format.GetRawText()):null;
        message["purged"]=false;
        using var document=JsonDocument.Parse(merged.ToJsonString());return document.RootElement.Clone();
    }
    public IEnumerable<JsonElement> Search(string query)
    {
        if(!Directory.Exists(directory))yield break;
        foreach(var path in Directory.EnumerateFiles(directory,"*.sealed"))
            if(Load(Path.GetFileNameWithoutExtension(path)) is{} value&&value.GetProperty("message") is var m&&(m.GetProperty("title").GetString()!.Contains(query,StringComparison.CurrentCultureIgnoreCase)||m.GetProperty("body").GetString()!.Contains(query,StringComparison.CurrentCultureIgnoreCase)))yield return value;
    }
    async Task Keep(string id)
    {
        if(Load(id) is null)
        {
            // A network failure propagates so the sync cursor stays put; a refusal (recalled, no access) is final.
            JsonElement value;try{value=await api.Get("api/messages/"+id+"?peek=true");}catch(MessengerApiRefusal error)when(error.Status is 403 or 404 or 410){return;}
            if(Purged(value))return;Save(value);
        }
        try{await api.Send("api/messages/"+id+"/stored");}catch(MessengerApiRefusal){}
    }
    public async Task Synchronize()
    {
        await synchronizing.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);var cursorPath=Path.Combine(directory,"cursor");
            var cursor=File.Exists(cursorPath)&&long.TryParse(File.ReadAllText(cursorPath),NumberStyles.None,CultureInfo.InvariantCulture,out var saved)?saved:0;
            for(var page=0;page<10;page++)
            {
                var changes=await api.Get("api/messages/changes?after="+cursor.ToString(CultureInfo.InvariantCulture));
                foreach(var entry in changes.GetProperty("events").EnumerateArray())
                    switch(entry.GetProperty("kind").GetString())
                    {
                        case "MessageRecalled":Remove(entry.GetProperty("resourceId").GetString()!);break;
                        case "NewMessage" or "SentMessage":await Keep(entry.GetProperty("resourceId").GetString()!);break;
                    }
                cursor=changes.GetProperty("cursor").GetInt64();
                var temporary=cursorPath+".tmp";File.WriteAllText(temporary,cursor.ToString(CultureInfo.InvariantCulture));File.Move(temporary,cursorPath,true);
                if(!changes.GetProperty("hasMore").GetBoolean())break;
            }
            // Once per Mac: keep the messages this account received or sent before PCs became the message store.
            var backfilled=Path.Combine(directory,"backfilled");if(File.Exists(backfilled))return;
            foreach(var box in new[]{"received","sent"})
                for(var offset=0;;offset+=100)
                {
                    var rows=await api.Get("api/messages?box="+box+"&offset="+offset.ToString(CultureInfo.InvariantCulture));
                    foreach(var row in rows.EnumerateArray())await Keep(row.GetProperty("id").GetString()!);
                    if(rows.GetArrayLength()<100)break;
                }
            File.WriteAllText(backfilled,"");
        }
        finally{synchronizing.Release();}
    }
    internal void Verify(JsonElement detail)
    {
        var id=Id(detail);var body=detail.GetProperty("message").GetProperty("body").GetString()!;
        Save(detail);var bytes=File.ReadAllBytes(FilePath(id));
        if(Load(id)?.GetProperty("message").GetRawText()!=detail.GetProperty("message").GetRawText()||bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(body))>=0)throw new InvalidOperationException("메시지 사본 암호화·복원 검증 실패");
        var purged=JsonNode.Parse(detail.GetRawText())!;purged["message"]!["body"]="";purged["message"]!["format"]=null;purged["message"]!["purged"]=true;
        using(var document=JsonDocument.Parse(purged.ToJsonString()))if(Merge(document.RootElement.Clone()).GetProperty("message").GetProperty("body").GetString()!=body)throw new InvalidOperationException("서버가 본문을 지운 뒤 Mac 사본으로 복원하지 못했습니다.");
        Remove(id);Save(detail);
        if(File.Exists(FilePath(id)))throw new InvalidOperationException("회수된 메시지 사본이 다시 저장되었습니다.");
        recalled.Remove(id);Save(detail);
    }
}
