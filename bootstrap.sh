#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly NAMESPACE="ai-stack"   # must match metadata.namespace in manifests/

MODEL_REPO="${MODEL_REPO:-Qwen/Qwen2.5-0.5B-Instruct-GGUF}"
MODEL_FILE="${MODEL_FILE:-qwen2.5-0.5b-instruct-q4_k_m.gguf}"
MODEL_PATH="${MODEL_PATH:-${SCRIPT_DIR}/models/${MODEL_FILE}}"
LLAMA_PORT="${LLAMA_PORT:-8080}"
UI_HOST="${UI_HOST:-127.0.0.1}"
UI_PORT="${UI_PORT:-8000}"

MODE="k8s"
CLEAN=false
LLAMA_PID=""

log()  { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }
die()  { log "ERROR: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "'$1' not found in PATH. $2"; }

usage() {
  cat <<USAGE
Usage: $0 [--mode k8s|local] [--clean] [--help]
  --mode   k8s (default): apply manifests to the current cluster
           local: run the stack natively on Ubuntu/Linux
  --clean  k8s only: prune unused container images first
Env overrides: MODEL_PATH, MODEL_REPO, MODEL_FILE, LLAMA_URL, SEARXNG_URL
USAGE
}

cleanup() {
  if [[ -n "$LLAMA_PID" ]]; then kill "$LLAMA_PID" 2>/dev/null || true; fi
}

run_k8s() {
  need kubectl "Install kubectl or fix your PATH."
  kubectl cluster-info >/dev/null 2>&1 || die "Cannot reach a cluster. Check your kubeconfig."

  if $CLEAN; then
    log "Pruning unused images (--clean)"
    crictl rmi --prune || log "crictl prune skipped"
  fi

  log "Applying manifests to namespace '${NAMESPACE}'"
  kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -
  kubectl apply -n "$NAMESPACE" -f "${SCRIPT_DIR}/manifests/"

  for d in llama-server searxng rag-frontend; do
    kubectl rollout status "deployment/${d}" -n "$NAMESPACE" --timeout=180s
  done
  log "Ready. UI on NodePort 30882 (llama API 30880, SearXNG 30881)."
}

run_local() {
  need python3 "Try: sudo apt install python3 python3-venv"
  need curl "Try: sudo apt install curl"

  python3 -m venv "${SCRIPT_DIR}/.venv"
  set +u; source "${SCRIPT_DIR}/.venv/bin/activate"; set -u
  python -m pip install --quiet --upgrade pip
  python -m pip install --quiet -r "${SCRIPT_DIR}/requirements.txt"

  if [[ ! -f "$MODEL_PATH" ]]; then
    [[ "$MODEL_PATH" == "${SCRIPT_DIR}/models/${MODEL_FILE}" ]] \
      || die "MODEL_PATH not found: $MODEL_PATH"
    log "Downloading ${MODEL_FILE} from ${MODEL_REPO}"
    mkdir -p "${SCRIPT_DIR}/models"
    python - <<PY
from huggingface_hub import hf_hub_download
hf_hub_download(repo_id="${MODEL_REPO}", filename="${MODEL_FILE}",
                local_dir="${SCRIPT_DIR}/models")
PY
  fi

  if [[ -z "${LLAMA_URL:-}" ]]; then
    need llama-server "Install llama.cpp, or set LLAMA_URL to an existing endpoint."
    log "Starting llama-server on 127.0.0.1:${LLAMA_PORT}"
    llama-server -m "$MODEL_PATH" --host 127.0.0.1 --port "$LLAMA_PORT" -c 2048 \
      >"${SCRIPT_DIR}/llama.log" 2>&1 &
    LLAMA_PID=$!
    trap cleanup EXIT

    ready=false
    for _ in $(seq 1 60); do
      if curl -fs "http://127.0.0.1:${LLAMA_PORT}/health" >/dev/null; then ready=true; break; fi
      kill -0 "$LLAMA_PID" 2>/dev/null || die "llama-server exited. See llama.log"
      sleep 2
    done
    $ready || die "llama-server did not become healthy. See llama.log"
    export LLAMA_URL="http://127.0.0.1:${LLAMA_PORT}/v1/chat/completions"
  else
    log "Using external LLAMA_URL=${LLAMA_URL}"
  fi

  if [[ -z "${SEARXNG_URL:-}" ]]; then
    log "WARN: SEARXNG_URL not set. Web Search will not work until you point it at a SearXNG instance."
  else
    export SEARXNG_URL
  fi

  log "UI on http://${UI_HOST}:${UI_PORT}"
  uvicorn rag_app:app --app-dir "${SCRIPT_DIR}/app" --host "$UI_HOST" --port "$UI_PORT"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode)  [[ $# -ge 2 ]] || die "--mode needs a value"; MODE="$2"; shift 2 ;;
    --clean) CLEAN=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "Unknown argument: $1" ;;
  esac
done

log "AI stack bootstrap (mode: ${MODE})"
case "$MODE" in
  k8s)          run_k8s ;;
  local|ubuntu) run_local ;;
  *)            usage; die "Unknown mode: $MODE" ;;
esac
