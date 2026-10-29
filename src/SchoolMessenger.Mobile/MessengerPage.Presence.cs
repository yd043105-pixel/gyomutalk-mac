namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    IDispatcherTimer? presenceTimer;
    readonly SemaphoreSlim presenceGate=new(1,1);
    MessengerApi? presenceOwner;
    async Task StartPhonePresence()
    {
#if MACCATALYST
        await StartDesktopPresence();return;
#else
        var source=api;
        if(covered||source?.Office!=true||source.Session.GetProperty("user").ValueKind!=System.Text.Json.JsonValueKind.Object)return;
        presenceOwner=source;
        presenceTimer??=Dispatcher.CreateTimer();
        if(presenceTimer.Interval!=TimeSpan.FromSeconds(45))
        {presenceTimer.Interval=TimeSpan.FromSeconds(45);presenceTimer.Tick+=async(_,_)=>{if(presenceOwner is {} owner)await SendPhonePresence(owner,false);};}
        await SendPhonePresence(source,false);
        if(!covered&&presenceOwner==source)presenceTimer.Start();
#endif
    }
    internal void StopPhonePresence()
    {
#if MACCATALYST
        StopDesktopPresence();
#else
        presenceTimer?.Stop();var source=presenceOwner;presenceOwner=null;
        if(source is not null)_=SendPhonePresence(source,true);
#endif
    }
    async Task SendPhonePresence(MessengerApi source,bool leave)
    {
        await presenceGate.WaitAsync();
        try
        {
            if(!leave&&(covered||presenceOwner!=source||api!=source))return;
            await source.Send(leave?"api/presence/mobile/leave":"api/presence/mobile");
        }
        catch(Exception error)when(error is HttpRequestException or InvalidOperationException or OperationCanceledException)
        { /* Losing the connection must not block the app. The server expires this lease after 90 seconds. */ }
        finally{presenceGate.Release();}
    }
}
