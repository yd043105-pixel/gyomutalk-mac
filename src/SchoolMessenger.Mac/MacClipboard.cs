using UIKit;

namespace SchoolMessenger.Mobile;

// What Ctrl+V gives a Windows chat: a copied picture becomes a PNG file, copied files keep their paths.
static class MacClipboard
{
    public static string[] Take()
    {
        var board=UIPasteboard.General;var paths=new List<string>();
        if(board.HasUrls)
            foreach(var url in board.Urls??[])
                if(url.IsFileUrl&&url.Path is{} path&&(File.Exists(path)||Directory.Exists(path)))paths.Add(path);
        if(paths.Count==0&&board.HasImages&&board.Image is{} image)
        {
            var folder=Path.Combine(FileSystem.CacheDirectory,"yy-paste");Directory.CreateDirectory(folder);
            // Pictures pasted more than a day ago are not needed any more.
            foreach(var old in Directory.EnumerateFiles(folder))if(File.GetLastWriteTimeUtc(old)<DateTime.UtcNow.AddDays(-1))try{File.Delete(old);}catch(IOException){}
            var file=Path.Combine(folder,"붙여넣은 사진 "+DateTime.Now.ToString("yyyyMMdd-HHmmss",System.Globalization.CultureInfo.InvariantCulture)+".png");
            using var data=image.AsPNG();if(data is not null){File.WriteAllBytes(file,data.ToArray());paths.Add(file);}
        }
        return paths.ToArray();
    }
}
