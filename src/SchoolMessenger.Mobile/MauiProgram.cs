using Microsoft.Extensions.Logging;

namespace SchoolMessenger.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
#if ANDROID
        // The school form supplies its own border; avoid a second native underline inside it.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
            if(view is Entry{ClassId:"school-field"})handler.PlatformView.BackgroundTintList=Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        });
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
            if(view is Picker{ClassId:"school-field"})handler.PlatformView.BackgroundTintList=Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        });
        Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
            if(view is DatePicker{ClassId:"school-field"})handler.PlatformView.BackgroundTintList=Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        });
        Microsoft.Maui.Handlers.TimePickerHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
            if(view is TimePicker{ClassId:"school-field"})handler.PlatformView.BackgroundTintList=Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        });
        Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("SchoolStory",(handler,view)=>
        {
            if(view is Button{ClassId:"school-story"})handler.PlatformView.Gravity=Android.Views.GravityFlags.Left|Android.Views.GravityFlags.CenterVertical;
        });
#elif IOS || MACCATALYST
        // Match the Android form: the surrounding card supplies the only border.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
            if(view is Entry{ClassId:"school-field"})handler.PlatformView.BorderStyle=UIKit.UITextBorderStyle.None;
        });
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
            if(view is Picker{ClassId:"school-field"})handler.PlatformView.BorderStyle=UIKit.UITextBorderStyle.None;
        });
        Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
#if IOS
            if(view is DatePicker{ClassId:"school-field"})handler.PlatformView.BorderStyle=UIKit.UITextBorderStyle.None;
#endif
        });
        Microsoft.Maui.Handlers.TimePickerHandler.Mapper.AppendToMapping("SchoolField",(handler,view)=>
        {
#if IOS
            if(view is TimePicker{ClassId:"school-field"})handler.PlatformView.BorderStyle=UIKit.UITextBorderStyle.None;
#endif
        });
        Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("SchoolStory",(handler,view)=>
        {
            if(view is Button{ClassId:"school-story"})handler.PlatformView.HorizontalAlignment=UIKit.UIControlContentHorizontalAlignment.Left;
        });
#endif
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
