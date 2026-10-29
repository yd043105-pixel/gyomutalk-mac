using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    static string ElectiveTime(long value)=>DateTimeOffset.FromUnixTimeMilliseconds(value).ToOffset(TimeSpan.FromHours(9)).ToString("M월 d일 HH:mm");
    static string ElectiveStatus(ElectiveRound round)=>round.State switch
    {"finalized"=>"분반 확정","closed"=>"신청 마감","open" when round.CanApply=>"신청 가능","open" when round.StartsAt>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()=>"신청 예정",_=>"신청 기간 종료"};
    static string CreditRule(ElectiveRound round)=>round.MinCredits==0&&round.MaxCredits==0?"학점 합계 제한 없음":$"최소 {round.MinCredits}학점 · "+(round.MaxCredits==0?"상한 없음":$"최대 {round.MaxCredits}학점");
    async Task Electives()
    {
        var source=api!;if(source.Office||source.Session.GetProperty("user").GetProperty("role").GetString()!="student")throw new InvalidOperationException("학생 본인 계정으로 신청하세요.");
        var version=++epoch;Header("내가 설계하는 다음 학기","수강신청",true);Action("홈으로",Home);Action("새로고침",Electives);
        var data=await source.Get("api/electives");if(!Current(source,version))return;
        var rounds=JsonSerializer.Deserialize(data,MobileJson.Default.ElectiveRoundArray)??[];
        if(rounds.Length==0){Empty("열린 신청 회차가 없어요","대상 학년과 공개 여부를 학교 담당자에게 확인해 주세요.");return;}
        foreach(var round in rounds)
        {
            var card=new VerticalStackLayout{Spacing=8};card.Add(Text(round.Term+" · "+ElectiveStatus(round),12,true));card.Add(Text(round.Title,20,true));
            card.Add(Text(ElectiveTime(round.StartsAt)+" ~ "+ElectiveTime(round.EndsAt)+" · 한국 시각",12));
            card.Add(Text(round.Application?.Submitted==true?"신청 완료 · "+ElectiveRules.Credits(round.Groups,round.Application.CourseIds)+"학점":"미신청 · 아직 제출하지 않았어요",14,true));
            card.Add(MakeAction("과목·내 신청 보기",()=>ElectiveDetail(round.Id),round.CanApply));body.Add(Card(card));
        }
    }
    async Task ElectiveDetail(string id)
    {
        var source=api!;var version=++epoch;Header("과목을 살펴보고 나의 수업을 선택하세요","수강신청",true);Action("신청 회차 목록",Electives);
        var data=await source.Get("api/electives/"+Uri.EscapeDataString(id));if(!Current(source,version))return;
        var round=JsonSerializer.Deserialize(data,MobileJson.Default.ElectiveRound)!;var application=round.Application!;
        body.Add(Text(round.Title,21,true));body.Add(Text(round.Term+" · "+ElectiveStatus(round),13));body.Add(Text("대상 "+string.Join(" · ",round.Grades.Select(g=>g+"학년 전체")),12));body.Add(Text(ElectiveTime(round.StartsAt)+" ~ "+ElectiveTime(round.EndsAt)+" (한국 시각)",12));
        body.Add(Text("공통과목은 자동 포함됩니다. 정원 제한 없이 신청할 수 있어요.",13));body.Add(Text(CreditRule(round),12));
        var checks=new Dictionary<string,CheckBox>();var summary=Text("",18,true);var selectedNames=Text("",12);
        var summaryPanel=new VerticalStackLayout{Spacing=5};summaryPanel.Add(summary);summaryPanel.Add(selectedNames);body.Add(Card(summaryPanel,16,Color.FromArgb("#E7EFFB")));
        var search=new SearchBar{Placeholder="과목명 또는 교과 검색"};body.Add(search);
        var types=new[]{"all"}.Concat(ElectiveRules.Types).ToArray();var filter=new Picker{Title="과목 유형",ItemsSource=new[]{"전체 유형"}.Concat(ElectiveRules.Types.Select(ElectiveRules.TypeName)).ToArray(),SelectedIndex=0};body.Add(filter);
        var selection=new VerticalStackLayout{Spacing=16};var courseViews=new List<(ElectiveCourse Course,View Row)>();
        foreach(var group in round.Groups)
        {
            var panel=new VerticalStackLayout{Spacing=10};panel.Add(Text(group.Name,18,true));panel.Add(Text(group.Required==0?"공통과목만 이수하는 묶음":"선택과목 "+group.Required+"개 선택 · 공통과목은 별도 자동 포함",12));
            var count=Text("",12);count.TextColor=Muted;
            foreach(var course in group.Courses)
            {
                var mandatory=course.Type=="common";var check=new CheckBox{IsChecked=mandatory||application.CourseIds.Contains(course.Id),IsEnabled=round.CanApply&&!mandatory};checks[course.Id]=check;
                SemanticProperties.SetDescription(check,course.Name+" · "+ElectiveRules.TypeName(course.Type)+(mandatory?" · 필수":""));
                var text=new VerticalStackLayout{Spacing=4,VerticalOptions=LayoutOptions.Center};
                var caption=Text(ElectiveRules.TypeName(course.Type)+(string.IsNullOrWhiteSpace(course.Area)?"":" · "+course.Area)+(mandatory?" · 필수":""),11,true);
                caption.TextColor=Color.FromArgb(course.Type switch{"common"=>"#506277","career"=>"#17745D","convergence"=>"#7050B0",_=>"#245BC6"});text.Add(caption);text.Add(Text(course.Name,16,true));
                var credits=Text(course.Credits>0?course.Credits+"학점":"학점 미설정",14,true);credits.VerticalOptions=LayoutOptions.Center;
                var row=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)},ColumnSpacing=8};row.Add(check);row.Add(text,1);row.Add(credits,2);
                var card=Card(row,10,mandatory?Color.FromArgb("#F3F5F8"):Colors.White);panel.Add(card);courseViews.Add((course,card));
                check.CheckedChanged+=(_,_)=>{count.Text="선택 "+group.Courses.Count(c=>c.Type!="common"&&checks.TryGetValue(c.Id,out var value)&&value.IsChecked)+" / "+group.Required+"개";UpdateSummary();};
            }
            count.Text="선택 "+group.Courses.Count(c=>c.Type!="common"&&checks[c.Id].IsChecked)+" / "+group.Required+"개";panel.Add(count);selection.Add(panel);
        }
        void UpdateSummary()
        {
            var ids=checks.Where(c=>c.Value.IsChecked).Select(c=>c.Key).ToHashSet();var courses=round.Groups.SelectMany(g=>g.Courses).Where(c=>ids.Contains(c.Id)).ToArray();
            summary.Text=$"총 {courses.Sum(c=>c.Credits)}학점 · {courses.Length}과목";
            selectedNames.Text=courses.Length==0?"아직 선택한 과목이 없어요.":string.Join(" · ",courses.Select(c=>c.Name));
        }
        void Filter(){var term=search.Text?.Trim()??"";var type=types[Math.Max(0,filter.SelectedIndex)];foreach(var row in courseViews)row.Row.IsVisible=(type=="all"||row.Course.Type==type)&&(row.Course.Name.Contains(term,StringComparison.OrdinalIgnoreCase)||row.Course.Area.Contains(term,StringComparison.OrdinalIgnoreCase));}
        search.TextChanged+=(_,_)=>Filter();filter.SelectedIndexChanged+=(_,_)=>Filter();UpdateSummary();body.Add(selection);
        body.Add(Text(application.Submitted?"접수 완료 · "+ElectiveTime(application.UpdatedAt!.Value):"미신청 · 선택 후 아래 신청 버튼을 눌러야 접수됩니다.",14,true));
        Action("내 신청 현황 다시 조회",()=>ElectiveDetail(id));
        if(!round.CanApply){body.Add(Text("지금은 신청 내용을 바꿀 수 없습니다.",13));return;}
        ElectiveApplicationRequest? pending=null;body.Add(Text("연결이 끊기면 내 신청 현황을 다시 확인한 뒤 진행하세요.",12));
        Action(application.Submitted?"선택 과목 변경":"선택 과목 신청",async()=>
        {
            if(pending is null)
            {
                var ids=checks.Where(c=>c.Value.IsChecked).Select(c=>c.Key).ToArray();var issue=ElectiveRules.SelectionError(round.Groups,ids,round.MinCredits,round.MaxCredits);if(issue is not null)throw new InvalidOperationException(issue);
                pending=new(Guid.NewGuid().ToString("N"),application.Revision,ids);selection.IsEnabled=false;
            }
            await source.Send("api/electives/"+id+"/application",pending);if(Current(source,version))await ElectiveDetail(id);
        },true);
        if(application.Submitted)
        {
            var cancel=new ElectiveCancellation(Guid.NewGuid().ToString("N"),application.Revision);
            Action("이번 회차 신청 취소",async()=>{if(!await DisplayAlertAsync("신청 취소","이번 회차 신청 전체를 취소할까요? 기간 안에는 다시 신청할 수 있습니다.","신청 취소","돌아가기"))return;await source.Send("api/electives/"+id+"/cancel",cancel);if(Current(source,version))await ElectiveDetail(id);});
        }
    }
}
