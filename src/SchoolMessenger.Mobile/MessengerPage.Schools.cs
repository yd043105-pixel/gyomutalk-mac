namespace SchoolMessenger.Mobile;

public sealed partial class MessengerPage
{
    const string CertificateRefused="연결 확인 코드가 달라 접속하지 않았습니다. 학교 관리자에게 알려 주세요.";
    // A school server from the administrator's simple setup has its own certificate. On the first connection the person
    // compares the connection code with the one shown on the server PC; once confirmed, the action is retried.
    async Task ConfirmingCertificate(string address,Func<Task> action)
    {
        try{await action();return;}
        catch(Exception)when(CertificatePins.Pending(address) is not null){}
        var fingerprint=CertificatePins.Pending(address)!;var owner=Navigation.NavigationStack.LastOrDefault()??this;
        if(!await owner.DisplayAlertAsync("학교 서버 확인","이 학교 서버에 처음 연결합니다.\n\n연결 확인 코드: "+CertificatePins.Code(fingerprint)+"\n\n서버 PC의 관리자 프로그램(또는 학교 관리자 안내)에 표시된 코드와 똑같을 때만 '예'를 누르세요. 다르면 '아니요'를 누르고 관리자에게 알려 주세요.","예","아니요"))
            throw new InvalidOperationException(CertificateRefused);
        CertificatePins.Trust(address,fingerprint);await action();
    }
    // There is no central school list: the address comes from the administrator program of the school's own server.
    static void RequireServerAddress(string? address)
    {
        if(string.IsNullOrWhiteSpace(address))throw new InvalidOperationException("학교 서버 주소를 입력하세요. 관리자 프로그램에 표시된 접속 주소를 학교 관리자에게 받으세요.");
    }
}
