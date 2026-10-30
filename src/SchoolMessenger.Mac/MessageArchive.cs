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
    static string Id(JsonElement detail)=>detail.GetProperty("message").GetProperty("id").GetString()!;
    public static bool Purged(JsonElement detail)=>detail.GetProperty("message").TryGetProperty("purged",out var purged)&&purged.ValueKind==JsonValueKind.True;
    string FilePath(string id){if(!Guid.TryParseExact(id,"N",out _))throw new InvalidOperationException("메시지 번호가 올바르지 않습니다.");return Path.Combine(directory,id+".sealed");}
    public void Save(JsonElement detail)
    {
        var id=Id(detail);if(recalled.Contains(id)||Purged(detail))return;
        Directory.CreateDirectory(directory);var path=FilePath(id);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,Seal(Encoding.UTF8.GetBytes(detail.GetRawText())));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void Remove(string id){recalled.Add(id);File.Delete(FilePath(id));}
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
