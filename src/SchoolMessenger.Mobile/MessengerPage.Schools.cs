using SchoolMessenger.Shared;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    static SchoolDirectory.Entry? ReadSelectedSchool(int mode)
    {
        var id=Preferences.Default.Get("school-id-"+mode,"");if(id.Length==0)return null;
        var address=Preferences.Default.Get("address-"+mode,"");
        return new(id,Preferences.Default.Get("school-name-"+mode,"학교"),Preferences.Default.Get("school-region-"+mode,""),mode==1?address:"",mode==0?address:"");
    }
    static void SaveSelectedSchool(SchoolDirectory.Entry? school,int mode)
    {
        Preferences.Default.Set("school-id-"+mode,school?.Id??"");Preferences.Default.Set("school-name-"+mode,school?.Name??"");Preferences.Default.Set("school-region-"+mode,school?.Region??"");
    }
    async Task<SchoolDirectory.Entry?> ChooseSchool(string service)
    {
        using var stop=new CancellationTokenSource();var result=new TaskCompletionSource<SchoolDirectory.Entry?>();
        var query=new SearchBar{Placeholder="학교명 또는 지역",AutomationId="school-search-query"};
        var status=Text("학교 목록을 불러오는 중…",12);status.TextColor=Muted;
        var list=new CollectionView{SelectionMode=SelectionMode.Single,AutomationId="school-search-results",ItemTemplate=new DataTemplate(()=>
        {
            var label=Text("",14,true);label.SetBinding(Label.TextProperty,"Label");return new Border{Content=label,Padding=16,BackgroundColor=Colors.White,Stroke=Line,StrokeThickness=1};
        })};
        var use=new Button{Text="이 학교 선택",BackgroundColor=Blue,TextColor=Colors.White,MinimumHeightRequest=48,CornerRadius=10};
        use.Clicked+=async(_,_)=>
        {
            if(!query.IsEnabled||list.SelectedItem is not SchoolDirectory.Entry school)return;
            use.IsEnabled=list.IsEnabled=query.IsEnabled=false;
            try{status.Text="학교 연결 확인 중…";await SchoolDirectory.Verify(school,service,stop.Token);result.TrySetResult(school);}
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            catch{status.Text="학교에 연결하지 못했거나 등록 정보가 다릅니다. 교내망/VPN 연결과 학교 서버 가동 여부를 확인하세요.";}
            finally{list.IsEnabled=query.IsEnabled=true;use.IsEnabled=list.SelectedItem is SchoolDirectory.Entry;}
        };use.IsEnabled=false;list.SelectionChanged+=(_,_)=>use.IsEnabled=query.IsEnabled&&list.SelectedItem is SchoolDirectory.Entry;
        var layout=new Grid{Padding=18,RowSpacing=12,RowDefinitions={new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)}};
        layout.Add(Text("우리 학교 찾기",24,true));layout.Add(query,0,1);layout.Add(list,0,2);layout.Add(status,0,3);layout.Add(use,0,4);
        var page=new ContentPage{Title="학교 검색",BackgroundColor=Color.FromArgb("#F3F6FB"),Content=layout};
        SchoolDirectory.Catalog catalog=new("",[]);
        void Filter(){list.ItemsSource=SchoolDirectory.Search(catalog.Schools,query.Text??"",service);}
        query.TextChanged+=(_,_)=>{if(query.Text?.Length>80){query.Text=query.Text[..80];return;}Filter();};
        page.Disappearing+=(_,_)=>{stop.Cancel();result.TrySetResult(null);};
        page.Appearing+=async(_,_)=>
        {
            try
            {
                catalog=SchoolDirectory.Bundled();Filter();catalog=await SchoolDirectory.Refresh(catalog,stop.Token);if(stop.IsCancellationRequested)return;Filter();
                status.Text=catalog.Schools.Length==0?"아직 등록된 학교가 없습니다. 학교 관리자에게 등록 여부를 확인하세요.":"학교 이름과 지역을 확인하세요. 최대 100개를 표시합니다.";
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            catch{if(!stop.IsCancellationRequested)status.Text="최신 목록에 연결하지 못했습니다. 포함된 목록 또는 이전 학교 연결을 이용하고 등록 여부를 확인하세요.";}
        };
        await Navigation.PushAsync(page);var school=await result.Task;
        if(Navigation.NavigationStack.LastOrDefault()==page)await Navigation.PopAsync();return school;
    }
}
