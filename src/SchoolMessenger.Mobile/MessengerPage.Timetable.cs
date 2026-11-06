using System.Globalization;
using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    async Task<bool> Timetable(string? selectedDate=null,string? selectedClass=null)
    {
        var source=api!;if(!MacScheduleActive(source))return false;
        var setup=await source.Get("api/timetable/setup");if(!MacScheduleActive(source))return false;
        var teacher=source.Office||source.Session.GetProperty("user").GetProperty("role").GetString()=="teacher";
        var rooms=setup.GetProperty("classes").EnumerateArray().ToArray();
        var page=DesktopPage("시간표");
        var stack=new VerticalStackLayout{Padding=16,Spacing=10};((Grid)page.Content).Add(new ScrollView{Content=stack},0,1);
        var date=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=selectedDate is null?DateTime.UtcNow.AddHours(9).Date:DateTime.ParseExact(selectedDate,"yyyy-MM-dd",CultureInfo.InvariantCulture)};
        var scopes=(teacher?new[]{"내 수업"}:Array.Empty<string>()).Concat(rooms.Select(r=>r.GetProperty("name").GetString()!)).ToArray();
        var scope=new Picker{ClassId="school-field",Title="조회할 시간표",ItemsSource=scopes,SelectedIndex=scopes.Length>0?0:-1};
        if(selectedClass is not null){var index=Array.FindIndex(rooms,r=>r.GetProperty("id").GetString()==selectedClass);if(index>=0)scope.SelectedIndex=index+(teacher?1:0);}
        stack.Add(Text("시간표",22,true));
        var hint=Text(teacher?"내 수업과 학급별 시간표":"학급별 시간표",12);hint.TextColor=Muted;stack.Add(hint);
        var filters=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10};
        var dates=new VerticalStackLayout{Spacing=4};dates.Add(Text("조회 날짜",11,true));dates.Add(date);
        var classes=new VerticalStackLayout{Spacing=4};classes.Add(Text("조회할 시간표",11,true));classes.Add(scope);filters.Add(dates);filters.Add(classes,1);stack.Add(Card(filters,12));
        SemanticProperties.SetDescription(date,"조회 날짜 · 한국 시간");SemanticProperties.SetDescription(scope,"조회할 시간표");
        var error=Text("",13);error.TextColor=Color.FromArgb("#A53A38");stack.Add(error);
        var dayButton=DesktopButton("하루");var weekButton=DesktopButton("한 주");
        var actions=new HorizontalStackLayout{Spacing=8,Children={dayButton,weekButton}};stack.Add(actions);
        TimetableDay[] printDays=[];string printTitle="";
        var printButton=DesktopButton("게시 시간표 인쇄");printButton.IsEnabled=false;
        var content=new VerticalStackLayout{Spacing=10};stack.Add(content);
        var scheduleTools=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap};
        if(teacher)
        {
            scheduleTools.Add(DesktopButton("수업 교체 신청",()=>MacSwapEditor(source,date.Date??DateTime.Today),true));
            var manager=setup.GetProperty("canManage").GetBoolean();
            scheduleTools.Add(DesktopButton("교체 신청 현황",()=>MacSwapQueue(source,setup,manager)));
            if(manager) scheduleTools.Add(DesktopButton("묶음수업 설정",()=>MacBundleEditor(source,date.Date??DateTime.Today)));
            scheduleTools.Add(printButton);
            foreach(var tool in scheduleTools.Children.OfType<View>())tool.Margin=new Thickness(0,0,8,6);
            stack.Add(scheduleTools);
        }
        printButton.Clicked+=(_,_)=>PrintMacSchedule(printTitle,printDays,error);
        bool loading=false,loaded=false;
        void WeekGrid(TimetableDay[] week,bool mine)
        {
            var shown=week.Where(day=>DateOnly.TryParse(day.Date,out var value)&&
                (value.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)||day.Slots.Any(s=>s.Subject.Length>0))).ToArray();
            var periods=Enumerable.Range(1,7).Concat(shown.SelectMany(day=>day.Slots)
                .Where(slot=>slot.Subject.Length>0).Select(slot=>slot.Period)).Distinct().Order().ToArray();
            var table=new Grid{ColumnSpacing=1,RowSpacing=1,BackgroundColor=Line};
            table.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(56)));
            foreach(var _ in shown)table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for(var i=0;i<=periods.Length;i++)table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void Put(int column,int row,View item,Color background)
            {
                var cell=new Border{Content=item,Padding=new Thickness(5,4),BackgroundColor=background,StrokeThickness=0};
                table.Add(cell,column,row);
            }
            Put(0,0,Text("교시",12,true),Pale);
            for(var c=0;c<shown.Length;c++)
            {
                var when=DateOnly.Parse(shown[c].Date,CultureInfo.InvariantCulture);
                Put(c+1,0,Text(when.ToString("ddd M.d",CultureInfo.GetCultureInfo("ko-KR")),12,true),Pale);
            }
            for(var r=0;r<periods.Length;r++)
            {
                Put(0,r+1,Text(periods[r]+"교시",12,true),Color.FromArgb("#F3F6FB"));
                for(var c=0;c<shown.Length;c++)
                {
                    var lessons=shown[c].Slots.Where(slot=>slot.Period==periods[r]&&slot.Subject.Length>0).ToArray();
                    var words=new VerticalStackLayout{Spacing=3};
                    foreach(var lesson in lessons)
                    {
                        words.Add(Text(lesson.Subject,13,true));
                        var label=Text((mine?lesson.ClassName:lesson.TeacherName)+(lesson.Changed?" · 변경":""),11);
                        label.TextColor=lesson.Changed?Blue:Muted;words.Add(label);
                        if(lesson.Changed)
                        {
                            var before=Text("변경 전 "+(lesson.BeforeSubject.Length==0?"수업 없음":lesson.BeforeSubject+" · "+lesson.BeforeTeacher),10);
                            before.TextColor=Muted;words.Add(before);
                        }
                    }
                    if(lessons.Length==0){var blank=Text("—",12);blank.TextColor=Muted;words.Add(blank);}
                    Put(c+1,r+1,words,lessons.Any(slot=>slot.Changed)?Pale:Colors.White);
                }
            }
            content.Add(DesktopPanel(table,0));
        }
        async Task Show(bool weekly)
        {
            if(loading||!MacScheduleActive(source))return;loading=true;loaded=false;printButton.IsEnabled=false;date.IsEnabled=scope.IsEnabled=dayButton.IsEnabled=weekButton.IsEnabled=false;error.Text="";content.Clear();
            try
            {
                if(scope.SelectedIndex<0||date.Date is not{} selected)throw new InvalidOperationException("학급과 날짜를 선택하세요.");
                var mine=teacher&&scope.SelectedIndex==0;var room=mine?null:rooms[scope.SelectedIndex-(teacher?1:0)].GetProperty("id").GetString();
                var first=weekly?selected.AddDays(-((int)selected.DayOfWeek+6)%7):selected;long? revision=null;var days=new List<JsonElement>();
                for(var n=0;n<(weekly?7:1);n++)
                {
                    var value=await source.Get("api/timetable/day?date="+first.AddDays(n).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+(mine?"&mine=true":"&classId="+room));if(!MacScheduleActive(source))return;
                    var rev=value.GetProperty("revision").GetInt64();if(revision is not null&&revision!=rev)throw new InvalidOperationException("조회 중 시간표가 변경되었습니다. 다시 조회하세요.");revision=rev;days.Add(value);
                }
                printDays=days.Select(d=>d.Deserialize(MobileJson.Default.TimetableDay)!).ToArray();
                dayButton.BackgroundColor=weekly?Colors.White:Pale;weekButton.BackgroundColor=weekly?Pale:Colors.White;
                printTitle=scopes[scope.SelectedIndex]+" 시간표";printButton.IsEnabled=printDays.Any(d=>d.PublishedAt>0);
                if(!printButton.IsEnabled&&!printDays.Any(day=>day.Slots.Any(slot=>slot.Subject.Length>0)))
                {
                    content.Add(DesktopEmptyState("아직 올라온 시간표가 없습니다","시간표 담당자가 올리면 여기에 표시됩니다."));
                    loaded=true;return;
                }
                if(weekly)
                {
                    WeekGrid(printDays,mine);
                    if(printDays.Any(day=>day.MissingClasses.Length>0))content.Add(Text("일부 학급의 적용 가능한 기초시간표가 없습니다.",12));
                }
                else foreach(var value in days)
                {
                    content.Add(Text(value.GetProperty("date").GetString()!,16,true));
                    if(!mine)
                    {
                        var roomInfo=rooms.FirstOrDefault(r=>r.GetProperty("id").GetString()==room);var grade=roomInfo.ValueKind==JsonValueKind.Object?roomInfo.GetProperty("grade").GetInt32():0;
                        if(grade is >=1 and <=3){var breaks=Text(string.Join("\n",SchoolDay.Schedule(grade).Where(p=>!p.Lesson).Select(p=>p.Label)),12);breaks.TextColor=Muted;content.Add(Card(breaks,12));}
                    }
                    var published=value.GetProperty("publishedAt").GetInt64();if(published>0){var stamp=Text("최종 게시 "+Date(published)+" · 버전 "+revision,11);stamp.TextColor=Muted;content.Add(stamp);}
                    if(value.GetProperty("missingClasses").GetArrayLength()>0)content.Add(Text("아직 시간표가 등록되지 않은 학급이 있습니다.",13));
                    var slots=value.GetProperty("slots").EnumerateArray().ToArray();
                    if(mine&&slots.Length==0)content.Add(Text("이 날짜에 배정된 수업이 없습니다.",14));
                    foreach(var slot in slots.Where(s=>s.GetProperty("period").GetInt32()<=10||!string.IsNullOrEmpty(s.GetProperty("subject").GetString())))
                    {
                        var subject=slot.GetProperty("subject").GetString();var changed=slot.GetProperty("changed").GetBoolean();
                        var roomInfo=rooms.FirstOrDefault(r=>r.GetProperty("id").GetString()==slot.GetProperty("classId").GetString());var grade=roomInfo.ValueKind==JsonValueKind.Object?roomInfo.GetProperty("grade").GetInt32():0;
                        var label=SchoolDay.Label(grade,slot.GetProperty("period").GetInt32())+" · "+(mine?slot.GetProperty("className").GetString()+" · ":"")+(string.IsNullOrEmpty(subject)?"수업 없음":subject+" · "+slot.GetProperty("teacherName").GetString());
                        if(changed)label+="\n변경 전: "+(slot.GetProperty("beforeSubject").GetString() is{Length:>0} before?before+" · "+slot.GetProperty("beforeTeacher").GetString():"수업 없음");
                        var bell=SchoolDay.Label(grade,slot.GetProperty("period").GetInt32()).Split(" · ",2,StringSplitOptions.None);
                        var row=new Grid{ColumnDefinitions={new(new GridLength(90)),new(GridLength.Star)},ColumnSpacing=12};
                        var timing=new VerticalStackLayout{Spacing=2,VerticalOptions=LayoutOptions.Center};timing.Add(Text(bell[0],14,true));
                        if(bell.Length>1){var clock=Text(bell[1],11);clock.TextColor=Muted;timing.Add(clock);}row.Add(timing);
                        var lesson=new VerticalStackLayout{Spacing=4};var name=Text(string.IsNullOrEmpty(subject)?"수업 없음":subject,16,!string.IsNullOrEmpty(subject));if(string.IsNullOrEmpty(subject))name.TextColor=Muted;lesson.Add(name);
                        var teacherName=slot.GetProperty("teacherName").GetString();var owner=(mine?slot.GetProperty("className").GetString()+" · ":"")+teacherName;
                        if(!string.IsNullOrWhiteSpace(owner)){var teacherLabel=Text(owner,12);teacherLabel.TextColor=Muted;lesson.Add(teacherLabel);}
                        if(changed){var changedLabel=Text("변경",11,true);changedLabel.TextColor=Blue;lesson.Add(changedLabel);}row.Add(lesson,1);
                        var item=new VerticalStackLayout{Spacing=8};item.Add(row);
                        if(changed){var change=Text("변경 전: "+(slot.GetProperty("beforeSubject").GetString() is{Length:>0} previous?previous+" · "+slot.GetProperty("beforeTeacher").GetString():"수업 없음"),12);change.TextColor=Blue;item.Add(change);}
                        var card=Card(item,14,changed?Pale:null);card.ClassId="timetable-lesson";SemanticProperties.SetDescription(card,label);content.Add(card);
                    }
                    if(slots.Length>0)content.Add(Text(slots.Any(s=>s.GetProperty("source").GetString()=="daily")?"일자별 시간표 적용":"기초시간표 적용",12));
                }
                loaded=true;
            }
            catch(Exception e){content.Clear();error.Text=e is HttpRequestException?"서버에 연결할 수 없습니다. 최신 시간표를 다시 조회하세요.":e.Message;}
            finally{loading=false;date.IsEnabled=scope.IsEnabled=dayButton.IsEnabled=weekButton.IsEnabled=true;}
        }
        dayButton.Clicked+=async(_,_)=>await Show(false);weekButton.Clicked+=async(_,_)=>await Show(true);
        await Show(selectedDate is null);if(!MacScheduleActive(source))return false;timetablePage=page;await Navigation.PushAsync(page);return loaded;
    }
}
