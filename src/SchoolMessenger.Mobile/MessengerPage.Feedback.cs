using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    Task Feedback()=>FeedbackForm(false);
    Task PrivacyRequests()=>FeedbackForm(true);
    async Task FeedbackForm(bool privacy)
    {
        var source=api!;var version=epoch;var page=new ContentPage{Title=privacy?"개인정보 권리 요청":"오류 신고 · 개선 의견",BackgroundColor=BackgroundColor};
        var stack=new VerticalStackLayout{Padding=24,Spacing=12};page.Content=new ScrollView{Content=stack};
        if(privacy){var policy=await source.Get("api/registration/policy");if(!Current(source,version))return;stack.Add(Text("처리 주체: "+policy.GetProperty("operator").GetString()+"\n문의: "+policy.GetProperty("contact").GetString(),13));}
        stack.Add(Text(privacy?"학교가 로그인 계정과 요청 내용을 확인하여 처리하고 이 화면에 회신합니다. 요청만으로 자료가 삭제되지는 않습니다. 비밀번호·주민등록번호·증명서 사진은 적거나 첨부하지 마세요. 로그인할 수 없으면 위 학교 문의처로 연락하세요.":"이름·학번·연락처·비밀번호·메시지 본문을 적지 마세요. 학교 서버에만 접수하며 자동으로 공개하지 않습니다.",13));
        var kind=new Picker{Title="종류",ItemsSource=privacy?new[]{"개인정보 열람","개인정보 정정","개인정보 삭제·탈퇴·동의 철회","개인정보 처리정지"}:new[]{"오류 신고","개선 의견"},SelectedIndex=0};
        var area=new Picker{Title="기능",ItemsSource=new[]{"로그인","메시지","학교 공지","시간표","상담","특별실","파일","기타"},SelectedIndex=7};
        var title=new Entry{Placeholder="제목 (3~100자)",MaxLength=100};
        var details=new Editor{Placeholder=privacy?"요청할 정보와 원하는 처리 내용 (10자 이상)":"재현 순서 · 예상 결과 · 실제 결과 (10자 이상)",MaxLength=4000,HeightRequest=150};
        var error=Text("",13);var send=new Button{Text="학교에 접수",BackgroundColor=Blue,TextColor=Colors.White};FeedbackRequest? pending=null;
        send.Clicked+=async(_,_)=>
        {
            if(!Current(source,version))return;send.IsEnabled=false;error.Text="";
            try
            {
                if(pending is null&&(title.Text?.Trim().Length is not >=3||details.Text?.Trim().Length is not >=10))throw new InvalidOperationException("제목 3자, 재현 설명 10자 이상 적어 주세요.");
                pending??=new(Guid.NewGuid().ToString("N"),privacy?"privacy":kind.SelectedIndex==0?"bug":"suggestion",source.ProductCode,privacy?new[]{"privacy-access","privacy-rectify","privacy-erase","privacy-restrict"}[kind.SelectedIndex]:new[]{"login","messages","notices","timetable","counseling","rooms","files","other"}[area.SelectedIndex],title.Text??"",details.Text??"",AppInfo.Current.VersionString);
                kind.IsEnabled=area.IsEnabled=title.IsEnabled=details.IsEnabled=false;
                var response=await source.Send("api/feedback",pending);pending=null;if(!Current(source,version))return;await Navigation.PopAsync();feedback.Text="접수 완료 · "+response.GetProperty("id").GetString();
            }
            catch(Exception exception){error.Text=exception.Message;}
            finally{send.IsEnabled=true;}
        };
        stack.Add(kind);if(!privacy)stack.Add(area);foreach(var view in new View[]{title,details,send,error})stack.Add(view);
        var refresh=new Button{Text="접수 현황 새로고침"};refresh.Clicked+=async(_,_)=>{if(!Current(source,version))return;refresh.IsEnabled=false;try{await Navigation.PopAsync();if(Current(source,version))await FeedbackForm(privacy);}catch(Exception exception){feedback.Text=exception.Message;}finally{refresh.IsEnabled=true;}};stack.Add(refresh);
        stack.Add(Text("내 접수 현황",18,true));
        foreach(var row in (await source.Get("api/feedback")).EnumerateArray())
        {
            if((row.GetProperty("report").GetProperty("kind").GetString()=="privacy")!=privacy)continue;
            stack.Add(Text(row.GetProperty("report").GetProperty("title").GetString()+" · "+(row.GetProperty("status").GetString() switch{"received"=>"접수","investigating"=>"확인 중","verified"=>"수정 대기","deferred"=>"보류","resolved"=>"처리 완료",_=>"확인 중"}),13));
            if(privacy&&row.GetProperty("developmentSummary").GetString() is {Length:>0} reply)stack.Add(Text("학교 회신: "+reply,13));
        }
        if(Current(source,version))await Navigation.PushAsync(page);
    }
}
