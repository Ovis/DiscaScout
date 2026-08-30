# DiscaScout Architecture

最終更新: **2026-08-30 / main PR #40 反映済み**

この文書は DiscaScout の現在の設計と主要な設計判断をまとめたものです。実装の最新状態は `current-state.md`、DISCAS HTML の解析仕様は `discas-scraping.md`、ジャンルマスターは `genre-master.md`、アクセス負荷制御は `scraping-policy.md` を参照してください。

## 1. 目的

DiscaScout は、TSUTAYA DISCAS の CD「近日リリース」「新作」を定期収集し、過去に観測した作品やユーザー自身の確認・レンタル状態と組み合わせて管理するローカル Web アプリケーションです。

主な用途:

- `近日リリース` / `新作` の定期収集
- 前回確認後に追加・変更された CD の確認
- Artist Watch による指定アーティストの新着検出
- Artist Catalog による指定アーティストの全作品収集
- レンタル済み CD の管理
- DISCAS レンタル履歴のインポート
- 過去に観測した CD の検索

DISCAS 全 CD の恒久的なミラーを作ることは目的としません。通常収集では観測した近日リリース・新作だけを蓄積し、全作品検索はユーザーが指定したアーティストに限定します。

## 2. 基本構成

### 2.1 技術スタック

- ASP.NET Core MVC
- .NET 10
- EF Core
- SQLite
- HttpClient
- AngleSharp
- Docker / Docker Compose

ブラウザ自動化は DiscaScout 本体の通常スクレイピングには使用しません。認証が必要なレンタル履歴取得だけは、ログイン済みブラウザ上で動く Chrome Manifest V3 拡張へ分離しています。

### 2.2 アプリケーション構成

単一 ASP.NET Core アプリケーション / 単一インスタンス運用です。

主な project:

- `DiscaScout.Core` — ドメインエンティティ、enum 等
- `DiscaScout.Application` — 差分反映、Artist Watch、詳細補完、レンタル履歴取込等のユースケース
- `DiscaScout.Persistence` — EF Core / SQLite
- `DiscaScout.Scraping` — DISCAS HTTP 取得・解析・Throttle・ジャンル解決
- `DiscaScout.Web` — MVC Controller / Razor View / BackgroundService
- `DiscaScout.ScraperProbe` — 調査・検証用

Web UI、Scheduler、ManualWork、Retry、詳細取得、画像キャッシュは同じプロセスで動作します。別 Worker / 別コンテナへ分離しません。

### 2.3 MVC

PR #28 で Razor Pages から ASP.NET Core MVC へ移行済みです。

公開 GET URL:

- `/discs`
- `/discs/{id}`
- `/artists`
- `/operations`
- `/settings`

Controller と ViewModel は画面単位で分離し、共通ナビゲーションは shared View に置きます。

### 2.4 認証とアクセス制御

DiscaScout 自身にはユーザー認証・ユーザー管理を持たせません。単一ユーザー用途を前提とし、Traefik やネットワーク境界でアクセスを制限します。

## 3. 永続化

SQLite を唯一の永続 DB とします。timestamp は SQLite で比較・ORDER BY 可能にするため UTC `DateTime` とし、表示時に JST へ変換します。

Docker 運用時はリポジトリ直下の `./data` を `/app/data` へ bind mount します。

- `data/discascout.db`
- `data/images/`

バックアップ機能はアプリケーションへ持たせず、ホスト / ストレージ側で行います。

## 4. Disc と通常 Source

### 4.1 Disc

`Disc` は DISCAS の CD 商品自体を表します。安定識別子は DISCAS `titleID` です。タイトルとアーティストの組み合わせは識別子にしません。

主な情報:

- `DiscasId`
- `ProductUrl`
- Title / NormalizedTitle
- Artist / NormalizedArtist
- `GenreId`
- `ImageUrl` / `ImagePath`
- `DetailImageUrl`
- `RentalStartDate`
- `IsMaxi`
- `IsTwoDisc`
- Description / Tracks
- FirstSeen / LastSeen / LastUpdated
- `IsArchived`
- `NeedsReview`
- `LastReviewedAt`
- `IsRented`
- `RentalHistoryImportedAt`
- 詳細取得状態

