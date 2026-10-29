using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Controls.Shapes;

namespace SchoolMessenger.Mobile;

// Windows WorkWindow와 같은 서버 API(api/todos, api/submission-requests)를 쓰는 Mac 할 일·파일 제출 화면.
public sealed partial class MessengerPage
{
    readonly Dictionary<string,JsonObject> macPendingSubmissions=new();
    Grid? macSubmissionTargets;
    static DateOnly KoreanToday=>DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).DateTime);
    static string? Field(JsonElement value,string name)=>value.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
    static string TodoStatus(string? status)=>status switch{"suggested"=>"추천 · 확인 필요","open"=>"진행 중","done"=>"완료",_=>"제외"};
    static string TodoDeadline(string? value,string? status)
    {
        if(!DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var due))return "기한 없음";
        var date=due.ToString("yyyy.MM.dd",CultureInfo.InvariantCulture);if(status is "done" or "dismissed")return date;
        var days=due.DayNumber-KoreanToday.DayNumber;return date+" · "+(days>0?"D-"+days:days==0?"D-Day":"기한 지남 "+(-days)+"일");
    }
    static string FileSize(long size)=>size<1024*1024?Math.Max(1,size/1024)+"KB":(size/1024d/1024).ToString("0.0",CultureInfo.InvariantCulture)+"MB";
    async Task MacWork(string mode="todos",string? select=null,int pageIndex=0)
    {
        var source=api!;var version=epoch;var rows=await source.Get("api/"+mode+"?offset="+pageIndex*100);if(!Current(source,version))return;
        desktopReader=null;macSubmissionTargets=null;
        var root=new Grid{RowDefinitions={new(GridLength.Auto),new(GridLength.Star)},BackgroundColor=Colors.White};
        var toolbar=new Grid{Padding=new Thickness(22,14),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=12,BackgroundColor=Color.FromArgb("#F8FAFD")};
        var heading=new VerticalStackLayout{Spacing=3};heading.Add(Text("할 일과 파일 제출",19,true));var meta=Text("진행 중인 일은 기한순 · D-Day는 한국 날짜 기준",11);meta.TextColor=Muted;heading.Add(meta);toolbar.Add(heading);
        var panes=new Grid{ColumnDefinitions={new(new GridLength(296)),new(GridLength.Star)},ColumnSpacing=1,BackgroundColor=Line};
        var detail=new ContentView{BackgroundColor=Colors.White};panes.Add(detail,1);
        var switcher=new HorizontalStackLayout{Spacing=6,VerticalOptions=LayoutOptions.Center};
        void Tab(string key,string label,Func<Task> action){var b=DesktopButton(label,action,key=="add");b.AutomationId="mac-work-"+key;b.FontSize=13;b.MinimumHeightRequest=34;b.Padding=new Thickness(10,5);if(key==mode){b.BackgroundColor=Pale;b.TextColor=Blue;b.BorderColor=Blue;}switcher.Add(b);}
        Tab("todos","나의 할 일",()=>MacWork("todos"));Tab("submission-requests","파일 제출 · 수합",()=>MacWork("submission-requests"));
        if(mode=="todos")Tab("add","할 일 추가",()=>{MacTodo(source,version,null,detail);return Task.CompletedTask;});
        toolbar.Add(switcher,1);root.Add(toolbar);root.Add(panes,0,1);
        var empty=new VerticalStackLayout{Spacing=10,VerticalOptions=LayoutOptions.Center,HorizontalOptions=LayoutOptions.Center};empty.Add(new Image{Source="action_calendar.png",HeightRequest=32,WidthRequest=32});
        var hint=Text(mode=="todos"?"왼쪽에서 할 일을 선택하거나 새로 추가하세요":"왼쪽에서 제출 요청을 선택하세요",13);hint.TextColor=Muted;empty.Add(hint);detail.Content=empty;
        var list=new Grid{BackgroundColor=Color.FromArgb("#F5F8FC"),Padding=12,RowSpacing=10,RowDefinitions={new(GridLength.Star),new(GridLength.Auto)}};
        var items=new VerticalStackLayout{Spacing=6};var choices=new List<(Border Row,string Id)>();
        async Task Open(JsonElement row,Border selected)
        {
            foreach(var choice in choices)choice.Row.BackgroundColor=choice.Row==selected?Pale:Colors.Transparent;
            if(mode=="todos")MacTodo(source,version,row,detail);else await MacSubmission(source,version,row.GetProperty("id").GetString()!,detail);
        }
        foreach(var row in rows.EnumerateArray())
        {
            var id=row.GetProperty("id").GetString()!;var card=new VerticalStackLayout{Spacing=4};var border=DesktopPanel(card,12,Colors.Transparent);border.StrokeThickness=0;
            var title=DesktopButton((row.GetProperty("title").GetString()??"").ReplaceLineEndings(" "),()=>Open(row,border));title.ClassId="school-story";title.AutomationId="mac-work-row-"+id;title.BackgroundColor=Colors.Transparent;title.BorderWidth=0;title.Padding=0;title.FontSize=15;title.MinimumHeightRequest=28;title.HorizontalOptions=LayoutOptions.Fill;
            title.FontAttributes=mode=="todos"&&Field(row,"status")=="suggested"?FontAttributes.Bold:FontAttributes.None;card.Add(title);
            var summary=Text(mode=="todos"?TodoStatus(Field(row,"status"))+" · "+TodoDeadline(Field(row,"dueDate"),Field(row,"status")):Field(row,"ownerName")+" · "+row.GetProperty("submittedCount").GetInt32()+"/"+row.GetProperty("targetCount").GetInt32()+"명 제출"+(row.GetProperty("closed").GetBoolean()?" · 종료":""),11);
            summary.AutomationId="mac-work-summary-"+id;summary.TextColor=Muted;summary.MaxLines=1;summary.LineBreakMode=LineBreakMode.TailTruncation;card.Add(summary);items.Add(border);choices.Add((border,id));
        }
        if(rows.GetArrayLength()==0){var none=Text(mode=="todos"?"등록된 할 일이 없습니다":"주고받은 제출 요청이 없습니다",12);none.TextColor=Muted;items.Add(none);}
        list.Add(new ScrollView{Content=items});
        var full=rows.GetArrayLength()==100;var more=DesktopButton(full?"다음 목록":"새로고침",()=>MacWork(mode,null,full?pageIndex+1:pageIndex));more.FontSize=13;more.MinimumHeightRequest=36;list.Add(more,0,1);panes.Add(list);
        desktopContent.Content=root;
        foreach(var pair in desktopMenu){pair.Value.BackgroundColor=pair.Key=="work"?Pale:Colors.Transparent;pair.Value.TextColor=pair.Key=="work"?Blue:Muted;}
        var initial=select is null?-1:choices.FindIndex(c=>c.Id==select);
        if(initial>=0)await Open(rows[initial],choices[initial].Row);else if(select is not null&&mode=="submission-requests")await MacSubmission(source,version,select,detail);
    }
    void MacTodo(MessengerApi source,int version,JsonElement? item,ContentView host)
    {
        var stack=new VerticalStackLayout{Padding=24,Spacing=10};host.Content=new ScrollView{Content=stack};
        stack.Add(Text(item is null?"할 일 직접 추가":"할 일 확인 · 수정",20,true));
        var status=item is{} current?Field(current,"status"):"open";
        if(item is{} original)
        {
            if(Field(original,"evidence") is{Length:>0} evidence){var quote=Text(evidence,12);quote.TextColor=Muted;stack.Add(quote);}
            var counter=Text(TodoDeadline(Field(original,"dueDate"),status),15,true);counter.AutomationId="mac-todo-deadline";counter.TextColor=Blue;stack.Add(counter);
            if(Field(original,"sourceId") is{Length:>0} sourceId){var view=DesktopButton("원본 메시지 보기",()=>OfficeDetail(sourceId));view.HorizontalOptions=LayoutOptions.Start;stack.Add(view);}
        }
        var title=new Entry{ClassId="school-field",AutomationId="mac-todo-title",Placeholder="할 일 제목",MaxLength=200,FontSize=16,TextColor=Navy,PlaceholderColor=Muted,BackgroundColor=Colors.Transparent,Text=item is{} t?Field(t,"title"):""};
        stack.Add(Text("제목",12,true));stack.Add(DesktopPanel(title,10));
        DateOnly? due=item is{} d&&DateOnly.TryParseExact(Field(d,"dueDate"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)?parsed:null;
        var date=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=(due??KoreanToday.AddDays(7)).ToDateTime(TimeOnly.MinValue),IsEnabled=due is not null};
        var dated=new CheckBox{IsChecked=due is not null,Color=Blue};dated.CheckedChanged+=(_,e)=>date.IsEnabled=e.Value;
        var dueLabel=Text("기한 지정",14);dueLabel.VerticalOptions=LayoutOptions.Center;
        stack.Add(Text("기한 · 선택",12,true));stack.Add(DesktopPanel(new HorizontalStackLayout{Spacing=8,Children={dated,dueLabel,date}},8));
        string[] statuses=["open","done","dismissed"];
        var picker=new Picker{ClassId="school-field",AutomationId="mac-todo-status",Title="상태",ItemsSource=statuses.Select(s=>TodoStatus(s)).ToArray(),SelectedIndex=Math.Max(0,Array.IndexOf(statuses,status)),IsEnabled=item is not null,TextColor=Navy};
        stack.Add(Text("상태",12,true));stack.Add(DesktopPanel(picker,8));
        var save=DesktopButton(status=="suggested"?"확인 후 등록":"저장",async()=>
        {
            if(!Current(source,version))return;
            var id=item is{} existing?Field(existing,"id"):null;
            var payload=new JsonObject{["title"]=title.Text??"",["dueDate"]=dated.IsChecked&&date.Date is{} day?day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture):null,["status"]=statuses[Math.Max(0,picker.SelectedIndex)],["revision"]=item is{} old?old.GetProperty("revision").GetInt32():0};
            var saved=await MacSend(source,"api/todos"+(id is null?"":"/"+id),payload,id is null?null:HttpMethod.Patch);
            await MacWork("todos",id??(saved.ValueKind==JsonValueKind.Object?Field(saved,"id"):null));feedback.Text="할 일을 저장했습니다.";
        },true);
        save.AutomationId="mac-todo-save";save.HorizontalOptions=LayoutOptions.Start;stack.Add(save);
    }
    async Task MacSubmission(MessengerApi source,int version,string id,ContentView host)
    {
        var response=await source.Get("api/submission-requests/"+id);if(!Current(source,version))return;
        var request=response.GetProperty("request");var targets=response.GetProperty("targets").EnumerateArray().ToArray();
        var closed=request.GetProperty("closed").GetBoolean();var mine=Field(request,"ownerId")==source.Session.GetProperty("user").GetProperty("id").GetString();
        Task Reload()=>MacSubmission(source,version,id,host);
        var stack=new VerticalStackLayout{Padding=24,Spacing=10};host.Content=new ScrollView{Content=stack};
        var heading=Text(Field(request,"title")??"",20,true);heading.MaxLines=2;heading.LineBreakMode=LineBreakMode.TailTruncation;stack.Add(heading);
        var deadline=Text("마감 "+DateTimeOffset.FromUnixTimeMilliseconds(request.GetProperty("deadline").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm",CultureInfo.InvariantCulture)+(closed?" · 종료":""),12);deadline.TextColor=Muted;stack.Add(deadline);
        var message=Text(Field(request,"body")??"",14);message.MaxLines=6;message.LineBreakMode=LineBreakMode.TailTruncation;stack.Add(message);
        if(mine)
        {
            var required=targets.Where(t=>!t.GetProperty("exempt").GetBoolean()).ToArray();var submitted=required.Count(t=>t.GetProperty("submittedAt").ValueKind!=JsonValueKind.Null);
            var summary=Text($"제출 {submitted}/{required.Length}명 · 미제출 {required.Length-submitted}명 · 면제 {targets.Length-required.Length}명",14,true);summary.AutomationId="mac-submission-summary";stack.Add(DesktopPanel(summary,12,Pale));
            var actions=new HorizontalStackLayout{Spacing=8};
            actions.Add(DesktopButton("파일 일괄 다운로드 (ZIP)",()=>source.ShareDownload("api/submission-requests/"+id+"/zip","제출파일.zip",2L*1024*1024*1024),true));
            if(!closed)actions.Add(DesktopButton("요청 종료",async()=>{if(!await DisplayAlertAsync("제출 요청 종료","새 제출과 자동 알림을 종료할까요?","종료","취소"))return;await source.Send("api/submission-requests/"+id+"/close");await Reload();}));
            stack.Add(actions);
        }
        // 이름 중심 카드: 보낸 사람은 한 줄 최대 4명, 창이 좁아지면 열 수를 줄인다.
        var grid=new Grid{ColumnSpacing=8,RowSpacing=8,AutomationId="mac-submission-targets"};macSubmissionTargets=grid;var columns=0;
        void Arrange(int count)
        {
            if(count==columns)return;columns=count;grid.ColumnDefinitions.Clear();grid.RowDefinitions.Clear();var cards=grid.Children.OfType<View>().ToArray();
            for(var c=0;c<count;c++)grid.ColumnDefinitions.Add(new(GridLength.Star));for(var r=0;r<(cards.Length+count-1)/count;r++)grid.RowDefinitions.Add(new(GridLength.Auto));
            for(var i=0;i<cards.Length;i++){Grid.SetColumn(cards[i],i%count);Grid.SetRow(cards[i],i/count);}
        }
        grid.SizeChanged+=(_,_)=>{if(grid.Width>0)Arrange(mine?Math.Clamp((int)((grid.Width+8)/140),1,4):1);};
        foreach(var target in targets)
        {
            var exempt=target.GetProperty("exempt").GetBoolean();var done=target.GetProperty("submittedAt").ValueKind!=JsonValueKind.Null;var name=Field(target,"name")??"";
            var state=exempt?"면제":done?target.GetProperty("late").GetBoolean()?"지각 제출":"제출 완료":"미제출";
            var content=new VerticalStackLayout{Spacing=4};var header=new Grid{ColumnDefinitions={new(new GridLength(12)),new(GridLength.Star),new(GridLength.Auto)}};
            header.Add(new Ellipse{WidthRequest=7,HeightRequest=7,Fill=new SolidColorBrush(exempt?Muted:done?Blue:Color.FromArgb("#B8C4D4")),VerticalOptions=LayoutOptions.Center,HorizontalOptions=LayoutOptions.Start});
            var label=Text(name,13,true);label.MaxLines=1;label.LineBreakMode=LineBreakMode.TailTruncation;label.VerticalOptions=LayoutOptions.Center;header.Add(label,1);content.Add(header);
            foreach(var file in target.GetProperty("files").EnumerateArray())
            {
                var fileId=Field(file,"id")!;var fileName=Field(file,"name")??"";var expired=file.GetProperty("expired").GetBoolean();var description=fileName+" · "+FileSize(file.GetProperty("size").GetInt64())+(expired?" · 파일 만료":"");
                var entry=DesktopButton(fileName+(expired?" (만료)":""),()=>source.ShareFile(fileId,fileName));entry.AutomationId="mac-submission-file";entry.FontSize=11;entry.MinimumHeightRequest=26;entry.Padding=new Thickness(6,3);entry.HorizontalOptions=LayoutOptions.Fill;entry.IsEnabled=!expired;
                ToolTipProperties.SetText(entry,description);SemanticProperties.SetDescription(entry,description);content.Add(entry);
            }
            var card=DesktopPanel(content,8,Color.FromArgb("#F8FAFD"));card.MinimumHeightRequest=48;card.AutomationId="mac-submission-target";card.ClassId=state;
            ToolTipProperties.SetText(card,name+" · "+state);SemanticProperties.SetDescription(card,name+" · "+state);
            if(mine&&!closed)
            {
                var targetId=Field(target,"id")!;
                var manage=new Button{Text="…",FontSize=14,Padding=0,WidthRequest=26,HeightRequest=24,MinimumHeightRequest=0,MinimumWidthRequest=0,BorderWidth=0,BackgroundColor=Colors.Transparent,TextColor=Muted};
                ToolTipProperties.SetText(manage,name+" 제출 관리");SemanticProperties.SetDescription(manage,name+" 제출 관리");
                manage.Clicked+=async(_,_)=>await Run(async()=>
                {
                    if(!await DisplayAlertAsync(name+" 제출 관리",exempt?"제출 면제를 해제할까요?":"제출 대상에서 면제할까요?",exempt?"면제 해제":"제출 면제","취소"))return;
                    await MacSend(source,"api/submission-requests/"+id+"/targets/"+targetId,new JsonObject{["exempt"]=!exempt},HttpMethod.Patch);await Reload();
                });
                header.Add(manage,2);
            }
            grid.Add(card);
        }
        Arrange(mine?4:1);stack.Add(grid);
        if(!mine&&!closed&&targets.Length>0&&!targets[0].GetProperty("exempt").GetBoolean())
        {
            var hint=Text("답장 없이 파일만 제출할 수 있습니다. 재제출하면 최신 파일로 바뀝니다.",12);hint.TextColor=Muted;stack.Add(hint);
            var actions=new HorizontalStackLayout{Spacing=8};
            if(Field(targets[0],"submissionId") is{} submissionId)actions.Add(DesktopButton("제출 회수",async()=>
            {
                if(!await DisplayAlertAsync("제출 회수","제출 파일을 회수하고 미제출 상태로 바꿀까요? 이미 내려받은 사본은 회수할 수 없습니다.","회수","취소"))return;
                await MacSend(source,"api/submission-requests/"+id+"/withdraw",new JsonObject{["submissionId"]=submissionId});macPendingSubmissions.Remove(id);await Reload();
            }));
            actions.Add(DesktopButton(macPendingSubmissions.ContainsKey(id)?"같은 제출 다시 시도":"파일 선택하여 제출 · 재제출",async()=>
            {
                if(!macPendingSubmissions.TryGetValue(id,out var packet))
                {
                    preserveComposerOnResume=true;var picked=(await FilePicker.Default.PickMultipleAsync(new PickOptions{PickerTitle="제출할 파일 선택"}))?.OfType<FileResult>().ToArray()??[];
                    if(picked.Length==0)return;if(picked.Length>10)throw new InvalidOperationException("파일은 최대 10개까지 제출할 수 있습니다.");
                    var uploads=new JsonArray();foreach(var file in picked){var upload=await source.Upload(file,false);if(!Current(source,version))return;uploads.Add((JsonNode?)JsonValue.Create(upload.GetProperty("id").GetString()));}
                    packet=new JsonObject{["clientId"]=Guid.NewGuid().ToString("N"),["attachmentIds"]=uploads};macPendingSubmissions[id]=packet;
                }
                await MacSend(source,"api/submission-requests/"+id+"/submit",packet);macPendingSubmissions.Remove(id);await Reload();feedback.Text="파일을 제출했습니다.";
            },true));
            stack.Add(actions);
        }
        var retention=Text("업로드한 파일은 30일 보관됩니다.",11);retention.TextColor=Muted;stack.Add(retention);
    }
}
