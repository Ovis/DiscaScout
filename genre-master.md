# DISCAS Genre Master

最終更新: **2026-08-30**

この文書は、PR #40 で導入した DISCAS ジャンルマスター、CD へのジャンル解決、一覧フィルターの確定仕様と、実 HTML を確認して判明した注意点を記録します。

## 1. 目的

従来は検索結果から取得した `GenreLarge` / `GenreMiddle` / `GenreSmall` の文字列を `Disc` に直接保持していましたが、ジャンルを正規化して扱うため、DISCAS のジャンルマスターを SQLite に保持し、`Disc.GenreId` からジャンルノードを参照する構成へ変更しました。

ジャンルマスターを正とし、検索結果・詳細ページから得たジャンル文字列によってマスターを暗黙に追加・変更しません。

## 2. データモデル

`Genre` は自己参照ツリーです。

主な項目:

- `Id`
- `ExternalId`
- `Name`
- `ParentId`
- `SortOrder`
- `IsActive`
- `FirstSeenAt`
- `LastSeenAt`

`Disc` は `GenreLarge` / `GenreMiddle` / `GenreSmall` を永続化せず、次だけを保持します。

- `GenreId` — DISCAS が当該 CD に対して報告したパスの最深ノード。未解決なら null

内部モデルは任意深度のツリーとして扱います。現在の一覧 UI は大・中・小の3段階を表示します。

## 3. ジャンルマスター取得

マスター取得元:

```text
https://movie-tsutaya.tsite.jp/netdvd/cd/genreAll.do
```

通常クロール開始前に `Genre` が空なら一度だけ初期取得します。既存マスターがある場合、アプリ起動や通常クロールのたびには更新しません。

以後の更新は `/settings` のジャンルマスター更新操作から明示的に実行します。

更新時は取得・解析・検証を DB transaction の前に行い、検証成功後にだけマスターへ反映します。

安全装置:

- 解析結果 0 件は失敗
- `ExternalId` 重複は失敗
- 親が存在しないノードは失敗
- cycle は失敗
- 既存 active 件数の 75% 未満まで解析件数が減った場合は失敗
- 失敗時は既存マスターを変更しない

更新では物理削除せず、消えたジャンルを inactive にします。再出現した場合は reactivate します。

## 4. DISCAS の `G` パラメータと階層

2026-08-30 に `genreAll.do` の実 HTML を確認した結果、ジャンル階層は `<ul>/<li>` の DOM ネストでは表現されていません。

大ジャンルは `.ppdis00033WrapB` 内の見出し、配下ジャンルは同じブロック内の一覧として配置されています。

階層はリンクの `G` / `g` パラメータ自身に含まれます。

例:

```text
01013             アニメ／ゲーム
01013,01072       声優
```

この場合:

```text
アニメ／ゲーム
└─ 声優
```

として扱い、`01013,01072` の親 `ExternalId` は `01013` とします。

### ExternalId は完全パスを保持する

末尾のジャンル ID だけを `ExternalId` にしません。同じ末尾 ID が別の大ジャンル配下で使用されるケースがあるためです。

したがって `ExternalId` は DISCAS の `G` パラメータの完全な経路をそのまま保持します。

### hidden 集約ブロックを除外する

`genreAll.do` の末尾には `style="display: none;"` の `.ppdis00033WrapB` があり、通常ジャンルツリーとは異なる集約用リンクが含まれます。

実際に確認した例:

```text
見出し: 0102e / オムニバス
子:     01023,01102 / 洋楽オムニバス
子:     01022,01101 / 邦楽オムニバス
```

この構造では `01023,01102` の親を単純に `01023` と解釈しても、同じブロック内にその親は存在しません。

これは通常のジャンル階層として取り込まず、`style="display: none;"` のジャンルブロック自体を解析対象から除外します。

## 5. CD のジャンル解決

検索結果と詳細ページの双方を次の共通パイプラインへ流します。

```text
DISCAS HTML
  ↓
ジャンルパス抽出
  ↓
GenreResolver
  ↓
Genre master の親子関係を完全一致
  ↓
Disc.GenreId
```

名前だけを全階層横断で検索せず、ルートから順番に親子関係をたどります。

例:

```text
アニメ／ゲーム > 声優
```

なら、ルート `アニメ／ゲーム` を一意に解決した後、その直接の子から `声優` を解決します。

途中まで一致しても部分解決は行いません。完全なパスを解決できない場合は `GenreId = null` とし warning を記録します。

マスターにないジャンルを CD 解析側から自動追加することもしません。

## 6. 詳細ページ

詳細ページのジャンル抽出では、ページ全体に対する正規表現を使用しません。

DISCAS ページにはナビゲーション等にも「ジャンル」という語があり、過去に `すべてのジャンル` 周辺から巨大な文字列を誤抽出したためです。

現在は AngleSharp で実際のジャンル表示付近を DOM として解析し、表示されているジャンルパスを取得します。

検索結果で解決済みの `GenreId` と詳細ページで解決したジャンルが異なる場合は warning を残し、より商品固有の情報である詳細ページ側を採用します。

## 7. マスター更新後の再解決

ジャンルマスター更新時、次の CD は詳細再取得対象へ戻します。

- `GenreId == null`
- 参照している Genre が inactive

その際、詳細取得状態をリセットします。マスター更新 HTTP リクエストの中で全 CD の詳細ページを同期取得することはしません。

既存の詳細取得 BackgroundService が通常の負荷制御に従って順次再取得・再解決します。

## 8. 一覧フィルター

`/discs` では3段階の連動セレクトを使用します。

- 大ジャンル
- 中ジャンル
- 小ジャンル

大ジャンルを選択すると対応する中ジャンル候補を JavaScript で即座に再構築し、中ジャンルを選択すると小ジャンル候補を再構築します。ページを一度 submit しないと子ジャンルを選べない構成にはしません。

親ジャンルだけを指定した場合は、そのノード自身だけでなく全 descendant を検索対象にします。中ジャンルまで指定した場合も同様に、その中ジャンル配下を含みます。

フィルター候補には原則 active ジャンルを表示します。ただし inactive でも既存 Disc から参照されているジャンルは表示対象に残し、旧ジャンルであることを UI 上で区別します。

## 9. 運用上の注意

`genreAll.do` の HTML、CSS class、`G` パラメータ仕様は DISCAS 側の外部仕様であり、将来変更される可能性があります。

特に次を変更する場合は、推測で対応せず実 HTML を保存して確認してください。

- `DiscasGenreMasterParser`
- `DiscasDiscDetailParser`
- `GenreResolver`
- ジャンルマスター更新時の validation

`genreAll.do` の取得自体が成功しても、親子関係が誤っていれば CD のジャンル解決は失敗します。件数だけで正常と判断しないことが重要です。

## 10. 関連実装

主な実装:

- `DiscasGenreMasterParser`
- `GenreMasterService`
- `GenreResolver`
- `DiscasDiscDetailParser`
- `DiscDetailMetadataService`
- `DiscasSnapshotApplier`
- `ArtistCatalogStore`
- `DiscsController`
- `DiscsViewModel`

この設計は PR #40 `Add DISCAS genre master and normalized genre filtering` で導入し、main へマージ済みです。
