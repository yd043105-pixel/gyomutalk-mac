using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.Controls.Shapes;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    static Border Card(View content,double padding=14,Color? color=null)=>new()
    {
        Content=content,Padding=padding,BackgroundColor=color??Colors.White,
        Stroke=Line,StrokeThickness=1,StrokeShape=new RoundRectangle{CornerRadius=14}
    };
    View Brand(string subtitle="학교와 일상을 잇다",bool signedIn=false)
    {
        var name=new VerticalStackLayout{Spacing=2,VerticalOptions=LayoutOptions.Center};
        var school=Text(schoolName,17,true);school.MaxLines=1;school.LineBreakMode=LineBreakMode.TailTruncation;name.Add(school);var caption=Text(subtitle,11);caption.TextColor=Muted;name.Add(caption);
        var grid=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=9,MinimumHeightRequest=48};
        var logo=new Image{Source="brand_mark.png",WidthRequest=32,HeightRequest=32};SemanticProperties.SetDescription(logo,ProductName);grid.Add(logo);
        grid.Add(name,1);
        if(signedIn)
        {
            var account=new ImageButton{AutomationId="school-account",Source="nav_profile.png",WidthRequest=44,HeightRequest=44,Padding=12,BackgroundColor=Colors.Transparent,CornerRadius=10};
            SemanticProperties.SetDescription(account,"내 정보 열기");account.Clicked+=async(_,_)=>await Run(async()=>{homeTab="account";await Home();});grid.Add(account,2);
        }
        return grid;
    }
    Button MakeAction(string label,Func<Task> action,bool primary=false)
    {
        var button=new Button{Text=label,BackgroundColor=primary?Blue:Colors.White,TextColor=primary?Colors.White:Navy,BorderColor=primary?Blue:Line,BorderWidth=1,CornerRadius=10,Padding=new Thickness(16,10),FontSize=14,MinimumHeightRequest=48,LineBreakMode=LineBreakMode.WordWrap};
#if MACCATALYST
        button.FontSize=16;button.MinimumHeightRequest=42;button.Padding=new Thickness(14,8);
#endif
        button.Clicked+=async(_,_)=>await Run(action);return button;
    }
    void NavigationTabs(bool office)
    {
#if MACCATALYST
        tabs.IsVisible=false;
#else
        tabs.Clear();tabs.IsVisible=true;
        var options=new[]{("home","홈","nav_home.png"),("notices",office?"메시지":"소식","nav_notices.png"),("account","내 정보","nav_profile.png")};
        for(var index=0;index<options.Length;index++)
        {
            var (key,label,icon)=options[index];var selected=homeTab==key;
            var button=MakeAction(label,async()=>{homeTab=key;offset=0;await Home();});
            button.AutomationId="school-tab-"+key;button.ImageSource=icon;button.ContentLayout=new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Top,3);
            button.BackgroundColor=selected?Pale:Colors.Transparent;button.BorderWidth=0;button.CornerRadius=12;button.MinimumHeightRequest=54;button.TextColor=selected?Blue:Muted;button.FontSize=11;button.FontAttributes=selected?FontAttributes.Bold:FontAttributes.None;button.Padding=new Thickness(6,7);
            tabs.Add(button,index);
        }
