using System.Text.Json;
using System.Globalization;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    async Task Counseling()
    {
        var source=api!;var version=epoch;var today=DateOnly.ParseExact(CounselingTimes.Today,"yyyy-MM-dd",CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue);
        var page=new ContentPage{Title="상담 예약",BackgroundColor=BackgroundColor};
        var stack=new VerticalStackLayout{Padding=18,Spacing=12};page.Content=new ScrollView{Content=stack};
        var heading=Text("선생님과 만나는 시간",20,true);stack.Add(heading);var hint=Text("한국 시간 기준 · 학생별 예정 상담은 1건입니다.",12);hint.TextColor=Muted;stack.Add(hint);
        var error=Text("",13);error.TextColor=Color.FromArgb("#A53A38");stack.Add(error);
        var day=new DatePicker{ClassId="school-field",Date=today.AddDays(1),MinimumDate=today.AddMonths(-6),MaximumDate=today.AddMonths(6),Format="yyyy.MM.dd"};
        var form=new VerticalStackLayout{Spacing=10};var list=new VerticalStackLayout{Spacing=12};
        var room=new Picker{ClassId="school-field",Title="담당 학급"};var childPicker=new Picker{ClassId="school-field",AutomationId="counseling-student",Title="상담할 학생"};var choices=new Dictionary<int,CheckBox>();
        var filters=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10};
        var dateField=new VerticalStackLayout{Spacing=4};dateField.Add(Text("상담 날짜",11,true));dateField.Add(day);filters.Add(dateField);
        var studentField=new VerticalStackLayout{Spacing=4,IsVisible=false};studentField.Add(Text("상담할 학생",11,true));studentField.Add(childPicker);filters.Add(studentField,1);stack.Add(Card(filters,12));
        var formCard=Card(form);formCard.AutomationId="counseling-manage-form";formCard.IsVisible=false;
        SemanticProperties.SetDescription(day,"상담 날짜 · 한국 시간");SemanticProperties.SetDescription(childPicker,"상담할 학생");
        var scheduled=new Switch();var opensDay=new DatePicker{ClassId="school-field",Date=today,MinimumDate=today,MaximumDate=today.AddMonths(6),Format="yyyy.MM.dd"};var opensTime=new TimePicker{ClassId="school-field",Time=TimeSpan.FromHours(9),Format="HH:mm"};
        CounselingView? data=null;CounselingOpen? pending=null;bool loading=false;Button? open=null;
        var periodGrid=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=8,RowSpacing=4};
        void PeriodChoices()
        {
            var selected=choices.Where(c=>c.Value.IsChecked).Select(c=>c.Key).ToArray();choices.Clear();periodGrid.Clear();periodGrid.RowDefinitions.Clear();
            if(data is not{CanManage:true}||room.SelectedIndex<0||room.SelectedIndex>=data.Classes.Length||data.Classes[room.SelectedIndex].Grade is <1 or >3)return;
            var available=CounselingTimes.Periods(data.Classes[room.SelectedIndex].Grade);
            for(var index=0;index<available.Length;index++)
            {
                var period=available[index];if(index%2==0)periodGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var check=new CheckBox{IsChecked=selected.Contains(period.Id)};choices[period.Id]=check;var caption=Text(period.Name+"\n"+period.Start+"–"+period.End,13);var row=new HorizontalStackLayout{Spacing=4,MinimumHeightRequest=54};row.Add(check);row.Add(caption);
                var tap=new TapGestureRecognizer();tap.Tapped+=(_,_)=>check.IsChecked=!check.IsChecked;caption.GestureRecognizers.Add(tap);periodGrid.Add(row,index%2,index/2);
            }
        }
        bool Live()=>Current(source,version)&&Navigation.NavigationStack.Contains(page);
        async Task Do(Func<Task> action)
        {
            if(loading||!Live())return;loading=true;page.Content.IsEnabled=false;error.Text="";
            try{await action();}catch(Exception e){if(Live())error.Text=e is HttpRequestException?"상담 서버에 연결할 수 없습니다. 연결을 확인하고 새로고침하세요.":e.Message;}
            finally{loading=false;if(Live())page.Content.IsEnabled=true;}
        }
        Button Act(string label,Func<Task> action,bool primary=false)
        {
            var button=new Button{Text=label,BackgroundColor=primary?Blue:Colors.White,TextColor=primary?Colors.White:Navy,BorderColor=primary?Blue:Line,BorderWidth=1,CornerRadius=10,Padding=new Thickness(16,10),FontSize=14,MinimumHeightRequest=48,LineBreakMode=LineBreakMode.WordWrap};
            button.Clicked+=async(_,_)=>await Do(action);return button;
        }
        void Render()
        {
            list.Clear();if(data is null)return;var student=childPicker.SelectedIndex>=0&&childPicker.SelectedIndex<data.Students.Length?data.Students[childPicker.SelectedIndex]:null;
            var bookings=data.CanManage?data.Bookings:data.Bookings.Where(b=>b.StudentId==student?.Id).ToArray();
            list.Add(Text(data.CanManage?"이 날짜의 상담 신청":"내 상담 예약",18,true));
            foreach(var booking in bookings)
            {
                var details=new VerticalStackLayout{Spacing=6};details.Add(Text(booking.StudentName+" · "+booking.ClassName,15,true));details.Add(Text(booking.TeacherName+" 선생님 · "+CounselingTimes.Display(booking.StartsAt),14));
                details.Add(Text(booking.CanceledAt is not null?"취소됨":booking.StartsAt<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()?"상담 시작·완료":"예약 확정",12));
                if(booking.CanceledAt is null&&booking.StartsAt>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())details.Add(Act("예약 취소",async()=>
                {if(await page.DisplayAlertAsync("예약 취소","이 상담 예약을 취소할까요?","취소하기","돌아가기")&&Live()){await source.Send("api/counseling/bookings/"+booking.Id+"/cancel");await Load();}}));list.Add(Card(details));
            }
            if(bookings.Length==0)list.Add(Text("등록된 상담 예약이 없습니다.",14));
            list.Add(Text(data.CanManage?"상담 시간 개방 현황":"선택한 날짜의 상담 시간",18,true));
            var slots=data.CanManage?data.Slots:data.Slots.Where(s=>s.ClassId==student?.ClassId).ToArray();
            foreach(var slot in slots)
            {
                var details=new VerticalStackLayout{Spacing=6};details.Add(Text(CounselingTimes.Display(slot.StartsAt)+"–"+DateTimeOffset.FromUnixTimeMilliseconds(slot.EndsAt).ToOffset(CounselingTimes.KoreaOffset).ToString("HH:mm"),17,true));
                details.Add(Text(slot.TeacherName+" 선생님 · "+slot.ClassName,13));
                var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();details.Add(Text(slot.Closed?"닫힘":slot.Booked?"예약 완료":slot.OpensAt>now?"신청 오픈 · "+CounselingTimes.Display(slot.OpensAt):slot.StartsAt<=now?"상담 시작·완료":"신청 가능",12));
                if(data.CanManage)
                {
                    if(!slot.Closed&&!slot.Booked&&slot.StartsAt>now)details.Add(Act("이 시간 닫기",async()=>{await source.Send("api/counseling/slots/"+slot.Id+"/close");await Load();}));
                }
                else if(student is not null&&!slot.Closed&&!slot.Booked&&slot.OpensAt<=now&&slot.StartsAt>now)
                {
                    var old=data.Bookings.FirstOrDefault(b=>b.StudentId==student.Id&&b.CanceledAt is null&&b.StartsAt>now);
                    var request=new CounselingReserve(Guid.NewGuid().ToString("N"),slot.Id,student.Id,old?.Id);
                    details.Add(Act(old is null?"이 시간 신청":"이 시간으로 변경",async()=>
                    {
                        var note=student.Name+" · "+slot.TeacherName+" 선생님\n"+CounselingTimes.Display(slot.StartsAt)+(old is null?"":"\n기존 예약은 새 예약 확정과 함께 취소됩니다.");
                        if(await page.DisplayAlertAsync(old is null?"상담 신청":"상담 시간 변경",note,"확인","돌아가기")&&Live()){await source.Send("api/counseling/bookings",request);await Load();}
                    },true));
                }
                var card=Card(details);card.AutomationId="counseling-slot-"+slot.Id;list.Add(card);
            }
            if(slots.Length==0)list.Add(Text(data.CanManage?"상담 시간을 선택해 열어주세요.":"이 날짜에 열린 상담 시간이 없습니다.",14));
        }
        async Task Load()
        {
            var value=await source.Get("api/counseling?date="+(day.Date??today).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));if(!Live())return;
            var previousChild=childPicker.SelectedIndex>=0&&data is not null&&childPicker.SelectedIndex<data.Students.Length?data.Students[childPicker.SelectedIndex].Id:null;
            var previousRoom=room.SelectedIndex>=0&&data is not null&&room.SelectedIndex<data.Classes.Length?data.Classes[room.SelectedIndex].Id:null;
            data=value.Deserialize(MobileJson.Default.CounselingView)!;
            room.ItemsSource=data.Classes.Select(c=>c.Name).ToArray();room.SelectedIndex=Math.Max(0,Array.FindIndex(data.Classes,c=>c.Id==previousRoom));
            childPicker.ItemsSource=data.Students.Select(s=>s.Name+" · "+s.ClassName).ToArray();childPicker.SelectedIndex=Math.Max(0,Array.FindIndex(data.Students,s=>s.Id==previousChild));
            studentField.IsVisible=!data.CanManage;formCard.IsVisible=data.CanManage;filters.ColumnDefinitions[1].Width=data.CanManage?new GridLength(0):GridLength.Star;filters.ColumnSpacing=data.CanManage?0:10;
            heading.Text=data.CanManage?"상담 시간 관리":"선생님과 만나는 시간";if(open is not null)open.IsVisible=data.CanManage;PeriodChoices();Render();
        }
        stack.Add(Act("날짜 조회 · 새로고침",async()=>{await Load();pending=null;form.IsEnabled=true;}));
        form.Add(Text("예약 시간 열기",18,true));form.Add(Card(room,8));
        form.Add(periodGrid);
        var schedule=new HorizontalStackLayout{Spacing=10};schedule.Add(scheduled);schedule.Add(Text("신청 오픈 시각 지정",14));form.Add(schedule);
        var openingFields=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10,IsVisible=false};openingFields.Add(opensDay);openingFields.Add(opensTime,1);form.Add(openingFields);scheduled.Toggled+=(_,e)=>openingFields.IsVisible=e.Value;stack.Add(formCard);
        open=Act("선택한 시간 열기",async()=>
        {
            if(data is not{CanManage:true})return;
            if(pending is null)
            {
                if(room.SelectedIndex<0||room.SelectedIndex>=data.Classes.Length)throw new InvalidOperationException("관리자에게 담당 학급 배정을 요청하세요.");
                var opening=scheduled.IsToggled?CounselingTimes.At(DateOnly.FromDateTime(opensDay.Date??today),(opensTime.Time??TimeSpan.FromHours(9)).ToString(@"hh\:mm")):(long?)null;
                pending=new CounselingOpen(Guid.NewGuid().ToString("N"),data.Classes[room.SelectedIndex].Id,(day.Date??today).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),choices.Where(c=>c.Value.IsChecked).Select(c=>c.Key).ToArray(),opening);form.IsEnabled=false;
            }
            await source.Send("api/counseling/slots",pending);pending=null;form.IsEnabled=true;await Load();
        },true);open.IsVisible=false;stack.Add(open);stack.Add(list);
        childPicker.SelectedIndexChanged+=(_,_)=>Render();
        room.SelectedIndexChanged+=(_,_)=>PeriodChoices();
        await Navigation.PushAsync(page);await Do(Load);open.IsVisible=data?.CanManage==true;
    }
}
