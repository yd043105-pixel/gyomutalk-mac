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
        // Changes to this teacher's lessons, as on Windows: pressing one shows that day and marks it read.
        var notices=new VerticalStackLayout{Spacing=6};stack.Add(notices);
        // One control row as on Windows: day or week, the week itself with arrows and "today", the timetable to show on the right.
        // The date picker stays as the place the chosen day is kept.
        SemanticProperties.SetDescription(date,"조회 날짜 · 한국 시간");SemanticProperties.SetDescription(scope,"조회할 시간표");
        var dayButton=DesktopButton("하루");var weekButton=DesktopButton("한 주");
        foreach(var mode in new[]{dayButton,weekButton}){mode.MinimumHeightRequest=32;mode.Padding=new Thickness(14,2);mode.FontSize=14;mode.BorderWidth=0;}
        var segment=new Border{Content=new HorizontalStackLayout{Spacing=2,Children={dayButton,weekButton}},Padding=3,BackgroundColor=Subtle,Stroke=Line,StrokeThickness=1,StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle{CornerRadius=Radius},VerticalOptions=LayoutOptions.Center};
        var range=Text("",14,true);range.VerticalOptions=LayoutOptions.Center;range.AutomationId="mac-timetable-range";
        Button Arrow(string label,string name){var arrow=new Button{Text=label,FontSize=18,WidthRequest=32,MinimumHeightRequest=32,Padding=0,BackgroundColor=Colors.Transparent,BorderWidth=0,TextColor=Blue,VerticalOptions=LayoutOptions.Center};SemanticProperties.SetDescription(arrow,name);ToolTipProperties.SetText(arrow,name);return arrow;}
        var previous=Arrow("‹","이전");var next=Arrow("›","다음");
        var today=new Button{Text="오늘",FontSize=14,BackgroundColor=Colors.Transparent,BorderWidth=0,TextColor=Blue,Padding=new Thickness(8,2),VerticalOptions=LayoutOptions.Center};
        var controls=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(new GridLength(240))},ColumnSpacing=6};
        controls.Add(segment);controls.Add(previous,1);controls.Add(range,2);controls.Add(next,3);controls.Add(today,4);
        scope.VerticalOptions=LayoutOptions.Center;controls.Add(DesktopPanel(scope,6),6);stack.Add(controls);
        var error=Text("",13);error.TextColor=Color.FromArgb("#A53A38");stack.Add(error);
        TimetableDay[] printDays=[];string printTitle="";
        var printButton=DesktopButton("보고 있는 시간표 인쇄");printButton.IsEnabled=false;
        var content=new VerticalStackLayout{Spacing=10};stack.Add(content);
        var scheduleTools=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap};
        if(teacher)
        {
            var manager=setup.GetProperty("canManage").GetBoolean();
            var swap=DesktopButton("수업 교체 신청",()=>MacSwapBoard(source,date.Date??DateTime.Today,manager),true);swap.AutomationId="mac-swap-board";scheduleTools.Add(swap);
            scheduleTools.Add(DesktopButton("교체 신청 현황",()=>MacSwapQueue(source,setup,manager)));
            if(manager) scheduleTools.Add(DesktopButton("묶음수업",()=>MacBundleEditor(source)));
            if(manager) scheduleTools.Add(DesktopButton("게시 이력",()=>MacScheduleHistory(source)));
            scheduleTools.Add(printButton);
            foreach(var tool in scheduleTools.Children.OfType<View>())tool.Margin=new Thickness(0,0,8,6);
            stack.Add(scheduleTools);
        }
        printButton.Clicked+=(_,_)=>PrintMacSchedule(printTitle,printDays,error);
        bool loading=false,loaded=false,current=selectedDate is null;
        JsonElement[] events=[];
        void WeekGrid(TimetableDay[] week,bool mine)
        {
            var shown=week.Where(day=>DateOnly.TryParse(day.Date,out var value)&&
                (value.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)||day.Slots.Any(s=>s.Subject.Length>0))).ToArray();
            var periods=Enumerable.Range(1,7).Concat(shown.SelectMany(day=>day.Slots)
                .Where(slot=>slot.Subject.Length>0).Select(slot=>slot.Period)).Distinct().Order().ToArray();
            // Rows divided by thin lines, lessons as tinted chips, today's column lightly shaded: the Windows week view.
            var todayKey=DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            var table=new Grid{ColumnSpacing=0,RowSpacing=1,BackgroundColor=Line};
            table.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(64)));
            foreach(var _ in shown)table.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for(var i=0;i<=periods.Length;i++)table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void Put(int column,int row,View item,Color background,double padding=4)
            {
                var cell=new Border{Content=item,Padding=new Thickness(padding,3),BackgroundColor=background,StrokeThickness=0};
                table.Add(cell,column,row);
            }
            var corner=Text("교시",12);corner.TextColor=Muted;corner.HorizontalTextAlignment=TextAlignment.Center;Put(0,0,corner,Subtle,6);
            for(var c=0;c<shown.Length;c++)
            {
                var isToday=shown[c].Date==todayKey;var when=DateOnly.Parse(shown[c].Date,CultureInfo.InvariantCulture);
                var day=Text(when.ToString("ddd M.d",CultureInfo.GetCultureInfo("ko-KR"))+(isToday?" · 오늘":""),13,isToday);day.TextColor=isToday?Blue:Navy;day.HorizontalTextAlignment=TextAlignment.Center;
                Put(c+1,0,day,isToday?Pale:Subtle,6);
            }
            for(var r=0;r<periods.Length;r++)
            {
                var bell=SchoolDay.Label(1,periods[r]).Split(" · ",2,StringSplitOptions.None);var period=new VerticalStackLayout{Spacing=0,VerticalOptions=LayoutOptions.Center};
                period.Add(Text(periods[r]+"교시",12,true));if(bell.Length>1&&bell[1].Contains(':')){var clock=Text(bell[1].Split('–')[0],10);clock.TextColor=Muted;period.Add(clock);}
                Put(0,r+1,period,Surface,8);
                for(var c=0;c<shown.Length;c++)
                {
                    var lessons=shown[c].Slots.Where(slot=>slot.Period==periods[r]&&slot.Subject.Length>0).ToArray();
                    var words=new VerticalStackLayout{Spacing=3};var on=DateTime.ParseExact(shown[c].Date,"yyyy-MM-dd",CultureInfo.InvariantCulture);
                    foreach(var e in events.Where(e=>EventCovers(e,on)&&EventPeriods(e).Contains(periods[r])))words.Add(EventTag(e,false));
                    foreach(var lesson in lessons)
                    {
                        var chip=new VerticalStackLayout{Spacing=0};
                        var subject=Text(lesson.Subject,13,true);subject.TextColor=lesson.Changed?ChangeInk:Blue;subject.MaxLines=1;subject.LineBreakMode=LineBreakMode.TailTruncation;chip.Add(subject);
                        var label=Text((mine?lesson.ClassName:lesson.TeacherName)+(lesson.Changed?" · 변경":""),11);label.TextColor=lesson.Changed?ChangeInk:Muted;label.MaxLines=1;label.LineBreakMode=LineBreakMode.TailTruncation;chip.Add(label);
                        if(lesson.Changed)
                        {
                            var before=Text("변경 전 "+(lesson.BeforeSubject.Length==0?"수업 없음":lesson.BeforeSubject+" · "+lesson.BeforeTeacher),10);
                            before.TextColor=Muted;chip.Add(before);
                        }
                        words.Add(new Border{Content=chip,Padding=new Thickness(7,3),BackgroundColor=lesson.Changed?ChangeTint:Pale,StrokeThickness=0,StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle{CornerRadius=6}});
                    }
                    if(words.Children.Count==0)words.HeightRequest=34;
                    Put(c+1,r+1,words,shown[c].Date==todayKey?Subtle:Surface,3);
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
                // School events that take lessons, shown in those lessons. The timetable works without them.
                try{events=(await source.Get("api/office/events?from="+first.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"&to="+first.AddDays(weekly?7:1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))).EnumerateArray().Where(e=>EventPeriods(e).Length>0).ToArray();}
                catch(Exception e)when(e is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException){events=[];}
                if(!MacScheduleActive(source))return;
                current=weekly;dayButton.BackgroundColor=weekly?Colors.Transparent:Colors.White;weekButton.BackgroundColor=weekly?Colors.White:Colors.Transparent;
                dayButton.FontAttributes=weekly?FontAttributes.None:FontAttributes.Bold;weekButton.FontAttributes=weekly?FontAttributes.Bold:FontAttributes.None;
                range.Text=weekly?first.ToString("M.d",CultureInfo.InvariantCulture)+" ~ "+first.AddDays(4).ToString("M.d",CultureInfo.InvariantCulture):selected.ToString("M월 d일 dddd",CultureInfo.GetCultureInfo("ko-KR"));
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
                    foreach(var e in events.Where(e=>EventCovers(e,first))){var tag=EventTag(e,true);tag.Padding=new Thickness(10,6);content.Add(tag);}
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
                        if(changed){var changedLabel=Text("변경",11,true);changedLabel.TextColor=ChangeInk;lesson.Add(changedLabel);}row.Add(lesson,1);
                        var item=new VerticalStackLayout{Spacing=8};item.Add(row);
                        if(changed){var change=Text("변경 전: "+(slot.GetProperty("beforeSubject").GetString() is{Length:>0} previous?previous+" · "+slot.GetProperty("beforeTeacher").GetString():"수업 없음"),12);change.TextColor=Blue;item.Add(change);}
                        var card=Card(item,14,changed?ChangeTint:null);card.ClassId="timetable-lesson";SemanticProperties.SetDescription(card,label);content.Add(card);
                    }
                    if(slots.Length>0)content.Add(Text(slots.Any(s=>s.GetProperty("source").GetString()=="daily")?"일자별 시간표 적용":"기초시간표 적용",12));
                }
                loaded=true;
            }
            catch(Exception e){content.Clear();error.Text=e is HttpRequestException?"서버에 연결할 수 없습니다. 최신 시간표를 다시 조회하세요.":e.Message;}
            finally{loading=false;date.IsEnabled=scope.IsEnabled=dayButton.IsEnabled=weekButton.IsEnabled=true;}
        }
        dayButton.Clicked+=async(_,_)=>await Show(false);weekButton.Clicked+=async(_,_)=>await Show(true);
        async Task Move(int step){if(date.Date is{} day){date.Date=day.AddDays(current?step*7:step);await Show(current);}}
        previous.Clicked+=async(_,_)=>await Move(-1);next.Clicked+=async(_,_)=>await Move(1);
        today.Clicked+=async(_,_)=>{date.Date=DateTime.UtcNow.AddHours(9).Date;await Show(current);};
        scope.SelectedIndexChanged+=async(_,_)=>{if(!loading)await Show(current);};
        async Task Notices()
        {
            notices.Clear();JsonElement list;
            try{list=await source.Get("api/timetable/notices");}catch(MessengerApiRefusal e)when(e.Status is 403 or 404){return;}
            var unread=list.EnumerateArray().Where(n=>n.GetProperty("readAt").ValueKind!=JsonValueKind.Number).OrderBy(n=>n.GetProperty("date").GetString(),StringComparer.Ordinal).ToArray();
            async Task Read(JsonElement n)=>await source.Send("api/timetable/notices/"+n.GetProperty("revision").GetInt64().ToString(CultureInfo.InvariantCulture)+"/read");
            if(unread.Length>1){var all=DesktopButton("알림 모두 확인",async()=>{foreach(var n in unread)await Read(n);await Notices();});SubtleButton(all);all.HorizontalOptions=LayoutOptions.Start;notices.Add(all);}
            foreach(var n in unread.Take(5))
            {
                var note=DesktopButton(n.GetProperty("title").GetString()??"시간표가 변경되었습니다",async()=>
                {
                    date.Date=DateTime.ParseExact(n.GetProperty("date").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture);
                    var classes=n.GetProperty("classIds").EnumerateArray().Select(c=>c.GetString()).ToHashSet();
                    var index=Array.FindIndex(rooms,r=>classes.Contains(r.GetProperty("id").GetString()));if(index>=0)scope.SelectedIndex=index+(teacher?1:0);
                    await Read(n);await Show(false);await Notices();
                });
                note.BackgroundColor=ChangeTint;note.TextColor=ChangeInk;note.BorderWidth=0;note.HorizontalOptions=LayoutOptions.Start;note.FontSize=14;note.AutomationId="mac-timetable-notice";
                ToolTipProperties.SetText(note,"누르면 그 날짜의 시간표를 봅니다.");notices.Add(note);
            }
        }
        await Notices();
        await Show(selectedDate is null);if(!MacScheduleActive(source))return false;timetablePage=page;await Navigation.PushAsync(page);return loaded;
    }
}
