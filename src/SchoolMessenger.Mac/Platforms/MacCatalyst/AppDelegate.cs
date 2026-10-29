using Foundation;
using UIKit;
namespace SchoolMessenger.Mobile;
[Register("AppDelegate")]
public sealed class AppDelegate:MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp()=>MauiProgram.CreateMauiApp();
#if MAC_VERIFY
    public override bool FinishedLaunching(UIApplication application,NSDictionary? options)
    {
        var result=base.FinishedLaunching(application,options);
        MainThread.BeginInvokeOnMainThread(async()=>await MacRuntimeChecks.Run());return result;
    }
#endif
}
