using System.Globalization;
using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    async Task<bool> Timetable(string? selectedDate=null,string? selectedClass=null)
    {
        var source=api!;var version=epoch;var setup=await source.Get("api/timetable/setup");if(!Current(source,version))return false;
        var teacher=source.Office||source.Session.GetProperty("user").GetProperty("role").GetString()=="teacher";
        var rooms=setup.GetProperty("classes").EnumerateArray().ToArray();
        var page=new ContentPage{Title=teacher?"시간표 · 내 수업":"우리 학급 시간표",BackgroundColor=BackgroundColor};
        var stack=new VerticalStackLayout{Padding=18,Spacing=12};page.Content=new ScrollView{Content=stack};
        var date=new DatePicker{ClassId="school-field",Format="yyyy.MM.dd",Date=selectedDate is null?DateTime.UtcNow.AddHours(9).Date:DateTime.ParseExact(selectedDate,"yyyy-MM-dd",CultureInfo.InvariantCulture)};
        var scopes=(teacher?new[]{"내 수업"}:Array.Empty<string>()).Concat(rooms.Select(r=>r.GetProperty("name").GetString()!)).ToArray();
        var scope=new Picker{ClassId="school-field",Title="조회할 시간표",ItemsSource=scopes,SelectedIndex=scopes.Length>0?0:-1};
        if(selectedClass is not null){var index=Array.FindIndex(rooms,r=>r.GetProperty("id").GetString()==selectedClass);if(index>=0)scope.SelectedIndex=index+(teacher?1:0);}
        stack.Add(Text(teacher?"내 시간표 · 학급 시간표":"학급 시간표",20,true));
        var hint=Text(teacher?"본인 수업 또는 학급을 선택하세요.":"학생별 수강 목록 등록 전에는 학급 시간표를 보여줍니다.",12);hint.TextColor=Muted;stack.Add(hint);
        var filters=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10};
        var dates=new VerticalStackLayout{Spacing=4};dates.Add(Text("조회 날짜",11,true));dates.Add(date);
        var classes=new VerticalStackLayout{Spacing=4};classes.Add(Text("조회할 시간표",11,true));classes.Add(scope);filters.Add(dates);filters.Add(classes,1);stack.Add(Card(filters,12));
        SemanticProperties.SetDescription(date,"조회 날짜 · 한국 시간");SemanticProperties.SetDescription(scope,"조회할 시간표");
        var error=Text("",13);error.TextColor=Color.FromArgb("#A53A38");stack.Add(error);var dayButton=new Button{Text="선택한 날짜 보기",BackgroundColor=Blue,TextColor=Colors.White};var weekButton=new Button{Text="선택한 주 보기"};var actions=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10};actions.Add(dayButton);actions.Add(weekButton,1);stack.Add(actions);
        var content=new VerticalStackLayout{Spacing=14};stack.Add(content);bool loading=false,loaded=false;
        async Task Show(bool weekly)
        {
            if(loading||!Current(source,version))return;loading=true;loaded=false;date.IsEnabled=scope.IsEnabled=dayButton.IsEnabled=weekButton.IsEnabled=false;error.Text="";content.Clear();
            try
            {
                if(scope.SelectedIndex<0||date.Date is not{} selected)throw new InvalidOperationException("학급과 날짜를 선택하세요.");
                var mine=teacher&&scope.SelectedIndex==0;var room=mine?null:rooms[scope.SelectedIndex-(teacher?1:0)].GetProperty("id").GetString();
                var first=weekly?selected.AddDays(-((int)selected.DayOfWeek+6)%7):selected;long? revision=null;var days=new List<JsonElement>();
                for(var n=0;n<(weekly?7:1);n++)
                {
                    var value=await source.Get("api/timetable/day?date="+first.AddDays(n).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+(mine?"&mine=true":"&classId="+room));if(!Current(source,version))return;
                    var rev=value.GetProperty("revision").GetInt64();if(revision is not null&&revision!=rev)throw new InvalidOperationException("조회 중 시간표가 변경되었습니다. 다시 조회하세요.");revision=rev;days.Add(value);
                }
                foreach(var value in days)
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
        await Show(false);if(!Current(source,version))return false;await Navigation.PushAsync(page);return loaded;
    }
}
