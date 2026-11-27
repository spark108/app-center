FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Spark108.Core/Spark108.Core.csproj src/Spark108.Core/
COPY src/Spark108.Server/Spark108.Server.csproj src/Spark108.Server/
COPY scripts/Install-AppCenter.ps1 scripts/
RUN dotnet restore src/Spark108.Server/Spark108.Server.csproj
COPY src/Spark108.Core/ src/Spark108.Core/
COPY src/Spark108.Server/ src/Spark108.Server/
RUN dotnet publish src/Spark108.Server/Spark108.Server.csproj -c Release --no-restore -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS desktop-build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Spark108.Core/ src/Spark108.Core/
COPY src/Spark108.Desktop/ src/Spark108.Desktop/
COPY src/Spark108.Updater/ src/Spark108.Updater/
COPY scripts/Zip-Package.proj scripts/
ARG APP_CENTER_VERSION
RUN version="${APP_CENTER_VERSION:-$(dotnet msbuild src/Spark108.Desktop/Spark108.Desktop.csproj -nologo -getProperty:Version)}" \
    && dotnet publish src/Spark108.Desktop/Spark108.Desktop.csproj -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true "-p:Version=$version" -o /client \
    && dotnet publish src/Spark108.Updater/Spark108.Updater.csproj -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true "-p:Version=$version" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o /updater \
    && cp /updater/Spark108.Updater.exe /client/ \
    && printf '%s\n' "$version" > /client/spark-release.txt \
    && mkdir /export \
    && cp /client/spark-release.txt /export/ \
    && dotnet msbuild scripts/Zip-Package.proj -nologo -t:Zip -p:PackageDirectory=/client -p:ArchivePath=/export/app-center.zip

FROM scratch AS desktop-package
COPY --from=desktop-build /export/ /

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS app
WORKDIR /app
RUN mkdir -p /data/temp && chown -R "$APP_UID:$APP_UID" /data
COPY --from=build /publish/ ./
RUN chmod -R u=rwX,go=rX /app
USER $APP_UID
ENTRYPOINT ["dotnet", "Spark108.Server.dll"]
