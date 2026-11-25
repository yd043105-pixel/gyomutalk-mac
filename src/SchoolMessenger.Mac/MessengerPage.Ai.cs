using System.Text.Json;

namespace SchoolMessenger.Mobile;

// The Windows AiWindow and the message composer's ‘AI로 다듬기’ for the Mac.
public sealed partial class MessengerPage
{
    async Task MacAiSettings(string? note=null)
    {
        await MacAi.Load();
        var page=DesktopPage("AI 연결");var grid=(Grid)page.Content;
        var stack=new VerticalStackLayout{Padding=new Thickness(28,20),Spacing=12,MaximumWidthRequest=760};grid.Add(new ScrollView{Content=stack},0,1);
        stack.Add(Text("AI 연결",22,true));
        var intro=Text(note??"메시지의 ‘AI로 다듬기’와 수업 교체 도우미의 순서 고르기에 씁니다. 선생님 본인의 계정이나 열쇠를 이 Mac에 연결합니다.",13);intro.TextColor=Muted;stack.Add(intro);
        var status=Text("",12);status.TextColor=Blue;stack.Add(status);
        var cards=new VerticalStackLayout{Spacing=10};stack.Add(cards);
        var privacy=Text("로그인 정보와 열쇠는 이 Mac의 키체인에만 저장하고 학교 서버로 보내지 않습니다. ‘AI로 다듬기’를 누르면 그 글이 고른 AI 회사로 전송되니 학생 이름·연락처 같은 개인정보는 빼고 쓰세요.",11);
        privacy.TextColor=Faint;stack.Add(privacy);
        var done=DesktopButton("완료",async()=>await Navigation.PopAsync(),true);done.HorizontalOptions=LayoutOptions.End;stack.Add(done);
        async Task Fill()
        {
            cards.Clear();status.Text=MacAi.StorageWarning??"";
            foreach(var provider in MacAi.Providers)
            {
                var card=new VerticalStackLayout{Spacing=8};var connected=MacAi.Connected(provider);var inUse=MacAi.InUse==provider;
                var head=new Grid{ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto)}};
                head.Add(Text(MacAi.Name(provider)+(inUse?" · 다듬기에 쓰는 중":""),16,true));
                var state=Text(connected?"연결됨"+(provider==MacAi.ChatGpt&&MacAi.Email is{} mail?" · "+mail:""):"연결 안 됨",12,true);state.TextColor=connected?Success:Faint;head.Add(state,1);card.Add(head);
                var about=Text(provider switch
                {
                    MacAi.ChatGpt=>"ChatGPT 계정으로 로그인하면 그 계정의 사용량으로 씁니다. 무료 계정도 됩니다(무료는 쓸 수 있는 모델이 정해져 있습니다). 따로 내는 돈은 없습니다.",
                    MacAi.OpenRouter=>"열쇠 하나로 여러 회사 모델(Claude, Gemini, Llama 등)을 씁니다. 이름이 ‘:free’로 끝나는 모델은 무료이고, 나머지는 충전한 만큼 씁니다.",
                    _=>"구글 계정으로 무료 열쇠를 받습니다. 무료 사용분은 하루 한도가 있고, 구글이 입력한 글을 서비스 개선에 쓸 수 있습니다."
                },12);about.TextColor=Muted;card.Add(about);
                var actions=new HorizontalStackLayout{Spacing=8};card.Add(actions);
                if(provider==MacAi.ChatGpt)
                {
                    if(!connected)actions.Add(DesktopButton("Continue with ChatGPT · 브라우저에서 로그인",async()=>
                    {
                        status.Text="브라우저에서 ChatGPT 로그인을 마치세요…";
                        try{await MacAi.SignIn(url=>Launcher.Default.OpenAsync(new Uri(url)),CancellationToken.None);status.Text="ChatGPT로 연결했습니다. 아래에서 모델을 고르세요.";}
                        catch(Exception error)when(error is MacAiException or IOException or OperationCanceledException or System.Net.Sockets.SocketException){status.Text=error is OperationCanceledException?"로그인 시간이 지났습니다. 다시 누르세요.":error.Message;}
                        await Fill();
                    },true));
                    else actions.Add(DesktopButton("로그아웃",async()=>{await MacAi.SignOut(provider);await Fill();}));
                }
                else
                {
                    actions.Add(DesktopButton("키 받기 · 브라우저에서 열기",async()=>{await Launcher.Default.OpenAsync(new Uri(provider==MacAi.OpenRouter?MacAi.OpenRouterKeys:MacAi.GoogleKeys));}));
                    if(connected)actions.Add(DesktopButton("연결 끊기",async()=>{await MacAi.SignOut(provider);await Fill();}));
                    else
                    {
                        var key=new Entry{ClassId="school-field",Placeholder=MacAi.Name(provider)+" 열쇠",IsPassword=true,FontSize=14,WidthRequest=320};
                        actions.Add(DesktopPanel(key,4));
                        actions.Add(DesktopButton("연결",async()=>
                        {
                            status.Text="열쇠를 확인하는 중…";
                            try{var models=await MacAi.Connect(provider,key.Text??"",CancellationToken.None);if(models.Length>0&&MacAi.Model(provider) is null)await MacAi.Choose(provider,models[0]);status.Text=MacAi.Name(provider)+"로 연결했습니다. 아래에서 모델을 고르세요.";}
                            catch(MacAiException error){status.Text=error.Message;}
                            finally{key.Text="";}
                            await Fill();
                        },true));
                    }
                }
                if(connected)
                {
                    var models=new Picker{ClassId="school-field",Title="모델 목록을 불러오는 중…",WidthRequest=360,TextColor=Navy};
                    var row=new HorizontalStackLayout{Spacing=10,Children={Text("모델",13,true),DesktopPanel(models,4)}};card.Add(row);
                    if(!inUse){var use=DesktopButton("이 AI로 다듬기",async()=>{if(MacAi.Model(provider) is not{} chosen)throw new InvalidOperationException("먼저 모델을 고르세요.");await MacAi.Choose(provider,chosen);await Fill();});SubtleButton(use);card.Add(use);}
                    _=LoadModels(provider,models);
                }
                cards.Add(DesktopPanel(card,16,inUse?Pale:Surface));
            }
        }
        async Task LoadModels(string provider,Picker picker)
        {
            try
            {
                var list=await MacAi.Models(provider,CancellationToken.None);picker.ItemsSource=list;picker.Title=list.Length==0?"쓸 수 있는 모델이 없습니다":"모델을 고르세요";
                picker.SelectedIndex=Array.IndexOf(list,MacAi.Model(provider));
                picker.SelectedIndexChanged+=async(_,_)=>{if(picker.SelectedIndex>=0)await MacAi.Choose(provider,list[picker.SelectedIndex]);};
            }
            catch(MacAiException error){picker.Title="모델 목록을 불러오지 못했습니다.";status.Text=error.Message;}
        }
        await Fill();await Navigation.PushAsync(page);
    }
    // The original and the suggestion side by side; the suggestion can be edited before it goes into the message.
    async Task MacAiResult(string label,string original,string answer,Action<string> replace)
    {
        var page=DesktopPage("AI로 다듬기 · "+label);var grid=(Grid)page.Content;
        var body=new Grid{Padding=new Thickness(24,16),RowSpacing=8,ColumnSpacing=12,ColumnDefinitions={new(GridLength.Star),new(GridLength.Star)},RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star)}};grid.Add(body,0,1);
        var hint=Text("AI가 고친 글은 틀릴 수 있습니다. 날짜·숫자·이름을 확인하고, 필요하면 오른쪽 글을 직접 고친 뒤 ‘바꾸기’를 누르세요.",12);hint.TextColor=Muted;body.Add(hint);Grid.SetColumnSpan(hint,2);
        body.Add(Text("원래 글",13,true),0,1);body.Add(Text("다듬은 글",13,true),1,1);
        var before=new Editor{Text=original,IsReadOnly=true,FontSize=15,TextColor=Navy,BackgroundColor=Colors.Transparent,VerticalOptions=LayoutOptions.Fill};body.Add(DesktopPanel(before,10,Subtle),0,2);
        var after=new Editor{Text=answer,FontSize=15,TextColor=Navy,BackgroundColor=Colors.Transparent,VerticalOptions=LayoutOptions.Fill,AutomationId="mac-ai-suggestion"};body.Add(DesktopPanel(after,10),1,2);
        var footer=new Grid{Padding=new Thickness(24,12),ColumnDefinitions={new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=8,BackgroundColor=Surface};
        var source=Text(MacAi.InUse is{} provider?MacAi.Name(provider)+" · "+MacAi.Model(provider)+(provider==MacAi.ChatGpt?" · Using ChatGPT plan":""):"",11);source.TextColor=Muted;source.VerticalOptions=LayoutOptions.Center;footer.Add(source);
        var keep=new Button{Text="그대로 두기"};var swap=new Button{Text="바꾸기",BackgroundColor=Blue,TextColor=Colors.White,FontAttributes=FontAttributes.Bold,CornerRadius=(int)Radius,Padding=new Thickness(16,8)};
        keep.Clicked+=async(_,_)=>{if(Navigation.NavigationStack.LastOrDefault()==page)await Navigation.PopAsync();};
        swap.Clicked+=async(_,_)=>{var text=after.Text??"";if(Navigation.NavigationStack.LastOrDefault()==page)await Navigation.PopAsync();replace(text);};
        footer.Add(keep,1);footer.Add(swap,2);grid.Add(footer,0,2);grid.Add(Rule(),0,2);
        await Navigation.PushAsync(page);
    }
    // ‘AI로 다듬기’: the selected words (or the whole body) go to the AI the teacher connected; nothing in the message
    // changes until the teacher presses ‘바꾸기’.
    async Task MacRewrite(ContentPage page,Editor body,Label status)
    {
        await MacAi.Load();
        var choice=await page.DisplayActionSheetAsync("AI로 다듬기","닫기",null,MacAi.Rewrites.Select(r=>r.Label).Append("AI 연결 설정…").ToArray());
        if(choice=="AI 연결 설정…"){await MacAiSettings();return;}
        if(MacAi.Rewrites.FirstOrDefault(r=>r.Label==choice) is not{} rewrite)return;
        if(!MacAi.Ready){await MacAiSettings("‘AI로 다듬기’를 쓰려면 먼저 AI를 연결하세요. 하나를 연결하고 모델을 고른 뒤 ‘완료’를 누르고, 다듬기를 다시 누르세요.");return;}
        var text=body.Text??"";var start=Math.Clamp(body.CursorPosition,0,text.Length);var length=Math.Clamp(body.SelectionLength,0,text.Length-start);
        var whole=length==0;var original=(whole?text:text.Substring(start,length)).Trim();
        if(original.Length==0){status.Text="다듬을 글을 먼저 쓰세요.";return;}
        var provider=MacAi.InUse!;status.Text=MacAi.Name(provider)+"가 "+(whole?"본문을":"고른 글을")+" 다듬는 중…";
        try
        {
            var answer=await MacAi.Ask(rewrite.Instructions,original,CancellationToken.None);status.Text="";
            await MacAiResult(rewrite.Label,original,answer,replaced=>
            {
                var now=body.Text??"";
                // The words may have changed meanwhile; the replacement goes only where the original still is.
                if(whole)body.Text=replaced;
                else if(start+length<=now.Length&&now.Substring(start,length).Trim()==original)body.Text=now[..start]+replaced+now[(start+length)..];
                else{status.Text="고른 글이 바뀌어 바꾸지 않았습니다. 다시 골라 다듬으세요.";return;}
                status.Text=whole?"본문을 다듬은 글로 바꿨습니다.":"고른 글을 다듬은 글로 바꿨습니다.";
            });
        }
        catch(MacAiException error){status.Text=error.Message;}
    }
    // 보내기 전 확인: only when the check finds something, and the teacher may still send it as it is.
    async Task<bool> MacStyleChecked(ContentPage page,string text,Label status)
    {
        var found=SchoolMessenger.Desktop.StyleCheck.Check(text);
        if(found.Length==0)return true;
        var lines=string.Join("\n",found.Take(6).Select(f=>$"· [{f.Kind}] ‘{f.Found}’ → {f.Instead}"))+(found.Length>6?$"\n· 그 밖에 {found.Length-6}건":"");
        if(await page.DisplayAlertAsync("보내기 전 확인",$"보내기 전에 확인할 표현이 {found.Length}건 있습니다.\n\n{lines}\n\n그대로 보낼까요?","그대로 보내기","고치기"))return true;
        status.Text="표현을 고친 뒤 다시 보내세요."+(MacFeature("ai")?" ‘AI로 다듬기’로 고칠 수도 있습니다.":"");return false;
    }
}
