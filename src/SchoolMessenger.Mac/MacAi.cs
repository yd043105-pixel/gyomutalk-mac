using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchoolMessenger.Mobile;

// The Windows Ai.cs for the Mac: AI for a teacher's own writing. Each teacher connects their own account on their own Mac:
// ChatGPT by signing in (their Plus or Pro plan, OpenAI's sign-in for apps that run locally), OpenRouter or Google AI
// Studio by pasting a key. It is kept in this Mac's keychain only; the school server never sees it.
static class MacAi
{
    public const string ChatGpt="chatgpt",OpenRouter="openrouter",Google="google";
    public static readonly string[] Providers=[ChatGpt,OpenRouter,Google];
    public static string Name(string provider)=>provider switch{ChatGpt=>"ChatGPT",OpenRouter=>"OpenRouter",_=>"Google AI Studio"};
    public const string ManageUsage="https://chatgpt.com/settings/usage";
    public const string OpenRouterKeys="https://openrouter.ai/keys",GoogleKeys="https://aistudio.google.com/apikey";
    const string StoreKey="mac-ai-settings";
    static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(90)};
    static readonly SemaphoreSlim Gate=new(1,1);
    static JsonObject settings=new();
    static bool loaded;
    // Set when the keychain refused the settings: the connection then lasts until the app quits.
    public static string? StorageWarning{get;private set;}
    public static async Task Load()
    {
        if(loaded)return;loaded=true;
        try{if(await SecureStorage.Default.GetAsync(StoreKey) is{} text&&JsonNode.Parse(text) is JsonObject read)settings=read;}
        catch(Exception error)when(error is not OperationCanceledException){settings=new();}
    }
    static async Task Save()
    {
        try{await SecureStorage.Default.SetAsync(StoreKey,settings.ToJsonString());StorageWarning=null;}
        catch(Exception error)when(error is not OperationCanceledException){StorageWarning="이 Mac 빌드에서는 키체인에 AI 연결을 저장하지 못했습니다. 프로그램을 끄면 다시 연결해야 합니다.";}
    }
    // A node can sit under one parent only, so the accounts object is made once and then reused.
    static JsonObject Accounts(){if(settings["accounts"] is JsonObject found)return found;var made=new JsonObject();settings["accounts"]=made;return made;}
    static JsonObject Account(string provider)=>settings["accounts"]?[provider] as JsonObject??new JsonObject();
    static string? Value(JsonObject account,string name)=>account[name]?.GetValue<string>();
    public static string? Use=>(string?)settings["use"];
    public static bool Connected(string provider)=>provider==ChatGpt?Value(Account(provider),"refresh") is not null:Value(Account(provider),"key") is not null;
    public static string? Model(string provider)=>Value(Account(provider),"model");
    public static string? Email=>Value(Account(ChatGpt),"email");
    public static string? InUse=>Use is{} use&&Connected(use)&&Model(use) is{Length:>0}?use:null;
    public static bool Ready=>InUse is not null;
    static async Task Put(string provider,JsonObject? account)
    {
        var accounts=Accounts();
        if(account is null)accounts.Remove(provider);else accounts[provider]=account;
        var use=Use;
        if(account is null&&use==provider)settings["use"]=Providers.FirstOrDefault(p=>p!=provider&&accounts.ContainsKey(p));
        else if(use is null&&account is not null)settings["use"]=provider;
        await Save();
    }
    public static async Task Choose(string provider,string model){var account=(JsonObject)Account(provider).DeepClone();account["model"]=model;await Put(provider,account);settings["use"]=provider;await Save();}
    public static Task SignOut(string provider)=>Put(provider,provider==ChatGpt&&Value(Account(ChatGpt),"clientId") is{} id?new JsonObject{["clientId"]=id}:null);

    // Test runs point these at a local stand-in; real runs use the companies' addresses.
    static string Base(string name,string real)=>Environment.GetEnvironmentVariable("YY_AI_"+name)??real;
    static string AuthBase=>Base("OPENAI_AUTH","https://auth.openai.com");
    static string OpenAiBase=>Base("OPENAI_API","https://api.openai.com/v1");
    static string OpenRouterBase=>Base("OPENROUTER","https://openrouter.ai/api/v1");
    static string GoogleBase=>Base("GOOGLE","https://generativelanguage.googleapis.com/v1beta/openai");
    static StringContent Json(JsonObject value)=>new(value.ToJsonString(),Encoding.UTF8,"application/json");

    public static async Task<string> Ask(string instructions,string text,CancellationToken cancel)
    {
        var provider=InUse??throw new MacAiException("내 정보 → AI 연결에서 먼저 AI를 연결하고 모델을 고르세요.");
        try{return provider==ChatGpt?await AskChatGpt(instructions,text,cancel):await AskCompatible(provider,instructions,text,cancel);}
        catch(HttpRequestException){throw new MacAiException(Name(provider)+"에 연결하지 못했습니다. 인터넷 연결이나 학교 방화벽을 확인하세요.");}
        catch(TaskCanceledException)when(!cancel.IsCancellationRequested){throw new MacAiException(Name(provider)+"의 응답이 너무 늦습니다. 잠시 뒤 다시 하세요.");}
    }
    static async Task<string> AskCompatible(string provider,string instructions,string text,CancellationToken cancel)
    {
        var account=Account(provider);
        using var request=new HttpRequestMessage(HttpMethod.Post,(provider==OpenRouter?OpenRouterBase:GoogleBase)+"/chat/completions")
        {Content=Json(new JsonObject{["model"]=Value(account,"model"),["messages"]=new JsonArray(new JsonObject{["role"]="system",["content"]=instructions},new JsonObject{["role"]="user",["content"]=text})})};
        Authorize(request,provider,Value(account,"key")!);
        using var response=await Http.SendAsync(request,cancel);var body=await response.Content.ReadAsStringAsync(cancel);
        if(!response.IsSuccessStatusCode)throw Failure(provider,response.StatusCode,body);
        using var json=JsonDocument.Parse(body);
        var answer=json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(answer)?throw new MacAiException(Name(provider)+"가 빈 답을 보냈습니다. 다른 모델을 골라 보세요."):answer.Trim();
    }
    static void Authorize(HttpRequestMessage request,string provider,string key)
    {
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        // OpenRouter asks apps to name themselves; header values must be plain ASCII.
        if(provider==OpenRouter){request.Headers.TryAddWithoutValidation("HTTP-Referer","https://github.com/yd043105-pixel/gyomutalk-mac");request.Headers.TryAddWithoutValidation("X-Title","GyomuTalk");}
    }
    static MacAiException Failure(string provider,HttpStatusCode status,string body)
    {
        var detail="";
        try
        {
            using var json=JsonDocument.Parse(body);var root=json.RootElement;
            var error=root.ValueKind==JsonValueKind.Array&&root.GetArrayLength()>0?root[0].GetProperty("error"):root.GetProperty("error");
            detail=error.ValueKind==JsonValueKind.String?error.GetString()??"":error.TryGetProperty("message",out var m)?m.GetString()??"":"";
        }
        catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException){}
        var what=status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"열쇠가 맞지 않거나 만료되었습니다. 내 정보 → AI 연결에서 다시 연결하세요.",
            HttpStatusCode.PaymentRequired=>"이 계정의 잔액·크레딧이 부족합니다. 무료 모델을 고르거나 충전하세요.",
            HttpStatusCode.TooManyRequests=>"사용 한도에 도달했습니다. 잠시 뒤 다시 하거나 다른 모델을 고르세요.",
            HttpStatusCode.NotFound=>"고른 모델을 찾지 못했습니다. 내 정보 → AI 연결에서 모델을 다시 고르세요.",
            _=>"요청을 처리하지 못했습니다."
        };
        return new MacAiException(Name(provider)+": "+what+(detail.Length>0?" ("+(detail.Length>160?detail[..160]+"…":detail)+")":""));
    }
    public static async Task<string[]> Models(string provider,CancellationToken cancel)
    {
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,(provider switch{ChatGpt=>OpenAiBase,OpenRouter=>OpenRouterBase,_=>GoogleBase})+"/models");
            if(provider==ChatGpt)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await AccessToken(cancel));
            else Authorize(request,provider,Value(Account(provider),"key")!);
            using var response=await Http.SendAsync(request,cancel);var body=await response.Content.ReadAsStringAsync(cancel);
            if(!response.IsSuccessStatusCode)throw Failure(provider,response.StatusCode,body);
            using var json=JsonDocument.Parse(body);
            var ids=json.RootElement.GetProperty("data").EnumerateArray()
                .Where(m=>provider!=ChatGpt||!m.TryGetProperty("visibility",out var v)||v.GetString()=="list")
                .Select(m=>m.GetProperty("id").GetString()??"").Select(id=>id.StartsWith("models/",StringComparison.Ordinal)?id[7..]:id)
                // Google lists embedding, image and speech models too; only text models can rewrite a message.
                .Where(id=>id.Length>0&&(provider!=Google||id.Contains("gemini",StringComparison.OrdinalIgnoreCase)&&!id.Contains("embedding",StringComparison.OrdinalIgnoreCase)&&!id.Contains("tts",StringComparison.OrdinalIgnoreCase)&&!id.Contains("image",StringComparison.OrdinalIgnoreCase)))
                .Distinct().ToArray();
            // Free OpenRouter models first, so a school can start without paying.
            return provider==OpenRouter?ids.OrderBy(id=>id.EndsWith(":free",StringComparison.Ordinal)?0:1).ThenBy(id=>id,StringComparer.Ordinal).ToArray():ids.OrderBy(id=>id,StringComparer.Ordinal).ToArray();
        }
        catch(HttpRequestException){throw new MacAiException(Name(provider)+"에 연결하지 못했습니다. 인터넷 연결이나 학교 방화벽을 확인하세요.");}
        catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException and not MacAiException){throw new MacAiException(Name(provider)+"의 모델 목록을 읽지 못했습니다.");}
    }
    // A pasted key is checked by listing the models with it before it is kept.
    public static async Task<string[]> Connect(string provider,string key,CancellationToken cancel)
    {
        key=key.Trim();
        if(key.Length<10||key.Any(char.IsWhiteSpace))throw new MacAiException("열쇠를 끝까지 정확히 붙여넣으세요.");
        var before=(JsonObject)settings.DeepClone();
        var account=(JsonObject)Account(provider).DeepClone();account["key"]=key;
        Accounts()[provider]=account;
        try{var models=await Models(provider,cancel);settings["use"]??=provider;await Save();return models;}
        catch{settings=before;throw;}
    }

    // ── ChatGPT sign-in (OAuth with PKCE on a 127.0.0.1 loopback) ──
    const string Scopes="openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    static string Url64(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    static string Random64()=>Url64(RandomNumberGenerator.GetBytes(32));
    public static async Task SignIn(Func<string,Task> openBrowser,CancellationToken cancel)
    {
        settings["hostId"]??="urn:uuid:"+Guid.NewGuid();
        var saved=Account(ChatGpt);var listener=Listen();
        try
        {
            var redirect=$"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback";
            string verifier=Random64(),state=Random64(),nonce=Random64();
            var challenge=Url64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var query=new List<(string,string)>
            {
                ("client_id",Value(saved,"clientId")??"dynamic_agent_client"),("response_type","code"),("redirect_uri",redirect),("scope",Scopes),
                ("resource","https://api.openai.com/v1"),("state",state),("nonce",nonce),("code_challenge",challenge),
                ("code_challenge_method","S256"),("ext_agent_host_id",(string)settings["hostId"]!)
            };
            if(Value(saved,"clientId") is null)query.Add(("agent_name_hint","교무톡"));
            else if(Value(saved,"idToken") is{} hint)query.Add(("id_token_hint",hint));
            await openBrowser(AuthBase+"/api/accounts/authorize?"+string.Join("&",query.Select(q=>q.Item1+"="+Uri.EscapeDataString(q.Item2))));
            var callback=await Callback(listener,cancel);
            if(callback.GetValueOrDefault("state")!=state)throw new MacAiException("로그인 응답이 이 요청과 맞지 않습니다. 다시 로그인하세요.");
            if(callback.ContainsKey("error"))throw new MacAiException(callback["error"]=="access_denied"?"ChatGPT 로그인을 취소했거나 플랜 사용을 허용하지 않았습니다.":"ChatGPT 로그인에 실패했습니다: "+callback["error"]);
            var clientId=callback.GetValueOrDefault("client_id");
            if(Value(saved,"clientId") is{} known&&clientId is not null&&clientId!=known)throw new MacAiException("로그인 응답의 앱 정보가 맞지 않습니다. 다시 로그인하세요.");
            clientId??=Value(saved,"clientId")??throw new MacAiException("ChatGPT가 이 프로그램을 등록하지 않았습니다. 다시 로그인하세요.");
            var code=callback.GetValueOrDefault("code")??throw new MacAiException("ChatGPT 로그인 응답에 승인 코드가 없습니다.");
            var tokens=await Token(new(){["grant_type"]="authorization_code",["client_id"]=clientId,["code"]=code,["code_verifier"]=verifier,["redirect_uri"]=redirect,["resource"]="https://api.openai.com/v1"},cancel);
            if(tokens.Scope is{} scope&&!scope.Split(' ').Contains("chatgpt.tokens.use.direct"))throw new MacAiException("ChatGPT 플랜 사용을 허용하지 않았습니다. 다시 로그인해 ‘플랜 사용’을 허용하세요.");
            var account=(JsonObject)saved.DeepClone();
            account["clientId"]=clientId;account["refresh"]=tokens.Refresh;account["access"]=tokens.Access;account["expires"]=tokens.Expires;
            account["idToken"]=tokens.IdToken??Value(saved,"idToken");account["email"]=EmailOf(tokens.IdToken)??Value(saved,"email");
            await Put(ChatGpt,account);
        }
        finally{listener.Stop();}
    }
    // The documented callback is http://127.0.0.1:1455/auth/callback; only the port may change when 1455 is taken.
    static TcpListener Listen()
    {
        foreach(var port in new[]{1455,0})
        {
            var listener=new TcpListener(IPAddress.Loopback,port);
            try{listener.Start();return listener;}catch(SocketException)when(port!=0){}
        }
        throw new MacAiException("로그인을 받을 자리를 열지 못했습니다.");
    }
    static async Task<Dictionary<string,string>> Callback(TcpListener listener,CancellationToken cancel)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while(true)
        {
            using var client=await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream=client.GetStream();
            var reader=new StreamReader(stream,Encoding.ASCII);
            var line=await reader.ReadLineAsync(timeout.Token)??"";
            var parts=line.Split(' ');var target=parts.Length>1?parts[1]:"";
            var found=target.StartsWith("/auth/callback?",StringComparison.Ordinal);
            var page=found?"<h2>교무톡 로그인이 끝났습니다.</h2><p>이 창을 닫고 교무톡으로 돌아가세요.</p>":"<p>교무톡</p>";
            var html=Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>교무톡</title><body style=\"font-family:-apple-system,sans-serif;padding:40px\">"+page);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n").Concat(html).ToArray(),timeout.Token);
            if(found)return target[(target.IndexOf('?')+1)..].Split('&',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2))
                .ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>p.Length>1?Uri.UnescapeDataString(p[1].Replace('+',' ')):"");
        }
    }
    sealed record Tokens(string Access,string? Refresh,long Expires,string? IdToken,string? Scope);
    static async Task<Tokens> Token(Dictionary<string,string> form,CancellationToken cancel)
    {
        using var response=await Http.PostAsync(AuthBase+"/api/accounts/oauth/token",new FormUrlEncodedContent(form),cancel);
        var body=await response.Content.ReadAsStringAsync(cancel);
        using var json=JsonDocument.Parse(body.Length==0?"{}":body);var root=json.RootElement;
        if(!response.IsSuccessStatusCode||!root.TryGetProperty("access_token",out var access))
        {
            var error=root.TryGetProperty("error",out var e)&&e.ValueKind==JsonValueKind.String?e.GetString():null;
            throw new MacAiException(form["grant_type"]=="refresh_token"?"ChatGPT 로그인이 만료되었습니다. 내 정보 → AI 연결에서 다시 로그인하세요.":"ChatGPT 로그인을 마치지 못했습니다"+(error is null?".":$" ({error})."),error);
        }
        string? Text(string name)=>root.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
        var seconds=root.TryGetProperty("expires_in",out var e2)&&e2.TryGetInt64(out var s)?s:3600;
        return new(access.GetString()!,Text("refresh_token"),DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds(),Text("id_token"),Text("scope"));
    }
    // The e-mail is only shown next to "연결됨"; nothing is decided from the ID token, so its signature is not checked here.
    static string? EmailOf(string? idToken)
    {
        try
        {
            if(idToken?.Split('.') is not[_,var payload,..])return null;
            var padded=payload.Replace('-','+').Replace('_','/');padded+=new string('=',(4-padded.Length%4)%4);
            using var json=JsonDocument.Parse(Convert.FromBase64String(padded));
            return json.RootElement.TryGetProperty("email",out var e)?e.GetString():null;
        }
        catch(Exception e)when(e is FormatException or JsonException){return null;}
    }
    static async Task<string> AccessToken(CancellationToken cancel)
    {
        await Gate.WaitAsync(cancel);
        try
        {
            var account=Account(ChatGpt);
            if(Value(account,"access") is{} access&&(account["expires"]?.GetValue<long>()??0)>DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds())return access;
            if(Value(account,"refresh") is not{} refresh||Value(account,"clientId") is not{} clientId)throw new MacAiException("내 정보 → AI 연결에서 ChatGPT로 다시 로그인하세요.");
            try
            {
                var tokens=await Token(new(){["grant_type"]="refresh_token",["refresh_token"]=refresh,["client_id"]=clientId},cancel);
                var updated=(JsonObject)account.DeepClone();updated["access"]=tokens.Access;updated["refresh"]=tokens.Refresh??refresh;updated["expires"]=tokens.Expires;updated["idToken"]=tokens.IdToken??Value(account,"idToken");
                await Put(ChatGpt,updated);return tokens.Access;
            }
            catch(MacAiException e)when(e.Code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused")
            {
                // The sign-in is gone; keep the issued app registration so signing in again skips registering.
                var cleared=(JsonObject)account.DeepClone();cleared.Remove("refresh");cleared.Remove("access");cleared["expires"]=0;await Put(ChatGpt,cleared);
                throw;
            }
        }
        finally{Gate.Release();}
    }
    static async Task<string> AskChatGpt(string instructions,string text,CancellationToken cancel)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,OpenAiBase+"/responses")
        {Content=Json(new JsonObject{["model"]=Model(ChatGpt),["instructions"]=instructions,["input"]=text,["store"]=false,["stream"]=true})};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await AccessToken(cancel));
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel);
        if(!response.IsSuccessStatusCode)throw ChatGptFailure(await response.Content.ReadAsStringAsync(cancel),response.StatusCode);
        var answer=new StringBuilder();
        using var reader=new StreamReader(await response.Content.ReadAsStreamAsync(cancel));
        while(await reader.ReadLineAsync(cancel) is{} line)
        {
            if(!line.StartsWith("data:",StringComparison.Ordinal))continue;
            var data=line[5..].Trim();if(data.Length==0||data=="[DONE]")continue;
            using var json=JsonDocument.Parse(data);
            var type=json.RootElement.TryGetProperty("type",out var t)?t.GetString():null;
            if(type=="response.output_text.delta")answer.Append(json.RootElement.GetProperty("delta").GetString());
            else if(type=="response.completed")return answer.Length>0?answer.ToString().Trim():throw new MacAiException("ChatGPT가 빈 답을 보냈습니다.");
            else if(type is "response.failed" or "response.incomplete" or "error")throw ChatGptFailure(data,null);
        }
        throw new MacAiException("ChatGPT 응답이 중간에 끊겼습니다. 다시 하세요.");
    }
    static MacAiException ChatGptFailure(string body,HttpStatusCode? status)
    {
        string? code=null;
        try
        {
            using var json=JsonDocument.Parse(body);var root=json.RootElement;
            var error=root.TryGetProperty("response",out var r)&&r.TryGetProperty("error",out var e1)?e1:root.TryGetProperty("error",out var e2)?e2:default;
            if(error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("code",out var c))code=c.GetString();
        }
        catch(JsonException){}
        return new MacAiException(code switch
        {
            "subscription_sharing_usage_limit_exceeded"=>"ChatGPT 플랜의 사용 한도에 도달했습니다. ChatGPT 설정 → 사용량에서 확인하세요.",
            "subscription_sharing_usage_unavailable"=>"ChatGPT 플랜을 지금 쓸 수 없습니다. 잠시 뒤 다시 하세요.",
            "subscription_sharing_user_not_eligible"=>"OpenAI가 이 ChatGPT 계정은 지금 이 프로그램에서 쓸 수 없다고 알려 왔습니다. 잠시 뒤 다시 하거나 OpenRouter·Google AI Studio를 연결하세요.",
            "subscription_sharing_invalid_user"=>"ChatGPT 로그인이 해제되었습니다. 내 정보 → AI 연결에서 다시 로그인하세요.",
            _ when status is HttpStatusCode.Unauthorized=>"ChatGPT 로그인이 해제되었습니다. 내 정보 → AI 연결에서 다시 로그인하세요.",
            _=>"ChatGPT가 요청을 처리하지 못했습니다"+(code is null?".":$" ({code}).")
        },code);
    }
    // ── What the teacher asks for ── the same three rewrites as Windows.
    public sealed record Rewrite(string Label,string Instructions);
    const string Keep=" 사실·날짜·시간·장소·숫자·사람 이름·요청 사항은 바꾸지 말고, 없는 내용을 지어내지 마세요. 설명이나 인사말 없이 고친 글만 출력하세요."+SchoolMessenger.Desktop.StyleCheck.Guide;
    public static readonly Rewrite[] Rewrites=
    [
        new("맞춤법·문장 다듬기","다음은 한국 고등학교 교사가 동료 교직원에게 보내는 업무 메시지 초안입니다. 맞춤법과 띄어쓰기를 고치고 어색한 문장을 자연스럽게 다듬으세요."+Keep),
        new("더 짧게","다음은 한국 고등학교 교사가 동료 교직원에게 보내는 업무 메시지 초안입니다. 핵심만 남겨 더 짧고 읽기 쉽게 줄이세요."+Keep),
        new("정중한 안내 말투로","다음은 한국 고등학교 교사가 동료 교직원에게 보내는 업무 메시지 초안입니다. 교직원 전체에게 보내는 정중한 업무 안내 말투(‘~하시기 바랍니다’)로 바꾸세요."+Keep),
    ];
}
sealed class MacAiException(string message,string? code=null):InvalidOperationException(message)
{
    public string? Code{get;}=code;
}
