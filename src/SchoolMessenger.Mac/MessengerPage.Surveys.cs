using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Controls.Shapes;

namespace SchoolMessenger.Mobile;

// The Windows SurveyWindow on the Mac: received and sent surveys on the left, the survey on the right, and a form for a
// new one. Anonymous answers move from the server to the owner's PC; the owner sees only what this Mac has kept.
public sealed partial class MessengerPage
{
    async Task MacSurveys(string box="received",string? select=null,bool create=false,string[]? targets=null)
    {
        var source=api!;var version=epoch;var me=source.Session.GetProperty("user").GetProperty("id").GetString()!;
        var user=source.Session.GetProperty("user");var broadcast=Flag(user,"isAdmin")||Flag(user,"canBroadcast");
        var people=await MacPeopleList(source);if(!Current(source,version))return;
        var root=new Grid{ColumnDefinitions={new(new GridLength(320)),new(GridLength.Star)},ColumnSpacing=1,BackgroundColor=Line};
        var side=new Grid{BackgroundColor=Canvas,Padding=new Thickness(14,16,14,8),RowSpacing=10,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star)}};
        var top=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};top.Add(Text("설문",22,true));
        var make=DesktopButton("새 설문",()=>MacSurveys("sent",null,true),true);make.AutomationId="mac-survey-new";make.FontSize=14;make.MinimumHeightRequest=34;make.Padding=new Thickness(12,4);top.Add(make,1);side.Add(top);
        var tabs=new HorizontalStackLayout{Spacing=6};
        foreach(var (key,label) in new[]{("received","받은 설문"),("sent","보낸 설문")})
        {var tab=DesktopButton(label,()=>MacSurveys(key));tab.AutomationId="mac-survey-"+key;Chip(tab,box==key);tabs.Add(tab);}
        side.Add(tabs,0,1);
        var list=new VerticalStackLayout{Spacing=2};side.Add(new ScrollView{Content=list},0,2);root.Add(side);
        var detail=new ContentView{BackgroundColor=Surface};root.Add(detail,1);
        JsonElement[] surveys=[];string? current=null;
        async Task LoadList()
        {
            var all=(await source.Get("api/surveys")).EnumerateArray().ToArray();if(!Current(source,version))return;
            surveys=all.Where(s=>box=="sent"?Field(s,"ownerId")==me:Flag(s,"target")||Field(s,"ownerId")!=me).ToArray();
            list.Clear();
            foreach(var survey in surveys)
            {
                var id=RowId(survey);var closed=Flag(survey,"closed");var answered=Flag(survey,"answered");
                var count=$"{survey.GetProperty("responseCount").GetInt32()}/{survey.GetProperty("targetCount").GetInt32()}명";
                var status=box=="sent"?$"{(closed?"종료":"진행 중")} · 응답 {count}":$"{Field(survey,"ownerName")} · {(closed?"종료":answered?"응답 완료":"미응답")} · {count}";
                var words=new VerticalStackLayout{Spacing=3};
                var titleRow=new HorizontalStackLayout{Spacing=6};
                if(Flag(survey,"anonymous")){var tag=Text("익명",10,true);tag.TextColor=Blue;titleRow.Add(new Border{Content=tag,BackgroundColor=Pale,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=8},Padding=new Thickness(6,1),VerticalOptions=LayoutOptions.Center});}
                var title=Text(Field(survey,"title")??"",14,box=="received"&&!answered&&!closed);title.MaxLines=1;title.LineBreakMode=LineBreakMode.TailTruncation;titleRow.Add(title);words.Add(titleRow);
                var note=Text(status,11);note.TextColor=Muted;note.MaxLines=1;note.LineBreakMode=LineBreakMode.TailTruncation;words.Add(note);
                var row=new Border{Content=words,Padding=new Thickness(10,9),BackgroundColor=id==current?Pale:Colors.Transparent,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=Radius},AutomationId="mac-survey-row"};
                var tap=new TapGestureRecognizer();tap.Tapped+=async(_,_)=>await Run(()=>Show(id));row.GestureRecognizers.Add(tap);list.Add(row);
            }
            if(surveys.Length==0)list.Add(DesktopEmptyState(box=="sent"?"보낸 설문이 없습니다":"받은 설문이 없습니다",box=="sent"?"새 설문을 만들면 응답 결과를 여기에서 봅니다.":"동료가 보낸 설문이 여기에 표시됩니다."));
        }
        async Task<JsonElement> Results(string id,bool anonymous)
        {
            var result=await source.Get($"api/surveys/{id}/results");
            return anonymous&&desktopArchive?.Owner==source?await desktopArchive.KeepBallots(result,id):result;
        }
        async Task Show(string id)
        {
            var survey=await source.Get($"api/surveys/{id}");if(!Current(source,version))return;
            current=id;await LoadList();
            var stack=new VerticalStackLayout{Padding=new Thickness(28,22),Spacing=10};detail.Content=new ScrollView{Content=stack};
            var anonymous=Flag(survey,"anonymous");var closed=Flag(survey,"closed");var canAnswer=Flag(survey,"canAnswer")&&!closed;var answered=Flag(survey,"answered");var own=Field(survey,"ownerId")==me;
            var heading=Text(Field(survey,"title")??"",22,true);heading.AutomationId="mac-survey-title";stack.Add(heading);
            var deadline=Text(DateTimeOffset.FromUnixTimeMilliseconds(survey.GetProperty("deadline").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm",CultureInfo.InvariantCulture)+" 마감 · "+(closed?"종료":"진행 중"),12);deadline.TextColor=Muted;stack.Add(deadline);
            if(Field(survey,"description") is{Length:>0} about)stack.Add(Text(about,14));
            var notice=Text(anonymous?"익명 설문입니다. 누가 어떤 답을 했는지 게시자도, 서버 관리자도 알 수 없습니다. 제출한 답은 고칠 수 없습니다.":"기명 설문입니다. 작성자는 응답자와 답변을 확인할 수 있습니다.",12);
            stack.Add(DesktopPanel(notice,12,anonymous?Pale:Subtle));
            var fields=new List<Func<string>>();
            if(anonymous&&answered){var done=Text("응답을 제출했습니다. 익명 설문이라 제출한 내용은 다시 볼 수 없습니다.",14);stack.Add(done);}
            else
            {
                var questions=survey.GetProperty("questions").EnumerateArray().ToArray();
                string[] given=survey.TryGetProperty("answers",out var a)&&a.ValueKind==JsonValueKind.Array?a.EnumerateArray().Select(x=>x.GetString()??"").ToArray():[];
                for(var i=0;i<questions.Length;i++)
                {
                    var question=questions[i];var value=given.ElementAtOrDefault(i)??"";
                    var label=Text($"{i+1}. {Field(question,"text")}"+(Flag(question,"required")?" *":""),15,true);label.Margin=new Thickness(0,10,0,2);stack.Add(label);
                    if(Field(question,"kind")=="choice")
                    {
                        var group="survey-"+id+"-"+i;var options=new VerticalStackLayout{Spacing=2};var radios=new List<RadioButton>();
                        foreach(var option in question.GetProperty("options").EnumerateArray().Select(o=>o.GetString()??""))
                        {var radio=new RadioButton{Content=option,GroupName=group,IsChecked=option==value,IsEnabled=canAnswer,TextColor=Navy,FontSize=15};radios.Add(radio);options.Add(radio);}
                        stack.Add(options);fields.Add(()=>radios.FirstOrDefault(r=>r.IsChecked)?.Content as string??"");
                    }
                    else
                    {
                        var editor=new Editor{Text=value,MaxLength=2000,HeightRequest=90,IsEnabled=canAnswer,FontSize=15,TextColor=Navy,BackgroundColor=Colors.Transparent,AutoSize=EditorAutoSizeOption.Disabled};
                        stack.Add(DesktopPanel(editor,8));fields.Add(()=>editor.Text??"");
                    }
                }
                if(questions.Length>0&&!(anonymous&&answered))
                {
                    var answer=DesktopButton(anonymous?"익명으로 제출":answered?"응답 수정":"응답 저장",async()=>
                    {
                        if(anonymous&&!await DisplayAlertAsync("익명 설문","제출한 답은 고칠 수 없습니다. 제출할까요?","제출","취소"))return;
                        await MacSend(source,$"api/surveys/{id}/answers",new JsonObject{["answers"]=new JsonArray(fields.Select(f=>(JsonNode?)JsonValue.Create(f())).ToArray())});
                        await Show(id);feedback.Text=anonymous?"익명으로 제출했습니다.":"응답을 저장했습니다. 마감 전에는 수정할 수 있습니다.";
                    },true);
                    answer.AutomationId="mac-survey-answer";answer.HorizontalOptions=LayoutOptions.Start;answer.IsEnabled=canAnswer;answer.Margin=new Thickness(0,8,0,0);stack.Add(answer);
                }
            }
            if(!own)return;
            // The owner's results: a bar for each choice, the written answers below, names only on a named survey.
            var item=surveys.FirstOrDefault(s=>RowId(s)==id);var result=await Results(id,anonymous);if(current!=id||!Current(source,version))return;
            stack.Add(Rule());
            var resultHead=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8,Margin=new Thickness(0,8,0,0)};resultHead.Add(Text("응답 결과",18,true));
            var buttons=new HorizontalStackLayout{Spacing=6};resultHead.Add(buttons,1);
            if(!closed)buttons.Add(DesktopButton("설문 종료",async()=>
            {
                if(!await DisplayAlertAsync("설문 종료","설문을 종료하면 응답을 받거나 수정할 수 없습니다. 종료하시겠습니까?","설문 종료","취소"))return;
                await source.Send($"api/surveys/{id}/close");await Show(id);
            }));
            buttons.Add(DesktopButton("CSV 저장",async()=>
            {
                var latest=await Results(id,anonymous);var folder=System.IO.Path.Combine(FileSystem.CacheDirectory,"yy-share");Directory.CreateDirectory(folder);
                var file=System.IO.Path.Combine(folder,"설문결과-"+id[..8]+".csv");await File.WriteAllTextAsync(file,SurveyCsv(latest),new UTF8Encoding(true));
                await Share.Default.RequestAsync(new ShareFileRequest("설문 결과 저장",new ShareFile(file)));
            }));
            stack.Add(resultHead);
            var responses=result.GetProperty("responses").EnumerateArray().ToArray();var nameless=Flag(result,"anonymous");
            var answeredCount=Math.Max(item.ValueKind==JsonValueKind.Object?item.GetProperty("responseCount").GetInt32():0,responses.Length);
            var targetCount=item.ValueKind==JsonValueKind.Object?item.GetProperty("targetCount").GetInt32():answeredCount;
            var summary=Text($"응답 {answeredCount} / {targetCount}명"+(nameless?" · 응답자 이름은 저장되지 않습니다":"")+(responses.Length<answeredCount?$" · 이 Mac에 저장된 응답 {responses.Length}개만 보입니다":""),12);summary.TextColor=Muted;summary.AutomationId="mac-survey-summary";stack.Add(summary);
            if(nameless){var caution=Text("응답자가 적거나 결과를 실시간으로 지켜보면, 답변 내용과 응답 시점만으로 누구인지 짐작될 수 있습니다.",11);caution.TextColor=Faint;stack.Add(caution);}
            var asked=result.GetProperty("questions").EnumerateArray().ToArray();
            string Answer(JsonElement response,int index)=>response.GetProperty("answers").EnumerateArray().ElementAtOrDefault(index).ValueKind==JsonValueKind.String?response.GetProperty("answers")[index].GetString()!:"";
            for(var i=0;i<asked.Length;i++)
            {
                var question=asked[i];var block=new VerticalStackLayout{Spacing=6};block.Add(Text($"{i+1}. {Field(question,"text")}",14,true));
                var choice=Field(question,"kind")=="choice";
                if(choice)
                    foreach(var (option,count) in question.GetProperty("options").EnumerateArray().Select(o=>o.GetString()??"").Select(o=>(o,responses.Count(r=>Answer(r,i)==o))).OrderByDescending(x=>x.Item2))
                    {
                        var label=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};label.Add(Text(option,13));
                        var number=Text(responses.Length==0?"0명":$"{count}명 · {Math.Round(100.0*count/responses.Length)}%",11);number.TextColor=Muted;label.Add(number,1);block.Add(label);
                        var track=new Grid{HeightRequest=8,ColumnDefinitions={new(new GridLength(Math.Max(count,0),GridUnitType.Star)),new(new GridLength(responses.Length==0?1:Math.Max(0,responses.Length-count),GridUnitType.Star))}};
                        var back=new Border{BackgroundColor=Subtle,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=4}};track.Add(back);Grid.SetColumnSpan(back,2);
                        if(count>0)track.Add(new Border{BackgroundColor=Blue,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=4}});
                        block.Add(track);
                    }
                var written=responses.Where(r=>Answer(r,i).Length>0&&!(nameless&&choice)).ToArray();
                foreach(var response in written)block.Add(Text(nameless?"· "+Answer(response,i):$"{Field(response,"name")} · {Field(response,"department")}: {Answer(response,i)}",13));
                if(!choice&&written.Length==0){var none=Text("아직 응답이 없습니다.",12);none.TextColor=Faint;block.Add(none);}
                stack.Add(DesktopPanel(block,14));
            }
        }
        View CreateForm()
        {
            var stack=new VerticalStackLayout{Padding=new Thickness(28,22),Spacing=10};
            stack.Add(Text("새 설문",22,true));
            var title=new Entry{ClassId="school-field",Placeholder="제목",MaxLength=200,FontSize=16,AutomationId="mac-survey-new-title"};stack.Add(Text("제목",12,true));stack.Add(DesktopPanel(title,8));
            var about=new Editor{Placeholder="안내문 · 선택",MaxLength=2000,HeightRequest=70,FontSize=15,BackgroundColor=Colors.Transparent,AutoSize=EditorAutoSizeOption.Disabled};stack.Add(Text("안내문",12,true));stack.Add(DesktopPanel(about,8));
            var day=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=DateTime.UtcNow.AddHours(9).Date.AddDays(7),VerticalOptions=LayoutOptions.Center};
            var time=new TimePicker{ClassId="school-field",Format="HH:mm",Time=new TimeSpan(17,0,0),VerticalOptions=LayoutOptions.Center};
            stack.Add(Text("마감 · 한국 시간",12,true));stack.Add(DesktopPanel(new HorizontalStackLayout{Spacing=12,Children={day,time}},8));
            var chosen=new HashSet<string>(targets??[]);var everyone=new CheckBox{Color=Blue,VerticalOptions=LayoutOptions.Center};
            var who=Text("",13);who.VerticalOptions=LayoutOptions.Center;who.MaxLines=2;who.LineBreakMode=LineBreakMode.TailTruncation;
            void Who()=>who.Text=everyone.IsChecked?"전체 교직원":chosen.Count==0?"대상을 고르세요":string.Join(", ",people.Where(p=>chosen.Contains(RowId(p))).Select(p=>Field(p,"name")));
            var pick=DesktopButton("대상 선택",()=>MacPickPeople("설문 대상","선택 적용",people,chosen,()=>{Who();return Task.CompletedTask;}));pick.FontSize=14;pick.MinimumHeightRequest=34;
            everyone.CheckedChanged+=(_,_)=>{pick.IsEnabled=!everyone.IsChecked;Who();};
            var targetRow=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=10};targetRow.Add(pick);targetRow.Add(who,1);
            stack.Add(Text("대상",12,true));
            if(broadcast){var all=new HorizontalStackLayout{Spacing=4,Children={everyone,Mid(Text("전체 교직원",13))}};stack.Add(all);}
            stack.Add(targetRow);Who();
            var anonymous=new CheckBox{Color=Blue,VerticalOptions=LayoutOptions.Center,AutomationId="mac-survey-anonymous"};
            stack.Add(new HorizontalStackLayout{Spacing=4,Children={anonymous,Mid(Text("익명 설문",13,true))}});
            var anonymousNote=Text("응답은 게시자 PC로 옮겨진 뒤 서버에서 지워지고, 누가 어떤 답을 했는지 게시자도 서버 관리자도 알 수 없습니다. 제출한 답은 고칠 수 없고, 게시한 뒤에는 이 설정을 바꿀 수 없습니다. 결과는 게시한 PC에서만 볼 수 있습니다. 응답자가 적으면 답변 내용으로 누구인지 짐작될 수 있습니다.",11);
            anonymousNote.TextColor=Muted;stack.Add(anonymousNote);
            stack.Add(Text("문항",12,true));
            var questions=new VerticalStackLayout{Spacing=8};stack.Add(questions);
            var asked=new List<(Entry Text,Picker Kind,CheckBox Required,Editor Options,View Card)>();
            void AddQuestion()
            {
                if(asked.Count>=20){feedback.Text="문항은 최대 20개입니다.";return;}
                var panel=new VerticalStackLayout{Spacing=6};var card=DesktopPanel(panel,12,Subtle);
                var header=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};header.Add(Text("문항",13,true));
                var remove=DesktopButton("삭제");SubtleButton(remove);header.Add(remove,1);panel.Add(header);
                var text=new Entry{ClassId="school-field",Placeholder="질문 내용",MaxLength=500,FontSize=15,AutomationId="mac-survey-question"};panel.Add(DesktopPanel(text,6));
                var kind=new Picker{ClassId="school-field",ItemsSource=new[]{"객관식","주관식"},SelectedIndex=0,WidthRequest=130};
                var required=new CheckBox{IsChecked=true,Color=Blue,VerticalOptions=LayoutOptions.Center};
                panel.Add(new HorizontalStackLayout{Spacing=10,Children={DesktopPanel(kind,4),required,Mid(Text("필수 응답",13))}});
                var hint=Text("선택지 · 한 줄에 하나씩",11);hint.TextColor=Muted;panel.Add(hint);
                var options=new Editor{Text="찬성\n반대",HeightRequest=70,MaxLength=4200,FontSize=14,BackgroundColor=Colors.Transparent,AutoSize=EditorAutoSizeOption.Disabled};var optionsBox=DesktopPanel(options,6);panel.Add(optionsBox);
                kind.SelectedIndexChanged+=(_,_)=>hint.IsVisible=optionsBox.IsVisible=kind.SelectedIndex==0;
                var entry=(text,kind,required,options,(View)card);asked.Add(entry);questions.Add(card);
                remove.Clicked+=(_,_)=>{asked.Remove(entry);questions.Remove(card);};
            }
            AddQuestion();
            var more=DesktopButton("문항 추가",()=>{AddQuestion();return Task.CompletedTask;});more.HorizontalOptions=LayoutOptions.Start;stack.Add(more);
            var post=DesktopButton("게시",async()=>
            {
                if(day.Date is not{} date||time.Time is not{} at)throw new InvalidOperationException("마감일과 시간을 확인하세요.");
                if(everyone.IsChecked&&!await DisplayAlertAsync("설문","전체 교직원에게 설문을 보내시겠습니까?","전체 발송","취소"))return;
                var deadline=new DateTimeOffset(DateTime.SpecifyKind(date.Date+at,DateTimeKind.Unspecified),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
                var items=new JsonArray(asked.Select(q=>(JsonNode?)new JsonObject
                {
                    ["text"]=q.Text.Text?.Trim()??"",["kind"]=q.Kind.SelectedIndex==0?"choice":"text",["required"]=q.Required.IsChecked,
                    ["options"]=new JsonArray(q.Kind.SelectedIndex==0?(q.Options.Text??"").Split('\n').Select(o=>o.Trim()).Where(o=>o.Length>0).Select(o=>(JsonNode?)JsonValue.Create(o)).ToArray():[])
                }).ToArray());
                var made=await MacSend(source,"api/surveys",new JsonObject{["title"]=title.Text??"",["description"]=about.Text??"",["deadline"]=deadline,["questions"]=items,
                    ["targetIds"]=new JsonArray(chosen.Select(t=>(JsonNode?)JsonValue.Create(t)).ToArray()),["all"]=everyone.IsChecked,["anonymous"]=anonymous.IsChecked});
                await MacSurveys("sent",Field(made,"id"));feedback.Text="설문을 보냈습니다.";
            },true);
            post.AutomationId="mac-survey-post";post.HorizontalOptions=LayoutOptions.Start;
            var cancel=DesktopButton("취소",()=>MacSurveys(box));
            stack.Add(new HorizontalStackLayout{Spacing=8,Margin=new Thickness(0,8,0,0),Children={post,cancel}});
            return new ScrollView{Content=stack};
        }
        macSurveyChanged=async id=>{if(!Current(source,version))return;await LoadList();if(id==current&&detail.Content is ScrollView)await Show(id);};
        MacShowPane("survey",root);SelectRail("survey");desktopReader=null;
        if(create){detail.Content=CreateForm();await LoadList();return;}
        await LoadList();
        var first=select is not null&&surveys.Any(s=>RowId(s)==select)?select:surveys.Select(RowId).FirstOrDefault();
        if(first is not null)await Show(first);
        else{var empty=new VerticalStackLayout{Spacing=10,VerticalOptions=LayoutOptions.Center,HorizontalOptions=LayoutOptions.Center};empty.Add(EmptyArt("Survey"));var hint=Text("왼쪽에서 설문을 선택하거나 새 설문을 만드세요",13);hint.TextColor=Muted;empty.Add(hint);detail.Content=empty;}
    }
    static string SurveyCsv(JsonElement results)
    {
        static string Cell(string value){var lead=value.TrimStart();if(lead.StartsWith('=')||lead.StartsWith('+')||lead.StartsWith('-')||lead.StartsWith('@'))value="'"+value;return "\""+value.Replace("\"","\"\"")+"\"";}
        var anonymous=Flag(results,"anonymous");
        string[] who=anonymous?[]:["이름","부서","응답 시각"];
        var rows=new List<string>{string.Join(",",who.Concat(results.GetProperty("questions").EnumerateArray().Select(q=>Field(q,"text")??"")).Select(Cell))};
        foreach(var response in results.GetProperty("responses").EnumerateArray())
        {
            string[] person=anonymous?[]:[Field(response,"name")??"",Field(response,"department")??"",DateTimeOffset.FromUnixTimeMilliseconds(response.GetProperty("updatedAt").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture)];
            rows.Add(string.Join(",",person.Concat(response.GetProperty("answers").EnumerateArray().Select(a=>a.GetString()??"")).Select(Cell)));
        }
        return string.Join("\r\n",rows);
    }
}
