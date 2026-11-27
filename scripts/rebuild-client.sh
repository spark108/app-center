#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
if [[ -z "$(docker compose ps --status running -q app)" ]]; then
    echo "Start the app service with docker compose up -d --build app first." >&2
    exit 1
fi

work=$(mktemp -d)
container_zip=
cleanup() {
    if [[ -n "$container_zip" ]]; then
        docker compose exec -T app rm -f -- "$container_zip" || true
    fi
    rm -rf -- "$work"
}
trap cleanup EXIT

docker build --target desktop-package --output "type=local,dest=$work" .
version=$(tr -d '\r\n' < "$work/spark-release.txt")
if [[ ! "$version" =~ ^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$ ]]; then
    echo "Invalid desktop version: $version" >&2
    exit 1
fi
container_zip="/data/temp/app-center-$version-$BASHPID.zip"
docker compose cp "$work/app-center.zip" "app:$container_zip"
docker compose exec -T app dotnet Spark108.Server.dll --publish-client "$container_zip" --client-version "$version"
