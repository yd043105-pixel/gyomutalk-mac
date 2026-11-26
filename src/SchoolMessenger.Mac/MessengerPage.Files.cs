using SchoolMessenger.Shared;

namespace SchoolMessenger.Mobile;

// Received files, as on Windows: a click saves into 문서/GyomutalkFiles; right-click offers that folder (with its path),
// 다른 이름으로 저장 and the folder itself.
public sealed partial class MessengerPage
{
    // Returns where the file now is. "elsewhere" downloads it first and then asks where to keep a copy.
    async Task<string> MacReceive(MessengerApi source, string id, string name, bool elsewhere = false, Action<AttachmentProgress>? progress = null, CancellationToken cancellation = default)
    {
        var path = elsewhere ? source.TemporaryPath(id, name) : MacFiles.FreePath(name);
        await source.SaveFile(id, path, progress, cancellation);
        if (elsewhere) await MacFiles.SaveAs(path);
        else feedback.Text = MacFiles.Saved(path);
        return path;
    }

    async Task MacReceiveDownload(MessengerApi source, string route, string name, long limit, bool elsewhere = false)
    {
        var path = elsewhere ? source.TemporaryPath(Guid.NewGuid().ToString("N"), name) : MacFiles.FreePath(name);
        await source.SaveDownload(route, path, limit);
        if (elsewhere) await MacFiles.SaveAs(path);
        else feedback.Text = MacFiles.Saved(path);
    }

    // Gives a received-file button its right-click menu; a failure shows where the page shows its notes.
    void MacFileMenu(View view, Func<bool, Task> receive)
    {
        async Task Safely(bool elsewhere)
        {
            try { await receive(elsewhere); }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                if (Navigation.NavigationStack.LastOrDefault() is ContentPage page && page != this) await page.DisplayAlertAsync(page.Title ?? "교무톡", error.Message, "확인");
                else feedback.Text = error.Message;
            }
        }
        FlyoutBase.SetContextFlyout(view, MacFiles.Menu(() => Safely(false), () => Safely(true)));
        ToolTipProperties.SetText(view, "누르면 GyomutalkFiles 폴더에 받습니다 · 오른쪽 클릭: 다른 이름으로 저장");
    }
}
