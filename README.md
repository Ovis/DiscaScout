# DiscaScout Documentation

このブランチは、DiscaScout の設計・調査資料を管理するための専用 orphan branch です。

アプリケーションのソースコードを管理する `main` および `feature/*` ブランチとは独立した履歴を持ち、実装上の判断や外部サービスの調査結果を、会話コンテキストや実装コードだけに依存せず残すことを目的とします。

## Documents

- [current-state.md](current-state.md) — **最初に読む文書**。現在の実装状態、確定済み仕様、最近の PR、未実装事項、次セッションへの引き継ぎ
- [architecture.md](architecture.md) — システム全体の目的、アーキテクチャ、データモデル、状態遷移、バックグラウンド処理、UI 方針
- [discas-scraping.md](discas-scraping.md) — TSUTAYA DISCAS の検索ページに関する実地調査結果とスクレイピング仕様
- [genre-master.md](genre-master.md) — DISCAS ジャンルマスター、`G` パラメータによる階層、CD へのジャンル解決、一覧の連動ジャンルフィルター
- [scraping-policy.md](scraping-policy.md) — DISCAS へのアクセス負荷制御、ジャンル取得、追加リクエスト抑制に関する必須方針
- [discas-live-verification-2026-08-29.md](discas-live-verification-2026-08-29.md) — 2026-08-29 に実施した全ジャンル実クロールの検証記録

新しい開発セッションでは、まず `current-state.md` を読み、その後に作業内容に応じて他の文書と `main` の最新コードを確認してください。

## Documentation policy

- 実装時に参照すべき確定仕様は、このブランチの文書へ反映する
- 外部サービスについては「確認済みの事実」「現時点の実装方針」「未確認事項」を区別する
- DISCAS の HTML や挙動は将来変更される可能性があるため、調査結果には確認時点を残す
- 実装と文書が食い違った場合は、差異を確認したうえで文書または実装を更新し、暗黙にどちらかを正としない
- `current-state.md` は会話セッションを跨ぐための要約として、主要機能の追加・設計変更時に更新する

## Scope

現在の文書は 2026-08-30 時点の設計・実装・TSUTAYA DISCAS 実ページ調査を基にしています。ジャンルマスター正規化は PR #40 まで反映済みです。
