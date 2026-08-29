# DiscaScout Architecture

## 1. 目的

DiscaScout は、TSUTAYA DISCAS の CD 検索結果を定期的に収集し、新作・近日リリース作品を確認しやすくするためのローカル Web アプリケーションです。

DISCAS の検索 UI をそのまま代替することではなく、次の用途に必要なデータを継続的に蓄積し、ユーザー自身の確認状態やレンタル状態と組み合わせて管理することを目的とします。

- `近日リリース` と `新作` の定期収集
- 前回確認後に追加・変更された CD の確認
- 指定アーティストの新着検出
- 指定アーティストの全作品カタログ収集
- レンタル済み CD の管理
- 過去に観測した CD の検索

DISCAS 全 CD の恒久的なミラーを作ることは目的としません。通常収集では、DiscaScout が実際に観測した近日リリース・新作を蓄積します。指定アーティストについてのみ、明示的な操作によって全作品を収集します。

## 2. 基本構成

### 2.1 技術スタック

現時点の採用方針は次のとおりです。

- ASP.NET Core
- .NET 10
- EF Core
- SQLite
- Docker
- HTTP 取得: `HttpClient`
- HTML 解析: AngleSharp

DISCAS の検索結果ページは `HttpClient` で取得できることを PoC で確認済みです。そのため、ブラウザ自動化は初期構成に含めません。静的 HTTP 取得では成立しない処理が後から判明した場合に限り Playwright 等を再検討します。

### 2.2 配置

単一の ASP.NET Core アプリケーションとして構成します。

- Web UI
- スクレイピング処理
- 定期実行
- SQLite への永続化
- 画像キャッシュ

を同一アプリケーション内で扱います。

定期実行専用 Worker や別コンテナは初期構成では作りません。単一インスタンス運用を前提とします。

### 2.3 認証とアクセス制御

DiscaScout 自身にはユーザー認証・ユーザー管理を実装しません。単一ユーザー用途を前提とし、必要なアクセス制御は Traefik、ネットワーク、その他の外部レイヤーで行います。

### 2.4 永続化とバックアップ

SQLite DB とローカル保存した画像を永続ボリュームへ保存します。

アプリケーション自身にはバックアップ機能を持たせません。バックアップはホスト側・ストレージ側で行います。

## 3. 実装順序

UI を先に作り込まず、データ取得と状態遷移の正しさを先に確立します。

1. DISCAS 実ページの技術調査 / スクレイパー PoC
2. ドメインモデルと DB スキーマ
3. `近日リリース` / `新作` の通常スクレイパー
4. 差分検出、Archive、ReviewReason、Artist Watch
5. アーティスト全作品収集
6. 画像ダウンロード / キャッシュ
7. Scheduler、Retry、ScrapeRun、Discord 通知
8. バックエンドテスト
9. 最小限の管理 UI
10. Inbox / Pickup / Catalog UI と表示調整

UI 実装へ本格的に移る前に、少なくとも次を自動テストで再現できる状態を目標とします。

- 全ページを取得してからカテゴリ単位でコミットできる
- 同一データを再取得しても不要な変更が発生しない
- タイトル変更を検出できる
- 一時的な消失と連続消失を区別できる
- Archive 後の再出現を検出できる
- Artist Watch の新規一致を検出できる
- 失敗・異常なクロール結果を部分コミットしない

## 4. スクレイピング単位

通常収集対象は次の 2 カテゴリです。

- `Upcoming` — DISCAS の `近日リリース`
- `New` — DISCAS の `新作`

両カテゴリは独立したクロール・コミット単位とします。

例えば Upcoming が成功して New が失敗した場合、Upcoming はコミットしてよい一方、New は前回成功時の状態を維持します。

1 カテゴリについては、全ページの取得・解析・検証が成功するまで DB の現在状態へ反映しません。途中ページまで取得できた状態で部分コミットすることは禁止します。

