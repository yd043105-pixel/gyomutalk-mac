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
            if(address.Length>0)await Run(async()=>
            {
                var school=ReadSelectedSchool(mode);if(school is not null)await SchoolMessenger.Shared.SchoolDirectory.Verify(school,mode==1?"office":"announcements");
                var source=new MessengerApi(address,mode==1);await source.Restore();
                if(!source.Remember){source.Dispose();return;}
                api=source;source.Invalidated+=()=>{if(api==source)Invalidate();};await source.Refresh();
                if(source.Session.GetProperty("user").ValueKind!=JsonValueKind.Null)await Home();
            });
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
    public void Cover(){covered=true;StopPhonePresence();epoch++;foreach(var p in Navigation.NavigationStack.OfType<ContentPage>())p.Content.IsVisible=false;}
    public async Task Resume(){var preserve=preserveComposerOnResume&&composer is not null&&Navigation.NavigationStack.Contains(composer);covered=false;if(api is null){foreach(var p in Navigation.NavigationStack.OfType<ContentPage>())p.Content.IsVisible=true;return;}resuming=true;body.IsEnabled=false;EnableActions(false);try{await api.Refresh();if(api.Session.ValueKind!=JsonValueKind.Undefined&&api.Session.GetProperty("user").ValueKind!=JsonValueKind.Null){await StartPhonePresence();if(!preserve){while(Navigation.NavigationStack.Count>1)await Navigation.PopAsync(false);await Home();}}}catch(Exception e){Invalidate();feedback.Text=e.Message;}finally{preserveComposerOnResume=false;resuming=false;body.IsEnabled=!busy;EnableActions(!busy);foreach(var p in Navigation.NavigationStack.OfType<ContentPage>())p.Content.IsVisible=true;}}
    static string Date(long value)=>DateTimeOffset.FromUnixTimeMilliseconds(value).ToOffset(TimeSpan.FromHours(9)).ToString("MM.dd HH:mm",System.Globalization.CultureInfo.InvariantCulture);
    async Task ShowUpdates(bool manual=false)
    {
        if(updateNoticeOpen||(!manual&&updateNoticeOffered))return;
        try
        {
            var notice=SchoolMessenger.Shared.ReleaseNotice.Load("mobile");
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
        var page=Detail(value.GetProperty("message").GetProperty("title").GetString()!,value.GetProperty("message").GetProperty("body").GetString()!,value.GetProperty("attachments"),source);
        var message=value.GetProperty("message");
        if(message.TryGetProperty("format",out var raw)&&raw.ValueKind==JsonValueKind.Object)
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
    partial void ConfigureDesktopDetail(ContentPage page,JsonElement value,MessengerApi source);
    partial void DisplayDesktopDetail(ContentPage page,ref bool handled);
    ContentPage Detail(string title,string text,JsonElement files,MessengerApi source,bool inline=false)
    {
        var page=new ContentPage{Title=title,BackgroundColor=BackgroundColor};var stack=new VerticalStackLayout{Padding=24,Spacing=18};page.Content=new ScrollView{Content=stack};stack.Add(Text(title,24,true));stack.Add(Text(text,16));
        stack.Add(Text(inline?"공지 이미지 · 바로 읽거나 확대할 수 있습니다. 30일 보관":"첨부는 게시·전송일부터 30일간 보관됩니다. 저장한 사본은 직접 관리하세요.",12));
        foreach(var file in files.EnumerateArray())
        {
            var expired=file.TryGetProperty("expired",out var flag)&&flag.GetBoolean();var id=file.GetProperty("id").GetString()!;var name=file.GetProperty("name").GetString()!;
            if(inline)
            {
                if(expired){stack.Add(Text(name+" · 보관 기간 만료",13));continue;}
                var image=new Image{Aspect=Aspect.AspectFit,HeightRequest=460,HorizontalOptions=LayoutOptions.Fill};SemanticProperties.SetDescription(image,name);
                var status=Text(name+" · 불러오는 중",12);var zoom=new Button{Text="확대해서 읽기",IsEnabled=false};var loading=false;stack.Add(image);stack.Add(status);stack.Add(zoom);
                async Task LoadImage()
                {
                    if(loading)return;loading=true;zoom.IsEnabled=false;image.Source=null;var imageEpoch=epoch;
                    try{var bytes=await source.NoticeImage(id);if(!Current(source,imageEpoch))return;image.Source=ImageSource.FromStream(()=>new MemoryStream(bytes,false));status.Text=name;zoom.IsEnabled=true;}
                    catch(Exception e){status.Text=e.Message;}
                    finally{loading=false;}
                }
                image.Loaded+=async(_,_)=>await LoadImage();
                var retry=new Button{Text="이미지 새로고침"};retry.Clicked+=async(_,_)=>await LoadImage();stack.Add(retry);
                zoom.Clicked+=async(_,_)=>
                {
                    zoom.IsEnabled=false;var imageEpoch=epoch;
                    try
                    {
                        // Recheck recipient permission before opening another image view.
                        var bytes=await source.NoticeImage(id);if(!Current(source,imageEpoch))return;
                        var large=new Image{Source=ImageSource.FromStream(()=>new MemoryStream(bytes,false)),Aspect=Aspect.AspectFit,WidthRequest=1200,HeightRequest=1700};SemanticProperties.SetDescription(large,name);
                        var reader=new ContentPage{Title="공지 이미지 확대",Content=new ScrollView{Orientation=ScrollOrientation.Both,Content=large}};await Navigation.PushAsync(reader);
                    }
                    catch(Exception e){await page.DisplayAlertAsync("공지 이미지",e.Message,"확인");}
                    finally{zoom.IsEnabled=true;}
                };
                continue;
            }
            var b=new Button{Text=name+(expired?" · 기간 만료":" · 저장/공유"),IsEnabled=!expired};b.Clicked+=async(_,_)=>{b.IsEnabled=false;try{await source.ShareFile(id,name);}catch(Exception e){await page.DisplayAlertAsync("첨부파일",e.Message,"확인");}finally{b.IsEnabled=true;}};stack.Add(b);
        }
        return page;
    }
    async Task NoticeDetail(string id)
    {
        var source=api!;var version=epoch;var value=await source.Get("api/announcements/"+id);if(!Current(source,version))return;var notice=value.GetProperty("notice");var page=Detail(notice.GetProperty("title").GetString()!,notice.GetProperty("body").GetString()!,value.GetProperty("attachments"),source,true);var stack=(VerticalStackLayout)((ScrollView)page.Content).Content;
        if(source.Session.GetProperty("user").GetProperty("role").GetString()=="teacher")
        {
            stack.Add(Text("확인 "+notice.GetProperty("readCount")+" / "+notice.GetProperty("recipientCount"),13));foreach(var receipt in value.GetProperty("receipts").EnumerateArray())stack.Add(Text(receipt.GetProperty("name").GetString()+" · "+receipt.GetProperty("studentName").GetString()+" · "+(receipt.GetProperty("revoked").GetBoolean()?"접근 회수":receipt.GetProperty("readAt").ValueKind==JsonValueKind.Null?"미확인":"확인"),13));
            if(!notice.GetProperty("withdrawn").GetBoolean()){var b=new Button{Text="공지 회수"};b.Clicked+=async(_,_)=>{if(!await page.DisplayAlertAsync("공지 회수","수신자의 공지·첨부 접근을 회수할까요?","회수","취소"))return;b.IsEnabled=false;try{await source.Send("api/announcements/"+id+"/retract");await Navigation.PopAsync();await Home();}catch(Exception e){await page.DisplayAlertAsync("공지 회수",e.Message,"확인");b.IsEnabled=true;}};stack.Add(b);}
        }
        else await source.Send("api/announcements/"+id+"/read");if(Current(source,version))await Navigation.PushAsync(page);
    }
    async Task Compose(bool office)
    {
        var source=api!;var version=epoch;var setup=await source.Get(office?"api/external-announcements/setup":"api/session");
        if(office&&(!setup.GetProperty("connected").GetBoolean()||setup.GetProperty("teacherId").ValueKind==JsonValueKind.Null))throw new InvalidOperationException("공지 관리자에게 교내 계정 연결·외부 서버 설정을 요청하세요.");
        var rooms=office?setup.GetProperty("classes"):await source.Get("api/classes");if(!Current(source,version))return;
        var page=new ContentPage{Title="학생·보호자 공지",BackgroundColor=BackgroundColor};composer=page;var stack=new VerticalStackLayout{Padding=24,Spacing=16};page.Content=new ScrollView{Content=stack};stack.Add(Text("학교 밖 학생·보호자에게 전달됩니다.",13,true));
        var kind=new Picker{Title="공지 유형",ItemsSource=new[]{"가정통신문 · JPG/PNG 한 장당 200KB","일반 공지 · JPG/PNG 한 장당 200KB"},SelectedIndex=0};stack.Add(kind);stack.Add(Text("모든 공지 첨부는 JPG·PNG로 바로 표시됩니다. 한 장당 200KB, 최대 5장입니다.",12));
        var title=new Entry{Placeholder="공지 제목 (200자 이내)",MaxLength=200};var text=new Editor{Placeholder="공지 내용",AutoSize=EditorAutoSizeOption.TextChanges,MinimumHeightRequest=180,MaxLength=10000};stack.Add(title);stack.Add(text);var audience=new Picker{Title="대상",ItemsSource=new[]{"학생과 보호자","보호자","학생"},SelectedIndex=0};stack.Add(audience);
        var scopes=new Dictionary<string,CheckBox>();foreach(var room in rooms.EnumerateArray()){var c=new CheckBox();scopes.Add(room.GetProperty("id").GetString()!,c);stack.Add(new HorizontalStackLayout{Children={c,Text(room.GetProperty("name").GetString()!,14)}});}
        var sectionScopes=new Dictionary<string,CheckBox>();var sections=office?setup.GetProperty("sections"):await source.Get("api/sections");stack.Add(Text("선택과목 분반 · 학급 선택과 함께 사용하지 않습니다.",13));foreach(var section in sections.EnumerateArray()){var check=new CheckBox{IsEnabled=section.GetProperty("memberCount").GetInt32()>0};sectionScopes[section.GetProperty("id").GetString()!]=check;stack.Add(new HorizontalStackLayout{Children={check,Text(section.GetProperty("name").GetString()+" · "+section.GetProperty("memberCount")+"명",14)}});}
        var whole=new CheckBox();var canAll=office?setup.GetProperty("canBroadcast").GetBoolean():setup.GetProperty("user").GetProperty("canBroadcast").GetBoolean();if(canAll)stack.Add(new HorizontalStackLayout{Children={whole,Text("학교 전체 발송",14)}});
        var error=Text("",13);stack.Add(error);var selectedFiles=new VerticalStackLayout{Spacing=4};stack.Add(selectedFiles);var send=new Button{Text="공지 보내기",BackgroundColor=Blue,TextColor=Colors.White};var files=new List<string>();var attach=new Button{Text="공지 이미지 추가 · 한 장당 200KB"};
        attach.Clicked+=async(_,_)=>{attach.IsEnabled=send.IsEnabled=kind.IsEnabled=false;try{if(files.Count>=5)throw new InvalidOperationException("공지 이미지는 최대 5장입니다.");preserveComposerOnResume=true;var selected=kind.SelectedIndex==0?"home-letter":"notice";var file=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="공지에 올릴 파일",FileTypes=new FilePickerFileType(new Dictionary<DevicePlatform,IEnumerable<string>>{{DevicePlatform.Android,["image/jpeg","image/png"]},{DevicePlatform.iOS,["public.jpeg","public.png"]},{DevicePlatform.MacCatalyst,["public.jpeg","public.png"]}})});if(file is null)return;var uploaded=await source.Upload(file,true,selected);files.Add(uploaded.GetProperty("id").GetString()!);selectedFiles.Add(Text(file.FileName,12));}catch(Exception e){error.Text=e.Message;}finally{attach.IsEnabled=send.IsEnabled=true;kind.IsEnabled=files.Count==0;}};stack.Add(attach);
        var client=Guid.NewGuid().ToString("N");AnnouncementRequest? pending=null;send.Clicked+=async(_,_)=>{send.IsEnabled=attach.IsEnabled=kind.IsEnabled=false;try{if(pending is null){if(string.IsNullOrWhiteSpace(title.Text)||string.IsNullOrWhiteSpace(text.Text)||!whole.IsChecked&&!scopes.Values.Any(c=>c.IsChecked)&&!sectionScopes.Values.Any(c=>c.IsChecked))throw new InvalidOperationException("제목·내용·수신 학급을 입력하세요.");if(!await page.DisplayAlertAsync("학교 밖으로 공지 발송","수신 대상과 내용을 확인했나요?","발송","취소"))return;pending=new(client,title.Text??"",text.Text??"",audience.SelectedIndex==1?"parents":audience.SelectedIndex==2?"students":"both",whole.IsChecked?[]:scopes.Where(c=>c.Value.IsChecked).Select(c=>c.Key).ToArray(),whole.IsChecked,files.ToArray(),whole.IsChecked?[]:sectionScopes.Where(c=>c.Value.IsChecked).Select(c=>c.Key).ToArray(),kind.SelectedIndex==0?"home-letter":"notice");}await source.Send(office?"api/external-announcements":"api/announcements",pending);await Navigation.PopAsync();await Home();feedback.Text=office?"발송 대기열에 등록했습니다. 외부 서버 확인 후 게시됩니다.":"공지가 게시되었습니다.";}catch(Exception e){error.Text=e.Message;if(pending is not null){title.IsEnabled=text.IsEnabled=audience.IsEnabled=attach.IsEnabled=false;foreach(var c in scopes.Values)c.IsEnabled=false;foreach(var c in sectionScopes.Values)c.IsEnabled=false;whole.IsEnabled=false;send.Text="같은 공지 다시 전송";}}finally{send.IsEnabled=true;attach.IsEnabled=pending is null;kind.IsEnabled=pending is null&&files.Count==0;}};stack.Add(send);
        if(office){var rows=await source.Get("api/external-announcements");foreach(var row in rows.EnumerateArray()){var id=row.GetProperty("id").GetString()!;stack.Add(Text(row.GetProperty("request").GetProperty("title").GetString()+" · "+State(row.GetProperty("state").GetString()!),14,true));if(row.GetProperty("error").ValueKind==JsonValueKind.String)stack.Add(Text(row.GetProperty("error").GetString()!,12));if(row.GetProperty("detail").ValueKind!=JsonValueKind.Null){var n=row.GetProperty("detail").GetProperty("notice");stack.Add(Text("확인 "+n.GetProperty("readCount")+" / "+n.GetProperty("recipientCount"),12));}if(row.GetProperty("state").GetString()!="withdrawn"){var b=new Button{Text="이 공지 회수 요청"};b.Clicked+=async(_,_)=>{try{if(await page.DisplayAlertAsync("공지 회수","연결이 복구되면 수신자의 접근을 회수합니다.","요청","취소")){await source.Send("api/external-announcements/"+id+"/retract");b.IsEnabled=false;error.Text="회수 요청을 등록했습니다.";}}catch(Exception e){error.Text=e.Message;}};stack.Add(b);}}}await Navigation.PushAsync(page);
    }
    static string State(string value)=>value switch{"pending"=>"전송 대기","published"=>"게시됨","needs-review"=>"대상·권한 재확인 필요","withdraw-pending"=>"회수 대기","withdrawn"=>"회수됨",_=>value};
}
