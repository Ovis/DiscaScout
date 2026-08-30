# DiscaScout Current State / Handoff

最終更新: **2026-08-30**

この文書は、新しい開発セッションで過去の会話ログを読み直さなくても DiscaScout の現在地を把握できるようにするための引き継ぎ資料です。詳細な設計理由は `architecture.md`、DISCAS HTML の調査結果は `discas-scraping.md`、ジャンルマスターは `genre-master.md`、アクセス負荷に関する必須制約は `scraping-policy.md` を参照してください。

## 1. プロジェクト概要

DiscaScout は TSUTAYA DISCAS の CD「近日リリース」「新作」を定期取得し、ローカルで検索・確認・レンタル状態管理を行う単一ユーザー向け Web アプリケーションです。

通常収集では DISCAS 全 CD をミラーせず、観測した近日リリース・新作を蓄積します。指定したアーティストについてのみ Artist Catalog として全作品検索を行えます。加えて、ログイン済み DISCAS のレンタル履歴をブラウザ拡張から JSON 化し、過去に借りた CD を取り込めます。

主な技術:

- ASP.NET Core MVC / .NET 10
- EF Core / SQLite
- HttpClient + AngleSharp
- Docker / Docker Compose
- 単一 Web アプリケーション / 単一インスタンス
- アプリ内 BackgroundService
- アプリ自身には認証を持たせず、ネットワーク / Traefik 等でアクセス制御

リポジトリ: `Ovis/DiscaScout`

命名:

- Solution: `DiscaScout.slnx`
- Web project: `DiscaScout.Web`
- Docker image: `disca-scout`
- SQLite: `discascout.db`

`main` の現在の基準は **PR #40 `Add DISCAS genre master and normalized genre filtering` マージ後**です。

## 2. 実装済み機能

### 2.1 通常スクレイピング

- `Upcoming`（近日リリース）と `New`（新作）を全ジャンルで取得
- カテゴリごとに全ページ取得・完全性検証後に SQLite へ反映
- 片方のカテゴリが失敗しても、成功したカテゴリは独立してコミット
- DISCAS `titleID` を安定識別子として使用
- Windows-31J を CP932 として明示デコード
- PC 向け `.cd-product-item` のみ解析
- hidden `titleId` 列と解析結果の完全一致を検証
- `【MAXI】` タイトル接頭辞から MAXI を判定

2026-08-29 の実地確認では全ジャンルで:

- New: 39 pages / 1,528 products
- Upcoming: 21 pages / 821 products

を完全取得できています。

### 2.2 スクレイピング件数安全装置

PR #26 で導入済みです。

- **0件取得は常に異常**として DB へ反映しない
- 同カテゴリの最後に DB 反映まで成功した `ScrapeRun` を正常基準とする
- 今回件数が正常基準の **70%未満**なら `CountDrop` 異常
- **70%ちょうどは正常**
- PageCount は判定には使わず、履歴・通知・確認 UI の参考情報として保存
- 異常として拒否した Run は次回の基準値にしない

失敗分類:

- `ScrapeFailureType.ProcessingError`
- `ScrapeFailureType.AbnormalCount`
  - `AbnormalCountReason.ZeroCount`
  - `AbnormalCountReason.CountDrop`

件数異常も通常の Retry フローへ接続します。

- 通常失敗 → 3時間後 Retry #1
- Retry #1 失敗 → 翌日 Retry #2
- Retry #2 失敗 → それ以上自動再試行しない

正当な大幅減少を確認した場合、`/settings` からカテゴリ別に「次回1回だけ急減を許可」できます。

- 0件は Override 不可
- Override は通信・解析失敗では消費しない
- 完全スナップショットの DB 反映成功後にのみ消費
- 有効化後は対象カテゴリだけ `CategoryScrape` ManualWork として即時 enqueue
- Override 利用成功は `CountDropOverrideUsed=true` として履歴に残る

### 2.3 差分・状態管理

`Disc` を中心に次を永続化しています。

- タイトル / アーティストと正規化値
- 正規化ジャンル参照 `GenreId`
- 画像 URL / ローカル画像パス / 詳細用画像 URL
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
- レンタル履歴インポート日時

ReviewReason:

- `NEW`
- `TITLE_CHANGED`
- `ARTIST_MATCHED`
- `REAPPEARED`

通常カテゴリで 2 回連続して見えなくなった Source を inactive とし、active な通常 Source がなくなった Disc を Archive します。失敗したクロールでは MissingCount を進めません。

レンタル履歴由来 Disc は通常 Source がなくても保持します。レンタル済み Disc は新しい ReviewReason による Inbox 再オープンを抑止します。

### 2.4 ジャンルマスター

PR #40 で、Disc に大・中・小ジャンル文字列を直接持たせる方式を廃止しました。

- DISCAS `genreAll.do` をジャンルマスターの正とする
- `Genre` を自己参照ツリーとして保持
- DISCAS の `G` パラメータを外部 ID として保存
- `Disc.GenreId` は最深ジャンルノードを参照
- マスター更新時に消えたジャンルは削除せず Inactive 化
- 再出現時は Reactivate
- 初回通常クロール前にジャンルマスターを準備
- `/settings` から手動更新可能
- ジャンルマスター更新にも安全装置があり、0件または既存 Active 件数の 75% 未満への急減は更新拒否

検索結果と詳細ページのジャンルは同じ完全パス Resolver で解決します。詳細ページ側と検索結果側が異なる場合は詳細ページ側を採用し警告ログを残します。

CD 一覧の大・中・小ジャンルフィルターは `GenreId` ベースです。親ジャンル選択時は子孫ジャンルを含めます。Inactive ジャンルでも既存 Disc が参照しているものはフィルターへ表示します。

詳細は `genre-master.md` を参照してください。

### 2.5 Artist Watch / Artist Catalog

ArtistSetting:

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

### 2.6 詳細ページと詳細メタデータ

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

- レンタル開始日
- 作品詳細
- 曲目
- 2枚組判定
- 詳細用ジャケット画像 URL
- ジャンル

`IsTwoDisc` は nullable で、null は「未確認」です。

詳細取得ポリシー:

- 未取得 Disc はバックグラウンド取得対象
- 詳細ページを開くと優先キューへ入れるが Web リクエスト自体は待たない
- レンタル履歴インポート由来で未取得の Disc は通常未取得 Disc より優先
- 初回成功がレンタル開始前なら、レンタル開始日以降にもう一度取得
- レンタル開始日以降の成功で `DetailRefreshCompleted=true`
- 失敗後は最低 6 時間空ける
- 通常スクレイピング / Artist Catalog 実行中は詳細取得を譲る
- 詳細取得は 1 件ずつ、さらに約 15 秒間隔

`/operations` では詳細補完進捗として、未完了総数、現在取得可能、失敗後6時間待機、レンタル開始待ち等を表示します。

### 2.7 画像キャッシュ

画像は通常スクレイピング完了条件から分離されています。

- DB の ImageUrl / ImagePath 状態を durable queue 相当として利用
- 専用 `DiscImageCacheBackgroundService`
- 1 pass 開始時に pending ID の snapshot を作成
- 40 IDs / batch
- 最大 4 HTTP concurrent
- batch 間 2 秒
- 10 batch ごとに 5～20 秒のランダム追加待機
- 同一 URL / 既存ファイルは再取得しない
- URL 更新時は新画像取得成功後に DB を切り替え、旧画像を削除
- 取得失敗時は旧画像を保持
- 画像失敗はスクレイピング成功・失敗に影響させない

レンタル履歴由来 Disc の詳細ページから得た MX ジャケット URL は詳細画面用に保持し、一覧キャッシュには対応する SX URL を使用します。

### 2.8 Scheduler / Retry / ManualWork

通常 schedule:

- Web UI から enabled / weekday / time を設定
- Asia/Tokyo 固定
- 初期値 disabled / Sunday 04:00
- 同日 catch-up

ManualWork:

- SQLite に Pending / Running / Completed / Failed を永続化
- `FullScrape` / `ArtistCatalog` / `CategoryScrape`
- Web 操作は enqueue して即応答
- BackgroundService が manual → due retry → scheduled の優先順で処理
- 起動時に中断された Running を Pending へ戻す
- 重複 ManualWork を防止
- 全 DISCAS scrape 系処理は `ScrapeExecutionGate` で単一実行

