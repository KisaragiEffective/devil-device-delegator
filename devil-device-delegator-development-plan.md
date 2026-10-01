# Devil Device Delegator (DDD) 開発計画書

> **文書状態**: v1 実装計画ドラフト  
> **更新日**: 2026-10-01  
> **対象**: `devil-device-delegator`  
> **目的**: 借り物・一時利用・十分に信用できない Windows PC から、自分の iPhone に保持した長期 Git 署名鍵を使って Git commit を安全に SSH 署名する。  
> **最上位原則**: **Less code is less stress.**

---

## 0. この文書の位置づけ

この文書は、DDD の実装に必要な情報を一か所へ集約した自己完結的な計画書である。過去の検討で候補だった Bonjour、永続 pairing、自前 SSHSIG core なども整理し、**現在の方針を優先して記述**する。

状態表記は次の意味とする。

| 状態 | 意味 |
|---|---|
| **[決定]** | v1 の前提として実装を進めてよい |
| **[暫定]** | 現在の第一候補。PoC / 実機確認で変更し得る |
| **[未決]** | 実装前または実装中に判断が必要 |
| **[却下]** | 現在の目的では採用しない |
| **[将来]** | v1 には入れず、必要になったら再検討 |

---

# 1. 出発点

借り物・レンタル・一時利用の Windows PC で Git commit を作りたい。しかし、自分の長期 Git 署名秘密鍵をその PC に置きたくない。

次の方法は目的に反する。

- GPG private key を import する
- SSH signing private key をコピーする
- 一時ファイルとして private key を置いて後で削除する
- password 付き private key だから安全とみなす
- QR / PNG / clipboard などで private key を運ぶ

DDD では PC を「署名器」ではなく「署名要求の生成・中継器」とする。

```text
borrowed Windows
    Git commit payload
          │
          ▼
   DDD Windows awaiter
          │
          │ authenticated / encrypted LAN session
          ▼
       iPhone
  trusted confirmation UI
          │
          ▼
 Secure Enclave signing key
          │
          ▼
        SSHSIG
          │
          ▼
        Windows
          │
          ▼
          Git
```

長期 private key は iPhone 外へ出さない。

---

# 2. プロジェクト名

**[決定]** `devil-device-delegator`、略称 **DDD**。

意味は「信用しにくい device が、秘密を持つ trusted device に限定された操作を delegate する」。Domain-Driven Design の DDD と衝突するが許容する。

---

# 3. 設計原則

## 3.1 Less code is less stress

**[決定]**

コード量を減らすこと自体が絶対目的ではないが、次を避ける。

- 自作暗号
- 既存 library で十分な規格の再実装
- 小規模 protocol への早すぎる code generation
- 共通化のためだけの FFI
- 言語数削減のためだけの cross-platform abstraction
- 一画面アプリへの過剰な MVVM / Clean Architecture
- YAML / shell 内への application logic の流出
- 「将来必要そう」だけを理由にした module / project 分割

高品質な依存一つで大きな自作 surface を消せるなら、その依存は良い依存とみなす。

## 3.2 platform が望む言語を使う

**[決定]**

```text
Windows   C# / .NET
iPhone    Swift
```

Rust 等の共通 core を作って FFI を増やさない。共有するのは実装ではなく、protocol semantics、invariants、schema、test vectors とする。

## 3.3 textual duplication より knowledge duplication を避ける

C# と Swift に同じ wire model が手書きで存在してよい。

良い重複:

```text
C# SignRequest
Swift SignRequest
```

悪い重複:

- protocol version semantics が C# / Swift で別々に決められる
- security size limit が二重管理される
- domain separation / validation rule が実装ごとに違う

authoritative knowledge は `protocol/` に置く。

## 3.4 architecture を先払いしない

**[決定]**

必要になるまで次を作らない。

- custom code generator
- generated C# / Swift tree
- custom UI DSL
- multi-project Windows architecture
- iOS module の過剰分割
- generic SSH agent
- generic remote HSM protocol

---

# 4. 動作環境

## 4.1 借り物 Windows

**[決定] 想定条件**

- Windows PC
- Git for Windows 等で Git が使える
- display あり
- keyboard / mouse あり
- network あり
- **camera は不要**
- **Bluetooth device connection は不要**
- NFC は不要
- TPM / Windows Hello は不要
- PC は十分には信用しない
- 長期秘密情報を保存しない
- 一時 executable / zip を持ち込める想定

## 4.2 iPhone

**[決定] 想定能力**

- camera
- Wi-Fi / LAN connectivity
- Secure Enclave
- LocalAuthentication / Face ID 等
- native iOS app
- ユーザーが物理的に保持する trusted device

v1 は foreground app でよい。常時 background daemon は目標にしない。

## 4.3 開発機

**[決定]** Debian/Linux 中心。

Apple SDK が必要な工程だけ GitHub Actions の macOS runner に寄せる。macOS を日常の primary workstation にしない。

---

# 5. セキュリティ目標

## 5.1 長期 Git private key を Windows に置かない

**[決定 / 最重要]**

Git signing private key は iPhone Secure Enclave で生成・保持し、Windows へ export しない。

Windows に存在してよいもの:

- Git signing public key
- protocol data
- ephemeral TLS key / certificate
- ephemeral session token
- temporary request state
- returned SSHSIG

## 5.2 silent signing oracle にしない

**[決定]**

```text
authenticated transport
    ≠
authorized signature
```

iPhone は各 request の実際の署名対象を確認し、人間の明示承認なしに署名しない。