## 5. スクレイピング結果モデル

検索結果から DB 更新前の中間データとして、概ね次の情報を保持します。

```csharp
public sealed record ScrapedDisc(
    string DiscasId,
    string ProductUrl,
    string Title,
    string Artist,
    string? ImageUrl,
    DateOnly? RentalStartDate,
    DiscSourceCategory Category,
    int SourceRank);
```

`RentalStartDate` は検索結果一覧から取得できない可能性が確認されているため nullable とします。レンタル開始日だけを取得するために商品詳細ページを全件クロールすることはしません。

商品同一性には DISCAS の安定した識別子を使用します。タイトルとアーティストの組み合わせを合成キーにはしません。タイトル変更そのものを検出対象にするためです。

## 6. データモデル

以下は論理モデルです。実際の EF Core エンティティ設計時には、制約・Index・Navigation 等を具体化します。

### 6.1 Disc

CD 自体を表します。

主な項目:

- `Id`
- `DiscasId`
- `ProductUrl`
- `Title`
- `NormalizedTitle`
- `Artist`
- `NormalizedArtist`
- `ImageUrl`
- `ImagePath`
- `RentalStartDate`
- `FirstSeenAt`
- `LastSeenAt`
- `LastUpdatedAt`
- `IsArchived`
- `NeedsReview`
- `LastReviewedAt`
- `IsRented`

観測済みの Disc は原則として物理削除しません。

### 6.2 DiscSource

Disc がどの通常収集カテゴリに現在存在しているかを表します。

主な項目:

- `DiscId`
- `Category`
- `SourceRank`
- `IsActive`
- `MissingCount`
- `LastSeenAt`

1 枚の Disc が Upcoming と New の双方に関連する期間を許容するため、Disc とカテゴリを 1:N ではなく別 Relation として管理します。

### 6.3 DiscReviewReason

現在ユーザー確認が必要な理由を保持します。

理由:

- `NEW`
- `TITLE_CHANGED`
- `ARTIST_MATCHED`
- `REAPPEARED`

複数理由が同時に存在することを許容します。確認済みにすると現在の理由を解消します。

### 6.4 DiscChangeHistory

意味のあるメタデータ変更を記録します。

主な項目:

- `DiscId`
- `Field`
- `OldValue`
- `NewValue`
- `ChangedAt`

正規化後の値が変化した場合のみ記録します。表示上の空白や Unicode 表現だけが変わった場合は履歴を増やしません。

### 6.5 ArtistSetting

Artist Watch と全作品収集の設定を統合して管理します。

主な項目:

- `Id`
- `ArtistName`
- `NormalizedArtistName`
- `MatchType` (`Exact` / `Contains`)
- `WatchEnabled`
- `CollectFullCatalog`
- `IsArchived`
- `CreatedAt`

既定の一致方法は `Exact` とします。正規表現は初期仕様に含めません。

### 6.6 DiscArtistWatchMatch

Disc と ArtistSetting の Watch 一致関係を保持します。

現在一致しているかだけでなく、過去に一致していたことと現在一致していることを区別できる情報を保持します。

これにより、同じ一致を毎週 `ARTIST_MATCHED` として再通知することを防ぎます。

### 6.7 DiscArtistCatalog

アーティスト全作品収集によって取得した Disc と ArtistSetting の関係を保持します。

主な項目:

- `DiscId`
- `ArtistSettingId`
- `IsActive`
- `LastSeenAt`

Disc の現在の Artist 表示が後から変わっても、「この ArtistSetting のカタログ収集で取得された」という provenance を失わないため、明示的な Relation とします。

### 6.8 ScrapeRun

スクレイピング実行履歴を恒久的に保存します。

主な項目:

- 実行日時
- 実行種別 (`Scheduled` / `Manual` / `Retry`)
- Category
- 成否
- 取得件数
- 解析件数
- 新規件数
- 更新件数
- 所要時間
- 簡潔な失敗理由

