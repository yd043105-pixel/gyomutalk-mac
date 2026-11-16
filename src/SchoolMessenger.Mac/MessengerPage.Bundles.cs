using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls.Shapes;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

// Lessons that run at the same time for several classes, chosen as on Windows: grade, then subject, then the classes
// that run it. Each bundle shows the colour it has on the swap board.
public sealed partial class MessengerPage
{
    async Task MacBundleEditor(MessengerApi source)
    {
        var page = DesktopPage("묶음수업");
        var body = new VerticalStackLayout { Padding = new Thickness(24, 16), Spacing = 10 };
        ((Grid)page.Content).Add(new ScrollView { Content = body }, 0, 1);
        var status = Text("", 13); status.TextColor = Muted;
        bool busy = false;
        async Task Act(Func<Task> action)
        {
            if (busy || !MacScheduleActive(source)) return;
            busy = true; body.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { if (MacScheduleActive(source)) status.Text = error is HttpRequestException ? "학교 서버에 연결하지 못했습니다." : error.Message; }
            finally { busy = false; body.IsEnabled = true; }
        }
        async Task Load()
        {
            var rules = (await source.Get("api/timetable/bundle-rules")).Deserialize(MobileJson.Default.TimetableBundleRuleArray)!;
            var lessons = (await source.Get("api/timetable/base-lessons")).Deserialize(MobileJson.Default.TimetableBaseLessonArray)!;
            var colors = await LoadBundleColors(source);
            if (!MacScheduleActive(source)) return;
            body.Clear();
            body.Add(Text("묶음수업", 22, true));
            var about = Text("여러 반이 같은 시간에 나뉘어 듣는 수업을 묶어 두면, 수업을 교체할 때 그 시간의 수업이 모두 함께 움직입니다.", 13); about.TextColor = Muted; body.Add(about);
            body.Add(status);

            var names = lessons.GroupBy(l => new TimetableBundleMember(l.ClassId, l.Subject)).ToDictionary(g => g.Key, g => g.First().ClassName + " " + g.First().Subject);
            var list = new VerticalStackLayout { Spacing = 10 };
            foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.Create(KoreanCulture, false)))
            {
                var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
                row.Add(new Border { WidthRequest = 14, HeightRequest = 14, BackgroundColor = colors.GetValueOrDefault(rule.Name, Subtle), Stroke = Line, StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 4 }, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 3, 0, 0) });
                var text = new VerticalStackLayout { Spacing = 2 }; text.Add(Text(rule.Name, 14, true));
                var members = Text(string.Join(", ", rule.Members.Select(m => names.GetValueOrDefault(m, m.Subject + " (지금 시간표에 없음)"))), 12); members.TextColor = Muted; text.Add(members);
                row.Add(text, 1);
                var remove = DesktopButton("삭제"); remove.TextColor = Danger; remove.BorderColor = Color.FromArgb("#F1B8B2"); remove.FontSize = 13; remove.MinimumHeightRequest = 32; remove.Padding = new Thickness(12, 4); remove.VerticalOptions = LayoutOptions.Start;
                remove.Clicked += async (_, _) => await Act(async () =>
                {
                    if (!await page.DisplayAlertAsync("묶음수업", $"‘{rule.Name}’ 묶음을 삭제할까요?", "삭제", "취소")) return;
                    await source.Send("api/timetable/bundle-rules/" + rule.Id, method: HttpMethod.Delete);
                    await Load();
                });
                row.Add(remove, 2);
                list.Add(row);
            }
            if (rules.Length == 0) { var none = Text("아직 지정한 묶음수업이 없습니다.", 13); none.TextColor = Faint; list.Add(none); }
            body.Add(DesktopPanel(list, 18));

            if (lessons.Length == 0) { var later = Text("기초시간표를 먼저 등록하면 묶을 수업을 고를 수 있습니다.", 13); later.TextColor = Muted; body.Add(later); return; }
            var taken = rules.SelectMany(r => r.Members).ToHashSet();
            var chosen = new List<TimetableBundleMember>();
            var form = new VerticalStackLayout { Spacing = 8 };
            form.Add(Text("새 묶음 만들기", 15, true));
            var guide = Text("학년 → 과목 → 분반 순으로 고릅니다. 과목을 바꿔 가며 여러 과목의 분반을 한 묶음에 담을 수 있습니다.", 12); guide.TextColor = Muted; form.Add(guide);
            var grades = lessons.Select(l => l.Grade).Distinct().Order().ToArray();
            var grade = new Picker { ClassId = "school-field", Title = "학년", ItemsSource = grades.Select(g => g + "학년").ToArray(), AutomationId = "mac-bundle-grade" };
            var subject = new Picker { ClassId = "school-field", Title = "과목", AutomationId = "mac-bundle-subject" };
            var pickers = new Grid { ColumnDefinitions = { new(new GridLength(140)), new(GridLength.Star) }, ColumnSpacing = 10 };
            VerticalStackLayout Field(string label, View input) { var stack = new VerticalStackLayout { Spacing = 4 }; var caption = Text(label, 12); caption.TextColor = Muted; stack.Add(caption); stack.Add(input); return stack; }
            // Pickers sit in a bordered box like the other fields.
            pickers.Add(Field("1. 학년", DesktopPanel(grade, 6))); pickers.Add(Field("2. 과목", DesktopPanel(subject, 6)), 1); form.Add(pickers);
            var classesCaption = Text("3. 분반 · 함께 움직일 반을 체크", 12); classesCaption.TextColor = Muted; form.Add(classesCaption);
            var classes = new VerticalStackLayout { Spacing = 2 }; form.Add(DesktopPanel(classes, 10, Subtle));
            var count = Text("", 12, true); form.Add(count);
            var picked = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap }; form.Add(picked);
            var name = new Entry { ClassId = "school-field", Placeholder = "묶음 이름 (예: 3학년 선택 A)", MaxLength = 60, AutomationId = "mac-bundle-name" };
            form.Add(Field("묶음 이름", DesktopPanel(name, 6)));
            var save = DesktopButton("묶음으로 저장", null, true); save.HorizontalOptions = LayoutOptions.Start; save.AutomationId = "mac-bundle-save"; form.Add(save);
            string[] subjects = [];
            static int Number(string room) { var m = Regex.Match(room, @"(\d+)\s*반"); return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : int.MaxValue; }
            void Picked()
            {
                picked.Clear();
                count.Text = chosen.Count == 0 ? "선택한 수업이 없습니다." : $"선택한 수업 {chosen.Count}개" + (chosen.Count == 1 ? " · 둘 이상 고르면 저장할 수 있습니다." : "");
                foreach (var member in chosen.ToArray())
                {
                    var chip = new Button { Text = names[member] + "  ×", FontSize = 12, MinimumHeightRequest = 28, Padding = new Thickness(10, 2), Margin = new Thickness(0, 0, 6, 6), CornerRadius = 14, BorderWidth = 1, BorderColor = Line, BackgroundColor = Pale, TextColor = Navy };
                    ToolTipProperties.SetText(chip, "누르면 선택에서 뺍니다.");
                    chip.Clicked += (_, _) => { chosen.Remove(member); Picked(); Classes(); };
                    picked.Add(chip);
                }
                save.IsEnabled = chosen.Count >= 2;
            }
            void Classes()
            {
                classes.Clear();
                if (grade.SelectedIndex < 0 || subject.SelectedIndex < 0)
                { var hint = Text("과목을 고르면 그 과목을 운영하는 반이 여기에 나옵니다.", 12); hint.TextColor = Faint; classes.Add(hint); return; }
                var level = grades[grade.SelectedIndex]; var course = subjects[subject.SelectedIndex];
                foreach (var lesson in lessons.Where(l => l.Grade == level && l.Subject == course).OrderBy(l => Number(l.ClassName)).ThenBy(l => l.ClassName, StringComparer.Create(KoreanCulture, false)))
                {
                    var member = new TimetableBundleMember(lesson.ClassId, lesson.Subject); var used = taken.Contains(member);
                    var row = new HorizontalStackLayout { Spacing = 4 };
                    var box = new CheckBox { IsChecked = chosen.Contains(member), IsEnabled = !used, Color = Blue, VerticalOptions = LayoutOptions.Center };
                    var label = Text($"{lesson.ClassName} · {lesson.Teachers} · 주 {lesson.Hours}시간" + (used ? " · 이미 다른 묶음에 있음" : ""), 13); label.VerticalOptions = LayoutOptions.Center; if (used) label.TextColor = Faint;
                    box.CheckedChanged += (_, e) => { if (e.Value) { if (!chosen.Contains(member)) chosen.Add(member); } else chosen.Remove(member); Picked(); };
                    row.Add(box); row.Add(label); classes.Add(row);
                }
            }
            grade.SelectedIndexChanged += (_, _) =>
            {
                subjects = grade.SelectedIndex < 0 ? [] : lessons.Where(l => l.Grade == grades[grade.SelectedIndex]).Select(l => l.Subject).Distinct().Order(StringComparer.Create(KoreanCulture, false)).ToArray();
                subject.ItemsSource = subjects.Select(s => s + " · " + lessons.Count(l => l.Grade == grades[grade.SelectedIndex] && l.Subject == s) + "개 반").ToArray();
                subject.SelectedIndex = subjects.Length > 0 ? 0 : -1; Classes();
            };
            subject.SelectedIndexChanged += (_, _) => Classes();
            save.Clicked += async (_, _) => await Act(async () =>
            {
                if (chosen.Count < 2) throw new InvalidOperationException("함께 움직일 수업을 둘 이상 체크하세요.");
                var title = (name.Text ?? "").Trim() is { Length: > 0 } typed ? typed : string.Join("·", chosen.Select(m => m.Subject).Distinct().Take(3)) + " 묶음";
                await source.Send("api/timetable/bundle-rules", new TimetableBundleRule(MacScheduleId(), title, chosen.ToArray()));
                await Load();
                status.Text = $"‘{title}’ 묶음을 저장했습니다.";
            });
            grade.SelectedIndex = 0; Picked();
            body.Add(DesktopPanel(form, 18));
        }
        await Act(Load);
        if (!MacScheduleActive(source)) return;
        await Navigation.PushAsync(page);
    }
}
