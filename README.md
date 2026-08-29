# DiscaScout Documentation

このブランチは、DiscaScout の設計・調査資料を管理するための専用 orphan branch です。

アプリケーションのソースコードを管理する `main` および `feature/*` ブランチとは独立した履歴を持ち、実装上の判断や外部サービスの調査結果を、会話コンテキストや実装コードだけに依存せず残すことを目的とします。

## Documents

- [architecture.md](architecture.md) — システム全体の目的、アーキテクチャ、データモデル、状態遷移、バックグラウンド処理、UI 方針
- [discas-scraping.md](discas-scraping.md) — TSUTAYA DISCAS の検索ページに関する実地調査結果とスクレイピング仕様
- [scraping-policy.md](scraping-policy.md) — DISCASへのアクセス負荷制御、ジャンル取得、追加リクエスト抑制に関する必須方針

## Documentation policy

- 実装時に参照すべき確定仕様は、このブランチの文書へ反映する
- 外部サービスについては「確認済みの事実」「現時点の実装方針」「未確認事項」を区別する
- DISCAS の HTML や挙動は将来変更される可能性があるため、調査結果には確認時点を残す
- 実装と文書が食い違った場合は、差異を確認したうえで文書または実装を更新し、暗黙にどちらかを正としない

## Scope

現在の文書は、2026-08-29 までに行った設計検討と、実際の TSUTAYA DISCAS 検索結果 HTML を使った PoC 調査を基にしています。
