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
        var username=Input("아이디");username.Keyboard=Keyboard.Text;var password=Input("비밀번호",true);
        var remember=new Switch{IsToggled=true};var rememberRow=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};
#if MACCATALYST
        remember.IsToggled=false;
#endif
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
        desktopContent.Content=desktopShared;desktopReader=null;
        var info=new VerticalStackLayout{Spacing=5};info.Add(Text(name,18,true));info.Add(Text("교직원 계정",12));body.Add(Card(info));
        SettingRow("비밀번호 변경",()=>{OfficePasswordChange();return Task.CompletedTask;});
        SettingRow("약관·개인정보 안내",PolicyMenu);SettingRow("개인정보 열람·정정·삭제 요청",PrivacyRequests);
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
