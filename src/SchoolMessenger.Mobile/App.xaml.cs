using Microsoft.Extensions.DependencyInjection;

namespace SchoolMessenger.Mobile;

public partial class App : Application
{
	public App()
	{
#if IOS || MACCATALYST
        PrepareIosStorage();
#endif
		InitializeComponent();
        UserAppTheme=AppTheme.Light;
	}

#if IOS || MACCATALYST
    internal static void PrepareIosStorage()
    {
        SecureStorage.DefaultAccessible = Security.SecAccessible.WhenUnlockedThisDeviceOnly;
        // iOS Keychain survives uninstall. A fresh installation must not inherit a prior login.
        if (!Preferences.Default.ContainsKey("ios-install-initialized"))
        {
            SecureStorage.Default.RemoveAll();
            Preferences.Default.Set("ios-install-initialized", true);
        }
    }
#endif

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new NavigationPage(new MessengerPage()) { BarBackgroundColor = Colors.White, BarTextColor = Color.FromArgb("#17243b") });
#if MACCATALYST
        window.Title="교무톡";window.Width=1120;window.Height=820;window.MinimumWidth=860;window.MinimumHeight=620;
        window.Destroying+=(_,_)=>MessengerPage.Active?.StopPhonePresence();
#else
		window.Stopped += (_, _) => MessengerPage.Active?.Cover();
		window.Resumed += async (_, _) => { if (MessengerPage.Active is { } page) await page.Resume(); };
#endif
		return window;
	}
}
