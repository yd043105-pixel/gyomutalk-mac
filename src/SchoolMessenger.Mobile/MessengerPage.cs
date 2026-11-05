using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage : ContentPage
{
#if FAMILY_APP
    const string ProductName="학교톡";
#else
    const string ProductName="교무톡";
#endif
    public static MessengerPage? Active{get;private set;}
    static readonly Color Navy=Color.FromArgb("#182C47"),Blue=Color.FromArgb("#245BC6"),Muted=Color.FromArgb("#5A6B82"),Line=Color.FromArgb("#DFE7F1"),Pale=Color.FromArgb("#EAF0FC");
#if MACCATALYST
    const string OfficeSpaceLabel="Mac 교직원 메신저";
#else
    const string OfficeSpaceLabel="교사용 학교 앱";
#endif
    readonly VerticalStackLayout body=new(){Spacing=14,Padding=new Thickness(18,18,18,24)};
    readonly Border identityHeader=new(){BackgroundColor=Colors.White,StrokeThickness=0,Padding=new Thickness(18,12)};
    readonly Grid tabs=new(){ColumnDefinitions={new(GridLength.Star),new(GridLength.Star),new(GridLength.Star)},Padding=new Thickness(6,4),ColumnSpacing=4,IsVisible=false};
    readonly Label feedback=new(){TextColor=Color.FromArgb("#a53a38"),FontSize=13,IsVisible=false};
    string homeTab="home";
    MessengerApi? api;
    MessengerApi? schoolIdentitySource;
    string schoolName=ProductName;
    bool busy,covered,restored,resuming,preserveComposerOnResume;
    ContentPage? composer;
    bool updateNoticeOpen,updateNoticeOffered;
    int epoch,offset;
    string view="received",child="";
    bool unread;
    public MessengerPage()
    {
        MessengerApi.CleanupShares();Active=this;Title=ProductName+" · 학교 소식";BackgroundColor=Color.FromArgb("#F3F6FB");
        NavigationPage.SetHasNavigationBar(this,false);
        var layout=new Grid{RowDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)}};
        layout.Add(identityHeader);
        layout.Add(new ScrollView{Content=body},0,1);
        var navigation=new Border{Content=tabs,BackgroundColor=Colors.White,StrokeThickness=0,Padding=new Thickness(8,4,8,8)};
        navigation.SetBinding(IsVisibleProperty,new Binding(nameof(IsVisible),source:tabs));layout.Add(navigation,0,2);Content=layout;
        feedback.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Label.Text))feedback.IsVisible=!string.IsNullOrEmpty(feedback.Text);};Login();
        ConfigureDesktop();
    }
    partial void ConfigureDesktop();
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if(!restored&&api is null)
        {
            restored=true;var mode=Preferences.Default.Get("mode",0);var address=Preferences.Default.Get("address-"+mode,"");
            if(address.Length>0)await Run(()=>ConfirmingCertificate(address,async()=>
            {
                var source=new MessengerApi(address,mode==1);await source.Restore();
                if(!source.Remember){source.Dispose();return;}
                api=source;source.Invalidated+=()=>{if(api==source)Invalidate();};await source.Refresh();
                if(source.Session.GetProperty("user").ValueKind!=JsonValueKind.Null)await Home();
            }));
            return;
        }
        if(api is {Session.ValueKind:JsonValueKind.Object} source&&source.Session.GetProperty("user").ValueKind!=JsonValueKind.Null&&!covered)
            await Run(async()=>{await source.Refresh();if(source.Session.GetProperty("user").ValueKind!=JsonValueKind.Null)await Home();});
    }
    static Label Text(string value,int size=15,bool bold=false)=>new(){Text=value,TextColor=Navy,FontSize=
#if MACCATALYST
        size*1.25,
#else
        size,
#endif
        FontAttributes=bold?FontAttributes.Bold:FontAttributes.None,LineBreakMode=LineBreakMode.WordWrap};
    void Header(string caption,string title,bool signedIn=false)
    {
        body.Clear();identityHeader.Content=Brand(signedIn?(api?.Office==true?OfficeSpaceLabel:"학생·학부모용 학교 앱"):"학교와 일상을 잇다",signedIn);
        var heading=new VerticalStackLayout{Spacing=6};heading.Add(Text(title,signedIn?22:24,true));
        var note=Text(caption,12);note.TextColor=Muted;heading.Add(note);body.Add(heading);
#if !MACCATALYST
        body.Add(feedback);
#endif
        feedback.Text="";
    }
    Entry Input(string title,bool password=false,string? value=null){var field=new VerticalStackLayout{Spacing=4};field.Add(Text(title,12,true));var input=new Entry{ClassId="school-field",Placeholder=title,IsPassword=password,Text=value,TextColor=Navy,BackgroundColor=Colors.Transparent};var border=Card(input);border.Padding=new Thickness(12,0);input.Focused+=(_,_)=>border.Stroke=Blue;input.Unfocused+=(_,_)=>border.Stroke=Line;field.Add(border);body.Add(field);return input;}
    void EnableActions(bool value){foreach(var button in body.Children.OfType<Button>())button.IsEnabled=value;tabs.IsEnabled=value;identityHeader.IsEnabled=value;}
    Button Action(string label,Func<Task> action,bool primary=false){var b=MakeAction(label,action,primary);body.Add(b);return b;}
    async Task Run(Func<Task> action){if(busy||resuming)return;busy=true;body.IsEnabled=false;EnableActions(false);feedback.Text="";try{await action();}catch(Exception e){feedback.Text=e is HttpRequestException?"학교 서버에 연결할 수 없습니다. 주소·인터넷·교내망/VPN을 확인하세요.":e.Message;}finally{busy=false;body.IsEnabled=!resuming;EnableActions(!resuming);}}
    bool Current(MessengerApi source,int version)=>api==source&&version==epoch&&!covered;
    void Invalidate(){epoch++;MainThread.BeginInvokeOnMainThread(()=>{while(Navigation.NavigationStack.Count>1)Navigation.RemovePage(Navigation.NavigationStack.Last());Login();});}
    public void Cover(){covered=true;StopDesktopPresence();epoch++;foreach(var p in Navigation.NavigationStack.OfType<ContentPage>())p.Content.IsVisible=false;}
    public async Task Resume(){var preserve=preserveComposerOnResume&&composer is not null&&Navigation.NavigationStack.Contains(composer);covered=false;if(api is null){foreach(var p in Navigation.NavigationStack.OfType<ContentPage>())p.Content.IsVisible=true;return;}resuming=true;body.IsEnabled=false;EnableActions(false);try{await api.Refresh();if(api.Session.ValueKind!=JsonValueKind.Undefined&&api.Session.GetProperty("user").ValueKind!=JsonValueKind.Null){await StartDesktopPresence();if(!preserve){while(Navigation.NavigationStack.Count>1)await Navigation.PopAsync(false);await Home();}}}catch(Exception e){Invalidate();feedback.Text=e.Message;}finally{preserveComposerOnResume=false;resuming=false;body.IsEnabled=!busy;EnableActions(!busy);foreach(var p in Navigation.NavigationStack.OfType<ContentPage>())p.Content.IsVisible=true;}}
    static string Date(long value)=>DateTimeOffset.FromUnixTimeMilliseconds(value).ToOffset(TimeSpan.FromHours(9)).ToString("MM.dd HH:mm",System.Globalization.CultureInfo.InvariantCulture);
    async Task ShowUpdates(bool manual=false)
    {
        if(updateNoticeOpen||(!manual&&updateNoticeOffered))return;
        try
        {
            var notice=SchoolMessenger.Shared.ReleaseNotice.Load("desktop");
            var confirmed=Preferences.Default.Get("update-confirmed-sequence",0);
            if(!manual&&!notice.ShouldShow(confirmed))return;
            updateNoticeOffered=true;
            updateNoticeOpen=true;
            if(await DisplayAlertAsync("업데이트 내역 · "+notice.Version,notice.Text,"확인","나중에"))
                Preferences.Default.Set("update-confirmed-sequence",Math.Max(confirmed,notice.Sequence));
        }
        catch(Exception e) when(e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {feedback.Text="업데이트 내역을 읽거나 확인 기록을 저장하지 못했습니다.";}
        finally{updateNoticeOpen=false;}
    }
    async Task OfficeDetail(string id)
    {
        var source=api!;var version=epoch;var value=await source.Get("api/messages/"+id);if(!Current(source,version))return;
#if MACCATALYST
        value=DesktopCopy(source,value);
#endif
        var message=value.GetProperty("message");
        // Once the sender's and every recipient's PC saved the message, the server keeps only its title and receipts.
        var purged=message.TryGetProperty("purged",out var gone)&&gone.ValueKind==JsonValueKind.True;
        var page=Detail(message.GetProperty("title").GetString()!,purged?PurgedBody:message.GetProperty("body").GetString()!,value.GetProperty("attachments"),source);
        if(!purged&&message.TryGetProperty("format",out var raw)&&raw.ValueKind==JsonValueKind.Object)
        {
            var format=raw.Deserialize(MobileJson.Default.MessageFormat);
            var body=message.GetProperty("body").GetString()!;
            if(SchoolMessenger.Contracts.MessageFormatRules.Valid(format,body)&&format is not null)
            {
                var content=(VerticalStackLayout)((ScrollView)page.Content).Content;
                var label=(Label)content.Children[1];var styled=new FormattedString();
                foreach(var run in format.Runs)
                {
                    var span=new Span{Text=run.Text,FontSize=run.Size*96/72,FontAttributes=(run.Bold?FontAttributes.Bold:FontAttributes.None)|(run.Italic?FontAttributes.Italic:FontAttributes.None),TextDecorations=(run.Underline?TextDecorations.Underline:TextDecorations.None)|(run.Strike?TextDecorations.Strikethrough:TextDecorations.None)};
                    // Windows fonts are not bundled; mobile uses its readable system font.
                    if(run.Color is not null)span.TextColor=Color.FromArgb(run.Color);
                    if(run.Highlight is not null)span.BackgroundColor=Color.FromArgb(run.Highlight);
                    styled.Spans.Add(span);
                }
                label.FormattedText=styled;
            }
        }
        var userId=source.Session.GetProperty("user").GetProperty("id").GetString();
        ConfigureDesktopDetail(page,value,source);
        if(value.GetProperty("recipients").EnumerateArray().Any(r=>r.GetProperty("id").GetString()==userId))await source.Send("api/messages/"+id+"/read");
        if(!Current(source,version))return;var handled=false;DisplayDesktopDetail(page,ref handled);if(!handled)await Navigation.PushAsync(page);
    }
    const string PurgedBody="이 메시지 본문은 PC 교무톡에 보관되어 있어 이 기기에서는 볼 수 없습니다. PC에서 확인하세요.";
    partial void ConfigureDesktopDetail(ContentPage page,JsonElement value,MessengerApi source);
    partial void DisplayDesktopDetail(ContentPage page,ref bool handled);
    ContentPage Detail(string title,string text,JsonElement files,MessengerApi source)
    {
        var page=new ContentPage{Title=title,BackgroundColor=BackgroundColor};var stack=new VerticalStackLayout{Padding=24,Spacing=18};page.Content=new ScrollView{Content=stack};stack.Add(Text(title,24,true));stack.Add(Text(text,16));
        stack.Add(Text("첨부는 전송일부터 30일간 보관됩니다. 저장한 사본은 직접 관리하세요.",12));
        foreach(var file in files.EnumerateArray())
        {
            var expired=file.TryGetProperty("expired",out var flag)&&flag.GetBoolean();var id=file.GetProperty("id").GetString()!;var name=file.GetProperty("name").GetString()!;
            var b=new Button{Text=name+(expired?" · 기간 만료":" · 저장/공유"),IsEnabled=!expired};b.Clicked+=async(_,_)=>{b.IsEnabled=false;try{await source.ShareFile(id,name);}catch(Exception e){await page.DisplayAlertAsync("첨부파일",e.Message,"확인");}finally{b.IsEnabled=true;}};stack.Add(b);
        }
        return page;
    }
}
