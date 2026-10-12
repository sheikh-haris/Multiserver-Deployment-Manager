#!/usr/bin/env bash
# Whole-flow browser smoke test. See README.md.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
W="${SMOKE_DIR:-/tmp/icc-smoke}"
OUT="${SMOKE_OUT:-$W/out}"
PORTAL_PORT=7032
SITE_PORT=18081
rm -rf "$W"; mkdir -p "$W/websites/shop" "$W/cfg" "$OUT" "$W/pkg"
PIDS="$W/pids"; : > "$PIDS"
cleanup() { while read -r p; do kill "$p" 2>/dev/null || true; done < "$PIDS"; }
trap cleanup EXIT

cd "$ROOT"
dotnet build src/IisControlCenter.Web -nologo -v q >/dev/null
dotnet build src/IisControlCenter.Agent -nologo -v q >/dev/null

# --- packages ---
python3 - "$W/pkg" <<'PY'
import sys, zipfile, os
d = sys.argv[1]
def z(name, files):
    with zipfile.ZipFile(os.path.join(d, name), "w") as f:
        for p, c in files.items(): f.writestr(p, c)
z("v1.zip", {"index.html": "<h1>version 1</h1>", "health.txt": "ok", "app.dll": "dll-v1", "web.config": "<new-config/>"})
z("v2-broken.zip", {"index.html": "BROKEN version 2", "app.dll": "dll-v2"})
z("v3.zip", {"index.html": "<h1>version 3</h1>", "health.txt": "ok", "app.dll": "dll-v3"})
z("evil.zip", {"../escape.txt": "x", "index.html": "x"})
PY

# --- portal ---
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$W/k.pem" -out "$W/c.pem" -subj "/CN=localhost" -days 2 -addext "subjectAltName=DNS:localhost" 2>/dev/null
openssl pkcs12 -export -out "$W/dev.pfx" -inkey "$W/k.pem" -in "$W/c.pem" -passout pass:x
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="https://localhost:$PORTAL_PORT"
export ASPNETCORE_Kestrel__Certificates__Default__Path="$W/dev.pfx"
export ASPNETCORE_Kestrel__Certificates__Default__Password=x
export ConnectionStrings__Default="Data Source=$W/portal.db"
export Deployments__PackageStorePath="$W/packages"
export Deployments__HealthTimeoutSeconds=6
export Deployments__AntivirusMode=Agent
export Deployments__PublicBaseUrl="https://localhost:$PORTAL_PORT"
(cd src/IisControlCenter.Web && exec dotnet bin/Debug/net10.0/IisControlCenter.Web.dll > "$OUT/portal.log" 2>&1) &
echo $! >> "$PIDS"
for i in $(seq 1 60); do curl -ks "https://localhost:$PORTAL_PORT/healthz" | grep -q ok && break; sleep 1; done
PORTAL="https://localhost:$PORTAL_PORT"

# --- agent ---
cat > "$W/sites.json" <<JSON
[{"id":1,"name":"shop-dev","path":"$W/websites/shop","pool":"ShopPool","port":$SITE_PORT,"host":null}]
JSON
SRV=$(curl -ks -X POST "$PORTAL/dev/e2e/new-server?name=SMOKE01")
SID=$(echo "$SRV" | python3 -c 'import sys,json;print(json.load(sys.stdin)["serverId"])')
TOKEN=$(echo "$SRV" | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
export ICC_AGENT_CONFIG="$W/cfg/agent.config.json" ICC_INSECURE_DEV_TLS=1 ICC_DEV_POSIX_PATHS=1 ICC_DEV_SKIP_SCAN=1 ICC_SAMPLE_SITES_FILE="$W/sites.json"
ICC_TOKEN="$TOKEN" dotnet src/IisControlCenter.Agent/bin/Debug/net10.0/IisControlCenter.Agent.dll register --portal "$PORTAL" --environment Dev --roots "$W/websites" --allow-managed > "$OUT/register.log" 2>&1
(cd "$W/cfg" && exec dotnet "$ROOT/src/IisControlCenter.Agent/bin/Debug/net10.0/IisControlCenter.Agent.dll" > "$OUT/agent.log" 2>&1) &
echo $! >> "$PIDS"
for i in $(seq 1 30); do grep -q "Connected to portal" "$OUT/agent.log" && break; sleep 1; done
curl -ks -X POST "$PORTAL/dev/e2e/approve?serverId=$SID&environment=Dev" >/dev/null     # approving an online agent starts discovery
for i in $(seq 1 60); do curl -ks "$PORTAL/dev/e2e/status?serverId=$SID" | grep -q '"sites":1' && break; sleep 1; done
curl -ks "$PORTAL/dev/e2e/status?serverId=$SID" | grep -q '"sites":1' || { echo "agent did not report its site"; tail -20 "$OUT/agent.log"; exit 1; }

# --- world ---
echo "<h1>original</h1>" > "$W/websites/shop/index.html"; echo "original-config" > "$W/websites/shop/web.config"; echo "user-data" > "$W/websites/shop/keep.txt"
# A tiny "application": /health.txt is 200 unless index.html says BROKEN, so a bad release fails its health check.
cat > "$W/site.py" <<'PY'
import http.server, sys, os
root, port = sys.argv[1], int(sys.argv[2])
class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=root, **k)
    def do_GET(self):
        if self.path.startswith("/health.txt"):
            try: broken = "BROKEN" in open(os.path.join(root, "index.html")).read()
            except OSError: broken = True
            offline = os.path.exists(os.path.join(root, "app_offline.htm"))
            self.send_response(500 if (broken or offline) else 200); self.end_headers(); self.wfile.write(b"x"); return
        super().do_GET()
http.server.ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
PY
(exec python3 "$W/site.py" "$W/websites/shop" $SITE_PORT > "$OUT/site.log" 2>&1) &
echo $! >> "$PIDS"
PRJ=$(curl -ks -X POST "$PORTAL/dev/e2e/project?name=Shop&health=/health.txt")
PID=$(echo "$PRJ" | python3 -c 'import sys,json;print(json.load(sys.stdin)["projectId"])')
CID=$(echo "$PRJ" | python3 -c 'import sys,json;print(json.load(sys.stdin)["componentId"])')
curl -ks -X POST "$PORTAL/dev/e2e/link?componentId=$CID&serverId=$SID&siteId=1" >/dev/null
for p in "deployer Deployer" "approver Approver" "approver2 Approver"; do set -- $p; curl -ks -X POST "$PORTAL/dev/e2e/grant?persona=$1&projectId=$PID&role=$2&env=Dev" >/dev/null; done

export PORTAL PID CID SID W OUT SITE_PORT
NODE_PATH="$(npm root -g)" node "$ROOT/tests/Smoke/smoke.mjs"
echo "SMOKE OK"
