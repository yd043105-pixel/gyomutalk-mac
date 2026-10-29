# Mac 배포 후보: Developer ID 서명과 Apple 공증

기존 시험본(`gyomutalk-mac-trial`, ad-hoc 서명)은 그대로 둡니다. 배포 후보는 **이미 검증을 통과한 시험본 ZIP을 다시 서명**해서 만듭니다. 앱 내용은 시험본과 같고, 서명·프로파일·공증만 바뀝니다. 이 과정은 GitHub Release를 만들거나 수정하지 않습니다.

```
verify.yml (기존 시험본·실제 실행 검사)
   └─ gyomutalk-mac-trial ZIP (arm64, x64)
        └─ mac-distribution.py sign      Developer ID + 하드닝 런타임 + 보안 타임스탬프 + 프로파일
             └─ mac-distribution.py notarize   Apple 공증 → staple → ZIP + .sha256
                  └─ mac-distribution.py verify   Gatekeeper 판정 · 서명 체인 · 실행 확인 → JSON 보고서
```

## 실행 방법

GitHub → Actions → **Mac distribution candidate** → Run workflow.

| mode | 필요한 것 | 결과 아티팩트 | 배포 가능 여부 |
| --- | --- | --- | --- |
| `rehearsal` (기본) | 없음 | `gyomutalk-mac-signing-rehearsal` (3일 보관) | 불가. 1일짜리 자체 서명 인증서로 같은 키체인·서명·검증 경로를 점검합니다. 파일 이름에 `rehearsal-not-for-distribution`이 붙습니다. |
| `notarize` | 아래 secrets | `gyomutalk-mac-distribution-candidate` (14일 보관) | 배포 후보. 공증·staple·Gatekeeper 판정을 통과한 ZIP만 남습니다. |

배포 후보 아티팩트에는 `gyomutalk-mac-<버전>-macos-arm64.zip`, `…-x64.zip`, 각 `.sha256`, `verification-<arch>.json`, `*-notary-log.json`, 서명에 쓴 `entitlements-<arch>.plist`가 들어 있습니다.

나중에 정식 릴리즈를 공개하면 `release.yml`이 같은 워크플로를 `notarize`로 호출하고, 검증을 다시 통과한 후보만 Release에 올린 뒤 업데이트 피드를 서명합니다. 이번 작업으로 Release를 만들지 않았습니다.

## GitHub 설정 (한 번만)

### 1. Environment 만들기

Settings → Environments → **New environment** → 이름 `mac-distribution`.

- **Required reviewers**: 본인 계정을 지정합니다. 공증 작업이 시작되기 전에 승인을 요구합니다.
- **Deployment branches and tags**: `main`과 릴리즈 태그만 허용합니다.
- 아래 secrets는 저장소 secrets가 아니라 **이 Environment의 secrets**에 넣습니다. 그러면 `notarize` 작업 외의 워크플로(시험본, 리허설, PR)는 값을 읽을 수 없습니다.

### 2. Variable

Settings → Secrets and variables → Actions → **Variables** (저장소 수준)

| 이름 | 값 |
| --- | --- |
| `MAC_TEAM_ID` | Apple Team ID 10자리 (예: `AB12CD34EF`). 서명에 공개적으로 기록되는 값이라 secret이 아닙니다. `release.yml`의 검증에서도 씁니다. |

### 3. Environment secrets (`mac-distribution`)

| 이름 | 내용 | 만드는 방법 |
| --- | --- | --- |
| `MAC_DEVELOPER_ID_P12_BASE64` | Developer ID Application 인증서 + 개인키 `.p12`의 Base64 | 아래 “CI용 .p12 만들기” |
| `MAC_DEVELOPER_ID_P12_PASSWORD` | 위 `.p12`의 내보내기 암호 | 비밀번호 관리자에서 생성한 긴 무작위 값 |
| `MAC_PROVISIONING_PROFILE_BASE64` | Developer ID · **Mac Catalyst** 프로파일(App ID `kr.school.kyomutalk.mac`)의 Base64 | Apple Developer → Profiles → Developer ID → Mac Catalyst |
| `APPLE_NOTARY_KEY_P8_BASE64` | App Store Connect API 키 `.p8`의 Base64 | App Store Connect → Users and Access → Integrations → Team Keys, 역할 **Developer** |
| `APPLE_NOTARY_KEY_ID` | 위 키의 Key ID | 같은 화면 |
| `APPLE_NOTARY_ISSUER_ID` | Issuer ID | 같은 화면 상단 |

