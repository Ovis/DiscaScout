# TSUTAYA DISCAS Scraping Research

## 1. 文書の位置付け

この文書は DiscaScout から TSUTAYA DISCAS の CD 検索結果を取得するために、実際の検索結果ページと PoC を使って確認した事項を記録します。

外部サイトの HTML・URL・パラメータは将来変更される可能性があります。そのため、確認済み事項と未確認事項を区別して記載します。

最終確認日: **2026-08-29**

## 2. PoC で確認した HTTP 取得結果

`HttpClient` を使用して次の検索結果ページを取得しました。

```text
https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?G=01013&PA=g_sk_&PN=1&SK=discas_music_new
```

実行結果:

```text
Status: 200 OK
Final URI: https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?G=01013&PA=g_sk_&PN=1&SK=discas_music_new
Charset: Windows-31J
HTML length: 306,590
Document title: アニメ／ゲーム・新作の作品一覧 | 宅配CDレンタルのTSUTAYA DISCAS
Anchors: 491
Unique links: 215
Candidate product links: 40
Images: 49
```

1 ページ 40 商品に対して、商品詳細 URL を 40 件抽出できています。

この結果から、現時点では Playwright 等でブラウザを起動しなくても検索結果本体を取得可能と判断しています。

## 3. HTTP request

### 3.1 User-Agent

PoC では独自 crawler User-Agent を使用せず、通常の Chrome 相当 User-Agent を送ります。

例:

```text
Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36
```

また、次の Accept-Language を送ります。

```text
ja-JP,ja;q=0.9,en;q=0.5
```

これは DISCAS に通常の desktop browser と異なるレスポンスを返させる要因を減らすためです。

## 4. 文字コード

### 4.1 確認済みのレスポンス

DISCAS の検索結果は次の charset を返しました。

```text
Windows-31J
```

### 4.2 .NET での注意点

`.NET` では `CodePagesEncodingProvider` を登録しても、`Windows-31J` という alias 自体を `Encoding.GetEncoding` で解決できない環境があります。

また `HttpContent.ReadAsStringAsync()` は Content-Type の charset を直接使ってデコードしようとするため、実際に次の例外が発生しました。

```text
System.InvalidOperationException:
The character set provided in ContentType is invalid.
Cannot read content as string using an invalid character set.

ArgumentException:
'Windows-31J' is not a supported encoding name.
```

### 4.3 採用するデコード方法

レスポンス本文を byte array として読み込み、charset を DiscaScout 側で解決してから文字列化します。

`Windows-31J` は code page 932 として明示的に扱います。

```text
Windows-31J -> CP932
```

実装では `System.Text.Encoding.CodePages` を使用し、次を登録します。

```csharp
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
```

charset が `Windows-31J` の場合:

```csharp
Encoding.GetEncoding(932)
```

でデコードします。

「Shift_JIS」という名前へ単純置換するのではなく、DISCAS が Windows-31J を返している事実を残したまま CP932 へ明示的に mapping する方針です。

## 5. 検索カテゴリ parameter

実際の検索ページ HTML から次の値を確認しています。

| Category | `SK` |
|---|---|
| 近日リリース | `discas_music_soon` |
| 新作 | `discas_music_new` |
| 準新作 | `discas_music_recent` |

DiscaScout の通常定期収集対象は次の 2 つです。

- `discas_music_soon`
- `discas_music_new`

`discas_music_recent` は現時点の通常収集対象ではありません。

## 6. Sort parameter

検索ページ HTML から次の対応を確認しています。

| Sort | `SRT` |
|---|---|
| 人気順 | `8` |
| 評価の高い順 | `1` |
| レンタル開始日の新しい順 | `5` |
| レンタル開始日の古い順 | `a` |

DiscaScout の通常クロールでは `SRT=5` を使用し、レンタル開始日の新しい順で取得する方針です。

例:

```text
https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?G=01013&PA=g_sk_&SK=discas_music_new&SRT=5&PN=1
```

近日リリースについては `SK` を `discas_music_soon` とします。

## 7. Paging

確認した検索結果は **1 ページ 40 商品**です。

ページ番号には `PN` parameter が使われます。

```text
PN=1
PN=2
...
```

