using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    async Task ShowPolicy(JsonElement policy,JsonElement document)
    {
        var page=new ContentPage{Title=document.GetProperty("title").GetString(),BackgroundColor=BackgroundColor};
        var stack=new VerticalStackLayout{Padding=24,Spacing=16};page.Content=new ScrollView{Content=stack};
        stack.Add(Text(policy.GetProperty("message").GetString()!,14,true));
        stack.Add(Text("문서 버전 · "+document.GetProperty("version").GetString(),12));
        stack.Add(Text("운영 주체 · "+policy.GetProperty("operator").GetString()+"\n문의 · "+policy.GetProperty("contact").GetString(),13));
        stack.Add(Text(document.GetProperty("body").GetString()!,15));
        var back=new Button{Text="이전 화면으로 돌아가기"};back.Clicked+=async(_,_)=>await Navigation.PopAsync();stack.Add(back);
        await Navigation.PushAsync(page);
    }
    Func<RegistrationAcceptance> AddPolicies(VerticalStackLayout stack,JsonElement policy)
    {
        stack.Add(Text("약관·개인정보 안내",20,true));stack.Add(Text(policy.GetProperty("message").GetString()!,13));
        var choices=new Dictionary<string,CheckBox>();
        foreach(var document in policy.GetProperty("documents").EnumerateArray())
        {
            var required=document.GetProperty("required").GetBoolean();var title=document.GetProperty("title").GetString()!;
            var check=new CheckBox{IsChecked=false,IsEnabled=false};choices.Add(document.GetProperty("id").GetString()!,check);
            var label=(required?"필수 · ":"선택 · ")+title+(document.GetProperty("action").GetString()=="consent"?"에 동의합니다.":"를 확인했습니다.");
            SemanticProperties.SetDescription(check,label);var row=new Grid{ColumnDefinitions={new ColumnDefinition(GridLength.Auto),new ColumnDefinition(GridLength.Star)}};row.Add(check);row.Add(new Label{Text=label,TextColor=Navy,LineBreakMode=LineBreakMode.WordWrap,VerticalOptions=LayoutOptions.Center},1);stack.Add(row);
            var open=new Button{Text="전문 보기 · "+title};open.Clicked+=async(_,_)=>{await ShowPolicy(policy,document);check.IsEnabled=policy.GetProperty("canRegister").GetBoolean();};stack.Add(open);
        }
        return ()=>
        {
            if(!policy.GetProperty("canRegister").GetBoolean())throw new InvalidOperationException(policy.GetProperty("message").GetString());
            if(policy.GetProperty("documents").EnumerateArray().Any(d=>d.GetProperty("required").GetBoolean()&&!choices[d.GetProperty("id").GetString()!].IsChecked))
                throw new InvalidOperationException("필수 약관·개인정보 안내의 전문을 확인하고 체크하세요.");
            return new(policy.GetProperty("revision").GetString(),choices.Where(c=>c.Value.IsChecked).Select(c=>c.Key).ToArray());
        };
    }
    async Task InviteSignup(string address)
    {
        var source=new MessengerApi(address,false);
        try
        {
            await source.Refresh();var policy=await source.Get("api/registration/policy");
            var page=new ContentPage{Title="학교 초대로 가입",BackgroundColor=BackgroundColor};var stack=new VerticalStackLayout{Padding=24,Spacing=16};page.Content=new ScrollView{Content=stack};
            stack.Add(Text("이름·역할·자녀는 학교에서 확인합니다.",15));var code=new Entry{Placeholder="학교 초대 코드",MaxLength=128};var id=new Entry{Placeholder="아이디 (3~32자)",MaxLength=32};var password=new Entry{Placeholder="비밀번호 (12~128자)",IsPassword=true,MaxLength=128};
            stack.Add(code);stack.Add(id);stack.Add(password);var agreement=AddPolicies(stack,policy);var error=Text("",13);stack.Add(error);var join=new Button{Text="계정 등록",IsEnabled=policy.GetProperty("canRegister").GetBoolean(),BackgroundColor=Blue,TextColor=Colors.White};
            var sending=false;join.Clicked+=async(_,_)=>
            {
                if(sending)return;sending=true;stack.IsEnabled=false;
                try{await source.Send("api/register",new MobileRegistration(code.Text,id.Text,password.Text,agreement()));password.Text="";await Navigation.PopAsync();feedback.Text="계정 등록 완료. 로그인하세요.";}
                catch(Exception e){password.Text="";error.Text=e.Message;}
                finally{sending=false;stack.IsEnabled=true;}
            };
            stack.Add(join);page.NavigatedFrom+=(_,_)=>{if(!Navigation.NavigationStack.Contains(page))source.Dispose();};await Navigation.PushAsync(page);
        }
        catch{source.Dispose();throw;}
    }
    async Task PolicyMenu()
    {
        var policy=await api!.Get("api/registration/policy");var page=new ContentPage{Title="약관·개인정보 안내",BackgroundColor=BackgroundColor};var stack=new VerticalStackLayout{Padding=24,Spacing=16};page.Content=new ScrollView{Content=stack};
        stack.Add(Text(policy.GetProperty("message").GetString()!,14));
        stack.Add(Text("처리 주체: "+policy.GetProperty("operator").GetString()+"\n문의: "+policy.GetProperty("contact").GetString(),14));
        foreach(var document in policy.GetProperty("documents").EnumerateArray()){var open=new Button{Text=document.GetProperty("title").GetString()};open.Clicked+=async(_,_)=>await ShowPolicy(policy,document);stack.Add(open);}
        await Navigation.PushAsync(page);
    }
}
