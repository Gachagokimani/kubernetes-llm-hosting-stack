#!/usr/bin/env bash
set -e
crictl rmi --prune || true
rm -rf /var/lib/kubelet/image_manager/pulling/* || true
kubectl create namespace ai-stack --dry-run=client -o yaml | kubectl apply -f -
kubectl config set-context --current --namespace=ai-stack
kubectl apply -f manifests/
kubectl rollout status deployment/llama-server --timeout=180s
kubectl rollout status deployment/searxng --timeout=120s
kubectl rollout status deployment/rag-frontend --timeout=120s
echo "Done! Open Killercoda Traffic / Ports -> 30882"
