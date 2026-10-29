using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    HubConnection? desktopHub;
    MessengerApi? desktopOwner;
    readonly Label desktopStatus=Text("로그인 필요",12);
    bool desktopAway;
    async Task StartDesktopPresence()
    {
        var source=api;if(source?.Office!=true)return;
        if(desktopOwner==source&&desktopHub is not null)return;
        StopDesktopPresence();desktopOwner=source;
        var hub=new HubConnectionBuilder().WithUrl(new Uri(source.Address,"hub"),options=>
        {options.Cookies=source.SessionCookies;options.HttpMessageHandlerFactory=handler=>{if(handler is HttpClientHandler client)client.AllowAutoRedirect=false;return handler;};}).WithAutomaticReconnect().Build();
        desktopHub=hub;
        hub.On<JsonElement>("NewMessage",_=>MainThread.BeginInvokeOnMainThread(()=>{if(desktopOwner==source)desktopStatus.Text="새 메시지가 도착했습니다";}));
        hub.Reconnecting+=_=>{MainThread.BeginInvokeOnMainThread(()=>desktopStatus.Text="다시 연결 중");return Task.CompletedTask;};
        hub.Reconnected+=_=>{MainThread.BeginInvokeOnMainThread(()=>desktopStatus.Text="온라인");return Task.CompletedTask;};
        hub.Closed+=_=>{MainThread.BeginInvokeOnMainThread(()=>{if(desktopHub==hub){desktopHub=null;desktopOwner=null;desktopStatus.Text="연결 끊김 · 받은함 새로고침";}});return Task.CompletedTask;};
        try{await hub.StartAsync();if(desktopOwner==source)desktopStatus.Text=source.StorageWarning??"온라인";}
        catch(Exception error)when(error is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {if(desktopHub==hub){desktopHub=null;desktopOwner=null;desktopStatus.Text="실시간 연결 실패 · 받은함으로 재시도";}await hub.DisposeAsync();}
    }
    void StopDesktopPresence()
    {
        var hub=desktopHub;desktopHub=null;desktopOwner=null;desktopAway=false;desktopStatus.Text="로그인 필요";
        if(hub is not null)_=DisposeDesktopHub(hub);
    }
    static async Task DisposeDesktopHub(HubConnection hub){try{await hub.DisposeAsync();}catch(Exception error)when(error is HttpRequestException or OperationCanceledException or InvalidOperationException){}}
    static async Task<JsonElement> MacSend(MessengerApi source,string path,JsonObject payload)
    {using var content=new StringContent(payload.ToJsonString(),Encoding.UTF8,"application/json");return await source.Send(path,content:content);}
    async Task MacPeople()
    {
        var source=api!;var version=epoch;var people=await source.Get("api/users");if(!Current(source,version))return;
        var page=DesktopPage("조직도");var stack=new VerticalStackLayout{Padding=20,Spacing=10};((Grid)page.Content).Add(new ScrollView{Content=stack},0,1);
        var search=new Entry{ClassId="school-field",Placeholder="이름·부서 검색",FontSize=16,TextColor=Navy,PlaceholderColor=Muted};stack.Add(DesktopPanel(search,8));var rows=new VerticalStackLayout{Spacing=4};stack.Add(rows);
        void Show()
        {
            rows.Clear();foreach(var group in people.EnumerateArray().Where(p=>(p.GetProperty("name").GetString()+p.GetProperty("department").GetString()).Contains(search.Text??"",StringComparison.OrdinalIgnoreCase)).GroupBy(p=>p.GetProperty("department").GetString()??"교직원").OrderBy(g=>g.Key))
            {
                rows.Add(DesktopPanel(Text(group.Key+" · "+group.Count()+"명",12,true),10,Color.FromArgb("#F3F6FB")));
                foreach(var person in group.OrderBy(p=>p.GetProperty("name").GetString()))
                {
                    var name=person.GetProperty("name").GetString()!;var id=person.GetProperty("id").GetString()!;
                    var online=person.GetProperty("online").GetBoolean();var away=person.GetProperty("away").GetBoolean();var phone=person.GetProperty("mobile").GetBoolean();
                    var status=online?(away?"자리비움":"온라인"):phone?"휴대폰":"오프라인";var color=online?(away?Color.FromArgb("#A87812"):Color.FromArgb("#218568")):phone?Blue:Muted;
                    var line=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8,Padding=new Thickness(12,0)};
                    var dot=Text(online||!phone?"●":"▣",11);dot.TextColor=color;dot.VerticalOptions=LayoutOptions.Center;line.Add(dot);
                    var position=person.TryGetProperty("position",out var role)?role.GetString():null;
                    var button=DesktopButton(name+(string.IsNullOrWhiteSpace(position)?"":" · "+position),()=>MacCompose(id));button.ClassId="school-story";button.BorderWidth=0;button.BackgroundColor=Colors.Transparent;button.HorizontalOptions=LayoutOptions.Fill;line.Add(button,1);
                    var state=Text(status,11);state.TextColor=color;state.VerticalOptions=LayoutOptions.Center;line.Add(state,2);rows.Add(line);
                }
            }
        }
        search.TextChanged+=(_,_)=>Show();Show();await Navigation.PushAsync(page);
    }
    async Task MacCompose(string? recipient=null)
    {
        var source=api!;var version=epoch;var people=await source.Get("api/users");if(!Current(source,version))return;
        var page=DesktopPage("새 메시지");composer=page;var grid=(Grid)page.Content;
        var fields=new Grid{Padding=new Thickness(20,10,20,14),RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)}};grid.Add(fields,0,1);
        var recipients=new HashSet<string>();
        var directory=people.EnumerateArray().ToDictionary(p=>p.GetProperty("id").GetString()!,p=>p.GetProperty("name").GetString()+" · "+p.GetProperty("department").GetString());
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
        var files=new List<string>();var names=new VerticalStackLayout{Spacing=4};var fileHint=Text("선택한 파일이 여기에 표시됩니다 · 30일 보관",11);fileHint.TextColor=Muted;names.Add(fileHint);
        var fileZone=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=12};names.VerticalOptions=LayoutOptions.Center;fileZone.Add(names,1);
        var error=Text("",12);error.TextColor=Color.FromArgb("#A53A38");fields.Add(error,0,5);
        var client=Guid.NewGuid().ToString("N");JsonObject? pending=null;
        var send=DesktopButton("보내기",null,true);send.AutomationId="mac-send";send.MinimumWidthRequest=112;send.HorizontalOptions=LayoutOptions.End;
        var attach=DesktopButton("파일 첨부");
        attach.Clicked+=async(_,_)=>
        {
            attach.IsEnabled=send.IsEnabled=false;
            try
            {
                preserveComposerOnResume=true;var file=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="메시지 첨부파일 선택"});if(file is null)return;
                var upload=await source.Upload(file,false);if(!Current(source,version))return;files.Add(upload.GetProperty("id").GetString()!);fileHint.IsVisible=false;var name=Text(file.FileName,12);name.MaxLines=1;name.LineBreakMode=LineBreakMode.TailTruncation;names.Add(name);
            }
            catch(Exception e){error.Text=e.Message;}
            finally{send.IsEnabled=true;attach.IsEnabled=pending is null;}
        };fileZone.Add(attach);fields.Add(DesktopPanel(fileZone,10,Color.FromArgb("#F3F6FB")),0,4);
        var footer=new Grid{Padding=new Thickness(20,10),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};var caption=Text("교직원에게 보내는 업무 메시지",11);caption.TextColor=Muted;caption.VerticalOptions=LayoutOptions.Center;footer.Add(caption);footer.Add(send,1);grid.Add(footer,0,2);
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
                    pending=new JsonObject{["clientId"]=client,["title"]=title.Text??"",["body"]=text.Text,["recipientIds"]=new JsonArray(ids.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["attachmentIds"]=new JsonArray(files.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["all"]=false};
                    title.IsEnabled=text.IsEnabled=recipientRows.IsEnabled=self.IsEnabled=add.IsEnabled=false;
                }
                await MacSend(source,"api/messages",pending);if(api==source){await Navigation.PopAsync();homeTab="notices";view="sent";offset=0;await Home();}
            }
            catch(Exception e){error.Text=e.Message;send.Text=pending is null?"보내기":"같은 메시지 다시 전송";}
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
        grid.Add(reply,0,1);page.Content=grid;page.AutomationId="mac-message-reader";
    }
}
