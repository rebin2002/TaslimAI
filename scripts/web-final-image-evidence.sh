#!/usr/bin/env bash
# Temporary hosted-only evidence harness for PR #1283; not intended for production release.
set -euo pipefail
image="${1:?usage: $0 IMAGE WEB_PORT}"
web_port="${2:-3000}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp_dir="$(mktemp -d)"
container_name="taslim-web-final-image-evidence-${GITHUB_RUN_ID:-local}"
mock_pid=""
started=0
cleanup() {
    if (( started == 1 )); then docker rm --force "$container_name" >/dev/null 2>&1 || true; fi
    if [[ -n "$mock_pid" ]]; then kill "$mock_pid" >/dev/null 2>&1 || true; wait "$mock_pid" >/dev/null 2>&1 || true; fi
    rm -rf "$tmp_dir"
}
trap cleanup EXIT

# Generate advisory names/nodes from the exact checked-out lockfile. npm's nonzero
# result is expected when the lock contains advisories; the JSON is consumed below.
audit_status=0
(
    cd "$repo_root/apps/web"
    npm audit --json > "$tmp_dir/npm-audit.json"
) || audit_status=$?
printf 'LOCKFILE_NPM_AUDIT_EXIT_CODE=%s\n' "$audit_status"

# Enumerate actual package.json files beneath the built runner's node_modules. This
# is a filesystem inventory from the Docker image, not npm ls/audit omit metadata.
inventory_js='const fs=require("node:fs"),path=require("node:path");
const root="/app/node_modules",packages=[];
function walk(dir,prefix){if(!fs.existsSync(dir))return;for(const ent of fs.readdirSync(dir,{withFileTypes:true})){if(ent.name.startsWith("."))continue;const p=path.join(dir,ent.name);if(ent.name.startsWith("@")){walk(p,path.posix.join(prefix,ent.name));continue;}if(!ent.isDirectory()&&!ent.isSymbolicLink())continue;const rel=path.posix.join(prefix,ent.name);try{const x=JSON.parse(fs.readFileSync(path.join(p,"package.json"),"utf8"));packages.push({path:"node_modules/"+rel,name:x.name||rel,version:x.version||"unknown"});}catch{}walk(path.join(p,"node_modules"),path.posix.join(rel,"node_modules"));}}
walk(root,"");packages.sort((a,b)=>a.path.localeCompare(b.path));process.stdout.write(JSON.stringify({packages}));'
docker run --rm --entrypoint node "$image" -e "$inventory_js" > "$tmp_dir/image-inventory.json"

expect_clean="${FINAL_IMAGE_EXPECT_CLEAN:-0}"
node - "$tmp_dir/npm-audit.json" "$tmp_dir/image-inventory.json" "$expect_clean" <<'NODE'
const fs = require('node:fs');
const audit = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const inventory = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const expectClean = process.argv[4] === '1';
const installed = new Map(inventory.packages.map((item) => [item.path, item]));
const vulnerabilities = Object.entries(audit.vulnerabilities || {});
console.log(`LOCKFILE_NPM_AUDIT_SEVERITIES=${JSON.stringify(audit.metadata?.vulnerabilities || {})}`);
console.log(`FINAL_RUNNER_PHYSICAL_PACKAGE_COUNT=${inventory.packages.length}`);
console.log('FINAL_RUNNER_PHYSICAL_INVENTORY_BEGIN');
for (const item of inventory.packages) console.log(`${item.path}\t${item.name}@${item.version}`);
console.log('FINAL_RUNNER_PHYSICAL_INVENTORY_END');
let presentNodes = 0;
let unknownRows = 0;
for (const [name, v] of vulnerabilities) {
  const nodes = Array.isArray(v.nodes) ? v.nodes : [];
  if (nodes.length === 0) unknownRows++;
  const matches = nodes.map((node) => installed.get(node)).filter(Boolean);
  presentNodes += matches.length;
  console.log('LOCKFILE_AUDIT_TO_IMAGE=' + JSON.stringify({
    name, severity: v.severity, range: v.range,
    affectedNodePaths: nodes,
    presentInFinalImage: matches.map(({path, version}) => ({path, version}))
  }));
}
console.log(`AUDITED_VULNERABILITY_PACKAGE_ROWS=${vulnerabilities.length}`);
console.log(`AUDITED_VULNERABLE_INSTANCES_PHYSICALLY_PRESENT=${presentNodes}`);
console.log(`AUDIT_ROWS_WITHOUT_NODE_PATHS=${unknownRows}`);
if (expectClean && (presentNodes > 0 || unknownRows > 0)) {
  console.error('FINAL_IMAGE_PACKAGE_GATE=FAIL: audited package instance remains or could not be mapped.');
  process.exit(1);
}
console.log(`FINAL_IMAGE_PACKAGE_GATE=${expectClean ? 'PASS: no audited node path exists in the physical runner inventory' : 'OBSERVATION ONLY'}`);
NODE

# Local TLS API stub, entirely synthetic. The real Next.js container is configured
# to proxy its login POST to this stub; no production host/provider/account is used.
api_port="$(python3 - <<'PY'
import socket
s=socket.socket(); s.bind(("0.0.0.0", 0)); print(s.getsockname()[1]); s.close()
PY
)"
openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$tmp_dir/api.key" -out "$tmp_dir/api.crt" -days 1 \
    -subj "/CN=api.example.test" \
    -addext "subjectAltName=DNS:api.example.test" \
    -addext "basicConstraints=critical,CA:TRUE" >/dev/null 2>&1
