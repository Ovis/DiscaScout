# DiscaScout Current State / Handoff

最終更新: **2026-08-30**

この文書は、新しい開発セッションで過去の会話ログを読み直さなくても DiscaScout の現在地を把握できるようにするための引き継ぎ資料です。詳細な設計理由は `architecture.md`、DISCAS HTML の調査結果は `discas-scraping.md`、アクセス負荷に関する必須制約は `scraping-policy.md` を参照してください。

## 1. プロジェクト概要

DiscaScout は TSUTAYA DISCAS の CD「近日リリース」「新作」を定期取得し、ローカルで検索・確認・レンタル状態管理を行う単一ユーザー向け Web アプリケーションです。

通常収集では DISCAS 全 CD をミラーせず、観測した近日リリース・新作を蓄積します。指定したアーティストについてのみ Artist Catalog として全作品検索を行えます。

主な技術:

- ASP.NET Core / .NET 10
- EF Core / SQLite
- HttpClient + AngleSharp
- Docker
- 単一 Web アプリケーション / 単一インスタンス
- アプリ内 BackgroundService
- アプリ自身には認証を持たせず、ネットワーク / Traefik 等でアクセス制御

リポジトリ: `Ovis/DiscaScout`

命名:

- Solution: `DiscaScout.slnx`
- Web project: `DiscaScout.Web`
- Docker image: `disca-scout`
- SQLite: `discascout.db`

## 2. 実装済み機能

### 2.1 通常スクレイピング

- `Upcoming`（近日リリース）と `New`（新作）を全ジャンルで取得
- カテゴリごとに全ページ取得・完全性検証後に SQLite へ反映
- 片方のカテゴリが失敗しても、成功したカテゴリは独立してコミット
- DISCAS `titleID` を安定識別子として使用
- Windows-31J を CP932 として明示デコード
- PC 向け `.cd-product-item` のみ解析
- hidden `titleId` 列と解析結果の完全一致を検証
- GA 用メタデータから GenreLarge / GenreMiddle / GenreSmall を追加 HTTP なしで取得
- `【MAXI】` タイトル接頭辞から MAXI を判定

2026-08-29 の実地確認では全ジャンルで:

- New: 39 pages / 1,528 products
- Upcoming: 21 pages / 821 products

を完全取得できています。

### 2.2 差分・状態管理

`Disc` を中心に次を永続化しています。

- タイトル / アーティストと正規化値
- ジャンル
- 画像 URL / ローカル画像パス
- レンタル開始日
- MAXI / 2枚組
- 詳細説明
- 曲目
- FirstSeen / LastSeen / LastUpdated
- Archive
- NeedsReview / LastReviewedAt
- IsRented
- 通常カテゴリ Source
- ReviewReason
- メタデータ変更履歴
- Artist Watch / Artist Catalog 関係

ReviewReason:

- `NEW`
- `TITLE_CHANGED`
- `ARTIST_MATCHED`
- `REAPPEARED`

通常カテゴリで 2 回連続して見えなくなった Source を inactive とし、active な通常 Source がなくなった Disc を Archive します。失敗したクロールでは MissingCount を進めません。

レンタル済み Disc は新しい ReviewReason による Inbox 再オープンを抑止します。

### 2.3 Artist Watch / Artist Catalog

ArtistSetting は以下を持ちます。

- Artist name
- Exact / Contains
- Watch enabled
- Collect full catalog
- Archived

Artist Watch:

- 設定追加・変更時にローカル Disc を即時再評価
- 保存前に一致件数、確認済み件数、新規一致件数、再オープン可能件数をプレビュー
- ユーザーが選択した場合のみ既存一致 CD を未チェックへ戻す
- 現在一致と過去の一致履歴を区別

Artist Catalog:

- DISCAS のアーティスト検索を全ページ取得
- Artist 文字列を Exact / Contains で post-filter
- ArtistSetting と Disc の provenance を `DiscArtistCatalog` として保持
- 初回収集、条件変更時、手動再取得はバックグラウンド ManualWork として実行
- 周期的な全作品再取得は行わない
- Catalog-only Disc は通常の `NEW` として Inbox に入れない
- 後に通常カテゴリへ初登場した場合は `NEW`

### 2.4 詳細ページと詳細メタデータ

`/discs/{id}` を実装済みです。

表示:

- 基本メタデータ
- レンタル状態 / Review 状態
- Source 状態
- Artist Watch 履歴
- Artist Catalog membership
- メタデータ変更履歴
- 説明
- 曲目
- レンタル開始日
- MAXI / 2枚組
- 詳細取得状態