## 5.3 表示と署名対象を一致させる

**[決定]**

Windows が送る「表示用 summary」は信用しない。iPhone が **実際に署名する payload** から表示情報を導出する。

payload から導出する候補:

- commit message
- author
- committer
- tree
- parent(s)
- payload hash

repository 名、working directory、Windows device name 等は client-reported / untrusted metadata と区別する。

## 5.4 LAN は hostile byte pipe

**[決定]**

LAN / Wi-Fi AP / 同一 LAN 上の第三者を信用しない。sniffing、injection、replay、MITM、DoS、fake peer を想定する。

---

# 6. 脅威モデル

## 6.1 信用するもの

- ユーザー本人
- iPhone OS
- Secure Enclave
- DDD iOS app
- 利用する暗号 library / AgePonyCore の対象部分
- iPhone が raw payload から導出した情報
- iPhone 上の明示承認
- LocalAuthentication の結果

## 6.2 信用しないもの

- borrowed Windows
- Windows 上の Git process
- Windows 上の DDD process
- Windows が申告する metadata
- LAN / Wi-Fi AP
- LAN 上の他端末
- Windows filesystem で「後で削除した」こと

## 6.3 想定する攻撃

- Windows malware が勝手に sign request を出す
- Windows が表示 metadata を偽る
- LAN 上で payload / response を盗聴・改竄する
- replay request を投げる
- QR endpoint を推測する
- endpoint を発見した別 client が payload を取りに来る
- borrowed PC に credential が残り後日再利用される
- rebase 等で大量 request を連続提示してユーザーを儀式的承認に追い込む

## 6.4 DDD が保証しないこと

Windows 自体が完全に侵害されていれば、その PC は working tree や Git 操作を改竄できる。DDD の主保証は、**iPhone 上で明示確認された exact payload 以外へ長期鍵で勝手に署名させないこと**である。

---

# 7. Git integration

## 7.1 Git SSH signing を使う

**[決定]** GPG ではなく Git の SSH signing path を利用する。

概念設定:

```ini
[gpg]
    format = ssh

[gpg "ssh"]
    program = C:/path/to/ddd.exe

[user]
    signingkey = C:/path/to/ddd-signing-key.pub
```

実際の key 指定、argv、stdin/stdout 契約は Git / OpenSSH の実測に合わせる。

## 7.2 Windows awaiter は最小の `ssh-keygen -Y sign` 互換 surface を持つ

**[暫定]**

OpenSSH 全体は再実装しない。確認対象:

- exact argv
- stdin
- namespace
- public key / key path
- stdout format
- stderr
- exit status
- cancellation
- temporary file
- Windows quoting
- `git commit -S`
- 必要なら `git tag -s`

## 7.3 generic ssh-agent は v1 で実装しない

**[決定]**

ssh-agent 化すると用途は広がるが、iPhone が見る情報が generic signing blob / hash へ縮退し、Git commit semantics を trustworthy に表示しにくくなる。v1 は Git-aware signing request を優先する。

## 7.4 batch signing API は作らない

**[決定]**

```text
signOne(request)
```

のみ。`signBatch([...])` は作らない。

10 commit を rebase で署名するなら 10 request / 10 explicit approval とする。

---

# 8. Git payload と確認 UI

## 8.1 raw payload を iPhone へ渡す

**[決定]**

Windows の summary ではなく、実際の signing payload を iPhone へ送る。

## 8.2 表示項目

**[暫定]**

優先:

- commit message / summary
- author
- committer
- parent count / IDs
- tree ID
- payload hash
- request/session 情報

補助:

- repository name
- working directory
- Windows display name

補助項目は client-reported と明示する。

## 8.3 1 commit = 1 explicit approval

**[決定]**

各 signing request ごとに iPhone 上の `Sign` 操作が必要。session が成立していても auto-sign しない。

## 8.4 LocalAuthentication 粒度

**[未決]**

最低条件:

```text
各 commit:
  内容表示
  ↓
  explicit Sign
```

候補 A: 各 commit で Face ID。  
候補 B: session 開始時に Face ID、短時間 unlock し、各 commit は Sign ボタンのみ。

B を有力候補とするが、実機 UX で決める。

---

# 9. Git signing key

## 9.1 Secure Enclave P-256

**[決定]**

Git signing key は Secure Enclave の non-exportable P-256 key とする。

SSH key type は概念上:

```text
ecdsa-sha2-nistp256
```

## 9.2 Ed25519 を使わない理由

Ed25519 自体を否定しない。DDD では Secure Enclave / non-exportability / SSH interoperability を優先するため v1 は P-256 を選ぶ。

## 9.3 ML-DSA 等

**[将来]**

Secure Enclave 側だけでなく SSH → SSHSIG → Git → hosting service まで interoperability が必要。v1 の block 要因にしない。

---

# 10. AgePonyCore

## 10.1 利用方針

**[決定寄り / 実装時 API 再確認]**

iOS 側で次を自作しないため AgePonyCore を利用する。

期待する責務:

- Secure Enclave key handling
- P-256 / ECDSA 周辺
- SSH public-key encoding
- SSH signature encoding
- SSHSIG
- OpenSSH interoperability に関する既存実装 / test knowledge

DDD 自身が持つ責務:

- Git-specific request semantics
- protocol transport
- trusted confirmation UI
- session bootstrap
- policy
- AgePonyCore との薄い glue

## 10.2 vendoring しない