cat > "$tmp_dir/mock_api.py" <<'PY'
import http.server, json, os, ssl, sys
port, cert, key, capture = int(sys.argv[1]), sys.argv[2], sys.argv[3], sys.argv[4]
expected = {"email": "image-smoke@example.invalid", "password": "ProxyOnlySynthetic!123"}
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        # Avoid logging request bodies or synthetic credentials.
        sys.stderr.write("synthetic-api " + (fmt % args) + "\n")
    def send_json(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        if self.path == "/health": self.send_json(200, {"ready": True, "synthetic": True})
        else: self.send_json(404, {"synthetic": True})
    def do_POST(self):
        size = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(size)
        try: payload = json.loads(raw)
        except Exception: payload = None
        valid = self.path == "/api/auth/login" and payload == expected
        with open(capture, "w", encoding="utf-8") as f:
            json.dump({"method": self.command, "path": self.path,
                       "email": (payload or {}).get("email") if isinstance(payload, dict) else None,
                       "synthetic_payload_match": valid,
                       "content_type": self.headers.get("Content-Type")}, f)
        self.send_json(200 if valid else 400,
                       {"synthetic": True, "accepted": valid, "path": self.path})
server = http.server.ThreadingHTTPServer(("0.0.0.0", port), Handler)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
ctx.load_cert_chain(cert, key)
server.socket = ctx.wrap_socket(server.socket, server_side=True)
server.serve_forever()
PY
python3 "$tmp_dir/mock_api.py" "$api_port" "$tmp_dir/api.crt" "$tmp_dir/api.key" "$tmp_dir/captured-request.json" > "$tmp_dir/mock-api.log" 2>&1 &
mock_pid=$!
for _ in $(seq 1 30); do
    if curl --silent --show-error --fail --cacert "$tmp_dir/api.crt" \
        --resolve "api.example.test:${api_port}:127.0.0.1" \
        "https://api.example.test:${api_port}/health" >/dev/null 2>&1; then break; fi
    sleep 1
done
if ! curl --silent --show-error --fail --cacert "$tmp_dir/api.crt" \
    --resolve "api.example.test:${api_port}:127.0.0.1" \
    "https://api.example.test:${api_port}/health" >/dev/null; then
    echo 'Synthetic HTTPS API stub did not become ready.' >&2
    cat "$tmp_dir/mock-api.log" >&2
    exit 1
fi

docker rm --force "$container_name" >/dev/null 2>&1 || true
docker run --detach --publish "${web_port}:${web_port}" --name "$container_name" \
    --add-host api.example.test:host-gateway \
    --volume "$tmp_dir/api.crt:/tmp/final-image-smoke-api.crt:ro" \
    --env NODE_ENV=production \
    --env PORT="$web_port" \
    --env HOSTNAME=0.0.0.0 \
    --env NODE_EXTRA_CA_CERTS=/tmp/final-image-smoke-api.crt \
    --env "NEXT_PUBLIC_API_URL=https://api.example.test:${api_port}" \
    "$image" >/dev/null
started=1

login_status=''
for _ in $(seq 1 60); do
    login_status="$(curl --silent --show-error --connect-timeout 2 --max-time 5 \
        --output "$tmp_dir/login.html" --write-out '%{http_code}' \
        "http://127.0.0.1:${web_port}/login" 2>/dev/null || true)"
    [[ "$login_status" == '200' ]] && break
    sleep 2
done
if [[ "$login_status" != '200' ]] || ! grep -Eiq 'type="email"' "$tmp_dir/login.html"; then
    echo "Final image login route failed: status=${login_status:-none}." >&2
    docker logs "$container_name" >&2 || true
    exit 1
fi
printf 'FINAL_IMAGE_LOGIN_ROUTE=PASS GET /login status=200 email-field-present\n'

payload='{"email":"image-smoke@example.invalid","password":"ProxyOnlySynthetic!123"}'
proxy_status="$(curl --silent --show-error --connect-timeout 3 --max-time 20 \
    --output "$tmp_dir/proxy-response.json" --write-out '%{http_code}' \
    -H 'Content-Type: application/json' -X POST --data "$payload" \
    "http://127.0.0.1:${web_port}/api/auth/login" 2>/dev/null || true)"
if [[ "$proxy_status" != '200' ]]; then
    echo "Final image synthetic login proxy failed: status=${proxy_status:-none}." >&2
    cat "$tmp_dir/proxy-response.json" 2>/dev/null >&2 || true
    docker logs "$container_name" >&2 || true
    cat "$tmp_dir/mock-api.log" >&2 || true
    exit 1
fi
python3 - "$tmp_dir/proxy-response.json" "$tmp_dir/captured-request.json" <<'PY'
import json, sys
response = json.load(open(sys.argv[1], encoding="utf-8"))
request = json.load(open(sys.argv[2], encoding="utf-8"))
assert response == {"synthetic": True, "accepted": True, "path": "/api/auth/login"}, response
assert request["method"] == "POST" and request["path"] == "/api/auth/login", request
assert request["email"] == "image-smoke@example.invalid" and request["synthetic_payload_match"], request
assert request["content_type"].startswith("application/json"), request
PY
printf 'FINAL_IMAGE_SYNTHETIC_LOGIN_PROXY=PASS POST /api/auth/login reached local HTTPS stub; synthetic-only payload validated\n'
printf 'FINAL_IMAGE_CONTAINER_STARTUP=PASS image=%s\n' "$image"
