# AEC Audio Router

Windows 11 x64 向けの C# / WPF 音声ルーターです。**WebRTC AEC3 で参照音声に対応するマイク内のエコーを抑圧し、選択した出力先へ送ります。** 参照音声はマイク出力に混ぜません。開始時にネイティブ DLL の読み込みと初期化が成功した場合だけ「AEC3 有効」と表示し、失敗時は開始を中止します。

## インストール

[Releases](https://github.com/yamaotoru7/AEC-Audio-Router/releases) から `AecAudioRouter-<版>-setup.exe` を取得して実行します。管理者権限は不要で、`%LOCALAPPDATA%\Programs\AecAudioRouter` に入ります。.NET ランタイムは同梱しているため別途インストールは不要です。アンインストールは Windows の「インストールされているアプリ」から行います。設定ファイルは残ります。

### 更新の確認

インストールしたアプリは、起動時に1日1回 GitHub のリリースを確認します。新しい版があると右下（開始ボタンの左）のボタンが「<版> に更新」に変わります。ボタンを押すとリリースノートを表示し、**更新して再起動** でインストーラーを取得してハッシュ（`.sha256`）を照合し、更新後に起動し直します。github.com のダウンロード URL から取れないときは、GitHub の API から取り直します。出力中に更新すると音声は停止します。手動で確認するには右下の **更新を確認** を押します。ビルドしたフォルダから起動した場合は、起動時の確認を行いません。起動時の確認を止めるには、アプリを閉じて `settings.json` の `CheckForUpdates` を `false` にします。

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

**ノイズ除去**（`Alt+N`）で、マイクの背景ノイズを抑えます。初期値は OFF で、出力中も切り替えられます。AEC3 の後（エコー除去が OFF のときは処理前のマイク音声）にかけるため、AEC3 が学習するエコーの経路には影響しません。

| 設定 | エンジン | 向いているノイズ | 増える遅延 | CPU の負荷（10 msあたり） |
| --- | --- | --- | --- | --- |
| 軽量 | RNNoise | 空調・ファン・ハムなどの定常的なノイズ | 20 ms | 約0.07 ms |
| 高品質 | DPDFNet | 上に加え、キーボードや物音など変化するノイズ | 50 ms | 約0.9 ms（CPU 1コア） |
| ASR | GTCRN | 音声認識や音声 AI への入力。16 kHz で処理し、8 kHz より上の帯域を落とす | 33 ms | 約0.4 ms |
| DFN3（試験的） | DeepFilterNet3 | 比較用。電源のハムなど強い低音は残る。Rust でビルドしたときだけ選べる | 30 ms | 約0.3 ms |

切り替えると、そのエンジンを新しく作り直します。切り替えの瞬間は遅延が変わるため、短い途切れや重なりが聞こえることがあります。

設定は「開始」を押したときとアプリを閉じたときに `%LOCALAPPDATA%\AecAudioRouter\settings.json` へ保存され、次回起動時に復元されます。対象は参照方式、各デバイス、CH 番号、WAV ファイル、エコー除去の ON / OFF、ノイズ除去の設定です。保存したデバイスが見つからない場合はそのデバイスを未選択にします。ファイルが壊れている場合は初期値で起動します。設定を初期化するにはアプリを閉じてこのファイルを削除します。

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
| 参照音声、マイク入力、出力 | 約150 msごとの Peak / RMS（dBFS）。バーは Peak の −60〜0 dBFS。出力は AEC3 とノイズ除去の処理後の信号です。 |
| CPU | プロセスの CPU 時間を約1秒ごとに測り、全論理コアの処理能力に対する割合で表示します。 |
| 処理時間 | マイクコールバック1回あたりの平均処理時間。モノラル化、同期、リサンプリング、AEC3、ノイズ除去、メーター、出力キューへの書き込みを含みます。別の参照コールバックの時間は含みません。 |
| 出力待ち | 現時点のアプリ内キューにある音声の長さ。上限は80 msです。 |
| バッファ要求・廃棄・無音補完 | WASAPI 共有モードに要求した出力バッファは30 ms。キューが満杯なら古いサンプルを捨て、不足した分は無音で補います。カウンターには開始直後も含まれます。 |
| 参照欠落・同期廃棄 | 参照を無音で補った回数と、時間が合わず破棄した参照の数（10 ms フレーム）。無音時にループバックが止まる場合も欠落に数えます。 |
| AEC 再同期 | マイクの時刻に100 msを超える不連続があり、AEC3 を作り直した回数。 |

「処理時間」「出力待ち」「バッファ要求」は別々の値です。AEC の参照を待つ固定40 ms、10 msフレーム化、リサンプラーと AEC のフィルター遅延、ノイズ除去が「軽量」なら20 ms、「高品質」なら50 ms、「ASR」なら33 ms、「DFN3」なら30 msも加わります。マイクから受け取り側アプリまでの遅延は実測が必要です。内部出力は48 kHzモノラルで、WASAPI の共有モードで出力デバイスの形式に変換します。

## テスト

```powershell
dotnet .\tests\AecAudioRouter.Tests\bin\Release\net10.0-windows\AecAudioRouter.Tests.dll
dotnet .\tests\AecAudioRouter.Tests\bin\Release\net10.0-windows\AecAudioRouter.Tests.dll --wpf
```

自動テストでは既存の PCM・メーター・キュー・経路検証に加え、実際のネイティブ DLL を使って次を確認します。`--wpf` はウィンドウの構築、参照方式の切り替え、デバイス列挙も確認します。

- 30 / 100 / 200 ms の遅延と反射を加えた合成エコーの抑圧（20 dB 以上）。
- ダブルトーク時の合成有声音との相関・ゲイン、参照が無音のときの近端音声保持。
- 8 / 16 / 44.1 / 48 / 96 / 192 kHz の入力、不定長パケット、ダウンサンプリング時の折り返し抑制。
- 参照の2秒間の停止・再開、30 / 60 / 90 ms の短い停止の後のエコー抑圧、マイクの不連続、二重破棄と破棄後の呼び出し。
- 44.1 / 48 kHz の異なる入力レート、到着ジッター、±150 ppm のクロック差を含む90秒の合成経路。
- ノイズ除去（軽量・高品質・ASR・DFN3 のそれぞれ。DFN3 はビルドしたときだけ、ハムなしのノイズで）：合成ノイズだけの区間の抑圧（12 dB 以上）、ノイズに重ねた合成有声音の保持（相関とゲイン）、遅延が20 ms / 50 ms / 33 ms / 30 msであること、無音・クリップ・不正なフレーム、モデルが無いときのエラー。STFT 用の FFT は素朴な DFT と比べます。
- AEC3 とノイズ除去を重ねた経路：エコー除去に上乗せしてノイズを抑えること、AEC を OFF にしても効くこと、ノイズ除去の OFF で信号を変えないこと、出力中の切り替え。

今回の Windows 11 / .NET SDK 10.0.403 の検証では、Release ビルド、依存ソースの新規取得からのクリーンビルド、WPF を含む全23テストが成功しました。同一レートの合成エコーを約41 dB抑圧し、異なるレートと±150 ppmのクロック差を含む経路では22.3〜26.0 dBでした。ダブルトークの合成有声音は相関約0.88〜0.89、ゲイン約0.62〜0.65でした。RNNoise は合成ノイズだけの区間を約78 dB抑え、ノイズに重ねた合成有声音との相関を0.63から0.93に上げました（ゲイン約0.85）。DPDFNet は同じ信号でノイズだけの区間を約64 dB抑え、相関を0.95に上げました（ゲイン約0.91）。GTCRN は約48 dB抑え、相関を0.93に上げました（ゲイン約0.79）。DeepFilterNet3 はこの合成ノイズの50 Hzのハムを音声とみなして通すため約8 dBにとどまり、ハムを除いたノイズでは約39 dB抑えて相関を0.74から0.94に上げました（同じ条件で RNNoise 74 dB / 0.93、DPDFNet 64 dB / 0.95、GTCRN 33 dB / 0.94）。これらは合成信号での結果で、実際の会話の聞きやすさを保証するものではありません。AEC3 統合後の実スピーカー・マイクによるエコー抑圧、実機の長時間運転、端末間遅延は未検証です。

## WebRTC AEC3 の実装

依存元は [Enaium/webrtc-aec3](https://github.com/Enaium/webrtc-aec3/tree/2cec2f52e26646f93bd2d5498bbabf59cba18da9) のコミット `2cec2f52e26646f93bd2d5498bbabf59cba18da9` です。WebRTC から抽出された AEC3 を使い、C ABI の薄いラッパーを C# から P/Invoke / SafeHandle で呼びます。WebRTC 全体や APM 全体のビルドではありません。抽出元の CPU 機能判定は無効化されており、AEC3 の基本演算を使用します。MSVC 対応のヘッダー補完と SIMD 構文修正は `native/CMakeLists.txt` に記録し、取得したソースは変更しません。

参照・マイクをモノラル化し、WebRTC の sinc リサンプラーで48 kHzに揃えます。480サンプルごとに参照の帯域分割 → `AnalyzeRender`、マイクの `AnalyzeCapture` → 帯域分割 → `ProcessCapture` → 帯域合成を実行します。AGC は追加していません。AEC3 の残留エコー抑制により、定常雑音なども減衰する場合があります。

入力の時刻は `Stopwatch` のコールバック到着時刻とパケット長から推定します。WASAPI のハードウェアタイムスタンプを直接取得していないため、OS のスケジューリングやデバイス内の遅延を正確に表すものではありません。40 msのマイク待機、緩やかなサンプルクロック補正、参照のサンプル単位の線形補間で到着差とクロック差を扱い、残る音響遅延は AEC3 が推定します。欠落した参照は無音で補い、古い参照は破棄します。大きな不連続や到着遅れでは抑圧が一時的に低下する可能性があります。参照キューは最大500 ms、出力キューは最大80 msです。

ライセンスと特許条項は `native/THIRD-PARTY-NOTICES.txt`（インストール先では `THIRD-PARTY-NOTICES.txt`）にまとめています。ONNX Runtime と .NET ランタイムの第三者の表記、DFN3 の `deepfilter.dll` にリンクされた Rust のクレートの一覧は、インストール先の `licenses` フォルダーにあります。

## ノイズ除去（RNNoise）の実装

[RNNoise 0.2](https://github.com/xiph/rnnoise/releases/tag/v0.2) のリリース tarball（学習済みモデル `rnnoise_data.c` を含む）を SHA-256 を固定して取得し、同じネイティブ DLL に静的にリンクします。RNNoise は48 kHz・480サンプルのフレームで動くため、AEC3 と同じフレームをリサンプリングなしで渡せます。CPU の機能判定は使わず、x64 の基本である SSE2 でビルドします。AEC3 の出力（またはバイパス時の生のマイク音声）を ±1 に制限して16ビットの尺度に直し、その場で処理します。

## ノイズ除去（DPDFNet）の実装

[DPDFNet](https://github.com/ceva-ip/DPDFNet)（Ceva、Apache-2.0）の48 kHz 版 `dpdfnet2_48khz_hr.onnx`（約10 MB）を [ONNX Runtime](https://github.com/microsoft/onnxruntime) の CPU 実行で動かします。モデルはビルド時に Hugging Face の固定したリビジョンから `src/AecAudioRouter/obj/models` へ取得して SHA-256 を確かめ、出力先の `models` フォルダーへコピーします（`AecAudioRouter.csproj`）。

モデルは STFT の1フレームを受け取り、内部の状態を次の呼び出しへ引き継ぎます。その外側の処理は参照スクリプト `onnx_model/infer_dpdfnet_onnx.py` と同じにしています：960点の vorbis 窓、480サンプルのホップ、960点の FFT（960 = 2⁶·3·5 のため、混合基数の FFT を `Fft.cs` に実装）、逆 FFT に同じ窓をかけて重ね合わせます。状態の初期値はモデルのメタデータから読みます。推論は1スレッドで、10 msのフレームごとにマイクのコールバックの中で行います。

## ノイズ除去（GTCRN）の実装

[GTCRN](https://github.com/Xiaobin-Rong/gtcrn)（MIT）のストリーミング用モデル `stream/onnx_models/gtcrn_simple.onnx`（約0.5 MB、DNS3 で学習）を ONNX Runtime で動かします。モデルの取得と確認は DPDFNet と同じで、GitHub の固定したコミットから取得します。

モデルは16 kHzで動くため、48 kHzのフレームを WebRTC の sinc リサンプラーで16 kHzに落とし、処理後に48 kHzへ戻します。出力には8 kHzより上の成分が残りません。モデルの外側の処理は `stream/gtcrn_stream.py` と同じにしています：512点の sqrt-Hann 窓、256サンプル（16 ms）のホップ、キャッシュの初期値はゼロ。16 msのホップと10 msのフレームのずれは入出力のキューで吸収し、出力のキューに1ホップ分の無音を先に入れて、遅延を一定に保ちます。作者のリポジトリの `test_wavs/mix.wav` をこの経路で処理した結果は、作者の `enh.wav` と相関0.9996で一致しました（差は往復のリサンプリングによるもの）。

## ノイズ除去（DeepFilterNet3、試験的）の実装

比較のために [DeepFilterNet3](https://github.com/Rikorose/DeepFilterNet)（MIT / Apache-2.0）を作者のストリーミング実装 libDF（Rust、推論は tract）の C API で動かします。公式の ONNX はモデルが3つに分かれ、再帰の状態を外に出さないため、1フレームずつの処理には使えません。

`native/build-deepfilternet.ps1` が固定したコミットのソースを取得し、`native/deepfilternet/Cargo.lock` で依存を固定して `deepfilter.dll` をビルドします。Rust のツールチェーン 1.91.0 が無い場合は警告を出して飛ばし、アプリでは「DFN3」を選べなくなります（`rustup toolchain install 1.91.0 --profile minimal`）。モデル `DeepFilterNet3_onnx.tar.gz` は、DLL をビルドできたときだけ取得して同梱します。libDF は内部のエラーでプロセスを止めるため（Rust の panic は C API を越えられない）、モデルのファイルを渡す前に、ビルドが取得時に照合したのと同じ SHA-256 で確かめます。壊れたモデルは読み込まずにエラーにします。