**[暫定]** 最初は外部 Swift package dependency とする。fork / vendor は upstream の制約が実害になった場合のみ検討する。

---

# 11. Transport 全体像

## 11.1 v1 は LAN / TCP 系 transport

**[決定]**

Bluetooth は使わない。USB transport は v1 必須にしない。

hotel / guest Wi-Fi の client isolation 等で LAN が成立しない場合、iPhone Personal Hotspot を実用上の fallback とできる。

## 11.2 hardware asymmetry を利用する

```text
Windows:
  display      yes
  camera       no requirement
  Bluetooth    no requirement
  trusted      no

iPhone:
  display         yes
  camera          yes
  Wi-Fi           yes
  Secure Enclave  yes
  trusted         yes
```

bootstrap は Windows → iPhone の一方向 optical channel でよい。

```text
Windows displays QR
        ↓
iPhone scans QR
```

---

# 12. QR session bootstrap

## 12.1 Bonjour を v1 の必須要素にしない

**[決定]**

過去案では Bonjour/mDNS discovery を想定したが、現在の v1 は Windows が endpoint を QR に含める。

これで discovery protocol、mDNS dependency、multicast 制限、fake advertisement の surface を削る。

## 12.2 QR に入れる情報

**[暫定 / 第一候補]**

```text
protocol version
session id
Windows endpoint (IP + port)
ephemeral TLS certificate/public-key fingerprint
unguessable session token / nonce
optional display label
```

Git payload 本体は TLS で運ぶ。

## 12.3 payload 全体を QR に載せない理由

QR に exact payload を載せる案は LAN 改竄耐性が強いが、payload size、animated QR、framing、reassembly 等の実装が増える。

v1 は:

```text
QR:
  endpoint + identity + session secret

TLS:
  exact Git payload + SSHSIG response
```

を優先する。

QR payload mode は将来の hardening option とする。

## 12.4 encoding

**[暫定]** CBOR を第一候補とする。binary field と QR size に向くため。ただし CBOR 自体を目的化しない。

---

# 13. TLS / peer authentication

## 13.1 Windows が ephemeral TLS endpoint を作る

**[暫定 / 第一候補]**

signing request または短い session ごとに Windows が ephemeral TLS server identity を作る。

QR に endpoint、fingerprint、session token を表示する。

iPhone は scan 後その endpoint へ接続する。

## 13.2 QR pinning

**[決定]**

iPhone は接続先 TLS identity が QR の fingerprint と一致することを検証する。Web PKI / public CA は不要。

## 13.3 strict mTLS は v1 の必須要件にしない

**[暫定]**

persistent client certificate / CA / pairing DB を導入すると surface が増える。

責務を次のように分ける。

```text
QR session token:
  QR を見た peer だけが request を取得できることの bootstrap proof

TLS pinning:
  iPhone → Windows の接続先認証
  LAN 上の confidentiality / integrity

SSHSIG:
  iPhone / long-term Git signing identity → Windows の最終 signer proof
```

Windows は正しい single-use session token を提示した TLS peer のみに request を返す。返却署名は期待する Git signing public key で検証可能であること。

## 13.4 session token 要件

- 十分長い random secret
- single-use
- timeout / cancel / success で破棄
- log に平文で残さない
- command line に載せない

## 13.5 persistent pairing DB

**[決定] v1 では不要**

borrowed Windows が主対象のため session-scoped bootstrap を基本とする。自分の PC 向け persistent pairing は将来機能。

---

# 14. Wire protocol

## 14.1 最小 model

**[暫定]**

```text
SignRequest {
    version
    requestId
    namespace
    payload
    clientMetadata?
}

SignResponse {
    requestId
    sshsig
}

Error {
    requestId?
    code
    message?
}
```

## 14.2 requestId

用途:

- response correlation
- duplicate / retry 検知
- replay 抑止
- UI correlation
- log correlation

十分大きな random ID を使う。

## 14.3 namespace

Git SSH signing の namespace として `git` を扱う。exact contract は OpenSSH / Git 実測で固定する。

## 14.4 size limits

**[未決]** DoS / parser safety のため payload、metadata、frame 等に明示的上限を置く。実データ観測後に決定する。

## 14.5 connection / session model

候補:

```text
A. 1 bootstrap = 1 request
B. 1 bootstrap = short-lived session、request は逐次 1 件ずつ
```

必須条件:

- batch authorization なし
- request ごとに approval
- stale session を長時間残さない

---

# 15. Protocol specification と code generation

## 15.1 `protocol/` を authoritative source にする

**[決定]**

```text
protocol/
├─ SPEC.md
├─ protocol.cddl
└─ fixtures/
```

`SPEC.md`: semantics / invariants / state transition / security rules。  
`protocol.cddl`: wire representation / field types / optionality。  
`fixtures/`: binary golden vectors / valid-invalid examples。

## 15.2 v1 は code generation しない

**[決定]**

C# と Swift に model / codec を手書きする。

```csharp
// Less code is less stress.
// TODO: Reconsider generating this from the spec once this definition gets messy or bloated.
```

codegen を再検討するのは schema 自体が明確に肥大化した時点。

## 15.3 generator が必要になった場合

まず既存 generator を探す。custom generator は最後の手段。

---

# 16. Golden fixtures

## 16.1 C# / Swift 共通 fixture

**[決定]**

protocol 変更時:

```text
1. SPEC / CDDL を変更
2. fixture を追加
3. C# tests が失敗
4. Swift tests が失敗
5. 両実装を更新
```