Base64 값은 Mac 터미널에서 클립보드로 바로 복사하고, 파일로 남기지 않습니다.

```bash
base64 -i developer-id.p12 | pbcopy
```

CI는 Apple ID 비밀번호나 앱 전용 암호를 쓰지 않습니다. API 키는 공증만 할 수 있고, 유출되면 App Store Connect에서 즉시 폐기(Revoke)할 수 있습니다.

## CI에서 비밀값을 다루는 방식

- `mac-distribution` Environment의 `notarize` 작업만 secrets를 받습니다. 시작할 때 빠진 이름을 모두 알려주고 멈춥니다.
- 비밀값은 `$RUNNER_TEMP/gyomutalk-secrets`(권한 700, umask 077)에만 풀고, 저장소 폴더에는 쓰지 않습니다. `.gitignore`도 `*.p12`, `*.p8`, `*.cer`, `*.provisionprofile`, `*.keychain-db`를 막습니다.
- `.p12`는 무작위 암호로 만든 **임시 키체인**(`Import-MacSigningKeychain.sh`)에 넣고 원본 파일은 바로 지웁니다. 가져온 인증서가 `Developer ID Application: … (MAC_TEAM_ID)`가 아니면 멈춥니다.
- 성공·실패와 관계없이 마지막 단계(`Remove-MacSigningKeychain.sh`)가 임시 키체인과 풀어 둔 파일을 모두 지웁니다.
- 서명 도구는 프로파일이 저장소 밖에 있을 때만 사용하고, 다음을 확인한 뒤 서명합니다: Team ID, App ID, Developer ID 프로파일 여부, 만료일(7일 이상 남음), 서명 인증서가 프로파일에 포함되어 있는지, 키체인 그룹 허용 여부.

## 로컬 Mac에서 하는 일 (Keychain 절차)

로컬에서는 임시 키체인을 만들지 않습니다. 개인키는 **로그인 키체인에만** 두고, CI용 `.p12`는 내보낸 직후 secret에 넣고 지웁니다.

### 인증서와 개인키 만들기

1. 키체인 접근 → 인증서 지원 → 인증 기관에서 인증서 요청 → **디스크에 저장**.
2. Apple Developer → Certificates → **Developer ID Application** (G2 Sub-CA) → 위 요청 파일 업로드 → `.cer` 내려받기 → 더블클릭해 로그인 키체인에 설치.
3. 확인:

   ```bash
   security find-identity -v -p codesigning
   ```

   `Developer ID Application: 이름 (TEAMID)`이 보여야 합니다.

### CI용 .p12 만들기 (필요할 때만)

1. 키체인 접근 → 내 인증서 → 위 인증서를 펼쳐 **인증서와 개인키를 함께** 선택 → 내보내기 → `.p12`. 암호는 비밀번호 관리자에서 생성합니다.
2. `base64 -i developer-id.p12 | pbcopy`로 `MAC_DEVELOPER_ID_P12_BASE64`에 붙여 넣고, 암호는 `MAC_DEVELOPER_ID_P12_PASSWORD`에 넣습니다.
3. `.p12` 파일을 휴지통 없이 지웁니다: `rm -P developer-id.p12`.

### 공증 자격 증명을 키체인에 저장

```bash
xcrun notarytool store-credentials gyomutalk-notary \
  --key ~/Downloads/AuthKey_XXXXXXXXXX.p8 --key-id XXXXXXXXXX --issuer xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
rm -P ~/Downloads/AuthKey_XXXXXXXXXX.p8   # 키체인에 저장한 뒤 원본은 지웁니다 (secret용 Base64가 필요하면 먼저 복사)
```

