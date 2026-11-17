using System.Globalization;
using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    void OfficePasswordChange(bool required=false)
    {
        var source=api!;Header("개인 비밀번호 설정",required?"첫 로그인 · 비밀번호 변경":"비밀번호 변경");tabs.IsVisible=false;
        body.Add(Text(required?"초기 비밀번호를 변경해야 업무 자료를 열 수 있습니다.":"변경 후 모든 기기에서 다시 로그인해야 합니다.",14));
        var current=Input("현재 비밀번호",true);var next=Input("새 비밀번호 · 8~128자",true);var confirm=Input("새 비밀번호 확인",true);
        Action("변경 후 다시 로그인",async()=>
        {
            if(next.Text!=confirm.Text)throw new InvalidOperationException("새 비밀번호 확인이 일치하지 않습니다.");
            try{await source.Send("api/password",new MobilePasswordChange(current.Text??"",next.Text??""));}
            finally{current.Text=next.Text=confirm.Text="";}
            source.Remember=false;await source.Refresh();source.Dispose();api=null;Login();feedback.Text="비밀번호를 변경했습니다. 개인 비밀번호로 로그인하세요.";
        },true);
        if(!required)Action("내 정보로 돌아가기",Home);
        Action("로그아웃",async()=>{try{await source.Logout();}finally{source.Dispose();api=null;Login();}});
    }
    async Task ActivateStaff()
    {
        var source=api!;Header("개인 계정의 첫 설정","교직원 계정 첫 설정");tabs.IsVisible=false;
        body.Add(Text("처음 로그인하셨습니다. 앞으로 쓸 개인 비밀번호를 정하고 안내를 확인하세요. 마치기 전에는 업무 자료를 열 수 없습니다.",14));
        var password=Input("새 개인 비밀번호 · 8~128자",true);var confirm=Input("새 비밀번호 확인",true);
        var policy=await source.Get("api/registration/policy");var agreement=AddPolicies(body,policy);
        Action("첫 설정 마치기",async()=>
        {
            if(password.Text!=confirm.Text)throw new InvalidOperationException("비밀번호 확인이 일치하지 않습니다.");
            try{await source.Send("api/activate",new StaffActivation(password.Text??"",agreement()));}
            finally{password.Text=confirm.Text="";}
            source.Remember=false;await source.Refresh();source.Dispose();api=null;Login();feedback.Text="설정을 마쳤습니다. 방금 정한 비밀번호로 로그인하세요.";
        },true);
        Action("로그아웃",async()=>{try{await source.Logout();}finally{source.Dispose();api=null;Login();}});
    }
    // An all-day event's days, "11/20" or "11/20 → 11/22".
    static string OfficeDay(JsonElement row){var from=DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("start").GetInt64()).ToOffset(TimeSpan.FromHours(9)).Date;var to=DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("end").GetInt64()).ToOffset(TimeSpan.FromHours(9)).Date.AddDays(-1);return from.ToString("M/d",CultureInfo.InvariantCulture)+(to>from?" → "+to.ToString("M/d",CultureInfo.InvariantCulture):"");}
    static string OfficeDate(JsonElement row,string field)=>DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty(field).GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("M/d HH:mm");
    // An event's lessons (servers before 0.1.6 send none) and whether it covers a day; an all-day event is stored up to the midnight after its last day.
    static int[] EventPeriods(JsonElement row)=>row.TryGetProperty("periods",out var p)&&p.ValueKind==JsonValueKind.Array?p.EnumerateArray().Select(x=>x.GetInt32()).ToArray():[];
    static bool EventCovers(JsonElement row,DateTime date)
    {
        var from=DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("start").GetInt64()).ToOffset(TimeSpan.FromHours(9)).DateTime;var end=DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("end").GetInt64()).ToOffset(TimeSpan.FromHours(9)).DateTime;
        var to=(end.TimeOfDay==TimeSpan.Zero&&end.Date>from.Date?end.AddDays(-1):end).Date;return date.Date>=from.Date&&date.Date<=to;
    }
    // An event in the lessons it takes, in the event's own colour as on Windows.
    static Border EventTag(JsonElement row,bool lessons)
    {
        var title=row.GetProperty("title").GetString()??"";var tint=AvatarTints[AvatarSlot(title)];
        var text=new Label{Text=(lessons?SchoolDay.PeriodText(EventPeriods(row)):"행사")+" "+title,FontSize=11,FontAttributes=FontAttributes.Bold,TextColor=tint.Ink,MaxLines=1,LineBreakMode=LineBreakMode.TailTruncation};
        var tag=new Border{Content=text,Padding=new Thickness(6,2),BackgroundColor=tint.Back,StrokeThickness=0,StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle{CornerRadius=6}};
        ToolTipProperties.SetText(tag,title+" · "+SchoolDay.PeriodText(EventPeriods(row))+" · "+row.GetProperty("ownerName").GetString()+" 등록");SemanticProperties.SetDescription(tag,title+" "+SchoolDay.PeriodText(EventPeriods(row)));return tag;
    }
    static string OfficeTimestamp(DateTime date,TimeSpan time)=>new DateTimeOffset(DateTime.SpecifyKind(date.Date,DateTimeKind.Unspecified).Add(time),TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz",CultureInfo.InvariantCulture);
    async Task OfficeSchedule(bool reservations)
    {
        var source=api!;var version=epoch;if(!source.Office)throw new InvalidOperationException("교사용 교무실 기능입니다.");
        var setup=await source.Get("api/office/setup");if(!Current(source,version))return;
        var page=new ContentPage{Title=reservations?"특별실 예약":"학사력 · 학교 일정",BackgroundColor=BackgroundColor};
        var stack=new VerticalStackLayout{Padding=20,Spacing=12};page.Content=new ScrollView{Content=stack};
        var date=new DatePicker{ClassId="school-field",Date=DateTime.UtcNow.AddHours(9).Date,Format="yyyy.MM.dd"};stack.Add(Text(reservations?"함께 쓰는 특별실":"우리 학교 일정",24,true));
        var filter=new VerticalStackLayout{Spacing=6};filter.Add(Text(reservations?"예약을 확인할 날짜":"일정을 확인할 달",12,true));filter.Add(date);stack.Add(Card(filter));
        var rooms=setup.GetProperty("rooms").EnumerateArray().Where(r=>r.GetProperty("active").GetBoolean()).ToArray();
        var room=new Picker{ClassId="school-field",Title="특별실",ItemsSource=new[]{"모든 특별실"}.Concat(rooms.Select(r=>r.GetProperty("name").GetString()!)).ToArray(),SelectedIndex=0};if(reservations)filter.Add(room);
        var list=new VerticalStackLayout{Spacing=12};var error=Text("",13);error.TextColor=Color.FromArgb("#A53A38");stack.Add(error);bool loading=false;
        async Task Load()
        {
            if(loading||!Current(source,version))return;loading=true;error.Text="";list.Clear();
            try
            {
                var selected=date.Date??DateTime.UtcNow.AddHours(9).Date;var from=reservations?selected.Date:new DateTime(selected.Year,selected.Month,1);var to=reservations?from.AddDays(1):from.AddMonths(1);
                var path="api/office/"+(reservations?"bookings":"events")+"?from="+from.ToString("yyyy-MM-dd")+"&to="+to.ToString("yyyy-MM-dd");if(reservations&&room.SelectedIndex>0)path+="&roomId="+rooms[room.SelectedIndex-1].GetProperty("id").GetString();
                var rows=await source.Get(path);if(!Current(source,version))return;
                foreach(var row in rows.EnumerateArray())
                {
                    var details=new VerticalStackLayout{Spacing=5};details.Add(Text(row.GetProperty("title").GetString()!,17,true));
                    details.Add(Text((!reservations&&row.GetProperty("allDay").GetBoolean()?OfficeDay(row):OfficeDate(row,"start")+" → "+OfficeDate(row,"end"))+" · "+row.GetProperty("ownerName").GetString()+(reservations?"":" 등록"),12));
                    if(!reservations&&EventPeriods(row).Length>0){var lessons=Text(SchoolDay.PeriodText(EventPeriods(row))+" · 시간표에 표시",13,true);lessons.TextColor=Blue;details.Add(lessons);}
                    if(reservations)details.Add(Text(row.GetProperty("roomName").GetString()!,13));else details.Add(Text(row.GetProperty("description").GetString()!,14));
                    var canEdit=reservations?row.GetProperty("ownerId").GetString()==setup.GetProperty("userId").GetString()||setup.GetProperty("roomManager").GetBoolean():setup.GetProperty("calendarManager").GetBoolean()||row.GetProperty("ownerId").GetString()==setup.GetProperty("userId").GetString();
                    if(canEdit)
                    {
                        var actions=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=8};
                        actions.Add(MakeAction("일정 변경",()=>OfficeEditor(source,setup,reservations,row,async()=>{await Load();})));
                        var cancel=MakeAction("취소",async()=>{if(await page.DisplayAlertAsync("취소 확인","이 항목을 취소할까요?","취소하기","돌아가기")){await source.Send("api/office/"+(reservations?"bookings":"events")+"/"+row.GetProperty("id").GetString()+"/cancel",new OfficeRevision(row.GetProperty("revision").GetInt32()));await Load();}});cancel.TextColor=Color.FromArgb("#A53A38");actions.Add(cancel,1);details.Add(actions);
                    }
                    list.Add(Card(details));
                }
                if(rows.GetArrayLength()==0)list.Add(Text(reservations?"이 날짜에는 예약이 없습니다.":"이 달에는 등록된 일정이 없습니다.",14));
            }
            catch(Exception e){error.Text=e.Message;}finally{loading=false;}
        }
        var tools=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10};
        tools.Add(MakeAction(reservations?"날짜 조회":"선택한 달 조회",Load));
        tools.Add(MakeAction(reservations?"특별실 예약하기":"학교 일정 등록",()=>OfficeEditor(source,setup,reservations,null,Load),true),1);stack.Add(tools);
        if(reservations&&setup.GetProperty("roomManager").GetBoolean())stack.Add(MakeAction("특별실 등록",async()=>
        {
            var name=await page.DisplayPromptAsync("특별실 등록","특별실 이름을 입력하세요.");if(string.IsNullOrWhiteSpace(name))return;
            var location=await page.DisplayPromptAsync("위치","특별실 위치를 입력하세요.");if(location is null)return;
            await source.Send("api/office/rooms",new OfficeRoomRequest(name,location));await Navigation.PopAsync();await OfficeSchedule(true);
        }));
        if(reservations&&setup.GetProperty("roomManager").GetBoolean())stack.Add(MakeAction("특별실 목록 설정",()=>OfficeRooms(source)));
        stack.Add(list);await Navigation.PushAsync(page);await Load();
    }
    async Task OfficeEditor(MessengerApi source,JsonElement setup,bool booking,JsonElement? existing,Func<Task> refresh)
    {
        if(booking){await RoomBookingEditor(source,setup,existing,refresh);return;}
        var page=new ContentPage{Title=booking?"특별실 예약":"학교 일정",BackgroundColor=BackgroundColor};var stack=new VerticalStackLayout{Padding=20,Spacing=12};page.Content=new ScrollView{Content=stack};
        var start=existing is{} value?DateTimeOffset.FromUnixTimeMilliseconds(value.GetProperty("start").GetInt64()).ToOffset(TimeSpan.FromHours(9)):DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).AddHours(1);
        var end=existing is{} endValue?DateTimeOffset.FromUnixTimeMilliseconds(endValue.GetProperty("end").GetInt64()).ToOffset(TimeSpan.FromHours(9)):start.AddHours(1);
        stack.Add(Text(booking?"예약할 공간과 시간":"학교 일정 작성",24,true));
        var title=new Entry{ClassId="school-field",Placeholder="제목",Text=existing?.GetProperty("title").GetString()};stack.Add(Text("제목",12,true));stack.Add(Card(title,10));
        var description=new Editor{Placeholder="행사·일정 안내",AutoSize=EditorAutoSizeOption.TextChanges,Text=!booking&&existing is{} ev?ev.GetProperty("description").GetString():""};if(!booking){stack.Add(Text("안내 내용",12,true));stack.Add(Card(description,10));}
        var whole=!booking&&existing is{} wholeDay&&wholeDay.GetProperty("allDay").GetBoolean();
        var first=new DatePicker{ClassId="school-field",Date=start.Date,Format="yyyy.MM.dd"};var last=new DatePicker{ClassId="school-field",Date=whole?end.Date.AddDays(-1):end.Date,Format="yyyy.MM.dd"};var firstTime=new TimePicker{ClassId="school-field",Time=start.TimeOfDay,Format="HH:mm"};var lastTime=new TimePicker{ClassId="school-field",Time=end.TimeOfDay,Format="HH:mm"};
        View TimeFields(string label,DatePicker day,TimePicker time)
        {
            var fields=new VerticalStackLayout{Spacing=6};fields.Add(Text(label,12,true));var row=new Grid{ColumnDefinitions={new(new GridLength(1.5,GridUnitType.Star)),new(GridLength.Star)},ColumnSpacing=8};row.Add(day);row.Add(time,1);fields.Add(row);return Card(fields);
        }
        stack.Add(TimeFields("시작 · 한국 시간",first,firstTime));stack.Add(TimeFields("종료 · 한국 시간",last,lastTime));
        var allDay=new CheckBox{IsChecked=whole};
        // Lessons the event takes on each of its days; every teacher sees them on the timetable.
        var saved=!booking&&existing is{} withLessons?EventPeriods(withLessons):[];var picks=Enumerable.Range(1,7).Select(p=>new CheckBox{IsChecked=saved.Contains(p)}).ToArray();
        if(!booking)
        {
            stack.Add(new HorizontalStackLayout{Children={allDay,Text("하루 종일 · 시각 없이 날짜만",12)}});firstTime.IsEnabled=lastTime.IsEnabled=!allDay.IsChecked;
            allDay.CheckedChanged+=(_,e)=>{firstTime.IsEnabled=lastTime.IsEnabled=!e.Value;if(!e.Value)foreach(var pick in picks)pick.IsChecked=false;};
            var lessons=new FlexLayout{Wrap=Microsoft.Maui.Layouts.FlexWrap.Wrap};
            for(var p=0;p<picks.Length;p++){var pick=picks[p];SemanticProperties.SetDescription(pick,(p+1)+"교시");pick.CheckedChanged+=(_,e)=>{if(e.Value)allDay.IsChecked=true;};lessons.Add(new HorizontalStackLayout{Margin=new Thickness(0,0,10,0),Children={pick,Text((p+1)+"교시",13)}});}
            var box=new VerticalStackLayout{Spacing=6};box.Add(Text("교시 (선택)",12,true));box.Add(lessons);var note=Text("교시를 고르면 날짜만 쓰는 일정이 되고, 모든 선생님의 시간표 그 교시에 행사 이름이 보입니다.",12);note.TextColor=Muted;box.Add(note);stack.Add(Card(box));
        }
        var rooms=setup.GetProperty("rooms").EnumerateArray().Where(r=>r.GetProperty("active").GetBoolean()).ToArray();var room=new Picker{ClassId="school-field",Title="특별실",ItemsSource=rooms.Select(r=>r.GetProperty("name").GetString()!).ToArray(),SelectedIndex=booking&&existing is{} old?Array.FindIndex(rooms,r=>r.GetProperty("id").GetString()==old.GetProperty("roomId").GetString()):rooms.Length>0?0:-1};if(booking){stack.Add(Text("특별실",12,true));stack.Add(Card(room,10));}
        var error=Text("",13);error.TextColor=Color.FromArgb("#A53A38");stack.Add(error);var client=existing is{} previous&&previous.TryGetProperty("clientId",out var oldClient)?oldClient.GetString()!:Guid.NewGuid().ToString("N");
        object? pending=null;var save=new Button{Text="저장",BackgroundColor=Blue,TextColor=Colors.White};
        save.Clicked+=async(_,_)=>
        {
            save.IsEnabled=false;try
            {
                if(pending is null)
                {
                    var a=OfficeTimestamp(first.Date??start.Date,!booking&&allDay.IsChecked?TimeSpan.Zero:firstTime.Time??start.TimeOfDay);var b=!booking&&allDay.IsChecked?OfficeTimestamp((last.Date??end.Date).AddDays(1),TimeSpan.Zero):OfficeTimestamp(last.Date??end.Date,lastTime.Time??end.TimeOfDay);var revision=existing?.GetProperty("revision").GetInt32()??0;
                    if(booking&&room.SelectedIndex<0)throw new InvalidOperationException("관리자가 특별실을 먼저 등록해야 합니다.");
                    pending=booking?new OfficeBookingRequest(client,rooms[room.SelectedIndex].GetProperty("id").GetString()!,title.Text??"",a,b,revision):new OfficeEventRequest(client,title.Text??"",description.Text??"",a,b,allDay.IsChecked,revision,picks.Select((pick,i)=>(pick,i)).Where(x=>x.pick.IsChecked).Select(x=>x.i+1).ToArray());
                }
                await source.Send("api/office/"+(booking?"bookings":"events")+(existing is{} row?"/"+row.GetProperty("id").GetString():""),pending);await Navigation.PopAsync();await refresh();
            }
            catch(Exception e){error.Text=e.Message;pending=null;}finally{save.IsEnabled=true;}
        };
        stack.Add(save);await Navigation.PushAsync(page);
    }
}
