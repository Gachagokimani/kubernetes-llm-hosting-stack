import os, math, re, urllib.request, urllib.parse, json
from collections import Counter
from pathlib import Path
from fastapi import FastAPI
from fastapi.responses import HTMLResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

app = FastAPI(title="Brighton Kimani Portfolio RAG API")

APP_DIR = Path(__file__).resolve().parent
if (APP_DIR / "static").exists():
    app.mount("/static", StaticFiles(directory=str(APP_DIR / "static")), name="static")
LLAMA_URL = os.getenv("LLAMA_URL", "http://llama-server:8080/v1/chat/completions")
SEARXNG_URL = os.getenv("SEARXNG_URL", "")

def load_knowledge_base():
    kb_path = APP_DIR / "knowledge_base.json"
    if kb_path.exists():
        data = json.loads(kb_path.read_text(encoding="utf-8"))
        return [f"[{item['topic']}] {item['content']}" for item in data]
    return []

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
        return ["(Web search disabled: SEARXNG_URL not configured)"]
    try:
        url = f"{SEARXNG_URL}?q={urllib.parse.quote(query)}&format=json"
        req = urllib.request.Request(url, headers={"User-Agent": "Brighton-Portfolio-Agent/1.0"})
        with urllib.request.urlopen(req, timeout=5) as resp:
            data = json.loads(resp.read().decode())
            results = data.get("results", [])[:max_results]
            return [f"{r.get('title', '')}: {r.get('content', '')}" for r in results]
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
        "llama_url": LLAMA_URL,
        "searxng_configured": bool(SEARXNG_URL),
    }

@app.get("/", response_class=HTMLResponse)
def ui():
    return (APP_DIR / "index.html").read_text(encoding="utf-8")

@app.post("/api/chat")
def chat(req: ChatRequest):
    local_ctx = retrieve_local(req.query)
    web_ctx = search_web(req.query) if req.use_web else []
    context_block = ""
    if local_ctx:
        context_block += "Verified Portfolio & CV Facts:\n- " + "\n- ".join(local_ctx) + "\n\n"
    if web_ctx:
        context_block += "Live Web Search Context:\n- " + "\n- ".join(web_ctx) + "\n\n"

    system_prompt = (
        "You are Brighton Kimani's AI Portfolio Concierge. "
        "1. When asked about Brighton Kimani, his work history, skills, or projects, answer strictly from Verified Portfolio & CV Facts. "
        "2. When asked general technical questions (Kubernetes, DevOps, AI, Linux), answer clearly and concisely as an expert SRE/AI engineer. "
        "3. Never invent career details not in context."
    )
    user_prompt = f"{context_block}Question: {req.query}"

    payload = json.dumps({
        "model": "qwen2.5-0.5b",
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt}
        ],
        "temperature": 0.25,
        "max_tokens": 320
    }).encode("utf-8")

    http_req = urllib.request.Request(LLAMA_URL, data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(http_req, timeout=60) as resp:
        llm_data = json.loads(resp.read().decode())
        answer = llm_data["choices"][0]["message"]["content"]

    return {"answer": answer, "local_sources": local_ctx, "web_sources": web_ctx}
