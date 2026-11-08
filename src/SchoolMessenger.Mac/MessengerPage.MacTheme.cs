using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace SchoolMessenger.Mobile;

// "A · 맑은 청" tokens, icons, initial avatars and empty-state art, matching the Windows Theme.xaml and Avatar.cs.
// Mac Catalyst runs in the iPad idiom (77% scale), so Windows pixel sizes are multiplied by about 1.25 here.
public sealed partial class MessengerPage
{
    static readonly Color Canvas=Color.FromArgb("#F4F7FB"),Surface=Colors.White,Faint=Color.FromArgb("#8794A6"),Subtle=Color.FromArgb("#F7F9FC"),
        Danger=Color.FromArgb("#DC2626"),Success=Color.FromArgb("#16A34A"),Warning=Color.FromArgb("#B45309"),ChangeTint=Color.FromArgb("#FFF3B0"),ChangeInk=Color.FromArgb("#6B5200");
    const double Radius=10,RailWidth=90;
    static readonly (Color Back,Color Ink)[] AvatarTints=
    [
        (Color.FromArgb("#DCE8FF"),Color.FromArgb("#1E4FC2")),(Color.FromArgb("#D7F2EC"),Color.FromArgb("#0F7A62")),(Color.FromArgb("#FDE9CF"),Color.FromArgb("#9A5B05")),(Color.FromArgb("#F7DDE2"),Color.FromArgb("#B0304A")),
        (Color.FromArgb("#E9E1FB"),Color.FromArgb("#5B3CB8")),(Color.FromArgb("#E3EEF7"),Color.FromArgb("#2C5F86")),(Color.FromArgb("#E6F3D9"),Color.FromArgb("#3F7417")),(Color.FromArgb("#ECEFF3"),Color.FromArgb("#47566B"))
    ];
    // Same slot as Windows for the same seed (string.GetHashCode is randomized per process).
    static int AvatarSlot(string seed){var sum=0;foreach(var c in seed)sum=sum*31+c;return (int)((uint)sum%(uint)AvatarTints.Length);}
    static ShapePath Glyph(string key,double size,Color color)=>new()
    {
        Data=(Geometry)new PathGeometryConverter().ConvertFromInvariantString(MacIcons.Paths[key])!,Stroke=new SolidColorBrush(color),StrokeThickness=1.75,
        StrokeLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Aspect=Stretch.Uniform,WidthRequest=size,HeightRequest=size,
        HorizontalOptions=LayoutOptions.Center,VerticalOptions=LayoutOptions.Center,InputTransparent=true
    };
    static Border Avatar(string name,double size,string? seed=null)
    {
        var tint=AvatarTints[AvatarSlot(string.IsNullOrWhiteSpace(seed)?name:seed)];
        var letter=new Label{Text=name.Trim().Length>0?name.Trim()[..1]:"?",FontSize=Math.Round(size*.4),FontAttributes=FontAttributes.Bold,TextColor=tint.Ink,HorizontalOptions=LayoutOptions.Center,VerticalOptions=LayoutOptions.Center};
        return new Border{WidthRequest=size,HeightRequest=size,StrokeThickness=0,BackgroundColor=tint.Back,StrokeShape=new Ellipse(),Content=letter,VerticalOptions=LayoutOptions.Center,InputTransparent=true};
    }
    // Online green, away amber, phone-only blue, offline a white dot with a grey ring.
    static Ellipse PresenceDot(bool online,bool away,bool mobile=false)
    {
        var fill=!online?(mobile?Color.FromArgb("#3B82F6"):Colors.White):away?Color.FromArgb("#F59E0B"):Success;
        return new Ellipse{WidthRequest=12,HeightRequest=12,Fill=new SolidColorBrush(fill),Stroke=new SolidColorBrush(online||mobile?Colors.White:Color.FromArgb("#94A3B8")),StrokeThickness=online||mobile?2:1.5,HorizontalOptions=LayoutOptions.End,VerticalOptions=LayoutOptions.End};
    }
    static Grid AvatarWithPresence(string name,double size,string? seed,bool online,bool away,bool mobile,string label)
    {
        var mark=new Grid{WidthRequest=size,HeightRequest=size,VerticalOptions=LayoutOptions.Center};mark.Add(Avatar(name,size,seed));mark.Add(PresenceDot(online,away,mobile));
        ToolTipProperties.SetText(mark,label);SemanticProperties.SetDescription(mark,label);return mark;
    }
    // Two stacked cards with the section's icon: the picture above every empty list.
    static Grid EmptyArt(string icon)
    {
        var art=new Grid{WidthRequest=115,HeightRequest=88,HorizontalOptions=LayoutOptions.Center,Margin=new Thickness(0,0,0,18),InputTransparent=true};
        art.Add(new Border{WidthRequest=78,HeightRequest=60,BackgroundColor=Pale,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=17},HorizontalOptions=LayoutOptions.Start,VerticalOptions=LayoutOptions.Start});
        art.Add(new Border{WidthRequest=78,HeightRequest=60,BackgroundColor=Surface,Stroke=Blue,StrokeThickness=1.5,StrokeShape=new RoundRectangle{CornerRadius=17},HorizontalOptions=LayoutOptions.End,VerticalOptions=LayoutOptions.End,Content=Glyph(icon,30,Blue)});
        return art;
    }
    static string EmptyIcon(string title)=>title.Contains("검색")||title.Contains("일치")?"Search":title.Contains("시간표")||title.Contains("수업")?"Timetable":title.Contains("메시지")?"Mail":
        title.Contains("할 일")||title.Contains("제출")?"File":title.Contains("교직원")?"Tree":title.Contains("특별실")||title.Contains("일정")?"Calendar":"Folder";
    // Search field with a leading magnifier on the soft canvas colour, as in the Windows roster and mailbox.
    static Border SearchField(Entry entry)
    {
        entry.BackgroundColor=Colors.Transparent;entry.TextColor=Navy;entry.PlaceholderColor=Faint;entry.VerticalOptions=LayoutOptions.Center;
        var row=new Grid{ColumnDefinitions={new(GridLength.Auto),new(GridLength.Star)},ColumnSpacing=6};row.Add(Glyph("Search",18,Faint));row.Add(entry,1);
        return new Border{Content=row,Padding=new Thickness(12,0,8,0),BackgroundColor=Canvas,StrokeThickness=0,StrokeShape=new RoundRectangle{CornerRadius=Radius}};
    }
    // Filter chip: white with a line border, filled accent when selected.
    static void Chip(Button button,bool selected)
    {
        button.FontSize=14;button.FontAttributes=FontAttributes.Bold;button.CornerRadius=17;button.Padding=new Thickness(14,4);button.MinimumHeightRequest=34;button.HeightRequest=34;button.BorderWidth=1;
        button.BackgroundColor=selected?Blue:Surface;button.BorderColor=selected?Blue:Line;button.TextColor=selected?Colors.White:Muted;
    }
    // A 1pt rule between stacked regions (header, list, footer).
    static BoxView Rule()=>new(){HeightRequest=1,Color=Line,VerticalOptions=LayoutOptions.Start,InputTransparent=true};
}
