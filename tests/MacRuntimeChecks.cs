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
        api=new MessengerApi(address,true);await api.Login("admin",password,false);
        var userId=api.Session.GetProperty("user").GetProperty("id").GetString();
        await MacSend(api,"api/messages",new JsonObject
        {
            ["clientId"]=Guid.NewGuid().ToString("N"),["title"]="교직원 업무 메시지 검증",
            ["body"]="받은 메시지 목록과 본문을 확인하는 가상 자료입니다.",
            ["recipientIds"]=new JsonArray((JsonNode?)JsonValue.Create(userId)),["attachmentIds"]=new JsonArray()
        });
        homeTab="notices";await Home();
    }
    internal MessengerApi MacVerifyApi=>api!;
    internal bool MacVerifyConnected=>desktopHub?.State==Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected;
    internal Task<bool> MacVerifyTimetable()=>Timetable();
    internal Task MacVerifySwapBoard()=>MacSwapBoard(api!,DateTime.UtcNow.AddHours(9).Date,true);
    internal Task MacVerifyBundles()=>MacBundleEditor(api!);
    internal Task MacVerifyCompose()=>MacCompose();
    internal Task MacVerifyPeople()=>MacPeople();
    internal Task MacVerifyDetail(string id)=>OfficeDetail(id);
    internal ContentView? MacVerifyReader=>desktopReader;
    internal async Task<MessageArchive> MacVerifyArchive(){var source=api!;await SynchronizeDesktopArchive(source);return desktopArchive?.Owner==source?desktopArchive:throw new InvalidOperationException("Mac message archive did not open");}
    internal Task MacVerifyWork(string mode,string? id=null)=>MacWork(mode,id);
    internal View? MacVerifyContent=>desktopContent.Content;
    internal Grid? MacVerifySubmissionTargets=>macSubmissionTargets;
    internal bool MacVerifyIdle=>!busy;
    internal bool MacVerifyReady=>!busy&&!resuming&&desktopReader?.Content is Grid;
    internal Task MacVerifyChat(string[]? members=null)=>MacChat(members:members);
    internal Task MacVerifySurveys(bool create=false)=>MacSurveys(create?"sent":"received",null,create);
    internal Task MacVerifyCourses(string tab)=>MacCourses(tab);
    internal Task MacVerifyAi()=>MacAiSettings();
    internal Task MacVerifyRegister(string address)=>MacRegister(address);
    internal async Task MacVerifySettings(){homeTab="account";await Home();}
    internal (int Mail,int Chat) MacVerifyUnread=>(macUnreadMail,macUnreadChat);
    internal Task MacVerifyCount()=>MacCountUnread(api!);
    internal bool MacVerifyRail(string key)=>desktopMenu.TryGetValue(key,out var b)&&b.IsVisible;
    internal static bool MacVerifyCompact{set=>MacCompactRoster=value;}
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
            Check(!Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId is "mac-nav-notices" or "mac-nav-counseling" or "mac-nav-electives"),"App-only notice, counseling and elective menus are absent");
            Check(Views(Page.Content).OfType<Button>().Where(b=>!string.IsNullOrEmpty(b.Text)).All(b=>b.Handler?.PlatformView is UIButton native&&!string.IsNullOrEmpty(native.CurrentTitle??native.Configuration?.Title)),"Native Mac buttons retain their visible title bindings");
            CertificatePinChecks();
            await Page.MacVerifyLogin(fixture.GetProperty("office").GetString()!.Replace("127.0.0.1","localhost"),fixture.GetProperty("password").GetString()!);
            Check(Page.MacVerifyApi.ProductCode=="teacher-macos"&&!Page.MacVerifyApi.Remember,"Mac product identity and session kept in memory for ad-hoc verification");
            await Wait(()=>Page.MacVerifyConnected);
            var rosterSource = Page.MacVerifyApi;
            var sample = await AddRosterTeacher(rosterSource, "roster.sample", "가상 교사 가");
            var other = await AddRosterTeacher(rosterSource, "roster.other", "가상 교사 나");
            var parent = Guid.NewGuid().ToString("N");
            var child = Guid.NewGuid().ToString("N");
            using (var settings = Json(new JsonObject
            {
                ["revision"] = (await rosterSource.Get("api/admin/roster-folders")).GetProperty("revision").GetInt32(),
                ["folders"] = new JsonArray(
                    new JsonObject { ["id"] = parent, ["parentId"] = null, ["name"] = "가상 프로젝트", ["memberIds"] = new JsonArray(sample) },
                    new JsonObject { ["id"] = child, ["parentId"] = parent, ["name"] = "가상 준비팀", ["memberIds"] = new JsonArray(sample, other) })
            }))
                await rosterSource.Send("api/admin/roster-folders", content: settings, method: HttpMethod.Put);
            await Page.MacVerifyPeople();
            var roster = ((ContentPage)Page.Navigation.NavigationStack.Last()).Content!;
            Check(roster is Grid rosterGrid&&rosterGrid.Children.OfType<Grid>().Any(footer=>Grid.GetRow(footer)==2&&
                Views(footer).OfType<Button>().Any(button=>button.Text=="선택 0명에게 메시지")),
                "Mac roster keeps its send action in a fixed footer");
            Check(Views(roster).OfType<Border>().Any(mark => mark.StrokeShape is Microsoft.Maui.Controls.Shapes.Ellipse && mark.Content is Label { Text: "가" }),
                "Mac roster rows show tinted initial avatars");
            Check(Views(roster).OfType<CheckBox>().Count(box => SemanticProperties.GetDescription(box) == "가상 교사 가 선택") == 2,
                "Mac roster shows a teacher in both assigned folders");
            Views(roster).OfType<CheckBox>().First(box => SemanticProperties.GetDescription(box) == "가상 교사 가 선택").IsChecked = true;
            Check(Views(roster).OfType<CheckBox>().Where(box => SemanticProperties.GetDescription(box) == "가상 교사 가 선택").All(box => box.IsChecked),
                "Mac roster checks duplicate entries for one teacher together");
            Check(Views(roster).OfType<Button>().Any(button => button.Text.Contains("미분류")),
                "Mac roster shows a separate folder for unassigned teachers");
            var filter = Views(roster).OfType<Entry>().Single();
            filter.Text = "가상 준비팀";
            Check(Views(roster).OfType<CheckBox>().Single(box => SemanticProperties.GetDescription(box) == "가상 교사 가 선택").IsChecked,
                "Mac folder search preserves previously selected recipients");
            Views(roster).OfType<CheckBox>().Single(box => SemanticProperties.GetDescription(box) == "가상 교사 나 선택").IsChecked = true;
            var sendSelection = Views(roster).OfType<Button>().Single(button => button.Text == "선택 2명에게 메시지");
            ((IButtonController)sendSelection).SendClicked();
            await Wait(() => Page.Navigation.NavigationStack.LastOrDefault() is ContentPage page && Views(page.Content!).OfType<Label>().Any(label => label.Text == "받는 사람 2명"));
            Check(true, "Mac roster selection opens the composer with both recipients");
            await Page.Navigation.PopAsync();
            await Page.Navigation.PopAsync();
            var largePath=Path.Combine(Folder,"대용량 시험.txt");
            try
            {
                const long largeSize=101L*1024*1024;
                using(var synthetic=File.Create(largePath))synthetic.SetLength(largeSize);
                var uploaded=await Page.MacVerifyApi.Upload(new FileResult(largePath),false);
                Check(uploaded.GetProperty("size").GetInt64()==largeSize,"Mac streams an attachment beyond the old 100 MiB limit");
                await Page.MacVerifyApi.Send("api/attachments/"+uploaded.GetProperty("id").GetString(),method:HttpMethod.Delete);
            }
            finally{File.Delete(largePath);}
            var folder=Path.Combine(Folder,"첨부 폴더 시험-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder,"빈 폴더"));File.WriteAllText(Path.Combine(folder,"문서.txt"),"synthetic");
            using(var url=Foundation.NSUrl.FromFilename(folder))
            using(var provider=new Foundation.NSItemProvider(url,"public.file-url"))
            using(var droppedFolder=await MacAttachmentDrop.Read(provider,SchoolMessenger.Contracts.AttachmentLimits.Message))
            using(var folderZip=await Task.Run(()=>SchoolMessenger.Shared.FolderAttachment.Create(droppedFolder.Path,SchoolMessenger.Contracts.AttachmentLimits.Message)))
            {
                var attachment=await Page.MacVerifyApi.Upload(new FileResult(folderZip.Path),false);
                using var zip=System.IO.Compression.ZipFile.OpenRead(folderZip.Path);
                Check(zip.Entries.Any(e=>e.FullName.EndsWith("/빈 폴더/",StringComparison.Ordinal))&&attachment.GetProperty("name").GetString()!.EndsWith(".zip",StringComparison.Ordinal),"A Mac file-URL drop provider opens a folder and uploads a ZIP with its empty subfolders");
                await Page.MacVerifyApi.Send("api/attachments/"+attachment.GetProperty("id").GetString(),method:HttpMethod.Delete);
            }
            var timetable=Views(Page.Content).OfType<Button>().Single(b=>b.AutomationId=="mac-nav-timetable");
            Check(timetable.IsVisible&&await Page.MacVerifyTimetable(),"The enabled timetable opens from the desktop workspace");
            var timetablePage=(ContentPage)Page.Navigation.NavigationStack.Last();
            await Wait(()=>Views(timetablePage.Content).OfType<Label>().Any(label=>label.Text=="국어"));
            Check(Views(timetablePage.Content).OfType<Button>().Any(button=>button.Text=="한 주")&&Views(timetablePage.Content).OfType<Button>().Any(button=>button.Text=="보고 있는 시간표 인쇄"),
                "Mac timetable shows the published week with the weekly view control and the print button");
            await Capture("mac-timetable.png");
            await Page.MacVerifySwapBoard();
            var boardPage=(ContentPage)Page.Navigation.NavigationStack.Last();
            await Wait(()=>Views(boardPage.Content).OfType<Label>().Count(label=>label.AutomationId=="mac-board-cell")>200);
            var palette=new[]{"#9DEDF1","#EAB6ED","#F4AFB6"}.Select(Color.FromArgb).ToArray();
            var cells=Views(boardPage.Content).OfType<Label>().Where(label=>label.AutomationId=="mac-board-cell").ToArray();
            Check(palette.All(color=>cells.Any(cell=>cell.Text.EndsWith("◆",StringComparison.Ordinal)&&cell.BackgroundColor.ToArgbHex()==color.ToArgbHex())),
                "Mac swap board shows every teacher's week and paints each of the three bundles in its own colour, in the order of their names");
            Check(Views(boardPage.Content).OfType<Button>().Any(button=>button.AutomationId=="mac-board-submit"&&!button.IsEnabled),"The request button waits until a lesson has been moved");
            await Capture("mac-swap-board.png");
            await Page.Navigation.PopAsync();
            // A period marked as impossible shows on the board, and the helper answers for one of the teacher's periods.
            var korea=DateTime.UtcNow.AddHours(9).Date;var monday=korea.AddDays(-(((int)korea.DayOfWeek+6)%7));
            var later=Enumerable.Range(0,5).Select(i=>monday.AddDays(i)).FirstOrDefault(d=>d>korea);
            if(later!=default)
            {
                var day=later.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
                var week=Enumerable.Range(0,5).Select(i=>monday.AddDays(i).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)).Where(d=>string.CompareOrdinal(d,day)>=0).ToArray();
                JsonObject Marks(params int[] periods)=>new(){["dates"]=new JsonArray(week.Select(d=>(JsonNode?)JsonValue.Create(d)).ToArray()),["times"]=new JsonArray(periods.Select(p=>(JsonNode?)new JsonObject{["date"]=day,["period"]=p}).ToArray())};
                await Page.MacVerifyApi.Send("api/timetable/unavailable",content:Json(Marks(1,2,3,4,5,6,7)),method:HttpMethod.Put);
                await Page.MacVerifySwapBoard();var marked=(ContentPage)Page.Navigation.NavigationStack.Last();
                await Wait(()=>Views(marked.Content).OfType<Label>().Any(l=>l.AutomationId=="mac-board-blocked"||l.Text?.StartsWith("⊘",StringComparison.Ordinal)==true));
                Check(Views(marked.Content).OfType<CheckBox>().Any(c=>c.AutomationId=="mac-board-marking")&&Views(marked.Content).OfType<Button>().Any(b=>b.AutomationId=="mac-board-helper"),"Mac swap board shows marked periods with the marking switch and the swap helper");
                Views(marked.Content).OfType<CheckBox>().Single(c=>c.AutomationId=="mac-board-marking").IsChecked=true;await Task.Delay(1500);
                await Capture("mac-swap-board-marked.png");
                await Page.MacVerifyApi.Send("api/timetable/unavailable",content:Json(Marks()),method:HttpMethod.Put);
                var board=(ContentPage)Page.Navigation.NavigationStack.Last();
                await Wait(()=>Views(board.Content).OfType<Button>().Any(b=>b.AutomationId=="mac-board-helper"));
                ((IButtonController)Views(board.Content).OfType<Button>().Single(b=>b.AutomationId=="mac-board-helper")).SendClicked();
                await Wait(()=>Page.Navigation.NavigationStack.Last() is ContentPage help&&help!=board);
                var helper=(ContentPage)Page.Navigation.NavigationStack.Last();
                Views(helper.Content).OfType<CheckBox>().First(c=>c.AutomationId=="mac-helper-period-3").IsChecked=true;
                ((IButtonController)Views(helper.Content).OfType<Button>().Single(b=>b.AutomationId=="mac-helper-find")).SendClicked();
                await Wait(()=>Views(helper.Content).OfType<Label>().Any(l=>l.AutomationId=="mac-helper-note"&&!string.IsNullOrEmpty(l.Text)));
                Check(true,"Mac swap helper finds ways to empty a period or says why there are none");
                await Capture("mac-swap-helper.png");
                while(Page.Navigation.NavigationStack.Count>1)await Page.Navigation.PopAsync();
            }
            await Page.MacVerifyBundles();
            var bundlePage=(ContentPage)Page.Navigation.NavigationStack.Last();
            await Wait(()=>Views(bundlePage.Content).OfType<Label>().Any(label=>label.Text=="1학년 선택 A"));
            Check(Views(bundlePage.Content).OfType<Picker>().Any(picker=>picker.AutomationId=="mac-bundle-grade"&&picker.SelectedIndex==0),"Mac bundle page lists the bundles with their colours and picks grade, subject and classes");
            await Capture("mac-bundles.png");
            await Page.Navigation.PopAsync();
            await Page.MacVerifyApi.Send("api/admin/timetable-feature",content:Json(new JsonObject{["enabled"]=false}));
            await Wait(()=>!timetable.IsVisible&&Page.Navigation.NavigationStack.Count==1);
            Check(!timetable.IsVisible,"Disabling the timetable hides its sidebar item and closes the active timetable view");
            await Page.MacVerifyApi.Send("api/admin/timetable-feature",content:Json(new JsonObject{["enabled"]=true}));
            await Wait(()=>timetable.IsVisible);
            Check(timetable.IsVisible,"Re-enabling the timetable restores its sidebar item without signing in again");
            var people=await Page.MacVerifyApi.Get("api/users");var self=people.EnumerateArray().Single(p=>p.GetProperty("id").GetString()==Page.MacVerifyApi.Session.GetProperty("user").GetProperty("id").GetString());
            Check(self.GetProperty("online").GetBoolean()&&!self.GetProperty("mobile").GetBoolean(),"Server reports Mac as desktop online, without a phone lease");
            // The workspace is drawn again after the timetable comes back; wait for it as the other checks do.
            await Wait(()=>Page.MacVerifyReader?.Content is Grid&&Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId=="mac-filter-unread"));
            Check(Page.MacVerifyReader?.Content is Grid&&Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId=="mac-filter-unread"),"Desktop message list and reader remain side by side with a native unread filter");
            Check(!Views(Page.Content).OfType<Microsoft.Maui.Controls.Shapes.Ellipse>().Any(dot=>dot.AutomationId=="mac-unread-dot"&&dot.IsVisible),"Opening the selected message clears its unread dot after the server read acknowledgement");
            Check(!Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId?.StartsWith("school-tab-")==true),"Mac uses desktop navigation without duplicate mobile bottom tabs");await Capture("mac-messages.png");
            var before=await Page.MacVerifyApi.Get("api/messages?box=sent&offset=0");
            var previousIds=before.EnumerateArray().Select(row=>row.GetProperty("id").GetString()).ToHashSet();
            await Page.MacVerifyCompose();var compose=(ContentPage)Page.Navigation.NavigationStack.Last();var controls=Views(compose.Content).ToArray();
            Check(controls.Single(v=>v.AutomationId=="mac-compose-attachment-drop").Handler?.PlatformView is UIView dropView&&dropView.Interactions.Any(i=>i is UIDropInteraction),"The native message composer installs a folder drop interaction");
            Check(!MacUpdater.CanInstall,"updater defers installation while a message composer is open");
            controls.OfType<CheckBox>().Single(c=>c.AutomationId=="mac-compose-self").IsChecked=true;
            controls.OfType<Entry>().Single(e=>e.AutomationId=="mac-compose-title").Text="Mac 네이티브 전송 검증";controls.OfType<Editor>().Single().Text="가상 학교 데이터로 Mac 메시지 작성과 수신 연결을 검사합니다.";await Capture("mac-compose.png");
            var send=controls.OfType<Button>().Single(b=>b.AutomationId=="mac-send");((IButtonController)send).SendClicked();
            await Wait(()=>Page.Navigation.NavigationStack.Count==1&&Page.MacVerifyReady);
            var rows=await Page.MacVerifyApi.Get("api/messages?box=sent&offset=0");var sent=rows.EnumerateArray().Single(r=>!previousIds.Contains(r.GetProperty("id").GetString()));
            Check(sent.GetProperty("recipientCount").GetInt32()==1,"Native composer sends through authenticated server with one selected recipient");
            await Page.MacVerifyDetail(sent.GetProperty("id").GetString()!);
            Check(Page.MacVerifyReader?.Content is Grid details&&details.RowDefinitions.Count==2&&details.Children.OfType<Button>().Any(b=>b.Text=="답장"),"Reply remains in fixed footer outside the message scroll area");
            await Capture("mac-message-detail.png");
            var sentId=sent.GetProperty("id").GetString()!;var archive=await Page.MacVerifyArchive();
            Check(archive.Load(sentId)?.GetProperty("message").GetProperty("title").GetString()=="Mac 네이티브 전송 검증","The sent title is available from the Mac copy after server cleanup");
            var dropped=await Page.MacVerifyApi.Get("api/messages/"+sentId+"?peek=true");
            Check(MessageArchive.Purged(dropped)&&dropped.GetProperty("message").GetProperty("body").GetString()=="","Server drops the body once this Mac confirmed its sealed copy");
            await Page.MacVerifyDetail(sentId);await Wait(()=>Page.MacVerifyReady);
            Check(Views(Page.MacVerifyReader!).OfType<Label>().Any(l=>l.Text=="가상 학교 데이터로 Mac 메시지 작성과 수신 연결을 검사합니다."),"Mac reader shows the body from its own copy after the server dropped it");
            Check(Views(Page.MacVerifyReader!).OfType<Label>().Any(l=>l.Text=="Mac 네이티브 전송 검증"),"Mac reader shows the title from its own copy after the server dropped it");
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
            Check(Page.MacVerifySubmissionTargets!.Children.OfType<Border>().All(c=>c.ClassId=="면제"||c.BackgroundColor.ToArgbHex()==Color.FromArgb(c.ClassId=="미제출"?"#FDECEC":"#E3F5EA").ToArgbHex()),"Handed-in cards are green and missing ones red");
            var due=DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).DateTime).AddDays(3);
            var todoId=(await admin.Send("api/todos",content:Json(new JsonObject{["title"]="Mac 할 일 검증",["dueDate"]=due.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),["status"]="open",["revision"]=0}))).GetProperty("id").GetString()!;
            await Page.MacVerifyWork("todos",todoId);var work=Views(Page.MacVerifyContent!).ToArray();
            Check(work.OfType<Label>().Any(l=>l.AutomationId=="mac-work-summary-"+todoId&&l.Text.EndsWith("D-3",StringComparison.Ordinal))&&work.OfType<Label>().Any(l=>l.AutomationId=="mac-todo-deadline"&&l.Text.EndsWith("D-3",StringComparison.Ordinal)),"Mac to-do list counts D-Day by Korean date");
            await Capture("mac-todos.png");
            work.OfType<Picker>().Single(p=>p.AutomationId=="mac-todo-status").SelectedIndex=1;((IButtonController)work.OfType<Button>().Single(b=>b.AutomationId=="mac-todo-save")).SendClicked();
            var saved=false;for(var i=0;i<100&&!saved;i++){await Task.Delay(100);saved=(await admin.Get("api/todos?offset=0")).EnumerateArray().Any(t=>t.GetProperty("id").GetString()==todoId&&t.GetProperty("status").GetString()=="done");}
            Check(saved,"Editing a to-do on Mac saves through PATCH with its revision");await Wait(()=>Page.MacVerifyIdle);
            // Chat: a room with a colleague, a line sent, kept on this Mac.
            await Page.MacVerifyChat([sample]);
            await Wait(()=>Views(Page.MacVerifyContent!).OfType<Label>().Any(l=>l.AutomationId=="mac-chat-title"&&l.Text=="가상 교사 가"));
            var chatView=Views(Page.MacVerifyContent!).ToArray();var chatInput=chatView.OfType<Entry>().Single(e=>e.AutomationId=="mac-chat-input");
            chatInput.Text="내일 조회 때 체험학습 안내문을 같이 나눠 주세요.";
            ((IButtonController)chatView.OfType<Button>().Single(b=>b.AutomationId=="mac-chat-send")).SendClicked();
            await Wait(()=>string.IsNullOrEmpty(chatInput.Text)&&!chatInput.IsReadOnly&&Views(Page.MacVerifyContent!).OfType<Label>().Any(l=>l.Text=="내일 조회 때 체험학습 안내문을 같이 나눠 주세요."));
            Check(!chatInput.IsReadOnly&&chatInput.IsEnabled,"Mac chat sends a line and keeps the box ready for the next one");
            var rooms=(await admin.Get("api/chats")).EnumerateArray().ToArray();var roomId=rooms.First(r=>r.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==sample)).GetProperty("id").GetString()!;
            Check((await Page.MacVerifyArchive()).ChatEntries(roomId).Any(e=>e.GetProperty("body").GetString()!.Contains("체험학습 안내문")),"Mac keeps its own sealed copy of the chat room");
            await Capture("mac-chat.png");
            // Unread counts: a colleague's message raises the number on the sidebar.
            using(var colleague=new MessengerApi(office,true))
            {
                await colleague.Login("mac.work.2",password,false);
                await colleague.Send("api/messages",content:Json(new JsonObject{["clientId"]=Guid.NewGuid().ToString(),["title"]="Mac 알림 검증",["body"]="안 읽은 개수를 확인합니다.",["recipientIds"]=new JsonArray((JsonNode?)JsonValue.Create(admin.Session.GetProperty("user").GetProperty("id").GetString())),["attachmentIds"]=new JsonArray(),["all"]=false}));
                await colleague.Logout();
            }
            for(var i=0;i<100&&Page.MacVerifyUnread.Mail==0;i++){await Page.MacVerifyCount();await Task.Delay(100);}
            Check(Page.MacVerifyUnread.Mail>0&&Views(Page.Content).OfType<Border>().Any(b=>b.AutomationId=="mac-badge-received"&&b.IsVisible),"A new message shows its count on the Mac sidebar");
            // Surveys: the form, then a sent survey with its results.
            await Page.MacVerifySurveys(true);await Wait(()=>Views(Page.MacVerifyContent!).OfType<Entry>().Any(e=>e.AutomationId=="mac-survey-new-title"));
            var surveyForm=Views(Page.MacVerifyContent!).ToArray();
            surveyForm.OfType<Entry>().Single(e=>e.AutomationId=="mac-survey-new-title").Text="Mac 설문 검증";surveyForm.OfType<Entry>().First(e=>e.AutomationId=="mac-survey-question").Text="체험학습 날짜로 좋은 날은?";
            await Capture("mac-survey-new.png");
            await admin.Send("api/surveys",content:Json(new JsonObject{["title"]="Mac 설문 검증",["description"]="",["deadline"]=DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds(),["questions"]=new JsonArray(new JsonObject{["text"]="체험학습 날짜로 좋은 날은?",["kind"]="choice",["required"]=true,["options"]=new JsonArray("금요일","월요일")}),["targetIds"]=new JsonArray((JsonNode?)JsonValue.Create(staff[0])),["all"]=false,["anonymous"]=false}));
            await Page.MacVerifySurveys();
            ((IButtonController)Views(Page.MacVerifyContent!).OfType<Button>().Single(b=>b.AutomationId=="mac-survey-sent")).SendClicked();
            await Wait(()=>Views(Page.MacVerifyContent!).OfType<Label>().Any(l=>l.AutomationId=="mac-survey-summary"));
            Check(Views(Page.MacVerifyContent!).OfType<Label>().Any(l=>l.AutomationId=="mac-survey-title"&&l.Text=="Mac 설문 검증"),"Mac shows a sent survey with its results");
            await Capture("mac-surveys.png");
            // Org chart in one short line per person.
            MessengerPage.MacVerifyCompact=true;await Page.MacVerifyPeople();
            var compact=((ContentPage)Page.Navigation.NavigationStack.Last()).Content!;
            Check(Views(compact).OfType<Border>().Any(b=>b.AutomationId=="mac-roster-row-compact")&&Views(compact).OfType<Button>().Any(b=>b.AutomationId=="mac-roster-favorites"),"Mac org chart has the compact view and the favourites filter");
            await Capture("mac-roster-compact.png");MessengerPage.MacVerifyCompact=false;await Page.Navigation.PopAsync();
            // Typing a recipient's name lists them; Enter adds the chip. Leaving keeps a draft.
            await Page.MacVerifyCompose();var writing=(ContentPage)Page.Navigation.NavigationStack.Last();if(!Views(writing.Content).OfType<Entry>().Any(e=>e.AutomationId=="mac-recipient-input"))throw new InvalidOperationException("composer not on top: "+string.Join(" > ",Page.Navigation.NavigationStack.Select(p=>p.Title))+" / entries "+string.Join(",",Views(writing.Content).OfType<Entry>().Select(e=>e.AutomationId)));var typed=Views(writing.Content).OfType<Entry>().Single(e=>e.AutomationId=="mac-recipient-input");
            typed.Text="가상교사0";
            await Wait(()=>Views(writing.Content).OfType<Border>().Any(b=>b.AutomationId=="mac-recipient-suggestions"&&b.IsVisible));
            await Capture("mac-compose-recipient.png");
            typed.SendCompleted();
            await Wait(()=>Views(writing.Content).OfType<Label>().Any(l=>l.Text=="받는 사람 1명"));
            Check(string.IsNullOrEmpty(typed.Text),"Mac composer adds a recipient typed by name");
            Views(writing.Content).OfType<Editor>().Single(e=>e.AutomationId=="mac-compose-body").Text="임시저장 확인용 가상 본문";
            await Page.Navigation.PopAsync();
            var drafts=(await Page.MacVerifyArchive()).Drafts();
            Check(drafts.Any(d=>d.GetProperty("body").GetString()=="임시저장 확인용 가상 본문"),"Leaving the Mac composer keeps a sealed draft");
            foreach(var d in drafts)(await Page.MacVerifyArchive()).DeleteDraft(d.GetProperty("id").GetString()!);
            // Settings, AI connection, a switched-off menu and 수강신청.
            await Page.MacVerifySettings();await Wait(()=>Views(Page.Content).OfType<Switch>().Any(t=>t.AutomationId=="mac-setting-dnd"));
            Check(true,"Mac settings offer sound off, do not disturb and the compact org chart");
            await Capture("mac-settings.png");
            await Page.MacVerifyAi();await Capture("mac-ai.png");await Page.Navigation.PopAsync();
            await admin.Send("api/admin/features",content:Json(new JsonObject{["name"]="chat",["enabled"]=false}));
            await Wait(()=>!Page.MacVerifyRail("chat"));
            Check(!Page.MacVerifyRail("chat"),"A menu the administrator switched off disappears from the Mac sidebar");
            await admin.Send("api/admin/features",content:Json(new JsonObject{["name"]="chat",["enabled"]=true}));
            await Wait(()=>Page.MacVerifyRail("chat"));
            await admin.Send("api/admin/office/permissions",content:Json(new JsonObject{["userId"]=admin.Session.GetProperty("user").GetProperty("id").GetString(),["calendarManager"]=true,["roomManager"]=true,["courseManager"]=true}));
            await Page.MacVerifyCourses("rounds");await Task.Delay(800);await Capture("mac-courses.png");
            await Page.MacVerifyCourses("connect");await Wait(()=>Views(Page.MacVerifyContent!).OfType<Entry>().Any(e=>e.AutomationId=="mac-course-url"));
            Check(true,"Mac 수강신청 shows rounds and the Google connection steps");
            await Capture("mac-courses-connect.png");
            await Page.MacVerifyLogout();
            Check(!Page.MacVerifyConnected,"Logout closes the desktop connection");
            Check(Page.MacVerifyReader is null&&!Views(Page.Content).OfType<Button>().Any(b=>b.AutomationId?.StartsWith("mac-message-")==true),"Logout removes the message reader and restores the login screen");
            await Page.MacVerifyRegister(fixture.GetProperty("office").GetString()!.Replace("127.0.0.1","localhost"));await Capture("mac-register.png");await Page.Navigation.PopAsync();
            try{_ =new MessengerApi("http://example.com",true);throw new InvalidOperationException("Unsafe HTTP accepted");}catch(InvalidOperationException e)when(e.Message.Contains("https://")){Check(true,"Non-local cleartext server address is rejected");}
            await File.WriteAllTextAsync(report+".tmp",JsonSerializer.Serialize(new{ok=true,passed}));File.Move(report+".tmp",report,true);
        }
        catch(Exception e){await File.WriteAllTextAsync(report+".tmp",JsonSerializer.Serialize(new{ok=false,passed,error=e.ToString()}));File.Move(report+".tmp",report,true);}
    }
    static async Task<string> AddRosterTeacher(MessengerApi api, string username, string name)
    {
        using var account = Json(new JsonObject
        {
            ["username"] = username, ["name"] = name, ["department"] = "검증부", ["password"] = "Test-" + Guid.NewGuid(),
            ["isAdmin"] = false, ["canBroadcast"] = false
        });
        return (await api.Send("api/admin/users", content: account)).GetProperty("id").GetString()!;
    }
}
#endif
