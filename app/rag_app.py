import os, math, re, urllib.request, urllib.parse, json
from collections import Counter
from pathlib import Path
from fastapi import FastAPI, UploadFile, File, HTTPException
from fastapi.responses import HTMLResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

app = FastAPI(title="Brighton Kimani Portfolio RAG API")

APP_DIR = Path(__file__).resolve().parent
import shutil

PROPOSALS_PATH = Path("/shared/kb/proposals_offers.json")

def ensure_baseline_kb():
    for target in [Path("/shared/kb/knowledge_base.json"), APP_DIR / "knowledge_base.json"]:
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            if not target.exists() and (APP_DIR / "knowledge_base.json").exists() and target != (APP_DIR / "knowledge_base.json"):
                shutil.copy2(APP_DIR / "knowledge_base.json", target)
        except Exception:
            pass

ensure_baseline_kb()

if (APP_DIR / "static").exists():
    app.mount("/static", StaticFiles(directory=str(APP_DIR / "static")), name="static")
if (APP_DIR.parent / "public").exists():
    app.mount("/public", StaticFiles(directory=str(APP_DIR.parent / "public")), name="public")

LLAMA_URL = os.getenv("LLAMA_URL", "http://llama-server:8080/v1/chat/completions")
SEARXNG_URL = os.getenv("SEARXNG_URL", "http://searxng:8080/search")
DOTNET_UPLOAD_URL = os.getenv("DOTNET_UPLOAD_URL", "http://pdf-ingest-dotnet:5000/api/pdf/upload")
SPRING_INDEX_URL = os.getenv("SPRING_INDEX_URL", "http://doc-indexer-spring:8080/api/index/sync")
SHARED_KB_FILE = Path(os.getenv("KB_FILE", "/shared/kb/knowledge_base.json"))

def load_knowledge_base():
    entries = []
    for path in [APP_DIR / "knowledge_base.json", SHARED_KB_FILE]:
        if path.exists():
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                entries.extend([f"[{item['topic']}] {item['content']}" for item in data])
            except Exception:
                pass
    return entries

def cosine_sim(a: str, b: str) -> float:
    wa = Counter(re.findall(r"\w+", a.lower()))
    wb = Counter(re.findall(r"\w+", b.lower()))
    common = set(wa.keys()) & set(wb.keys())
    num = sum(wa[w] * wb[w] for w in common)
    den = math.sqrt(sum(v*v for v in wa.values())) * math.sqrt(sum(v*v for v in wb.values()))
    return num / den if den else 0.0

def retrieve_local(query: str, top_k: int = 3):
    docs = load_knowledge_base()
    scored = sorted(docs, key=lambda d: cosine_sim(query, d), reverse=True)
    return [d for d in scored[:top_k] if cosine_sim(query, d) > 0.04]

def search_web(query: str, max_results: int = 2):
    if not SEARXNG_URL:
        return []
    try:
        url = f"{SEARXNG_URL}?q={urllib.parse.quote(query)}&format=json"
        req = urllib.request.Request(url, headers={"User-Agent": "Brighton-Portfolio-Agent/1.0"})
        with urllib.request.urlopen(req, timeout=5) as resp:
            data = json.loads(resp.read().decode())
            return [f"{r.get('title', '')}: {r.get('content', '')}" for r in data.get("results", [])[:max_results]]
    except Exception as e:
        return [f"(Web search unavailable: {e})"]

class ChatRequest(BaseModel):
    query: str
    use_web: bool = False

@app.get("/health")
def health():
    return {
        "status": "ok",
        "kb_entries": len(load_knowledge_base()),
        "shared_kb_mounted": SHARED_KB_FILE.parent.exists(),
    }

@app.get("/", response_class=HTMLResponse)
def ui():
    return (APP_DIR / "index.html").read_text(encoding="utf-8")

@app.post("/api/upload-pdf")
async def upload_pdf(file: UploadFile = File(...)):
    raw = await file.read()
    if not raw.startswith(b"%PDF-"):
        raise HTTPException(status_code=400, detail="Only valid PDF files are accepted.")

    boundary = "----Boundary7MA4YWxkTrZu0gW"
    body = (
        f"--{boundary}\r\n"
        f"Content-Disposition: form-data; name=\"file\"; filename=\"{file.filename}\"\r\n"
        f"Content-Type: application/pdf\r\n\r\n"
    ).encode("utf-8") + raw + f"\r\n--{boundary}--\r\n".encode("utf-8")

    req = urllib.request.Request(
        DOTNET_UPLOAD_URL,
        data=body,
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=20) as resp:
        upload_res = json.loads(resp.read().decode())

    idx_req = urllib.request.Request(SPRING_INDEX_URL, data=b"", method="POST")
    with urllib.request.urlopen(idx_req, timeout=30) as resp:
        index_res = json.loads(resp.read().decode())

    return {"upload": upload_res, "index": index_res}

@app.post("/api/chat")
def chat(req: ChatRequest):
    local_ctx = retrieve_local(req.query)
    web_ctx = search_web(req.query) if req.use_web else []
    context_block = ""
    if local_ctx:
        context_block += "Verified Portfolio & Uploaded PDF Context:\n- " + "\n- ".join(local_ctx) + "\n\n"
    if web_ctx:
        context_block += "Live Web Search Context:\n- " + "\n- ".join(web_ctx) + "\n\n"

    system_prompt = (
        "You are Brighton Kimani's AI Portfolio Concierge. "
        "Answer strictly from the provided Verified Portfolio & Uploaded PDF Context when relevant. "
        "Never invent facts not present in context."
    )
    payload = json.dumps({
        "model": "qwen2.5-0.5b",
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": f"{context_block}Question: {req.query}"}
        ],
        "temperature": 0.2,
        "max_tokens": 320
    }).encode("utf-8")

    http_req = urllib.request.Request(LLAMA_URL, data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(http_req, timeout=60) as resp:
        answer = json.loads(resp.read().decode())["choices"][0]["message"]["content"]

    return {"answer": answer, "local_sources": local_ctx, "web_sources": web_ctx}
