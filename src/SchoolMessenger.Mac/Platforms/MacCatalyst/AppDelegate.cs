using Foundation;
using UIKit;
namespace SchoolMessenger.Mobile;
[Register("AppDelegate")]
public sealed class AppDelegate:MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp()=>MauiProgram.CreateMauiApp();
    [Export("canInstallGyomutalkUpdate")]
    public bool CanInstallGyomutalkUpdate()=>MessengerPage.Active is {} page&&page.Navigation.NavigationStack.Count<=1;
    public override bool FinishedLaunching(UIApplication application,NSDictionary? options)
    {
        var result=base.FinishedLaunching(application,options);
#if MAC_VERIFY
        MainThread.BeginInvokeOnMainThread(async()=>{MacUpdater.Initialize(false);await MacRuntimeChecks.Run();});
#else
        MainThread.BeginInvokeOnMainThread(()=>MacUpdater.Initialize(true));
#endif
        return result;
    }
}
