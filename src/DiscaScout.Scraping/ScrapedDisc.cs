namespace DiscaScout.Scraping;

/// <summary>
/// DISCAS検索結果から抽出したCD情報を保持する
/// </summary>
/// <param name="DiscasId">DISCASの商品識別子。検索結果の商品URLに含まれるtitleID</param>
/// <param name="ProductUrl">DISCASの商品詳細URL</param>
/// <param name="Title">検索結果に表示されたタイトル</param>
/// <param name="Artist">検索結果に表示されたアーティスト</param>
/// <param name="GenreLarge">DISCASの商品メタデータに含まれる大ジャンル</param>
/// <param name="GenreMiddle">DISCASの商品メタデータに含まれる中ジャンル。値がない場合はnull</param>
/// <param name="GenreSmall">DISCASの商品メタデータに含まれる小ジャンル。値がない場合はnull</param>
/// <param name="ImageUrl">ジャケット画像URL。DISCASの画像未登録プレースホルダーの場合はnull</param>
/// <param name="RentalStartDate">レンタル開始日。検索結果から取得できない場合はnull</param>
/// <param name="Category">この検索結果を取得したリリースカテゴリ</param>
/// <param name="SourceRank">カテゴリ全体での表示順位。1始まり</param>
/// <param name="IsMaxiSingle">タイトル先頭の【MAXI】表記から判定したマキシシングルかどうか</param>
public sealed record ScrapedDisc(
    string DiscasId,
    string ProductUrl,
    string Title,
    string Artist,
    string GenreLarge,
    string? GenreMiddle,
    string? GenreSmall,
    string? ImageUrl,
    DateOnly? RentalStartDate,
    DiscSourceCategory Category,
    int SourceRank,
    bool IsMaxiSingle = false);