観測済み Disc は原則として物理削除しません。

### 4.2 DiscSource

通常収集カテゴリとの関係を Disc 本体から分離します。

カテゴリ:

- `Upcoming`
- `New`

1 枚の Disc が両カテゴリへ同時に存在し得るため、カテゴリ状態は Relation として保持します。

成功クロール時:

- 今回存在 → `MissingCount=0`, Active
- 今回不在 → `MissingCount += 1`
- 2 回連続不在 → Inactive

失敗クロールでは MissingCount を進めません。

すべての通常 Source が Inactive になった Disc は Archive します。ただしレンタル履歴インポート由来 Disc は通常 Source がなくても保持します。

## 5. Review モデル

現在確認が必要な理由を `DiscReviewReason` として複数保持できます。

- `NEW`
- `TITLE_CHANGED`
- `ARTIST_MATCHED`
- `REAPPEARED`

確認済み操作:

- `NeedsReview=false`
- `LastReviewedAt` 更新
- 現在の ReviewReason を削除

借りた操作:

- `IsRented=true`
- `NeedsReview=false`
- `LastReviewedAt` 更新
- ReviewReason 削除

レンタル済み Disc は後続イベントによる Inbox 再オープンを抑止します。未レンタルへ戻しても自動的には未チェックへ戻しません。

## 6. 文字列正規化と変更履歴

Title / Artist は共通で以下を適用します。

- Unicode NFKC
- trim
- whitespace collapse
- uppercase invariant

表示文字列が変わっても正規化結果が同じ場合は意味のある変更として扱いません。正規化結果が変化した場合のみ変更履歴と `TITLE_CHANGED` の対象になります。

Artist 変更だけでは原則 Review を再オープンしません。ただし変更後に新たな Artist Watch へ一致した場合は `ARTIST_MATCHED` を付与します。

## 7. ジャンルモデル

PR #40 で旧 `GenreLarge / GenreMiddle / GenreSmall` 文字列列を廃止しました。

### 7.1 Genre

DISCAS `genreAll.do` をジャンル体系の正とします。

`Genre` は自己参照ツリーです。

- DISCAS `G` パラメータを外部 ID として保持
- Parent / Children で階層化
- Active / Inactive を保持
- 消えたジャンルは削除せず Inactive 化
- 再出現時は Reactivate

`Disc.GenreId` はその Disc に解決できた最深ジャンルノードを参照します。

### 7.2 マスター更新

- 初回通常クロール前にジャンルマスターを準備
- `/settings` から手動更新可能
- 0件は異常
- 既存 Active 件数の 75% 未満へ急減した場合も更新拒否

古い DB のジャンル文字列から新マスターを推測移行する設計は採用していません。本番運用前で DB 破棄可能という前提で、マスターは DISCAS 側から再構築します。

### 7.3 ジャンル解決

検索結果と詳細ページは共通 Resolver で完全パスをジャンルマスターへ解決します。

詳細ページと検索結果で異なるジャンルが得られた場合は詳細ページ側を採用し、差異を警告ログに残します。

一覧フィルターは `GenreId` ベースです。親ジャンル選択時は子孫ジャンルを含めて検索します。Inactive でも既存 Disc が参照中の Genre は選択肢に残します。

詳細は `genre-master.md` を参照してください。

## 8. 通常スクレイピング

通常収集対象は `Upcoming` と `New` です。カテゴリごとに独立した完全スナップショットとして処理します。

1. 全ページ HTTP 取得
2. 解析
3. hidden `titleId` と解析結果の完全性検証
4. 件数安全装置
5. DB 反映

1 カテゴリ内では途中ページまでの部分コミットを行いません。Upcoming 成功 / New 失敗のようにカテゴリ間では独立してコミットできます。

### 8.1 件数安全装置

通常カテゴリでは:

- 0件を常に拒否
- 最後の DB 反映成功 Run の件数に対し 70% 未満を拒否
- 70% ちょうどは許可

拒否された Run は次回基準になりません。

