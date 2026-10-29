using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed class MessengerApi : IDisposable
{
    readonly CookieContainer cookies=new();
    readonly HttpClient http;
    readonly string key;
    internal CookieContainer SessionCookies=>cookies;
    public string ProductCode=>(Office?"teacher-":"school-")+(DeviceInfo.Platform==DevicePlatform.MacCatalyst?"macos":DeviceInfo.Platform==DevicePlatform.iOS?"ios":"android");
    public bool Office{get;}
    public Uri Address{get;}
    public JsonElement Session{get;private set;}
    public bool Remember{get;set;}
    public event Action? Invalidated;
    string csrf="";
    public MessengerApi(string address,bool office)
    {
        if(!Uri.TryCreate(address.TrimEnd('/')+"/",UriKind.Absolute,out var uri)||uri.AbsolutePath!="/"||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new InvalidOperationException("학교에서 안내한 서버 기본 주소를 입력하세요.");
        var localDebug=false;
#if DEBUG
        localDebug=uri.Scheme=="http"&&(uri.IsLoopback||uri.Host=="10.0.2.2");
#endif
        if(uri.Scheme!="https"&&!localDebug)throw new InvalidOperationException("서버 주소는 https://로 시작해야 합니다.");
        Address=uri;Office=office;key=SessionKey(uri,office);
#if FAMILY_APP
        if(office)throw new InvalidOperationException("학생·학부모용 학교 앱은 학교 소식 서버만 사용할 수 있습니다.");
#elif TEACHER_APP
        if(!office)throw new InvalidOperationException("교사 앱은 교직원 교무실 서버를 사용하세요.");
#endif
#if IOS || MACCATALYST
        // Default NSURLSession shares native cookies across clients and app restarts.
        // Each school space/account gets private native storage; only our Keychain data restores it.
        using var configuration=Foundation.NSUrlSessionConfiguration.EphemeralSessionConfiguration;
        configuration.URLCredentialStorage=null;
        configuration.TimeoutIntervalForRequest=90;
        configuration.TimeoutIntervalForResource=90;
        var handler=new System.Net.Http.NSUrlSessionHandler(configuration){CookieContainer=cookies,AllowAutoRedirect=false,DisableCaching=true};
#else
        var handler=new HttpClientHandler{CookieContainer=cookies,AllowAutoRedirect=false};
#endif
        http=new HttpClient(handler){BaseAddress=uri,Timeout=TimeSpan.FromSeconds(90)};
        http.DefaultRequestHeaders.Add("X-YY-Product",ProductCode);
        http.DefaultRequestHeaders.Add("X-YY-Version",AppInfo.Current.VersionString);
    }
    public async Task Restore()
    {
        try{var data=await SecureStorage.Default.GetAsync(key);if(data is null)return;foreach(var cookie in JsonSerializer.Deserialize(data,MobileJson.Default.MobileCookieArray)??[])if(cookie.Expires>DateTime.UtcNow)cookies.Add(Address,new Cookie(cookie.Name,cookie.Value,"/"){Expires=cookie.Expires,Secure=Address.Scheme=="https",HttpOnly=true});Remember=true;}
        catch{SecureStorage.Default.Remove(key);}
    }
    static string SessionKey(Uri uri,bool office)=>"session-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((office?"office:":"portal:")+uri.AbsoluteUri)));
    public static void ForgetRemembered(string address,bool office)
    {
        if(Uri.TryCreate(address.TrimEnd('/')+"/",UriKind.Absolute,out var uri))SecureStorage.Default.Remove(SessionKey(uri,office));
    }
    async Task Persist()
    {
        if(!Remember||Session.ValueKind!=JsonValueKind.Object||!Session.TryGetProperty("user",out var user)||user.ValueKind!=JsonValueKind.Object||
           user.TryGetProperty("activationRequired",out var activation)&&activation.GetBoolean()||user.TryGetProperty("passwordChangeRequired",out var change)&&change.GetBoolean())
        {SecureStorage.Default.Remove(key);return;}
        var name=Office?"SchoolMessenger.Session":"YyAnnouncements.Session";
        var list=cookies.GetCookies(Address).Cast<Cookie>().Where(c=>!c.Expired&&c.Expires>DateTime.Now&&c.Name.StartsWith(name,StringComparison.Ordinal)).Select(c=>new MobileCookie(c.Name,c.Value,c.Expires.ToUniversalTime())).ToArray();
        if(list.Length==0){SecureStorage.Default.Remove(key);return;}
        try{await SecureStorage.Default.SetAsync(key,JsonSerializer.Serialize(list,MobileJson.Default.MobileCookieArray));}
#if MACCATALYST
        catch(Exception error)when(error is not OperationCanceledException)
        {Remember=false;StorageWarning="이 Mac 빌드에서는 키체인에 로그인 정보를 저장하지 못했습니다. 현재 창에서만 로그인됩니다.";}
#else
        catch{throw;}
#endif
    }
    public string? StorageWarning{get;private set;}
    public async Task Refresh()
    {
        var previousShares=ShareFolder();
        var previouslyAuthenticated=Session.ValueKind==JsonValueKind.Object&&Session.GetProperty("user").ValueKind!=JsonValueKind.Null;
        Session=await Get("api/session");csrf=Session.GetProperty("csrfToken").GetString()!;
        if(Session.GetProperty("user").ValueKind==JsonValueKind.Null){DeleteShares(previousShares);SecureStorage.Default.Remove(key);if(previouslyAuthenticated)Invalidated?.Invoke();}
        else if(Session.GetProperty("user").TryGetProperty("activationRequired",out var initial)&&initial.GetBoolean())SecureStorage.Default.Remove(key);
        else if(Session.GetProperty("user").TryGetProperty("passwordChangeRequired",out var change)&&change.GetBoolean())SecureStorage.Default.Remove(key);
        else await Persist();
        if(Office&&Session.GetProperty("user").ValueKind==JsonValueKind.Object&&!Session.GetProperty("user").TryGetProperty("department",out _)){await Logout();throw new InvalidOperationException("교사 앱에는 교직원 교무실 서버 주소와 교직원 계정을 사용하세요.");}
#if FAMILY_APP
        if(Session.GetProperty("user").ValueKind==JsonValueKind.Object&&(!Session.GetProperty("user").TryGetProperty("role",out var familyRole)||familyRole.GetString() is not("parent" or "student"))){await Logout();throw new InvalidOperationException("학생·학부모용 학교 앱은 학교 소식 서버의 학생·학부모 계정으로 로그인하세요.");}
#endif
    }
    public async Task<JsonElement> Get(string path)
    {using var response=await http.GetAsync(path);await Check(response);return await Read(response);}
    public async Task<JsonElement> Send(string path,object? body=null,HttpContent? content=null)
    {
        if(csrf.Length==0)await Refresh();using var request=new HttpRequestMessage(HttpMethod.Post,path);request.Headers.Add("X-CSRF-TOKEN",csrf);var payload=body??new MobileEmpty();request.Content=content??JsonContent.Create(payload,MobileJson.Default.GetTypeInfo(payload.GetType())??throw new InvalidOperationException("지원하지 않는 요청입니다."));
        using var response=await http.SendAsync(request);await Check(response);var value=await Read(response);await Persist();return value;
    }
    public async Task Login(string username,string password,bool remember)
    {Remember=remember;await Refresh();await Send("api/login",new MobileLogin(username,password,remember));await Refresh();}
    public async Task Logout()
    {var folder=ShareFolder();try{await Send("api/logout");}finally{DeleteShares(folder);SecureStorage.Default.Remove(key);foreach(Cookie c in cookies.GetCookies(Address))c.Expired=true;Session=default;Invalidated?.Invoke();}}
    public async Task<JsonElement> Upload(FileResult file,bool external,string kind="notice")
    {
        using var content=new MultipartFormDataContent();if(!Office)content.Add(new StringContent(Guid.NewGuid().ToString("N")),"clientId");
        var stream=await file.OpenReadAsync();
        try
        {
            if(external)
            {
                if(Path.GetExtension(file.FileName).ToLowerInvariant() is not(".png"or".jpg"or".jpeg"))throw new InvalidOperationException("학교 공지는 JPG·PNG 이미지로 올려주세요.");
                var image=await ReadNoticeImage(stream);await stream.DisposeAsync();stream=new MemoryStream(image,false);
            }
            if(external)content.Add(new StringContent(kind),"kind");
            content.Add(new StreamContent(stream),"file",file.FileName);return await Send(external&&Office?"api/external-announcements/attachments":"api/attachments",content:content);
        }
        finally{await stream.DisposeAsync();}
    }
    static async Task<byte[]> ReadNoticeImage(Stream stream)
    {
        using var output=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer))>0){if(output.Length+count>200_000)throw new InvalidOperationException("학교 공지 이미지는 한 장당 200KB(0.2MB)까지입니다.");output.Write(buffer,0,count);}
        if(output.Length==0)throw new InvalidOperationException("빈 이미지는 올릴 수 없습니다.");return output.ToArray();
    }
    public async Task<byte[]> NoticeImage(string id)
    {
        for(var attempt=0;;attempt++)
        {
        using var request=new HttpRequestMessage(HttpMethod.Get,"api/attachments/"+id+"/view");request.Headers.CacheControl=new(){NoStore=true};
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        if(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable&&attempt<5)
        {response.Dispose();await Task.Delay(TimeSpan.FromSeconds(Math.Min(30,2<<attempt))+TimeSpan.FromMilliseconds(Random.Shared.Next(200,1200)));continue;}
        await Check(response);
        if(response.Content.Headers.ContentLength>200_000||response.Content.Headers.ContentType?.MediaType is not("image/png"or"image/jpeg"))throw new InvalidOperationException("학교 공지 이미지 형식을 확인하세요.");
        await using var stream=await response.Content.ReadAsStreamAsync();return await ReadNoticeImage(stream);
        }
    }
    public async Task ShareFile(string id,string name)
    {
        using var response=await http.GetAsync("api/attachments/"+id+"/download",HttpCompletionOption.ResponseHeadersRead);await Check(response);
        if(response.Content.Headers.ContentLength>100L*1024*1024)throw new InvalidOperationException("파일이 너무 큽니다.");
        var folder=ShareFolder();Directory.CreateDirectory(folder);var path=Path.Combine(folder,Guid.NewGuid().ToString("N")+"-"+Path.GetFileName(name));
        try{await using(var output=File.Create(path))await response.Content.CopyToAsync(output);await Share.Default.RequestAsync(new ShareFileRequest("첨부 저장·공유",new ShareFile(path)));}
        catch{if(File.Exists(path))File.Delete(path);throw;}
        // Android returns when the chooser opens; deleting here would break the selected recipient's stream.
    }
    public static void CleanupShares()
    {
        var folder=Path.Combine(FileSystem.CacheDirectory,"yy-share");if(!Directory.Exists(folder))return;
        foreach(var file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))if(File.GetLastWriteTimeUtc(file)<DateTime.UtcNow.AddDays(-1))try{File.Delete(file);}catch(IOException){}
    }
    string ShareFolder()=>Path.Combine(FileSystem.CacheDirectory,"yy-share",key,Session.ValueKind==JsonValueKind.Object&&Session.GetProperty("user").ValueKind==JsonValueKind.Object?Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Session.GetProperty("user").GetProperty("id").GetString()!))):"anonymous");
    static void DeleteShares(string folder){if(!Directory.Exists(folder))return;foreach(var file in Directory.EnumerateFiles(folder))try{File.Delete(file);}catch(IOException){}}
    async Task Check(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;if(response.StatusCode==HttpStatusCode.Unauthorized){DeleteShares(ShareFolder());SecureStorage.Default.Remove(key);Session=default;Invalidated?.Invoke();}
        string message=(int)response.StatusCode switch{401=>"로그인이 만료되었습니다. 다시 로그인하세요.",403=>"승인된 계정과 담당 학급 권한을 확인하세요.",429=>"요청이 많습니다. 잠시 후 다시 시도하세요.",_=>"서버 연결·학교 접속 권한을 확인하세요."};
        try{var data=await response.Content.ReadFromJsonAsync(MobileJson.Default.JsonElement);if(data.TryGetProperty("error",out var error))message=error.GetString()??message;}catch(JsonException){}
        throw new InvalidOperationException(message);
    }
    static async Task<JsonElement> Read(HttpResponseMessage response){var text=await response.Content.ReadAsStringAsync();if(string.IsNullOrEmpty(text))return default;using var document=JsonDocument.Parse(text);return document.RootElement.Clone();}
    public void Dispose(){DeleteShares(ShareFolder());http.Dispose();}
}
internal record MobileCookie(string Name,string Value,DateTime Expires);
internal record MobileLogin(string Username,string Password,bool RememberLogin);
internal record MobilePasswordChange(string CurrentPassword,string NewPassword);
internal record MobileRegistration(string? Code,string? Username,string? Password,RegistrationAcceptance? Agreement=null);
internal record MobileSignup(string? Name,string? Username,string? Password,string Role,string? ClassId,int? StudentNumber,RegistrationAcceptance? Agreement=null);
internal record MobileFamilyCode(string? Code);
internal record MobileEmpty;
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MobileCookie[]))]
[JsonSerializable(typeof(MobileLogin))]
[JsonSerializable(typeof(MobilePasswordChange))]
[JsonSerializable(typeof(MobileRegistration))]
[JsonSerializable(typeof(MobileSignup))]
[JsonSerializable(typeof(MobileFamilyCode))]
[JsonSerializable(typeof(MobileEmpty))]
[JsonSerializable(typeof(AnnouncementRequest))]
[JsonSerializable(typeof(StaffActivation))]
[JsonSerializable(typeof(OfficeEventRequest))]
[JsonSerializable(typeof(OfficeRoomRequest))]
[JsonSerializable(typeof(OfficeBookingRequest))]
[JsonSerializable(typeof(OfficeRevision))]
[JsonSerializable(typeof(CounselingOpen))]
[JsonSerializable(typeof(CounselingReserve))]
[JsonSerializable(typeof(CounselingView))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(MessageFormat))]
[JsonSerializable(typeof(SchoolMessenger.Contracts.FeedbackRequest))]
[JsonSerializable(typeof(ElectiveRound))]
[JsonSerializable(typeof(ElectiveRound[]))]
[JsonSerializable(typeof(ElectiveApplicationRequest))]
[JsonSerializable(typeof(ElectiveCancellation))]
internal partial class MobileJson:JsonSerializerContext;