永続化する timestamp は SQLite で比較・ORDER BY 可能にするため **UTC DateTime**。UI では JST に変換します。

### 2.9 Discord 通知

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

成功通知には PR #27 以降、**その取得で新たに Artist Watch へ一致した CD 件数**も含みます。同じ CD が複数 Watch に一致しても 1 件として集計します。

画像キャッシュや個別詳細取得失敗は Discord 通知しません。Webhook 送信失敗はログ警告に留め、本体の取得結果や Retry 制御へ波及させません。

### 2.10 レンタル履歴インポート

PR #31 以降で実装済みです。

`/operations` から、次形式の JSON をインポートできます。

```json
[
  {
    "titleId": "0000102452",
    "title": "断絶",
    "artist": "井上陽水"
  }
]
```

- `titleId` 単位で冪等
- 既存 Disc は重複作成しない
- 取り込んだ Disc を `IsRented=true` / `NeedsReview=false` にする
- 履歴にしか存在しない CD も新規 Disc として作成
- `RentalHistoryImportedAt` で provenance を保持
- 後から通常クロールで同じ titleID が見つかれば既存 Disc へ Source と正式メタデータを追加
- 未取得 Disc は詳細取得優先対象

レンタル履歴 JSON を作るため、`tools/discas-rental-history-exporter/` に Chrome Manifest V3 拡張があります。

拡張の主な仕様:

- ログイン済み DISCAS レンタル履歴ページから開始
- 総件数・総ページ数を動的取得
- 全ページを直列取得
- 最低 2 秒間隔、10 ページごとに 5～20 秒追加待機
- 失敗時は 10秒 → 30秒 → 60秒で最大3回 Retry
- `chrome.storage.local` へページ単位の進捗を保存
- CD 行だけ抽出
- 全履歴行数と DISCAS 表示総件数で完全性検証
- 最終 JSON は `titleId` で重複排除
- Cookie / 認証情報は保存・出力しない

## 3. Web UI の現在地

PR #28 で Razor Pages から **ASP.NET Core MVC（Controller + Razor View）** へ移行済みです。GET URL は維持しています。

共通ナビゲーション:

- CD 一覧
- Artist 設定
- 運用
- 設定

`/discs`:

- タブ: 未チェック / Pickup / レンタル済み / 全件
- 未チェック専用フィルター: すべて / 近日リリース / 新着 / Artist Watch
- タイトル検索とアーティスト検索を独立
- タイトル検索は任意で「作品詳細」「曲目」も対象にできる
- 各検索は空白区切り AND
- 大 / 中 / 小ジャンルの階層フィルター
- アーティスト・ジャンル表示をクリックしてフィルター化
- MAXI / Album 除外は折りたたみの形式フィルター
- レンタル状態 filter
- sort: updated / rental / title / artist
- 50 / 100 / 200 件、page size は localStorage
- rental sort は詳細由来日付が未取得なら active SourceRank を fallback
- フィルターを tab / paging で維持
- ReviewReason を日本語表示
- 近日リリース / 新作 / 準新作 / 旧作 badge
- MAXI / 2枚組 / 借りた / Archive / Pickup badge
- 現在ページの未チェックを一括確認済み
- 未レンタル CD を複数選択し一括「借りた」
- 個別 Reviewed / Rented の直前 1 操作を Undo
- タイトルからローカル詳細へ遷移
- DISCAS への外部リンク
- ページ番号を直接選択できるページャー。現在ページ前後2ページ、先頭・末尾、`…` を表示

DISCAS CD レンタル区分は現在日付と RentalStartDate から動的計算し、DB に固定保存しません。

- レンタル開始前: 近日リリース
- 0～90 日: 新作
- 91～180 日: 準新作
- 181 日～: 旧作

`/operations`:

- 手動スクレイピング
- ManualWork 状態 / 履歴
- ScrapeRun 履歴
- 詳細メタデータ補完進捗
- レンタル履歴 JSON インポート

`/settings`:

- Schedule
- Discord Webhook / 通知モード / テスト通知
- 通常スクレイピング件数安全装置 / Override
- ジャンルマスター状態 / 手動更新

