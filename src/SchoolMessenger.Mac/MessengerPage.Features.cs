using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace SchoolMessenger.Mobile;

// What Windows MainWindow.Features does for the Mac: the menus the school's administrator switched off stay hidden,
// and new mail, chat, surveys, timetable changes and waiting swap requests raise a banner and a count on the sidebar.
public sealed partial class MessengerPage
{
    HashSet<string> macFeaturesOff=[];
    bool macCourseDuty;
    // The feature drawn in the workspace right now, so switching it off on the server closes it here too.
    string? macPane;
    readonly Dictionary<string,long> macChatSeen=[];
    readonly HashSet<string> macKnownSurveys=[];
    readonly HashSet<long> macKnownTimetables=[];
    readonly HashSet<string> macSwapsNoticed=[];
    bool? macSwapManager;DateTime macSwapRoleAt,macSwapNoticeAt;
    int macUnreadMail,macUnreadChat;
    Func<string,Task>? macChatChanged,macSurveyChanged;
    bool MacFeature(string name)=>!macFeaturesOff.Contains(name);
    static bool MacQuiet{get=>Preferences.Default.Get("mac-quiet",false);set=>Preferences.Default.Set("mac-quiet",value);}
    static bool MacDoNotDisturb{get=>Preferences.Default.Get("mac-dnd",false);set=>Preferences.Default.Set("mac-dnd",value);}
    string MacUserKey=>api?.Session.ValueKind==JsonValueKind.Object&&api.Session.GetProperty("user") is{ValueKind:JsonValueKind.Object} user?user.GetProperty("id").GetString()??"":"";
    static HashSet<string> MacList(string key)=>Preferences.Default.Get(key,"").Split(',',StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    static void SaveMacList(string key,IEnumerable<string> items)=>Preferences.Default.Set(key,string.Join(',',items));
    HashSet<string> MacMutedRooms()=>MacList("mac-muted-"+MacUserKey);
    static bool Flag(JsonElement value,string name)=>value.TryGetProperty(name,out var flag)&&flag.ValueKind==JsonValueKind.True;
    static string RowId(JsonElement value)=>value.GetProperty("id").GetString()!;
    // Background work never interrupts the screen: a failure is tried again with the next event.
    static async Task MacQuietly(Func<Task> action)
    {
        try{await action();}
        catch(Exception error)when(error is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException or KeyNotFoundException or IOException or UnauthorizedAccessException){}
    }
    void ResetMacSession()
    {
        macFeaturesOff=[];macCourseDuty=false;macPane=null;macChatSeen.Clear();macKnownSurveys.Clear();macKnownTimetables.Clear();macSwapsNoticed.Clear();
        macSwapManager=null;macUnreadMail=macUnreadChat=0;macChatChanged=macSurveyChanged=null;ShowMacBadges();
    }
    async Task RefreshMacFeatures(MessengerApi source)
    {
        var off=new HashSet<string>();
        // An older server has no such list and shows everything.
        try{off=(await source.Get("api/features")).EnumerateObject().Where(p=>p.Value.ValueKind==JsonValueKind.False).Select(p=>p.Name).ToHashSet();}
        catch(MessengerApiRefusal e)when(e.Status==404){}
        // 수강신청 shows only for the curriculum duty; servers before it have no such route.
        var courses=false;
        try{courses=(await source.Get("api/courses/access")).GetProperty("manager").GetBoolean();}
        catch(MessengerApiRefusal e)when(e.Status is 403 or 404){}
        if(api!=source||desktopOwner!=source)return;
        macFeaturesOff=off;macCourseDuty=courses;ShowMacFeatures();
        var closed=macPane switch{"chat"=>!MacFeature("chat"),"survey"=>!MacFeature("survey"),"work"=>!MacFeature("tasks"),"courses"=>!(macCourseDuty&&MacFeature("courses")),_=>false};
        if(closed){macPane=null;homeTab="notices";view="received";offset=0;await Home();}
    }
    void ShowMacFeatures()
    {
        ShowRail("chat",MacFeature("chat"));ShowRail("survey",MacFeature("survey"));ShowRail("work",MacFeature("tasks"));
        ShowRail("calendar",MacFeature("calendar"));ShowRail("rooms",MacFeature("rooms"));ShowRail("courses",macCourseDuty&&MacFeature("courses"));
    }
    void WatchMacHub(HubConnection hub,MessengerApi source)
    {
        Task OnMain(Func<Task> action)=>MainThread.InvokeOnMainThreadAsync(()=>desktopOwner==source?MacQuietly(action):Task.CompletedTask);
        hub.On<JsonElement>("ChatChanged",id=>OnMain(async()=>
        {
            var room=id.ValueKind==JsonValueKind.String?id.GetString()!:"";
            if(macChatChanged is{} changed)await changed(room);
            if(MacFeature("chat"))await MacNotifyChat(source,room);
        }));
        hub.On<JsonElement>("SurveyChanged",id=>OnMain(async()=>{if(MacFeature("survey")&&id.ValueKind==JsonValueKind.String)await MacNotifySurvey(source,id.GetString()!);}));
        hub.On("FeaturesChanged",()=>OnMain(()=>RefreshMacFeatures(source)));
        hub.On<JsonElement,long>("TimetableChanged",(_,_)=>OnMain(()=>MacNotifyTimetable(source)));
    }
    void MacNotify(string title,string body,string target,string? room=null)
    {
        if(MacDoNotDisturb)return;
        MacNotifications.Show(title,body,target,room,!MacQuiet);
    }
    // The room being read right now raises nothing.
    string? macReadingRoom;
    bool MacReading(string room)=>macPane=="chat"&&macReadingRoom==room&&Navigation.NavigationStack.Count==1;
    async Task<JsonElement[]> MacRooms(MessengerApi source)
    {
        var server=await source.Get("api/chats");
        return desktopArchive?.Owner==source?await desktopArchive.LocalRooms(server):server.EnumerateArray().ToArray();
    }
    // Everything unread in one number: mail and chat together, without rooms set to silent or the room being read.
    async Task<JsonElement[]> MacCountUnread(MessengerApi source,JsonElement[]? rooms=null)
    {
        var mail=(await source.Get("api/messages?box=unread&offset=0")).GetArrayLength();
        if(MacFeature("chat"))rooms??=await MacRooms(source);
        rooms??=[];
        var muted=MacMutedRooms();
        var chat=MacFeature("chat")?rooms.Where(r=>!muted.Contains(RowId(r))&&!MacReading(RowId(r))).Sum(r=>r.GetProperty("unread").GetInt32()):0;
        if(desktopOwner==source){macUnreadMail=mail;macUnreadChat=chat;ShowMacBadges();}
        return rooms;
    }
    async Task MacNotifyUnread(MessengerApi source,string target,string? room,JsonElement[]? rooms=null)
    {
        rooms=await MacCountUnread(source,rooms);
        foreach(var known in rooms)macChatSeen[RowId(known)]=MacLastAt(known);
        var total=macUnreadMail+macUnreadChat;if(total==0)return;
        var more=macUnreadMail==100?"+":"";var goal=target=="message"&&macUnreadMail>0||macUnreadChat==0?"message":"chat";
        var muted=MacMutedRooms();
        MacNotify($"안 읽은 메시지 {total}{more}개",$"업무 메시지 {macUnreadMail}{more} · 채팅 {macUnreadChat}",goal,
            goal=="chat"?room??rooms.Where(r=>r.GetProperty("unread").GetInt32()>0&&!muted.Contains(RowId(r))).Select(RowId).FirstOrDefault():null);
    }
    static long MacLastAt(JsonElement room)=>room.TryGetProperty("lastAt",out var at)&&at.TryGetInt64(out var value)?value:0;
    async Task MacNotifyChat(MessengerApi source,string id)
    {
        var rooms=await MacRooms(source);
        // Someone leaving or a room being made changes nothing to read; only a newer message from someone else does.
        var room=rooms.FirstOrDefault(r=>RowId(r)==id);
        if(room.ValueKind!=JsonValueKind.Object||room.GetProperty("unread").GetInt32()==0||MacLastAt(room)<=macChatSeen.GetValueOrDefault(id))
        {foreach(var known in rooms)macChatSeen[RowId(known)]=MacLastAt(known);await MacCountUnread(source,rooms);return;}
        if(MacMutedRooms().Contains(id)||MacReading(id)){macChatSeen[id]=MacLastAt(room);await MacCountUnread(source,rooms);return;}
        await MacNotifyUnread(source,"chat",id,rooms);
    }
    async Task MacNotifySurvey(MessengerApi source,string id)
    {
        var me=MacUserKey;var survey=(await source.Get("api/surveys")).EnumerateArray().FirstOrDefault(s=>RowId(s)==id);
        if(survey.ValueKind!=JsonValueKind.Object)return;
        // Anonymous answers leave the server for the owner's PC as soon as they arrive.
        if(Flag(survey,"anonymous")&&Field(survey,"ownerId")==me&&desktopArchive?.Owner==source)await desktopArchive.KeepBallots(await source.Get($"api/surveys/{id}/results"),id);
        if(macSurveyChanged is{} changed)await changed(id);
        if(Flag(survey,"target")&&!Flag(survey,"answered")&&!Flag(survey,"closed")&&Field(survey,"ownerId")!=me&&macKnownSurveys.Add(id))MacNotify("새 설문이 도착했습니다.","눌러서 확인하세요.","survey");
    }
    // Several changed days arrive together when a weekly file goes up, so they share one banner.
    async Task MacNotifyTimetable(MessengerApi source)
    {
        await RefreshDesktopTimetable(source);
        if(!desktopMenu["timetable"].IsVisible)return;
        JsonElement notices;
        try{notices=await source.Get("api/timetable/notices");}catch(MessengerApiRefusal e)when(e.Status==403){return;}
        var fresh=notices.EnumerateArray().Where(n=>macKnownTimetables.Add(n.GetProperty("revision").GetInt64())&&n.GetProperty("readAt").ValueKind!=JsonValueKind.Number)
            .OrderBy(n=>n.GetProperty("date").GetString(),StringComparer.Ordinal).ToArray();
        if(fresh.Length>0)MacNotify(fresh.Length==1?"시간표가 변경되었습니다":$"시간표 변경 {fresh.Length}건",fresh[0].GetProperty("title").GetString()??"","timetable",fresh[0].GetProperty("date").GetString());
    }
    // Swap requests the timetable manager has not looked at: a banner when one arrives and every five minutes after,
    // like unread mail, until the requests page has shown them.
    static readonly TimeSpan MacSwapReminder=TimeSpan.FromMinutes(5);
    HashSet<string> MacSwapsSeen()=>MacList("mac-swaps-seen-"+MacUserKey);
    internal void MarkMacSwapsSeen(IEnumerable<string> shown,IEnumerable<string> pending)
    {
        var seen=MacSwapsSeen();seen.UnionWith(shown);var waiting=pending.ToHashSet();seen.RemoveWhere(id=>!waiting.Contains(id));
        SaveMacList("mac-swaps-seen-"+MacUserKey,seen);
    }
    async Task MacRemindSwaps(MessengerApi source)
    {
        if(!desktopMenu["timetable"].IsVisible)return;
        if(macSwapManager is null||DateTime.UtcNow-macSwapRoleAt>MacSwapReminder){macSwapManager=(await source.Get("api/timetable/setup")).GetProperty("canManage").GetBoolean();macSwapRoleAt=DateTime.UtcNow;}
        if(macSwapManager!=true||desktopOwner!=source)return;
        var me=MacUserKey;var seen=MacSwapsSeen();
        var waiting=(await source.Get("api/timetable/swaps")).Deserialize(MobileJson.Default.TimetableSwapArray)!
            .Where(s=>s.Status is "pending" or "review"&&s.Owner!=me&&!seen.Contains(s.Id)).OrderBy(s=>s.CreatedAt).ToArray();
        if(waiting.Length==0||desktopOwner!=source)return;
        var fresh=false;foreach(var swap in waiting)fresh|=macSwapsNoticed.Add(swap.Id);
        if(!fresh&&DateTime.UtcNow-macSwapNoticeAt<MacSwapReminder)return;
        macSwapNoticeAt=DateTime.UtcNow;var first=waiting[0];
        var where=first.Changes.Length>0?$" · {first.Changes[0].Position.Date} {first.Changes[0].Before.ClassName} {first.Changes[0].Position.Period}교시":"";
        MacNotify(waiting.Length==1?"수업 교체 신청을 확인하세요":$"확인하지 않은 수업 교체 신청 {waiting.Length}건",first.OwnerName+where,"swaps");
    }
    // A banner opens its place. A message being written stays open; the banner is only a reminder then.
    async Task OpenMacNotification(string target,string? room)
    {
        if(api is not{} source||desktopOwner!=source)return;
        if(composer is not null&&Navigation.NavigationStack.Contains(composer))return;
        while(Navigation.NavigationStack.Count>1)await Navigation.PopAsync(false);
        await Run(async()=>
        {
            if(target=="chat"&&MacFeature("chat")){SelectRail("chat");await MacChat(room);}
            else if(target=="survey"&&MacFeature("survey")){SelectRail("survey");await MacSurveys();}
            else if(target=="timetable"&&desktopMenu["timetable"].IsVisible)await Timetable(room);
            else if(target=="swaps"&&desktopMenu["timetable"].IsVisible){var setup=await source.Get("api/timetable/setup");await MacSwapQueue(source,setup,setup.GetProperty("canManage").GetBoolean());}
            else{homeTab="notices";view="unread";offset=0;await Home();}
        });
    }
}