#endif
    }
    void Login()
    {
        ResetDesktopView();
        StopPhonePresence();schoolName=ProductName;schoolIdentitySource=null;Title=ProductName+" · 학교 앱";homeTab="home";tabs.IsVisible=false;updateNoticeOffered=false;
#if TEACHER_APP
        Header("교직원을 위한 우리 학교 공간","온라인 교무실");
#else
        Header("우리 학교의 하루를 함께","우리 학교 소식");
#endif
        var help=Text("가정통신문부터 시간표까지,\n필요한 학교 소식을 한곳에서 확인하세요.",14);help.TextColor=Muted;help.Margin=new Thickness(0,0,0,6);body.Add(help);
        var mode=new Picker{ClassId="school-field",Title="접속할 공간",ItemsSource=new[]{"학교 소식 · 학생/보호자/교사","교직원 교무실 · 교내망/VPN"},SelectedIndex=Preferences.Default.Get("mode",0)};var modeCard=Card(mode,8);body.Add(modeCard);
#if TEACHER_APP
        mode.SelectedIndex=1;modeCard.IsVisible=false;help.Text="메시지·내 수업·학교 일정·특별실 예약을\n교내망 또는 학교 VPN에서 확인하세요.";
#elif FAMILY_APP
        mode.SelectedIndex=0;modeCard.IsVisible=false;
#endif
        SchoolDirectory.Entry? selected=ReadSelectedSchool(mode.SelectedIndex);
        var selection=Text(selected?.Label??"학교를 검색해 선택하세요",14,true);body.Add(Card(selection));
        var address=Input("관리자 안내 주소 · 기존 설치/시험용",value:Preferences.Default.Get("address-"+mode.SelectedIndex,""));address.Keyboard=Keyboard.Url;
        var addressField=(View)body.Children.Last();addressField.IsVisible=false;
        System.Action clearSchoolLoginFields=()=>{};
        async Task CheckSchool(){if(selected is not null)await SchoolDirectory.Verify(selected,mode.SelectedIndex==1?"office":"announcements",default,CertificatePins.Handler(selected.Address(mode.SelectedIndex==1?"office":"announcements")));else if(string.IsNullOrWhiteSpace(address.Text))throw new InvalidOperationException("학교를 먼저 검색해 선택하세요.");}
        var choose=MakeAction("학교 검색",async()=>
        {
            var school=await ChooseSchool(mode.SelectedIndex==1?"office":"announcements");if(school is null)return;
            var next=school.Address(mode.SelectedIndex==1?"office":"announcements");
            if(selected?.Id!=school.Id||address.Text?.TrimEnd('/')!=next.TrimEnd('/'))
            {
                MessengerApi.ForgetRemembered(address.Text??"",mode.SelectedIndex==1);api?.Dispose();api=null;epoch++;
                clearSchoolLoginFields();
            }
            selected=school;address.Text=next;selection.Text=school.Label;SaveSelectedSchool(school,mode.SelectedIndex);
            Preferences.Default.Set("address-"+mode.SelectedIndex,next);Preferences.Default.Set("mode",mode.SelectedIndex);
        },true);choose.AutomationId="school-search";body.Add(choose);
        var advanced=MakeAction("연결 도움 · 관리자 안내를 받은 경우",()=>{addressField.IsVisible=!addressField.IsVisible;return Task.CompletedTask;});advanced.FontSize=11;body.Add(advanced);
        mode.SelectedIndexChanged+=(_,_)=>{selected=ReadSelectedSchool(mode.SelectedIndex);address.Text=Preferences.Default.Get("address-"+mode.SelectedIndex,"");selection.Text=selected?.Label??"학교를 검색해 선택하세요";help.Text=mode.SelectedIndex==1?"교직원 업무 메시지는 교내망 또는\n학교 VPN에 연결한 뒤 확인하세요.":"가정통신문부터 시간표까지,\n필요한 학교 소식을 한곳에서 확인하세요.";};
        var username=Input("아이디");username.Keyboard=Keyboard.Text;var password=Input("비밀번호",true);
        var remember=new Switch{IsToggled=true};var rememberRow=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};
#if MACCATALYST
        remember.IsToggled=false;