正当な急減はカテゴリ別の 1 回限り Override で受け入れられますが、0件は Override 不可です。Override は DB 反映成功後にのみ消費されます。

## 9. DISCAS アクセス負荷制御

最重要の固定制約です。詳細は `scraping-policy.md` を参照してください。

HTML 系アクセス:

- 全体で直列
- request start は最低 2 秒間隔
- 10 request ごとに 5～20 秒のランダム追加待機

同じ制御対象:

- Upcoming / New
- Artist Catalog
- Detail page
- ジャンルマスター取得

詳細 worker は scrape 実行中に譲り、さらに CD ごとに約 15 秒空けます。

画像は別系統ですが bounded load:

- 40 IDs / batch
- 最大 4 concurrent
- batch 間 2 秒
- 10 batch ごとに 5～20 秒追加待機

検索結果やジャンルマスターで得られる情報のために detail page を追加取得しないことを原則とします。

## 10. Artist Watch / Artist Catalog

### 10.1 ArtistSetting

1 つの設定で Watch と Catalog の両方を管理します。

- Artist name
- Exact / Contains
- Watch enabled
- Collect full catalog
- Archived

### 10.2 Artist Watch

設定保存時にローカル Disc を即時再評価します。

保存前には:

- 一致件数
- 確認済み件数
- 新規一致件数
- 再オープン可能件数

をプレビューします。

現在一致と過去一致を Relation で区別し、同じ一致を繰り返し `ARTIST_MATCHED` として扱わないようにします。

### 10.3 Artist Catalog

DISCAS アーティスト検索の全ページを取得し、結果 Artist 文字列を Exact / Contains で post-filter します。

`DiscArtistCatalog` に ArtistSetting と Disc の provenance を保持します。

- 初回収集
- 条件変更時
- 手動再取得

は ManualWork としてバックグラウンド実行します。周期的な全作品再取得は行いません。

Catalog-only Disc は通常 Source lifecycle と独立し、通常の `NEW` として Inbox へ入れません。

## 11. 詳細メタデータ

詳細ページは低頻度の BackgroundService で補完します。

取得対象:

- レンタル開始日
- 作品詳細
- 曲目
- 2枚組
- 詳細ジャケット URL
- ジャンル

ポリシー:

- 未取得 Disc は対象
- ローカル詳細画面を開くと優先要求するが HTTP request 自体は待たない
- レンタル履歴由来未取得 Disc を優先
- 初回成功がレンタル開始前なら開始日以降にもう 1 回取得
- 開始日以降の成功で完了
- 失敗後は最低 6 時間待機
- scrape 実行中は譲る
- 1 件ずつ約 15 秒間隔

`IsTwoDisc=null` は未確認を意味します。

## 12. 画像キャッシュ

画像取得は通常 scrape 成功条件から切り離します。

DB の `ImageUrl / ImagePath` 状態を durable queue 相当として使い、専用 BackgroundService が補完します。

失敗画像が後続を止めないよう、pass 開始時に pending ID の snapshot を作ります。

URL 更新時は新画像取得成功後に DB を切り替え、旧画像を削除します。失敗時は旧画像を保持します。

レンタル履歴由来 Disc の詳細ページから MX 画像 URL を得た場合、詳細画面には MX を使用し、一覧用ローカルキャッシュには対応する SX URL を使用します。

## 13. Scheduler / Retry / ManualWork

### Scheduler

- enabled / weekday / time を Web UI から設定
- Asia/Tokyo 固定
- 初期値 disabled / Sunday 04:00
- 同日 catch-up

### Retry

- 通常失敗 → +3時間
- Retry #1 失敗 → +1日
- Retry #2 失敗 → 自動 Retry 終了

Scheduled / Manual 成功時は対象カテゴリの pending Retry を cancel します。

### ManualWork

SQLite に durable queue として保持します。

- Pending
- Running
- Completed
- Failed

種類:

- FullScrape
- CategoryScrape
- ArtistCatalog

Web request は enqueue 後すぐ返し、BackgroundService が処理します。起動時に残った Running は Pending へ戻します。

