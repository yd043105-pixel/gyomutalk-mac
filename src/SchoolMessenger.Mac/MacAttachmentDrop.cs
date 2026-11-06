using Foundation;
using UIKit;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Mobile;

sealed class MacAttachmentDrop(Func<bool> available,Func<NSItemProvider[],Task> receive):UIDropInteractionDelegate
{
    const string FileUrl="public.file-url", Folder="public.folder";
    public static void Connect(VisualElement target,Func<bool> available,Func<NSItemProvider[],Task> receive)
    {
        var receiver=new MacAttachmentDrop(available,receive);
        UIView? view=null;UIDropInteraction? interaction=null;
        void Attach()
        {
            if(view is not null&&interaction is not null)view.RemoveInteraction(interaction);
            interaction?.Dispose();interaction=null;
            view=target.Handler?.PlatformView as UIView;
            if(view is null)return;
            interaction=new UIDropInteraction(receiver);view.AddInteraction(interaction);
        }
        target.HandlerChanged+=(_,_)=>Attach();Attach();
    }
    static bool Supported(NSItemProvider provider)=>provider.HasItemConformingTo(FileUrl)||provider.HasItemConformingTo(Folder);
    public override bool CanHandleSession(UIDropInteraction interaction,IUIDropSession session)=>available()&&session.Items.Any(i=>Supported(i.ItemProvider));
    public override UIDropProposal SessionDidUpdate(UIDropInteraction interaction,IUIDropSession session)=>
        new(CanHandleSession(interaction,session)?UIDropOperation.Copy:UIDropOperation.Cancel);
    public override void PerformDrop(UIDropInteraction interaction,IUIDropSession session)
    {
        if(available())_=receive(session.Items.Select(i=>i.ItemProvider).Where(Supported).Take(11).ToArray());
    }
    public static bool HasFileUrl(NSItemProvider provider)=>provider.HasItemConformingTo(FileUrl);
    public sealed record Item(NSUrl? Url,bool Scoped,FolderAttachment? Archive):IDisposable
    {
        public string Path=>Archive?.Path??Url!.Path!;
        public void Dispose(){Archive?.Dispose();if(Scoped)Url!.StopAccessingSecurityScopedResource();}
    }
    public static async Task<Item> Read(NSItemProvider provider,long limit)
    {
        if(!HasFileUrl(provider))return new(null,false,await ReadFolder(provider,limit));
        var (url,scoped)=await ReadUrl(provider);return new(url,scoped,null);
    }
    static Task<(NSUrl Url,bool Scoped)> ReadUrl(NSItemProvider provider)
    {
        var result=new TaskCompletionSource<(NSUrl,bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.LoadItem(FileUrl,null,(item,error)=>
        {
            try
            {
                var url=item as NSUrl;
                if(url is null&&item is NSData data&&data.Length<=65536)url=NSUrl.CreateWithDataRepresentation(data,null);
                if(error is not null||url is not {IsFileUrl:true,Path:not null})
                    throw new IOException(error?.LocalizedDescription??"Finder에서 선택한 파일이나 폴더를 읽을 수 없습니다.");
                result.TrySetResult((url,url.StartAccessingSecurityScopedResource()));
            }
            catch(Exception failure){result.TrySetException(failure);}
        });
        return result.Task;
    }
    static Task<FolderAttachment> ReadFolder(NSItemProvider provider,long limit)
    {
        var result=new TaskCompletionSource<FolderAttachment>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.LoadInPlaceFileRepresentation(Folder,(url,_,error)=>
        {
            if(error is not null||url?.Path is not { } path){result.TrySetException(new IOException(error?.LocalizedDescription??"Finder 폴더를 읽을 수 없습니다."));return;}
            var scoped=url.StartAccessingSecurityScopedResource();
            try{result.TrySetResult(FolderAttachment.Create(path,limit));}
            catch(Exception failure){result.TrySetException(failure);}
            finally{if(scoped)url.StopAccessingSecurityScopedResource();}
        });
        return result.Task;
    }
}