Stack trace や詳細な HTTP 情報は通常のアプリケーションログへ出力し、ScrapeRun には運用上必要な要約を保存します。

## 7. 文字列正規化

Title と Artist には共通の正規化を適用します。

- Unicode NFKC
- 前後空白除去
- 連続する空白を 1 個へ統合
- 大文字小文字を区別しない比較

カナ変換や句読点除去などの積極的な正規化は行いません。

表示文字列が変化しても正規化結果が同じ場合:

- 最新表示文字列へ更新する
- `TITLE_CHANGED` は付与しない
- `DiscChangeHistory` は追加しない

正規化結果が変化した場合のみ意味のある変更として扱います。

## 8. 通常カテゴリのライフサイクル

カテゴリごとの成功クロール時に DiscSource を更新します。

- 今回存在した: `MissingCount = 0`, `IsActive = true`
- 今回存在しなかった: `MissingCount += 1`
- 2 回連続で存在しなかった: `IsActive = false`

失敗したクロールでは MissingCount を変更しません。

すべての DiscSource が Inactive になった Disc は `IsArchived = true` とします。

Archived Disc が再度通常カテゴリに現れた場合:

- 対応 DiscSource を Active へ戻す
- Disc を Archive 解除する
- レンタル済みでなければ `REAPPEARED` を付与する

Archive は論理状態であり、Disc・画像・履歴を削除しません。

## 9. 未チェック / 確認済み

確認状態は Disc 単位です。

- `NeedsReview`
- `LastReviewedAt`
- 未解消の `DiscReviewReason`

確認済みにすると:

- 現在の ReviewReason を解消する
- `NeedsReview = false`
- `LastReviewedAt` を更新する

確認操作そのものの完全な履歴は初期仕様では保持しません。

### 9.1 再確認が必要になる変更

レンタル済みでない場合、次のイベントで未チェックへ戻します。

- 新規 Disc: `NEW`
- 意味のある Title 変更: `TITLE_CHANGED`
- 新たな Artist Watch 一致: `ARTIST_MATCHED`
- Archive 後の再出現: `REAPPEARED`

Artist の変更だけでは再確認にしません。ただし変更後に新たな Artist Watch 条件へ一致した場合は `ARTIST_MATCHED` とします。

RentalStartDate や画像だけの変更では再確認にしません。

## 10. レンタル済み状態

初期仕様では `Disc.IsRented` の boolean のみを保持します。

レンタル履歴、レンタル日、複数回レンタル等は扱いません。

`借りた` 操作時:

- `IsRented = true`
- ReviewReason を解消
- `NeedsReview = false`
- `LastReviewedAt` を更新

レンタル済み Disc は、その後タイトル変更・再出現・Watch 一致が発生しても Inbox へ再表示しません。ただし意味のある変更履歴は記録して構いません。

レンタル済みでも Artist Watch の Pickup 対象からは除外しません。

`IsRented` を false に戻す操作は詳細画面で確認付きとします。false に戻しただけでは確認状態を変更しません。必要なら別途「未チェックに戻す」を操作します。

## 11. Artist Watch

ArtistSetting を追加・編集した時点で、ローカル DB に存在する Disc を再評価します。

設定適用前には、既存一致件数や既に確認済みの一致 Disc を確認できるようにし、必要なら既存一致 Disc を未チェックへ戻す選択肢を用意します。

同一条件への継続一致では毎回 `ARTIST_MATCHED` を発生させません。

Artist metadata の変更によって新規一致した場合は、レンタル済みでなければ `ARTIST_MATCHED` を付与します。

Watch を Disabled にした場合は Pickup から除外しますが、設定と過去の一致情報は保持します。

## 12. アーティスト全作品収集

`ArtistSetting.CollectFullCatalog` を有効にした Artist について、DISCAS のアーティスト検索結果を全ページ取得します。

