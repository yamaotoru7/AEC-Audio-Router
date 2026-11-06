# AEC Audio Router

Windows 11 x64 向けの C# / WPF 音声ルーターです。**WebRTC AEC3 で参照音声に対応するマイク内のエコーを抑圧し、選択した出力先へ送ります。** 参照音声はマイク出力に混ぜません。開始時にネイティブ DLL の読み込みと初期化が成功した場合だけ「AEC3 有効」と表示し、失敗時は開始を中止します。

## インストール

[Releases](https://github.com/yamaotoru7/AEC-Audio-Router/releases) から `AecAudioRouter-<版>-setup.exe` を取得して実行します。管理者権限は不要で、`%LOCALAPPDATA%\Programs\AecAudioRouter` に入ります。.NET ランタイムは同梱しているため別途インストールは不要です。アンインストールは Windows の「インストールされているアプリ」から行います。設定ファイルは残ります。

### 更新の確認

インストールしたアプリは、起動時に1日1回 GitHub のリリースを確認します。新しい版があると右上のボタンが「<版> に更新」に変わります。ボタンを押すとリリースノートを表示し、**更新して再起動** でインストーラーを取得してハッシュ（`.sha256`）を照合し、更新後に起動し直します。出力中に更新すると音声は停止します。手動で確認するには右上の **更新を確認** を押します。ビルドしたフォルダから起動した場合は、起動時の確認を行いません。起動時の確認を止めるには、アプリを閉じて `settings.json` の `CheckForUpdates` を `false` にします。

## 起動とビルド

Windows 11 x64、.NET 10 SDK、Visual Studio 2022 Build Tools の C++ ワークロードと Windows SDK、CMake 3.24 以降、Git、利用する音声デバイスが必要です。CMake と Git は PATH に登録してください。依存ライブラリは NAudio.Wasapi 2.2.1 です。仮想デバイスは Windows に登録済みのものを使います。

`dotnet build` が CMake を呼び出し、AEC3Native.dll を Release / x64 でビルドしてアプリとテストの出力フォルダへコピーします。初回は NuGet と GitHub への接続が必要です。AEC3 ソースは下記のコミットに固定され、通常は `native/build/_deps` にキャッシュされます。ネイティブだけをビルドする場合は `powershell -File native/build-native.ps1` を実行します。

プロジェクトのルートで PowerShell を開いて実行します。

```powershell
dotnet restore .\AecAudioRouter.slnx
dotnet build .\AecAudioRouter.slnx -c Release --no-restore
dotnet .\src\AecAudioRouter\bin\Release\net10.0-windows\AecAudioRouter.dll
```

ビルド済みのアプリは `src\AecAudioRouter\bin\Release\net10.0-windows\AecAudioRouter.exe` です。エクスプローラーから起動できます。別の PC には同じフォルダの DLL と `THIRD-PARTY-NOTICES.txt` を含めてコピーしてください。実行先には .NET 10 Desktop Runtime x64 が必要です。ネイティブ DLL は C++ ランタイムを静的リンクします。

インストーラーは [Inno Setup 6](https://jrsoftware.org/isinfo.php) を入れて `pwsh tools/installer/build-installer.ps1` で作ります。self-contained で publish し、`.work\installer\AecAudioRouter-<版>-setup.exe` と、更新時に照合する `.sha256` を出力します。版は `Directory.Build.props` の `<Version>` です。

## 使い方

1. **参照音声**の方式と対象デバイスを選びます。
   - **入力デバイス**。別の PC や機器の再生信号を、このアプリを動かす PC のライン入力などに接続して取り込みます。参照とマイクが同じ多チャンネル入力にある場合は、両方に同じデバイスを選び、それぞれ別の CH 番号を入力します。この組み合わせは同じキャプチャストリームから読み取ります。同じ CH や「0：全チャンネル」の併用は、自分の声を参照に含めないため禁止します。
   - **再生音のループバック**。この PC で参照音声が再生される出力先を選びます。選択した再生先の Windows ミックスを取り込みます。再生が止まると参照メーターは下がります。 [Microsoft の WASAPI ループバック解説](https://learn.microsoft.com/ja-jp/windows/win32/coreaudio/loopback-recording)
   - **WAV ファイル**。PCM または 32-bit float の WAV ファイルと再生先を選びます。「開始」で再生し、その再生デバイスのループバックを AEC3 の参照に使います。同じデバイスで鳴る他のアプリの音も含まれます。ファイルの終端に達してもマイクの処理は続きます。先頭から再生するには停止後に再度開始します。
2. **マイク入力**と**マイクの出力先**を選びます。CH の `0` は全チャンネルを平均してモノラル化します。メーターは参照・マイクとも、このモノラル信号を測定します。CH を個別に指定するときは `1` 始まりです。参照にはスピーカーから出る音を選び、自分のマイク音声を混ぜないでください。
3. 処理後の音声は仮想オーディオデバイスを通して受け取る側のアプリ（会議アプリなど）へ渡します。**マイクの出力先**に仮想デバイスの再生側を選び、受け取る側のアプリのマイクには同じデバイスの録音側を選びます。仮想デバイスはこのアプリに含まれていないため、別に用意してください（無償のものは下記の「[仮想オーディオデバイス](#仮想オーディオデバイス)」を参照）。
4. **開始**を押します。参照とマイクに信号を入れ、メーターを見ながら、受け取る側のアプリでも入力レベルを確認します。出力先が表示されない場合や機器を接続し直した場合は、停止中に **一覧を更新** を押します。

右上の **エコー除去** スイッチで AEC3 の ON / OFF を切り替えられます（`Alt+A`）。出力中も切り替えでき、OFF の間はマイク音声を AEC3 処理前のまま出力します（遅延は ON と同じ）。OFF の間も AEC3 は参照音声で学習を続けるため、ON に戻すと再収束を待たずにすぐ効きます。開始前に OFF にしておけば「AEC3 無効」の状態で開始します。

設定は「開始」を押したときとアプリを閉じたときに `%LOCALAPPDATA%\AecAudioRouter\settings.json` へ保存され、次回起動時に復元されます。対象は参照方式、各デバイス、CH 番号、WAV ファイル、エコー除去の ON / OFF です。保存したデバイスが見つからない場合はそのデバイスを未選択にします。ファイルが壊れている場合は初期値で起動します。設定を初期化するにはアプリを閉じてこのファイルを削除します。

ループバック先や WAV 再生先に、マイクと同じ出力先は選べません。仮想デバイスやミキサーを組み合わせる場合は、マイク出力が参照入力や選択したマイクに戻らないように配線・設定してください。

## 仮想オーディオデバイス

画面の **Virtual Cables** 欄の **入手先を開く** は、無償の仮想ケーブル [Virtual Cables](https://github.com/tarekwasfy01/Virtual-Cables) の配布ページを開きます。Windows に登録される仮想オーディオデバイスであれば、他の製品も使えます。

1. 配布ページから `Virtual_Cables_Setup.exe` を取得します。Virtual Cables は商用利用も許す BSD 2-Clause ライセンスです。 [公式リリース](https://github.com/tarekwasfy01/Virtual-Cables/releases/tag/Virtual-Cables) · [ライセンス](https://github.com/tarekwasfy01/Virtual-Cables/blob/main/LICENSE.txt)
2. Virtual Cables を起動し、必要なら **Download and Install Driver** から USB/IP ドライバーを導入します。配布元は Windows x64 向けの Microsoft 署名済み USB/IP ドライバーを公開しています。Virtual Cables のセットアップ本体は署名されていないため、PC のポリシーによって実行が制限される場合があります。 [USB/IP ドライバーの配布元](https://github.com/vadimgrn/usbip-win2/releases)
3. Virtual Cables でケーブルを1本作り、起動したままにします。AEC Audio Router で **一覧を更新** を押し、**マイクの出力先**に `Virtual Cable 01` の再生側を選びます。受け取る側のアプリのマイクには、同じ番号の録音側を選びます。 [Virtual Cables の接続手順](https://github.com/tarekwasfy01/Virtual-Cables#example)

この組み合わせでのインストール、音声の疎通、連続運転は未確認です。Virtual Cables は配布元が実験段階としているため、実運用前に受け取る側のアプリで音量と音切れを確認してください。 [配布元の開発状況](https://github.com/tarekwasfy01/Virtual-Cables#project-status)

## メーターと負荷表示

| 表示 | 測定内容 |
| --- | --- |
| 参照音声、マイク入力、出力 | 約150 msごとの Peak / RMS（dBFS）。バーは Peak の −60〜0 dBFS。出力は AEC3 処理後の信号です。 |
| CPU | プロセスの CPU 時間を約1秒ごとに測り、全論理コアの処理能力に対する割合で表示します。 |
| 処理時間 | マイクコールバック1回あたりの平均処理時間。モノラル化、同期、リサンプリング、AEC3、メーター、出力キューへの書き込みを含みます。別の参照コールバックの時間は含みません。 |
| 出力待ち | 現時点のアプリ内キューにある音声の長さ。上限は80 msです。 |
| バッファ要求・廃棄・無音補完 | WASAPI 共有モードに要求した出力バッファは30 ms。キューが満杯なら古いサンプルを捨て、不足した分は無音で補います。カウンターには開始直後も含まれます。 |
| 参照欠落・同期廃棄 | 参照を無音で補った回数と、時間が合わず破棄した参照の数（10 ms フレーム）。無音時にループバックが止まる場合も欠落に数えます。 |
| AEC 再同期 | マイクの時刻に100 msを超える不連続があり、AEC3 を作り直した回数。 |

「処理時間」「出力待ち」「バッファ要求」は別々の値です。AEC の参照を待つ固定40 ms、10 msフレーム化、リサンプラーと AEC のフィルター遅延も加わります。マイクから受け取り側アプリまでの遅延は実測が必要です。内部出力は48 kHzモノラルで、WASAPI の共有モードで出力デバイスの形式に変換します。

## テスト

```powershell
dotnet .\tests\AecAudioRouter.Tests\bin\Release\net10.0-windows\AecAudioRouter.Tests.dll
dotnet .\tests\AecAudioRouter.Tests\bin\Release\net10.0-windows\AecAudioRouter.Tests.dll --wpf
```

自動テストでは既存の PCM・メーター・キュー・経路検証に加え、実際のネイティブ DLL を使って次を確認します。`--wpf` はウィンドウの構築、参照方式の切り替え、デバイス列挙も確認します。

- 30 / 100 / 200 ms の遅延と反射を加えた合成エコーの抑圧（20 dB 以上）。
- ダブルトーク時の合成有声音との相関・ゲイン、参照が無音のときの近端音声保持。
- 8 / 16 / 44.1 / 48 / 96 / 192 kHz の入力、不定長パケット、ダウンサンプリング時の折り返し抑制。
- 参照の2秒間の停止・再開、マイクの不連続、二重破棄と破棄後の呼び出し。
- 44.1 / 48 kHz の異なる入力レート、到着ジッター、±150 ppm のクロック差を含む90秒の合成経路。

今回の Windows 11 / .NET SDK 10.0.403 の検証では、Release ビルド、依存ソースの新規取得からのクリーンビルド、WPF を含む全12テストが成功しました。同一レートの合成エコーを約41 dB抑圧し、異なるレートと±150 ppmのクロック差を含む経路では22.3〜26.0 dBでした。ダブルトークの合成有声音は相関約0.88〜0.89、ゲイン約0.62〜0.65でした。これらは合成信号での結果で、実際の会話の聞きやすさを保証するものではありません。AEC3 統合後の実スピーカー・マイクによるエコー抑圧、実機の長時間運転、端末間遅延は未検証です。

## WebRTC AEC3 の実装

依存元は [Enaium/webrtc-aec3](https://github.com/Enaium/webrtc-aec3/tree/2cec2f52e26646f93bd2d5498bbabf59cba18da9) のコミット `2cec2f52e26646f93bd2d5498bbabf59cba18da9` です。WebRTC から抽出された AEC3 を使い、C ABI の薄いラッパーを C# から P/Invoke / SafeHandle で呼びます。WebRTC 全体や APM 全体のビルドではありません。抽出元の CPU 機能判定は無効化されており、AEC3 の基本演算を使用します。MSVC 対応のヘッダー補完と SIMD 構文修正は `native/CMakeLists.txt` に記録し、取得したソースは変更しません。

参照・マイクをモノラル化し、WebRTC の sinc リサンプラーで48 kHzに揃えます。480サンプルごとに参照の帯域分割 → `AnalyzeRender`、マイクの `AnalyzeCapture` → 帯域分割 → `ProcessCapture` → 帯域合成を実行します。AGC や独立したノイズ抑制は追加していません。AEC3 の残留エコー抑制により、定常雑音なども減衰する場合があります。

入力の時刻は `Stopwatch` のコールバック到着時刻とパケット長から推定します。WASAPI のハードウェアタイムスタンプを直接取得していないため、OS のスケジューリングやデバイス内の遅延を正確に表すものではありません。40 msのマイク待機、緩やかなサンプルクロック補正、参照のサンプル単位の線形補間で到着差とクロック差を扱い、残る音響遅延は AEC3 が推定します。欠落した参照は無音で補い、古い参照は破棄します。大きな不連続や到着遅れでは抑圧が一時的に低下する可能性があります。参照キューは最大500 ms、出力キューは最大80 msです。

ライセンスと特許条項は `native/THIRD-PARTY-NOTICES.txt` に同梱しています。
