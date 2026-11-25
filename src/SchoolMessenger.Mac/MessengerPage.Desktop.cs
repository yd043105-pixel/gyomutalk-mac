using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;
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
        await MacQuietly(()=>RefreshMacFeatures(source));if(desktopOwner!=source)return;
#if !MAC_VERIFY
        // The verification run has no one to answer the permission prompt; it checks the counts instead.
        MacNotifications.Start();
#endif
        MacNotifications.Opened=(target,room)=>{if(desktopOwner==source)_=OpenMacNotification(target,room);};
        var timer=Dispatcher.CreateTimer();timer.Interval=TimeSpan.FromSeconds(30);
        timer.Tick+=async(_,_)=>{if(desktopOwner!=source)return;await RefreshDesktopTimetable(source);await MacQuietly(()=>MacRemindSwaps(source));};timer.Start();timetableTimer=timer;
        if(!await OpenDesktopArchive(source)||desktopOwner!=source)return;
        var hub=new HubConnectionBuilder().WithUrl(new Uri(source.Address,"hub"),options=>
        {
            options.Cookies=source.SessionCookies;
            options.HttpMessageHandlerFactory=handler=>{if(handler is HttpClientHandler client){client.AllowAutoRedirect=false;client.ServerCertificateCustomValidationCallback=CertificatePins.Callback(source.Address);}return handler;};
            options.WebSocketConfiguration=socket=>socket.RemoteCertificateValidationCallback=(_,certificate,_,errors)=>CertificatePins.Validate(source.Address,certificate,errors);
        }).WithAutomaticReconnect().Build();
        desktopHub=hub;WatchMacHub(hub,source);
        hub.On<JsonElement>("NewMessage",message=>MainThread.BeginInvokeOnMainThread(()=>{if(desktopOwner!=source)return;desktopStatus.Text="새 메시지가 도착했습니다";_=SynchronizeDesktopArchive(source);_=MacQuietly(()=>MacNotifyUnread(source,"message",null));}));
        hub.On<JsonElement>("MessageRecalled",id=>MainThread.BeginInvokeOnMainThread(()=>{if(desktopOwner==source&&desktopArchive?.Owner==source&&id.ValueKind==JsonValueKind.String)try{desktopArchive.Remove(id.GetString()!);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException){}}));
        hub.On("TimetableAvailabilityChanged",()=>MainThread.InvokeOnMainThreadAsync(()=>desktopOwner==source?RefreshDesktopTimetable(source):Task.CompletedTask));
        hub.Reconnecting+=_=>{MainThread.BeginInvokeOnMainThread(()=>desktopStatus.Text="다시 연결 중");return Task.CompletedTask;};
        hub.Reconnected+=connection=>{MainThread.BeginInvokeOnMainThread(()=>{desktopStatus.Text="온라인";_=SynchronizeDesktopArchive(source);_=RefreshDesktopTimetable(source);});return Task.CompletedTask;};
        hub.Closed+=_=>{MainThread.BeginInvokeOnMainThread(()=>{if(desktopHub==hub){desktopHub=null;desktopStatus.Text="연결 끊김 · 받은함 새로고침";}});return Task.CompletedTask;};
        try{await hub.StartAsync();if(desktopOwner==source){desktopStatus.Text=source.StorageWarning??"온라인";_=SynchronizeDesktopArchive(source);_=MacQuietly(async()=>{await MacCountUnread(source);await MacRemindSwaps(source);});}}
        catch(Exception error)when(error is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {if(desktopHub==hub){desktopHub=null;desktopStatus.Text="실시간 연결 실패 · 받은함으로 재시도";}await hub.DisposeAsync();}
    }
    internal void StopDesktopPresence()
    {
        timetableTimer?.Stop();timetableTimer=null;SetDesktopTimetable(false);
        var hub=desktopHub;desktopHub=null;desktopOwner=null;desktopArchive=null;desktopAway=false;desktopStatus.Text="로그인 필요";
        MacNotifications.Opened=null;ResetMacSession();
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
    // 조직도 작게 보기, as in the Windows "화면 · 조직도 보기" menu: one short line per person.
    static bool MacCompactRoster{get=>Preferences.Default.Get("mac-compact-roster",false);set=>Preferences.Default.Set("mac-compact-roster",value);}
    // Favourites stay on this Mac for this account, as they stay on the Windows PC.
    HashSet<string> MacFavorites()=>MacList("mac-favorites-"+MacUserKey);
    async Task MacPeople(string presence="all")
    {
        var source=api!;var version=epoch;var people=await source.Get("api/users");
        RosterFolder[]? folders = null; var ordered = false;
        try { var read = (await source.Get("api/roster-folders")).Deserialize(MobileJson.Default.RosterFolders); folders = read?.Folders; ordered = read?.Ordered == true; }
        catch (MessengerApiRefusal e) when (e.Status == 404) { }
        if(!Current(source,version))return;
        var me=MacUserKey;
        var directory = people.EnumerateArray().Select(person => (
            Person: person,
            Id: person.GetProperty("id").GetString()!,
            Name: person.GetProperty("name").GetString()!,
            Department: person.GetProperty("department").GetString() ?? "교직원")).ToDictionary(row => row.Id);
        var page=DesktopPage("조직도");var root=(Grid)page.Content;
        // Windows main window: search on the soft canvas, presence chips, the folder tree, and a fixed send footer.
        var content=new Grid{Padding=new Thickness(24,18,24,0),RowSpacing=12,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star)}};root.Add(content,0,1);
        var heading=new VerticalStackLayout{Spacing=2};heading.Add(Text("교직원",22,true));var total=Text("전체 "+directory.Count+"명",11);total.TextColor=Muted;heading.Add(total);content.Add(heading);
        var search=new Entry{ClassId="school-field",Placeholder="이름 · 부서 · 폴더 검색",FontSize=15};content.Add(SearchField(search),0,1);
        var favorites=MacFavorites();var compact=MacCompactRoster;var onlineFirst=false;
        var chipRow=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};var chips=new HorizontalStackLayout{Spacing=8};chipRow.Add(chips);content.Add(chipRow,0,2);
        var sort=DesktopButton("");SubtleButton(sort);sort.AutomationId="mac-roster-sort";chipRow.Add(sort,1);
        void SortLabel(){sort.Text=onlineFirst?"접속순 ↕":ordered?"지정순 ↕":"이름순 ↕";ToolTipProperties.SetText(sort,onlineFirst?"현재 접속자 우선 · 누르면 "+(ordered?"관리자가 정한 순서":"이름순"):"누르면 접속자 우선");}
        var selection = new HashSet<string>();
        var send = DesktopButton("선택 0명에게 메시지", () => MacCompose(selectedIds: selection.ToArray()), true);
        send.IsEnabled = false;
        var footerActions=new HorizontalStackLayout{Spacing=8};
        Button? chatSelected=null,surveySelected=null;
        if(MacFeature("chat")){chatSelected=DesktopButton("채팅",async()=>{var ids=selection.Where(id=>id!=me).ToArray();if(ids.Length==0)throw new InvalidOperationException("다른 교직원을 선택하세요.");await Navigation.PopAsync();SelectRail("chat");await MacChat(members:ids);});chatSelected.IsEnabled=false;chatSelected.AutomationId="mac-roster-chat";footerActions.Add(chatSelected);}
        if(MacFeature("survey")){surveySelected=DesktopButton("설문",async()=>{var ids=selection.ToArray();await Navigation.PopAsync();SelectRail("survey");await MacSurveys("sent",null,true,ids);});surveySelected.IsEnabled=false;surveySelected.AutomationId="mac-roster-survey";footerActions.Add(surveySelected);}
        footerActions.Add(send);
        var footer=new Grid{Padding=new Thickness(24,12),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Surface};
        var chosen=Text("0명 선택",12,true);chosen.TextColor=Blue;chosen.VerticalOptions=LayoutOptions.Center;footer.Add(chosen);footer.Add(footerActions,1);root.Add(footer,0,2);root.Add(Rule(),0,2);
        var rows=new VerticalStackLayout{Spacing=compact?0:2,Padding=new Thickness(0,0,0,12)};content.Add(new ScrollView{Content=rows},0,3);
        var checks = new List<(CheckBox Check, string Id)>();
        var collapsed = new HashSet<string>();
        static (bool Online,bool Away,bool Phone) State(JsonElement person)=>(person.GetProperty("online").GetBoolean(),person.GetProperty("away").GetBoolean(),person.GetProperty("mobile").GetBoolean());
        static int Rank(JsonElement person)=>State(person) switch{{Online:true,Away:false}=>0,{Online:true}=>1,{Phone:true}=>2,_=>3};
        void Chips()
        {
            chips.Clear();
            foreach(var (key,label) in new[]{("all","전체"),("online","온라인"),("away","자리비움"),("favorites","즐겨찾기")})
            {
                var chip=DesktopButton(label,()=>{presence=key;Chips();Show();return Task.CompletedTask;});chip.AutomationId="mac-roster-"+key;Chip(chip,presence==key);chips.Add(chip);
            }
        }
        void Selected()
        {
            send.Text = $"선택 {selection.Count}명에게 메시지";chosen.Text = $"{selection.Count}명 선택";send.IsEnabled = selection.Count > 0;
            if(chatSelected is not null)chatSelected.IsEnabled=selection.Any(id=>id!=me);if(surveySelected is not null)surveySelected.IsEnabled=selection.Count>0;
        }
        // What the Windows right-click menu offers for one person.
        async Task Menu(string id)
        {
            var row=directory[id];var (online,away,phone)=State(row.Person);var position=Field(row.Person,"position")??"";
            var options=new List<string>{"메시지 보내기","파일 보내기"};
            if(MacFeature("chat")&&id!=me)options.Add("채팅하기");
            if(MacFeature("survey"))options.Add("설문 보내기");
            options.Add("주고받은 메시지");options.Add("사용자 정보");options.Add(favorites.Contains(id)?"즐겨찾기 해제":"즐겨찾기 추가");
            var picked=await page.DisplayActionSheetAsync(row.Name+" · "+row.Department,"닫기",null,options.ToArray());
            switch(picked)
            {
                case "메시지 보내기":await MacCompose(id);break;
                case "파일 보내기":await MacCompose(id,pickFiles:true);break;
                case "채팅하기":await Navigation.PopAsync();SelectRail("chat");await MacChat(members:[id]);break;
                case "설문 보내기":await Navigation.PopAsync();SelectRail("survey");await MacSurveys("sent",null,true,[id]);break;
                case "주고받은 메시지":await Navigation.PopAsync();macConversationWith=id;macConversationName=row.Name;homeTab="notices";view="conversation";offset=0;await Home();break;
                case "사용자 정보":
                    var status=online?(away?"자리비움":"온라인"):phone?"휴대폰":"오프라인";
                    await page.DisplayAlertAsync("사용자 정보",$"이름: {row.Name}\n부서: {row.Department}\n직책: {(position.Length>0?position:"-")}\n상태: {status}","확인");break;
                case "즐겨찾기 추가" or "즐겨찾기 해제":
                    if(!favorites.Add(id))favorites.Remove(id);SaveMacList("mac-favorites-"+me,favorites);Show();break;
            }
        }
        void Show()
        {
            rows.Clear(); checks.Clear();SortLabel();
            var query = search.Text?.Trim() ?? "";
            var shown = directory.Values.Where(row => presence switch { "online" => State(row.Person) is { Online: true, Away: false }, "away" => State(row.Person) is { Online: true, Away: true }, "favorites" => favorites.Contains(row.Id), _ => true });
            var groups = RosterGroup.Build(folders, shown.Select(row => (row.Id, row.Name + " · " + row.Department, row.Department)).ToArray(), query, hideEmpty: presence != "all", ordered: ordered);
            foreach (var group in groups) AddGroup(group, 0);
            if (rows.Children.Count == 0) rows.Add(presence=="favorites"&&query.Length==0
                ? DesktopEmptyState("즐겨찾기한 교직원이 없습니다","교직원 줄의 ⋯ 버튼에서 즐겨찾기에 추가하세요.")
                : query.Length>0||presence!="all"
                ? DesktopEmptyState("일치하는 교직원이 없습니다",query.Length>0?"이름이나 부서, 폴더 이름을 바꿔 보세요.":"지금 이 상태인 교직원이 없습니다.")
                : DesktopEmptyState("표시할 교직원이 없습니다","관리자가 교직원을 등록하면 여기에 표시됩니다."));

            void AddGroup(RosterGroup group, int indent)
            {
                var expanded = query.Length > 0 || !collapsed.Contains(group.Key);
                var header = DesktopButton((expanded ? "⌄  " : "›  ") + group.Name + " · " + group.AllIds().Length + "명", () =>
                {
                    if (!collapsed.Add(group.Key)) collapsed.Remove(group.Key);
                    Show(); return Task.CompletedTask;
                });
                header.HorizontalOptions = LayoutOptions.Start;
                header.BackgroundColor = Colors.Transparent;header.BorderWidth = 0;header.TextColor = Muted;header.FontAttributes = FontAttributes.Bold;
                header.FontSize = compact?13:14;header.MinimumHeightRequest = compact?26:32;header.Padding = new Thickness(6,compact?2:4);
                header.Margin = new Thickness(indent, compact?2:6, 0, 0);
                rows.Add(header);
                if (!expanded) return;
                foreach (var child in group.Children) AddGroup(child, indent + 20);
                var members=group.DirectIds.Select(id => directory[id]);
                var sorted=onlineFirst?members.OrderBy(p=>Rank(p.Person)):members.OrderBy(p=>0);
                foreach(var row in sorted.ThenBy(p => ordered ? Array.IndexOf(group.DirectIds, p.Id) : 0).ThenBy(p => p.Name))
                {
                    var person = row.Person;
                    var (online,away,phone)=State(person);
                    var status=online?(away?"자리비움":"온라인"):phone?"휴대폰":"오프라인";var color=online?(away?Warning:Success):phone?Blue:Faint;
                    var line=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=compact?8:10,Padding=new Thickness(6 + indent,compact?1:4,4,compact?1:4)};
                    var check = new CheckBox { IsChecked = selection.Contains(row.Id), VerticalOptions = LayoutOptions.Center };
                    SemanticProperties.SetDescription(check, row.Name + " 선택");
                    checks.Add((check, row.Id)); line.Add(check);
                    var item = new Border { Content = line, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = Radius }, BackgroundColor = check.IsChecked ? Pale : Colors.Transparent };
                    item.AutomationId=compact?"mac-roster-row-compact":"mac-roster-row";
                    check.CheckedChanged += (_, e) =>
                    {
                        item.BackgroundColor = e.Value ? Pale : Colors.Transparent;
                        var changed = e.Value ? selection.Add(row.Id) : selection.Remove(row.Id);
                        if (!changed) return;
                        foreach (var (other, id) in checks)
                            if (id == row.Id) other.IsChecked = e.Value;
                        Selected();
                    };
                    line.Add(AvatarWithPresence(row.Name,compact?24:40,row.Id,online,away,phone,row.Name+" · "+status),1);
                    var position=person.TryGetProperty("position",out var role)?role.GetString():null;var star=favorites.Contains(row.Id)?" ★":"";
                    var button=DesktopButton(row.Name+star+(string.IsNullOrWhiteSpace(position)?"":" · "+position),()=>MacCompose(row.Id));button.ClassId="school-story";button.BorderWidth=0;button.BackgroundColor=Colors.Transparent;button.Padding=0;button.MinimumHeightRequest=compact?20:24;button.FontSize=compact?14:16;button.FontAttributes=FontAttributes.Bold;button.HorizontalOptions=LayoutOptions.Fill;
                    if(compact)
                    {
                        // One line: name and position, then the department in the muted colour.
                        var names=new HorizontalStackLayout{Spacing=8,VerticalOptions=LayoutOptions.Center};names.Add(button);
                        var department=Text(row.Department,11);department.TextColor=Muted;department.MaxLines=1;department.LineBreakMode=LineBreakMode.TailTruncation;department.VerticalOptions=LayoutOptions.Center;names.Add(department);
                        button.HorizontalOptions=LayoutOptions.Start;line.Add(names,2);
                    }
                    else
                    {
                        var names=new VerticalStackLayout{Spacing=1,VerticalOptions=LayoutOptions.Center};names.Add(button);
                        var department=Text(row.Department,11);department.TextColor=Muted;department.MaxLines=1;department.LineBreakMode=LineBreakMode.TailTruncation;names.Add(department);line.Add(names,2);
                    }
                    var state=Text(status,11);state.TextColor=color;state.VerticalOptions=LayoutOptions.Center;line.Add(state,3);
                    var more=new Button{Text="⋯",FontSize=16,Padding=0,WidthRequest=30,HeightRequest=compact?24:30,MinimumHeightRequest=0,MinimumWidthRequest=0,BorderWidth=0,BackgroundColor=Colors.Transparent,TextColor=Muted,AutomationId="mac-roster-menu"};
                    ToolTipProperties.SetText(more,row.Name+" 메뉴");SemanticProperties.SetDescription(more,row.Name+" 메뉴");
                    var id=row.Id;more.Clicked+=async(_,_)=>await Run(()=>Menu(id));line.Add(more,4);
                    // A right click on the row opens the same menu, as on Windows.
                    var secondary=new TapGestureRecognizer{Buttons=ButtonsMask.Secondary};secondary.Tapped+=async(_,_)=>await Run(()=>Menu(id));item.GestureRecognizers.Add(secondary);
                    rows.Add(item);
                }
            }
        }
        sort.Clicked+=(_,_)=>{onlineFirst=!onlineFirst;Show();};
        search.TextChanged+=(_,_)=>Show();Chips();Show();await Navigation.PushAsync(page);
    }
    async Task MacCompose(string? recipient=null, string[]? selectedIds=null, bool pickFiles=false, string? draftId=null)
    {
        var source=api!;var version=epoch;var people=await source.Get("api/users");if(!Current(source,version))return;
        var me=source.Session.GetProperty("user").GetProperty("id").GetString()!;
        var page=DesktopPage("새 메시지");composer=page;var grid=(Grid)page.Content;
        var fields=new Grid{Padding=new Thickness(24,16,24,14),RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto)}};grid.Add(fields,0,1);
        fields.AutomationId="mac-compose-attachment-drop";
        var recipients=new List<string>();
        var error=Text("",12);error.TextColor=Danger;fields.Add(error,0,6);
        var everyone=people.EnumerateArray().ToArray();
        var directory=everyone.ToDictionary(p=>p.GetProperty("id").GetString()!,p=>p.GetProperty("name").GetString()+" · "+p.GetProperty("department").GetString());
        void Pick(string id){if(!recipients.Contains(id))recipients.Add(id);}
        if (selectedIds is not null)
        {
            if (selectedIds.Any(id => !directory.ContainsKey(id)))
                throw new InvalidOperationException("선택한 교직원 중 현재 메시지를 받을 수 없는 계정이 있습니다. 조직도를 다시 열어 주세요.");
            foreach(var id in selectedIds)Pick(id);
        }
        if(recipient is not null){if(!directory.ContainsKey(recipient))throw new InvalidOperationException("현재 메시지를 받을 수 없는 계정입니다.");Pick(recipient);}
        var recipientRows=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap,Direction=Microsoft.Maui.Layouts.FlexDirection.Row,AlignItems=Microsoft.Maui.Layouts.FlexAlignItems.Center};var recipientCount=Text("받는 사람",13,true);
        // Typing a name or department next to the chips lists the matching people; Enter or a click adds one. The list
        // follows the typing without rewriting the box, so Korean stays whole.
        var typed=new Entry{ClassId="school-field",Placeholder="이름 입력 후 Enter",FontSize=14,WidthRequest=170,BackgroundColor=Colors.Transparent,TextColor=Navy,PlaceholderColor=Faint,AutomationId="mac-recipient-input"};
        SemanticProperties.SetDescription(typed,"받는 사람 이름 입력");ToolTipProperties.SetText(typed,"이름이나 부서를 입력하고 Enter");
        var suggestions=new VerticalStackLayout{Spacing=0};var suggestionBox=new Border{Content=new ScrollView{Content=suggestions,MaximumHeightRequest=220},Padding=4,BackgroundColor=Surface,Stroke=Line,StrokeThickness=1,StrokeShape=new RoundRectangle{CornerRadius=6},IsVisible=false,MaximumWidthRequest=360,HorizontalOptions=LayoutOptions.Start,AutomationId="mac-recipient-suggestions"};
        var locked=false;
        void ShowRecipients()
        {
            recipientRows.Clear();
            foreach(var id in recipients.ToArray())
            {
                // Windows compose chip: initial avatar, name and a remove button on the accent tint.
                var name=directory[id];var person=Text(name,12,true);person.TextColor=Blue;person.VerticalOptions=LayoutOptions.Center;person.MaxLines=1;
                var remove=new Button{Text="×",FontSize=16,Padding=0,WidthRequest=26,HeightRequest=26,MinimumHeightRequest=0,MinimumWidthRequest=0,BackgroundColor=Colors.Transparent,BorderWidth=0,TextColor=Blue,CornerRadius=13,IsEnabled=!locked};
                SemanticProperties.SetDescription(remove,name+" 빼기");remove.Clicked+=(_,_)=>{recipients.Remove(id);ShowRecipients();};
                var chipRow=new HorizontalStackLayout{Spacing=6};chipRow.Add(Avatar(name,24,id));chipRow.Add(person);chipRow.Add(remove);
                recipientRows.Add(new Border{Content=chipRow,Padding=new Thickness(4,3,4,3),Margin=3,BackgroundColor=Pale,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=16}});
            }
            if(recipients.Count==0){var none=Text("이름을 입력하거나 조직도에서 선택하세요.",11);none.TextColor=Muted;none.Margin=new Thickness(6,6,4,6);recipientRows.Add(none);}
            recipientRows.Add(typed);typed.IsEnabled=!locked;
            recipientCount.Text="받는 사람 "+recipients.Count+"명";
        }
        string[] Matches(string text)=>directory.Where(p=>!recipients.Contains(p.Key)&&p.Value.Contains(text,StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(p=>p.Value.StartsWith(text,StringComparison.CurrentCultureIgnoreCase)?0:1).ThenBy(p=>p.Value,StringComparer.Create(KoreanCulture,false)).Take(30).Select(p=>p.Key).ToArray();
        void Add(string id){if(locked)return;Pick(id);typed.Text="";suggestionBox.IsVisible=false;ShowRecipients();typed.Focus();}
        typed.TextChanged+=(_,_)=>
        {
            var text=typed.Text?.Trim()??"";suggestions.Clear();
            // A pasted list ("가상교사01, 가상교사02") adds everyone whose name matches exactly one person.
            var parts=text.Split([',',';','\n','\r','\t'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length>=2)
            {
                var left=new List<string>();
                foreach(var part in parts){var hits=everyone.Where(p=>Field(p,"name")==part).ToArray();if(hits.Length==1)Pick(RowId(hits[0]));else left.Add(part);}
                ShowRecipients();typed.Text=string.Join(", ",left);
                if(left.Count>0)error.Text="찾지 못했거나 같은 이름이 여럿인 사람: "+string.Join(", ",left);
                return;
            }
            if(text.Length==0){suggestionBox.IsVisible=false;return;}
            foreach(var id in Matches(text))
            {
                var choice=new Button{Text=directory[id],FontSize=14,BackgroundColor=Colors.Transparent,BorderWidth=0,TextColor=Navy,Padding=new Thickness(8,4),HorizontalOptions=LayoutOptions.Fill,MinimumHeightRequest=30,ClassId="school-story"};
                choice.Clicked+=(_,_)=>Add(id);suggestions.Add(choice);
            }
            suggestionBox.IsVisible=suggestions.Children.Count>0;
        };
        typed.Completed+=(_,_)=>{var text=typed.Text?.Trim()??"";if(text.Length>0&&Matches(text).FirstOrDefault() is{} first)Add(first);};
        var targets=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=8};recipientCount.VerticalOptions=LayoutOptions.Center;targets.Add(recipientCount);fields.Add(targets);
        var recipientArea=new VerticalStackLayout{Spacing=4};recipientArea.Add(DesktopPanel(new ScrollView{Content=recipientRows,MaximumHeightRequest=96},8,Canvas));recipientArea.Add(suggestionBox);fields.Add(recipientArea,0,1);
        var add=MakeAction("조직도에서 선택",async()=>
        {
            var chosen=recipients.ToHashSet();
            await MacPickPeople("받는 사람 선택","선택 적용",everyone,chosen,()=>{recipients.RemoveAll(id=>!chosen.Contains(id));foreach(var id in chosen)Pick(id);ShowRecipients();return Task.CompletedTask;});
        });targets.Add(add,2);
        var self=new CheckBox{Color=Blue,VerticalOptions=LayoutOptions.Center,IsChecked=recipients.Contains(me),AutomationId="mac-compose-self"};
        self.CheckedChanged+=(_,e)=>{if(e.Value)Pick(me);else recipients.Remove(me);ShowRecipients();};
        targets.Add(new HorizontalStackLayout{Spacing=2,VerticalOptions=LayoutOptions.Center,Children={self,Mid(Text("나에게도 보내기",13))}},1);
        var title=new Entry{ClassId="school-field",Placeholder="제목 · 비워 두면 본문 앞부분이 표시됩니다",MaxLength=200,FontSize=16,TextColor=Navy,PlaceholderColor=Muted,BackgroundColor=Colors.Transparent,AutomationId="mac-compose-title"};fields.Add(DesktopPanel(title,10),0,2);
        var text=new Editor{Placeholder="메시지 내용을 입력하세요",MaxLength=10000,FontSize=18,TextColor=Navy,PlaceholderColor=Muted,BackgroundColor=Colors.Transparent,VerticalOptions=LayoutOptions.Fill,AutomationId="mac-compose-body"};fields.Add(DesktopPanel(text,12),0,3);
        var files=new List<(string Id,string Path,long Size,Label Label,string Name)>();var names=new VerticalStackLayout{Spacing=4};var fileHint=Text("",11);fileHint.TextColor=Muted;names.Add(fileHint);
        void ShowLimit()=>fileHint.Text=$"파일당·합계 {AttachmentLimits.Label(source.AttachmentLimit)} · 파일 또는 폴더 끌어놓기 (폴더는 ZIP)";
        ShowLimit();
        var fileZone=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=10};names.VerticalOptions=LayoutOptions.Center;fileZone.Add(names,2);
        var client=Guid.NewGuid().ToString("N");JsonObject? pending=null;var sent=false;
        var draft=draftId??Guid.NewGuid().ToString("N");
        var send=DesktopButton("보내기",null,true);send.AutomationId="mac-send";send.MinimumWidthRequest=112;send.HorizontalOptions=LayoutOptions.End;
        var attach=DesktopButton("파일 첨부");var paste=DesktopButton("붙여넣기");paste.AutomationId="mac-compose-paste";ToolTipProperties.SetText(paste,"복사한 사진이나 파일을 첨부합니다");
        var attaching=false;
        Label FileLabel(string name,long size){var label=Text(name+" · "+FileSize(size),12);label.MaxLines=1;label.LineBreakMode=LineBreakMode.TailTruncation;return label;}
        async Task AddAttachment(FileResult file,string key)
        {
            if(files.Any(x=>x.Path==key))return;
            if(files.Count>=10)throw new IOException("첨부는 최대 10개입니다.");
            var size=new FileInfo(file.FullPath).Length;
            if(size<=0||size>source.AttachmentLimit-files.Sum(x=>x.Size))throw new IOException($"첨부 합계는 {AttachmentLimits.Label(source.AttachmentLimit)}까지입니다.");
            error.Text="첨부 업로드 중 · "+file.FileName;
            var upload=await source.Upload(file,false);
            if(!Current(source,version))return;
            var name=FileLabel(file.FileName,upload.GetProperty("size").GetInt64());names.Add(name);
            files.Add((upload.GetProperty("id").GetString()!,key,upload.GetProperty("size").GetInt64(),name,file.FileName));error.Text="";
        }
        async Task AddFiles(Func<Task> action,bool refresh=true)
        {
            if(attaching||pending is not null||!Current(source,version))return;
            attaching=true;
            attach.IsEnabled=paste.IsEnabled=send.IsEnabled=false;
            try
            {
                if(refresh){await source.RefreshAttachmentLimit();ShowLimit();}
                await action();
            }
            catch(Exception e){error.Text=e.Message;}
            finally{attaching=false;send.IsEnabled=true;attach.IsEnabled=paste.IsEnabled=pending is null;}
        }
        async Task AddPath(string path)
        {
            var isFolder=Directory.Exists(path);
            if(isFolder)error.Text="폴더 ZIP 준비 중 · "+System.IO.Path.GetFileName(path);
            using var folder=isFolder?await Task.Run(()=>FolderAttachment.Create(path,source.AttachmentLimit-files.Sum(x=>x.Size))):null;
            if(!Current(source,version))return;
            await AddAttachment(new FileResult(folder?.Path??path),path);
        }
        async Task Choose()
        {
            preserveComposerOnResume=true;
            var picked=(await FilePicker.Default.PickMultipleAsync(new PickOptions{PickerTitle="메시지 첨부파일 선택"}))?.OfType<FileResult>().ToArray()??[];
            foreach(var file in picked)if(Current(source,version))await AddAttachment(file,file.FullPath);
        }
        attach.Clicked+=async(_,_)=>await AddFiles(Choose);
        paste.Clicked+=async(_,_)=>await AddFiles(async()=>
        {
            var paths=MacClipboard.Take();
            if(paths.Length==0)throw new InvalidOperationException("붙여넣을 사진이나 파일이 없습니다. 사진이나 파일을 먼저 복사하세요.");
            foreach(var path in paths)await AddPath(path);
        });
        fileZone.Add(attach);fileZone.Add(paste,1);fields.Add(DesktopPanel(fileZone,10,Canvas),0,4);
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
                    if(!canUpload||!Current(source,version)||files.Any(f=>f.Path==item.Path))continue;
                    await AddPath(item.Path);
                }
                catch(Exception failure){failures.Add(failure.Message);}
            }
            if(failures.Count>0)error.Text=string.Join("\n",failures.Distinct());
        },refresh:false));
        // AI, the saved drafts and the scheduled messages, under the attachments as on Windows.
        var tools=new HorizontalStackLayout{Spacing=4};fields.Add(tools,0,5);
        Button Tool(string label,string id){var b=DesktopButton(label);SubtleButton(b);b.AutomationId=id;tools.Add(b);return b;}
        if(MacFeature("ai")){var polish=Tool("AI로 다듬기 ▾","mac-compose-ai");polish.Clicked+=async(_,_)=>await Run(()=>MacRewrite(page,text,error));}
        var saveDraft=Tool("임시저장","mac-compose-draft");var draftList=Tool("임시저장 목록","mac-compose-drafts");var scheduledList=Tool("예약 목록","mac-compose-scheduled");
        var footer=new Grid{Padding=new Thickness(24,12),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Surface};
        var options=new HorizontalStackLayout{Spacing=14,VerticalOptions=LayoutOptions.Center};
        var caption=Text("파일 제출 요청 · 마감일",12);caption.TextColor=Muted;caption.VerticalOptions=LayoutOptions.Center;
        var collect=new CheckBox{AutomationId="mac-collect",Color=Blue,VerticalOptions=LayoutOptions.Center};var collectDate=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=DateTime.UtcNow.AddHours(9).Date.AddDays(7),IsEnabled=false,VerticalOptions=LayoutOptions.Center};collect.CheckedChanged+=(_,e)=>collectDate.IsEnabled=e.Value&&pending is null;
        // Collected files are handed in through the task list, so the option goes with that menu.
        if(MacFeature("tasks"))options.Add(new HorizontalStackLayout{Spacing=6,Children={collect,Mid(caption),collectDate}});
        var schedule=new CheckBox{AutomationId="mac-schedule",Color=Blue,VerticalOptions=LayoutOptions.Center};var scheduleCaption=Text("예약 전송",12);scheduleCaption.TextColor=Muted;scheduleCaption.VerticalOptions=LayoutOptions.Center;
        var scheduleDate=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=DateTime.UtcNow.AddHours(9).Date.AddDays(1),IsEnabled=false,VerticalOptions=LayoutOptions.Center};
        var scheduleTime=new TimePicker{ClassId="school-field",Format="HH:mm",Time=new TimeSpan(9,0,0),IsEnabled=false,VerticalOptions=LayoutOptions.Center};
        ToolTipProperties.SetText(scheduleTime,"한국 시간");
        schedule.CheckedChanged+=(_,e)=>{scheduleDate.IsEnabled=scheduleTime.IsEnabled=e.Value&&pending is null;send.Text=e.Value?"예약 저장":"보내기";};
        options.Add(new HorizontalStackLayout{Spacing=6,Children={schedule,scheduleCaption,scheduleDate,scheduleTime}});
        footer.Add(options);footer.Add(send,1);grid.Add(footer,0,2);grid.Add(Rule(),0,2);
        void Lock(bool on)
        {
            locked=on;title.IsEnabled=text.IsEnabled=self.IsEnabled=add.IsEnabled=collect.IsEnabled=schedule.IsEnabled=saveDraft.IsEnabled=!on;
            collectDate.IsEnabled=!on&&collect.IsChecked;scheduleDate.IsEnabled=scheduleTime.IsEnabled=!on&&schedule.IsChecked;ShowRecipients();
        }
        bool Empty()=>string.IsNullOrWhiteSpace(title.Text)&&string.IsNullOrWhiteSpace(text.Text)&&files.Count==0&&recipients.Count==0;
        JsonObject DraftValue()=>new()
        {
            ["id"]=draft,["title"]=title.Text??"",["body"]=text.Text??"",["recipientIds"]=new JsonArray(recipients.Select(r=>(JsonNode?)JsonValue.Create(r)).ToArray()),
            ["files"]=new JsonArray(files.Select(f=>(JsonNode?)new JsonObject{["id"]=f.Id,["name"]=f.Name,["size"]=f.Size,["path"]=f.Path}).ToArray()),
            ["collect"]=collect.IsChecked,["collectDate"]=collectDate.Date?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            ["schedule"]=schedule.IsChecked,["scheduleDate"]=scheduleDate.Date?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),["scheduleTime"]=scheduleTime.Time is TimeSpan chosenTime?chosenTime.ToString(@"hh\:mm",CultureInfo.InvariantCulture):null,
            ["pending"]=pending?.DeepClone()
        };
        // Saved on every leave, so a closed window loses nothing; a sent message takes its draft with it.
        bool SaveDraft(){if(sent||Empty()||desktopArchive?.Owner!=source)return false;try{desktopArchive.SaveDraft(draft,DraftValue());return true;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or CryptographicException){error.Text=DesktopArchiveFailed;return false;}}
        void DropDraft(){if(desktopArchive?.Owner==source)try{desktopArchive.DeleteDraft(draft);}catch(IOException){}}
        int DraftCount(){try{return desktopArchive?.Owner==source?desktopArchive.Drafts().Length:0;}catch(IOException){return 0;}}
        void CountDrafts()=>draftList.Text=$"임시저장 목록 ({DraftCount()})";
        page.Disappearing+=(_,_)=>SaveDraft();
        saveDraft.Clicked+=(_,_)=>{if(Empty()){error.Text="저장할 내용이 없습니다.";return;}if(SaveDraft()){error.Text="";feedbackSaved();CountDrafts();}};
        void feedbackSaved(){var stamp=DateTime.Now.ToString("HH:mm",CultureInfo.InvariantCulture);fileHint.Text=$"{stamp} 임시저장했습니다 · 창을 닫아도 자동 임시저장됩니다.";}
        draftList.Clicked+=async(_,_)=>await Run(()=>MacDrafts(source,draft));
        scheduledList.Clicked+=async(_,_)=>await Run(()=>MacScheduled(source));
        // A draft opened from the list brings everything back, including a send that may not have gone through.
        if(draftId is not null&&desktopArchive?.Owner==source&&desktopArchive.Draft(draftId) is{} saved)
        {
            title.Text=Field(saved,"title")??"";text.Text=Field(saved,"body")??"";
            foreach(var id in saved.GetProperty("recipientIds").EnumerateArray().Select(r=>r.GetString()!).Where(directory.ContainsKey))Pick(id);
            var kept=saved.GetProperty("files").EnumerateArray().ToArray();
            if(kept.Length>0)
            {
                using var asked=new StringContent(new JsonArray(kept.Select(f=>(JsonNode?)JsonValue.Create(Field(f,"id"))).ToArray()).ToJsonString(),Encoding.UTF8,"application/json");
                HashSet<string?> staged;
                try{staged=(await source.Send("api/attachments/staged/check",content:asked)).EnumerateArray().Select(item=>item.GetString()).ToHashSet();}
                catch(MessengerApiRefusal refusal)when(refusal.Status==404){staged=kept.Select(f=>Field(f,"id")).ToHashSet();}
                foreach(var file in kept.Where(f=>staged.Contains(Field(f,"id"))))
                {var name=FileLabel(Field(file,"name")??"",file.GetProperty("size").GetInt64());names.Add(name);files.Add((Field(file,"id")!,Field(file,"path")??"",file.GetProperty("size").GetInt64(),name,Field(file,"name")??""));}
                if(files.Count<kept.Length)error.Text="기간이 지난 첨부파일은 빠졌습니다. 해당 파일을 다시 첨부하세요.";
            }
            collect.IsChecked=Flag(saved,"collect");if(DateTime.TryParseExact(Field(saved,"collectDate"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var cd))collectDate.Date=cd;
            schedule.IsChecked=Flag(saved,"schedule");if(DateTime.TryParseExact(Field(saved,"scheduleDate"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var sd))scheduleDate.Date=sd;
            if(TimeSpan.TryParseExact(Field(saved,"scheduleTime"),@"hh\:mm",CultureInfo.InvariantCulture,out var st))scheduleTime.Time=st;
            if(saved.TryGetProperty("pending",out var waiting)&&waiting.ValueKind==JsonValueKind.Object)
            {pending=(JsonObject)JsonNode.Parse(waiting.GetRawText())!;client=(string?)pending["clientId"]??client;Lock(true);send.Text="같은 메시지 다시 전송";error.Text="전송 결과 미확인. 같은 메시지를 재시도합니다. 내용 변경은 완료 확인 후 가능합니다.";}
        }
        self.IsChecked=recipients.Contains(me);ShowRecipients();CountDrafts();
        send.Clicked+=async(_,_)=>
        {
            if(api!=source||!Current(source,version))return;
            send.IsEnabled=attach.IsEnabled=paste.IsEnabled=false;
            try
            {
                if(pending is null)
                {
                    var ids=recipients.ToArray();
                    if(ids.Length==0||string.IsNullOrWhiteSpace(text.Text))throw new InvalidOperationException("받는 사람과 메시지를 입력하세요.");
                    var today=DateTime.UtcNow.AddHours(9).Date;
                    var deadline=collectDate.Date is{} day?DateTime.SpecifyKind(day.Date,DateTimeKind.Unspecified):today;
                    if(collect.IsChecked&&deadline<today)throw new InvalidOperationException("제출 마감일은 오늘 이후로 선택하세요.");
                    long? scheduledAt=null;
                    if(schedule.IsChecked)
                    {
                        if(scheduleDate.Date is not{} on||scheduleTime.Time is not{} at)throw new InvalidOperationException("예약 날짜와 시간을 고르세요.");
                        var when=new DateTimeOffset(DateTime.SpecifyKind(on.Date.Add(at),DateTimeKind.Unspecified),TimeSpan.FromHours(9));
                        if(when<=DateTimeOffset.UtcNow)throw new InvalidOperationException("예약 시간은 지금 이후로 고르세요.");
                        scheduledAt=when.ToUnixTimeMilliseconds();
                    }
                    if(!await MacStyleChecked(page,(title.Text??"")+"\n"+(text.Text??""),error))return;
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
                            if(expired.Length>0)throw new IOException("일부 첨부가 만료됐습니다. 해당 파일만 다시 첨부하세요. "+string.Join(", ",expired.Select(f=>f.Name)));
                        }
                        catch(MessengerApiRefusal refusal)when(refusal.Status==404){}
                    }
                    // An older server must not silently take a scheduled message for immediate delivery.
                    if(scheduledAt is not null)await source.Get("api/messages/scheduled");
                    pending=new JsonObject{["clientId"]=client,["title"]=title.Text??"",["body"]=text.Text,["recipientIds"]=new JsonArray(ids.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["attachmentIds"]=new JsonArray(files.Select(x=>(JsonNode?)JsonValue.Create(x.Id)).ToArray()),["all"]=false};
                    // 서버가 마감일을 한국 시간 23:59:59로 맞춘다.
                    if(collect.IsChecked)pending["submissionDeadline"]=new DateTimeOffset(deadline.AddHours(12),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
                    if(scheduledAt is not null)pending["scheduledAt"]=scheduledAt;
                    Lock(true);
                }
                var scheduled=pending.ContainsKey("scheduledAt");
                await MacSend(source,"api/messages",pending);sent=true;DropDraft();
                if(api==source){await Navigation.PopAsync();homeTab="notices";view="sent";offset=0;await Home();if(scheduled)feedback.Text="예약 메시지를 저장했습니다. 새 메시지의 ‘예약 목록’에서 확인하고 취소할 수 있습니다.";}
            }
            catch(Exception e)
            {
                if(e is MessengerApiRefusal {Status:400 or 403 or 404 or 409 or 413 or 415 or 422}){pending=null;client=Guid.NewGuid().ToString("N");Lock(false);}
                error.Text=e.Message;send.Text=pending is null?(schedule.IsChecked?"예약 저장":"보내기"):"같은 메시지 다시 전송";
            }
            finally{send.IsEnabled=true;attach.IsEnabled=paste.IsEnabled=pending is null;}
        };
        await Navigation.PushAsync(page);
        if(pickFiles)await AddFiles(Choose);
    }
    // 임시저장 목록: open a saved message again, or throw it away.
    async Task MacDrafts(MessengerApi source,string current)
    {
        if(desktopArchive?.Owner!=source)throw new InvalidOperationException("이 Mac의 메시지 보관함을 열지 못해 임시저장을 볼 수 없습니다.");
        var page=DesktopPage("임시저장 목록");var stack=new VerticalStackLayout{Padding=new Thickness(24,16),Spacing=8};((Grid)page.Content).Add(new ScrollView{Content=stack},0,1);
        void Fill()
        {
            stack.Clear();var drafts=desktopArchive.Drafts();
            if(drafts.Length==0)stack.Add(DesktopEmptyState("임시저장한 메시지가 없습니다","쓰던 메시지는 창을 닫으면 여기에 저장됩니다."));
            foreach(var saved in drafts)
            {
                var id=Field(saved,"id")??"";var at=saved.TryGetProperty("savedAt",out var stamp)&&stamp.TryGetInt64(out var ms)?Date(ms):"";
                var words=new VerticalStackLayout{Spacing=2};var heading=Text(Field(saved,"title") is{Length:>0} t?t:SchoolMessenger.Shared.MessageText.Excerpt(Field(saved,"body"),60) is{Length:>0} b?b:"(내용 없음)",14,true);heading.MaxLines=1;heading.LineBreakMode=LineBreakMode.TailTruncation;words.Add(heading);
                var meta=Text($"{at} · 받는 사람 {saved.GetProperty("recipientIds").GetArrayLength()}명 · 첨부 {saved.GetProperty("files").GetArrayLength()}개"+(id==current?" · 지금 쓰는 메시지":""),11);meta.TextColor=Muted;words.Add(meta);
                var row=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=8};row.Add(words);
                var open=DesktopButton("열기",async()=>{while(Navigation.NavigationStack.Count>1)await Navigation.PopAsync(false);await MacCompose(draftId:id);});open.FontSize=14;open.MinimumHeightRequest=32;open.IsEnabled=id!=current;row.Add(open,1);
                var remove=DesktopButton("삭제",async()=>{if(!await page.DisplayAlertAsync("임시저장 삭제","이 임시저장을 지울까요? 되돌릴 수 없습니다.","삭제","취소"))return;desktopArchive.DeleteDraft(id);Fill();});remove.FontSize=14;remove.MinimumHeightRequest=32;remove.TextColor=Danger;remove.IsEnabled=id!=current;row.Add(remove,2);
                stack.Add(DesktopPanel(row,12));
            }
        }
        Fill();await Navigation.PushAsync(page);
    }
    // 예약 목록: the messages waiting to go out, each with its state, and a cancel while it still waits.
    async Task MacScheduled(MessengerApi source)
    {
        var page=DesktopPage("내 예약 메시지");var stack=new VerticalStackLayout{Padding=new Thickness(24,16),Spacing=8};((Grid)page.Content).Add(new ScrollView{Content=stack},0,1);
        async Task Fill()
        {
            var entries=(await source.Get("api/messages/scheduled")).EnumerateArray().ToArray();stack.Clear();
            if(entries.Length==0)stack.Add(DesktopEmptyState("등록된 예약 메시지가 없습니다","‘예약 전송’을 켜고 보내면 보내기 전까지 여기에서 확인하고 취소할 수 있습니다."));
            foreach(var entry in entries)
            {
                var id=RowId(entry);var state=Field(entry,"state");
                var words=new VerticalStackLayout{Spacing=2};words.Add(Text(Field(entry,"title")??"",14,true));
                var when=DateTimeOffset.FromUnixTimeMilliseconds(entry.GetProperty("deliverAt").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm",CultureInfo.InvariantCulture);
                var meta=Text(when+" · "+(state switch{"pending"=>"예약 대기","delivered"=>"전송 완료","cancelled"=>"취소",_=>"발송 실패"}),12);meta.TextColor=Muted;words.Add(meta);
                if(Field(entry,"error") is{Length:>0} failure){var why=Text(failure,12);why.TextColor=Danger;words.Add(why);}
                var row=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8};row.Add(words);
                if(state=="pending"){var cancel=DesktopButton("예약 취소",async()=>{if(!await page.DisplayAlertAsync("예약 취소","이 메시지를 보내지 않고 예약을 취소할까요?","예약 취소","닫기"))return;await source.Send("api/messages/scheduled/"+id,method:HttpMethod.Delete);await Fill();});cancel.FontSize=14;cancel.MinimumHeightRequest=32;row.Add(cancel,1);}
                stack.Add(DesktopPanel(row,12));
            }
        }
        await Fill();await Navigation.PushAsync(page);
    }
    partial void ConfigureDesktopDetail(ContentPage page,JsonElement value,MessengerApi source)
    {
        var scroll=page.Content;var stack=(VerticalStackLayout)((ScrollView)scroll).Content;stack.Padding=new Thickness(32,24);stack.Spacing=16;
        var heading=(Label)stack.Children[0];heading.FontSize=25;heading.MaxLines=1;heading.LineBreakMode=LineBreakMode.TailTruncation;ToolTipProperties.SetText(heading,heading.Text);
        if(stack.Children[1] is Label{FormattedText:{} formatted})foreach(var span in formatted.Spans)span.FontSize*=1.25;
        // Windows reader: sender avatar, name and time, the read count as an accent pill, then a hairline above the body.
        var message=value.GetProperty("message");var senderName=message.GetProperty("senderName").GetString()??"";
        var meta=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=12};
        meta.Add(Avatar(senderName,40,Field(message,"senderId")??senderName));
        var who=new VerticalStackLayout{Spacing=1,VerticalOptions=LayoutOptions.Center};who.Add(Text(senderName,13,true));
        var metadata=Text(Date(message.GetProperty("createdAt").GetInt64()),11);metadata.TextColor=Muted;who.Add(metadata);meta.Add(who,1);
        var receipts=value.GetProperty("recipients").EnumerateArray().ToArray();
        if(receipts.Length>0)
        {
            var read=receipts.Count(r=>r.TryGetProperty("readAt",out var at)&&at.ValueKind==JsonValueKind.Number);
            var pillText=Text("확인 "+read+"/"+receipts.Length,11,true);pillText.TextColor=Blue;
            meta.Add(new Border{Content=pillText,BackgroundColor=Pale,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=14},Padding=new Thickness(11,4),VerticalOptions=LayoutOptions.Center},2);
        }
        stack.Children.Insert(1,meta);stack.Children.Insert(2,Rule());
        foreach(var child in stack.Children)
        {
            if(child is Button file){file.BackgroundColor=Surface;file.BorderColor=Line;file.BorderWidth=1;file.CornerRadius=(int)Radius;file.TextColor=Navy;file.FontSize=15;file.Padding=new Thickness(14,8);file.HorizontalOptions=LayoutOptions.Start;}
            else if(child is Label{Text:{} note} hint&&note.StartsWith("첨부는 전송일부터",StringComparison.Ordinal))hint.TextColor=Faint;
        }
        var grid=new Grid{RowDefinitions={new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Surface};grid.Add(scroll);
        var sender=message.GetProperty("senderId").GetString();
        var reply=DesktopButton("답장",()=>MacCompose(sender),true);reply.Margin=new Thickness(24,12);reply.MinimumWidthRequest=100;reply.HorizontalOptions=LayoutOptions.End;
        grid.Add(reply,0,1);grid.Add(Rule(),0,1);
        if(Field(value,"submissionRequestId") is{} requestId){var collect=DesktopButton("파일 제출 현황",()=>MacWork("submission-requests",requestId));collect.AutomationId="mac-open-submission";collect.Margin=new Thickness(24,12);collect.HorizontalOptions=LayoutOptions.Start;grid.Add(collect,0,1);}
        page.Content=grid;page.AutomationId="mac-message-reader";
    }
}