この摩擦は security protocol では有益。

## 16.2 additive / versioned

例:

```text
sign-request-v1.cbor
sign-request-v2.cbor
```

原則として古い fixture を上書きしない。

## 16.3 安易な golden auto-update を作らない

implementation bug を新しい「正解」にする危険があるため、security-relevant fixture は review して commit する。

---

# 17. Windows application

## 17.1 言語

**[決定] C# / .NET**

Rust core 等を挟まない。

## 17.2 UI framework

**[決定] Avalonia UI**

理由:

- WinForms を使いたくない
- WebView / HTML UI を使いたくない
- C# で普通の desktop UI を書ける
- QR / status / cancel 程度の小さい UI に十分
- cross-platform capability は副次的メリット

## 17.3 AXAML 禁止

**[決定]**

```text
UI definitions MUST be written in C#.
Do not add .axaml files.
```

「XML 全般禁止」ではない。`.csproj` は MSBuild XML なので対象外。

## 17.4 custom C# UI DSL を作らない

AXAML の代わりに独自 DSL を構築しない。Avalonia の通常の C# API / object initializer を直接使う。

## 17.5 MVVM をやりすぎない

候補:

```text
MainWindow
   ↕
MainViewModel
   ↓
SigningSession
```

一画面なら code-behind も許容する。Service / Repository / UseCase 層を目的化しない。

---

# 18. Windows UI 要件

最低限:

- application/session state
- QR
- waiting state
- iPhone connected
- request transferred
- awaiting approval
- signature received
- success / failure
- cancel

例:

```text
Devil Device Delegator

Scan this QR with the iPhone app.

[ QR ]

Waiting for iPhone...

[ Cancel ]
```

---

# 19. `PairingQr` / bootstrap QR 実装

## 19.1 QR 規格を自作しない

**[決定寄り]** QRCoder 等の既存 C# library を使い、module matrix を得る。

```text
PairingDescriptor
    ↓
CBOR
    ↓
bytes
    ↓
QR encoder
    ↓
module matrix
    ↓
Avalonia DrawingContext
```

## 19.2 PNG を中間生成しない

避ける:

```text
QR modules
→ PNG encode
→ byte[]
→ PNG decode
→ Bitmap
→ render
```

優先:

```text
QR modules
→ Avalonia rectangles
```

## 19.3 描画要件

- integer module size を優先
- quiet zone を確保
- scaling で blur させない
- high-DPI でも module boundary を潰さない
- high contrast
- resize 時も正方形

## 19.4 ViewModel に `Bitmap` を持たせない

ViewModel / session layer は logical payload または module matrix まで。Avalonia image type を protocol/domain state に漏らさない。

---

# 20. Windows 内部構成

最初は production `.csproj` 一つ + test project 程度でよい。

```text
windows/
├─ Ddd.slnx
├─ src/
│  └─ Ddd.Windows/
│     ├─ Ddd.Windows.csproj
│     ├─ Git/
│     ├─ Protocol/
│     ├─ Transport/
│     ├─ Pairing/
│     ├─ Qr/
│     ├─ Views/
│     ├─ ViewModels/
│     └─ Program.cs
└─ tests/
   └─ Ddd.Windows.Tests/
      └─ Ddd.Windows.Tests.csproj
```

責務:

- `Git/`: Git/OpenSSH invocation adapter
- `Protocol/`: model / CBOR / validation
- `Transport/`: ephemeral TLS / timeout / lifecycle
- `Pairing/`: session descriptor / random token
- `Qr/`: QRCoder adapter / module matrix
- `Views/`, `ViewModels/`: Avalonia coded UI

---

# 21. iPhone application

## 21.1 言語 / UI

**[決定]** Swift + SwiftUI + native iOS app。

Web/PWA は使わない。

## 21.2 薄い app とする

AgePonyCore を使うため、過去案の厚い `app-core` / `app-ios-bridge` 分割を v1 から先払いしない。

初期候補:

```text
ios/
├─ Package.swift
├─ Sources/
│  └─ DddApp/
│     ├─ Protocol/
│     ├─ Transport/
│     ├─ Signing/
│     ├─ Views/
│     └─ DddApp.swift
└─ Tests/
```

必要になった時点で純 Swift 部分を `DddCore` へ抽出する。

## 21.3 iOS app の責務

- QR scan
- bootstrap descriptor decode
- TLS fingerprint pinning
- session token 提示
- SignRequest 受信
- Git payload parse
- trusted confirmation UI
- Sign / Reject
- LocalAuthentication
- AgePonyCore 呼び出し
- Secure Enclave signing
- SSHSIG response
- key fingerprint 表示
- cancel / error handling

---

# 22. iOS UI 状態

最低限:

```text
Idle
Scanning QR
Connecting
Request received
Review
Awaiting approval
Authenticating
Signing
Sending response
Success
Failure
```

approval screen は装飾より正確性を優先する。

---

# 23. Monorepo

## 23.1 方針

**[決定]** Windows、iOS、protocol、interoperability tests、CI を同一 repository に置く。

理由:

- protocol 変更を C# / Swift / fixture と同一 commit で更新できる
- release choreography が減る
- cross-language review が容易
- protocol drift を防ぎやすい

## 23.2 推奨構成