操作:

- 確認済み
- 未チェックへ戻す
- 借りた
- 未レンタルへ戻す

詳細ページ HTML からバックグラウンドで取得:

- `レンタル開始日：YYYY年MM月DD日`
- `作品詳細` ～ `ジャンル` の説明
- `曲目` ～ `記番` のトラック一覧
- `tx_item_info03.png` の有無による 2 枚組判定

`IsTwoDisc` は nullable で、null は「未確認」です。

詳細取得ポリシー:

- 未取得 Disc はバックグラウンド取得対象
- 詳細ページを開くと優先キューへ入れるが Web リクエスト自体は待たない
- 初回成功がレンタル開始前なら、レンタル開始日以降にもう一度取得
- レンタル開始日以降の成功で `DetailRefreshCompleted=true` とし通常は終了
- 失敗後は最低 6 時間空ける
- 通常スクレイピング / Artist Catalog 実行中は詳細取得を譲る
- 詳細取得は 1 件ずつ、さらに約 15 秒間隔

説明文は DB では原文を保持し、詳細画面の表示時だけ `。` の後に改行を入れます。

### 2.5 画像キャッシュ

画像は通常スクレイピング完了条件から分離されています。

- DB の ImageUrl / ImagePath 状態を暗黙の durable queue として使用
- 専用 `ImageCacheBackgroundService`
- 1 pass 開始時に pending ID の snapshot を作り、失敗した先頭画像が後続を飢餓させない
- 40 IDs / batch
- 最大 4 HTTP concurrent
- batch 間 2 秒
- 10 batch ごとに 5～20 秒のランダム追加待機
- 同一 URL / 既存ファイルは再取得しない
- URL 更新時は新画像取得成功後に DB を切り替え、旧画像を削除
- 取得失敗時は旧画像を保持
- 画像失敗はスクレイピング成功・失敗に影響させない

### 2.6 Scheduler / Retry / ManualWork

通常 schedule:

- Web UI から enabled / weekday / time を設定
- Asia/Tokyo 固定
- 初期値 disabled / Sunday 04:00
- 同日 catch-up

Retry:

- 通常失敗: +3 時間
- Retry #1 失敗: +1 日
- Retry #2 失敗: それ以上自動再試行しない
- Scheduled / Manual 成功時は該当カテゴリの pending retry を cancel

ManualWork:

- SQLite に Pending / Running / Completed / Failed を永続化
- FullScrape と ArtistCatalog
- Web 操作は enqueue して即応答
- BackgroundService が manual → due retry → scheduled の優先順で処理
- 起動時に中断された Running を Pending へ戻す
- 重複 ManualWork を防止
- 全 DISCAS scrape 系処理は `ScrapeExecutionGate` で単一実行

永続化する timestamp は SQLite で比較・ORDER BY 可能にするため **UTC DateTime**。UI では JST に変換します。

### 2.7 Discord 通知

`/settings` から設定します。環境変数を正とはしません。

SQLite に保存:

- Discord Webhook URL
- Notification mode
  - `Off`
  - `FailureOnly`（初期値）
  - `SuccessAndFailure`

通知対象:

- Scheduled / Manual / Retry のカテゴリ結果
- 失敗時の次回 Retry 予定
- Artist Catalog の手動取得失敗

画像キャッシュや個別詳細取得失敗は Discord 通知しません。

Webhook 送信失敗はログ警告に留め、本体の取得結果や Retry 制御へ波及させません。

設定画面から保存済み Webhook へテスト通知を送信できます。テスト通知は `Off` でも明示操作として送信可能です。

現状、成功通知には Artist Watch 新規一致件数を独立集計して含めていません。

## 3. Web UI の現在地

共通ナビゲーション:

- CD 一覧
- Artist 設定
- 運用
- 設定

`/discs`:

- タブ: 未チェック / Pickup / 全件
- タイトル検索とアーティスト検索を独立
- 各検索は空白区切り AND
- ジャンル exact filter（large / middle / small）
- アーティスト・ジャンル表示をクリックしてフィルター化
- MAXI / Album 除外
- レンタル状態 filter
- sort: updated / rental / title / artist
- 50 / 100 / 200 件、page size は localStorage
- rental sort は詳細由来日付が未取得なら active SourceRank を fallback
- フィルターを tab / paging で維持
- ReviewReason を日本語表示
- 近日リリース / 新作 / 準新作 / 旧作 badge
- MAXI / 2枚組 / 借りた / Archive / Pickup badge
- 現在ページの未チェックを一括確認済み
- 未レンタル CD をチェックボックスで複数選択し一括「借りた」
- 個別 Reviewed / Rented の直前 1 操作を Undo
- タイトルからローカル詳細へ遷移
- DISCAS への外部リンク

