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
ARG APP_CENTER_VERSION
RUN --mount=type=bind,target=/source --mount=type=tmpfs,target=/work,size=3221225472 \
    mkdir -p /work/source /work/home /work/tmp /export \
    && cp -a /source/. /work/source/ \
    && cd /work/source \
    && export HOME=/work/home DOTNET_CLI_HOME=/work/home NUGET_PACKAGES=/work/nuget TMPDIR=/work/tmp DOTNET_NOLOGO=1 \
    && version="${APP_CENTER_VERSION:-$(dotnet msbuild src/Spark108.Desktop/Spark108.Desktop.csproj -nologo -getProperty:Version)}" \
    && dotnet publish src/Spark108.Desktop/Spark108.Desktop.csproj -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true "-p:Version=$version" -o /work/client \
    && dotnet publish src/Spark108.Updater/Spark108.Updater.csproj -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true "-p:Version=$version" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o /work/updater \
    && cp /work/updater/Spark108.Updater.exe /work/client/ \
    && printf '%s\n' "$version" > /work/client/spark-release.txt \
    && cp /work/client/spark-release.txt /export/ \
    && dotnet msbuild scripts/Zip-Package.proj -nologo -t:Zip -p:PackageDirectory=/work/client -p:ArchivePath=/export/app-center.zip \
    && chmod 644 /export/app-center.zip /export/spark-release.txt

FROM scratch AS desktop-package
COPY --from=desktop-build /export/ /

FROM golang:1.27-alpine AS setup-build
WORKDIR /setup
COPY src/Spark108.Setup/ ./
ARG APP_CENTER_SETUP_ORIGIN=https://app.godish.ru
RUN CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath \
    -ldflags="-s -w -H=windowsgui -X main.serviceOrigin=$APP_CENTER_SETUP_ORIGIN" \
    -o /out/Spark108-AppCenter-Setup.exe .

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS app
WORKDIR /app
RUN mkdir -p /data/temp && chown -R "$APP_UID:$APP_UID" /data
COPY --from=build /publish/ ./
COPY --from=setup-build /out/Spark108-AppCenter-Setup.exe ./
RUN chmod -R u=rwX,go=rX /app
USER $APP_UID
ENTRYPOINT ["dotnet", "Spark108.Server.dll"]