```text
devil-device-delegator/
├─ README.md
├─ LICENSE
├─ .gitignore
├─ .editorconfig
│
├─ protocol/
│  ├─ SPEC.md
│  ├─ protocol.cddl
│  └─ fixtures/
│     ├─ valid/
│     └─ invalid/
│
├─ windows/
│  ├─ Ddd.slnx
│  ├─ src/
│  │  └─ Ddd.Windows/
│  └─ tests/
│     └─ Ddd.Windows.Tests/
│
├─ ios/
│  ├─ Package.swift
│  ├─ Sources/
│  │  └─ DddApp/
│  └─ Tests/
│
├─ scripts/
│  └─ 必要になったものだけ
│
└─ .github/
   └─ workflows/
      ├─ windows.yml
      ├─ ios.yml
      └─ protocol.yml
```

空 directory を将来のためだけに作らない。

## 23.3 Git repo と IDE project は別物

```text
Git repository
    ≠ Visual Studio solution
    ≠ Swift package
```

Zed は repo root を workspace として開く。

---

# 24. 開発環境

## 24.1 primary editor

**[決定] Zed**

概念:

```text
windows/**   → Roslyn / C#
ios/**       → SourceKit-LSP / Swift
protocol/**  → text / CDDL
scripts/**   → shell / PowerShell
.github/**   → YAML
```

## 24.2 editor-independent commands

build / test は editor に依存させない。

```text
dotnet test windows/...
swift test --package-path ios/...
```

repo-level `scripts/check.sh` は必要なら作るが、巨大 wrapper にしない。

---

# 25. 言語構成

primary:

```text
C#       Windows application / tests
Swift    iPhone application / tests
```

supporting:

```text
CDDL        protocol schema
YAML        GitHub Actions
sh          Linux/macOS glue
PowerShell  Windows glue
TypeScript  GitHub API logic が十分複雑になった場合のみ
```

言語数自体を悪としない。新しい product language は明確な必要性が出るまで増やさない。

---

# 26. CI 方針

## 26.1 Windows CI

- restore
- build
- unit tests
- protocol fixture tests
- publish smoke test
- Git invocation compatibility tests

## 26.2 iOS CI

macOS runner で:

- Swift tests
- package resolution
- iOS build
- Apple-specific compile/link
- provisioning
- codesign
- signed IPA Artifact

## 26.3 protocol change

`protocol/**` が変わったら Windows / iOS 両方を走らせる。

```text
windows/** + protocol/**
ios/**     + protocol/**
```

README だけの変更で重い iOS build を回さない程度の最適化は可。

## 26.4 YAML に logic を書きすぎない

YAML は orchestration、shell / PowerShell は platform commands の glue に留める。glue が software になり始めたら適切な言語へ出す。

---

# 27. iOS build / provisioning / distribution

## 27.1 macOS dependency を CI に隔離

**[決定]**

```text
Debian/Linux
  edit / test portable parts
      ↓ push
GitHub Actions macOS
  Xcode / iOS SDK
  build
  provision
  codesign
  signed IPA
```

## 27.2 Personal Team / short-lived provisioning

**[暫定 / 実証必要]**

無料 Apple Account / Personal Team の短い development provisioning lifetime を前提に、fresh signed IPA を定期生成する。最新制約は実装時点で再確認する。

## 27.3 install

**[暫定]** Linux / Windows から `pymobiledevice3` を第一候補にする。

install tool が code signing / provisioning を bypass するものとは考えない。

---

# 28. daily freshness pipeline

## 28.1 GHA `schedule` を唯一の起動源にしない

**[決定]** transient cron tick より、未反映状態が残る trigger chain を好む。

## 28.2 external `daily-ref`

**[決定]** `daily-ref` は DDD monorepo 外。

```text
daily-ref
   ↓ new SHA
DDD が参照
   ↓
Renovate
   ↓
bump PR
   ↓
PR-triggered GHA macOS
   ↓
fresh signed IPA Artifact
```

重要なのは定刻ではなく、new state が残り、後から検出・retry できること。

---

# 29. Artifact policy

- signed IPA を repository に commit しない
- daily Release を乱造しない
- GitHub Actions Artifact を主経路にする
- retention は provisioning lifetime と運用を見て決める
- build date / commit / provisioning info を artifact metadata に出すことを検討する

---

# 30. Secure Enclave key persistence

## 30.1 P0 実機検証

次で signing identity がどうなるか確認する。

```text
A. app 上書き更新
B. provisioning 更新
C. IPA 再署名
D. app uninstall / reinstall
E. Bundle ID 維持
F. Team / Keychain access group 変更
```

望ましい invariant:

```text
routine IPA refresh
    ≠
Git signing key rotation
```

## 30.2 iPhone 故障

non-exportable key であることを受け入れる。端末故障時は新 key を作り hosting service に新 public key を登録する。既存 commit は old public key があれば検証可能。

---

# 31. 機能要件

## FR-001 Git commit SSH signing

Windows で `git commit -S` を行うと DDD が起動し、iPhone Secure Enclave key による有効な Git SSH signature を生成できること。

## FR-002 Public key export

iPhone の Git signing public key を OpenSSH / Git hosting で利用可能な形で表示・export できること。private key は export できないこと。

## FR-003 Session bootstrap QR

Windows が signing session ごとに QR を表示できること。QR から iPhone が protocol version、endpoint、ephemeral TLS fingerprint、session ID、unguessable token を取得できること。

## FR-004 QR scan

iPhone が Windows 上の QR を camera で scan して session を開始できること。

## FR-005 TLS pinning

iPhone が QR の fingerprint と実 peer の TLS identity を照合し、不一致なら request を取得しないこと。

