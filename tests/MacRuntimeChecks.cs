#if MAC_VERIFY && DEBUG
using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UIKit;
namespace SchoolMessenger.Mobile;
public sealed partial class MessengerPage
{
    internal async Task MacVerifyLogin(string address,string password)
    {
        Preferences.Default.Set("update-confirmed-sequence",999999);
        api=new MessengerApi(address,true);await api.Login("admin",password,false);homeTab="notices";await Home();
    }
    internal MessengerApi MacVerifyApi=>api!;
    internal bool MacVerifyConnected=>desktopHub?.State==Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected;
    internal Task MacVerifyCompose()=>MacCompose();
    internal Task MacVerifyDetail(string id)=>OfficeDetail(id);
    internal ContentView? MacVerifyReader=>desktopReader;
    internal async Task<MessageArchive> MacVerifyArchive(){var source=api!;await SynchronizeDesktopArchive(source);return desktopArchive?.Owner==source?desktopArchive:throw new InvalidOperationException("Mac message archive did not open");}
    internal Task MacVerifyWork(string mode,string? id=null)=>MacWork(mode,id);
    internal View? MacVerifyContent=>desktopContent.Content;
    internal Grid? MacVerifySubmissionTargets=>macSubmissionTargets;
    internal bool MacVerifyIdle=>!busy;
    internal bool MacVerifyReady=>!busy&&!resuming&&desktopReader?.Content is Grid;
    internal async Task MacVerifyLogout(){var source=api!;await source.Logout();source.Dispose();api=null;Login();}
}
internal static class MacRuntimeChecks
{
    static readonly List<string> passed=[];
    static string Folder=>Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    static MessengerPage Page=>MessengerPage.Active!;
    static IEnumerable<View> Views(View view)
    {
        yield return view;IEnumerable<View> children=view switch{ScrollView s when s.Content is not null=>[s.Content],Border b when b.Content is not null=>[b.Content],Microsoft.Maui.Controls.Layout l=>l.Children.OfType<View>(),ContentView c when c.Content is not null=>[c.Content],_=>[]};
        foreach(var child in children)foreach(var descendant in Views(child))yield return descendant;
    }
    static void Check(bool value,string description){if(!value)throw new InvalidOperationException(description);passed.Add(description);Console.WriteLine("MAC_PROGRESS:"+description);}
    static StringContent Json(JsonObject value)=>new(value.ToJsonString(),Encoding.UTF8,"application/json");
    static X509Certificate2 SelfIssued()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=new CertificateRequest("CN=pin-check.invalid",key,HashAlgorithmName.SHA256);
        return request.Create(request.SubjectName,X509SignatureGenerator.CreateForECDsa(key),DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddYears(10),RandomNumberGenerator.GetBytes(8));
    }
    // The verification server speaks Development HTTP, so the pin rule is checked with invented self-issued certificates.
    static void CertificatePinChecks()
    {
        const string school="https://pin-check.invalid:18443/",elsewhere="https://pin-check.invalid:18444/";
        CertificatePins.Forget(school);CertificatePins.Forget(elsewhere);
        using var issued=SelfIssued();using var replaced=SelfIssued();var print=CertificatePins.Fingerprint(issued);var chain=SslPolicyErrors.RemoteCertificateChainErrors;
        try
        {
            Check(CertificatePins.Validate(new Uri(school),null,SslPolicyErrors.None),"Certificates the operating system already trusts are accepted without a connection code");
            Check(!CertificatePins.Validate(new Uri(school),issued,chain)&&CertificatePins.Pending(school)==print,"Unconfirmed self-issued school certificate is refused and held for the connection code");
            var code=CertificatePins.Code(print);
            Check(code.Length==19&&code.Split('-').All(part=>part.Length==4&&part.All(Uri.IsHexDigit))&&print.StartsWith(code.Replace("-",""),StringComparison.Ordinal),"Connection code is the first 16 hex digits of the SHA-256 fingerprint in groups of four");
            CertificatePins.Trust(school,print);
            Check(CertificatePins.Validate(new Uri(school),issued,chain)&&CertificatePins.Pending(school) is null,"Confirmed school certificate is accepted afterwards");
            Check(!CertificatePins.Validate(new Uri(school),replaced,chain)&&CertificatePins.Pending(school) is null,"A different certificate at a confirmed school address is always refused without asking again");
            Check(!CertificatePins.Validate(new Uri(elsewhere),issued,chain),"A confirmed certificate is trusted only at the address it was confirmed for");
        }
        finally{CertificatePins.Forget(school);CertificatePins.Forget(elsewhere);}
    }
    static async Task Wait(Func<bool> condition){for(var i=0;i<600;i++){if(condition())return;await Task.Delay(100);}throw new InvalidOperationException("Mac native UI timeout");}
    static async Task Capture(string name)
    {
        await Task.Delay(350);
        var window=UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>().SelectMany(s=>s.Windows).First(w=>w.IsKeyWindow);
        using var renderer=new UIGraphicsImageRenderer(window.Bounds.Size);using var image=renderer.CreateImage(context=>window.Layer.RenderInContext(context.CGContext));using var data=image.AsPNG();File.WriteAllBytes(Path.Combine(Folder,name),data!.ToArray());
        var request=Path.Combine(Folder,"mac-capture-request.json");await File.WriteAllTextAsync(request+".tmp",JsonSerializer.Serialize(new{name}));File.Move(request+".tmp",request,true);
        for(var i=0;i<200;i++){var complete=Path.Combine(Folder,"mac-capture-complete.txt");if(File.Exists(complete)&&File.ReadAllText(complete)==name)return;await Task.Delay(100);}
    }
    public static async Task Run()
    {
        var report=Path.Combine(Folder,"mac-results.json");Directory.CreateDirectory(Folder);Console.WriteLine("MAC_REPORT_PATH:"+report);
        try
        {
            await Wait(()=>MessengerPage.Active?.Handler is not null);await Task.Delay(1200);
            Check(MacUpdater.Available,"native Sparkle AppKit updater loads inside the sandboxed Catalyst app without starting a network update check");
            Check(MacUpdater.CanInstall,"native updater can query the managed application before restarting");
            var fixture=JsonDocument.Parse(Convert.FromBase64String(Environment.GetEnvironmentVariable("YY_MAC_FIXTURE_B64")!)).RootElement;
            Check(DeviceInfo.Platform==DevicePlatform.MacCatalyst,"Actual native Mac Catalyst process");
            Check(AppInfo.Current.Name=="교무톡"&&AppInfo.Current.VersionString==typeof(App).Assembly.GetName().Version!.ToString(3),"Mac brand and configured release version");
            Check(Page.Content is Grid grid&&grid.ColumnDefinitions.Count==2,"Native desktop sidebar and separate content column");await Capture("mac-login.png");
            Check(Views(Page.Content).OfType<Button>().Where(b=>!string.IsNullOrEmpty(b.Text)).All(b=>b.Handler?.PlatformView is UIButton native&&!string.IsNullOrEmpty(native.CurrentTitle??native.Configuration?.Title)),"Native Mac buttons retain their visible title bindings");
            CertificatePinChecks();
            await Page.MacVerifyLogin(fixture.GetProperty("office").GetString()!.Replace("127.0.0.1","localhost"),fixture.GetProperty("password").GetString()!);
            Check(Page.MacVerifyApi.ProductCode=="teacher-macos"&&!Page.MacVerifyApi.Remember,"Mac product identity and session kept in memory for ad-hoc verification");
            await Wait(()=>Page.MacVerifyConnected);
            var people=await Page.MacVerifyApi.Get("api/users");var self=people.EnumerateArray().Single(p=>p.GetProperty("id").GetString()==Page.MacVerifyApi.Session.GetProperty("user").GetProperty("id").GetString());
            Check(self.GetProperty("online").GetBoolean()&&!self.GetProperty("mobile").GetBoolean(),"Server reports Mac as desktop online, without a phone lease");
            Check(Page.MacVerifyReader?.Content is Grid&&Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId=="mac-filter-unread"),"Desktop message list and reader remain side by side with a native unread filter");
            Check(!Views(Page.Content).OfType<Label>().Any(label=>label.Text?.EndsWith(" · 미확인")==true),"Opening the selected message updates its unread indicator after the server read acknowledgement");
            Check(!Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId?.StartsWith("school-tab-")==true),"Mac uses desktop navigation without duplicate mobile bottom tabs");await Capture("mac-messages.png");
            var before=await Page.MacVerifyApi.Get("api/messages?box=received&offset=0");
            await Page.MacVerifyCompose();var compose=(ContentPage)Page.Navigation.NavigationStack.Last();var controls=Views(compose.Content).ToArray();
            Check(!MacUpdater.CanInstall,"updater defers installation while a message composer is open");
            ((IButtonController)controls.OfType<Button>().Single(b=>b.Text=="나에게 전송")).SendClicked();
            controls.OfType<Entry>().Single().Text="Mac 네이티브 전송 검증";controls.OfType<Editor>().Single().Text="가상 학교 데이터로 Mac 메시지 작성과 수신 연결을 검사합니다.";await Capture("mac-compose.png");
            var send=controls.OfType<Button>().Single(b=>b.AutomationId=="mac-send");((IButtonController)send).SendClicked();
            await Wait(()=>Page.Navigation.NavigationStack.Count==1&&Page.MacVerifyReady);
            var rows=await Page.MacVerifyApi.Get("api/messages?box=sent&offset=0");var sent=rows.EnumerateArray().Single(r=>r.GetProperty("title").GetString()=="Mac 네이티브 전송 검증");
            Check(sent.GetProperty("recipientCount").GetInt32()==1,"Native composer sends through authenticated server with one selected recipient");
            await Page.MacVerifyDetail(sent.GetProperty("id").GetString()!);
            Check(Page.MacVerifyReader?.Content is Grid details&&details.RowDefinitions.Count==2&&details.Children.OfType<Button>().Any(b=>b.Text=="답장"),"Reply remains in fixed footer outside the message scroll area");
            await Capture("mac-message-detail.png");
            var sentId=sent.GetProperty("id").GetString()!;var archive=await Page.MacVerifyArchive();
            var dropped=await Page.MacVerifyApi.Get("api/messages/"+sentId+"?peek=true");
            Check(MessageArchive.Purged(dropped)&&dropped.GetProperty("message").GetProperty("body").GetString()=="","Server drops the body once this Mac confirmed its sealed copy");
            await Page.MacVerifyDetail(sentId);await Wait(()=>Page.MacVerifyReady);
            Check(Views(Page.MacVerifyReader!).OfType<Label>().Any(l=>l.Text=="가상 학교 데이터로 Mac 메시지 작성과 수신 연결을 검사합니다."),"Mac reader shows the body from its own copy after the server dropped it");
            archive.Verify(archive.Merge(dropped));Check(true,"Mac message copy is sealed on disk, restores a dropped body and is not saved again after recall");
            // File collection and to-dos use invented staff on the isolated loopback server only.
            var office=fixture.GetProperty("office").GetString()!.Replace("127.0.0.1","localhost");var password=fixture.GetProperty("password").GetString()!;var admin=Page.MacVerifyApi;var staff=new List<string>();
            for(var i=1;i<=6;i++)staff.Add((await admin.Send("api/admin/users",content:Json(new JsonObject{["username"]="mac.work."+i,["name"]="가상교사"+i.ToString("00",CultureInfo.InvariantCulture),["department"]="업무검사",["password"]=password,["isAdmin"]=false,["canBroadcast"]=false}))).GetProperty("id").GetString()!);
            var request=await admin.Send("api/messages",content:Json(new JsonObject{["clientId"]=Guid.NewGuid().ToString(),["title"]="Mac 파일 제출 검증",["body"]="부서별 운영 계획 자료를 제출해 주세요.",["recipientIds"]=new JsonArray(staff.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["attachmentIds"]=new JsonArray(),["all"]=false,["submissionDeadline"]=DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds()}));
            var requestId=request.GetProperty("id").GetString()!;
            using(var teacher=new MessengerApi(office,true))
            {
                await teacher.Login("mac.work.1",password,false);var synthetic=Path.Combine(Folder,"2026학년도_부서별_운영계획서_교육과정_평가계획_세부운영내용_최종검토완료본_제출자료.txt");await File.WriteAllTextAsync(synthetic,"synthetic-submission");
                var upload=await teacher.Upload(new FileResult(synthetic),false);
                await teacher.Send("api/submission-requests/"+requestId+"/submit",content:Json(new JsonObject{["clientId"]=Guid.NewGuid().ToString("N"),["attachmentIds"]=new JsonArray((JsonNode?)JsonValue.Create(upload.GetProperty("id").GetString()))}));await teacher.Logout();
            }
            await Page.MacVerifyWork("submission-requests",requestId);var targets=Page.MacVerifySubmissionTargets!;var cards=targets.Children.OfType<Border>().ToArray();
            Check(cards.Length==6,"Mac submission status shows every recipient as a compact name card");
            for(var i=0;i<100&&!(cards.All(c=>c.Width>0)&&Math.Abs(cards[0].Y-cards[3].Y)<1&&cards[4].Y>cards[0].Y);i++)await Task.Delay(100);
            Check(targets.ColumnDefinitions.Count==4&&Math.Abs(cards[0].Y-cards[3].Y)<1&&cards[4].Y>cards[0].Y&&cards.All(c=>c.X+c.Width<=targets.Width+1),$"Four recipient cards fit on one row at the default window size without horizontal overflow (grid {targets.Width:0}px, {targets.ColumnDefinitions.Count} columns)");
            Check(Views(Page.MacVerifyContent!).OfType<Label>().Any(l=>l.AutomationId=="mac-submission-summary"&&l.Text.StartsWith("제출 1/6명",StringComparison.Ordinal)),"Collector sees submitted, missing and exempt counts");
            var file=Views(cards.Single(c=>c.ClassId=="제출 완료")).OfType<Button>().Single(b=>b.AutomationId=="mac-submission-file");
            Check(file.LineBreakMode==LineBreakMode.TailTruncation&&file.Width<=cards[0].Width&&ToolTipProperties.GetText(file)?.ToString()?.Contains("최종검토완료본")==true,"Long submitted filenames are truncated while the full name stays in the tooltip");
            await Capture("mac-submissions.png");
            await admin.Send("api/submission-requests/"+requestId+"/targets/"+staff[1],content:Json(new JsonObject{["exempt"]=true}),method:HttpMethod.Patch);
            await Page.MacVerifyWork("submission-requests",requestId);
            Check(Page.MacVerifySubmissionTargets!.Children.OfType<Border>().Count(c=>c.ClassId=="면제")==1,"Exemption saved with PATCH is reflected in the recipient card state");
            var due=DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).DateTime).AddDays(3);
            var todoId=(await admin.Send("api/todos",content:Json(new JsonObject{["title"]="Mac 할 일 검증",["dueDate"]=due.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),["status"]="open",["revision"]=0}))).GetProperty("id").GetString()!;
            await Page.MacVerifyWork("todos",todoId);var work=Views(Page.MacVerifyContent!).ToArray();
            Check(work.OfType<Label>().Any(l=>l.AutomationId=="mac-work-summary-"+todoId&&l.Text.EndsWith("D-3",StringComparison.Ordinal))&&work.OfType<Label>().Any(l=>l.AutomationId=="mac-todo-deadline"&&l.Text.EndsWith("D-3",StringComparison.Ordinal)),"Mac to-do list counts D-Day by Korean date");
            await Capture("mac-todos.png");
            work.OfType<Picker>().Single(p=>p.AutomationId=="mac-todo-status").SelectedIndex=1;((IButtonController)work.OfType<Button>().Single(b=>b.AutomationId=="mac-todo-save")).SendClicked();
            var saved=false;for(var i=0;i<100&&!saved;i++){await Task.Delay(100);saved=(await admin.Get("api/todos?offset=0")).EnumerateArray().Any(t=>t.GetProperty("id").GetString()==todoId&&t.GetProperty("status").GetString()=="done");}
            Check(saved,"Editing a to-do on Mac saves through PATCH with its revision");await Wait(()=>Page.MacVerifyIdle);
            await Page.MacVerifyLogout();
            Check(!Page.MacVerifyConnected,"Logout closes the desktop connection");
            Check(Page.MacVerifyReader is null&&!Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId?.StartsWith("mac-message-")==true),"Logout removes the message reader and restores the login screen");
            try{_ =new MessengerApi("http://example.com",true);throw new InvalidOperationException("Unsafe HTTP accepted");}catch(InvalidOperationException e)when(e.Message.Contains("https://")){Check(true,"Non-local cleartext server address is rejected");}
            await File.WriteAllTextAsync(report+".tmp",JsonSerializer.Serialize(new{ok=true,passed}));File.Move(report+".tmp",report,true);
        }
        catch(Exception e){await File.WriteAllTextAsync(report+".tmp",JsonSerializer.Serialize(new{ok=false,passed,error=e.ToString()}));File.Move(report+".tmp",report,true);}
    }
}
#endif
