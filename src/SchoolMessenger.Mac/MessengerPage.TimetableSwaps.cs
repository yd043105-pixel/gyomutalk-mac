using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    Page? timetableExternalPage;
    Page? timetablePage;
    bool MacScheduleActive(MessengerApi source) => api == source && !covered && desktopMenu["timetable"].IsVisible &&
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
    (ContentPage Page, VerticalStackLayout Body, Action<string, Func<Task>> Add, Label Status) MacSchedulePage(MessengerApi source, string title)
    {
        // The same frame as the other desktop pages: the window header, then the page with its title.
        var body = new VerticalStackLayout { Padding = new Thickness(24, 16), Spacing = 10 };
        var status = Text("", 13); status.TextColor = Muted;
        var page = DesktopPage(title);
        ((Grid)page.Content).Add(new ScrollView { Content = body }, 0, 1);
        body.Add(Text(title, 22, true));
        body.Add(status);
        bool busy = false;
        void Add(string label, Func<Task> action)
        {
            var button = DesktopButton(label); button.HorizontalOptions = LayoutOptions.Start;
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
            // What this page showed stops the five-minute reminder for those requests.
            var waiting = requests.Where(s => s.Status is "pending" or "review").Select(s => s.Id).ToArray();
            if (manager) MarkMacSwapsSeen(waiting, waiting);
            rows.Children.Clear();
            selected.Clear();
            var byId = requests.ToDictionary(s => s.Id);
            foreach (var swap in requests)
            {
                var panel = new VerticalStackLayout { Spacing = 4 };
                // A trade between weeks moves a lesson of one week into another: worth a look before approving.
                var across = swap.Changes.Select(c => DateTime.ParseExact(c.Position.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Select(d => d.AddDays(-((int)d.DayOfWeek + 6) % 7)).Distinct().Count() > 1;
                panel.Add(Text($"{swap.OwnerName} · {MacScheduleStatus(swap.Status)}" + (across ? " · 다른 주와 교체" : ""), 14, true));
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
        // How far apart traded lessons may be, for the whole school. A server without the setting shows no choice.
        if (manager && setup.TryGetProperty("swapWeeks", out var range) && range.ValueKind == JsonValueKind.Number)
        {
            (int Weeks, string Label)[] choices = [(0, "같은 주만"), (1, "다음 주까지"), (2, "2주 뒤까지"), (3, "3주 뒤까지"), (4, "4주 뒤까지")];
            var weeks = new Picker { Title = "교체할 수 있는 범위", ItemsSource = choices.Select(c => c.Label).ToList(), SelectedIndex = Array.FindIndex(choices, c => c.Weeks == range.GetInt32()), WidthRequest = 160, AutomationId = "mac-swap-range" };
            var label = Text("교체할 수 있는 범위", 13); label.VerticalOptions = LayoutOptions.Center;
            weeks.SelectedIndexChanged += async (_, _) =>
            {
                if (weeks.SelectedIndex < 0 || !MacScheduleActive(source)) return;
                try
                {
                    await MacSend(source, "api/timetable/swap-range", new System.Text.Json.Nodes.JsonObject { ["weeks"] = choices[weeks.SelectedIndex].Weeks }, HttpMethod.Put);
                    ui.Status.Text = $"이제 선생님들이 {choices[weeks.SelectedIndex].Label} 수업을 바꿀 수 있습니다.";
                }
                catch (Exception error) when (error is InvalidOperationException or HttpRequestException or TaskCanceledException) { ui.Status.Text = error.Message; }
            };
            ui.Body.Add(new HorizontalStackLayout { Spacing = 10, Children = { label, weeks } });
        }
        // Weekly times (creative activities) that only the timetable manager trades. A server without them shows no button.
        if (manager && setup.TryGetProperty("fixedTimes", out var fixedTimes) && fixedTimes.ValueKind == JsonValueKind.Array)
            ui.Add("교체하지 않는 시간", () => MacFixedTimes(source, setup));
        if (manager)
        {
            // The board already checked every rule, so a request can be approved and published without a NEIS file.
            ui.Add("선택 신청 승인·게시", async () =>
            {
                var ids = selected.Where(p => p.Value.IsChecked).Select(p => p.Key).ToArray();
                if (ids.Length == 0) throw new InvalidOperationException("승인할 신청을 선택하세요.");
                if (!await DisplayAlertAsync("교체 신청 승인", $"선택한 신청 {ids.Length}건을 승인하고 시간표에 게시할까요? 관련 선생님께 알림이 갑니다.", "승인·게시", "닫기") || !MacScheduleActive(source)) return;
                await MacSend(source, "api/timetable/swaps/approve", new System.Text.Json.Nodes.JsonObject { ["candidateId"] = "", ["clientId"] = MacScheduleId(), ["swapIds"] = new System.Text.Json.Nodes.JsonArray(ids.Select(id => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id)).ToArray()) });
                await Reload(); ui.Status.Text = $"신청 {ids.Length}건을 승인하고 시간표에 게시했습니다.";
            });
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
        // Names shared by several accounts get one choice per subject they teach, as on Windows; the server remembers the
        // choice and fills it in next time. An older server sends no subjects, so the choice is per name.
        var links = new List<(string Name, string? Subject, string[] Ids, Picker Picker)>();
        var merged = imports.SelectMany(d => d.GetProperty("teachers").EnumerateArray()).GroupBy(t => t.GetProperty("source").GetString()!).ToArray();
        foreach (var group in merged)
        {
            var person = group.First(); var name = group.Key;
            var options = person.GetProperty("candidates").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (options.Length <= 1) continue;
            var users = setup.GetProperty("teachers").EnumerateArray().Where(t => options.Contains(t.GetProperty("id").GetString())).ToArray();
            var labels = users.Select(u => $"{u.GetProperty("name").GetString()} · {u.GetProperty("department").GetString()} · {u.GetProperty("username").GetString()}").ToArray();
            var accountIds = users.Select(u => u.GetProperty("id").GetString()!).ToArray();
            if (links.Count == 0) { var head = Text("이름이 같은 선생님이 있어 과목마다 계정을 골라 주세요", 15, true); ui.Body.Add(head); var hint = Text("고른 내용은 기억해 두었다가 다음에 시간표를 올릴 때 미리 채웁니다.", 12); hint.TextColor = Muted; ui.Body.Add(hint); }
            var subjects = group.SelectMany(t => t.TryGetProperty("subjects", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array ? list.EnumerateArray() : Enumerable.Empty<System.Text.Json.JsonElement>())
                .GroupBy(x => x.GetProperty("subject").GetString() ?? "").Select(g => (Subject: g.Key, Linked: g.Select(x => x.TryGetProperty("linked", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String ? l.GetString() : null).FirstOrDefault(l => l is not null))).ToArray();
            void Choice(string? subject, string? linked)
            {
                var picker = new Picker { ClassId = "school-field", Title = name + (subject is null ? "" : " · " + subject) + " 계정 선택", ItemsSource = labels, SelectedIndex = linked is null ? -1 : Array.IndexOf(accountIds, linked), AutomationId = "mac-upload-link" };
                var row = new Grid { ColumnDefinitions = { new(new GridLength(200)), new(GridLength.Star) }, ColumnSpacing = 10 };
                var label = Text(name + (subject is null ? "" : " · " + subject), 13, true); label.VerticalOptions = LayoutOptions.Center; row.Add(label); row.Add(DesktopPanel(picker, 4), 1);
                links.Add((name, subject, accountIds, picker)); ui.Body.Add(row);
            }
            if (subjects.Length == 0) Choice(null, null); else foreach (var (subject, linked) in subjects) Choice(subject, linked);
        }
        var preview = new VerticalStackLayout { Spacing = 4 };
        string? candidate = null, clientId = null;
        var draftIds = imports.Select(i => i.GetProperty("draftId").GetString()!).ToArray();
        ui.Add("변경 내용 미리보기", async () =>
        {
            candidate = null;
            preview.Children.Clear();
            if (links.Any(p => p.Picker.SelectedIndex < 0)) throw new InvalidOperationException("이름이 같은 선생님의 계정을 과목마다 고르세요.");
            var teachers = links.Select(p => new TimetableLink(p.Name, p.Ids[p.Picker.SelectedIndex], p.Subject)).ToArray();
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

    // 게시 이력: every published timetable, newest first; one without approved swaps can be withdrawn.
    async Task MacScheduleHistory(MessengerApi source)
    {
        var ui = MacSchedulePage(source, "게시 이력");
        var rows = new VerticalStackLayout { Spacing = 8 }; ui.Body.Add(rows);
        async Task Reload()
        {
            var history = (await source.Get("api/timetable/history")).EnumerateArray().ToArray(); if (!MacScheduleActive(source)) return;
            rows.Children.Clear();
            if (history.Length == 0) rows.Add(DesktopEmptyState("게시한 시간표가 없습니다", "시간표를 올려 게시하면 여기에 기록이 남습니다."));
            foreach (var r in history)
            {
                var withdrawn = r.GetProperty("withdrawn").GetBoolean(); var swaps = r.TryGetProperty("approvedSwapCount", out var approvals) ? approvals.GetInt32() : 0;
                var line = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
                var words = new VerticalStackLayout { Spacing = 2 };
                var name = Text($"#{r.GetProperty("revision")} · {r.GetProperty("fileName").GetString()}", 13, true); name.MaxLines = 1; name.LineBreakMode = LineBreakMode.TailTruncation; words.Add(name);
                var when = Text($"{r.GetProperty("start").GetString()} ~ {r.GetProperty("end").GetString()}" + (swaps > 0 ? $" · 교체 신청 {swaps}건 승인·게시" : ""), 12); when.TextColor = Muted; words.Add(when);
                line.Add(words);
                if (withdrawn) { var gone = Text("취소됨", 12, true); gone.TextColor = Warning; gone.VerticalOptions = LayoutOptions.Center; line.Add(gone, 1); }
                else if (swaps == 0)
                {
                    var id = r.GetProperty("id").GetString()!;
                    var cancel = DesktopButton("게시 취소", async () =>
                    {
                        if (!await DisplayAlertAsync("시간표 취소", "일자별 표 취소 시 기초시간표로 돌아갑니다. 취소할까요?", "게시 취소", "닫기") || !MacScheduleActive(source)) return;
                        await source.Send("api/timetable/" + id + "/cancel", new TimetableCommit("", MacScheduleId())); await Reload();
                    });
                    cancel.TextColor = Danger; cancel.FontSize = 14; cancel.MinimumHeightRequest = 32; line.Add(cancel, 1);
                }
                rows.Add(DesktopPanel(line, 12));
            }
        }
        await Reload();
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