## FR-006 Session token validation

Windows は正しい single-use token を提示した peer にのみ payload を渡すこと。

## FR-007 Raw payload transfer

実際の Git signing payload を iPhone へ送信できること。

## FR-008 Payload parsing

iPhone が exact payload から人間向け Git commit 情報を解析できること。

## FR-009 Explicit approval

各 signing request ごとに `Sign` / `Reject` を iPhone 上に提示すること。

## FR-010 LocalAuthentication

LocalAuthentication を signing policy に組み込めること。粒度は別途決定する。

## FR-011 Secure Enclave signing

承認済み request のみ Secure Enclave signing operation へ到達すること。

## FR-012 SSHSIG response

iPhone が Git / OpenSSH と interoperability のある SSHSIG を返せること。AgePonyCore を優先利用する。

## FR-013 Host-side verification

Windows は受信した署名が期待する Git signing public key で検証可能であることを確認してから Git へ返すことが望ましい。

## FR-014 Cancel

Windows / iPhone 双方から session / request を cancel できること。

## FR-015 Timeout

放置された session は expiry し、ephemeral state を破棄すること。

## FR-016 Error reporting

最低限次を区別できること。

- invalid QR
- unsupported protocol version
- TLS pin mismatch
- bad / expired session token
- malformed request
- payload too large
- user rejected
- LocalAuthentication failed
- signing failed
- connection lost
- invalid response
- unsupported Git invocation

## FR-017 Sequential requests

rebase 等で複数 commit が続いても、それぞれ独立 request として処理できること。

## FR-018 No batch authorization

複数 commit を一回の approval でまとめて署名する API を持たないこと。

---

# 32. 非機能要件

## NFR-001 Secret isolation

長期 Git private key は Windows memory / disk へ送られないこと。

## NFR-002 Minimal persistence

Windows 側 session secret / ephemeral TLS private key は通常終了後に再利用不能であること。

## NFR-003 Minimal dependencies

自作 surface を大幅に減らす dependency は許容するが、軽微な convenience のためだけに dependency tree を増やさない。

## NFR-004 Minimal custom crypto

暗号 primitive / signature scheme / QR standard を自作しない。

## NFR-005 Interoperability

最終成果物は通常の Git / OpenSSH / `git verify-commit` で検証できること。

## NFR-006 Auditability

protocol semantics、size limits、security invariants が `SPEC.md` 等に明文化され、C# / Swift の暗黙実装だけに存在しないこと。

## NFR-007 Testability

platform-independent logic は可能な範囲で unit test 可能にする。ただし testability のためだけの過剰な interface 抽象化は避ける。

## NFR-008 UI simplicity

UI は signing 確認と状態把握に必要なものだけを持つ。

## NFR-009 Deployment portability

borrowed Windows への導入は zip / self-contained publish 等で完結できる方向を優先し、machine-wide installer を必須にしない。

## NFR-010 Logs without secrets

log に session token、private key material、不要な full payload を残さない。requestId で相関できるようにする。

## NFR-011 Fail closed

次では sign しない。

- payload parse failure
- protocol validation failure
- TLS pin mismatch
- unknown version
- user approval なし
- key operation failure

## NFR-012 Bounded resources

payload size、connection timeout、approval timeout、outstanding request 数、QR size に上限を設ける。

---

# 33. Security invariants

```text
Invariant 1:
The Windows host never receives the long-term Git signing private key.

Invariant 2:
The iPhone UI derives trusted signing information from the exact payload to be signed.

Invariant 3:
A successful transport/session bootstrap does not authorize a signature.

Invariant 4:
One Git signing request requires one explicit human approval.

Invariant 5:
No batch-sign authorization endpoint exists in v1.

Invariant 6:
The Git signing key is not reused as an ad-hoc TLS key.

Invariant 7:
LAN security does not rely on the Wi-Fi network being trusted.

Invariant 8:
QR bootstrap data binds the iPhone to the intended ephemeral Windows TLS endpoint.

Invariant 9:
A session token is single-use and short-lived.

Invariant 10:
Routine app refresh should not silently rotate the Git signing identity.

Invariant 11:
The final signature must verify with ordinary Git/OpenSSH tooling.

Invariant 12:
Client-reported metadata must never be presented as payload-derived trusted fact.

Invariant 13:
Protocol knowledge is centralized in SPEC/CDDL/fixtures even if C# and Swift code is duplicated.

Invariant 14:
No custom cryptographic primitive is introduced.
```

---

# 34. テスト戦略

## 34.1 Protocol

- valid v1 request / response
- unsupported version
- missing required field
- malformed CBOR
- oversized payload
- invalid requestId
- wrong namespace
- invalid SSHSIG

## 34.2 Golden vectors

両言語で同じ fixture を読む。

- C# encode/decode
- Swift encode/decode
- 必要に応じて cross-output comparison

## 34.3 Git interoperability

最優先。

```text
software P-256 test key
→ DDD-compatible SSHSIG
→ git verify-commit success
```

iPhone networking より先に成立させる。

## 34.4 OpenSSH interoperability

known payload / key で OpenSSH と相互確認する。

## 34.5 Windows Git invocation

- argv
- stdin
- stdout/stderr
- exit code
- cancel
- path with spaces
- Unicode path
- temporary file

を fixture 化する。

## 34.6 QR

- descriptor encode/decode
- normal / maximum size
- corrupted data
- unsupported version
- quiet zone
- DPI / resize
- 実機 camera scanning

## 34.7 TLS / session

