using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

// 교체하지 않는 시간, as on Windows: weekly times (creative activities and the like) the timetable manager keeps out of
// teachers' trades. Each change is saved at once.
public sealed partial class MessengerPage
{
    static readonly string[] MacFixedWeekdays = ["월", "화", "수", "목", "금"];
    static string MacFixedLabel(TimetableFixedTime t) => $"{MacFixedWeekdays[t.Weekday - 1]}요일 {t.Period}교시 · {(t.Grade == 0 ? "모든 학년" : t.Grade + "학년")}";

    async Task MacFixedTimes(MessengerApi source, JsonElement setup)
    {
        var page = DesktopPage("교체하지 않는 시간");
        var body = new VerticalStackLayout { Padding = new Thickness(24, 16), Spacing = 10, MaximumWidthRequest = 760 };
        ((Grid)page.Content).Add(new ScrollView { Content = body }, 0, 1);
        body.Add(Text("교체하지 않는 시간", 22, true));
        var about = Text("창의적 체험활동처럼 매주 같은 시간의 수업은 선생님들이 교체 신청으로 옮기거나 그 시간으로 넣을 수 없습니다. 시간표 담당자는 바꿀 수 있습니다.", 13); about.TextColor = Muted; body.Add(about);
        var grades = setup.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("grade").GetInt32()).Where(g => g > 0).Distinct().Order().ToArray();
        Picker Pick(string name, string id, IEnumerable<string> items, double width) =>
            new() { Title = name, ItemsSource = items.ToList(), SelectedIndex = 0, WidthRequest = width, AutomationId = id, VerticalOptions = LayoutOptions.Center };
        var day = Pick("요일", "mac-fixed-day", MacFixedWeekdays.Select(d => d + "요일"), 100);
        var period = Pick("교시", "mac-fixed-period", Enumerable.Range(1, 9).Select(p => p + "교시"), 90);
        var grade = Pick("학년", "mac-fixed-grade", new[] { "모든 학년" }.Concat(grades.Select(g => g + "학년")), 120);
        var add = DesktopButton("추가", null, true); add.AutomationId = "mac-fixed-add";
        body.Add(DesktopPanel(new HorizontalStackLayout { Spacing = 10, Children = { day, period, grade, add } }, 16));
        var status = Text("", 13); status.TextColor = Muted; status.AutomationId = "mac-fixed-note"; body.Add(status);
        var list = new VerticalStackLayout { Spacing = 8 }; body.Add(DesktopPanel(list, 16));
        TimetableFixedTime[] times = [];
        var busy = false;
        async Task Save(TimetableFixedTime[] next, string done)
        {
            if (busy || !MacScheduleActive(source)) return;
            busy = true; body.IsEnabled = false;
            try { times = (await source.Send("api/timetable/fixed-times", next, method: HttpMethod.Put)).Deserialize(MobileJson.Default.TimetableFixedTimeArray)!; Draw(); status.Text = done; }
            catch (Exception error) { if (MacScheduleActive(source)) status.Text = error is HttpRequestException ? "학교 서버에 연결하지 못했습니다." : error.Message; }
            finally { busy = false; body.IsEnabled = true; }
        }
        void Draw()
        {
            list.Clear();
            if (times.Length == 0) { var none = Text("등록한 시간이 없습니다. 위에서 요일·교시·학년을 고르고 ‘추가’를 누르세요.", 13); none.TextColor = Faint; list.Add(none); return; }
            foreach (var time in times)
            {
                var row = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
                var label = Text(MacFixedLabel(time), 14); label.VerticalOptions = LayoutOptions.Center; row.Add(label);
                var remove = DesktopButton("삭제"); remove.TextColor = Danger; remove.BorderColor = Color.FromArgb("#F1B8B2"); remove.FontSize = 13; remove.MinimumHeightRequest = 32; remove.Padding = new Thickness(12, 4);
                remove.Clicked += async (_, _) => await Save(times.Where(t => t != time).ToArray(), MacFixedLabel(time) + "을(를) 지웠습니다. 이제 선생님들이 이 시간 수업을 바꿀 수 있습니다.");
                row.Add(remove, 1); list.Add(row);
            }
        }
        add.Clicked += async (_, _) =>
        {
            var time = new TimetableFixedTime(day.SelectedIndex + 1, period.SelectedIndex + 1, grade.SelectedIndex <= 0 ? 0 : grades[grade.SelectedIndex - 1]);
            if (times.Contains(time)) { status.Text = MacFixedLabel(time) + "은(는) 이미 있습니다."; return; }
            await Save([.. times, time], MacFixedLabel(time) + "을(를) 추가했습니다. 매주 이 시간 수업은 시간표 담당자만 바꿀 수 있습니다.");
        };
        times = (await source.Get("api/timetable/fixed-times")).Deserialize(MobileJson.Default.TimetableFixedTimeArray)!;
        Draw();
        await Navigation.PushAsync(page);
    }
}
