# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# プロジェクトファイルを先にコピーし、ソース変更時もNuGet restoreのDockerキャッシュを再利用できるようにする
COPY ["src/DiscaScout.Core/DiscaScout.Core.csproj", "src/DiscaScout.Core/"]
COPY ["src/DiscaScout.Application/DiscaScout.Application.csproj", "src/DiscaScout.Application/"]
COPY ["src/DiscaScout.Persistence/DiscaScout.Persistence.csproj", "src/DiscaScout.Persistence/"]
COPY ["src/DiscaScout.Scraping/DiscaScout.Scraping.csproj", "src/DiscaScout.Scraping/"]
COPY ["src/DiscaScout.Web/DiscaScout.Web.csproj", "src/DiscaScout.Web/"]
RUN dotnet restore "src/DiscaScout.Web/DiscaScout.Web.csproj"

COPY . .
RUN dotnet publish "src/DiscaScout.Web/DiscaScout.Web.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# SQLiteと画像キャッシュをホスト側へbind mountするための配置先。
# 実データはcompose.yamlでリポジトリ直下のdataへ永続化する。
RUN mkdir -p /app/data

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DiscaScout__DatabasePath=/app/data/discascout.db \
    DiscaScout__ImageCachePath=/app/data/images

EXPOSE 8080
ENTRYPOINT ["dotnet", "DiscaScout.Web.dll"]