- correct / wrong fingerprint
- expired token
- wrong token
- replayed token
- second connection
- disconnect mid-request
- timeout
- cancel
- endpoint unreachable

## 34.8 Trusted UI

- clientMetadata と payload を意図的に食い違わせる
- UI は payload-derived 値を表示
- clientMetadata は untrusted と区別
- malformed Git payload をそれっぽく表示せず reject

## 34.9 Approval policy

- approval なし → sign 0 回
- Reject → sign 0 回
- Sign → sign 1 回
- 10 requests → 10 approval events
- duplicate / expired request → sign 不可

## 34.10 Lifecycle

iPhone:

- foreground/background
- lock/unlock
- biometric failure
- app relaunch
- network change

Windows:

- UI close
- Git abort
- DDD crash
- second invocation
- firewall prompt
- IP change

## 34.11 Provisioning / key persistence

- same Bundle ID update
- fresh provisioning
- re-sign
- reinstall
- uninstall/reinstall
- Keychain behavior
- Secure Enclave key reference behavior

結果を文書化する。

---

# 35. 実装順序

## Phase 0 — repository bootstrap

- monorepo
- `protocol/`
- Windows minimal project
- iOS minimal project
- CI skeleton
- Zed で repo root を開ける

## Phase 1 — Git / SSHSIG compatibility

目標:

```text
Git
→ local software P-256 signer
→ valid SSHSIG
→ git verify-commit success
```

ここでは LAN / iPhone / QR は不要。

完了条件:

- Git invocation contract 判明
- SSHSIG acceptance 判明
- fixture 作成

## Phase 2 — protocol specification

- `SPEC.md`
- `protocol.cddl`
- SignRequest / SignResponse / Error
- limits
- golden fixtures
- C# codec
- Swift codec

## Phase 3 — Windows Avalonia shell

- code-only Avalonia
- no `.axaml`
- session state
- QR display
- cancel
- status

この時点では mock signer でもよい。

## Phase 4 — QR bootstrap + TLS

Windows:

- ephemeral TLS identity
- endpoint
- session token
- descriptor encode
- QR render
- request server

iPhone:

- QR scan
- descriptor decode
- TLS connect
- fingerprint pin
- token present
- request receive

## Phase 5 — iPhone trusted confirmation

- Git payload parser
- human-readable review
- client metadata distinction
- Sign / Reject
- LocalAuthentication policy

## Phase 6 — Secure Enclave / AgePonyCore

- key generation
- persistence
- public key export
- P-256 signing
- SSHSIG generation
- response

## Phase 7 — end-to-end

```text
git commit -S
→ Avalonia QR
→ iPhone scan
→ TLS request
→ review
→ Sign
→ Secure Enclave
→ SSHSIG
→ Git commit complete
→ git verify-commit success
```

## Phase 8 — packaging / CI / install

- Windows self-contained publish
- macOS iOS build
- provisioning
- codesign
- IPA artifact
- pymobiledevice3 install
- persistence test

## Phase 9 — daily freshness

- external daily-ref
- Renovate
- bump PR
- PR build
- artifact lifecycle

---

# 36. v1 完了条件

1. borrowed Windows に長期 Git private key を保存せず `git commit -S` できる。
2. Windows は QR を表示するだけでよく、camera / Bluetooth を要求しない。
3. iPhone は QR scan から session を bootstrap できる。
4. LAN 上の payload は TLS で confidentiality / integrity が保護される。
5. TLS endpoint は QR から pin される。
6. request は single-use session token で保護される。
7. iPhone は exact Git payload を自分で parse する。
8. iPhone 上で 1 request ごとに explicit approval が必要。
9. signing private key は Secure Enclave にあり export されない。
10. AgePonyCore を利用して interoperable SSHSIG を生成できる。
11. Git / OpenSSH が署名を検証できる。
12. Windows UI は Avalonia / C# code-only、`.axaml` なしで動く。
13. iPhone UI は薄い SwiftUI で動く。
14. protocol は SPEC/CDDL/golden fixtures で検証できる。
15. C# と Swift の双方が同じ fixture に適合する。
16. GitHub Actions macOS で signed IPA を生成できる。
17. routine refresh による signing identity persistence の挙動が実機確認されている。

---

# 37. v1 で採用しないもの

| 項目 | 状態 | 理由 |
|---|---|---|
| GPG private key を Windows に import | 却下 | private key isolation に反する |
| private key を QR / PNG で運ぶ | 却下 | transport が変わるだけ |
| generic GPG remote signer | 却下寄り | surface が大きい |
| generic ssh-agent | v1 却下 | trusted Git semantic UI が弱くなる |
| WinForms | 却下 | UI 方針 |
| WebView / HTML desktop UI | 却下 | UI 方針 |
| WPF | 不採用 | Avalonia 採用 |
| AXAML | 却下 | C# coded UI 方針 |
| 自作 C# UI DSL | 却下 | machinery が増える |
| Rust shared core | v1 却下 | FFI / bindings が増える |
| 自作 QR encoder | 却下 | existing library を使う |
| QR→PNG→Bitmap | 不採用 | module matrix を直接描画 |
| 自作 SSHSIG crypto stack | 原則却下 | AgePonyCore を優先 |
| Bonjour 必須 discovery | v1 却下 | QR endpoint で不要 |
| Bluetooth pairing | 却下 | rented PC hardware に期待しない |
| public CA / Web PKI | 不要 | QR pinning |
| persistent pairing DB | v1 不採用 | session bootstrap で十分 |
| strict mTLS | v1 必須にしない | TLS pin + token + SSHSIG で surface 削減 |
| batch sign API | 却下 | explicit approval を壊す |
| auto-sign after pairing | 却下 | signing oracle 化 |
| always-background iOS listener | 却下 | iOS lifecycle と不釣り合い |
| Mac を primary dev machine にする | 却下 | Debian 中心 |
| GHA schedule のみで daily build | 却下 | state-driven trigger |
| custom protocol codegen | v1 不採用 | protocol が小さい |
| huge shell / YAML logic | 却下 | glue を software にしない |
| USB transport | 将来 | LAN で実害が出てから |

