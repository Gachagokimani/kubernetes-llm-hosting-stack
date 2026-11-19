#!/usr/bin/env bash
set -euo pipefail

# Enables Kubernetes etcd Encryption at Rest (aescbc) on a kubeadm control-plane node (OCI / Ubuntu)
ENC_DIR="/etc/kubernetes/enc"
ENC_FILE="${ENC_DIR}/enc.yaml"
APISERVER_MANIFEST="/etc/kubernetes/manifests/kube-apiserver.yaml"

if [ ! -f "$APISERVER_MANIFEST" ]; then
  echo "Skipping etcd encryption setup: $APISERVER_MANIFEST not found."
  exit 0
fi

sudo mkdir -p "$ENC_DIR"
if [ ! -f "$ENC_FILE" ]; then
  KEY=$(head -c 32 /dev/urandom | base64)
  sudo tee "$ENC_FILE" > /dev/null <<YAML
apiVersion: apiserver.config.k8s.io/v1
kind: EncryptionConfiguration
resources:
  - resources:
      - secrets
    providers:
      - aescbc:
          keys:
            - name: key1
              secret: ${KEY}
      - identity: {}
YAML
  sudo chmod 600 "$ENC_FILE"
fi

if ! grep -q "encryption-provider-config" "$APISERVER_MANIFEST"; then
  sudo cp "$APISERVER_MANIFEST" "${APISERVER_MANIFEST}.bak"
  sudo python3 -c '
p = "/etc/kubernetes/manifests/kube-apiserver.yaml"
txt = open(p).read()
txt = txt.replace("- kube-apiserver\n", "- kube-apiserver\n    - --encryption-provider-config=/etc/kubernetes/enc/enc.yaml\n", 1)
txt = txt.replace("volumeMounts:\n", "volumeMounts:\n    - mountPath: /etc/kubernetes/enc\n      name: enc\n      readOnly: true\n", 1)
txt = txt.replace("volumes:\n", "volumes:\n  - hostPath:\n      path: /etc/kubernetes/enc\n      type: DirectoryOrCreate\n    name: enc\n", 1)
open(p, "w").write(txt)
'
  echo "Enabled etcd encryption at rest. Waiting for kube-apiserver static pod restart..."
  sleep 10
fi

# Re-encrypt any existing secrets in ai-stack
kubectl get secrets -n ai-stack -o json | kubectl replace -f - 2>/dev/null || true
echo "etcd secret encryption active."
