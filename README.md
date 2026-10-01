# Devil Device Delegator

借り物 Windows から、自分の iPhone の non-exportable Secure Enclave P-256 key で Git commit を SSH 署名するためのプロジェクトです。

**現在は P0/P1 feasibility と Git/OpenSSH compatibility milestone です。製品の iPhone transport／Git signing はまだ使えません。** QR、protocol codec、Git payload review UI、LAN signing は後続 phase です。

## この milestone の構成

- `windows/src/Ddd.Windows/`: 最小 Git signing invocation adapter。file／stdin の exact bytes を bounded に取得します。iPhone backend 未実装のため署名は fail closed します。
- `windows/tests/Ddd.CompatSigner/`: **テスト専用**の software P-256 signer。OpenSSH を使用し、期待する公開鍵・payload・namespace で検証後に返します。production publish に含めません。
- `windows/tests/Ddd.Windows.Tests/`: 実 Git/OpenSSH 互換性、adapter 拒否系、TLS pinning、CBOR 候補 API の検証。
- `protocol/fixtures/compat/`: exact Git commit payload、invocation、公開鍵のみの SSHSIG golden。wire protocol はまだ確定していません。
- `ios/`: revision-pinned AgePonyCore consumer、software SSHSIG interoperability tests、明示確認付きの実機鍵永続性 probe。

## .NET tests（Linux／Windows）

必要: .NET SDK 10、Git、`ssh-keygen` が PATH にあること。Git/OpenSSH テストは skip せず、依存不足なら失敗します。ルート自体を Git repository にする必要はなく、テストは使い捨て repository を生成します。

```sh
dotnet test windows/Ddd.slnx -c Release
```

この開発 sandbox では MSBuild の並列 worker が動かないため、以下で検証しました。

```sh
dotnet test windows/Ddd.slnx --no-restore --tl:off -m:1 -nr:false -p:UseSharedCompilation=false
```

テストは一時 software private key を生成・削除します。これは borrowed Windows 上で製品の長期鍵を保存する設計ではありません。capture はテスト専用で、fixture／テスト payload のみを記録します。

## iOS feasibility（macOS／実機）

```sh
swift test --package-path ios
swift test --package-path ios -c release
```

Linux 上の Swift/Xcode と iPhone が利用できないため、Swift build と実機検証は未実行です。`.github/workflows/ios.yml` に macOS tests／unsigned device build を用意しています。Unsigned build は installable IPA ではありません。

実機の作成・導入・更新手順、Apple provisioning／key persistence の未検証 matrix は [iOS feasibility](docs/ios-feasibility.md) を参照してください。probe key を production Git identity として登録しないでください。

## 検証結果・次の gate

- [Git invocation と SSHSIG contract](docs/git-compatibility.md)
- [P0/P1 decisions and pending gates](docs/p0-p1-status.md)
- [.NET TLS feasibility](docs/tls-feasibility.md)
- [開発計画書](devil-device-delegator-development-plan.md)

v1 の security boundary は **exact payload の iPhone 上の確認 + 1 request ごとの明示承認 + Secure Enclave** です。通信接続だけでは署名を許可しません。
