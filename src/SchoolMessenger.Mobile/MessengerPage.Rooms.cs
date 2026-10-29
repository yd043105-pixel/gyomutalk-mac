using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    async Task RoomBookingEditor(MessengerApi source,JsonElement setup,JsonElement? existing,Func<Task> refresh)
    {
        var version=epoch;var today=DateTimeOffset.UtcNow.ToOffset(SchoolDay.KoreaOffset).Date;
        var start=existing is{} old?DateTimeOffset.FromUnixTimeMilliseconds(old.GetProperty("start").GetInt64()).ToOffset(SchoolDay.KoreaOffset):new DateTimeOffset(today.AddDays(1),SchoolDay.KoreaOffset);
        var end=existing is{} oldEnd?DateTimeOffset.FromUnixTimeMilliseconds(oldEnd.GetProperty("end").GetInt64()).ToOffset(SchoolDay.KoreaOffset):start;
        var page=new ContentPage{Title="특별실 예약 · 1~7교시",BackgroundColor=BackgroundColor};var stack=new VerticalStackLayout{Padding=20,Spacing=12};page.Content=new ScrollView{Content=stack};
        stack.Add(Text("예약할 공간과 교시",23,true));stack.Add(Text("학년별 수업 시각을 적용하며 7교시 16:40까지 예약할 수 있습니다.",13));
        var title=new Entry{Placeholder="예약 제목",MaxLength=200,Text=existing?.GetProperty("title").GetString()};stack.Add(Card(title,10));
        var date=new DatePicker{Date=start.Date,Format="yyyy.MM.dd"};stack.Add(Text("예약 날짜 · 한국 시간",12,true));stack.Add(Card(date,8));
        var grade=new Picker{Title="학년",ItemsSource=new[]{"1학년","2학년","3학년"},SelectedIndex=existing is{} value&&value.TryGetProperty("grade",out var g)?Math.Clamp(g.GetInt32()-1,0,2):0};stack.Add(Card(grade,8));
        var first=new Picker{Title="시작 교시"};var last=new Picker{Title="마지막 교시"};SchoolPeriod[] periods=[];
        void Choices()
        {
            var oldFirst=first.SelectedIndex>=0&&first.SelectedIndex<periods.Length?periods[first.SelectedIndex].Number:0;var oldLast=last.SelectedIndex>=0&&last.SelectedIndex<periods.Length?periods[last.SelectedIndex].Number:0;
            periods=SchoolDay.Lessons(Math.Clamp(grade.SelectedIndex+1,1,3),7);first.ItemsSource=last.ItemsSource=periods.Select(p=>p.Label).ToArray();
            first.SelectedIndex=Math.Max(0,Array.FindIndex(periods,p=>oldFirst>0?p.Number==oldFirst:p.Start==start.ToString("HH:mm")));
            last.SelectedIndex=Math.Max(first.SelectedIndex,Array.FindIndex(periods,p=>oldLast>0?p.Number==oldLast:p.End==end.ToString("HH:mm")));
        }
        grade.SelectedIndexChanged+=(_,_)=>Choices();Choices();stack.Add(Card(first,8));stack.Add(Card(last,8));
        var rooms=setup.GetProperty("rooms").EnumerateArray().Where(r=>r.GetProperty("active").GetBoolean()).ToArray();var room=new Picker{Title="특별실",ItemsSource=rooms.Select(r=>r.GetProperty("name").GetString()!).ToArray(),SelectedIndex=existing is{} row?Array.FindIndex(rooms,r=>r.GetProperty("id").GetString()==row.GetProperty("roomId").GetString()):rooms.Length>0?0:-1};stack.Add(Card(room,8));
        stack.Add(Text("조회·점심·청소·8교시·야간은 별도로 예약할 수 없습니다. 여러 교시는 사이의 쉬는 시간도 예약 구간에 포함합니다.",12));
        var error=Text("",13);error.TextColor=Color.FromArgb("#A53A38");stack.Add(error);
        var client=existing?.GetProperty("clientId").GetString()??Guid.NewGuid().ToString("N");var save=new Button{Text="특별실 예약 저장",BackgroundColor=Blue,TextColor=Colors.White,CornerRadius=14,MinimumHeightRequest=48};
        save.Clicked+=async(_,_)=>
        {
            if(!Current(source,version)||!save.IsEnabled)return;page.Content.IsEnabled=false;save.IsEnabled=false;
            try
            {
                if(date.Date is not{} day||grade.SelectedIndex<0||room.SelectedIndex<0||first.SelectedIndex<0||last.SelectedIndex<first.SelectedIndex)throw new InvalidOperationException("학년·특별실·날짜·교시 순서를 확인하세요.");
                var packet=new OfficeBookingRequest(client,rooms[room.SelectedIndex].GetProperty("id").GetString()!,title.Text??"",SchoolDay.Timestamp(day,periods[first.SelectedIndex].Start),SchoolDay.Timestamp(day,periods[last.SelectedIndex].End),existing?.GetProperty("revision").GetInt32()??0,grade.SelectedIndex+1);
                await source.Send("api/office/bookings"+(existing is{} oldRow?"/"+oldRow.GetProperty("id").GetString():""),packet);if(!Current(source,version))return;await Navigation.PopAsync();await refresh();
            }
            catch(Exception e){if(Current(source,version))error.Text=e.Message;}finally{if(Current(source,version)){page.Content.IsEnabled=true;save.IsEnabled=true;}}
        };
        stack.Add(save);await Navigation.PushAsync(page);
    }
    async Task OfficeRooms(MessengerApi source)
    {
        var version=epoch;var setup=await source.Get("api/office/setup");if(!Current(source,version))return;
        if(!setup.GetProperty("roomManager").GetBoolean())throw new InvalidOperationException("특별실 운영 담당 권한이 필요합니다.");
        var page=new ContentPage{Title="특별실 목록 설정",BackgroundColor=BackgroundColor};var stack=new VerticalStackLayout{Padding=20,Spacing=12};page.Content=new ScrollView{Content=stack};stack.Add(Text("특별실 목록 설정",23,true));
        stack.Add(Text("이름·위치를 수정하고 신규 예약을 허용하거나 중지합니다. 중지해도 기존 기록은 유지합니다.",13));
        foreach(var row in setup.GetProperty("rooms").EnumerateArray())
        {
            var details=new VerticalStackLayout{Spacing=8};var name=new Entry{Text=row.GetProperty("name").GetString(),MaxLength=80,Placeholder="특별실 이름"};var location=new Entry{Text=row.GetProperty("location").GetString(),MaxLength=100,Placeholder="위치"};var active=new Switch{IsToggled=row.GetProperty("active").GetBoolean()};details.Add(name);details.Add(location);details.Add(Text("신규 예약 허용",12));details.Add(active);
            details.Add(MakeAction("특별실 설정 저장",async()=>{await source.Send("api/office/rooms/"+row.GetProperty("id").GetString(),new OfficeRoomRequest(name.Text??"",location.Text??"",active.IsToggled,row.GetProperty("revision").GetInt32()));if(!Current(source,version))return;await Navigation.PopAsync();await OfficeRooms(source);}));stack.Add(Card(details));
        }
        await Navigation.PushAsync(page);
    }
}
