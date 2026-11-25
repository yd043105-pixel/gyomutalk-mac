using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Controls.Shapes;

namespace SchoolMessenger.Mobile;

// 수강신청 for the curriculum duty, as the Windows CourseWindow: rounds and their results, the student roster, and the
// connection to the school's Google Apps Script. Students answer on the Google page; names stay locked behind each link.
public sealed partial class MessengerPage
{
    static readonly string[] CourseTerms=["1·2학기","1학기","2학기"];
    string macCourseTab="rounds";string macCourseView="counts";int macRosterGrade,macRosterClass;
    static string CourseDay(long closes)=>DateTimeOffset.FromUnixTimeMilliseconds(closes-1).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd",CultureInfo.InvariantCulture);
    static string CourseWhen(long at)=>DateTimeOffset.FromUnixTimeMilliseconds(at).ToOffset(TimeSpan.FromHours(9)).ToString("M.d HH:mm",CultureInfo.InvariantCulture);
    static string[][]? CoursePicks(JsonElement student)=>student.TryGetProperty("picks",out var p)&&p.ValueKind==JsonValueKind.Array?p.EnumerateArray().Select(g=>g.EnumerateArray().Select(c=>c.GetString()??"").ToArray()).ToArray():null;
    static string[] CourseClosed(JsonElement group)=>group.TryGetProperty("closed",out var c)&&c.ValueKind==JsonValueKind.Array?c.EnumerateArray().Select(x=>x.GetString()??"").ToArray():[];
    async Task MacCourses(string? tab=null)
    {
        var source=api!;var version=epoch;if(tab is not null)macCourseTab=tab;
        var root=new Grid{RowDefinitions={new(GridLength.Auto),new(GridLength.Star)},BackgroundColor=Surface};
        var toolbar=new Grid{Padding=new Thickness(28,18,24,12),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=12};
        var heading=new VerticalStackLayout{Spacing=3};heading.Add(Text("수강신청",22,true));var meta=Text("학교 구글 앱스스크립트로 받고 교육과정 담당 교사가 결과를 봅니다",11);meta.TextColor=Muted;heading.Add(meta);toolbar.Add(heading);
        var tabs=new HorizontalStackLayout{Spacing=6,VerticalOptions=LayoutOptions.Center};
        foreach(var (key,label) in new[]{("rounds","신청 · 결과"),("roster","학생 명단"),("connect","구글 연결")}){var b=DesktopButton(label,()=>MacCourses(key));b.AutomationId="mac-course-"+key;Chip(b,key==macCourseTab);tabs.Add(b);}
        toolbar.Add(tabs,1);root.Add(toolbar);var rule=Rule();rule.VerticalOptions=LayoutOptions.End;root.Add(rule);
        var body=new VerticalStackLayout{Padding=new Thickness(28,16),Spacing=10};root.Add(new ScrollView{Content=body},0,1);
        var say=Text("",13);say.TextColor=Muted;
        void Say(string text,bool problem){say.Text=text;say.TextColor=problem?Danger:Muted;}
        // One action at a time; a slow one says what it is doing, and a failure stays on screen with what to do next.
        async Task Do(Func<Task> action,string? doing=null)
        {
            if(doing is not null)Say(doing,false);
            try{await action();}catch(Exception error)when(error is InvalidOperationException or HttpRequestException or IOException or JsonException or TaskCanceledException){Say(error is HttpRequestException?"학교 서버에 연결하지 못했습니다.":error.Message,true);}
        }
        Button Act(string label,Func<Task> action,bool primary=false,string? doing=null){var b=DesktopButton(label,null,primary);b.FontSize=14;b.MinimumHeightRequest=34;b.Clicked+=async(_,_)=>{b.IsEnabled=false;try{await Do(action,doing);}finally{b.IsEnabled=true;}};return b;}
        void Start(){body.Clear();body.Add(say);}
        async Task Share(string name,string header,IEnumerable<string[]> rows,string done)
        {
            static string Cell(string text)=>text.IndexOfAny([',','"','\n','\r'])>=0?"\""+text.Replace("\"","\"\"")+"\"":text;
            var folder=System.IO.Path.Combine(FileSystem.CacheDirectory,"yy-share");Directory.CreateDirectory(folder);
            var file=System.IO.Path.Combine(folder,string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())));
            // The byte order mark lets Excel read Korean correctly.
            await File.WriteAllTextAsync(file,header+"\r\n"+string.Join("\r\n",rows.Select(r=>string.Join(",",r.Select(Cell))))+"\r\n",new UTF8Encoding(true));
            await Microsoft.Maui.ApplicationModel.DataTransfer.Share.Default.RequestAsync(new ShareFileRequest(name,new ShareFile(file)));Say(done,false);
        }
        async Task Rounds()
        {
            Start();
            var setup=await source.Get("api/courses/setup");var rounds=(await source.Get("api/courses/rounds")).EnumerateArray().ToArray();if(!Current(source,version))return;
            if(Flag(setup,"connected")&&!Flag(setup,"scriptCurrent"))body.Add(DesktopPanel(Text("교무톡이 업데이트되어 구글 스크립트를 새로 붙여넣어야 합니다. ‘구글 연결’에서 코드를 다시 복사해 새 버전으로 배포하세요.",13),12,SwapTangle));
            if(!Flag(setup,"connected")){body.Add(DesktopEmptyState("먼저 구글을 연결하세요","학생 신청 화면은 학교 구글 계정의 앱스스크립트에서 열립니다. 처음 한 번만 연결하면 됩니다."));var go=Act("구글 연결하기",()=>MacCourses("connect"),true);go.HorizontalOptions=LayoutOptions.Center;body.Add(go);return;}
            var add=Act("새 신청 만들기",()=>Editor(null),true);add.HorizontalOptions=LayoutOptions.Start;add.AutomationId="mac-course-new";body.Add(add);
            if(rounds.Length==0){body.Add(DesktopEmptyState("아직 만든 신청이 없습니다",setup.GetProperty("grades").GetArrayLength()==0?"‘학생 명단’에 나이스 명단을 먼저 올린 뒤, 선택군과 과목을 넣어 신청을 만드세요.":"선택군과 과목을 넣어 신청을 만들고 학생 링크를 보내세요."));return;}
            foreach(var round in rounds)
            {
                var published=round.GetProperty("publishedAt").ValueKind==JsonValueKind.Number;var closes=round.GetProperty("closes").GetInt64();
                var row=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=12};
                var words=new VerticalStackLayout{Spacing=3};var title=Text(Field(round,"title")??"",15,true);title.MaxLines=1;title.LineBreakMode=LineBreakMode.TailTruncation;words.Add(title);
                var info=Text($"{round.GetProperty("grade").GetInt32()}학년 · {CourseDay(closes)}까지",12);info.TextColor=Muted;words.Add(info);row.Add(words);
                var state=!published?"링크 보내기 전":closes<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()?"마감":"신청 받는 중";
                var side=Text(state+(published?$" · 제출 {round.GetProperty("submitted").GetInt32()} / {round.GetProperty("students").GetInt32()}명":""),13,true);side.TextColor=state=="신청 받는 중"?Success:Muted;side.VerticalOptions=LayoutOptions.Center;row.Add(side,1);
                var card=DesktopPanel(row,14);card.AutomationId="mac-course-round";var id=RowId(round);
                var tap=new TapGestureRecognizer();tap.Tapped+=async(_,_)=>await Do(()=>ShowRound(id,published),published?"구글에서 제출 내용을 가져오는 중…":null);card.GestureRecognizers.Add(tap);body.Add(card);
            }
        }
        async Task ShowRound(string id,bool pull)
        {
            JsonElement results;
            if(pull)
            {
                try{results=await source.Send($"api/courses/rounds/{id}/refresh");}
                catch(MessengerApiRefusal e){results=await source.Get($"api/courses/rounds/{id}/results");Draw(results);Say(e.Message+" 마지막으로 가져온 내용을 보여 줍니다.",true);return;}
            }
            else results=await source.Get($"api/courses/rounds/{id}/results");
            if(Current(source,version)){Draw(results);if(pull)Say("",false);}
        }
        string[] Lost(JsonElement round,JsonElement student)
        {
            var picks=CoursePicks(student);if(picks is null)return [];
            var groups=round.GetProperty("groups").EnumerateArray().ToArray();
            return groups.SelectMany((g,i)=>i<picks.Length?picks[i].Where(c=>CourseClosed(g).Contains(c)):Enumerable.Empty<string>()).ToArray();
        }
        void ByClass(VerticalStackLayout host,IEnumerable<JsonElement> students,Func<JsonElement,string>? note=null)
        {
            foreach(var room in students.GroupBy(s=>s.GetProperty("classNo").GetInt32()).OrderBy(r=>r.Key))
            {
                var line=new Grid{ColumnDefinitions={new(new GridLength(80)),new(GridLength.Star)},Margin=new Thickness(0,8,0,0)};
                var label=Text($"{room.Key}반 {room.Count()}명",13,true);label.TextColor=Muted;line.Add(label);
                line.Add(Text(string.Join("   ",room.OrderBy(s=>s.GetProperty("number").GetInt32()).Select(s=>$"{s.GetProperty("number").GetInt32()} {Field(s,"name")}"+(note?.Invoke(s)??""))),13),1);
                host.Add(line);
            }
        }
        async Task SaveLinks(JsonElement round,JsonElement[]? only=null)
        {
            var links=(await source.Get($"api/courses/rounds/{RowId(round)}/links")).EnumerateArray().ToArray();
            if(only is not null)links=links.Where(l=>only.Any(s=>s.GetProperty("classNo").GetInt32()==l.GetProperty("classNo").GetInt32()&&s.GetProperty("number").GetInt32()==l.GetProperty("number").GetInt32())).ToArray();
            await Share($"{Field(round,"title")} 학생 링크{(only is null?"":$" {links.Length}명")}.csv","학년,반,번호,이름,링크",
                links.Select(l=>new[]{l.GetProperty("grade").GetInt32().ToString(CultureInfo.InvariantCulture),l.GetProperty("classNo").GetInt32().ToString(CultureInfo.InvariantCulture),l.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture),Field(l,"name")??"",Field(l,"link")??""}),
                "링크 목록을 저장했습니다. 학생 한 명마다 자기 링크만 받도록 나눠 주세요.");
        }
        void Draw(JsonElement results)
        {
            Start();
            var round=results.GetProperty("round");var roundId=RowId(round);var groups=round.GetProperty("groups").EnumerateArray().ToArray();
            var students=results.GetProperty("students").EnumerateArray().ToArray();var answered=students.Count(s=>CoursePicks(s) is not null);var total=students.Length;
            var published=round.GetProperty("publishedAt").ValueKind==JsonValueKind.Number;
            var back=DesktopButton("‹  신청 목록으로",Rounds);SubtleButton(back);back.HorizontalOptions=LayoutOptions.Start;body.Add(back);
            body.Add(Text(Field(round,"title")??"",20,true));
            var info=Text($"{round.GetProperty("grade").GetInt32()}학년 · {CourseDay(round.GetProperty("closes").GetInt64())}까지"+(round.GetProperty("pulledAt").ValueKind==JsonValueKind.Number?$" · {CourseWhen(round.GetProperty("pulledAt").GetInt64())}에 가져옴":""),13);info.TextColor=Muted;body.Add(info);
            var tools=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap};
            void Tool(Button b){b.Margin=new Thickness(0,0,8,8);tools.Add(b);}
            Tool(Act(published?"링크 다시 보내기":"학생 링크 만들기",async()=>
            {
                var sent=await source.Send($"api/courses/rounds/{roundId}/publish");await ShowRound(roundId,false);
                Say($"학생 {sent.GetProperty("students").GetInt32()}명의 링크를 구글에 올렸습니다. ‘링크 목록 저장’으로 담임 선생님께 나눠 주세요.",false);
            },!published,"구글에 신청 내용을 올리는 중…"));
            if(published)
            {
                Tool(Act("결과 새로고침",()=>ShowRound(roundId,true),false,"구글에서 제출 내용을 가져오는 중…"));
                Tool(Act("링크 목록 저장",()=>SaveLinks(round)));
                Tool(Act("결과 저장",()=>Share($"{Field(round,"title")} 결과.csv","학년,반,번호,이름,제출,"+string.Join(",",groups.Select(g=>Field(g,"name")??""))+",제출 시각",
                    students.Select(s=>{var picks=CoursePicks(s);return new[]{round.GetProperty("grade").GetInt32().ToString(CultureInfo.InvariantCulture),s.GetProperty("classNo").GetInt32().ToString(CultureInfo.InvariantCulture),s.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture),Field(s,"name")??"",picks is null?"미제출":"제출"}
                        .Concat(groups.Select((g,i)=>picks is null||i>=picks.Length?"":string.Join(", ",picks[i].Select(c=>CourseClosed(g).Contains(c)?c+"(폐강)":c))))
                        .Append(s.TryGetProperty("submittedAt",out var at)&&at.ValueKind==JsonValueKind.Number?CourseWhen(at.GetInt64()):"").ToArray();}),"결과를 저장했습니다.")));
            }
            Tool(Act("수정",()=>Editor(round)));
            var remove=Act("삭제",async()=>
            {
                if(!await DisplayAlertAsync("신청 삭제",$"‘{Field(round,"title")}’ 신청을 지울까요?\n\n학생 링크가 더 이상 열리지 않고, 받은 제출 내용도 함께 지워집니다. 되돌릴 수 없으니 필요하면 먼저 ‘결과 저장’을 누르세요.","신청 삭제","취소"))return;
                await source.Send($"api/courses/rounds/{roundId}/delete");await Rounds();Say("신청을 지웠습니다.",false);
            },false,"구글에서 신청을 내리는 중…");remove.TextColor=Danger;Tool(remove);
            body.Add(tools);
            if(!published){body.Add(DesktopEmptyState("아직 학생 링크를 만들지 않았습니다",$"‘학생 링크 만들기’를 누르면 {round.GetProperty("grade").GetInt32()}학년 학생마다 링크가 만들어져 구글에 올라갑니다."));return;}
            var redo=students.Where(s=>Lost(round,s).Length>0).ToArray();
            var choices=new List<(string Key,string Label)>{("counts",$"과목별 인원 · 제출 {answered} / {total}명"),("waiting",$"미제출 {total-answered}명")};
            if(redo.Length>0)choices.Add(("redo",$"폐강 재선택 {redo.Length}명"));
            if(macCourseView=="redo"&&redo.Length==0)macCourseView="counts";
            var views=new HorizontalStackLayout{Spacing=6};var content=new VerticalStackLayout{Spacing=10};
            foreach(var (key,label) in choices){var chip=DesktopButton(label,()=>{macCourseView=key;Draw(results);return Task.CompletedTask;});Chip(chip,key==macCourseView);chip.AutomationId="mac-course-view-"+key;views.Add(chip);}
            body.Add(views);body.Add(content);
            if(macCourseView=="waiting")
            {
                var waiting=students.Where(s=>CoursePicks(s) is null).ToArray();var list=new VerticalStackLayout{Spacing=4};
                var top=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};top.Add(Text(waiting.Length==0?"모두 제출했습니다":$"미제출 학생 {waiting.Length}명",15,true));
                if(waiting.Length>0)top.Add(Act("미제출 학생 링크만 저장",()=>SaveLinks(round,waiting)),1);list.Add(top);
                ByClass(list,waiting,s=>Flag(s,"linked")?"":"(링크 없음)");content.Add(DesktopPanel(list,16));return;
            }
            if(macCourseView=="redo")
            {
                var list=new VerticalStackLayout{Spacing=4};var top=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};
                top.Add(Text($"폐강으로 다시 골라야 하는 학생 {redo.Length}명",15,true));top.Add(Act("이 학생들 링크만 저장",()=>SaveLinks(round,redo)),1);list.Add(top);
                var hint=Text("이 학생들이 자기 링크를 다시 열면 폐강 안내가 나오고, 그 선택군만 다시 고르면 됩니다. 마감일이 지났으면 ‘수정’에서 마감일을 늘리세요.",12);hint.TextColor=Muted;list.Add(hint);
                ByClass(list,redo,s=>$"({string.Join(", ",Lost(round,s))})");content.Add(DesktopPanel(list,16));return;
            }
            var tallies=results.GetProperty("groups").EnumerateArray().ToArray();
            for(var index=0;index<tallies.Length;index++)
            {
                var group=tallies[index];var term=index<groups.Length&&groups[index].TryGetProperty("semester",out var sem)?sem.GetInt32():0;string[] shut=index<groups.Length?CourseClosed(groups[index]):[];
                var stack=new VerticalStackLayout{Spacing=6};stack.Add(Text(term==0?Field(group,"name")??"":$"{Field(group,"name")} · {CourseTerms[Math.Clamp(term,0,2)]}",15,true));
                var courses=group.GetProperty("courses").EnumerateArray().ToArray();
                var hint=Text($"{courses.Length}과목 중 {group.GetProperty("pick").GetInt32()}과목 선택"+(shut.Length>0?$" · 폐강 {string.Join(", ",shut)}":""),12);hint.TextColor=Muted;stack.Add(hint);
                // Bars are out of the students who answered, so a long bar means most of the grade chose it.
                var most=Math.Max(1,answered);var groupIndex=index;
                foreach(var course in courses.OrderByDescending(c=>c.GetProperty("count").GetInt32()))
                {
                    var name=Field(course,"name")??"";var count=course.GetProperty("count").GetInt32();var closed=shut.Contains(name);
                    var line=new Grid{ColumnDefinitions={new(new GridLength(190)),new(GridLength.Star),new(new GridLength(56)),new(new GridLength(96))},ColumnSpacing=8};
                    var label=Text(closed?name+" · 폐강":name,13);label.TextColor=closed?Danger:Navy;label.MaxLines=1;label.LineBreakMode=LineBreakMode.TailTruncation;label.VerticalOptions=LayoutOptions.Center;line.Add(label);
                    var track=new Grid{HeightRequest=10,VerticalOptions=LayoutOptions.Center,ColumnDefinitions={new(new GridLength(Math.Max(count,0),GridUnitType.Star)),new(new GridLength(Math.Max(0,most-count),GridUnitType.Star))}};
                    var bar=new Border{BackgroundColor=Subtle,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=5}};track.Add(bar);Grid.SetColumnSpan(bar,2);
                    if(count>0)track.Add(new Border{BackgroundColor=closed?Faint:Blue,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=5}});line.Add(track,1);
                    var number=Text(count+"명",13,true);number.HorizontalTextAlignment=TextAlignment.End;number.VerticalOptions=LayoutOptions.Center;line.Add(number,2);
                    var toggle=Act(closed?"폐강 취소":"폐강",async()=>
                    {
                        if(!closed&&!await DisplayAlertAsync("폐강",$"‘{name}’을(를) 폐강할까요?\n\n이 과목을 고른 학생 {count}명은 자기 링크를 다시 열면 이 선택군을 다시 골라야 합니다. 학생 화면에 바로 반영됩니다.","폐강","취소"))return;
                        await MacSend(source,$"api/courses/rounds/{roundId}/closed",new JsonObject{["group"]=groupIndex,["course"]=name,["closed"]=!closed});
                        await ShowRound(roundId,false);Say(closed?$"‘{name}’ 폐강을 취소했습니다. 학생 화면에 다시 나옵니다.":$"‘{name}’을(를) 폐강했습니다. ‘폐강 재선택’에서 다시 골라야 하는 학생을 볼 수 있습니다.",false);
                    },false,"학생 화면에 반영하는 중…");
                    SubtleButton(toggle);if(!closed)toggle.TextColor=Muted;line.Add(toggle,3);stack.Add(line);
                }
                content.Add(DesktopPanel(stack,16));
            }
            var later=Text("함께 듣는 학생 수, 분반 편성, 확인서 인쇄는 Windows 교무톡의 수강신청에서 볼 수 있습니다.",11);later.TextColor=Faint;content.Add(later);
        }
        async Task Editor(JsonElement? round)
        {
            Start();var setup=await source.Get("api/courses/setup");if(!Current(source,version))return;
            var detail=round;var published=detail is{} r0&&r0.GetProperty("publishedAt").ValueKind==JsonValueKind.Number;
            body.Add(Text(detail is null?"새 신청 만들기":"신청 수정",20,true));
            Label Caption(string text){var c=Text(text,13);c.TextColor=Muted;c.Margin=new Thickness(0,8,0,0);return c;}
            body.Add(Caption("신청 이름"));
            var title=new Entry{ClassId="school-field",Text=detail is{} r1?Field(r1,"title"):$"{DateTime.Today.Year+1}학년도 선택과목 신청",MaxLength=80,FontSize=15,AutomationId="mac-course-title"};body.Add(DesktopPanel(title,6));
            var counts=setup.GetProperty("grades").EnumerateArray().ToDictionary(g=>g.GetProperty("grade").GetInt32(),g=>g.GetProperty("count").GetInt32());
            var grade=new Picker{ClassId="school-field",ItemsSource=new[]{1,2,3}.Select(g=>$"{g}학년 ({counts.GetValueOrDefault(g)}명)").ToArray(),SelectedIndex=(detail is{} r2?r2.GetProperty("grade").GetInt32():counts.Keys.DefaultIfEmpty(1).Min())-1,TextColor=Navy};
            var closes=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=detail is{} r3?DateTimeOffset.FromUnixTimeMilliseconds(r3.GetProperty("closes").GetInt64()-1).ToOffset(TimeSpan.FromHours(9)).Date:DateTime.Today.AddDays(7),MinimumDate=DateTime.Today.AddDays(-1)};
            var pair=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=12};
            var left=new VerticalStackLayout{Children={Caption("신청하는 학년"),DesktopPanel(grade,4)}};var right=new VerticalStackLayout{Children={Caption("마감일 (그날 밤 12시까지)"),DesktopPanel(closes,4)}};
            pair.Add(left);pair.Add(right,1);body.Add(pair);
            body.Add(Caption("선택군 · 과목 (과목은 한 줄에 하나씩)"));
            var groups=new VerticalStackLayout{Spacing=10};body.Add(groups);
            var parts=new List<(Entry Name,Picker Term,Picker Pick,Editor Courses,View Card,JsonElement? Kinds)>();
            void AddGroup(JsonElement? g)
            {
                var card=new VerticalStackLayout{Spacing=8};
                var name=new Entry{ClassId="school-field",Text=g is{} a?Field(a,"name"):$"선택군 {(char)('A'+parts.Count)}",MaxLength=40,FontSize=14};
                var term=new Picker{ClassId="school-field",ItemsSource=CourseTerms,SelectedIndex=g is{} b&&b.TryGetProperty("semester",out var s)?s.GetInt32():0,TextColor=Navy};
                var pick=new Picker{ClassId="school-field",ItemsSource=Enumerable.Range(1,10).Select(n=>n+"과목 고르기").ToArray(),SelectedIndex=(g is{} c?c.GetProperty("pick").GetInt32():1)-1,TextColor=Navy};
                var drop=DesktopButton("빼기");drop.FontSize=13;drop.MinimumHeightRequest=30;
                var top=new Grid{ColumnDefinitions={new(GridLength.Star),new(new GridLength(110)),new(new GridLength(130)),new(GridLength.Auto)},ColumnSpacing=8};
                top.Add(DesktopPanel(name,4));top.Add(DesktopPanel(term,4),1);top.Add(DesktopPanel(pick,4),2);top.Add(drop,3);card.Add(top);
                var courses=new Editor{Text=g is{} d?string.Join("\n",d.GetProperty("courses").EnumerateArray().Select(x=>x.GetString())):"",HeightRequest=110,FontSize=14,BackgroundColor=Colors.Transparent,AutoSize=EditorAutoSizeOption.Disabled,Placeholder="과목 이름을 한 줄에 하나씩"};
                card.Add(DesktopPanel(courses,6));
                var shell=DesktopPanel(card,14,Subtle);groups.Add(shell);
                JsonElement? kinds=g is{} e&&e.TryGetProperty("kinds",out var k)&&k.ValueKind==JsonValueKind.Object?k:null;
                var entry=(name,term,pick,courses,(View)shell,kinds);parts.Add(entry);
                drop.Clicked+=(_,_)=>{if(parts.Count>1){parts.Remove(entry);groups.Remove(shell);}};
            }
            JsonElement?[] existing=detail is{} r4?r4.GetProperty("groups").EnumerateArray().Select(x=>(JsonElement?)x).ToArray():[null];
            foreach(var g in existing)AddGroup(g);
            var more=DesktopButton("+ 선택군 추가",()=>{AddGroup(null);return Task.CompletedTask;});more.HorizontalOptions=LayoutOptions.Start;body.Add(more);
            if(published){var note=Text("저장하면 바뀐 내용이 학생 화면에 바로 반영됩니다. 제출한 학생이 있으면 학년·선택군·과목은 바꿀 수 없고, 신청 이름·마감일·학기만 바꿀 수 있습니다.",12);note.TextColor=Muted;body.Add(note);}
            var save=Act("저장",async()=>
            {
                if(closes.Date is not{} day)throw new InvalidOperationException("마감일을 고르세요.");
                var list=new JsonArray(parts.Select(p=>
                {
                    var names=(p.Courses.Text??"").Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
                    // The subject area and type set on Windows stay with the courses kept here.
                    var kinds=new JsonObject();
                    if(p.Kinds is{} known)foreach(var item in known.EnumerateObject().Where(item=>names.Contains(item.Name)))kinds[item.Name]=JsonNode.Parse(item.Value.GetRawText());
                    return (JsonNode?)new JsonObject{["name"]=p.Name.Text??"",["pick"]=p.Pick.SelectedIndex+1,["courses"]=new JsonArray(names.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["semester"]=Math.Max(0,p.Term.SelectedIndex),["kinds"]=kinds};
                }).ToArray());
                var request=new JsonObject{["title"]=title.Text??"",["grade"]=grade.SelectedIndex+1,["closes"]=day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),["groups"]=list};
                var saved=await MacSend(source,detail is{} r5?$"api/courses/rounds/{RowId(r5)}":"api/courses/rounds",request);
                await ShowRound(RowId(saved),false);Say(!published?"저장했습니다. ‘학생 링크 만들기’를 누르면 학생 링크가 만들어집니다.":"저장하고 학생 화면에 반영했습니다.",false);
            },true,published?"학생 화면에 반영하는 중…":null);
            save.AutomationId="mac-course-save";
            var cancel=Act("취소",()=>detail is{} r6?ShowRound(RowId(r6),false):Rounds());
            body.Add(new HorizontalStackLayout{Spacing=8,Children={save,cancel}});
        }
        async Task Roster()
        {
            Start();var students=(await source.Get("api/courses/students")).EnumerateArray().ToArray();if(!Current(source,version))return;
            var intro=Text("나이스에서 받은 학생 명단(.xlsx, .csv)을 올리거나, 엑셀에서 머리글(학년·반·번호·성명)까지 복사해 붙여넣으세요. 올린 학년의 명단만 바뀝니다.",13);intro.TextColor=Muted;body.Add(intro);
            var paste=new Editor{HeightRequest=110,FontSize=13,BackgroundColor=Colors.Transparent,AutoSize=EditorAutoSizeOption.Disabled,Placeholder="엑셀에서 복사한 명단을 여기에 붙여넣기"};
            var pasteBox=DesktopPanel(paste,6);pasteBox.IsVisible=false;
            var read=Act("붙여넣은 명단 읽기",async()=>await Confirm(await MacSend(source,"api/courses/students/read",new JsonObject{["text"]=paste.Text??""})),true,"명단을 읽는 중…");read.IsVisible=false;
            var open=Act("붙여넣기로 넣기",()=>{pasteBox.IsVisible=read.IsVisible=true;paste.Focus();return Task.CompletedTask;});
            var upload=Act("나이스 명단 올리기",async()=>
            {
                preserveComposerOnResume=true;
                var file=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="나이스에서 받은 학생 명단 선택"});if(file is null)return;
                if(System.IO.Path.GetExtension(file.FileName).ToLowerInvariant() is not(".xlsx" or ".csv" or ".xls"))throw new InvalidOperationException("나이스 학생 명단(.xlsx, .csv)을 고르세요.");
                Say("명단을 읽는 중…",false);
                await using var stream=await file.OpenReadAsync();using var form=new MultipartFormDataContent();form.Add(new StreamContent(stream),"file",file.FileName);
                await Confirm(await source.Send("api/courses/students/read",content:form));
            },true);
            body.Add(new HorizontalStackLayout{Spacing=8,Children={upload,open}});body.Add(pasteBox);body.Add(read);
            if(students.Length==0){body.Add(DesktopEmptyState("학생 명단이 비어 있습니다","나이스 명단을 올리면 학년·반·번호·이름이 들어갑니다."));AddForm();return;}
            var grades=students.Select(s=>s.GetProperty("grade").GetInt32()).Distinct().Order().ToArray();if(!grades.Contains(macRosterGrade))macRosterGrade=grades[0];
            var classes=students.Where(s=>s.GetProperty("grade").GetInt32()==macRosterGrade).Select(s=>s.GetProperty("classNo").GetInt32()).Distinct().Order().ToArray();if(!classes.Contains(macRosterClass))macRosterClass=classes[0];
            FlexLayout Chips(IEnumerable<(string Label,bool On,Action Pick)> items)
            {
                var row=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap};
                foreach(var (label,on,pick) in items){var chip=DesktopButton(label,async()=>{pick();await Roster();});Chip(chip,on);chip.Margin=new Thickness(0,0,6,6);row.Add(chip);}
                return row;
            }
            body.Add(Chips(grades.Select(g=>($"{g}학년 {students.Count(s=>s.GetProperty("grade").GetInt32()==g)}명",g==macRosterGrade,(Action)(()=>{macRosterGrade=g;macRosterClass=0;})))));
            body.Add(Chips(classes.Select(c=>($"{c}반 {students.Count(s=>s.GetProperty("grade").GetInt32()==macRosterGrade&&s.GetProperty("classNo").GetInt32()==c)}",c==macRosterClass,(Action)(()=>macRosterClass=c)))));
            var table=new VerticalStackLayout{Spacing=0};
            foreach(var s in students.Where(s=>s.GetProperty("grade").GetInt32()==macRosterGrade&&s.GetProperty("classNo").GetInt32()==macRosterClass))
            {
                var name=Field(s,"name")??"";var number=s.GetProperty("number").GetInt32();
                var line=new Grid{ColumnDefinitions={new(new GridLength(64)),new(GridLength.Star),new(GridLength.Auto)},Padding=new Thickness(16,6,12,6)};
                var place=Text($"{number}번",13);place.TextColor=Muted;place.VerticalOptions=LayoutOptions.Center;line.Add(place);
                var who=Text(name,13,true);who.VerticalOptions=LayoutOptions.Center;line.Add(who,1);
                var id=RowId(s);
                var drop=Act("빼기",async()=>
                {
                    if(!await DisplayAlertAsync("학생 빼기",$"{macRosterGrade}학년 {macRosterClass}반 {number}번 {name} 학생을 명단에서 뺄까요?\n\n받은 링크가 더 이상 열리지 않고, 제출 내용도 함께 지워집니다.","빼기","취소"))return;
                    await source.Send($"api/courses/students/{id}/delete");await Roster();Say(name+" 학생을 뺐습니다. 이미 보낸 신청에서도 이 학생 링크와 제출 내용을 지웠습니다.",false);
                });drop.FontSize=13;drop.MinimumHeightRequest=28;line.Add(drop,2);
                table.Add(line);table.Add(Rule());
            }
            body.Add(DesktopPanel(table,0));AddForm();
        }
        void AddForm()
        {
            Entry Box(string label,double width,string text=""){var box=new Entry{ClassId="school-field",Placeholder=label,Text=text,WidthRequest=width,FontSize=14};SemanticProperties.SetDescription(box,label);return box;}
            var grade=Box("학년",56,macRosterGrade>0?macRosterGrade.ToString(CultureInfo.InvariantCulture):"");var room=Box("반",56,macRosterClass>0?macRosterClass.ToString(CultureInfo.InvariantCulture):"");var number=Box("번호",56);var name=Box("이름",150);
            var line=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap,AlignItems=Microsoft.Maui.Layouts.FlexAlignItems.Center};
            var title=Text("한 명 더하기",13,true);title.TextColor=Muted;title.Margin=new Thickness(0,0,12,0);line.Add(title);
            foreach(var (box,unit) in new[]{(grade,"학년"),(room,"반"),(number,"번"),(name,"")}){var shell=DesktopPanel(box,2);shell.Margin=new Thickness(0,0,4,6);line.Add(shell);if(unit.Length>0){var u=Text(unit,13);u.TextColor=Muted;u.Margin=new Thickness(0,0,10,6);line.Add(u);}}
            var add=Act("더하기",async()=>
            {
                static int Number(Entry box)=>int.TryParse(box.Text?.Trim(),out var n)?n:0;
                await MacSend(source,"api/courses/students",new JsonObject{["grade"]=Number(grade),["classNo"]=Number(room),["number"]=Number(number),["name"]=name.Text??""});
                macRosterGrade=Number(grade);macRosterClass=Number(room);var added=name.Text?.Trim();await Roster();Say(added+" 학생을 더했습니다. 이미 링크를 보낸 신청이 있으면 이 학생 링크도 만들었으니 ‘링크 목록 저장’으로 받아 전해 주세요.",false);
            });
            line.Add(add);body.Add(line);
        }
        async Task Confirm(JsonElement read)
        {
            var rows=read.GetProperty("students").EnumerateArray().ToArray();if(rows.Length==0)throw new InvalidOperationException("명단에서 학생을 찾지 못했습니다. 학년·반·번호·성명 머리글이 있는지 확인하세요.");
            int N(JsonElement s,string name)=>s.GetProperty(name).GetInt32();
            var byGrade=rows.GroupBy(s=>N(s,"grade")).Select(g=>$"{g.Key}학년 {g.Count()}명({g.Min(s=>N(s,"classNo"))}~{g.Max(s=>N(s,"classNo"))}반)");
            var grades=rows.Select(s=>N(s,"grade")).Distinct().Order().ToArray();var skipped=read.GetProperty("skipped").GetInt32();var first=rows[0];
            var text=$"읽은 명단: {string.Join(", ",byGrade)}\n읽은 칸: {Field(read,"columns")}"+(skipped>0?$"\n학생으로 읽지 않은 줄: {skipped}줄(합계·빈칸 등)":"")+
                $"\n첫 학생: {N(first,"grade")}학년 {N(first,"classNo")}반 {N(first,"number")}번 {Field(first,"name")}"+
                $"\n\n{string.Join("·",grades)}학년 명단을 이 내용으로 바꿀까요?\n반·번호·이름이 그대로인 학생은 받은 링크와 제출 내용이 그대로 남고, 여기에 없는 학생은 명단에서 빠집니다.";
            Say("",false);
            if(!await DisplayAlertAsync("명단 넣기",text,"바꾸기","취소"))return;
            var done=await MacSend(source,"api/courses/students/import",new JsonObject{["grades"]=new JsonArray(grades.Select(g=>(JsonNode?)g).ToArray()),["students"]=JsonNode.Parse(read.GetProperty("students").GetRawText())});
            macRosterGrade=grades[0];macRosterClass=0;await Roster();
            Say($"명단을 넣었습니다. 새로 들어온 학생 {done.GetProperty("added").GetInt32()}명, 빠진 학생 {done.GetProperty("removed").GetInt32()}명. 이미 보낸 신청에도 바로 반영했습니다.",false);
        }
        async Task Connect()
        {
            Start();var setup=await source.Get("api/courses/setup");if(!Current(source,version))return;
            var connected=Flag(setup,"connected");var current=Flag(setup,"scriptCurrent");
            var state=Text(connected?current?"연결됨":"스크립트 다시 붙여넣기 필요":"아직 연결 안 됨",13,true);state.TextColor=connected?current?Success:Warning:Muted;body.Add(state);
            View Step(int number,string title,string detail,params View[] extra)
            {
                var text=new VerticalStackLayout{Spacing=4};text.Add(Text(title,15,true));var d=Text(detail,13);d.TextColor=Muted;text.Add(d);foreach(var v in extra)text.Add(v);
                var dot=new Border{WidthRequest=26,HeightRequest=26,BackgroundColor=Pale,StrokeThickness=0,StrokeShape=new Ellipse(),VerticalOptions=LayoutOptions.Start,Content=new Label{Text=number.ToString(CultureInfo.InvariantCulture),TextColor=Blue,FontAttributes=FontAttributes.Bold,HorizontalOptions=LayoutOptions.Center,VerticalOptions=LayoutOptions.Center}};
                var grid=new Grid{ColumnDefinitions={new(new GridLength(40)),new(GridLength.Star)}};grid.Add(dot);grid.Add(text,1);return DesktopPanel(grid,16);
            }
            body.Add(Step(1,"수강신청용 구글 계정으로 로그인","학교 도메인 계정은 ‘모든 사용자’ 공개가 막혀 있는 경우가 많아, 학교 이름으로 개인 구글 계정을 하나 만들어 쓰는 것을 권합니다. 이 계정의 비밀번호는 담당 선생님끼리 인계하세요."));
            var copy=Act("코드 복사",async()=>
            {
                var script=await source.Get("api/courses/script");await Clipboard.Default.SetTextAsync(script.GetProperty("code").GetString());
                Say("코드를 복사했습니다. 이 코드에는 학교 서버와 구글을 잇는 열쇠가 들어 있으니 다른 곳에 붙여넣지 마세요.",false);
            },true);
            var browse=Act("브라우저에서 새 스크립트 열기",async()=>{await Launcher.Default.OpenAsync(new Uri("https://script.google.com/home/projects/create"));});
            body.Add(Step(2,"새 스크립트에 코드 붙여넣기","브라우저에서 script.google.com의 새 프로젝트를 열고, 원래 있던 코드를 모두 지운 뒤 ‘코드 복사’로 복사한 코드를 붙여넣고 저장(⌘S)합니다.",new HorizontalStackLayout{Spacing=8,Children={copy,browse}}));
            body.Add(Step(3,"웹 앱으로 배포","오른쪽 위 ‘배포 → 새 배포’에서 톱니바퀴를 눌러 ‘웹 앱’을 고릅니다. ‘액세스 권한이 있는 사용자’를 ‘모든 사용자’로 바꾸고 배포합니다. ‘액세스 승인’을 눌러 계정을 고르고 허용합니다(‘Google에서 확인하지 않은 앱’ 경고가 나오면 ‘고급 → 이동’)."));
            var url=new Entry{ClassId="school-field",Text=Field(setup,"scriptUrl"),Placeholder="https://script.google.com/macros/s/…/exec",FontSize=14,AutomationId="mac-course-url"};
            var check=Act("연결 확인",async()=>
            {
                var result=await MacSend(source,"api/courses/connect",new JsonObject{["url"]=url.Text??""});var fresh=Flag(result,"scriptCurrent");
                await Connect();Say(fresh?"연결되었습니다. 이제 ‘신청 · 결과’에서 신청을 만들 수 있습니다.":"연결은 되었지만 붙여넣은 코드가 옛 버전입니다. 코드를 다시 복사해 붙여넣고 ‘배포 → 배포 관리 → 새 버전’으로 다시 배포하세요.",!fresh);
            },true,"구글 스크립트에 연결하는 중…");
            var line=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8};line.Add(DesktopPanel(url,4));line.Add(check,1);
            body.Add(Step(4,"웹 앱 URL 붙여넣기","배포가 끝나면 나오는 ‘웹 앱 URL’(…/exec로 끝남)을 복사해 아래에 붙여넣고 ‘연결 확인’을 누릅니다.",line));
            var after=Text("교무톡이 업데이트되어 코드가 바뀌면 ‘스크립트 다시 붙여넣기 필요’가 표시됩니다. 그때는 2번처럼 코드를 다시 붙여넣고 ‘배포 → 배포 관리 → 수정 → 새 버전’으로 배포하면 주소는 그대로입니다.",12);after.TextColor=Faint;body.Add(after);
        }
        MacShowPane("courses",root);SelectRail("courses");desktopReader=null;
        await Do(macCourseTab switch{"roster"=>Roster,"connect"=>Connect,_=>Rounds});
    }
}
