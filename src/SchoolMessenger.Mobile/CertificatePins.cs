using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SchoolMessenger.Mobile;

// A school server made by the administrator program uses its own certificate. Trust it only after a person
// compares the connection code shown on the server PC; afterwards only that exact certificate is accepted.
// Same rule as the Windows client (SchoolMessenger.Desktop/CertificatePins.cs).
public static class CertificatePins
{
    static readonly object gate=new();
    static readonly Dictionary<string,string> pending=new();
    static string Key(Uri address)=>address.Scheme+"://"+address.Authority.ToLowerInvariant();
    static string Store(string key)=>"certificate-pin-"+key;
    static Uri? Parse(string address)=>Uri.TryCreate(address.TrimEnd('/')+"/",UriKind.Absolute,out var uri)?uri:null;
    public static string Fingerprint(X509Certificate certificate)=>Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    // 64 bits of the SHA-256 fingerprint, grouped for reading aloud.
    public static string Code(string fingerprint)=>string.Join("-",Enumerable.Range(0,4).Select(i=>fingerprint.Substring(i*4,4)));
    public static bool Validate(Uri address,X509Certificate? certificate,SslPolicyErrors errors)
    {
        if(errors==SslPolicyErrors.None)return true;
        return certificate is not null&&ValidateFingerprint(address,Fingerprint(certificate));
    }
    public static bool ValidateFingerprint(Uri address,string fingerprint)
    {
        lock(gate)
        {
            var pinned=Preferences.Default.Get(Store(Key(address)),"");
            if(pinned.Length>0)return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pinned),Convert.FromHexString(fingerprint));
            pending[Key(address)]=fingerprint;return false;
        }
    }
    public static string? Pending(string address)
    {
        if(Parse(address) is not {} uri)return null;
        lock(gate){if(Preferences.Default.ContainsKey(Store(Key(uri))))return null;return pending.TryGetValue(Key(uri),out var value)?value:null;}
    }
    public static void Trust(string address,string fingerprint)
    {
        var uri=Parse(address)??throw new InvalidOperationException("학교 서버 주소를 확인하세요.");
        lock(gate){Preferences.Default.Set(Store(Key(uri)),fingerprint);pending.Remove(Key(uri));}
    }
    internal static void Forget(string address)
    {
        if(Parse(address) is not {} uri)return;
        lock(gate){Preferences.Default.Remove(Store(Key(uri)));pending.Remove(Key(uri));}
    }
    public static Func<HttpRequestMessage,X509Certificate2?,X509Chain?,SslPolicyErrors,bool> Callback(Uri address)=>(_,certificate,_,errors)=>Validate(address,certificate,errors);
}
