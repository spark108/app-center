#!/usr/bin/env bash
set -euo pipefail

version=${APP_CENTER_VERSION:-}
if [[ ! "$version" =~ ^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$ ]]; then
    echo "Set APP_CENTER_VERSION to a version such as 1.0.1." >&2
    exit 1
fi
IFS=. read -r major minor patch <<< "$version"
if (( major > 65534 || minor > 65534 || patch > 65534 )); then
    echo "Each APP_CENTER_VERSION number must be between 0 and 65534." >&2
    exit 1
fi

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

docker build --target desktop-package --build-arg "APP_CENTER_VERSION=$version" --output "type=local,dest=$work" .
if [[ "$(tr -d '\r\n' < "$work/spark-release.txt")" != "$version" ]]; then
    echo "Built App Center version does not match APP_CENTER_VERSION." >&2
    exit 1
fi
container_zip="/data/temp/app-center-$version-$BASHPID.zip"
docker compose cp "$work/app-center.zip" "app:$container_zip"
docker compose exec -T app dotnet Spark108.Server.dll --publish-client "$container_zip" --client-version "$version"
