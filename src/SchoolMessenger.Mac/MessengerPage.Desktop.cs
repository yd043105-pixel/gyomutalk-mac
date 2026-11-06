using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Maui.Dispatching;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    HubConnection? desktopHub;
    MessengerApi? desktopOwner;
    readonly Label desktopStatus=Text("로그인 필요",12);
    bool desktopAway;
    IDispatcherTimer? timetableTimer;
    int timetableAvailabilityRequest;
    async Task RefreshDesktopTimetable(MessengerApi source)
    {
        var request=++timetableAvailabilityRequest;
        var enabled=true; // Older school servers always exposed the timetable.
        try { enabled=(await source.Get("api/timetable/status")).GetProperty("enabled").GetBoolean(); }
        catch(MessengerApiRefusal e) when(e.Status==404) { }
        catch(Exception e) when(e is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException) { return; }
        if(api!=source||desktopOwner!=source||covered||request!=timetableAvailabilityRequest)return;
        SetDesktopTimetable(enabled);
        if(!enabled&&timetablePage is { } page&&Navigation.NavigationStack.Contains(page))await Navigation.PopToRootAsync(false);
    }
    async Task StartDesktopPresence()
    {
        var source=api;if(source?.Office!=true)return;
        if(desktopOwner==source&&desktopHub is not null)return;
        StopDesktopPresence();desktopOwner=source;
        await RefreshDesktopTimetable(source);if(desktopOwner!=source)return;
        var timer=Dispatcher.CreateTimer();timer.Interval=TimeSpan.FromSeconds(30);
        timer.Tick+=async(_,_)=>{if(desktopOwner==source)await RefreshDesktopTimetable(source);};timer.Start();timetableTimer=timer;
        if(!await OpenDesktopArchive(source)||desktopOwner!=source)return;
        var hub=new HubConnectionBuilder().WithUrl(new Uri(source.Address,"hub"),options=>
        {
            options.Cookies=source.SessionCookies;
            options.HttpMessageHandlerFactory=handler=>{if(handler is HttpClientHandler client){client.AllowAutoRedirect=false;client.ServerCertificateCustomValidationCallback=CertificatePins.Callback(source.Address);}return handler;};
            options.WebSocketConfiguration=socket=>socket.RemoteCertificateValidationCallback=(_,certificate,_,errors)=>CertificatePins.Validate(source.Address,certificate,errors);
        }).WithAutomaticReconnect().Build();
        desktopHub=hub;
        hub.On<JsonElement>("NewMessage",message=>MainThread.BeginInvokeOnMainThread(()=>{if(desktopOwner!=source)return;desktopStatus.Text="새 메시지가 도착했습니다";_=SynchronizeDesktopArchive(source);}));
        hub.On<JsonElement>("MessageRecalled",id=>MainThread.BeginInvokeOnMainThread(()=>{if(desktopOwner==source&&desktopArchive?.Owner==source&&id.ValueKind==JsonValueKind.String)try{desktopArchive.Remove(id.GetString()!);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException){}}));
        hub.On("TimetableAvailabilityChanged",()=>MainThread.InvokeOnMainThreadAsync(()=>desktopOwner==source?RefreshDesktopTimetable(source):Task.CompletedTask));
        hub.Reconnecting+=_=>{MainThread.BeginInvokeOnMainThread(()=>desktopStatus.Text="다시 연결 중");return Task.CompletedTask;};
        hub.Reconnected+=connection=>{MainThread.BeginInvokeOnMainThread(()=>{desktopStatus.Text="온라인";_=SynchronizeDesktopArchive(source);_=RefreshDesktopTimetable(source);});return Task.CompletedTask;};
        hub.Closed+=_=>{MainThread.BeginInvokeOnMainThread(()=>{if(desktopHub==hub){desktopHub=null;desktopStatus.Text="연결 끊김 · 받은함 새로고침";}});return Task.CompletedTask;};
        try{await hub.StartAsync();if(desktopOwner==source){desktopStatus.Text=source.StorageWarning??"온라인";_=SynchronizeDesktopArchive(source);}}
        catch(Exception error)when(error is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {if(desktopHub==hub){desktopHub=null;desktopStatus.Text="실시간 연결 실패 · 받은함으로 재시도";}await hub.DisposeAsync();}
    }
    internal void StopDesktopPresence()
    {
        timetableTimer?.Stop();timetableTimer=null;SetDesktopTimetable(false);
        var hub=desktopHub;desktopHub=null;desktopOwner=null;desktopArchive=null;desktopAway=false;desktopStatus.Text="로그인 필요";
        if(hub is not null)_=DisposeDesktopHub(hub);
    }
    MessageArchive? desktopArchive;
    const string DesktopArchiveFailed="이 Mac에 메시지를 저장하지 못했습니다. 디스크 공간과 권한을 확인하세요.";
    // Message bodies live on the PCs: this Mac keeps its own sealed copy and tells the server once it has one.
    async Task<bool> OpenDesktopArchive(MessengerApi source)
    {
        if(desktopArchive?.Owner==source)return true;
        MessageArchive opened;
        try{opened=await MessageArchive.Open(source);}
        catch(Exception error)when(error is not OperationCanceledException){if(api==source)desktopStatus.Text="이 Mac의 메시지 보관함을 열지 못했습니다. 메시지 본문은 서버에 최대 30일 남습니다.";return api==source;}
        if(api!=source)return false;desktopArchive=opened;return true;
    }
    async Task SynchronizeDesktopArchive(MessengerApi source)
    {
        if(!await OpenDesktopArchive(source)||desktopArchive?.Owner!=source)return;
        try{await desktopArchive.Synchronize();}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or CryptographicException){if(api==source)desktopStatus.Text=DesktopArchiveFailed;}
        // The next message, reconnection or mailbox refresh tries again from the saved position.
        catch(Exception error)when(error is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException or KeyNotFoundException){}
    }
    JsonElement DesktopCopy(MessengerApi source,JsonElement value)
    {
        if(desktopArchive?.Owner!=source)return value;
        try{return desktopArchive.Merge(value);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or CryptographicException){desktopStatus.Text=DesktopArchiveFailed;return value;}
    }
    static async Task DisposeDesktopHub(HubConnection hub){try{await hub.DisposeAsync();}catch(Exception error)when(error is HttpRequestException or OperationCanceledException or InvalidOperationException){}}
    static async Task<JsonElement> MacSend(MessengerApi source,string path,JsonObject payload,HttpMethod? method=null)
    {using var content=new StringContent(payload.ToJsonString(),Encoding.UTF8,"application/json");return await source.Send(path,content:content,method:method);}
    async Task MacPeople()
    {
        var source=api!;var version=epoch;var people=await source.Get("api/users");
        RosterFolder[]? folders = null;
        try { folders = (await source.Get("api/roster-folders")).Deserialize(MobileJson.Default.RosterFolders)?.Folders; }
        catch (MessengerApiRefusal e) when (e.Status == 404) { }
        if(!Current(source,version))return;
        var directory = people.EnumerateArray().Select(person => (
            Person: person,
            Id: person.GetProperty("id").GetString()!,
            Name: person.GetProperty("name").GetString()!,
            Department: person.GetProperty("department").GetString() ?? "교직원")).ToDictionary(row => row.Id);
        var page=DesktopPage("조직도");var root=(Grid)page.Content;
        var content=new Grid{Padding=16,RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star)}};root.Add(content,0,1);
        content.Add(Text("교직원",22,true));
        var search=new Entry{ClassId="school-field",Placeholder="이름·부서·폴더 검색",FontSize=15,TextColor=Navy,PlaceholderColor=Muted};content.Add(DesktopPanel(search,6),0,1);
        var selection = new HashSet<string>();
        var send = DesktopButton("선택 0명에게 메시지", () => MacCompose(selectedIds: selection.ToArray()), true);
        send.IsEnabled = false;
        var footer=new Grid{Padding=new Thickness(16,8),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Color.FromArgb("#F3F6FB")};
        var total=Text("전체 "+directory.Count+"명",12);total.TextColor=Muted;total.VerticalOptions=LayoutOptions.Center;footer.Add(total);footer.Add(send,1);root.Add(footer,0,2);
        var rows=new VerticalStackLayout{Spacing=4};content.Add(new ScrollView{Content=rows},0,2);
        var checks = new List<(CheckBox Check, string Id)>();
        var collapsed = new HashSet<string>();
        void Show()
        {
            rows.Clear(); checks.Clear();
            var query = search.Text?.Trim() ?? "";
            var groups = RosterGroup.Build(folders, directory.Values.Select(row => (row.Id, row.Name + " · " + row.Department, row.Department)).ToArray(), query);
            foreach (var group in groups) AddGroup(group, 0);
            if (rows.Children.Count == 0) rows.Add(query.Length>0
                ? DesktopEmptyState("일치하는 교직원이 없습니다","이름이나 부서, 폴더 이름을 바꿔 보세요.")
                : DesktopEmptyState("표시할 교직원이 없습니다","관리자가 교직원을 등록하면 여기에 표시됩니다."));

            void AddGroup(RosterGroup group, int indent)
            {
                var expanded = query.Length > 0 || !collapsed.Contains(group.Key);
                var header = DesktopButton((expanded ? "⌄ " : "› ") + group.Name + " · " + group.AllIds().Length + "명", () =>
                {
                    if (!collapsed.Add(group.Key)) collapsed.Remove(group.Key);
                    Show(); return Task.CompletedTask;
                });
                header.HorizontalOptions = LayoutOptions.Start;
                header.BackgroundColor = Color.FromArgb("#F3F6FB");
                header.FontSize = 13;header.MinimumHeightRequest = 34;header.Padding = new Thickness(10,5);
                header.Margin = new Thickness(indent, 0, 0, 0);
                rows.Add(header);
                if (!expanded) return;
                foreach (var child in group.Children) AddGroup(child, indent + 20);
                foreach(var row in group.DirectIds.Select(id => directory[id]).OrderBy(p => p.Name))
                {
                    var person = row.Person;
                    var online=person.GetProperty("online").GetBoolean();var away=person.GetProperty("away").GetBoolean();var phone=person.GetProperty("mobile").GetBoolean();
                    var status=online?(away?"자리비움":"온라인"):phone?"휴대폰":"오프라인";var color=online?(away?Color.FromArgb("#A87812"):Color.FromArgb("#218568")):phone?Blue:Muted;
                    var line=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8,Padding=new Thickness(12 + indent,0,12,0)};
                    var check = new CheckBox { IsChecked = selection.Contains(row.Id), VerticalOptions = LayoutOptions.Center };
                    SemanticProperties.SetDescription(check, row.Name + " 선택");
                    checks.Add((check, row.Id)); line.Add(check);
                    check.CheckedChanged += (_, e) =>
                    {
                        var changed = e.Value ? selection.Add(row.Id) : selection.Remove(row.Id);
                        if (!changed) return;
                        foreach (var (other, id) in checks)
                            if (id == row.Id) other.IsChecked = e.Value;
                        send.Text = $"선택 {selection.Count}명에게 메시지";
                        send.IsEnabled = selection.Count > 0;
                    };
                    var dot=Text(online||!phone?"●":"▣",11);dot.TextColor=color;dot.VerticalOptions=LayoutOptions.Center;line.Add(dot,1);
                    var position=person.TryGetProperty("position",out var role)?role.GetString():null;
                    var button=DesktopButton(row.Name+(string.IsNullOrWhiteSpace(position)?"":" · "+position),()=>MacCompose(row.Id));button.ClassId="school-story";button.BorderWidth=0;button.BackgroundColor=Colors.Transparent;button.HorizontalOptions=LayoutOptions.Fill;line.Add(button,2);
                    var state=Text(status,11);state.TextColor=color;state.VerticalOptions=LayoutOptions.Center;line.Add(state,3);rows.Add(line);
                }
            }
        }
        search.TextChanged+=(_,_)=>Show();Show();await Navigation.PushAsync(page);
    }
    async Task MacCompose(string? recipient=null, string[]? selectedIds=null)
    {
        var source=api!;var version=epoch;var people=await source.Get("api/users");if(!Current(source,version))return;
        var page=DesktopPage("새 메시지");composer=page;var grid=(Grid)page.Content;
        var fields=new Grid{Padding=new Thickness(20,10,20,14),RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)}};grid.Add(fields,0,1);
        fields.AutomationId="mac-compose-attachment-drop";
        var recipients=new HashSet<string>();
        var directory=people.EnumerateArray().ToDictionary(p=>p.GetProperty("id").GetString()!,p=>p.GetProperty("name").GetString()+" · "+p.GetProperty("department").GetString());
        if (selectedIds is not null)
        {
            if (selectedIds.Any(id => !directory.ContainsKey(id)))
                throw new InvalidOperationException("선택한 교직원 중 현재 메시지를 받을 수 없는 계정이 있습니다. 조직도를 다시 열어 주세요.");
            recipients.UnionWith(selectedIds);
        }
        if(recipient is not null){if(!directory.ContainsKey(recipient))throw new InvalidOperationException("현재 메시지를 받을 수 없는 계정입니다.");recipients.Add(recipient);}
        var recipientRows=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap,Direction=Microsoft.Maui.Layouts.FlexDirection.Row};var recipientCount=Text("받는 사람",13,true);
        void ShowRecipients()
        {
            recipientRows.Clear();foreach(var id in recipients.ToArray())
            {
                var chip=new Button{Text=directory[id]+"  ×",FontSize=14,Padding=new Thickness(10,4),Margin=3,BackgroundColor=Colors.White,TextColor=Navy,CornerRadius=7,MinimumHeightRequest=32};
                chip.Clicked+=(_,_)=>{recipients.Remove(id);ShowRecipients();};recipientRows.Add(chip);
            }
            recipientCount.Text="받는 사람 "+recipients.Count+"명";
        }
        ShowRecipients();var targets=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=8};recipientCount.VerticalOptions=LayoutOptions.Center;targets.Add(recipientCount);fields.Add(targets);
        fields.Add(DesktopPanel(new ScrollView{Content=recipientRows,HeightRequest=64},8,Color.FromArgb("#F3F6FB")),0,1);
        var add=MakeAction("수신자 추가",async()=>
        {
            var selection=new VerticalStackLayout{Padding=20,Spacing=8};var search=new SearchBar{Placeholder="이름·부서 검색"};selection.Add(search);
            var rows=new VerticalStackLayout();selection.Add(rows);var entries=new List<(View Row,string Name)>();
            foreach(var pair in directory)
            {
                var check=new CheckBox{IsChecked=recipients.Contains(pair.Key)};check.CheckedChanged+=(_,e)=>{if(e.Value)recipients.Add(pair.Key);else recipients.Remove(pair.Key);ShowRecipients();};
                var row=new HorizontalStackLayout{Children={check,Text(pair.Value,14)}};rows.Add(row);entries.Add((row,pair.Value));
            }
            search.TextChanged+=(_,_)=>{foreach(var entry in entries)entry.Row.IsVisible=entry.Name.Contains(search.Text??"",StringComparison.OrdinalIgnoreCase);};
            selection.Add(MakeAction("선택 완료",async()=>await Navigation.PopAsync(),true));await Navigation.PushAsync(new ContentPage{Title="받는 사람 선택",Content=new ScrollView{Content=selection},BackgroundColor=BackgroundColor});
        });targets.Add(add,2);
        var self=MakeAction("나에게 전송",()=>{recipients.Clear();recipients.Add(source.Session.GetProperty("user").GetProperty("id").GetString()!);ShowRecipients();return Task.CompletedTask;});targets.Add(self,1);
        var title=new Entry{ClassId="school-field",Placeholder="제목 · 비워 두면 본문 앞부분이 표시됩니다",MaxLength=200,FontSize=16,TextColor=Navy,PlaceholderColor=Muted,BackgroundColor=Colors.Transparent};fields.Add(DesktopPanel(title,10),0,2);
        var text=new Editor{Placeholder="메시지 내용을 입력하세요",MaxLength=10000,FontSize=18,TextColor=Navy,PlaceholderColor=Muted,BackgroundColor=Colors.Transparent,VerticalOptions=LayoutOptions.Fill};fields.Add(DesktopPanel(text,12),0,3);
        var files=new List<(string Id,string Path,long Size,Label Label)>();var names=new VerticalStackLayout{Spacing=4};var fileHint=Text("",11);fileHint.TextColor=Muted;names.Add(fileHint);
        void ShowLimit()=>fileHint.Text=$"파일당·합계 {AttachmentLimits.Label(source.AttachmentLimit)} · 파일 또는 폴더 끌어놓기 (폴더는 ZIP)";
        ShowLimit();
        var fileZone=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=12};names.VerticalOptions=LayoutOptions.Center;fileZone.Add(names,1);
        var error=Text("",12);error.TextColor=Color.FromArgb("#A53A38");fields.Add(error,0,5);
        var client=Guid.NewGuid().ToString("N");JsonObject? pending=null;
        var send=DesktopButton("보내기",null,true);send.AutomationId="mac-send";send.MinimumWidthRequest=112;send.HorizontalOptions=LayoutOptions.End;
        var attach=DesktopButton("파일 첨부");
        var attaching=false;
        async Task AddAttachment(FileResult file,string key)
        {
            if(files.Any(x=>x.Path==key))return;
            if(files.Count>=10)throw new IOException("첨부는 최대 10개입니다.");
            var size=new FileInfo(file.FullPath).Length;
            if(size<=0||size>source.AttachmentLimit-files.Sum(x=>x.Size))throw new IOException($"첨부 합계는 {AttachmentLimits.Label(source.AttachmentLimit)}까지입니다.");
            error.Text="첨부 업로드 중 · "+file.FileName;
            var upload=await source.Upload(file,false);
            if(!Current(source,version))return;
            var name=Text(file.FileName,12);name.MaxLines=1;name.LineBreakMode=LineBreakMode.TailTruncation;names.Add(name);
            files.Add((upload.GetProperty("id").GetString()!,key,upload.GetProperty("size").GetInt64(),name));error.Text="";
        }
        async Task AddFiles(Func<Task> action,bool refresh=true)
        {
            if(attaching||pending is not null||!Current(source,version))return;
            attaching=true;
            attach.IsEnabled=send.IsEnabled=false;
            try
            {
                if(refresh){await source.RefreshAttachmentLimit();ShowLimit();}
                await action();
            }
            catch(Exception e){error.Text=e.Message;}
            finally{attaching=false;send.IsEnabled=true;attach.IsEnabled=pending is null;}
        }
        attach.Clicked+=async(_,_)=>await AddFiles(async()=>
        {
            preserveComposerOnResume=true;
            var file=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="메시지 첨부파일 선택"});
            if(file is not null&&Current(source,version))await AddAttachment(file,file.FullPath);
        });
        fileZone.Add(attach);fields.Add(DesktopPanel(fileZone,10,Color.FromArgb("#F3F6FB")),0,4);
        MacAttachmentDrop.Connect(fields,()=>!attaching&&pending is null&&send.IsEnabled,providers=>AddFiles(async()=>
        {
            if(providers.Length>10-files.Count)throw new IOException("첨부는 최대 10개입니다.");
            if(providers.Count(p=>!MacAttachmentDrop.HasFileUrl(p))>1)throw new IOException("이 위치의 폴더는 한 번에 하나씩 끌어놓으세요.");
            // The OS must receive the load requests during PerformDrop, before the first await.
            var reads=providers.Select(p=>MacAttachmentDrop.Read(p,source.AttachmentLimit-files.Sum(x=>x.Size))).ToArray();
            var failures=new List<string>();
            try{await source.RefreshAttachmentLimit();ShowLimit();}catch(Exception failure){failures.Add(failure.Message);}
            var canUpload=failures.Count==0;
            foreach(var read in reads)
            {
                try
                {
                    using var item=await read;
                    var path=item.Path;
                    if(!canUpload||!Current(source,version)||files.Any(f=>f.Path==path))continue;
                    var isFolder=Directory.Exists(path);
                    if(isFolder)error.Text="폴더 ZIP 준비 중 · "+Path.GetFileName(path);
                    using var folder=isFolder?await Task.Run(()=>FolderAttachment.Create(path,source.AttachmentLimit-files.Sum(x=>x.Size))):null;
                    if(!Current(source,version))continue;
                    await AddAttachment(new FileResult(folder?.Path??path),path);
                }
                catch(Exception failure){failures.Add(failure.Message);}
            }
            if(failures.Count>0)error.Text=string.Join("\n",failures.Distinct());
        },refresh:false));
        var footer=new Grid{Padding=new Thickness(20,10),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};var caption=Text("파일 제출 요청 · 마감일",12);caption.TextColor=Muted;caption.VerticalOptions=LayoutOptions.Center;
        var collect=new CheckBox{AutomationId="mac-collect",Color=Blue,VerticalOptions=LayoutOptions.Center};var collectDate=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=DateTime.UtcNow.AddHours(9).Date.AddDays(7),IsEnabled=false,VerticalOptions=LayoutOptions.Center};collect.CheckedChanged+=(_,e)=>collectDate.IsEnabled=e.Value&&pending is null;
        footer.Add(new HorizontalStackLayout{Spacing=6,Children={collect,caption,collectDate}});footer.Add(send,1);grid.Add(footer,0,2);
        send.Clicked+=async(_,_)=>
        {
            if(api!=source||!Current(source,version))return;
            send.IsEnabled=attach.IsEnabled=false;
            try
            {
                if(pending is null)
                {
                    var ids=recipients.ToArray();
                    if(ids.Length==0||string.IsNullOrWhiteSpace(text.Text))throw new InvalidOperationException("받는 사람과 메시지를 입력하세요.");
                    var deadline=collectDate.Date is{} day?DateTime.SpecifyKind(day.Date,DateTimeKind.Unspecified):DateTime.UtcNow.AddHours(9).Date;
                    if(collect.IsChecked&&deadline<DateTime.UtcNow.AddHours(9).Date)throw new InvalidOperationException("제출 마감일은 오늘 이후로 선택하세요.");
                    await source.RefreshAttachmentLimit();ShowLimit();
                    if(files.Sum(f=>f.Size)>source.AttachmentLimit)throw new IOException($"첨부 합계는 {AttachmentLimits.Label(source.AttachmentLimit)}까지입니다.");
                    if(files.Count>0)
                    {
                        using var requested=new StringContent(new JsonArray(files.Select(f=>(JsonNode?)JsonValue.Create(f.Id)).ToArray()).ToJsonString(),Encoding.UTF8,"application/json");
                        try
                        {
                            var staged=(await source.Send("api/attachments/staged/check",content:requested)).EnumerateArray().Select(item=>item.GetString()).ToHashSet();
                            var expired=files.Where(f=>!staged.Contains(f.Id)).ToArray();
                            foreach(var file in expired){files.Remove(file);names.Remove(file.Label);}
                            if(expired.Length>0)throw new IOException("일부 첨부가 만료됐습니다. 해당 파일만 다시 첨부하세요. "+string.Join(", ",expired.Select(f=>f.Label.Text)));
                        }
                        catch(MessengerApiRefusal error)when(error.Status==404){}
                    }
                    pending=new JsonObject{["clientId"]=client,["title"]=title.Text??"",["body"]=text.Text,["recipientIds"]=new JsonArray(ids.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["attachmentIds"]=new JsonArray(files.Select(x=>(JsonNode?)JsonValue.Create(x.Id)).ToArray()),["all"]=false};
                    // 서버가 마감일을 한국 시간 23:59:59로 맞춘다.
                    if(collect.IsChecked)pending["submissionDeadline"]=new DateTimeOffset(deadline.AddHours(12),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
                    title.IsEnabled=text.IsEnabled=recipientRows.IsEnabled=self.IsEnabled=add.IsEnabled=collect.IsEnabled=collectDate.IsEnabled=false;
                }
                await MacSend(source,"api/messages",pending);if(api==source){await Navigation.PopAsync();homeTab="notices";view="sent";offset=0;await Home();}
            }
            catch(Exception e)
            {
                if(e is MessengerApiRefusal {Status:400 or 403 or 404 or 413 or 415 or 422})
                {pending=null;title.IsEnabled=text.IsEnabled=recipientRows.IsEnabled=self.IsEnabled=add.IsEnabled=collect.IsEnabled=true;collectDate.IsEnabled=collect.IsChecked;}
                error.Text=e.Message;send.Text=pending is null?"보내기":"같은 메시지 다시 전송";
            }
            finally{send.IsEnabled=true;attach.IsEnabled=pending is null;}
        };
        await Navigation.PushAsync(page);
    }
    partial void ConfigureDesktopDetail(ContentPage page,JsonElement value,MessengerApi source)
    {
        var scroll=page.Content;var stack=(VerticalStackLayout)((ScrollView)scroll).Content;stack.Padding=24;
        var heading=(Label)stack.Children[0];heading.FontSize=22;heading.MaxLines=1;heading.LineBreakMode=LineBreakMode.TailTruncation;
        if(stack.Children[1] is Label{FormattedText:{} formatted})foreach(var span in formatted.Spans)span.FontSize*=1.25;
        var metadata=Text(value.GetProperty("message").GetProperty("senderName").GetString()+" · "+Date(value.GetProperty("message").GetProperty("createdAt").GetInt64()),11);metadata.TextColor=Muted;stack.Children.Insert(1,metadata);
        var grid=new Grid{RowDefinitions={new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Colors.White};grid.Add(scroll);
        var sender=value.GetProperty("message").GetProperty("senderId").GetString();
        var reply=DesktopButton("답장",()=>MacCompose(sender),true);reply.Margin=16;reply.MinimumWidthRequest=100;reply.HorizontalOptions=LayoutOptions.End;
        grid.Add(reply,0,1);
        if(Field(value,"submissionRequestId") is{} requestId){var collect=DesktopButton("파일 제출 현황",()=>MacWork("submission-requests",requestId));collect.AutomationId="mac-open-submission";collect.Margin=16;collect.HorizontalOptions=LayoutOptions.Start;grid.Add(collect,0,1);}
        page.Content=grid;page.AutomationId="mac-message-reader";
    }
}
