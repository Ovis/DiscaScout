# Scraping Policy

最終更新: **2026-08-30**

## 1. 目的

DiscaScout は TSUTAYA DISCAS の検索結果と必要な商品詳細情報を取得しますが、相手側サーバーへ不要な負荷をかけないことを最重要の運用要件とします。

この文書は、通常クロール、手動実行、リトライ、Artist Catalog、商品詳細メタデータ取得を含むすべての DISCAS HTML アクセスで維持する制約を定めます。

高速化を理由として、この制約を個別機能側で迂回してはいけません。

## 2. DISCAS HTML アクセスの共通制約

検索 HTML、Artist Catalog、商品詳細 HTML は共有 `DiscasRequestThrottle` / `DiscasPageFetcher` を経由します。

必須制約:

- DISCAS HTML HTTP request はアプリケーション全体で **並列化しない**
- 連続する request の開始時刻を最低 **2 秒**空ける
- HTML page request **10 件ごと**に、次の request 開始前へ **5～20 秒のランダム追加待機**を入れる
- New / Upcoming / Artist Catalog / detail fetch の間でも同じ global throttle を共有する

短時間で処理を完了させることより、相手サーバーへのアクセス頻度を抑えることを優先します。ページ数が増えた場合も HTML request の並列化による高速化は行いません。

この制約は個別 crawler に分散して実装せず、共通 Fetcher / throttle で保証します。将来 DISCAS HTML を取得する新機能を追加する場合も同じ経路を使用してください。

## 3. 通常クロールとジャンル取得

通常クロールは `New` と `Upcoming` を全ジャンルでそれぞれ一度だけ取得します。

J-POP、アニメ／ゲーム等をジャンル別に個別クロールすると同じカテゴリへの追加アクセスが必要になるため、その方式は採用しません。

現在の DISCAS 検索結果 HTML には商品ごとの GA 用メタデータとして次の値が含まれています。

- `genre_large`
- `genre_mid`
- `genre_min`
- `titleid`

DiscaScout は `titleid` を商品識別子と対応付け、追加 HTTP request なしでジャンルを取得します。

保存対象:

- `GenreLarge` — 大ジャンル。必須
- `GenreMiddle` — 中ジャンル。値がない場合は null
- `GenreSmall` — 小ジャンル。値がない場合は null

2026-08-29 の全ジャンル実クロールでは Upcoming 821 件 / New 1,528 件についてジャンルメタデータを含めた完全取得を確認しています。

商品に対応する大ジャンルを解析できない場合は欠損値のまま保存せず、そのページの解析失敗として扱います。

## 4. 追加 detail page 取得の原則

検索結果 HTML だけで取得できる情報のために商品詳細ページを追加取得しません。

特に次は検索結果側から取得します。

- 商品 ID
- Title
- Artist
- Image URL
- Genre
- MAXI 判定

ジャンル取得を目的とした detail page fetch は禁止します。

一方、検索結果だけでは取得できない次の情報については、低頻度の専用 detail enrichment を許可しています。

- RentalStartDate
- Description
- Tracks
- 2 枚組判定

将来新たなメタデータが必要になった場合も、まず検索結果 HTML、埋め込みデータ、既存内部データから取得できないか確認し、全商品に対する追加 request は最後の手段とします。

## 5. Detail enrichment の追加制約

商品詳細のバックグラウンド取得は global HTML throttle に加えて次を守ります。

- 1 商品ずつ処理する
- detail request 間を約 **15 秒**空ける
- 通常 scrape / Artist Catalog が `ScrapeExecutionGate` を使用中は detail worker が譲る
- detail page をユーザーが開いた場合は優先度を上げるだけで、Web response 内では DISCAS を取得しない
- 初回取得失敗後は最低 **6 時間**再試行しない
- レンタル開始前に成功した場合のみ、レンタル開始日以降に最終 refresh を 1 回行う
- レンタル開始日以降の成功後は通常それ以上取得しない

これにより、詳細情報を徐々に補完しつつ、通常カテゴリ取得を優先します。

## 6. 画像アクセス

商品画像は DISCAS HTML と別系統で扱います。通常 scrape / Artist Catalog の完了を画像取得待ちにしません。

画像 cache worker の制約:

- pending ID を pass 開始時に snapshot 化
- **40 IDs / batch**
- image HTTP は最大 **4 concurrent**
- batch 間を **2 秒**空ける
- **10 batch ごと**に **5～20 秒**のランダム追加待機
- 同一 ImageUrl かつローカル file が存在する場合は再取得しない
- replacement download が失敗した場合は旧画像を保持
- 個別画像失敗は通常 scrape / Artist Catalog の成功判定へ影響させない

画像は HTML request と異なり最大 4 concurrent を許容していますが、無制限並列化はしません。

## 7. リトライ

HTTP エラーや解析失敗が発生しても、同じ要求を短時間に連打しません。

カテゴリ単位の自動 retry:

- 通常実行失敗: 約 3 時間後
- 1 回目 retry 失敗: 約 1 日後
- 2 回目 retry 失敗: 自動 retry 終了

Retry 実行も global HTML throttle と非並列制約を維持します。

## 8. 実装変更時の確認事項

DISCAS に対する HTTP 処理を追加・変更する場合は最低限次を確認してください。

1. 検索結果や既存 DB だけで目的を達成できないか
2. HTML request が共有 `DiscasRequestThrottle` を通っているか
3. 複数 BackgroundService 間で意図せず HTML request が並列化されないか
4. 失敗時に短時間 retry loop へ入らないか
5. 通常 scrape の優先度を detail / 補助取得が阻害しないか
6. 画像取得を scrape 完了条件へ再び混入させていないか

サーバー負荷を増やす変更は、処理時間短縮だけを理由に採用しません。
