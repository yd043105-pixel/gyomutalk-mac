using System.Runtime.InteropServices;
using Foundation;
namespace SchoolMessenger.Mobile;
internal static class MacUpdater
{
    static IntPtr plugin;
    internal static bool Available=>plugin!=IntPtr.Zero;
    internal static bool CanInstall=>Available&&SendBool(plugin,sel_registerName("canInstallNow"));
    internal static void Initialize(bool automatic)
    {
        try
        {
            var path=Path.Combine(NSBundle.MainBundle.BundlePath,"Contents","PlugIns","GyomutalkUpdater.bundle");
            using var bundle=NSBundle.FromPath(path);
            if(bundle is null||!bundle.Load())return;
            var type=objc_getClass("GYUpdaterPlugin");if(type==IntPtr.Zero)return;
            plugin=Send(Send(type,sel_registerName("alloc")),sel_registerName("init"));
            if(automatic&&Available)Send(plugin,sel_registerName("startUpdater"));
        }
        catch(Exception e){System.Diagnostics.Debug.WriteLine("Updater unavailable: "+e.GetType().Name);}
    }
    internal static async Task Settings(Page page)
    {
        if(!Available){await page.DisplayAlertAsync("프로그램 업데이트","업데이트 구성 요소가 없습니다. 공식 설치본을 다시 확인하세요.","확인");return;}
        var enabled=NSUserDefaults.StandardUserDefaults.BoolForKey("SUEnableAutomaticChecks");
        var choice=await page.DisplayActionSheetAsync("프로그램 업데이트","닫기",null,"지금 업데이트 확인",enabled?"자동 확인 끄기":"하루 한 번 자동 확인 켜기");
        if(choice=="지금 업데이트 확인")Send(plugin,sel_registerName("checkForUpdates"));
        else if(choice is "자동 확인 끄기" or "하루 한 번 자동 확인 켜기")Send(plugin,sel_registerName(enabled?"disableAutomaticChecks":"enableAutomaticChecks"));
    }
    [DllImport("/usr/lib/libobjc.dylib")]static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")]static extern IntPtr sel_registerName(string selector);
    [DllImport("/usr/lib/libobjc.dylib",EntryPoint="objc_msgSend")]static extern IntPtr Send(IntPtr target,IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib",EntryPoint="objc_msgSend")][return:MarshalAs(UnmanagedType.I1)]static extern bool SendBool(IntPtr target,IntPtr selector);
}