DISCAS の人物・アーティスト検索は、検索対象人物と商品に表示される Artist が一致しない結果を返す場合があることを確認しています。そのため検索結果を信用せず、取得後に表示 Artist を `Exact` または `Contains` 条件でローカル再判定します。

### 12.1 実行タイミング

- `CollectFullCatalog` を有効化したとき: 初回全取得
- その後: 定期実行しない
- Web UI の `全作品を再取得` で手動更新

ArtistName または MatchType を変更し、CollectFullCatalog が有効な場合は全作品を再取得します。

### 12.2 初回 import

全作品収集だけで初めて見つかった Disc には `NEW` を付与せず、通常 Inbox へは表示しません。

Artist Catalog は主としてレンタル済み / 未レンタルの在庫確認に使用します。

### 12.3 再取得時の消失

全作品の再取得が成功したにもかかわらず以前の Catalog Disc が存在しない場合、`DiscArtistCatalog.IsActive = false` とします。

通常カテゴリと異なり、全作品収集は手動・明示的な全件取得なので 1 回の不在で Inactive とします。

### 12.4 設定停止・Archive

CollectFullCatalog を off にしても取得済み Relation や Disc を削除しません。

ArtistSetting 自体も物理削除せず Archive 可能とします。Archive 時は Watch / Catalog 処理を停止し、設定画面の通常一覧から隠します。

復元時には以前の WatchEnabled / CollectFullCatalog 状態を復元します。Watch は直ちにローカル再評価しますが、全作品の自動再取得は行わず手動操作とします。

## 13. 画像

Disc には外部 `ImageUrl` とローカル `ImagePath` を保持します。

画像は次の場合に取得します。

- ローカル画像が存在しない Disc に有効な ImageUrl が現れた
- ImageUrl が変更された

同一 URL の画像内容が変わったかを検出するために毎回再取得することはしません。

URL 変更時は:

1. 新画像をダウンロード
2. 正常に保存できたことを確認
3. DB の参照を新画像へ切り替える
4. 旧画像を削除

新画像の取得に失敗した場合は旧画像を保持します。

画像取得失敗は CD メタデータやカテゴリ全体のコミットを失敗させません。

Archived / Catalog Inactive になった Disc の画像も保持します。

## 14. Scheduler と排他

定期実行は ASP.NET Core アプリ内の BackgroundService 等で実装します。

Web UI から設定する項目:

- Enabled
- 曜日
- 時刻

Cron 式はユーザー設定として公開しません。

スクレイピングジョブはアプリケーション全体で同時に 1 つだけ実行します。

実行中は Web UI の手動取得ボタンを無効化し、現在処理中のカテゴリや進捗を表示します。

通常の手動取得は Upcoming と New の両方を対象とします。失敗した ScrapeRun からの再実行では、失敗カテゴリだけを再実行できるようにします。

## 15. Retry

1 回のカテゴリ取得中では短時間の HTTP retry を行います。それでも失敗した場合、カテゴリ単位で次の retry を予定します。

1. 3 時間後
2. 翌日に 1 回
3. それでも失敗したら次回の通常定期実行まで停止

予定 retry より先に手動実行または次の有効な実行が成功した場合、古い retry はキャンセルします。

## 16. クロール結果の検証

HTTP 200 だけを成功条件にしません。DOM 変更や異常レスポンスを正常データとして DB へ反映しないため、コミット前に妥当性を検証します。

少なくとも次を異常として扱います。

- parsed result が 0 件
- 必須フィールド欠落や解析失敗が許容範囲を超えた
- ページが示す総件数と実際の全ページ解析件数が一致しない
- 前回成功時と比較して総件数が不自然に大幅減少した

件数減少の具体的なしきい値は実データを継続観測してから決定します。

異常なクロールは DB へコミットせず、通常の失敗と同じ retry / Discord 通知フローへ流します。

## 17. Discord 通知

