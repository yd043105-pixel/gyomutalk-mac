using System.Runtime.InteropServices;
using Foundation;
using UIKit;

namespace SchoolMessenger.Mobile;

// Files received in 교무톡 land in 문서/GyomutalkFiles without asking, as on Windows. The app is sandboxed, so its
// entitlements open just that one folder of the real home (temporary-exception.files.home-relative-path).
static class MacFiles
{
    [DllImport("libc")] static extern uint getuid();
    [DllImport("libc")] static extern IntPtr getpwuid(uint uid);

    // Inside the sandbox HOME is the app's container; the account's real home comes from the user database.
    // struct passwd on macOS: pw_name, pw_passwd, pw_uid + pw_gid, pw_change, pw_class, pw_gecos, pw_dir — pw_dir is the seventh 8-byte field.
    static string RealHome()
    {
        var entry = getpwuid(getuid());
        var home = entry == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry, 6 * IntPtr.Size));
        return string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
    }
    internal static string Folder { get; } = Path.Combine(RealHome(), "Documents", "GyomutalkFiles");

    // The sender chose the name: only its last part is kept, characters macOS and Windows refuse become _, and it stays short.
    internal static string CleanName(string? name)
    {
        var clean = string.Concat((name ?? "").Replace('\\', '/').Split('/')[^1].Select(c => c is ':' or '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "받은 파일";
        var stem = Path.GetFileNameWithoutExtension(clean); var extension = Path.GetExtension(clean);
        if (extension.Length > 20) { stem = clean; extension = ""; }
        if (stem.Length > 120) stem = stem[..120];
        if (stem.Length == 0 || stem.StartsWith('.')) stem = "_" + stem;
        return stem + extension;
    }

    // A free path in the folder: a file already there is kept, and the new one becomes "이름 (2).확장자".
    internal static string FreePath(string? name)
    {
        try { Directory.CreateDirectory(Folder); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("문서/GyomutalkFiles 폴더에 저장할 수 없습니다. 파일을 오른쪽 클릭해 ‘다른 이름으로 저장’을 쓰세요."); }
        var clean = CleanName(name);
        var stem = Path.GetFileNameWithoutExtension(clean); var extension = Path.GetExtension(clean);
        var path = Path.Combine(Folder, clean);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++) path = Path.Combine(Folder, $"{stem} ({n}){extension}");
        return path;
    }

    internal static string Saved(string path) => $"GyomutalkFiles 폴더에 저장했습니다 · {Path.GetFileName(path)}";

    // 다른 이름으로 저장: the macOS save panel copies the downloaded file to the place and name the teacher picks.
    internal static Task SaveAs(string path)
    {
        var done = new TaskCompletionSource();
        var picker = new UIDocumentPickerViewController([NSUrl.FromFilename(path)], true);
        picker.DidPickDocumentAtUrls += (_, _) => done.TrySetResult();
        picker.WasCancelled += (_, _) => done.TrySetResult();
        var top = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>().SelectMany(s => s.Windows).FirstOrDefault(w => w.IsKeyWindow)?.RootViewController;
        while (top?.PresentedViewController is { } shown) top = shown;
        if (top is null) throw new InvalidOperationException("저장 창을 열지 못했습니다.");
        top.PresentViewController(picker, true, null);
        return done.Task;
    }

    internal static void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        UIApplication.SharedApplication.OpenUrl(NSUrl.FromFilename(Folder), new UIApplicationOpenUrlOptions(), null);
    }

    // The right-click menu of a received file: the folder (with its path), another place, and the folder itself.
    internal static MenuFlyout Menu(Func<Task> into, Func<Task> elsewhere)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Func<Task> action) { var item = new MenuFlyoutItem { Text = text }; item.Clicked += async (_, _) => await action(); return item; }
        menu.Add(Item("지정된 폴더에 다운로드 · " + Folder, into));
        menu.Add(Item("다른 이름으로 저장…", elsewhere));
        menu.Add(new MenuFlyoutSeparator());
        menu.Add(Item("받은 파일 폴더 열기", () => { OpenFolder(); return Task.CompletedTask; }));
        return menu;
    }
}
