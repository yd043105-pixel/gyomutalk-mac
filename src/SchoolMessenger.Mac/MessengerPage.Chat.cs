using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Controls.Shapes;

namespace SchoolMessenger.Mobile;

// The Windows ChatWindow on the Mac: rooms on the left, the conversation and the box on the right. Messages live on the
// PCs, so this Mac keeps its own sealed copy of each room and tells the server once it has one.
public sealed partial class MessengerPage
{
    sealed record MacChatFile(string Path,string Name,long Size){public string? Uploaded{get;set;}}
    // A send that may or may not have reached the server is retried with the same request number.
    readonly Dictionary<string,JsonObject> macChatPending=[];
    static string ChatDisplay(JsonElement room,string me)
    {
        if(Field(room,"alias") is{Length:>0} alias)return alias;
        if(Flag(room,"direct"))return string.Join(", ",room.GetProperty("members").EnumerateArray().Where(p=>RowId(p)!=me).Select(p=>Field(p,"name")));
        return Field(room,"name")??"대화";
    }
    static string ChatWhen(long at)
    {
        if(at==0)return "";
        var when=DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime();var days=(DateTime.Today-when.Date).Days;
        return days==0?when.ToString("HH:mm",CultureInfo.InvariantCulture):days==1?"어제":when.ToString("M/d",CultureInfo.InvariantCulture);
    }
    // Group rooms show the first two other members as overlapping initials, as on Windows.
    static View ChatFace(JsonElement room,string me,string display)
    {
        var others=room.GetProperty("members").EnumerateArray().Where(p=>RowId(p)!=me).Select(p=>Field(p,"name")??"").ToArray();
        var grid=new Grid{WidthRequest=40,HeightRequest=40,VerticalOptions=LayoutOptions.Center,InputTransparent=true};
        if(Flag(room,"direct")||others.Length<2||Field(room,"alias") is{Length:>0}){grid.Add(Avatar(Flag(room,"direct")?others.FirstOrDefault()??display:display,38));return grid;}
        var back=Avatar(others[0],27);back.HorizontalOptions=LayoutOptions.Start;back.VerticalOptions=LayoutOptions.Start;grid.Add(back);
        var front=Avatar(others[1],29);front.HorizontalOptions=LayoutOptions.End;front.VerticalOptions=LayoutOptions.End;front.Stroke=Canvas;front.StrokeThickness=2;grid.Add(front);
        return grid;
    }
    async Task<JsonElement[]> MacPeopleList(MessengerApi source)=>(await source.Get("api/users")).EnumerateArray().ToArray();
    // One chooser for chat members, survey targets and message recipients: search, favourites first, a fixed footer.
    async Task MacPickPeople(string title,string confirm,JsonElement[] people,HashSet<string> chosen,Func<Task> applied,View? extra=null)
    {
        var page=DesktopPage(title);var grid=(Grid)page.Content;
        var body=new Grid{Padding=new Thickness(24,16,24,8),RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star)}};grid.Add(body,0,1);
        if(extra is not null)body.Add(extra);
        var search=new Entry{ClassId="school-field",Placeholder="이름 · 부서 검색",FontSize=15,AutomationId="mac-picker-search"};body.Add(SearchField(search),0,1);
        var rows=new VerticalStackLayout{Spacing=2};body.Add(new ScrollView{Content=rows},0,2);
        var count=Text("",12,true);count.TextColor=Blue;count.VerticalOptions=LayoutOptions.Center;
        var apply=DesktopButton(confirm,null,true);apply.AutomationId="mac-picker-apply";
        var footer=new Grid{Padding=new Thickness(24,12),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},BackgroundColor=Surface};footer.Add(count);footer.Add(apply,1);grid.Add(footer,0,2);grid.Add(Rule(),0,2);
        void Count()=>count.Text=chosen.Count+"명 선택";
        var favorites=MacFavorites();var entries=new List<(View Row,string Label)>();
        foreach(var person in people.OrderByDescending(p=>favorites.Contains(RowId(p))).ThenBy(p=>Field(p,"department")).ThenBy(p=>Field(p,"name")))
        {
            var id=RowId(person);var name=Field(person,"name")??"";var label=name+" · "+Field(person,"department")+(favorites.Contains(id)?" ★":"");
            var check=new CheckBox{IsChecked=chosen.Contains(id),VerticalOptions=LayoutOptions.Center};SemanticProperties.SetDescription(check,name+" 선택");
            var line=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=10,Padding=new Thickness(6,4)};
            line.Add(check);line.Add(Avatar(name,32,id),1);var words=Text(label,14);words.VerticalOptions=LayoutOptions.Center;words.MaxLines=1;words.LineBreakMode=LineBreakMode.TailTruncation;line.Add(words,2);
            var row=new Border{Content=line,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=Radius},BackgroundColor=check.IsChecked?Pale:Colors.Transparent};
            check.CheckedChanged+=(_,e)=>{if(e.Value)chosen.Add(id);else chosen.Remove(id);row.BackgroundColor=e.Value?Pale:Colors.Transparent;Count();};
            var tap=new TapGestureRecognizer();tap.Tapped+=(_,_)=>check.IsChecked=!check.IsChecked;words.GestureRecognizers.Add(tap);
            rows.Add(row);entries.Add((row,label));
        }
        if(entries.Count==0)rows.Add(DesktopEmptyState("고를 수 있는 교직원이 없습니다","관리자가 교직원을 등록하면 여기에 표시됩니다."));
        search.TextChanged+=(_,_)=>{var text=search.Text?.Trim()??"";foreach(var entry in entries)entry.Row.IsVisible=entry.Label.Contains(text,StringComparison.CurrentCultureIgnoreCase);};
        apply.Clicked+=async(_,_)=>{if(Navigation.NavigationStack.LastOrDefault()==page)await Navigation.PopAsync();await Run(applied);};
        Count();await Navigation.PushAsync(page);
    }
    async Task MacChat(string? select=null,string[]? members=null)
    {
        var source=api!;var version=epoch;var me=source.Session.GetProperty("user").GetProperty("id").GetString()!;
        if(members is{Length:>0})
        {
            var made=await MacSend(source,"api/chats",new JsonObject{["memberIds"]=new JsonArray(members.Select(m=>(JsonNode?)JsonValue.Create(m)).ToArray()),["name"]=""});
            select=Field(made,"id");if(!Current(source,version))return;
        }
        var people=await MacPeopleList(source);if(!Current(source,version))return;
        string? current=null;var shown=100;var busySending=false;
        var files=new List<MacChatFile>();
        var root=new Grid{ColumnDefinitions={new(new GridLength(300)),new(GridLength.Star)},ColumnSpacing=1,BackgroundColor=Line};
        // Left: the rooms.
        var side=new Grid{BackgroundColor=Canvas,Padding=new Thickness(14,16,14,8),RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Star)}};
        var top=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};top.Add(Text("채팅",22,true));
        var newRoom=DesktopButton("새 대화방",null,true);newRoom.AutomationId="mac-chat-new";newRoom.FontSize=14;newRoom.MinimumHeightRequest=34;newRoom.Padding=new Thickness(12,4);top.Add(newRoom,1);side.Add(top);
        var roomList=new VerticalStackLayout{Spacing=2};side.Add(new ScrollView{Content=roomList},0,1);root.Add(side);
        // Right: the conversation, with the box fixed under it.
        var pane=new Grid{BackgroundColor=Surface,RowDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)}};root.Add(pane,1);
        var head=new Grid{Padding=new Thickness(24,14),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8};
        var titles=new VerticalStackLayout{Spacing=3,VerticalOptions=LayoutOptions.Center};var roomTitle=Text("대화방을 선택하세요",16,true);roomTitle.MaxLines=1;roomTitle.LineBreakMode=LineBreakMode.TailTruncation;roomTitle.AutomationId="mac-chat-title";
        var membersLabel=Text("",11);membersLabel.TextColor=Muted;membersLabel.MaxLines=1;membersLabel.LineBreakMode=LineBreakMode.TailTruncation;titles.Add(roomTitle);titles.Add(membersLabel);head.Add(titles);
        var tools=new HorizontalStackLayout{Spacing=2,VerticalOptions=LayoutOptions.Center};head.Add(tools,1);
        Button Tool(string label,string id){var b=DesktopButton(label);SubtleButton(b);b.AutomationId=id;tools.Add(b);return b;}
        var mute=Tool("알림 끄기","mac-chat-mute");var rename=Tool("이름 바꾸기","mac-chat-rename");var leave=Tool("나가기","mac-chat-leave");leave.TextColor=Danger;
        pane.Add(head);var headRule=Rule();headRule.VerticalOptions=LayoutOptions.End;pane.Add(headRule);
        var lines=new VerticalStackLayout{Spacing=10,Padding=new Thickness(24,12)};var scroll=new ScrollView{Content=lines};pane.Add(scroll,0,1);
        var feedbackLine=Text("",12);feedbackLine.TextColor=Muted;feedbackLine.IsVisible=false;
        feedbackLine.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Label.Text))feedbackLine.IsVisible=!string.IsNullOrEmpty(feedbackLine.Text);};
        var chips=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap,Direction=Microsoft.Maui.Layouts.FlexDirection.Row};
        var input=new Entry{ClassId="school-field",Placeholder="메시지를 입력하세요",MaxLength=20000,FontSize=16,TextColor=Navy,PlaceholderColor=Faint,BackgroundColor=Colors.Transparent,ReturnType=ReturnType.Send,AutomationId="mac-chat-input"};
        var attach=DesktopButton("파일");attach.FontSize=14;attach.MinimumHeightRequest=34;attach.Padding=new Thickness(12,4);attach.AutomationId="mac-chat-attach";
        var paste=DesktopButton("붙여넣기");paste.FontSize=14;paste.MinimumHeightRequest=34;paste.Padding=new Thickness(12,4);paste.AutomationId="mac-chat-paste";
        ToolTipProperties.SetText(paste,"복사한 사진이나 파일을 첨부합니다");
        var send=DesktopButton("보내기",null,true);send.FontSize=14;send.MinimumHeightRequest=34;send.Padding=new Thickness(16,4);send.AutomationId="mac-chat-send";
        var hint=Text("Enter 전송 · 파일은 이 칸에 끌어놓아도 됩니다",11);hint.TextColor=Faint;hint.VerticalOptions=LayoutOptions.Center;
        var actions=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8};actions.Add(attach);actions.Add(paste,1);actions.Add(hint,2);actions.Add(send,3);
        var composerStack=new VerticalStackLayout{Spacing=8};composerStack.Add(chips);composerStack.Add(input);composerStack.Add(actions);
        var box=DesktopPanel(composerStack,12);box.AutomationId="mac-chat-drop";
        var bottom=new VerticalStackLayout{Padding=new Thickness(24,8,24,16),Spacing=6};bottom.Add(feedbackLine);bottom.Add(box);pane.Add(bottom,0,2);
        JsonElement[] rooms=[];
        JsonElement? Room()=>rooms.FirstOrDefault(r=>RowId(r)==current) is{ValueKind:JsonValueKind.Object} found?found:null;
        void ShowFiles()
        {
            chips.Clear();
            foreach(var file in files.ToArray())
            {
                var name=Text(file.Name,12);name.MaxLines=1;name.LineBreakMode=LineBreakMode.TailTruncation;name.MaximumWidthRequest=200;name.VerticalOptions=LayoutOptions.Center;
                var size=Text(FileSize(file.Size),11);size.TextColor=Faint;size.VerticalOptions=LayoutOptions.Center;
                var remove=new Button{Text="×",FontSize=15,Padding=0,WidthRequest=24,HeightRequest=24,MinimumHeightRequest=0,MinimumWidthRequest=0,BackgroundColor=Colors.Transparent,BorderWidth=0,TextColor=Muted};
                SemanticProperties.SetDescription(remove,file.Name+" 보내지 않기");
                remove.Clicked+=async(_,_)=>{if(busySending||current is not null&&macChatPending.ContainsKey(current))return;files.Remove(file);ShowFiles();if(file.Uploaded is{} sent)await MacQuietly(()=>source.Send("api/attachments/"+sent,method:HttpMethod.Delete));};
                var row=new HorizontalStackLayout{Spacing=6,Children={name,size,remove}};
                chips.Add(new Border{Content=row,Padding=new Thickness(10,2,4,2),Margin=new Thickness(0,0,8,4),BackgroundColor=Pale,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=14}});
            }
            chips.IsVisible=files.Count>0;
        }
        // Files uploaded for a message that will never go out are taken off the server again.
        void DiscardFiles()
        {
            foreach(var sent in files.Select(f=>f.Uploaded).OfType<string>().ToArray())_=MacQuietly(()=>source.Send("api/attachments/"+sent,method:HttpMethod.Delete));
            files.Clear();ShowFiles();
        }
        // Chosen, dropped or pasted files go up at once: a Finder drop is readable only while it lasts. They wait under
        // the box until sent, and "×" takes one back off the server.
        async Task AddPaths(IEnumerable<string> paths)
        {
            if(current is null||busySending||macChatPending.ContainsKey(current))return;
            await source.RefreshAttachmentLimit();var limit=source.AttachmentLimit;var label=SchoolMessenger.Contracts.AttachmentLimits.Label(limit);
            var fresh=paths.Where(p=>files.All(f=>f.Path!=p)).Distinct().ToArray();
            if(files.Count+fresh.Length>10)throw new IOException("첨부는 최대 10개입니다.");
            busySending=true;send.IsEnabled=false;
            try
            {
                foreach(var path in fresh)
                {
                    var left=limit-files.Sum(f=>f.Size);
                    if(Directory.Exists(path))
                    {
                        feedbackLine.Text="폴더 ZIP 준비 중 · "+System.IO.Path.GetFileName(path);
                        using var folder=await Task.Run(()=>SchoolMessenger.Shared.FolderAttachment.Create(path,left));
                        var zipped=new FileInfo(folder.Path);feedbackLine.Text="첨부 업로드 중 · "+zipped.Name;
                        files.Add(new(path,zipped.Name,zipped.Length){Uploaded=RowId(await source.Upload(new FileResult(folder.Path),false))});
                    }
                    else
                    {
                        var info=new FileInfo(path);if(!info.Exists)continue;
                        if(info.Length<=0||info.Length>left)throw new IOException($"첨부 합계는 {label}까지입니다.");
                        feedbackLine.Text="첨부 업로드 중 · "+info.Name;
                        files.Add(new(path,info.Name,info.Length){Uploaded=RowId(await source.Upload(new FileResult(path),false))});
                    }
                    ShowFiles();
                }
                feedbackLine.Text="";
            }
            finally{busySending=false;send.IsEnabled=true;ShowFiles();}
        }
        void Freeze(bool frozen){input.IsReadOnly=frozen;attach.IsEnabled=paste.IsEnabled=!frozen;roomList.IsEnabled=!frozen;}
        void ShowRooms()
        {
            roomList.Clear();var muted=MacMutedRooms();
            foreach(var room in rooms)
            {
                var id=RowId(room);var display=ChatDisplay(room,me);var unreadCount=room.GetProperty("unread").GetInt32();var selected=id==current;
                var line=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},RowDefinitions={new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=10,RowSpacing=3};
                var face=ChatFace(room,me,display);line.Add(face);Grid.SetRowSpan(face,2);
                var name=Text(display+(muted.Contains(id)?"  🔕":""),13,true);name.MaxLines=1;name.LineBreakMode=LineBreakMode.TailTruncation;name.TextColor=selected?Blue:Navy;line.Add(name,1);
                var when=Text(ChatWhen(MacLastAt(room)),11);when.TextColor=Faint;line.Add(when,2);
                var preview=Text(Field(room,"preview")??"",12);preview.TextColor=Muted;preview.MaxLines=1;preview.LineBreakMode=LineBreakMode.TailTruncation;line.Add(preview,1,1);
                if(unreadCount>0)
                {
                    var count=new Label{Text=unreadCount>99?"99+":unreadCount.ToString(CultureInfo.InvariantCulture),FontSize=10,FontAttributes=FontAttributes.Bold,TextColor=Colors.White,HorizontalTextAlignment=TextAlignment.Center,VerticalTextAlignment=TextAlignment.Center};
                    line.Add(new Border{Content=count,BackgroundColor=Danger,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=9},MinimumWidthRequest=18,HeightRequest=18,Padding=new Thickness(5,0),HorizontalOptions=LayoutOptions.End},2,1);
                }
                var row=new Border{Content=line,Padding=new Thickness(10,9),BackgroundColor=selected?Pale:Colors.Transparent,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=Radius},AutomationId="mac-chat-room"};
                SemanticProperties.SetDescription(row,display+(unreadCount>0?$", 안 읽은 메시지 {unreadCount}개":""));
                var tap=new TapGestureRecognizer();tap.Tapped+=async(_,_)=>await Run(()=>Open(id));row.GestureRecognizers.Add(tap);roomList.Add(row);
            }
            var hasRooms=rooms.Length>0;pane.IsVisible=hasRooms;
            if(!hasRooms&&roomList.Children.Count==0)
            {
                var none=DesktopEmptyState("아직 대화방이 없습니다","동료를 골라 대화를 시작하세요.");none.Margin=new Thickness(0,24,0,0);roomList.Add(none);
            }
        }
        void ShowHead()
        {
            if(Room() is not{} room){roomTitle.Text="대화방을 선택하세요";membersLabel.Text="";tools.IsVisible=false;return;}
            tools.IsVisible=true;roomTitle.Text=ChatDisplay(room,me);ToolTipProperties.SetText(roomTitle,roomTitle.Text);
            membersLabel.Text=string.Join(", ",room.GetProperty("members").EnumerateArray().Select(p=>Field(p,"name")));
            mute.Text=MacMutedRooms().Contains(RowId(room))?"알림 켜기":"알림 끄기";leave.Text=Flag(room,"direct")?"목록에서 지우기":"나가기";
        }
        View Bubble(JsonElement entry)
        {
            var mine=Field(entry,"senderId")==me;var at=DateTimeOffset.FromUnixTimeMilliseconds(entry.GetProperty("createdAt").GetInt64()).ToLocalTime();
            var heading=Text((mine?"나":Field(entry,"senderName"))+" · "+at.ToString(at.Date==DateTime.Today?"HH:mm":"M.d HH:mm",CultureInfo.InvariantCulture),11);heading.TextColor=Faint;
            var content=new VerticalStackLayout{Spacing=6};var words=Text(Field(entry,"body")??"",15);words.TextColor=mine?Colors.White:Navy;content.Add(words);
            foreach(var file in entry.GetProperty("attachments").EnumerateArray())
            {
                var fileId=RowId(file);var fileName=Field(file,"name")??"";var expired=Flag(file,"expired");
                var get=DesktopButton(fileName+(expired?" · 기간 만료":" · 받기"),()=>MacReceive(source,fileId,fileName));MacFileMenu(get,elsewhere=>MacReceive(source,fileId,fileName,elsewhere));get.FontSize=12;get.MinimumHeightRequest=28;get.Padding=new Thickness(10,2);get.IsEnabled=!expired;get.HorizontalOptions=LayoutOptions.Start;
                content.Add(get);
            }
            var bubble=new Border{Content=content,Padding=new Thickness(14,10),BackgroundColor=mine?Blue:Canvas,StrokeThickness=0,
                StrokeShape=new RoundRectangle{CornerRadius=mine?new CornerRadius(14,4,14,14):new CornerRadius(4,14,14,14)},HorizontalOptions=mine?LayoutOptions.End:LayoutOptions.Start,MaximumWidthRequest=560};
            var stack=new VerticalStackLayout{Spacing=4,HorizontalOptions=mine?LayoutOptions.End:LayoutOptions.Start};heading.HorizontalOptions=mine?LayoutOptions.End:LayoutOptions.Start;stack.Add(heading);stack.Add(bubble);
            if(mine)return stack;
            var line=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=10};var face=Avatar(Field(entry,"senderName")??"",32,Field(entry,"senderId"));face.VerticalOptions=LayoutOptions.Start;line.Add(face);line.Add(stack,1);
            return line;
        }
        async Task Messages(bool older)
        {
            var room=current;if(room is null)return;
            JsonElement[] all;
            if(desktopArchive?.Owner==source){await desktopArchive.KeepRoom(room);all=desktopArchive.ChatEntries(room);}
            else all=(await source.Get($"api/chats/{room}/messages")).EnumerateArray().ToArray();
            if(room!=current||!Current(source,version))return;
            if(older)shown+=100;
            lines.Clear();
            if(all.Length>shown){var more=DesktopButton("이전 대화 불러오기",()=>Messages(true));SubtleButton(more);more.HorizontalOptions=LayoutOptions.Center;lines.Add(more);}
            if(all.Length==0){var note=Text("이 Mac에 저장된 대화만 보입니다. 다른 PC에서 받은 대화는 볼 수 없습니다.",12);note.TextColor=Faint;note.HorizontalTextAlignment=TextAlignment.Center;lines.Add(note);}
            foreach(var entry in all.TakeLast(shown))lines.Add(Bubble(entry));
            if(!older&&all.Length>0)
            {
                await MacSend(source,$"api/chats/{room}/read",new JsonObject{["sequence"]=all[^1].GetProperty("sequence").GetInt64()});
                // Scrolled to the newest line once the bubbles have their size.
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120),async()=>{if(lines.Children.LastOrDefault() is Element last)await scroll.ScrollToAsync(last,ScrollToPosition.End,false);});
            }
        }
        async Task Open(string id)
        {
            if(busySending)return;
            if(current!=id){DiscardFiles();input.Text="";feedbackLine.Text="";shown=100;}
            current=id;macReadingRoom=id;
            if(macChatPending.TryGetValue(id,out var waiting)){input.Text=(string?)waiting["body"]??"";Freeze(true);feedbackLine.Text="전송 결과 미확인. 보내기를 눌러 같은 대화를 재시도하세요.";}
            else Freeze(false);
            ShowRooms();ShowHead();await Messages(false);
            await Reload(false);
        }
        async Task Reload(bool openFirst=true)
        {
            var listed=await MacRooms(source);if(!Current(source,version))return;
            rooms=listed;
            if(current is null||rooms.All(r=>RowId(r)!=current)){current=null;if(openFirst&&rooms.Length>0){await Open(select is not null&&rooms.Any(r=>RowId(r)==select)?select:RowId(rooms[0]));select=null;return;}}
            ShowRooms();ShowHead();await MacCountUnread(source,rooms);
        }
        async Task Send()
        {
            if(current is not{} room||busySending)return;
            var pending=macChatPending.GetValueOrDefault(room);
            if(pending is null&&string.IsNullOrWhiteSpace(input.Text)&&files.Count==0)return;
            busySending=true;Freeze(true);send.IsEnabled=false;
            try
            {
                if(pending is null)
                {
                    // Uploads wait on the server for a while only; the ones that expired are named so they can be added again.
                    if(files.Count>0)
                    {
                        using var asked=new StringContent(new JsonArray(files.Select(f=>(JsonNode?)JsonValue.Create(f.Uploaded)).ToArray()).ToJsonString(),System.Text.Encoding.UTF8,"application/json");
                        try
                        {
                            var staged=(await source.Send("api/attachments/staged/check",content:asked)).EnumerateArray().Select(i=>i.GetString()).ToHashSet();
                            var gone=files.Where(f=>!staged.Contains(f.Uploaded)).ToArray();
                            if(gone.Length>0){foreach(var file in gone)files.Remove(file);ShowFiles();throw new IOException("일부 첨부가 만료됐습니다. 해당 파일만 다시 첨부하세요. "+string.Join(", ",gone.Select(f=>f.Name)));}
                        }
                        catch(MessengerApiRefusal refusal)when(refusal.Status==404){}
                    }
                    pending=new JsonObject{["clientId"]=Guid.NewGuid().ToString("N"),["body"]=input.Text??"",["attachmentIds"]=new JsonArray(files.Select(f=>(JsonNode?)JsonValue.Create(f.Uploaded)).ToArray())};
                    macChatPending[room]=pending;
                }
                await MacSend(source,$"api/chats/{room}/messages",pending);
                macChatPending.Remove(room);input.Text="";files.Clear();ShowFiles();feedbackLine.Text="";
                await Messages(false);
            }
            catch(MessengerApiRefusal error)when(error.Status is 400 or 401 or 403 or 404 or 413 or 415 or 422){macChatPending.Remove(room);feedbackLine.Text=error.Message;}
            catch(Exception error){feedbackLine.Text=(macChatPending.ContainsKey(room)?"전송 결과 미확인. 보내기를 눌러 재시도하세요. ":"첨부 업로드 실패. 다시 시도하세요. ")+error.Message;}
            finally
            {
                busySending=false;Freeze(macChatPending.ContainsKey(room));send.IsEnabled=true;
                // The box keeps the keyboard, so the next line can be typed right after Enter.
                input.Focus();
            }
        }
        input.Completed+=async(_,_)=>await Send();
        send.Clicked+=async(_,_)=>await Send();
        attach.Clicked+=async(_,_)=>await Run(async()=>
        {
            preserveComposerOnResume=true;
            var picked=(await FilePicker.Default.PickMultipleAsync(new PickOptions{PickerTitle="채팅 첨부파일 선택"}))?.OfType<FileResult>().Select(f=>f.FullPath).ToArray()??[];
            if(picked.Length>0)await AddPaths(picked);
        });
        paste.Clicked+=async(_,_)=>await Run(async()=>
        {
            var paths=MacClipboard.Take();
            if(paths.Length==0)throw new InvalidOperationException("붙여넣을 사진이나 파일이 없습니다. 사진이나 파일을 먼저 복사하세요.");
            await AddPaths(paths);
        });
        MacAttachmentDrop.Connect(box,()=>current is not null&&!busySending&&!macChatPending.ContainsKey(current),providers=>Run(async()=>
        {
            // The OS must receive the load requests during PerformDrop, before the first await.
            var reads=providers.Select(p=>MacAttachmentDrop.Read(p,source.AttachmentLimit)).ToArray();var kept=new List<MacAttachmentDrop.Item>();
            try{foreach(var read in reads)kept.Add(await read);await AddPaths(kept.Select(k=>k.Path));}
            finally{foreach(var item in kept)item.Dispose();}
        }));
        newRoom.Clicked+=async(_,_)=>await Run(async()=>
        {
            var chosen=new HashSet<string>();var name=new Entry{ClassId="school-field",Placeholder="대화방 이름 · 비워 두면 참여자 이름으로 표시",MaxLength=100,FontSize=15};
            await MacPickPeople("새 대화방","대화 시작",people.Where(p=>RowId(p)!=me).ToArray(),chosen,async()=>
            {
                if(chosen.Count==0)throw new InvalidOperationException("함께 대화할 교직원을 한 명 이상 고르세요.");
                var made=await MacSend(source,"api/chats",new JsonObject{["memberIds"]=new JsonArray(chosen.Select(m=>(JsonNode?)JsonValue.Create(m)).ToArray()),["name"]=name.Text??""});
                select=Field(made,"id");current=null;await Reload();
            },DesktopPanel(name,8));
        });
        mute.Clicked+=async(_,_)=>await Run(async()=>
        {
            if(current is null)return;var muted=MacMutedRooms();if(!muted.Add(current))muted.Remove(current);SaveMacList("mac-muted-"+me,muted);
            ShowRooms();ShowHead();await MacCountUnread(source,rooms);
        });
        rename.Clicked+=async(_,_)=>await Run(async()=>
        {
            if(Room() is not{} room)return;
            var name=await DisplayPromptAsync("대화방 이름 바꾸기","내 화면에서만 바뀝니다. 비워 두면 원래 이름으로 돌아갑니다.","저장","취소",maxLength:100,initialValue:ChatDisplay(room,me));
            if(name is null)return;
            await MacSend(source,$"api/chats/{RowId(room)}/name",new JsonObject{["name"]=name});await Reload(false);
        });
        leave.Clicked+=async(_,_)=>await Run(async()=>
        {
            if(Room() is not{} room)return;var direct=Flag(room,"direct");var display=ChatDisplay(room,me);
            var warning=direct?$"{display} 대화를 내 목록에서 지울까요?\n상대방의 대화는 그대로이고, 상대방이 새 메시지를 보내면 다시 나타납니다.":$"{display} 대화방에서 나갈까요?\n나간 뒤에는 이 대화방을 다시 볼 수 없습니다.";
            if(!await DisplayAlertAsync("채팅",warning,direct?"목록에서 지우기":"대화방 나가기","취소"))return;
            var id=RowId(room);await source.Send($"api/chats/{id}/leave");
            if(!direct&&desktopArchive?.Owner==source)desktopArchive.DropRoom(id);
            var muted=MacMutedRooms();muted.Remove(id);SaveMacList("mac-muted-"+me,muted);
            macChatPending.Remove(id);DiscardFiles();current=null;await Reload();
        });
        macChatChanged=async id=>{if(!busySending&&Current(source,version)){if(id==current)await Messages(false);await Reload(false);}};
        MacShowPane("chat",root);SelectRail("chat");desktopReader=null;
        ShowRooms();ShowHead();await Reload();
    }
}