#endif
        var rememberLabel=Text("자동 로그인",13);rememberLabel.VerticalOptions=LayoutOptions.Center;rememberRow.Add(rememberLabel);rememberRow.Add(remember,1);body.Add(rememberRow);
        SemanticProperties.SetDescription(remember,"자동 로그인. 비밀번호는 저장하지 않습니다.");
        clearSchoolLoginFields=()=>{password.Text="";remember.IsToggled=false;};
        address.TextChanged+=(_,_)=>{if(selected is null||address.Text?.TrimEnd('/')==selected.Address(mode.SelectedIndex==1?"office":"announcements").TrimEnd('/'))return;MessengerApi.ForgetRemembered(selected.Address(mode.SelectedIndex==1?"office":"announcements"),mode.SelectedIndex==1);selected=null;SaveSelectedSchool(null,mode.SelectedIndex);selection.Text="학교를 검색해 선택하세요";clearSchoolLoginFields();};
        async Task Connect(bool restore)
        {
            // The password is read once: a first connection to a self-issued school certificate retries after the code check.
            var secret=password.Text??"";
            try
            {
                await ConfirmingCertificate(address.Text??"",async()=>
                {
                    await CheckSchool();
                    api?.Dispose();epoch++;var source=new MessengerApi(address.Text??"",mode.SelectedIndex==1);api=source;source.Invalidated+=()=>{if(api==source)Invalidate();};
                    Preferences.Default.Set("mode",mode.SelectedIndex);Preferences.Default.Set("address-"+mode.SelectedIndex,source.Address.AbsoluteUri);
                    if(restore){await source.Restore();await source.Refresh();if(source.Session.GetProperty("user").ValueKind==JsonValueKind.Null)throw new InvalidOperationException("저장된 로그인이 없습니다. 아이디·비밀번호로 로그인하세요.");}
                    else await source.Login(username.Text??"",secret,remember.IsToggled);
                    homeTab="home";offset=0;child="";unread=false;await Home();
                });
            }
            finally{if(!restore)password.Text="";}
        }
        Action("로그인",()=>Connect(false),true);
        var restore=Action("저장된 로그인으로 접속",()=>Connect(true));restore.BackgroundColor=Colors.Transparent;restore.TextColor=Muted;restore.FontSize=12;
        var joins=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=8};
        var signup=MakeAction("학생·학부모 회원가입",async()=>{if(mode.SelectedIndex==1)throw new InvalidOperationException("교직원 계정 신청은 PC 교무실에서 진행하세요.");await ConfirmingCertificate(address.Text??"",CheckSchool);await Signup(address.Text??"");});signup.FontSize=12;
        var invitation=MakeAction("학교 초대 코드로 가입",async()=>{if(mode.SelectedIndex==1)throw new InvalidOperationException("교직원 계정 신청은 PC 교무실에서 진행하세요.");await ConfirmingCertificate(address.Text??"",CheckSchool);await InviteSignup(address.Text??"");});invitation.FontSize=12;
        joins.Add(signup);joins.Add(invitation,1);body.Add(joins);
#if TEACHER_APP
        joins.IsVisible=false;
