using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.Controls.Shapes;

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
        StopDesktopPresence();schoolName=ProductName;schoolIdentitySource=null;Title=ProductName+" · 교직원 메신저";homeTab="home";tabs.IsVisible=false;updateNoticeOffered=false;
#if TEACHER_APP
        Header("교직원을 위한 우리 학교 공간","온라인 교무실");
#else
        Header("우리 학교의 하루를 함께","우리 학교 소식");
#endif
        var help=Text("교직원 메시지와 시간표, 학교 일정을 확인하세요.",14);help.TextColor=Muted;help.Margin=new Thickness(0,0,0,6);body.Add(help);
        var mode=new Picker{ClassId="school-field",Title="접속할 공간",ItemsSource=new[]{"교직원 교무실","교직원 교무실 · 교내망/VPN"},SelectedIndex=Preferences.Default.Get("mode",0)};var modeCard=Card(mode,8);body.Add(modeCard);
#if TEACHER_APP
        mode.SelectedIndex=1;modeCard.IsVisible=false;help.Text="메시지·내 수업·학교 일정·특별실 예약을\n교내망 또는 학교 VPN에서 확인하세요.";
#elif FAMILY_APP
        mode.SelectedIndex=0;modeCard.IsVisible=false;
#endif
        var address=Input("학교 서버 주소",value:Preferences.Default.Get("address-"+mode.SelectedIndex,""));address.Keyboard=Keyboard.Url;address.Placeholder="https://192.168.0.10:5443";address.AutomationId="school-address";
        var addressHint=Text("관리자 프로그램에 표시된 접속 주소를 입력하세요. 처음 연결할 때 서버 PC에 표시된 연결 확인 코드와 비교합니다.",11);addressHint.TextColor=Muted;body.Add(addressHint);
        System.Action clearSchoolLoginFields=()=>{};var shownAddress=address.Text??"";
        mode.SelectedIndexChanged+=(_,_)=>{shownAddress=Preferences.Default.Get("address-"+mode.SelectedIndex,"");address.Text=shownAddress;help.Text=mode.SelectedIndex==1?"교직원 업무 메시지는 교내망 또는\n학교 VPN에 연결한 뒤 확인하세요.":"교직원 메시지와 학교 업무를 확인하세요.";};
        var username=Input("이름");username.Keyboard=Keyboard.Text;var password=Input("비밀번호",true);
        // On from the first start, as on Windows: the sign-in stays in this Mac's keychain until 로그아웃.
        var remember=new Switch{IsToggled=true};var rememberRow=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};
        var rememberLabel=Text("자동 로그인",13);rememberLabel.VerticalOptions=LayoutOptions.Center;rememberRow.Add(rememberLabel);rememberRow.Add(remember,1);body.Add(rememberRow);
        SemanticProperties.SetDescription(remember,"자동 로그인. 비밀번호는 저장하지 않습니다.");
        clearSchoolLoginFields=()=>{password.Text="";remember.IsToggled=false;};
        // Each school keeps its own sign-in, so moving to another address forgets the remembered one.
        address.TextChanged+=(_,_)=>{if((address.Text??"").Trim().TrimEnd('/')==shownAddress.Trim().TrimEnd('/'))return;MessengerApi.ForgetRemembered(shownAddress,mode.SelectedIndex==1);shownAddress=address.Text??"";clearSchoolLoginFields();};
        async Task Connect(bool restore)
        {
            // The password is read once: a first connection to a self-issued school certificate retries after the code check.
            var secret=password.Text??"";RequireServerAddress(address.Text);
            try
            {
                await ConfirmingCertificate(address.Text??"",async()=>
                {
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
        // 계정 신청, as on the Windows sign-in: the administrator approves it once, then the teacher signs in.
        var register=Action("처음 사용하시나요? 계정 신청",()=>{RequireServerAddress(address.Text);return MacRegister(address.Text??"");});register.BackgroundColor=Colors.Transparent;register.TextColor=Blue;register.FontSize=12;register.AutomationId="mac-register";
    }
    async Task MacRegister(string address)
    {
        var page=DesktopPage("계정 신청");var stack=new VerticalStackLayout{Padding=new Thickness(28,20),Spacing=8,MaximumWidthRequest=520};((Grid)page.Content).Add(new ScrollView{Content=stack},0,1);
        stack.Add(Text("계정 신청",22,true));var intro=Text("관리자가 한 번 승인하면 로그인할 수 있습니다.",13);intro.TextColor=Muted;stack.Add(intro);
        Entry Field(string label,string? note=null,bool secret=false)
        {
            stack.Add(Text(label,12,true));var box=new Entry{ClassId="school-field",IsPassword=secret,FontSize=15,TextColor=Navy};stack.Add(DesktopPanel(box,6));
            if(note is not null){var hint=Text(note,11);hint.TextColor=Muted;stack.Add(hint);}return box;
        }
        var name=Field("이름","로그인할 때도 이 이름을 씁니다. 같은 이름이 이미 있으면 홍길동.2처럼 뒤에 숫자를 붙이세요.");
        var department=Field("부서");var password=Field("비밀번호 · 8~128자",null,true);var confirm=Field("비밀번호 확인",null,true);
        var result=Text("",13);result.TextColor=Blue;
        var send=DesktopButton("관리자에게 계정 신청",null,true);send.AutomationId="mac-register-send";send.Margin=new Thickness(0,10,0,0);stack.Add(send);stack.Add(result);
        var note=Text("승인 전에는 메시지와 교직원 목록에 접근할 수 없습니다.",11);note.TextColor=Faint;stack.Add(note);
        page.Disappearing+=(_,_)=>{password.Text="";confirm.Text="";};
        var sending=false;
        send.Clicked+=async(_,_)=>
        {
            if(sending)return;
            if((password.Text??"")!=(confirm.Text??"")){result.TextColor=Danger;result.Text="비밀번호 확인이 일치하지 않습니다.";return;}
            sending=true;send.IsEnabled=false;
            try
            {
                await ConfirmingCertificate(address,async()=>
                {
                    using var source=new MessengerApi(address,true);
                    var answer=await MacSend(source,"api/register",new System.Text.Json.Nodes.JsonObject{["username"]=name.Text?.Trim()??"",["department"]=department.Text?.Trim()??"",["password"]=password.Text??""});
                    password.Text="";confirm.Text="";result.TextColor=Blue;result.Text=answer.GetProperty("message").GetString()??"신청했습니다.";
                    send.Text="신청 완료 · 승인 후 로그인하세요";
                });
            }
            catch(Exception error){result.TextColor=Danger;result.Text=error is HttpRequestException?"학교 서버에 연결할 수 없습니다. 주소·교내망/VPN을 확인하세요.":error.Message;send.IsEnabled=true;}
            finally{sending=false;}
        };
        await Navigation.PushAsync(page);
    }

    partial void ResetDesktopView();
    async Task Home()
    {
        var source=api!;var user=source.Session.GetProperty("user");var version=++epoch;
        if(user.TryGetProperty("activationRequired",out var initial)&&initial.GetBoolean()){await ActivateStaff();return;}
        if(user.TryGetProperty("passwordChangeRequired",out var reset)&&reset.GetBoolean()){OfficePasswordChange(true);return;}
        if(schoolIdentitySource!=source)
        {
            var policy=await source.Get("api/registration/policy");if(!Current(source,version))return;
            schoolName=policy.GetProperty("operator").GetString()!;schoolIdentitySource=source;Title=schoolName+" · 교무톡";
        }
        await StartDesktopPresence();if(!Current(source,version))return;
        var name=user.GetProperty("name").GetString()!;
        Header(name+"님, 안녕하세요",homeTab=="account"?"내 정보":"온라인 교무실",true);NavigationTabs(true);
        await ShowUpdates();if(!Current(source,version))return;
        if(homeTab!="account"){await DesktopMailbox();return;}
        MacShowPane("account",desktopShared);desktopReader=null;
        var info=new VerticalStackLayout{Spacing=5};info.Add(Text(name,18,true));info.Add(Text("교직원 계정",12));body.Add(Card(info));
        // The same switches as the Windows settings menu; they are kept on this Mac only.
        SwitchRow("알림 소리 끄기","새 메시지·채팅 알림이 소리 없이 뜹니다.",MacQuiet,on=>MacQuiet=on,"mac-setting-quiet");
        SwitchRow("방해금지","알림 배너를 띄우지 않습니다. 왼쪽 메뉴의 숫자는 그대로 보입니다.",MacDoNotDisturb,on=>MacDoNotDisturb=on,"mac-setting-dnd");
        SwitchRow("조직도 작게 보기","조직도에서 한 사람을 한 줄로 작게 보여 스크롤을 덜 합니다.",MacCompactRoster,on=>MacCompactRoster=on,"mac-setting-compact");
        if(MacFeature("ai"))SettingRow("AI 연결",()=>MacAiSettings());
        SettingRow("비밀번호 변경",()=>{OfficePasswordChange();return Task.CompletedTask;});
        SettingRow("개인정보 안내",PolicyMenu);SettingRow("개인정보 열람·정정·삭제 요청",PrivacyRequests);
        SettingRow("업데이트 내역",()=>ShowUpdates(true));SettingRow("오류 신고 · 개선 의견",Feedback);
        SettingRow("프로그램 업데이트",()=>MacUpdater.Settings(this));
        SettingRow("로그아웃",async()=>{try{await source.Logout();}finally{source.Dispose();if(api==source)api=null;Login();}});
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
    void SwitchRow(string label,string note,bool value,Action<bool> change,string id)
    {
        var words=new VerticalStackLayout{Spacing=2,VerticalOptions=LayoutOptions.Center};words.Add(Text(label,14,true));var hint=Text(note,11);hint.TextColor=Muted;words.Add(hint);
        var toggle=new Switch{IsToggled=value,AutomationId=id,VerticalOptions=LayoutOptions.Center};toggle.Toggled+=(_,e)=>change(e.Value);
        SemanticProperties.SetDescription(toggle,label);
        var row=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=12};row.Add(words);row.Add(toggle,1);body.Add(Card(row,12));
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