実行優先順位:

1. ManualWork
2. due Retry
3. Scheduled

全 scrape 系処理は `ScrapeExecutionGate` で単一実行です。

## 14. Discord 通知

設定は SQLite を正とし `/settings` から管理します。

通知モード:

- Off
- FailureOnly
- SuccessAndFailure

通知対象:

- Scheduled / Manual / Retry のカテゴリ結果
- Retry 予定
- Artist Catalog 手動取得失敗

成功通知にはその scrape で新たに Artist Watch へ一致した Disc 数を含めます。同じ Disc が複数 Watch に一致しても 1 件です。

Webhook 送信失敗は本体処理の成否や Retry 制御へ波及させません。画像・個別詳細取得失敗は通知対象外です。

## 15. レンタル履歴連携

### 15.1 本体へのインポート

`/operations` から JSON を取り込みます。

入力例:

```json
[
  {
    "titleId": "0000102452",
    "title": "断絶",
    "artist": "井上陽水"
  }
]
```

設計:

- `titleId` で冪等
- 既存 Disc と統合
- 履歴だけに存在する CD も作成
- `IsRented=true`, `NeedsReview=false`
- `RentalHistoryImportedAt` で provenance を保持
- 通常 Source がなくても保持
- 後から通常クロールで見つかれば同じ Disc へ正式メタデータと Source を追加
- 未取得なら detail enrichment を優先

### 15.2 Chrome exporter

`tools/discas-rental-history-exporter/` に Chrome Manifest V3 拡張を置きます。

認証情報を DiscaScout 本体へ持ち込まず、ログイン済みブラウザ内だけで履歴ページを取得します。

- 全ページ直列
- 最低 2 秒間隔
- 10ページごとに 5～20 秒追加待機
- Retry 10秒 → 30秒 → 60秒
- `chrome.storage.local` に進捗保存
- 完全性確認後に JSON 生成
- `titleId` で重複排除
- Cookie / 認証情報は出力しない

## 16. Web UI 方針

主要画面:

- `/discs` — 日常確認・検索・Review / Rented 操作
- `/discs/{id}` — 詳細
- `/artists` — Artist Watch / Catalog 設定
- `/operations` — scrape / work / detail 補完 / import
- `/settings` — Schedule / Discord / safety guard / Genre master

CD 一覧ではタブ・検索・フィルター・並び順を組み合わせます。

主なタブ:

- 未チェック
- Pickup
- レンタル済み
- 全件

タイトル検索は任意で Description / Tracks も対象にできます。検索語は空白区切り AND です。

ジャンルは正規化 Genre tree を利用し、大 / 中 / 小の連動フィルターとして扱います。

ページャーは前後移動だけでなく、現在ページ前後2ページ、先頭・末尾、`…` を表示します。

## 17. Docker 配置

`Dockerfile` と `compose.yaml` を `main` に保持します。

- .NET 10 multi-stage build
- host 8080
- `restart: unless-stopped`
- `TZ=Asia/Tokyo`
- `/health` healthcheck
- `./data:/app/data` bind mount

アプリケーション自身には認証を持たないため、実運用では Traefik / LAN 等の境界で公開範囲を制限します。

## 18. 開発上の原則

- `main` はコード
- `docs` は設計・調査資料専用 orphan branch
- 機能変更は feature branch → PR → CI → merge
- `docs` 更新は直接 commit 可
- 明示されない限り PR を自動 merge しない
- write API では branch を明示し、default branch への暗黙書き込みをしない

コードコメント:

- 日本語・常体
- 「何を」より「なぜ」を残す
- 新規 / 変更 class、主要 public / internal method は原則 XML documentation
- 負荷制御、順序、retry、concurrency、互換性、安全性の意図を積極的に記述

## 19. 現在地

2026-08-30 時点で主要バックエンド、日常 UI、件数安全装置、Artist Watch / Catalog、詳細補完、画像キャッシュ、Discord、レンタル履歴 import/export、Docker 配置、ジャンルマスター正規化まで実装済みです。

現在の `main` は **PR #40 マージ後**を基準とします。
