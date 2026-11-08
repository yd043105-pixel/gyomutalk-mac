using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.AspNetCore.SignalR.Client;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    readonly ContentView desktopContent=new();
    View desktopShared=null!;
    ContentView? desktopReader;
    ContentView? desktopIdentity;
    readonly Dictionary<string,Button> desktopMenu=[];
    readonly Dictionary<string,string> desktopMenuIcons=[];
    string? desktopRailSelected;
    bool desktopRailCompact;
    static Border DesktopPanel(View content,double padding=12,Color? fill=null)=>new()
    {Content=content,Padding=padding,BackgroundColor=fill??Surface,Stroke=Line,StrokeThickness=1,StrokeShape=new RoundRectangle{CornerRadius=Radius}};
    Border DesktopEmptyState(string title,string description)
    {
        var words=new VerticalStackLayout{Spacing=6};words.Add(EmptyArt(EmptyIcon(title)));
        var heading=Text(title,15,true);heading.HorizontalTextAlignment=TextAlignment.Center;words.Add(heading);
        var hint=Text(description,12);hint.TextColor=Muted;hint.HorizontalTextAlignment=TextAlignment.Center;words.Add(hint);
        var card=DesktopPanel(words,24,Subtle);
        card.MaximumWidthRequest=520;card.HorizontalOptions=LayoutOptions.Fill;
        return card;
    }
    Button DesktopButton(string label,Func<Task>? action=null,bool primary=false)
    {
        var button=new Button{Text=label,FontSize=16,FontAttributes=primary?FontAttributes.Bold:FontAttributes.None,Padding=new Thickness(16,8),MinimumHeightRequest=42,CornerRadius=(int)Radius,BorderWidth=1,BorderColor=primary?Blue:Line,BackgroundColor=primary?Blue:Surface,TextColor=primary?Colors.White:Navy,LineBreakMode=LineBreakMode.TailTruncation};
        if(action is not null)button.Clicked+=async(_,_)=>await Run(async()=>{try{await action();}catch(Exception error)when(Navigation.NavigationStack.LastOrDefault() is ContentPage page&&page!=this){await page.DisplayAlertAsync(page.Title??"교무톡",error.Message,"확인");}});return button;
    }
    // Borderless text button in the accent colour (Windows SubtleButton).
    static void SubtleButton(Button button){button.BackgroundColor=Colors.Transparent;button.BorderWidth=0;button.TextColor=Blue;button.FontSize=14;button.Padding=new Thickness(10,4);button.MinimumHeightRequest=34;}
    ContentPage DesktopPage(string title)
    {
        var page=new ContentPage{Title=title,BackgroundColor=Surface};NavigationPage.SetHasNavigationBar(page,false);
        var root=new Grid{RowDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Surface};
        var header=new Grid{Padding=new Thickness(24,12),ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=12,BackgroundColor=Surface};
        header.Add(new Image{Source="brand_mark.png",HeightRequest=28,WidthRequest=28});
        var name=new FormattedString();name.Spans.Add(new Span{Text="교무톡 · ",TextColor=Muted});name.Spans.Add(new Span{Text=title,TextColor=Navy,FontAttributes=FontAttributes.Bold});
        var caption=Text("",14);caption.FormattedText=name;caption.VerticalOptions=LayoutOptions.Center;caption.MaxLines=1;header.Add(caption,1);
        var close=DesktopButton("닫기",async()=>await Navigation.PopAsync());close.FontSize=14;close.MinimumHeightRequest=36;close.Padding=new Thickness(14,4);header.Add(close,2);
        root.Add(header);var rule=Rule();rule.VerticalOptions=LayoutOptions.End;root.Add(rule);page.Content=root;return page;
    }
    partial void ConfigureDesktop()
    {
        desktopShared=Content;Content=null;desktopContent.Content=desktopShared;desktopContent.BackgroundColor=Surface;
        BackgroundColor=Canvas;ApplyDesktopStyles();
        body.MaximumWidthRequest=620;body.HorizontalOptions=LayoutOptions.Center;
        // Windows: a 72px rail on the soft canvas, icon over a short label, the current item as an accent pill.
        var rail=new Grid{Padding=new Thickness(0,14,0,12),RowDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Canvas};
        rail.Add(new Image{Source="brand_mark.png",WidthRequest=36,HeightRequest=36,Margin=new Thickness(0,0,0,14),HorizontalOptions=LayoutOptions.Center});
        Button Link(string key,string label,string tip,string icon,Func<Task> action)
        {
            var button=DesktopButton(label,async()=>
            {
                if(api?.Session.ValueKind!=JsonValueKind.Object||api.Session.GetProperty("user").ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("학교 계정으로 먼저 로그인하세요.");
                var user=api.Session.GetProperty("user");if(user.TryGetProperty("activationRequired",out var activation)&&activation.GetBoolean()||user.TryGetProperty("passwordChangeRequired",out var reset)&&reset.GetBoolean())throw new InvalidOperationException("계정 활성화와 비밀번호 변경을 먼저 완료하세요.");
                SelectRail(key);
                await action();
            });
            button.AutomationId="mac-nav-"+key;desktopMenuIcons[key]=icon;button.ImageSource=icon+".png";button.ContentLayout=new(Button.ButtonContentLayout.ImagePosition.Top,4);
            button.FontSize=12;button.BorderWidth=0;button.CornerRadius=(int)Radius;button.Padding=new Thickness(0,6);button.Margin=new Thickness(10,1);button.HeightRequest=62;button.MinimumHeightRequest=0;
            button.BackgroundColor=Colors.Transparent;button.TextColor=Muted;
            ToolTipProperties.SetText(button,tip);SemanticProperties.SetDescription(button,tip);
            desktopMenu[key]=button;return button;
        }
        var links=new VerticalStackLayout{Spacing=2};
        links.Add(Link("received","받은함","받은 메시지","rail_mail",async()=>{homeTab="notices";view="received";offset=0;await Home();}));
        links.Add(Link("sent","보낸함","보낸 메시지","rail_send",async()=>{homeTab="notices";view="sent";offset=0;await Home();}));
        links.Add(Link("compose","새 메시지","새 메시지 쓰기","rail_add",()=>MacCompose()));
        links.Add(Link("people","조직도","조직도 · 교직원","rail_tree",MacPeople));
        links.Add(Link("work","할 일","할 일 · 파일 제출","rail_file",()=>MacWork()));
        links.Add(Link("timetable","시간표","시간표","rail_timetable",async()=>{await Timetable();}));SetDesktopTimetable(false);
        links.Add(Link("calendar","일정","학교 일정","rail_calendar",()=>OfficeSchedule(false)));
        links.Add(Link("rooms","특별실","특별실 예약","rail_room",()=>OfficeSchedule(true)));
        rail.Add(links,0,1);
        var away=DesktopButton("자리비움",async()=>{if(desktopHub?.State!=HubConnectionState.Connected)throw new InvalidOperationException("연결을 확인하세요.");desktopAway=await desktopHub.InvokeAsync<bool>("SetAway",!desktopAway);desktopStatus.Text=desktopAway?"자리비움":"온라인";});
        away.AutomationId="mac-away";away.FontSize=12;away.BorderWidth=0;away.BackgroundColor=Colors.Transparent;away.TextColor=Muted;away.Padding=new Thickness(2,4);away.Margin=new Thickness(10,0);away.MinimumHeightRequest=0;away.HeightRequest=34;
        var bottom=new VerticalStackLayout{Spacing=2};bottom.Add(away);
        bottom.Add(Link("account","내 정보","내 정보 · 설정","rail_user",async()=>{homeTab="account";await Home();}));rail.Add(bottom,0,2);
        // The header avatar and the away toggle follow every connection or presence change.
        desktopStatus.PropertyChanged+=(_,e)=>{if(e.PropertyName!=nameof(Label.Text))return;away.Text=desktopAway?"자리 복귀":"자리비움";ShowDesktopIdentity();};
        var grid=new Grid{ColumnDefinitions={new(new GridLength(RailWidth)),new(GridLength.Star)},RowDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=1,BackgroundColor=Line};grid.Add(rail);Grid.SetRowSpan(rail,2);grid.Add(desktopContent,1);
        feedback.Margin=0;feedback.Padding=new Thickness(24,8);feedback.BackgroundColor=Surface;grid.Add(feedback,1,1);Content=grid;
        // Keep the desktop navigation in view at short window heights as well.
        rail.SizeChanged+=(_,_)=>{var compact=rail.Height>0&&rail.Height<720;if(compact==desktopRailCompact)return;desktopRailCompact=compact;foreach(var item in desktopMenu.Values)item.HeightRequest=compact?38:62;SelectRail(desktopRailSelected);};
    }
    void SelectRail(string? key)
    {
        desktopRailSelected=key;
        foreach(var (name,button) in desktopMenu)
        {
            var on=name==key;button.BackgroundColor=on?Pale:Colors.Transparent;button.TextColor=on?Blue:Muted;button.FontAttributes=on?FontAttributes.Bold:FontAttributes.None;
            button.ImageSource=desktopRailCompact?null:desktopMenuIcons[name]+(on?"_on":"")+".png";
        }
    }
    void ShowDesktopIdentity()
    {
        if(desktopIdentity is null||api?.Session.ValueKind!=JsonValueKind.Object||api.Session.GetProperty("user") is not{ValueKind:JsonValueKind.Object} user)return;
        var online=desktopHub?.State==HubConnectionState.Connected;var name=user.GetProperty("name").GetString()??"";
        desktopIdentity.Content=AvatarWithPresence(name,44,user.GetProperty("id").GetString(),online,desktopAway,false,name+" · "+(online?desktopAway?"자리비움":"온라인":"오프라인"));
    }
    // Shared Styles.xaml keeps the mobile palette; the Mac app layers the "A · 맑은 청" defaults over it.
    static void ApplyDesktopStyles()
    {
        if(Application.Current is not{} app||app.Resources.ContainsKey("mac-a-theme"))return;
        app.Resources["mac-a-theme"]=true;
        void Restyle(Type type,params (BindableProperty Property,object Value)[] values)
        {
            var style=new Style(type){ApplyToDerivedTypes=type==typeof(Page)};
            if(app.Resources.TryGetValue(type.FullName!,out var old)&&old is Style based&&based.TargetType==type)style.BasedOn=based;
            foreach(var (property,value) in values)style.Setters.Add(new Setter{Property=property,Value=value});
            app.Resources.Add(style);
        }
        Restyle(typeof(Label),(Label.TextColorProperty,Navy));
        Restyle(typeof(Button),(Button.TextColorProperty,Navy),(Button.CornerRadiusProperty,(int)Radius));
        Restyle(typeof(Entry),(Entry.TextColorProperty,Navy),(Entry.PlaceholderColorProperty,Faint));
        Restyle(typeof(Editor),(Editor.TextColorProperty,Navy),(Editor.PlaceholderColorProperty,Faint));
        Restyle(typeof(Picker),(Picker.TextColorProperty,Navy),(Picker.TitleColorProperty,Faint));
        Restyle(typeof(DatePicker),(DatePicker.TextColorProperty,Navy));
        Restyle(typeof(CheckBox),(CheckBox.ColorProperty,Blue));
        Restyle(typeof(Switch),(Switch.OnColorProperty,Pale),(Switch.ThumbColorProperty,Blue));
        Restyle(typeof(ActivityIndicator),(ActivityIndicator.ColorProperty,Blue));
        Restyle(typeof(Page),(VisualElement.BackgroundColorProperty,Canvas));
    }
    void SetDesktopTimetable(bool enabled)
    {
        if(desktopMenu.TryGetValue("timetable",out var button))button.IsVisible=enabled;
    }
    async Task DesktopMailbox(string query="")
    {
        var source=api!;var version=epoch;var rows=await source.Get("api/messages?box="+view+"&offset="+offset+"&q="+Uri.EscapeDataString(query));if(!Current(source,version))return;
        _=SynchronizeDesktopArchive(source);
        var found=rows.EnumerateArray().ToList();if(offset==0&&query.Trim().Length>0)found.AddRange(LocalMatches(source,query.Trim()).Where(m=>found.All(r=>r.GetProperty("id").GetString()!=m.GetProperty("id").GetString())));
        var root=new Grid{RowDefinitions={new(GridLength.Auto),new(GridLength.Star)},BackgroundColor=Surface};
        var search=new Entry{ClassId="school-field",Placeholder="제목 · 본문 · 보낸 사람",Text=query,FontSize=15,MaxLength=200,ReturnType=ReturnType.Search};search.Completed+=async(_,_)=>await Run(()=>DesktopMailbox(search.Text??""));
        // Windows MailboxWindow: title and the user's presence, then underlined box tabs over a hairline.
        var header=new VerticalStackLayout{Padding=new Thickness(28,18,24,0),Spacing=4,BackgroundColor=Surface};
        var toolbar=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=14};
        desktopIdentity=new ContentView{VerticalOptions=LayoutOptions.Center};toolbar.Add(desktopIdentity);ShowDesktopIdentity();
        var heading=new VerticalStackLayout{Spacing=2,VerticalOptions=LayoutOptions.Center};heading.Add(Text("메시지함",22,true));var meta=Text(schoolName+" · "+source.Session.GetProperty("user").GetProperty("name").GetString(),11);meta.TextColor=Muted;meta.MaxLines=1;meta.LineBreakMode=LineBreakMode.TailTruncation;heading.Add(meta);toolbar.Add(heading,1);
        desktopStatus.MaxLines=1;desktopStatus.LineBreakMode=LineBreakMode.TailTruncation;desktopStatus.MaximumWidthRequest=260;desktopStatus.TextColor=Faint;desktopStatus.FontSize=14;desktopStatus.VerticalOptions=LayoutOptions.Center;
        if(desktopStatus.Parent is Grid oldToolbar)oldToolbar.Remove(desktopStatus);toolbar.Add(desktopStatus,2);
        var compose=DesktopButton("새 메시지",()=>MacCompose(),true);compose.VerticalOptions=LayoutOptions.Center;toolbar.Add(compose,3);header.Add(toolbar);
        var tabs=new HorizontalStackLayout{Spacing=6,Margin=new Thickness(-6,8,0,0)};
        foreach(var (key,label) in new[]{("received","받은 메시지"),("unread","미확인"),("sent","보낸 메시지")})
        {
            var selected=view==key;
            var tab=DesktopButton(label,async()=>{view=key;offset=0;await DesktopMailbox(search.Text??"");});
            tab.AutomationId="mac-filter-"+key;tab.BorderWidth=0;tab.BackgroundColor=Colors.Transparent;tab.FontSize=15;tab.Padding=new Thickness(8,6);tab.MinimumHeightRequest=0;
            tab.TextColor=selected?Blue:Muted;tab.FontAttributes=selected?FontAttributes.Bold:FontAttributes.None;
            var cell=new VerticalStackLayout();cell.Add(tab);cell.Add(new BoxView{HeightRequest=2,Color=selected?Blue:Colors.Transparent,Margin=new Thickness(6,0)});tabs.Add(cell);
        }
        header.Add(tabs);root.Add(header);var headerRule=Rule();headerRule.VerticalOptions=LayoutOptions.End;root.Add(headerRule);
        var panes=new Grid{ColumnDefinitions={new(new GridLength(320)),new(GridLength.Star)},ColumnSpacing=1,BackgroundColor=Line};root.Add(panes,0,1);
        panes.SizeChanged+=(_,_)=>{if(panes.ColumnDefinitions.Count==0)return;var width=panes.Width<820?270:320;if(panes.ColumnDefinitions[0].Width.Value!=width)panes.ColumnDefinitions[0].Width=new GridLength(width);};
        var list=new Grid{BackgroundColor=Surface,Padding=new Thickness(14,14,14,8),RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)}};
        list.Add(SearchField(search));
        var messages=new VerticalStackLayout{Spacing=2};var choices=new List<(Border Row,string Id)>();desktopReader=new ContentView{BackgroundColor=Surface};
        var empty=DesktopEmptyState("메시지를 선택하세요","본문과 첨부파일을 확인할 수 있습니다.");
        empty.Margin=24;empty.VerticalOptions=LayoutOptions.Center;desktopReader.Content=empty;panes.Add(desktopReader,1);
        async Task OpenMessage(string id,Button subject,Ellipse fresh)
        {
            foreach(var choice in choices)choice.Row.BackgroundColor=choice.Id==id?Pale:Colors.Transparent;
            await OfficeDetail(id);if(!Current(source,version))return;
            subject.FontAttributes=FontAttributes.None;fresh.IsVisible=false;
        }
        var opens=new List<Func<Task>>();
        foreach(var message in found)
        {
            var id=message.GetProperty("id").GetString()!;var label=DesktopTitle(source,message);
            var sender=message.GetProperty("senderName").GetString()??"";var unreadRow=message.GetProperty("readAt").ValueKind==JsonValueKind.Null&&view!="sent";
            // Windows row: initial avatar, sender and date, an unread dot before the title, then one line of preview.
            var line=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=12};
            var avatar=Avatar(sender,44,Field(message,"senderId")??sender);avatar.VerticalOptions=LayoutOptions.Start;avatar.Margin=new Thickness(0,2,0,0);line.Add(avatar);
            var stack=new VerticalStackLayout{Spacing=2};
            var metaRow=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8};
            var from=Text(sender,12);from.TextColor=Muted;from.MaxLines=1;from.LineBreakMode=LineBreakMode.TailTruncation;metaRow.Add(from);
            var when=Text(Date(message.GetProperty("createdAt").GetInt64()),11);when.TextColor=Faint;when.VerticalOptions=LayoutOptions.Center;metaRow.Add(when,1);stack.Add(metaRow);
            var titleRow=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star)}};
            var fresh=new Ellipse{AutomationId="mac-unread-dot",WidthRequest=8,HeightRequest=8,Fill=new SolidColorBrush(Blue),Margin=new Thickness(0,0,7,0),VerticalOptions=LayoutOptions.Center,IsVisible=unreadRow};
            SemanticProperties.SetDescription(fresh,"미확인");ToolTipProperties.SetText(fresh,"미확인");titleRow.Add(fresh);
            Button subject=null!;
            subject=DesktopButton(label,()=>OpenMessage(id,subject,fresh));subject.ClassId="school-story";subject.AutomationId="mac-message-"+id;subject.BackgroundColor=Colors.Transparent;subject.BorderWidth=0;subject.Padding=0;subject.FontSize=16;
            subject.FontAttributes=unreadRow?FontAttributes.Bold:FontAttributes.None;subject.MinimumHeightRequest=24;subject.HorizontalOptions=LayoutOptions.Fill;subject.ContentLayout=new(Button.ButtonContentLayout.ImagePosition.Left,0);titleRow.Add(subject,1);stack.Add(titleRow);
            if(message.TryGetProperty("preview",out var preview)&&preview.GetString() is{Length:>0} excerpt&&excerpt!=label)
            {var peek=Text(excerpt.ReplaceLineEndings(" "),12);peek.TextColor=Muted;peek.MaxLines=1;peek.LineBreakMode=LineBreakMode.TailTruncation;stack.Add(peek);}
            line.Add(stack,1);
            var border=new Border{Content=line,Padding=new Thickness(10,10),BackgroundColor=Colors.Transparent,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=Radius}};
            var tap=new TapGestureRecognizer();tap.Tapped+=async(_,_)=>await Run(()=>OpenMessage(id,subject,fresh));border.GestureRecognizers.Add(tap);
            messages.Add(border);choices.Add((border,id));opens.Add(()=>OpenMessage(id,subject,fresh));
        }
        if(found.Count==0)
        {
            panes.Children.Clear();panes.ColumnDefinitions.Clear();
            panes.RowDefinitions.Add(new RowDefinition(GridLength.Auto));panes.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            list.RowDefinitions[1].Height=new GridLength(0);panes.Add(list);
            var title=!string.IsNullOrWhiteSpace(query)?"검색 결과가 없습니다":view switch{"sent"=>"보낸 메시지가 없습니다","unread"=>"미확인 메시지가 없습니다",_=>"받은 메시지가 없습니다"};
            var blank=DesktopEmptyState(title,!string.IsNullOrWhiteSpace(query)?"검색어를 바꿔 보세요.":"메시지가 생기면 여기에서 확인할 수 있습니다.");
            blank.Margin=24;blank.VerticalOptions=LayoutOptions.Center;desktopReader.Content=blank;panes.Add(desktopReader,0,1);
        }
        else
        {
            list.Add(new ScrollView{Content=messages},0,1);panes.Add(list);
        }
        var more=DesktopButton(rows.GetArrayLength()==100?"이전 메시지 더 보기":"새로고침",async()=>{if(rows.GetArrayLength()==100)offset+=100;await DesktopMailbox(search.Text??"");});SubtleButton(more);list.Add(more,0,2);desktopContent.Content=root;
        SelectRail(view=="unread"?"received":view);
        if(opens.Count>0)await opens[0]();
    }
    // The server drops a body once every PC saved it, so untitled messages and search fall back to this Mac's copies.
    string DesktopTitle(MessengerApi source,JsonElement row)
    {
        if(row.GetProperty("title").GetString() is{Length:>0} title)return title;
        if(row.TryGetProperty("preview",out var preview)&&preview.GetString() is{Length:>0} text)return text;
        try{return desktopArchive?.Owner==source&&desktopArchive.Load(row.GetProperty("id").GetString()!) is{} copy?SchoolMessenger.Shared.MessageText.Excerpt(copy.GetProperty("message").GetProperty("body").GetString()):"";}
        catch(InvalidOperationException){return "";}
    }
    IEnumerable<JsonElement> LocalMatches(MessengerApi source,string query)
    {
        if(desktopArchive?.Owner!=source)yield break;
        var me=source.Session.GetProperty("user").GetProperty("id").GetString();
        foreach(var copy in desktopArchive.Search(query))
        {
            var message=copy.GetProperty("message");var mine=copy.GetProperty("recipients").EnumerateArray().Where(r=>r.GetProperty("id").GetString()==me).Select(r=>(JsonElement?)r).FirstOrDefault();
            var fits=view switch{"sent"=>message.GetProperty("senderId").GetString()==me,"unread"=>mine?.GetProperty("readAt").ValueKind==JsonValueKind.Null,_=>mine is not null};
            if(!fits)continue;
            var row=new JsonObject{["id"]=message.GetProperty("id").GetString(),["title"]=message.GetProperty("title").GetString(),["preview"]=SchoolMessenger.Shared.MessageText.Excerpt(message.GetProperty("body").GetString()),["createdAt"]=message.GetProperty("createdAt").GetInt64(),["senderName"]=message.GetProperty("senderName").GetString(),["readAt"]=mine?.GetProperty("readAt").ValueKind==JsonValueKind.Number?mine.Value.GetProperty("readAt").GetInt64():null};
            using var document=JsonDocument.Parse(row.ToJsonString());yield return document.RootElement.Clone();
        }
    }
    partial void DisplayDesktopDetail(ContentPage page,ref bool handled)
    {
        if(desktopReader is null||homeTab=="account"||Navigation.NavigationStack.Count!=1)return;
        var content=page.Content;page.Content=null;desktopReader.Content=content;handled=true;
    }
    partial void ResetDesktopView()
    {desktopReader=null;if(desktopShared is not null)desktopContent.Content=desktopShared;}
}