本番スクレイパーでは 1 ページ目だけを取得するのではなく、検索結果が示す総件数と paging 情報を基に全ページを取得します。

カテゴリ単位で全ページ取得・解析が成功した場合のみ DB へ反映します。

## 8. 商品識別子

商品詳細 URL は次の形式です。

```text
https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=7720224056
```

PoC で 1 ページ 40 商品すべてについてこの形式の商品リンクを取得できました。

確認例:

```text
https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=7720224056
https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=7720224060
https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=7720224069
https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=7720224072
https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=7720224070
```

`titleID` を DISCAS 側の安定識別子として使用する方針です。

検索結果 HTML 内には商品リンクとは別に `titleId` の hidden field 群も存在し、ページの商品 ID が列挙されています。

本番パーサーでは商品ブロックから取得した `titleID` を正規データとして使用し、hidden field は必要に応じて「ページ上の商品数 / ID 集合とパーサー結果が一致しているか」を検証する補助情報として利用できます。

タイトル + アーティストから synthetic ID を作ることはしません。タイトル変更を検出対象にするためです。

## 9. 検索結果 DOM

### 9.1 PC 向け商品ブロック

検索結果では PC 向けの商品 1 件が概ね次の container にまとまっています。

```css
.cd-product-item
```

確認できた主な selector:

```css
.cd-product-item
.card-title-searchCd
.card-img
```

商品 container 内から、少なくとも次を関連付けて取得できます。

- 商品詳細 URL
- `titleID`
- Title
- Artist
- Image URL
- Release category 表示

本番パーサーは、まず `.cd-product-item` を 1 商品単位として解析する方針です。

### 9.2 Mobile 向け DOM

同じ HTML 内には mobile 向けと思われる商品 DOM も含まれています。

確認した class:

```css
.item-product-mb
.link-product
.link-artist
```

PC / mobile の双方を単純に document 全体から抽出すると同じ商品を重複解析する可能性があります。

そのため本番パーサーでは **PC 向け `.cd-product-item` のみを正規解析対象とする**方針です。

Mobile DOM は、将来 PC DOM が変更された際の調査材料として利用できますが、同時に解析結果へ混ぜません。

## 10. Title

PC 向け商品 container 内の Title は `.card-title-searchCd` 周辺から取得可能であることを確認しています。

Title は表示用文字列を保持するとともに、差分比較用に共通 normalization を適用した `NormalizedTitle` を DB 側で保持します。

## 11. Artist

Artist も商品 container 内から取得できます。

Mobile DOM では `.link-artist` として明示されています。

本番パーサーでは PC 商品 container 内の Artist link / 表示部分を selector として確定させます。

Artist についても表示文字列と normalized value を分けます。

## 12. Image

商品画像 URL は PC 商品 container の `.card-img` から取得可能です。

ページ全体の `img` を列挙した PoC では商品 40 件に対して 49 images が存在しました。そのため document 内の画像を順番で商品へ対応させる方法は禁止します。

必ず商品 container 内から画像を取得します。

### 12.1 No image

画像未登録商品では次の placeholder URL が使われることを確認しています。

```text
https://img.discas.net/img/jacket/no_image_cd_s.png
```

この placeholder は実際の商品画像として保存せず、DiscaScout では次のように扱います。

```text
ImageUrl = null
```

後日のクロールで実画像 URL が現れた場合に画像を取得します。

## 13. Release category

商品単位に `新作` 等を示す表示が存在します。確認した HTML では category image の `alt="新作"` 等から判定可能です。

ただし通常クロールでは request 自体が Upcoming / New のどちらかに固定されるため、DiscaScout の `DiscSource.Category` は基本的にクロール対象カテゴリから決定します。

商品 DOM 上の表示は、request category と HTML 内容が食い違っていないかを確認する validation signal としても利用できます。

## 14. SourceRank

検索結果の表示順を 1 から連番で `SourceRank` として保持します。

複数ページの場合はページ内順位ではなくカテゴリ全体での順位にします。

例:

```text
Page 1: 1 - 40
Page 2: 41 - 80
```

SourceRank の履歴は保持せず、現在の Active DiscSource に最新順位だけを保存します。

RentalStartDate を取得できない商品についても、`SRT=5` で取得した SourceRank により DISCAS 側の「レンタル開始日の新しい順」を一定程度再現できます。