Webhook URL と通知モードを設定可能にします。

モード:

- Off
- Failure only（既定）
- Success + Failure

成功通知には、通常カテゴリの取得件数、新規件数、タイトル変更件数、Watch 新規一致件数、未チェック件数などの集計を含めます。

すべての新規 CD を列挙することはせず、Artist Watch に新規一致した CD は通知内容へ含めます。

通知の有無にかかわらず ScrapeRun は保存します。

## 18. Web UI

PC の Chrome / Edge を主対象とします。初期仕様ではモバイル・レスポンシブ対応を要件としません。

### 18.1 メイン画面

高密度な一覧を基本とし、大型カード UI にはしません。

タブ:

- `未チェック N`
- `★ ピックアップ N`
- `全件`

主な検索・Filter:

- タイトル・アーティスト検索
- Category
- 状態
- Sort

1 行には概ね次を表示します。

- 100〜120px 程度の画像
- NEW / TITLE CHANGED / REAPPEARED / Pickup / Rented / Archived 等の badge
- Title
- Artist
- Title 変更時の旧 Title
- RentalStartDate
- Category
- `DISCASで開く` 外部リンク

行全体を DISCAS 外部リンクにはしません。Title からローカル詳細画面へ移動できる構成を想定します。

### 18.2 検索

Title と Artist を横断して部分一致検索します。

空白区切りの複数語は AND 条件とします。

例: `梶浦 OST` なら、Title + Artist を合わせた検索対象に `梶浦` と `OST` の双方が存在する Disc を返します。

通常は Active Disc のみ表示しますが、検索文字列が入力された場合は Archived Disc も自動的に検索対象へ含めます。

### 18.3 Pagination

すべての一覧を Pagination します。

Page size:

- 50
- 100
- 200
- 既定 50

選択値は LocalStorage に保存します。

`表示中のN件を確認済みにする` は現在ページかつ現在 Filter に一致する未チェック Disc のみを対象とし、確認 dialog を表示します。

個別確認では行を直ちに未チェック一覧から消し、短時間の Undo を提供します。

バックグラウンド取得完了による一覧の自動 refresh は行いません。SignalR や polling も初期仕様には含めません。

### 18.4 Sort

少なくとも次を用意します。

- 新規検出 / 更新順
- レンタル開始日の新しい順
- レンタル開始日の古い順
- Title
- Artist

RentalStartDate が取得できない場合でも、DISCAS 検索結果上の `SourceRank` を保持しているためソース順序を再現できます。

### 18.5 詳細画面

主な表示・操作:

- Image
- Title
- Artist
- RentalStartDate
- 現在 Category
- FirstSeen / LastSeen
- Change history
- Artist Watch match
- DISCAS 外部リンク
- Rented 状態編集
- `未チェックに戻す`

DISCAS 外部リンクは新しいタブで開きます。

## 19. Artist Catalog UI

Artist Catalog はレンタル在庫確認を主用途とし、既定で未レンタルを表示します。

Filter:

- 未レンタル
- レンタル済み
- すべて

Checkbox による複数選択と `選択したCDをレンタル済みにする` を提供します。

誤操作リスクが高いため「表示中をすべてレンタル済みにする」のような一括操作は初期仕様に含めません。

## 20. 将来検討事項

### 20.1 DISCAS レンタル履歴 import

DISCAS へログインしてレンタル履歴を自動取得する機能は初期仕様から除外します。

将来的には PC ブラウザ上でユーザーがレンタル履歴を表示し、ChatGPT / browser integration 等で抽出した CSV を DiscaScout へ import するワークフローを検討できます。

CSV schema や import 仕様は現時点では決定しません。

### 20.2 Playwright

現在の検索結果は HttpClient + AngleSharp で取得・解析できています。将来 DISCAS 側の実装変更により JavaScript 実行が必須になった場合のみ Playwright 等を再検討します。