---

# 38. 未決事項

## P0

### Git → DDD exact invocation contract

Git / OpenSSH behavior を実測して固定する。

### AgePonyCore exact API

- package integration
- Secure Enclave API
- SSHSIG API
- required iOS version
- test vectors

### Secure Enclave key persistence

provisioning refresh を含む実機検証。

### Payload parser boundary

Git commit payload と SSHSIG input の exact relation を確定する。

## P1

### LocalAuthentication policy

Face ID を request ごとか session ごとか。

### TLS API / implementation

Windows .NET / iOS の具体 API。

### CBOR library

C# / Swift 双方で小さく保てるものを選ぶ。

### Session lifetime

QR expiry、connect timeout、approval timeout、session completion。

### Payload size limit

実データを観測して決定。

### Windows packaging

framework-dependent / self-contained / single-file / directory bundle のどこまで求めるか。

## P2

- direct-network fallback UX
- multiple signing profiles
- key rotation UX
- GitHub 等での Verified 表示 end-to-end 確認
- QR に payload 本体を載せる hardened mode

---

# 39. 開発判断チェックリスト

新しい dependency / language / abstraction を追加する前に確認する。

```text
□ これで自作コードは本当に減るか
□ security-critical logic を増やしていないか
□ platform-native solution で済まないか
□ protocol knowledge が複数箇所へ分散しないか
□ generated-code machinery の方が手書きより大きくないか
□ glue が software になっていないか
□ v1 の実際の requirement に必要か
□ 「将来あるかも」だけで追加していないか
```

---

# 40. 最終アーキテクチャ

```text
┌─────────────────────────────────────────────────────┐
│ Borrowed Windows                                    │
│                                                     │
│ Git                                                 │
│  │ git commit -S                                    │
│  ▼                                                  │
│ DDD / C#                                            │
│  ├─ minimal ssh-keygen-compatible adapter           │
│  ├─ SignRequest                                     │
│  ├─ ephemeral TLS server identity                   │
│  ├─ single-use session token                        │
│  ├─ CBOR bootstrap descriptor                       │
│  ├─ QRCoder                                         │
│  └─ Avalonia code-only UI                           │
│          │                                          │
│          └──── display QR ───────────────┐          │
└──────────────────────────────────────────┼──────────┘
                                           │ camera
                                           ▼
┌─────────────────────────────────────────────────────┐
│ iPhone                                              │
│                                                     │
│ SwiftUI                                             │
│  ├─ scan QR                                         │
│  ├─ decode bootstrap                                │
│  ├─ connect to Windows                              │
│  ├─ pin ephemeral TLS identity                      │
│  ├─ present single-use token                        │
│  ├─ receive exact Git payload                       │
│  ├─ parse trusted commit information                │
│  ├─ Sign / Reject                                   │
│  ├─ LocalAuthentication                             │
│  └─ AgePonyCore                                     │
│        │                                            │
│        ▼                                            │
│  Secure Enclave P-256                               │
│        │                                            │
│        ▼                                            │
│      SSHSIG                                         │
└────────┬────────────────────────────────────────────┘
         │ TLS response
         ▼
┌─────────────────────────────────────────────────────┐
│ Windows                                             │
│   verify / return signature to Git                  │
│            │                                        │
│            ▼                                        │
│        signed commit                                │
└─────────────────────────────────────────────────────┘
```

---

# 41. 開発・配布アーキテクチャ

```text
                     ┌─────────────────┐
                     │ Zed / Debian    │
                     │ repo root       │
                     └───────┬─────────┘
                             │
             ┌───────────────┼──────────────┐
             ▼               ▼              ▼
        C# / .NET        Swift          protocol
        Windows          iOS            SPEC/CDDL
             │               │              │
             └───────────────┼──────────────┘
                             │
                             ▼
                           Git
                             │
                             ▼
                    GitHub Actions
                    ┌────────┴────────┐
                    ▼                 ▼
               Windows runner      macOS runner
               build/test          iOS build
                                   provision
                                   codesign
                                      │
                                      ▼
                                  signed IPA
                                      │
                                      ▼
                                GHA Artifact
                                      │
                                      ▼
                            Linux / Windows install
                                      │
                              pymobiledevice3
                                      │
                                      ▼
                                    iPhone
```

---

# 42. このプロジェクトの本質

DDD の security boundary は単に「private key が iPhone にある」ことではない。

```text
Secure Enclave
        +
exact payload verification on iPhone
        +
explicit human approval
```

この三つが揃って初めて、iPhone は network signing oracle ではなく trusted signer になる。

v1 の目標は、**borrowed Windows では長期秘密を持たず、Windows が出した exact Git signing request を自分の iPhone 上で確認し、必要なときだけ Secure Enclave の長期 signing identity を使えることを、最小限のコードと machinery で成立させること**である。