DISCAS CD レンタル区分は現在日付と RentalStartDate から動的計算し、DB に固定保存しません。

- レンタル開始前: 近日リリース
- 0～90 日: 新作
- 91～180 日: 準新作
- 181 日～: 旧作

## 4. 最重要: DISCAS アクセス負荷制御

この制約は今後の実装でも維持してください。高速化を理由に安易に緩和しません。

検索 HTML / Artist Catalog / 詳細 HTML は共有 `DiscasRequestThrottle` を通します。

- DISCAS HTML HTTP は **全体で直列**
- request start は最低 **2 秒**間隔
- HTML page request **10 件ごと**に次の request 前へ **5～20 秒のランダム追加待機**
- 通常 New / Upcoming / Artist Catalog / detail を同じ制約下に置く
- detail worker は scrape 実行中に譲る
- detail はさらに 1 件ずつ約 15 秒間隔

画像は HTML と別系統ですが bounded load とします。

- 最大 4 concurrent
- 40 IDs / batch
- batch 間 2 秒
- 10 batch ごとに 5～20 秒追加待機

検索結果 HTML だけで取得できる情報のために detail page を追加取得しないことも原則です。

## 5. 重要なドメイン仕様

### 文字列正規化

Title / Artist:

- Unicode NFKC
- trim
- whitespace collapse
- uppercase invariant

正規化後に同じ表示変更は履歴・再オープン対象にしません。

### Review

確認済み:

- NeedsReview=false
- LastReviewedAt 更新
- 現在の ReviewReason を削除

Rented:

- IsRented=true
- NeedsReview=false
- LastReviewedAt 更新
- ReviewReason 削除

未レンタルへ戻しても自動的に未チェックへは戻しません。必要なら別操作で `未チェックへ戻す` を使います。

### Catalog と通常 Source

Artist Catalog membership は通常 Source / Archive lifecycle と独立です。Catalog-only Disc は Archive 相当でも Pickup に表示し得ます。

## 6. 未実装・今後の候補

現時点で主要なバックエンドと日常操作 UI はかなり揃っています。次のセッションでは main の実装と Issue / PR 状態を確認してから優先順位を決めてください。

既知の候補:

- Discord 成功通知へ Artist Watch 新規一致件数を含めるか検討
- UI の最終的な見た目・操作性調整
- 異常クロール検出の閾値具体化（総件数の急減等）
- 必要に応じて運用テスト・E2E の追加
- ドキュメントと実装の継続同期

レンタル履歴の DISCAS ログイン連携は意図的に後回しです。将来は PC ブラウザで履歴を取得し、CSV 化してインポートする方式を候補としています。ログインセッションを DiscaScout の scraper に持たせる設計は現時点で採用していません。

## 7. Git / 開発運用上の注意

- `main` はアプリケーションコード
- `docs` は設計・調査資料専用の orphan branch
- 機能変更は原則 feature branch → PR → CI → ユーザーが確認して merge
- ユーザーから明示されない限り PR を勝手に merge しない
- GitHub へファイルを書き込む前に必ず対象 branch を作成・確認し、write API には branch を明示する
- 過去に誤って `main` へ直接 commit した事故があったため、default branch への暗黙書き込みは禁止

コードコメント方針:

- 日本語・常体
- コードの逐語訳ではなく「なぜ」を残す
- 新規 / 変更 class と主要 public / internal method には原則日本語 XML documentation
- 順序、互換性、安全性、負荷制御、retry、concurrency 等の設計意図は積極的にコメントする

## 8. 最近の主要 PR

- #15 UTC DateTime 化
- #16 ManualWork のバックグラウンド化
- #17 DISCAS 負荷制御強化・画像キャッシュ非同期化
- #18 CD 詳細ページ
- #19 詳細メタデータの低頻度バックグラウンド取得
- #20 CD 一覧の検索・Review UI 改善
- #21 Artist Watch 設定プレビュー
- #22 Discord scrape notifications
- #23 Discord test notification
- #24 複数 CD の一括「借りた」操作

**#24 まで main へ merge 済み**です。