## 15. RentalStartDate

### 15.1 現在確認できていること

DISCAS の検索機能にはレンタル開始日による sort が存在し、`SRT=5` が「レンタル開始日の新しい順」であることを HTML から確認しています。

内部の sort 指定として `sale_start_date_rental:desc` に相当する情報も確認されています。

したがって DISCAS 側がレンタル開始日データを持っていること自体は明らかです。

### 15.2 現在確認できていないこと

2026-08-29 に保存した検索結果 HTML を確認した範囲では、各商品 container 内に RentalStartDate の具体的な日付値を確認できていません。

そのため現時点では:

```text
RentalStartDate = null
```

を許容します。

### 15.3 Detail page の扱い

RentalStartDate だけを取得するために 1 ページ 40 商品すべての detail page を追加取得することはしません。

優先順位は次のとおりです。

1. 検索結果の visible DOM
2. data attribute / embedded JSON
3. 検索結果自身が利用している合理的な内部データソース
4. それでも取得できなければ null

安定した商品識別子は検索結果の商品 URL から `titleID` を取得できることが確認できたため、identity 確保を目的とした detail page fetch も現時点では不要です。

## 16. Result count と validation

本番スクレイパーでは「HTTP request が成功した」だけではカテゴリ取得成功としません。

検索結果ページが示す総件数を取得し、全ページ解析後の件数と一致することを確認します。

少なくとも次を validation 対象とします。

- reported total count
- 実際に解析した商品数
- `titleID` の重複
- 必須項目 (`titleID`, ProductUrl, Title, Artist) の欠落
- paging の取得漏れ
- 可能なら hidden `titleId` と商品 container の ID 集合の一致
- request category と商品上の category 表示の不整合

全ページの取得または validation に失敗したカテゴリは DB へ部分反映しません。

## 17. Artist search に関する注意

DISCAS の人物・Artist 検索では、検索した人物名と検索結果の商品に表示される Artist が必ずしも一致しないことを確認しています。

例として「小室哲哉」で検索した場合に、表示 Artist が華原朋美やオムニバス等の CD が結果へ含まれるケースがあります。

そのため Artist full catalog では:

1. DISCAS の Artist / person search で候補を全件取得
2. 各商品の表示 Artist を取得
3. DiscaScout 側で ArtistSetting の `Exact` / `Contains` を使って再判定
4. 一致した Disc だけを Catalog relation へ反映

とします。

DISCAS の検索結果に含まれたことだけを Artist 一致の根拠にはしません。

## 18. 現時点で確定している scraper 方針

- `HttpClient + AngleSharp` を使用する
- Chrome 相当 User-Agent を送る
- Windows-31J を CP932 として明示的に decode する
- Upcoming / New を独立した category crawl とする
- `SRT=5` を使用する
- 1 page 40 件として paging を全件取得する
- `.cd-product-item` を商品単位として解析する
- mobile DOM を同時解析しない
- ProductUrl から `titleID` を取得する
- `titleID` を stable DISCAS ID として使用する
- Image は商品 container 内から取得する
- `no_image_cd_s.png` は `ImageUrl = null` とする
- RentalStartDate は取得できなければ null とする
- RentalStartDate のためだけに detail page を大量取得しない
- Category 全ページの取得・解析・validation 成功後にのみ DB へ commit する

## 19. 次に実装・検証する事項

PoC の次段階では、保存した実 HTML を fixture として利用できる形へ整理し、検索結果 parser を実装します。

目標は 1 ページの HTML から次の `ScrapedDisc` を **ちょうど 40 件**生成できることです。

```text
DiscasId
ProductUrl
Title
Artist
ImageUrl
RentalStartDate
Category
SourceRank
```

その後、複数ページ取得と reported total count の一致検証を実装します。

### 未確定事項

- PC 商品 DOM の Artist selector の最終固定
- 総件数 / paging DOM の最終 selector
- `近日リリース` 実ページで New と同じ DOM 前提が成立するか
- RentalStartDate が list HTML 内の別データとして取得可能か
- 継続運用データを基にした「総件数の異常な減少」の具体的なしきい値
- DISCAS 側 DOM 変更をどの validation combination で最も早く検知するか

これらは推測で固定せず、実ページ / fixture による確認後に更新します。
