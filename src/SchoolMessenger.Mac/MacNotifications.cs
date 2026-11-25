using Foundation;
using UserNotifications;

namespace SchoolMessenger.Mobile;

// Banners in the macOS Notification Centre, as the Windows toast does: new mail and chat, a survey to answer, a changed
// timetable, swap requests waiting. Clicking one opens that place in the app.
sealed class MacNotifications:UNUserNotificationCenterDelegate
{
    static MacNotifications? shared;
    public static Action<string,string?>? Opened;
    public static void Start()
    {
        if(shared is not null)return;
        shared=new();var center=UNUserNotificationCenter.Current;center.Delegate=shared;
        // The first start asks once; a teacher who says no still sees the counts on the sidebar.
        center.RequestAuthorization(UNAuthorizationOptions.Alert|UNAuthorizationOptions.Sound|UNAuthorizationOptions.Badge,(_,_)=>{});
    }
    public static void Show(string title,string body,string target,string? room,bool sound)
    {
        if(shared is null)return;
        var content=new UNMutableNotificationContent{Title=title,Body=body};
        if(sound)content.Sound=UNNotificationSound.Default;
        content.UserInfo=NSDictionary.FromObjectsAndKeys(new NSObject[]{new NSString(target),new NSString(room??"")},new NSObject[]{new NSString("target"),new NSString("room")});
        // One banner per place: a newer count replaces the older one instead of piling up.
        var request=UNNotificationRequest.FromIdentifier("gyomutalk-"+target,content,null);
        UNUserNotificationCenter.Current.AddNotificationRequest(request,_=>{});
    }
    public static void Badge(int count)
    {
        if(shared is null||!OperatingSystem.IsMacCatalystVersionAtLeast(16))return;
        UNUserNotificationCenter.Current.SetBadgeCount(count,_=>{});
    }
    public override void WillPresentNotification(UNUserNotificationCenter center,UNNotification notification,Action<UNNotificationPresentationOptions> completionHandler)=>
        completionHandler(UNNotificationPresentationOptions.Banner|UNNotificationPresentationOptions.List|UNNotificationPresentationOptions.Sound);
    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center,UNNotificationResponse response,Action completionHandler)
    {
        var info=response.Notification.Request.Content.UserInfo;
        var target=info.ObjectForKey(new NSString("target"))?.ToString()??"message";var room=info.ObjectForKey(new NSString("room"))?.ToString();
        MainThread.BeginInvokeOnMainThread(()=>Opened?.Invoke(target,string.IsNullOrEmpty(room)?null:room));
        completionHandler();
    }
}