## 4. 最重要: DISCAS アクセス負荷制御

この制約は今後の実装でも維持してください。高速化を理由に安易に緩和しません。

検索 HTML / Artist Catalog / 詳細 HTML / ジャンルマスター取得は共有 `DiscasRequestThrottle` の方針下で扱います。

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

検索結果 HTML や既存のマスターで取得できる情報のために detail page を追加取得しないことも原則です。

レンタル履歴 exporter もログイン済みブラウザから DISCAS へアクセスするため、同様に直列・最低2秒・10ページごとの追加待機を守ります。

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

### Rental history と通常 Source

レンタル履歴由来 Disc は通常 Source がなくても消しません。後から通常クロールで同じ titleID を観測した場合は同じ Disc へ統合します。

### Genre

ジャンル文字列を Disc に直接保存する設計は廃止済みです。`Genre` マスターを正とし、`Disc.GenreId` から階層を解決します。

## 6. 配置 / Docker

PR #36 で Docker Compose 運用構成を追加済みです。

- .NET 10 multi-stage `Dockerfile`
- `compose.yaml`
- ホスト 8080 → コンテナ Web
- `restart: unless-stopped`
- `TZ=Asia/Tokyo`
- `/health` ヘルスチェック
- リポジトリ直下 `./data` を `/app/data` へ bind mount
  - `data/discascout.db`
  - `data/images/`

実環境で `docker compose up -d --build` による起動確認済みです。

## 7. 未実装・今後の候補

主要なバックエンドと日常操作 UI、レンタル履歴取込、Docker 配置まで揃っています。

現時点で「次に必ず実装する」と確定している大きな機能はありません。今後の候補は:

- 実運用での UI / 操作性の最終調整
- 長期運用で判明したスクレイピング・ジャンル解決の例外対応
- 必要に応じた運用テスト / E2E の追加
- レンタル履歴 exporter の実サイト変化への追従
- ドキュメントと実装の継続同期

DISCAS のログインセッションを DiscaScout 本体 scraper に持たせる設計は採用していません。認証が必要なレンタル履歴取得はブラウザ拡張側に分離します。

## 8. Git / 開発運用上の注意

- `main` はアプリケーションコード
- `docs` は設計・調査資料専用の orphan branch
- 機能変更は原則 feature branch → PR → CI → ユーザーが確認して merge
- **`docs` branch のドキュメント更新は PR 不要で直接 commit してよい**
- ユーザーから明示されない限り PR を勝手に merge しない
- GitHub へファイルを書き込む前に対象 branch を確認し、write API には branch を明示する
- default branch への暗黙書き込みは禁止
- CI は PR 番号 / ref 単位の concurrency を持ち、新しい実行開始時に同一グループの古い実行を cancel する

コードコメント方針:

- 日本語・常体
- コードの逐語訳ではなく「なぜ」を残す
- 新規 / 変更 class と主要 public / internal method には原則日本語 XML documentation
- 順序、互換性、安全性、負荷制御、retry、concurrency 等の設計意図は積極的にコメントする

## 9. 最近の主要 PR

- #26 scrape count anomaly guard — **merged**
- #27 Artist Watch 新規一致件数を成功通知へ追加 — **merged**
- #28 Razor Pages → ASP.NET Core MVC — **merged**
- #29 詳細取得進捗を運用画面へ追加 — **merged**
- #30 タイトル検索を作品詳細・曲目へ拡張 — **merged**
- #31 DISCAS レンタル履歴インポート — **merged**
- #32 CI の superseded run 自動キャンセル — **merged**
- #33 詳細 Razor View 整形 — **merged**
- #34 レンタル履歴 exporter Chrome 拡張 — **merged**
- #35 レンタル済みタブ / 未チェック分類フィルター — **merged**
- #36 Docker deployment configuration — **merged**
- #37 CD 一覧ページャー改善 — **merged**
- #38 レンタル履歴由来 Disc の詳細ジャンル補完 — **merged**
- #39 detail genre parser 修正 — close / **未マージ**。後続 #40 のジャンルマスター実装で問題領域を再設計
- #40 DISCAS genre master / normalized genre filtering — **merged**

現在の `main` は **#40 まで反映済み**です。
