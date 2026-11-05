using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    Page? timetableExternalPage;
    bool MacScheduleActive(MessengerApi source) => api == source && !covered &&
        source.Session.ValueKind == JsonValueKind.Object &&
        source.Session.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object;

    static string MacScheduleDate(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string MacScheduleId() => Guid.NewGuid().ToString("N");
    static string MacScheduleStatus(string status) => status switch
    {
        "pending" => "승인 대기", "approved" => "승인·게시 완료", "rejected" => "반려",
        "cancelled" => "취소", _ => "재검토 필요"
    };
    static string MacSwapLine(TimetableSwapChange change) =>
        $"{change.Position.Date} · {change.Before.ClassName} {change.Position.Period}교시 " +
        $"{change.Before.Subject} ({change.Before.TeacherName}) → {change.After.Subject} ({change.After.TeacherName})";
    static string MacSwapTitle(TimetableSwap swap) =>
        $"{swap.OwnerName} · {MacScheduleStatus(swap.Status)} · {MacSwapLine(swap.Changes[0])}";
    static string MacSlotLabel(TimetablePlanSlot slot, HashSet<TimetablePosition> waiting) =>
        $"{slot.Lesson.ClassName} {slot.Position.Period}교시 · {slot.Lesson.Subject} ({slot.Lesson.TeacherName})" +
        (slot.Mine ? " · 내 수업" : "") + (slot.BundleName is null ? "" : " · 묶음 " + slot.BundleName) +
        (waiting.Contains(slot.Position) ? " · 신청 있음" : "");

    (ContentPage Page, VerticalStackLayout Body, Action<string, Func<Task>> Add, Label Status) MacSchedulePage(MessengerApi source, string title)
    {
        var body = new VerticalStackLayout { Padding = 18, Spacing = 10 };
        var status = Text("", 13);
        var page = new ContentPage { Title = title, BackgroundColor = BackgroundColor, Content = new ScrollView { Content = body } };
        body.Add(Text(title, 22, true));
        body.Add(status);
        bool busy = false;
        void Add(string label, Func<Task> action)
        {
            var button = new Button { Text = label, HorizontalOptions = LayoutOptions.Start };
            button.Clicked += async (_, _) =>
            {
                if (busy || !MacScheduleActive(source)) return;
                busy = true;
                body.IsEnabled = false;
                status.Text = "";
                try { await action(); }
                catch (Exception error) { if (MacScheduleActive(source)) status.Text = error.Message; }
                finally { busy = false; body.IsEnabled = true; }
            };
            body.Add(button);
        }
        return (page, body, Add, status);
    }

    async Task MacSwapEditor(MessengerApi source, DateTime selected)
    {
        var requests = (await source.Get("api/timetable/swaps")).Deserialize(MobileJson.Default.TimetableSwapArray)!;
        if (!MacScheduleActive(source)) return;
        var ui = MacSchedulePage(source, "수업 교체 신청");
        var firstDate = new DatePicker { Date = selected, Format = "yyyy.MM.dd" };
        var secondDate = new DatePicker { Date = selected, Format = "yyyy.MM.dd" };
        ui.Body.Add(Text("출발 날짜와 교환할 날짜를 선택하세요. 묶음은 구성 수업을 모두 추가합니다.", 13));
        var dates = new HorizontalStackLayout { Spacing = 12 };
        dates.Add(firstDate);
        dates.Add(secondDate);
        ui.Body.Add(dates);

        var pending = requests.Where(s => s.Status is "pending" or "review").ToArray();
        var waiting = pending.SelectMany(s => s.Changes.Select(c => c.Position)).ToHashSet();
        var dependencies = new Dictionary<string, CheckBox>();
        ui.Body.Add(Text("대기 신청 · 체크하면 반영된다고 가정", 14, true));
        foreach (var swap in pending)
        {
            var check = new CheckBox { IsEnabled = swap.Status == "pending" };
            var row = new HorizontalStackLayout { Spacing = 5 };
            row.Add(check);
            row.Add(Text(MacSwapTitle(swap), 12));
            ui.Body.Add(row);
            if (swap.Status == "pending") dependencies.Add(swap.Id, check);
        }

        var first = new Picker { Title = "출발 수업" };
        var second = new Picker { Title = "같은 학급의 교환 수업" };
        var added = new VerticalStackLayout { Spacing = 4 };
        var reason = new Entry { Placeholder = "신청 사유 (수업계와 본인에게 표시)", MaxLength = 500 };
        var pairs = new List<TimetableExchange>();
        TimetableSwapPlan? plan = null;
        TimetablePlanSlot[] firstOptions = [], targetOptions = [], secondOptions = [];
        string[] planDependencies = [];
        string? requestId = null;
        void Reset()
        {
            plan = null;
            firstOptions = targetOptions = secondOptions = [];
            first.ItemsSource = null;
            second.ItemsSource = null;
            pairs.Clear();
            added.Children.Clear();
            requestId = null;
        }
        firstDate.DateSelected += (_, _) => Reset();
        secondDate.DateSelected += (_, _) => Reset();
        foreach (var check in dependencies.Values) check.CheckedChanged += (_, _) => Reset();
        reason.TextChanged += (_, _) => requestId = null;
        ui.Add("선택한 날짜의 수업 불러오기", async () =>
        {
            Reset();
            if (firstDate.Date is not { } from || secondDate.Date is not { } to)
                throw new InvalidOperationException("두 날짜를 선택하세요.");
            var start = MacScheduleDate(from);
            var end = MacScheduleDate(to);
            var chosen = dependencies.Where(p => p.Value.IsChecked).Select(p => p.Key).ToHashSet();
            foreach (var id in chosen.ToArray()) AddPreceding(id);
            void AddPreceding(string id)
            {
                foreach (var parent in requests.Single(r => r.Id == id).Dependencies)
                    if (dependencies.ContainsKey(parent) && chosen.Add(parent)) AddPreceding(parent);
            }
            planDependencies = chosen.Order(StringComparer.Ordinal).ToArray();
            plan = (await source.Send("api/timetable/swap-plan", new TimetableSwapPlanRequest([start, end], planDependencies)))
                .Deserialize(MobileJson.Default.TimetableSwapPlan)!;
            if (!MacScheduleActive(source)) return;
            firstOptions = plan.Slots.Where(s => s.Position.Date == start).OrderByDescending(s => s.Mine).ToArray();
            targetOptions = plan.Slots.Where(s => s.Position.Date == end).ToArray();
            first.ItemsSource = firstOptions.Select(s => MacSlotLabel(s, waiting)).ToArray();
            ui.Status.Text = planDependencies.Length == 0 ? "공식 시간표 기준입니다." : $"선행 신청 {planDependencies.Length}건의 반영을 가정합니다.";
        });
        first.SelectedIndexChanged += (_, _) =>
        {
            if (first.SelectedIndex < 0) { second.ItemsSource = null; return; }
            var position = firstOptions[first.SelectedIndex].Position;
            secondOptions = targetOptions.Where(s => s.Position.ClassId == position.ClassId && s.Position != position).ToArray();
            second.ItemsSource = secondOptions.Select(s => MacSlotLabel(s, waiting)).ToArray();
        };
        ui.Body.Add(first);
        ui.Body.Add(second);
        ui.Add("교환 쌍 추가", () =>
        {
            if (plan is null || first.SelectedIndex < 0 || second.SelectedIndex < 0)
                throw new InvalidOperationException("두 수업을 선택하세요.");
            var a = firstOptions[first.SelectedIndex];
            var b = secondOptions[second.SelectedIndex];
            var used = pairs.SelectMany(p => new[] { p.First, p.Second }).ToHashSet();
            if (pairs.Count >= 12 || used.Contains(a.Position) || used.Contains(b.Position))
                throw new InvalidOperationException("수업은 한 번만 선택할 수 있고 교환 쌍은 최대 12개입니다.");
            var pair = new TimetableExchange(a.Position, b.Position);
            pairs.Add(pair);
            requestId = null;
            var row = new HorizontalStackLayout { Spacing = 8 };
            row.Add(Text($"{a.Position.Date} {MacSlotLabel(a, waiting)} ↔ {b.Position.Date} {MacSlotLabel(b, waiting)}", 12));
            var remove = new Button { Text = "빼기" };
            remove.Clicked += (_, _) => { pairs.Remove(pair); added.Children.Remove(row); requestId = null; };
            row.Add(remove);
            added.Add(row);
            return Task.CompletedTask;
        });
        ui.Body.Add(added);
        ui.Body.Add(reason);
        ui.Add("교체 신청 제출", async () =>
        {
            if (plan is null || pairs.Count == 0) throw new InvalidOperationException("교환할 수업을 추가하세요.");
            requestId ??= MacScheduleId();
            await source.Send("api/timetable/swaps", new TimetableSwapCreate(requestId, plan.Revision, pairs.ToArray(), planDependencies, reason.Text));
            if (!MacScheduleActive(source)) return;
            Reset();
            ui.Status.Text = "교체 신청을 접수했습니다.";
        });
        await Navigation.PushAsync(ui.Page);
    }

    async Task MacSwapQueue(MessengerApi source, JsonElement setup, bool manager)
    {
        var ui = MacSchedulePage(source, "교체 신청 현황");
        var rows = new VerticalStackLayout { Spacing = 8 };
        var selected = new Dictionary<string, CheckBox>();
        var user = source.Session.GetProperty("user").GetProperty("id").GetString();
        async Task Reload()
        {
            var requests = (await source.Get("api/timetable/swaps")).Deserialize(MobileJson.Default.TimetableSwapArray)!;
            if (!MacScheduleActive(source)) return;
            rows.Children.Clear();
            selected.Clear();
            var byId = requests.ToDictionary(s => s.Id);
            foreach (var swap in requests)
            {
                var panel = new VerticalStackLayout { Spacing = 4 };
                panel.Add(Text($"{swap.OwnerName} · {MacScheduleStatus(swap.Status)}", 14, true));
                if (manager && swap.Status == "pending")
                {
                    var check = new CheckBox();
                    selected[swap.Id] = check;
                    panel.Add(check);
                }
                foreach (var change in swap.Changes) panel.Add(Text(MacSwapLine(change), 12));
                foreach (var dep in swap.Dependencies)
                    panel.Add(Text("선행 · " + (byId.TryGetValue(dep, out var parent) ? MacSwapTitle(parent) : dep[..8]), 12));
                if (swap.Reason.Length > 0) panel.Add(Text("사유 · " + swap.Reason, 12));
                if (swap.ReviewedAt is { } at) panel.Add(Text($"{swap.ReviewerName} · {Date(at)}", 12));
                if (swap.Status is "pending" or "review")
                {
                    if (swap.Owner == user) Decision("신청 취소", "cancelled");
                    if (manager) Decision("반려", "rejected");
                }
                void Decision(string label, string status) => panel.Add(DesktopButton(label, async () =>
                {
                    if (!await DisplayAlertAsync("교체 신청", $"{label}할까요?", label, "닫기") || !MacScheduleActive(source)) return;
                    await source.Send($"api/timetable/swaps/{swap.Id}/decision", new TimetableSwapDecision(status));
                    await Reload();
                }));
                rows.Add(Card(panel, 12));
            }
            if (requests.Length == 0) rows.Add(Text("아직 교체 신청이 없습니다.", 13));
        }
        ui.Add("새로 고침", Reload);
        if (manager)
        {
            ui.Add("선택 신청 승인·나이스 파일 선택", async () =>
            {
                var ids = selected.Where(p => p.Value.IsChecked).Select(p => p.Key).ToArray();
                if (ids.Length == 0) throw new InvalidOperationException("파일에 반영한 신청을 선택하세요.");
                await MacScheduleUpload(source, setup, ids, Reload);
            });
            ui.Add("신청 없이 나이스 파일 게시", () => MacScheduleUpload(source, setup, [], Reload));
        }
        ui.Body.Add(rows);
        await Reload();
        if (MacScheduleActive(source)) await Navigation.PushAsync(ui.Page);
    }

    async Task MacScheduleUpload(MessengerApi source, JsonElement setup, string[] ids, Func<Task> reload)
    {
        FileResult[] files;
        timetableExternalPage = Navigation.NavigationStack.LastOrDefault();
        try
        {
            files = (await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "나이스 시간표 엑셀 선택" }))?.ToArray() ?? [];
        }
        finally { timetableExternalPage = null; }
        if (files.Length == 0 || !MacScheduleActive(source)) return;
        if (files.Length > 20 || files.Any(f => !f.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("나이스 .xlsx 파일을 최대 20개 선택하세요.");
        var imports = new List<JsonElement>();
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(stream), "file", file.FileName);
            imports.Add(await source.Send("api/timetable/import", content: form));
            if (!MacScheduleActive(source)) return;
        }
        var ui = MacSchedulePage(source, ids.Length == 0 ? "시간표 게시" : $"교체 신청 {ids.Length}건 승인·게시");
        var links = new List<(string Name, string[] Ids, Picker Picker)>();
        foreach (var person in imports.SelectMany(d => d.GetProperty("teachers").EnumerateArray())
            .DistinctBy(t => t.GetProperty("source").GetString()))
        {
            var name = person.GetProperty("source").GetString()!;
            var options = person.GetProperty("candidates").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (options.Length <= 1) continue;
            var users = setup.GetProperty("teachers").EnumerateArray().Where(t => options.Contains(t.GetProperty("id").GetString())).ToArray();
            var picker = new Picker { Title = name + " 계정 선택", ItemsSource = users.Select(u =>
                $"{u.GetProperty("name").GetString()} · {u.GetProperty("department").GetString()} · {u.GetProperty("username").GetString()}").ToArray() };
            links.Add((name, users.Select(u => u.GetProperty("id").GetString()!).ToArray(), picker));
            ui.Body.Add(picker);
        }
        var preview = new VerticalStackLayout { Spacing = 4 };
        string? candidate = null, clientId = null;
        var draftIds = imports.Select(i => i.GetProperty("draftId").GetString()!).ToArray();
        ui.Add("변경 내용 미리보기", async () =>
        {
            candidate = null;
            preview.Children.Clear();
            if (links.Any(p => p.Picker.SelectedIndex < 0)) throw new InvalidOperationException("동명이인의 계정을 선택하세요.");
            var teachers = links.Select(p => new TimetableLink(p.Name, p.Ids[p.Picker.SelectedIndex])).ToArray();
            var result = await source.Send("api/timetable/preview", new TimetablePreviewRequest(draftIds[0], "", "", [], teachers, draftIds));
            if (!MacScheduleActive(source)) return;
            candidate = result.GetProperty("candidateId").GetString();
            clientId = MacScheduleId();
            foreach (var change in result.GetProperty("changes").EnumerateArray().Take(100))
                preview.Add(Text($"{change.GetProperty("date").GetString()} · {change.GetProperty("className").GetString()} " +
                    $"{change.GetProperty("period").GetInt32()}교시  {change.GetProperty("beforeSubject").GetString()} " +
                    $"({change.GetProperty("beforeTeacher").GetString()}) → {change.GetProperty("subject").GetString()} ({change.GetProperty("teacher").GetString()})", 12));
            ui.Status.Text = $"달라지는 수업 {result.GetProperty("changedCount").GetInt32()}개";
        });
        ui.Body.Add(preview);
        foreach (var link in links) link.Picker.SelectedIndexChanged += (_, _) => { candidate = null; preview.Children.Clear(); };
        ui.Add(ids.Length == 0 ? "게시" : "승인·시간표 게시", async () =>
        {
            if (candidate is null || clientId is null) throw new InvalidOperationException("변경 내용을 먼저 미리보세요.");
            await source.Send("api/timetable/publish", new TimetableCommit(candidate, clientId, ids));
            if (!MacScheduleActive(source)) return;
            await reload();
            await Navigation.PopAsync();
        });
        if (MacScheduleActive(source)) await Navigation.PushAsync(ui.Page);
    }

    async Task MacBundleEditor(MessengerApi source, DateTime selected)
    {
        var ui = MacSchedulePage(source, "묶음수업 설정");
        var day = new DatePicker { Date = selected, Format = "yyyy.MM.dd" };
        var name = new Entry { Placeholder = "묶음 이름", MaxLength = 60 };
        var kind = new Picker { ItemsSource = new[] { "연강", "동시 수업" }, SelectedIndex = 0 };
        var choices = new VerticalStackLayout { Spacing = 4 };
        var existing = new VerticalStackLayout { Spacing = 4 };
        var picked = new Dictionary<TimetablePosition, CheckBox>();
        async Task LoadBundles()
        {
            var bundles = (await source.Get("api/timetable/bundles")).Deserialize(MobileJson.Default.TimetableBundleArray)!;
            if (!MacScheduleActive(source)) return;
            existing.Children.Clear();
            foreach (var bundle in bundles.OrderBy(b => b.Slots[0].Date))
                existing.Add(DesktopButton($"{bundle.Slots[0].Date} · {bundle.Name} 삭제", async () =>
                {
                    if (!await DisplayAlertAsync("묶음수업", $"‘{bundle.Name}’ 묶음을 삭제할까요?", "삭제", "닫기") || !MacScheduleActive(source)) return;
                    await source.Send("api/timetable/bundles/" + bundle.Id, method: HttpMethod.Delete);
                    await LoadBundles();
                }));
        }
        ui.Body.Add(day);
        ui.Body.Add(name);
        ui.Body.Add(kind);
        ui.Body.Add(existing);
        day.DateSelected += (_, _) => { choices.Children.Clear(); picked.Clear(); };
        ui.Add("날짜의 수업 불러오기", async () =>
        {
            if (day.Date is not { } date) throw new InvalidOperationException("날짜를 선택하세요.");
            var plan = (await source.Send("api/timetable/swap-plan", new TimetableSwapPlanRequest([MacScheduleDate(date)], [])))
                .Deserialize(MobileJson.Default.TimetableSwapPlan)!;
            if (!MacScheduleActive(source)) return;
            choices.Children.Clear();
            picked.Clear();
            foreach (var slot in plan.Slots)
            {
                var row = new HorizontalStackLayout { Spacing = 5 };
                var check = new CheckBox { IsEnabled = slot.BundleName is null };
                picked.Add(slot.Position, check);
                row.Add(check);
                row.Add(Text(MacSlotLabel(slot, []), 12));
                choices.Add(row);
            }
        });
        ui.Add("선택 수업을 묶음으로 저장", async () =>
        {
            var slots = picked.Where(p => p.Value.IsChecked).Select(p => p.Key).ToArray();
            await source.Send("api/timetable/bundles", new TimetableBundle(MacScheduleId(), name.Text ?? "",
                kind.SelectedIndex == 0 ? "consecutive" : "simultaneous", slots));
            if (!MacScheduleActive(source)) return;
            choices.Children.Clear();
            picked.Clear();
            await LoadBundles();
        });
        ui.Body.Add(choices);
        await LoadBundles();
        if (MacScheduleActive(source)) await Navigation.PushAsync(ui.Page);
    }

    void PrintMacSchedule(string title, TimetableDay[] days, Label error)
    {
        if (days.Length == 0) return;
        var shown = days.Where(d => days.Length == 1 || d.Slots.Any(s => s.Subject.Length > 0) ||
            DateTime.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
        var html = new StringBuilder("<html><head><meta charset=\"utf-8\"><style>body{font-family:system-ui;font-size:10pt}table{border-collapse:collapse;width:100%}td,th{border:1px solid #999;padding:5px;vertical-align:top}</style></head><body>");
        html.Append("<h2>").Append(WebUtility.HtmlEncode(title)).Append("</h2><table><tr><th>교시</th>");
        foreach (var day in shown) html.Append("<th>").Append(WebUtility.HtmlEncode(day.Date)).Append("</th>");
        html.Append("</tr>");
        var last = shown.SelectMany(d => d.Slots).Where(s => s.Subject.Length > 0).Select(s => s.Period).DefaultIfEmpty(7).Max();
        for (var period = 1; period <= last; period++)
        {
            html.Append("<tr><th>").Append(period).Append("</th>");
            foreach (var day in shown)
            {
                html.Append("<td>");
                foreach (var lesson in day.Slots.Where(s => s.Period == period && s.Subject.Length > 0))
                    html.Append(WebUtility.HtmlEncode($"{lesson.Subject} · {lesson.ClassName} · {lesson.TeacherName}")).Append("<br>");
                html.Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</table></body></html>");
        var printer = UIKit.UIPrintInteractionController.SharedPrintController;
        var formatter = new UIKit.UIMarkupTextPrintFormatter(html.ToString());
        printer.PrintFormatter = formatter;
        timetableExternalPage = Navigation.NavigationStack.LastOrDefault();
        if (!printer.Present(true, (_, completed, failure) =>
        {
            timetableExternalPage = null;
            formatter.Dispose();
            if (!completed && failure is not null) error.Text = failure.LocalizedDescription;
        }))
        {
            timetableExternalPage = null;
            formatter.Dispose();
            error.Text = "인쇄 창을 열 수 없습니다.";
        }
    }
}
