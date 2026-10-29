using System.Text.Json;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.AspNetCore.SignalR.Client;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    readonly ContentView desktopContent=new();
    View desktopShared=null!;
    ContentView? desktopReader;
    readonly Dictionary<string,Button> desktopMenu=[];
    static Border DesktopPanel(View content,double padding=12,Color? fill=null)=>new()
    {Content=content,Padding=padding,BackgroundColor=fill??Colors.White,Stroke=Line,StrokeThickness=1,StrokeShape=new RoundRectangle{CornerRadius=10}};
    Button DesktopButton(string label,Func<Task>? action=null,bool primary=false)
    {
        var button=new Button{Text=label,FontSize=16,FontAttributes=FontAttributes.None,Padding=new Thickness(14,8),MinimumHeightRequest=42,CornerRadius=10,BorderWidth=1,BorderColor=primary?Blue:Line,BackgroundColor=primary?Blue:Colors.White,TextColor=primary?Colors.White:Navy,LineBreakMode=LineBreakMode.TailTruncation};
        if(action is not null)button.Clicked+=async(_,_)=>await Run(async()=>{try{await action();}catch(Exception error)when(Navigation.NavigationStack.LastOrDefault() is ContentPage page&&page!=this){await page.DisplayAlertAsync(page.Title??"교무톡",error.Message,"확인");}});return button;
    }
    ContentPage DesktopPage(string title)
    {
        var page=new ContentPage{Title=title,BackgroundColor=Colors.White};NavigationPage.SetHasNavigationBar(page,false);
        var root=new Grid{RowDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)}};
        var header=new Grid{Padding=new Thickness(20,10),ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=10,BackgroundColor=Color.FromArgb("#F8FAFD")};
        header.Add(new Image{Source="brand_mark.png",HeightRequest=26,WidthRequest=26});var name=Text("교무톡 · "+title,13,true);name.VerticalOptions=LayoutOptions.Center;header.Add(name,1);
        header.Add(DesktopButton("닫기",async()=>await Navigation.PopAsync()),2);root.Add(header);page.Content=root;return page;
    }
    partial void ConfigureDesktop()
    {
        desktopShared=Content;Content=null;desktopContent.Content=desktopShared;
        body.MaximumWidthRequest=620;body.HorizontalOptions=LayoutOptions.Center;
        var rail=new Grid{Padding=new Thickness(6,12,6,10),RowSpacing=3,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Color.FromArgb("#FAFCFF")};
        var brand=new Image{Source="brand_mark.png",WidthRequest=34,HeightRequest=34,Margin=new Thickness(0,2,0,10)};rail.Add(brand);
        var links=new Grid{RowSpacing=3};var row=0;
        void Section(string text,Color tint)
        {links.RowDefinitions.Add(new(GridLength.Auto));var label=Text(text,10,true);label.TextColor=Muted;label.HorizontalTextAlignment=TextAlignment.Center;links.Add(DesktopPanel(label,5,tint),0,row++);}
        void Link(string key,string label,string icon,Func<Task> action)
        {
            var button=DesktopButton(label,async()=>
            {
                if(api?.Session.ValueKind!=JsonValueKind.Object||api.Session.GetProperty("user").ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("학교 계정으로 먼저 로그인하세요.");
                var user=api.Session.GetProperty("user");if(user.TryGetProperty("activationRequired",out var activation)&&activation.GetBoolean()||user.TryGetProperty("passwordChangeRequired",out var reset)&&reset.GetBoolean())throw new InvalidOperationException("계정 활성화와 비밀번호 변경을 먼저 완료하세요.");
                foreach(var pair in desktopMenu){pair.Value.BackgroundColor=pair.Key==key?Pale:Colors.Transparent;pair.Value.TextColor=pair.Key==key?Blue:Muted;}
                await action();
            });
            button.AutomationId="mac-nav-"+key;button.ImageSource=icon;button.ContentLayout=new(Button.ButtonContentLayout.ImagePosition.Top,2);button.FontSize=12;button.BorderWidth=0;button.Padding=new Thickness(2,4);button.BackgroundColor=Colors.Transparent;button.TextColor=Muted;button.HeightRequest=56;button.MinimumHeightRequest=0;
            desktopMenu[key]=button;links.RowDefinitions.Add(new(GridLength.Star));links.Add(button,0,row++);
        }
        Section("메시지",Pale);
        Link("received","받은 메시지","nav_notices.png",async()=>{homeTab="notices";view="received";offset=0;await Home();});
        Link("sent","보낸 메시지","nav_notices.png",async()=>{homeTab="notices";view="sent";offset=0;await Home();});
        Link("compose","새 메시지","nav_notices.png",()=>MacCompose());Link("people","조직도","nav_profile.png",MacPeople);
        Section("학교 업무",Color.FromArgb("#E3F1EB"));
        Link("notices","가정통신문","nav_notices.png",()=>Compose(true));
        Link("timetable","시간표","action_calendar.png",async()=>{await Timetable();});Link("calendar","학교 일정","action_calendar.png",()=>OfficeSchedule(false));
        Link("rooms","특별실 예약","action_room.png",()=>OfficeSchedule(true));Link("counseling","상담 예약","action_calendar.png",Counseling);
        Link("account","내 정보","nav_profile.png",async()=>{homeTab="account";await Home();});rail.Add(links,0,2);
        var away=DesktopButton("자리비움",async()=>{if(desktopHub?.State!=HubConnectionState.Connected)throw new InvalidOperationException("연결을 확인하세요.");desktopAway=await desktopHub.InvokeAsync<bool>("SetAway",!desktopAway);desktopStatus.Text=desktopAway?"자리비움":"온라인";});away.FontSize=12;away.Padding=4;rail.Add(away,0,3);
        var grid=new Grid{ColumnDefinitions={new(new GridLength(96)),new(GridLength.Star)},RowDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=1,BackgroundColor=Line};grid.Add(rail);Grid.SetRowSpan(rail,2);grid.Add(desktopContent,1);feedback.Margin=new Thickness(16,4);grid.Add(feedback,1,1);Content=grid;
        // Keep the desktop navigation in view at short window heights as well.
        rail.SizeChanged+=(_,_)=>{var compact=rail.Height>0&&rail.Height<720;foreach(var item in desktopMenu.Values){item.ImageSource=compact?null:item.AutomationId is "mac-nav-rooms"?"action_room.png":item.AutomationId is "mac-nav-timetable" or "mac-nav-calendar" or "mac-nav-counseling"?"action_calendar.png":item.AutomationId is "mac-nav-people" or "mac-nav-account"?"nav_profile.png":"nav_notices.png";item.HeightRequest=compact?40:56;}};
    }
    async Task DesktopMailbox(string query="")
    {
        var source=api!;var version=epoch;var rows=await source.Get("api/messages?box="+view+"&offset="+offset+"&q="+Uri.EscapeDataString(query));if(!Current(source,version))return;
        var root=new Grid{RowDefinitions={new(GridLength.Auto),new(GridLength.Star)},BackgroundColor=Colors.White};
        var toolbar=new Grid{Padding=new Thickness(22,14),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=12,BackgroundColor=Color.FromArgb("#F8FAFD")};
        var heading=new VerticalStackLayout{Spacing=3};heading.Add(Text(view=="sent"?"보낸 메시지":view=="unread"?"미확인 메시지":"받은 메시지",19,true));var meta=Text(schoolName+" · "+source.Session.GetProperty("user").GetProperty("name").GetString(),11);meta.TextColor=Muted;heading.Add(meta);toolbar.Add(heading);
        desktopStatus.MaxLines=1;desktopStatus.LineBreakMode=LineBreakMode.TailTruncation;desktopStatus.MaximumWidthRequest=240;desktopStatus.TextColor=Muted;desktopStatus.VerticalOptions=LayoutOptions.Center;
        if(desktopStatus.Parent is Grid oldToolbar)oldToolbar.Remove(desktopStatus);toolbar.Add(desktopStatus,1);toolbar.Add(DesktopButton("새 메시지",()=>MacCompose(),true),2);root.Add(toolbar);
        var panes=new Grid{ColumnDefinitions={new(new GridLength(296)),new(GridLength.Star)},ColumnSpacing=1,BackgroundColor=Line};root.Add(panes,0,1);
        var list=new Grid{BackgroundColor=Color.FromArgb("#F5F8FC"),Padding=12,RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)}};
        var search=new Entry{ClassId="school-field",Placeholder="제목·본문·보낸 사람 검색",Text=query,FontSize=15,MaxLength=200,TextColor=Navy,PlaceholderColor=Muted,ReturnType=ReturnType.Search};search.Completed+=async(_,_)=>await Run(()=>DesktopMailbox(search.Text??""));list.Add(DesktopPanel(search,7));
        var filters=new HorizontalStackLayout{Spacing=6};var received=DesktopButton("받은",async()=>{view="received";offset=0;await DesktopMailbox(query);});received.AutomationId="mac-filter-received";var unreadButton=DesktopButton("미확인",async()=>{view="unread";offset=0;await DesktopMailbox(query);});unreadButton.AutomationId="mac-filter-unread";foreach(var filter in new[]{received,unreadButton}){filter.FontSize=13;filter.MinimumHeightRequest=34;filter.Padding=new Thickness(10,5);filters.Add(filter);}list.Add(filters,0,1);
        var messages=new VerticalStackLayout{Spacing=6};var choices=new List<(Border Row,string Id)>();desktopReader=new ContentView{BackgroundColor=Colors.White};
        var empty=new VerticalStackLayout{Spacing=10,VerticalOptions=LayoutOptions.Center,HorizontalOptions=LayoutOptions.Center};empty.Add(new Image{Source="nav_notices.png",HeightRequest=32,WidthRequest=32});var hint=Text("왼쪽에서 메시지를 선택하세요",13);hint.TextColor=Muted;empty.Add(hint);desktopReader.Content=empty;panes.Add(desktopReader,1);
        async Task OpenMessage(string id,Border selected,long createdAt)
        {
            foreach(var choice in choices)choice.Row.BackgroundColor=choice.Id==id?Pale:Colors.Transparent;
            await OfficeDetail(id);if(!Current(source,version))return;
            var content=(VerticalStackLayout)selected.Content;((Button)content.Children[0]).FontAttributes=FontAttributes.None;((Label)content.Children[content.Children.Count-1]).Text=Date(createdAt);
        }
        foreach(var message in rows.EnumerateArray())
        {
            var id=message.GetProperty("id").GetString()!;var label=message.GetProperty("title").GetString()??"";var card=new VerticalStackLayout{Spacing=4};var border=DesktopPanel(card,12,Colors.Transparent);border.StrokeThickness=0;
            var createdAt=message.GetProperty("createdAt").GetInt64();
            var subject=DesktopButton(label,()=>OpenMessage(id,border,createdAt));subject.ClassId="school-story";subject.AutomationId="mac-message-"+id;subject.BackgroundColor=Colors.Transparent;subject.BorderWidth=0;subject.Padding=0;subject.FontSize=16;subject.FontAttributes=message.GetProperty("readAt").ValueKind==JsonValueKind.Null&&view!="sent"?FontAttributes.Bold:FontAttributes.None;subject.MinimumHeightRequest=28;subject.HorizontalOptions=LayoutOptions.Fill;subject.ContentLayout=new(Button.ButtonContentLayout.ImagePosition.Left,0);card.Add(subject);
            var author=Text(message.GetProperty("senderName").GetString()??"",11);author.TextColor=Muted;card.Add(author);var stamp=Text(Date(message.GetProperty("createdAt").GetInt64())+(message.GetProperty("readAt").ValueKind==JsonValueKind.Null&&view!="sent"?" · 미확인":""),10);stamp.TextColor=Muted;card.Add(stamp);messages.Add(border);choices.Add((border,id));
        }
        if(rows.GetArrayLength()==0){var noMessages=Text("표시할 메시지가 없습니다",12);noMessages.TextColor=Muted;messages.Add(noMessages);}
        list.Add(new ScrollView{Content=messages},0,2);
        var more=DesktopButton(rows.GetArrayLength()==100?"다음 메시지":"새로고침",async()=>{if(rows.GetArrayLength()==100)offset+=100;await DesktopMailbox(query);});more.FontSize=13;more.MinimumHeightRequest=36;list.Add(more,0,3);panes.Add(list);desktopContent.Content=root;
        foreach(var pair in desktopMenu){pair.Value.BackgroundColor=pair.Key==view?Pale:Colors.Transparent;pair.Value.TextColor=pair.Key==view?Blue:Muted;}
        if(rows.GetArrayLength()>0)await OpenMessage(choices[0].Id,choices[0].Row,rows[0].GetProperty("createdAt").GetInt64());
    }
    partial void DisplayDesktopDetail(ContentPage page,ref bool handled)
    {
        if(desktopReader is null||homeTab=="account"||Navigation.NavigationStack.Count!=1)return;
        var content=page.Content;page.Content=null;desktopReader.Content=content;handled=true;
    }
    partial void ResetDesktopView()
    {desktopReader=null;if(desktopShared is not null)desktopContent.Content=desktopShared;}
}