#endif
        var privacy=Text("자동 로그인 시 비밀번호는 저장하지 않습니다.",11);privacy.TextColor=Muted;privacy.HorizontalTextAlignment=TextAlignment.Center;body.Add(privacy);
    }
    partial void ResetDesktopView();
    async Task Home()
    {
        var source=api!;var user=source.Session.GetProperty("user");var version=++epoch;
        if(source.Office&&user.TryGetProperty("activationRequired",out var initial)&&initial.GetBoolean()){await ActivateStaff();return;}
        if(source.Office&&user.TryGetProperty("passwordChangeRequired",out var passwordResetRequired)&&passwordResetRequired.GetBoolean()){OfficePasswordChange(true);return;}
        if(schoolIdentitySource!=source)
        {
            var policy=await source.Get("api/registration/policy");if(!Current(source,version))return;
            schoolName=policy.GetProperty("operator").GetString()!;schoolIdentitySource=source;Title=schoolName+(source.Office?" · 교무실":" · 학교 소식");
        }
        await StartPhonePresence();if(!Current(source,version))return;
        var name=user.GetProperty("name").GetString()!;var role=source.Office?"teacher":user.GetProperty("role").GetString();
        Header(name+"님, 안녕하세요",homeTab=="account"?"내 정보":source.Office?"온라인 교무실":"학교 소식",true);NavigationTabs(source.Office);
        await ShowUpdates();if(!Current(source,version))return;
#if MACCATALYST
        if(homeTab!="account"){await DesktopMailbox();return;}
        desktopContent.Content=desktopShared;desktopReader=null;
#endif
        if(homeTab=="account")
        {
            var info=new VerticalStackLayout{Spacing=5};info.Add(Text(name,18,true));var status=Text(source.Office?"교직원 계정":role switch{"parent"=>"학부모 계정","student"=>"학생 계정","teacher"=>"교사 계정",_=>"관리자 계정"},12);status.TextColor=Muted;info.Add(status);body.Add(Card(info));
            if(!source.Office&&role is "parent" or "student")SettingRow(role=="parent"?"설정 · 자녀 연결":"설정 · 내 고유번호",AccountSettings);
            if(source.Office)SettingRow("비밀번호 변경",()=>{OfficePasswordChange();return Task.CompletedTask;});
            SettingRow("약관·개인정보 안내",PolicyMenu);SettingRow("개인정보 열람·정정·삭제 요청",PrivacyRequests);SettingRow("업데이트 내역",()=>ShowUpdates(true));SettingRow("오류 신고 · 개선 의견",Feedback);
#if MAC_DESKTOP
            SettingRow("프로그램 업데이트",()=>MacUpdater.Settings(this));
#endif
            SettingRow("로그아웃",async()=>{try{await source.Logout();}finally{source.Dispose();if(api==source)api=null;Login();}});return;
        }
        if(!source.Office&&role=="parent"&&user.TryGetProperty("membership",out var membership)&&membership.GetString()=="temporary")
        {
            var info=new VerticalStackLayout{Spacing=10};info.Add(Text("자녀를 연결해주세요",18,true));info.Add(Text("임시회원입니다. 자녀 고유번호를 입력하면 학교 소식과 시간표를 확인할 수 있어요.",14));info.Add(MakeAction("설정 · 자녀 연결",AccountSettings,true));body.Add(Card(info));return;
        }
        if(!source.Office&&role=="admin")
        {
            body.Add(Card(Text("관리자 계정입니다. 계정·학급·보호자 연결은 학교 소식 관리 화면에서 관리하세요.",14)));
            Action("관리 화면 열기",()=>Launcher.Default.OpenAsync(source.Address).ContinueWith(_=>{}));return;
        }
        if(!source.Office&&role=="parent")
        {
            var children=await source.Get("api/children");if(!Current(source,version))return;
            var options=children.EnumerateArray().ToArray();var picker=new Picker{ClassId="school-field",Title="자녀",ItemsSource=new[]{"전체 자녀"}.Concat(options.Select(x=>x.GetProperty("name").GetString()+" · "+x.GetProperty("className").GetString())).ToArray(),SelectedIndex=Array.FindIndex(options,x=>x.GetProperty("id").GetString()==child)+1};
            body.Add(Card(picker,6));picker.SelectedIndexChanged+=async(_,_)=>{child=picker.SelectedIndex<=0?"":options[picker.SelectedIndex-1].GetProperty("id").GetString()!;offset=0;await Run(Home);};
        }
        if(homeTab=="home")
        {
            var today=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
            var day=Text("오늘 · "+today.ToString("M월 d일 dddd",CultureInfo.GetCultureInfo("ko-KR")),12,true);day.TextColor=Muted;day.Margin=new Thickness(0,4,0,0);body.Add(day);
            var shortcuts=new List<(string Label,string Icon,Func<Task> Action)>
            {
                (source.Office?"내 시간표":"학급 시간표","action_calendar.png",()=>Timetable()),
                ("상담 예약","nav_profile.png",Counseling)
            };
            if(source.Office)
            {
                shortcuts.Add(("학교 일정","action_calendar.png",()=>OfficeSchedule(false)));
                shortcuts.Add(("특별실 예약","action_room.png",()=>OfficeSchedule(true)));
            }
            if(!source.Office&&role=="student")shortcuts.Add(("수강신청","nav_notices.png",Electives));
            body.Add(QuickActions(shortcuts));
        }
        var scheduleNotices=await source.Get("api/timetable/notices");if(!Current(source,version))return;
        foreach(var notice in scheduleNotices.EnumerateArray().Where(n=>n.GetProperty("readAt").ValueKind==JsonValueKind.Null))
        {
            var revision=notice.GetProperty("revision").GetInt64();var day=notice.GetProperty("date").GetString();
            var room=notice.GetProperty("classIds").EnumerateArray().Select(c=>c.GetString()).FirstOrDefault();
            var change=Action("시간표 변경 · "+notice.GetProperty("title").GetString()+" · "+day,async()=>{if(await Timetable(day,room)&&Current(source,version))await source.Send($"api/timetable/notices/{revision}/read");});change.BackgroundColor=Pale;change.TextColor=Blue;change.FontSize=12;
        }
        var heading=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},Margin=new Thickness(0,8,0,0)};
        var section=Text(source.Office?"업무 메시지":homeTab=="home"?"최근 학교 소식":"받은 학교 소식",16,true);section.VerticalOptions=LayoutOptions.Center;heading.Add(section);
        var refresh=MakeAction("새로고침",async()=>{await source.Refresh();await Home();});refresh.BackgroundColor=Colors.Transparent;refresh.BorderWidth=0;refresh.TextColor=Blue;refresh.FontSize=12;refresh.Padding=0;heading.Add(refresh,1);body.Add(heading);
        if(source.Office)
        {
            if(homeTab=="notices")
            {
                var picker=new Picker{ClassId="school-field",Title="업무",ItemsSource=new[]{"받은 메시지","보낸 메시지","미확인 메시지"},SelectedIndex=view=="sent"?1:view=="unread"?2:0};body.Add(Card(picker,6));picker.SelectedIndexChanged+=async(_,_)=>{view=picker.SelectedIndex==1?"sent":picker.SelectedIndex==2?"unread":"received";offset=0;await Run(Home);};
            }
            var rows=await source.Get("api/messages?box="+(homeTab=="home"?"received":view)+"&offset="+(homeTab=="home"?0:offset));if(!Current(source,version))return;
            foreach(var row in homeTab=="home"?rows.EnumerateArray().Take(3):rows.EnumerateArray())
            {
                var id=row.GetProperty("id").GetString()!;
                var fresh=(homeTab=="home"||view!="sent")&&row.GetProperty("readAt").ValueKind==JsonValueKind.Null;
                Story(row.GetProperty("title").GetString()!,row.GetProperty("senderName").GetString()!,Date(row.GetProperty("createdAt").GetInt64()),fresh,()=>OfficeDetail(id));
            }
            if(rows.GetArrayLength()==0)Empty("받은 메시지가 없습니다","새 업무 메시지가 도착하면 여기에 표시됩니다.");if(homeTab=="notices"&&rows.GetArrayLength()==100)Action("다음 목록",async()=>{offset+=100;await Home();});
            if(homeTab=="home"&&rows.GetArrayLength()>3)Action("메시지 전체 보기",async()=>{homeTab="notices";offset=0;await Home();});
            Action("학생·보호자 공지 작성 / 발송 현황",()=>Compose(true),true);return;
        }
        var filter=new Switch{IsToggled=unread};var filterRow=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};
        var filterLabel=Text("미확인 공지만 보기",12);filterLabel.TextColor=Muted;filterLabel.VerticalOptions=LayoutOptions.Center;filterRow.Add(filterLabel);filterRow.Add(filter,1);body.Add(filterRow);SemanticProperties.SetDescription(filter,"미확인 공지만 보기");filter.Toggled+=async(_,e)=>{unread=e.Value;offset=0;await Run(Home);};
        if(role=="teacher")Action("새 공지 작성",()=>Compose(false),true);
        var notices=await source.Get("api/announcements?offset="+offset+(child.Length>0?"&childId="+child:""));if(!Current(source,version))return;
        var visible=notices.EnumerateArray().Where(row=>!unread||row.GetProperty("readAt").ValueKind==JsonValueKind.Null).ToArray();
        foreach(var row in homeTab=="home"?visible.Take(4):visible)
        {
            var id=row.GetProperty("id").GetString()!;Story(row.GetProperty("title").GetString()!,row.GetProperty("senderName").GetString()!,Date(row.GetProperty("publishedAt").GetInt64()),row.GetProperty("readAt").ValueKind==JsonValueKind.Null,()=>NoticeDetail(id));
        }
        if(visible.Length==0)Empty(unread?"모든 소식을 확인했어요":"아직 도착한 소식이 없어요","학교에서 전달한 소식이 이곳에 표시됩니다.");
        if(homeTab=="home"&&visible.Length>4)Action("학교 소식 전체 보기",async()=>{homeTab="notices";await Home();});
        if(homeTab=="notices"&&notices.GetArrayLength()==100)Action("다음 목록",async()=>{offset+=100;await Home();});
    }
    View QuickActions(List<(string Label,string Icon,Func<Task> Action)> actions)
    {
        var grid=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10,RowSpacing=10};
        for(var index=0;index<actions.Count;index++)
        {
            if(index%2==0)grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var item=actions[index];var button=MakeAction(item.Label,item.Action);button.ClassId="school-shortcut";button.ImageSource=item.Icon;button.ContentLayout=new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left,7);button.FontSize=12;button.Padding=new Thickness(8,10);button.MinimumHeightRequest=64;button.FontAttributes=FontAttributes.Bold;
            button.CornerRadius=12;grid.Add(button,index%2,index/2);
        }
        return grid;
    }
    void SettingRow(string label,Func<Task> action)
    {
        var button=Action(label,action);button.HorizontalOptions=LayoutOptions.Fill;button.TextColor=label=="로그아웃"?Color.FromArgb("#b45353"):Navy;button.FontSize=14;button.Padding=new Thickness(18,14);
    }
    void Story(string title,string sender,string date,bool fresh,Func<Task> action)
    {
        var content=new VerticalStackLayout{Spacing=8};
        var metadata=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8};
        var tag=Text(api?.Office==true?(fresh?"미확인 메시지":"업무 메시지"):fresh?"새 소식":"학교 소식",11,true);tag.TextColor=fresh?Blue:Muted;
        var stamp=Text(date,11);stamp.TextColor=Muted;metadata.Add(tag);metadata.Add(stamp,1);content.Add(metadata);
        var button=MakeAction(title,action);button.ClassId="school-story";button.BackgroundColor=Colors.Transparent;button.BorderWidth=0;button.FontSize=15;button.FontAttributes=fresh?FontAttributes.Bold:FontAttributes.None;button.Padding=0;button.LineBreakMode=LineBreakMode.TailTruncation;button.HorizontalOptions=LayoutOptions.Fill;content.Add(button);
        var author=Text(sender,11);author.TextColor=Muted;author.LineBreakMode=LineBreakMode.TailTruncation;author.MaxLines=1;content.Add(author);
        body.Add(Card(content,14));
    }
    void Empty(string title,string description)
    {
        var stack=new VerticalStackLayout{Spacing=6,Padding=new Thickness(0,10)};stack.Add(Text(title,16,true));var text=Text(description,13);text.TextColor=Muted;stack.Add(text);body.Add(Card(stack));
    }
}