### 로컬에서 배포 후보 만들기

시험본 ZIP은 GitHub Actions의 `gyomutalk-mac-trial` 아티팩트를 받거나 `bash scripts/Build-Mac.sh`로 만듭니다. 프로파일은 저장소 밖(예: `~/Library/MobileDevice/Provisioning Profiles/`)에 둡니다.

```bash
export MAC_SIGN_IDENTITY="Developer ID Application: 이름 (TEAMID)"
export MAC_TEAM_ID=TEAMID
export MAC_PROVISIONING_PROFILE="$HOME/Library/MobileDevice/Provisioning Profiles/gyomutalk-mac-developer-id.provisionprofile"
export NOTARY_KEYCHAIN_PROFILE=gyomutalk-notary
version=1.0.0
for arch in arm64 x64; do
  python3 scripts/mac-distribution.py sign "artifacts/mac/gyomutalk-mac-$version-macos-$arch.zip" "$arch" artifacts/mac-signed
  python3 scripts/mac-distribution.py notarize artifacts/mac-signed/$arch/*.app "$arch" artifacts/mac-candidate
  python3 scripts/mac-distribution.py verify "artifacts/mac-candidate/gyomutalk-mac-$version-macos-$arch.zip" "$arch" --launch
done
```

`codesign`이 키체인 접근을 물으면 **항상 허용**이 아니라 **허용**을 선택합니다.

## 검증 항목 (`verify`)

- 시험본과 같은 번들 ID·버전·아키텍처·HTTPS 정책·샌드박스(`tests/mac-bundle.py`)
- 앱과 중첩된 모든 실행 코드(.NET 라이브러리, Sparkle XPC·Autoupdate·Updater.app, 업데이트 플러그인)의 하드닝 런타임
- Developer ID Application 인증서 체인, `MAC_TEAM_ID`, Apple 보안 타임스탬프
- 권한: `allow-jit` 있음, `get-task-allow` 없음, 라이브러리 검증 해제 없음, 키체인 그룹 `TEAMID.kr.school.kyomutalk.mac`, 프로파일 포함
- `stapler validate`, `spctl --assess --type execute` → `source=Notarized Developer ID`
- 실행 중인 러너와 같은 아키텍처는 서명된 앱을 20초간 실제 실행

리허설은 Apple 인증서·공증 항목을 건너뛰고, 자체 서명용 표시(라이브러리 검증 해제, 키체인 그룹 없음)가 들어 있는지 확인해 배포본으로 오인되지 않게 합니다.

## 권한 파일

`Platforms/MacCatalyst/Entitlements.Distribution.plist`가 배포용 권한의 원본입니다. 시험본용 `Entitlements.plist`와 달리 `.NET` JIT(`com.apple.security.cs.allow-jit`)와 키체인 그룹이 들어 있습니다. Sparkle 샌드박스 설치 서비스를 위한 `mach-lookup` 예외는 두 파일에 모두 있습니다. `$(AppIdentifierPrefix)`는 서명할 때 Team ID로 바뀝니다.

## 교체와 유출 대응

| 상황 | 할 일 |
| --- | --- |
| 프로파일 만료 7일 전 | Apple Developer에서 재생성 → `MAC_PROVISIONING_PROFILE_BASE64` 교체 (만료 임박 시 서명이 자동으로 멈춥니다) |
| `.p12` 또는 암호 유출 의심 | Apple Developer에서 해당 인증서 폐기(Revoke) → 새 인증서·프로파일 발급 → 두 secrets 교체. 이미 공증된 이전 배포본은 Apple 정책에 따라 실행이 막힐 수 있습니다. |
| API 키 유출 의심 | App Store Connect에서 키 Revoke → 새 키로 세 secrets 교체, 로컬은 `store-credentials` 다시 실행 |
| 담당자 변경 | Environment reviewers와 Apple Developer 역할을 함께 바꿉니다. |

Windows 설치파일의 Authenticode 서명과 휴대폰 앱 배포는 이 문서의 범위가 아닙니다.
