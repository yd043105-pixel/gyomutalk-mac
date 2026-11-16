using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls.Shapes;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Mobile;

// The whole-school board, as on Windows: teachers down the side, the week's periods across. Picking a lesson lights
// every lesson it can trade places with; the server works those out, so the board never offers a refused trade.
public sealed partial class MessengerPage
{
    // The same twenty pastels as Windows (TimetableWindow.Board.cs), in the same order: no greens (green means "you
    // can swap here") and no yellows (a changed cell), every pair at least 9 apart in CIE Lab.
    static readonly string[] BundlePalette =
    [
        "#9DEDF1", "#EAB6ED", "#F4AFB6", "#9DE0F1", "#F4AFF4", "#F4B9AF", "#AECCE0", "#F1C6EF", "#F1D1C6", "#9DC7F1",
        "#F4AFD5", "#F1BA9D", "#AFC0F4", "#F9D2E6", "#E0CCAE", "#D2CBEB", "#EDB6CC", "#9DF1E0", "#D1C1F6", "#E6BCC1",
    ];
    static readonly Color SwapOption = Color.FromArgb("#CFEFD9"), SwapPick = Color.FromArgb("#C9DAFB");
    static readonly CultureInfo KoreanCulture = CultureInfo.GetCultureInfo("ko-KR");
    // Bundles take the palette in the order of their names, as on Windows, so a bundle has one colour on both.
    static Dictionary<string, Color> BundleColors(IEnumerable<string> names)
    {
        var map = new Dictionary<string, Color>();
        foreach (var name in names.Distinct().Order(StringComparer.Create(KoreanCulture, false)))
            map[name] = Color.FromArgb(BundlePalette[map.Count % BundlePalette.Length]);
        return map;
    }
    static async Task<Dictionary<string, Color>> LoadBundleColors(MessengerApi source)
    {
        var names = new List<string>();
        foreach (var path in new[] { "api/timetable/bundle-rules", "api/timetable/bundles" })
        {
            // A server without one of the two lists simply gives no names from it.
            try { names.AddRange((await source.Get(path)).EnumerateArray().Select(b => b.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0)); }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException) { }
        }
        return BundleColors(names);
    }
    static string BoardClass(string name) { var m = Regex.Match(name, @"(\d+)\s*학년\s*(\d+)\s*반"); return m.Success ? m.Groups[1].Value + "-" + m.Groups[2].Value : name; }
    static string BoardTeacher(TimetableSlot lesson) => lesson.TeacherId.Length > 0 ? lesson.TeacherId : "name:" + lesson.TeacherName;
    static string BoardDay(string date) => DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("M.d(ddd)", KoreanCulture);

    sealed class SwapBoard
    {
        public DateTime Week;
        public bool All, Assume, Busy;
        public string Reason = "";
        public string? RequestId;
        public string[] Dependencies = [];
        public readonly List<(TimetableSwapOption Option, string Label)> Moves = [];
        public TimetableSwapPlan? Plan;
        public TimetablePlanSlot? Pick;
        public Dictionary<TimetablePosition, TimetableSwapOption> Options = [];
        public readonly Dictionary<TimetablePosition, Label> Cells = [];
        public readonly Dictionary<Label, (string Kind, Color? Bundle)> Paint = [];
        public Dictionary<string, Color> Bundles = [];
        public bool Manager;
        public string[] Dates => Enumerable.Range(0, 5).Select(i => Week.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        public TimetableExchange[]? Exchanges => Moves.Count == 0 ? null : Moves.SelectMany(m => m.Option.Exchanges).ToArray();
    }

    async Task MacSwapBoard(MessengerApi source, DateTime selected, bool manager)
    {
        var state = new SwapBoard { Week = selected.Date.AddDays(-((int)selected.DayOfWeek + 6) % 7), Manager = manager };
        var page = DesktopPage("수업 교체 신청");
        var host = new ContentView { Padding = new Thickness(20, 12, 20, 14) };
        ((Grid)page.Content).Add(host, 0, 1);
        var status = Text("", 12); status.TextColor = Muted; status.MaxLines = 2; status.LineBreakMode = LineBreakMode.TailTruncation;
        async Task Act(Func<Task> action)
        {
            if (state.Busy || !MacScheduleActive(source)) return;
            state.Busy = true; host.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { if (MacScheduleActive(source)) status.Text = error is HttpRequestException ? "학교 서버에 연결하지 못했습니다." : error.Message; }
            finally { state.Busy = false; host.IsEnabled = true; }
        }
        async Task Load()
        {
            state.Bundles = await LoadBundleColors(source);
            var requests = (await source.Get("api/timetable/swaps")).Deserialize(MobileJson.Default.TimetableSwapArray)!;
            var pending = requests.Where(s => s.Status == "pending").ToArray();
            state.Dependencies = state.Assume ? pending.Select(s => s.Id).Order(StringComparer.Ordinal).ToArray() : [];
            state.Plan = (await source.Send("api/timetable/swap-plan", new TimetableSwapPlanRequest(state.Dates, state.Dependencies, state.Exchanges)))
                .Deserialize(MobileJson.Default.TimetableSwapPlan)!;
            if (!MacScheduleActive(source)) return;
            state.Pick = null; state.Options = []; state.Cells.Clear(); state.Paint.Clear();
            host.Content = Arrange(pending);
        }
        void Repaint(Label cell)
        {
            var (kind, bundle) = state.Paint[cell];
            cell.BackgroundColor = kind switch { "option" => SwapOption, "pick" => SwapPick, "changed" => ChangeTint, _ => bundle ?? (kind == "mine" ? Pale : Surface) };
        }
        void ClearPick() { state.Pick = null; state.Options = []; foreach (var cell in state.Paint.Keys) Repaint(cell); }
        async Task PickLesson(TimetablePlanSlot slot)
        {
            ClearPick();
            if (!slot.Mine && !state.Manager && slot.BundleName is null) { status.Text = "내 수업만 옮길 수 있습니다. 내 수업은 파란색으로 표시됩니다."; return; }
            var options = (await source.Send("api/timetable/swap-options", new TimetableSwapOptionsRequest(state.Dates, state.Dependencies, state.Exchanges, slot.Position)))
                .Deserialize(MobileJson.Default.TimetableSwapOptionArray)!;
            if (!MacScheduleActive(source)) return;
            state.Pick = slot; state.Options = options.ToDictionary(o => o.Target);
            if (state.Cells.TryGetValue(slot.Position, out var picked)) picked.BackgroundColor = SwapPick;
            foreach (var option in options) if (state.Cells.TryGetValue(option.Target, out var cell)) cell.BackgroundColor = SwapOption;
            var hidden = options.Count(o => !state.Cells.ContainsKey(o.Target));
            var what = $"{slot.Lesson.ClassName} {BoardDay(slot.Position.Date)} {slot.Position.Period}교시 {slot.Lesson.Subject}";
            status.Text = options.Length == 0
                ? what + ": 이번 주에는 바꿀 수 있는 수업이 없습니다. 다른 주를 확인해 보세요."
                : what + $": 초록색 {options.Length - hidden}칸과 바꿀 수 있습니다." + (hidden > 0 ? $" ‘모든 선생님 보기’를 켜면 {hidden}칸이 더 보입니다." : "");
        }
        async Task Apply(TimetableSwapOption option)
        {
            if ((state.Exchanges?.Length ?? 0) + option.Exchanges.Length > 12)
                throw new InvalidOperationException("한 번에 신청할 수 있는 교체는 12쌍까지입니다. 지금까지 바꾼 내용을 먼저 신청하세요.");
            var slots = state.Plan!.Slots.ToDictionary(s => s.Position, s => s.Lesson);
            string Lesson(TimetablePosition p) => slots.TryGetValue(p, out var l) ? $"{p.Period}교시 {l.Subject}({l.TeacherName})" : p.Period + "교시";
            string Line(TimetableExchange e) => $"{BoardDay(e.First.Date)} {(slots.TryGetValue(e.First, out var l) ? l.ClassName : "")} {Lesson(e.First)}  ↔  " +
                (e.Second.Date == e.First.Date ? "" : BoardDay(e.Second.Date) + " ") + Lesson(e.Second);
            state.Moves.Add((option, string.Join("\n", option.Exchanges.Select((e, i) => (i == 0 ? "" : "함께 이동(묶음) · ") + Line(e)))));
            state.RequestId = null;
            await Load();
            status.Text = option.Exchanges.Length > 1 ? $"수업을 맞바꿨습니다. 묶음수업 {option.Exchanges.Length}개 학급이 함께 움직였습니다." : "수업을 맞바꿨습니다. 아래 ‘변경 기록’을 확인한 뒤 신청하세요.";
        }
        void Pressed(TimetablePlanSlot[] lessons)
        {
            if (state.Busy) return;
            if (state.Pick is not null && lessons.Select(l => l.Position).FirstOrDefault(state.Options.ContainsKey) is { } target)
            { var option = state.Options[target]; _ = Act(() => Apply(option)); return; }
            if (lessons.Length == 0) { ClearPick(); status.Text = ""; return; }
            var slot = lessons.FirstOrDefault(l => l.Mine) ?? lessons[0];
            if (state.Pick?.Position == slot.Position) return;
            _ = Act(() => PickLesson(slot));
        }

        View Arrange(TimetableSwap[] pending)
        {
            var plan = state.Plan!;
            var root = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, RowSpacing = 6 };
            // Title row: week, then the two switches.
            var head = new HorizontalStackLayout { Spacing = 6 };
            Button Step(string label, int days, string name)
            {
                var button = new Button { Text = label, FontSize = 18, WidthRequest = 34, MinimumHeightRequest = 30, Padding = 0, BackgroundColor = Colors.Transparent, BorderWidth = 0, TextColor = Blue };
                SemanticProperties.SetDescription(button, name); ToolTipProperties.SetText(button, name);
                button.Clicked += async (_, _) => await Act(async () => { state.Week = state.Week.AddDays(days); await Load(); });
                return button;
            }
            var week = Text(state.Week.ToString("M.d", CultureInfo.InvariantCulture) + " ~ " + state.Week.AddDays(4).ToString("M.d", CultureInfo.InvariantCulture), 14, true); week.VerticalOptions = LayoutOptions.Center;
            head.Add(Step("‹", -7, "이전 주")); head.Add(week); head.Add(Step("›", 7, "다음 주"));
            View Toggle(string label, bool value, Action<bool> change, string id)
            {
                var row = new HorizontalStackLayout { Spacing = 2, Margin = new Thickness(12, 0, 0, 0) };
                var box = new CheckBox { IsChecked = value, Color = Blue, AutomationId = id, VerticalOptions = LayoutOptions.Center };
                var text = Text(label, 13); text.VerticalOptions = LayoutOptions.Center;
                box.CheckedChanged += async (_, e) => await Act(async () => { change(e.Value); await Load(); });
                row.Add(box); row.Add(text); return row;
            }
            var mine = plan.Slots.Where(s => s.Mine).ToArray();
            if (mine.Length > 0) head.Add(Toggle("모든 선생님 보기", state.All, v => state.All = v, "mac-board-all"));
            if (pending.Length > 0 && state.Moves.Count == 0) head.Add(Toggle($"대기 중인 신청 {pending.Length}건을 승인된 것으로 보기", state.Assume, v => state.Assume = v, "mac-board-assume"));
            root.Add(head, 0, 0);

            // How to use it, or what just happened, and the colour key on the right.
            var under = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 12 };
            if (status.Text.Length == 0) status.Text = "옮길 수업을 누르면 바꿀 수 있는 수업이 초록색으로 표시됩니다. 초록 칸을 누르면 두 수업이 맞바뀝니다.";
            (status.Parent as Layout)?.Remove(status); under.Add(status);
            var legend = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Start };
            void Key(string label, Color color)
            {
                legend.Add(new Border { WidthRequest = 13, HeightRequest = 13, BackgroundColor = color, Stroke = Line, StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 3 }, VerticalOptions = LayoutOptions.Center });
                var text = Text(label, 12); text.TextColor = Muted; text.Margin = new Thickness(0, 0, 8, 0); legend.Add(text);
            }
            Key("내 수업", Pale); Key("바꿀 수 있는 수업", SwapOption); Key("이번 신청으로 바뀐 칸", ChangeTint);
            var bundleKey = Text("◆ 묶음수업 · 같은 색은 같은 묶음", 12); bundleKey.TextColor = Muted; legend.Add(bundleKey);
            under.Add(legend, 1);
            root.Add(under, 0, 1);

            root.Add(BoardTable(pending), 0, 2);
            root.Add(Record(), 0, 3);
            return root;
        }

        View BoardTable(TimetableSwap[] pending)
        {
            var plan = state.Plan!; var dates = state.Dates; var today = DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (plan.Slots.Length == 0) return DesktopEmptyState("이 주에는 시간표가 없습니다", "다른 주로 이동하거나 기초시간표가 등록되었는지 확인하세요.");
            var periods = Math.Max(7, plan.Slots.Max(s => s.Position.Period));
            var mine = plan.Slots.Where(s => s.Mine).ToArray();
            var myRooms = mine.Select(s => s.Position.ClassId).ToHashSet();
            var changed = (state.Exchanges ?? []).SelectMany(e => new[] { e.First, e.Second }).ToHashSet();
            var waiting = pending.SelectMany(s => s.Changes.Select(c => c.Position)).ToHashSet();
            // A trade partner teaches one of my classes, so those teachers come first; "everyone" shows the whole school.
            var rows = plan.Slots.Where(s => state.All || mine.Length == 0 || s.Mine || myRooms.Contains(s.Position.ClassId))
                .GroupBy(s => BoardTeacher(s.Lesson)).Select(g => (Key: g.Key, Name: g.First().Lesson.TeacherName, Mine: g.Any(s => s.Mine)))
                .OrderByDescending(t => t.Mine).ThenBy(t => t.Name, StringComparer.Create(KoreanCulture, false)).ToArray();
            var byTeacher = plan.Slots.ToLookup(s => (BoardTeacher(s.Lesson), s.Position.Date, s.Position.Period));
            const double nameWidth = 72;
            Grid Columns() { var g = new Grid { ColumnSpacing = 1, RowSpacing = 1, BackgroundColor = Line }; g.ColumnDefinitions.Add(new(new GridLength(nameWidth))); for (var i = 0; i < dates.Length * periods; i++) g.ColumnDefinitions.Add(new(GridLength.Star)); return g; }
            Label Cell(string text, Color back, Color ink, double size = 11, bool bold = false) =>
                new() { Text = text, BackgroundColor = back, TextColor = ink, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
            var header = Columns(); header.RowDefinitions.Add(new(new GridLength(26))); header.RowDefinitions.Add(new(new GridLength(20)));
            var corner = Cell("교사", Subtle, Muted, 12); header.Add(corner, 0, 0); Microsoft.Maui.Controls.Grid.SetRowSpan(corner, 2);
            for (var d = 0; d < dates.Length; d++)
            {
                var isToday = dates[d] == today;
                var day = Cell(BoardDay(dates[d]) + (isToday ? " · 오늘" : ""), Subtle, isToday ? Blue : Navy, 12, true);
                header.Add(day, 1 + d * periods, 0); Microsoft.Maui.Controls.Grid.SetColumnSpan(day, periods);
                for (var p = 1; p <= periods; p++) header.Add(Cell(p.ToString(CultureInfo.InvariantCulture), Subtle, Muted, 10), d * periods + p, 1);
            }
            var body = Columns();
            for (var r = 0; r < rows.Length; r++)
            {
                body.RowDefinitions.Add(new(new GridLength(24)));
                var teacher = rows[r];
                var name = Cell(teacher.Name, teacher.Mine ? Pale : Subtle, teacher.Mine ? Blue : Navy, 12, teacher.Mine); name.HorizontalTextAlignment = TextAlignment.Start; name.Padding = new Thickness(8, 0, 4, 0);
                ToolTipProperties.SetText(name, teacher.Name); body.Add(name, 0, r);
                for (var d = 0; d < dates.Length; d++)
                    for (var p = 1; p <= periods; p++)
                    {
                        var lessons = byTeacher[(teacher.Key, dates[d], p)].ToArray();
                        var cell = Cell("", Surface, Navy, 10.5);
                        if (lessons.Length > 0)
                        {
                            var first = lessons[0];
                            cell.Text = BoardClass(first.Lesson.ClassName) + (lessons.Length > 1 ? "+" + (lessons.Length - 1) : "") + (first.BundleName is null ? "" : "◆");
                            cell.TextColor = first.Mine ? Blue : Navy; cell.FontAttributes = FontAttributes.Bold;
                            var bundle = lessons.FirstOrDefault(l => l.BundleName is not null)?.BundleName is { } b && state.Bundles.TryGetValue(b, out var tint) ? tint : (Color?)null;
                            state.Paint[cell] = (lessons.Any(l => changed.Contains(l.Position)) ? "changed" : first.Mine ? "mine" : "lesson", bundle);
                            Repaint(cell);
                            var tip = string.Join("\n", lessons.Select(l => $"{l.Lesson.ClassName} {l.Position.Period}교시 · {l.Lesson.Subject} ({l.Lesson.TeacherName})" +
                                (l.BundleName is null ? "" : " · 묶음 " + l.BundleName) + (changed.Contains(l.Position) ? " · 이번 신청으로 바뀜" : "") + (waiting.Contains(l.Position) ? " · 대기 중인 신청 있음" : "")));
                            ToolTipProperties.SetText(cell, tip); SemanticProperties.SetDescription(cell, $"{teacher.Name} {BoardDay(dates[d])} {p}교시 {first.Lesson.ClassName} {first.Lesson.Subject}");
                            cell.AutomationId = "mac-board-cell";
                            if (string.CompareOrdinal(dates[d], today) < 0) cell.Opacity = 0.45;
                            foreach (var lesson in lessons) state.Cells[lesson.Position] = cell;
                        }
                        var tap = new TapGestureRecognizer(); tap.Tapped += (_, _) => Pressed(lessons); cell.GestureRecognizers.Add(tap);
                        body.Add(cell, d * periods + p, r);
                    }
            }
            // The rows share the height of the window: a school-sized week fits, a bigger one scrolls.
            var scroll = new ScrollView { Content = body, Orientation = ScrollOrientation.Vertical };
            scroll.SizeChanged += (_, _) =>
            {
                if (scroll.Height <= 0 || rows.Length == 0) return;
                var height = Math.Clamp(Math.Floor((scroll.Height - rows.Length) / rows.Length), 16, 32);
                foreach (var row in body.RowDefinitions) row.Height = new GridLength(height);
                var size = height >= 26 ? 12 : height >= 20 ? 11 : 10;
                foreach (var label in body.Children.OfType<Label>()) label.FontSize = Microsoft.Maui.Controls.Grid.GetColumn(label) == 0 ? Math.Min(12, size + 1) : size;
            };
            var frame = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
            frame.Add(header, 0, 0); frame.Add(scroll, 0, 1);
            return new Border { Content = frame, Stroke = Line, StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 8 }, BackgroundColor = Line };
        }

        // Sending the request sits right under the board; below it, everything this request changes in the order it was done.
        View Record()
        {
            var panel = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            var send = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
            var reason = new Entry { ClassId = "school-field", Placeholder = "신청 사유 · 시간표 담당자와 본인에게만 보입니다", Text = state.Reason, MaxLength = 500, AutomationId = "mac-board-reason" };
            reason.TextChanged += (_, e) => { state.Reason = e.NewTextValue ?? ""; state.RequestId = null; };
            send.Add(reason);
            var submit = DesktopButton("교체 신청 제출", null, true); submit.AutomationId = "mac-board-submit"; submit.IsEnabled = state.Moves.Count > 0;
            submit.Clicked += async (_, _) => await Act(async () =>
            {
                state.RequestId ??= MacScheduleId();
                await source.Send("api/timetable/swaps", new TimetableSwapCreate(state.RequestId, state.Plan!.Revision, state.Exchanges!, state.Dependencies, state.Reason));
                state.Moves.Clear(); state.RequestId = null; state.Reason = "";
                await Load();
                status.Text = "교체 신청을 접수했습니다. 시간표 담당자가 승인하면 시간표에 반영되고 관련 선생님께 알림이 갑니다.";
            });
            send.Add(submit, 1); panel.Add(send);
            var record = new VerticalStackLayout { Spacing = 4 };
            var top = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
            top.Add(Text($"변경 기록 · {state.Moves.Count}건", 13, true));
            if (state.Moves.Count > 0)
            {
                var edits = new HorizontalStackLayout { Spacing = 4 };
                Button Edit(string label, Action change)
                {
                    var button = new Button { Text = label, FontSize = 13, BackgroundColor = Colors.Transparent, BorderWidth = 0, TextColor = Blue, Padding = new Thickness(8, 2) };
                    button.Clicked += async (_, _) => await Act(async () => { change(); state.RequestId = null; await Load(); });
                    return button;
                }
                edits.Add(Edit("마지막 변경 되돌리기", () => state.Moves.RemoveAt(state.Moves.Count - 1)));
                if (state.Moves.Count > 1) edits.Add(Edit("모두 되돌리기", state.Moves.Clear));
                top.Add(edits, 1);
            }
            record.Add(top);
            var lines = new VerticalStackLayout { Spacing = 4 };
            if (state.Moves.Count == 0) { var none = Text("아직 바꾼 수업이 없습니다. 위 표에서 옮길 수업을 누르세요.", 12); none.TextColor = Muted; lines.Add(none); }
            for (var i = 0; i < state.Moves.Count; i++) lines.Add(Text($"{i + 1}. {state.Moves[i].Label}", 12));
            record.Add(new ScrollView { Content = lines, MaximumHeightRequest = 78 });
            panel.Add(DesktopPanel(record, 10, Subtle));
            return panel;
        }

        await Act(Load);
        if (!MacScheduleActive(source)) return;
        await Navigation.PushAsync(page);
    }
}
