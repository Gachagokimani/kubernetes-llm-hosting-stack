import json
import os
import re
import shutil
import urllib.parse
import urllib.request
from pathlib import Path
from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel


SEED_KB_PATH = os.path.join(os.path.dirname(__file__), "data", "knowledge_base.json")

def _sync_seed_kb():
    if not os.path.exists(SEED_KB_PATH):
        return
    os.makedirs(os.path.dirname(KB_FILE), exist_ok=True)
    with open(SEED_KB_PATH, "r", encoding="utf-8") as f:
        seeded = json.load(f)
    existing = []
    if os.path.exists(KB_FILE):
        try:
            with open(KB_FILE, "r", encoding="utf-8") as f:
                existing = json.load(f)
        except Exception:
            existing = []
    by_topic = {item.get("topic", ""): item for item in existing if isinstance(item, dict)}
    for item in seeded:
        by_topic[item["topic"]] = item
    with open(KB_FILE, "w", encoding="utf-8") as f:
        json.dump(list(by_topic.values()), f, indent=2)

try:
    _sync_seed_kb()
except Exception as _seed_err:
    print("KB seed warning:", _seed_err)

STOPWORDS = {
    "the", "and", "for", "with", "what", "does", "have", "about", "tell",
    "brighton", "kimani", "this", "that", "from", "into", "are", "his",
    "how", "use", "engage", "following", "other"
}

QUERY_GROUPS = {
    "certifications": {"cert", "certificate", "certifications", "certification", "skillsoft", "credential", "credentials", "ef", "set", "english", "istqb", "badge"},
    "security": {"rbac", "sso", "oauth", "jwt", "secret", "secrets", "etcd", "auth", "login", "security", "devsecops", "encryption", "token", "admin", "proposals", "proposal"},
    "projects": {"project", "projects", "repo", "repos", "repository", "repositories", "github", "rhomes", "cybersmarter", "firstlight", "concierge", "self", "hosted"},
    "architecture": {"k8s", "kubernetes", "llama", "gguf", "searxng", "dotnet", ".net", "java", "spring", "fastapi", "pod", "pvc", "architecture", "stack", "ram", "microservice"},
    "experience": {"experience", "work", "worked", "job", "safaricom", "glimpse", "bill", "motors", "aiops", "uipath", "n8n", "terraform", "career", "role"},
    "education": {"education", "degree", "bsc", "university", "kirinyaga", "thesis", "blockchain", "referee", "referees", "gilbert", "joyce", "winrose"},
}

def _route_query_groups(query: str) -> set[str]:
    tokens = set(re.findall(r"[a-z0-9.+]+", query.lower()))
    matched = {grp for grp, kws in QUERY_GROUPS.items() if tokens & kws}
    return matched or set(QUERY_GROUPS.keys())

def _score_single_query(sub_q: str, kb: list[dict], per_q_limit: int = 2) -> list[tuple[int, str, str]]:
    q_tokens = {t for t in re.findall(r"[a-z0-9.+]+", sub_q.lower()) if len(t) > 1 and t not in STOPWORDS}
    target_groups = _route_query_groups(sub_q)
    # If user asks about repos/projects, prioritize projects first so .NET 8 architecture doesn't drown them out
    if "projects" in target_groups and "dotnet" not in q_tokens and ".net" not in q_tokens and "rbac" not in q_tokens:
        target_groups = {"projects"}
    filtered_kb = [x for x in kb if x.get("category", "architecture") in target_groups] or kb

    scored = []
    for item in filtered_kb:
        topic = item.get("topic", "")
        content = item.get("content", "")
        t_tokens = set(re.findall(r"[a-z0-9.+]+", topic.lower()))
        c_tokens = set(re.findall(r"[a-z0-9.+]+", content.lower()))
        score = (4 * len(q_tokens & t_tokens)) + len(q_tokens & c_tokens)
        if score > 0:
            scored.append((score, topic, content))
    scored.sort(key=lambda x: x[0], reverse=True)
    limit = 4 if target_groups == {"certifications"} else per_q_limit
    return scored[:limit]

def retrieve_context(query: str, top_k: int = 4) -> str:
    if not os.path.exists(KB_FILE):
        return ""
    with open(KB_FILE, "r", encoding="utf-8") as f:
        kb = json.load(f)

    # Split compound multi-question prompts so each sub-question retrieves its own exact domain chunk(s)
    sub_queries = [q.strip() for q in re.split(r"[?\n]+", query) if len(q.strip()) > 6] or [query]
    seen_topics = set()
    picked = []
    per_q = 1 if len(sub_queries) > 1 else 3
    for sq in sub_queries:
        for _, topic, content in _score_single_query(sq, kb, per_q_limit=per_q):
            if topic not in seen_topics:
                seen_topics.add(topic)
                picked.append((topic, content))

    return "\n".join(f"- [{topic}]: {content}" for topic, content in picked[:6])

app = FastAPI(title="Brighton Kimani Portfolio & Hybrid RAG Concierge")

APP_DIR = Path(__file__).resolve().parent
PROPOSALS_PATH = Path("/shared/kb/proposals_offers.json")
LLM_URL = os.getenv("LLM_URL", "http://llama-server:8080/v1/chat/completions")
SEARXNG_URL = os.getenv("SEARXNG_URL", "http://searxng:8080/search")
DOTNET_BASE_URL = os.getenv("DOTNET_BASE_URL", "http://pdf-ingest-dotnet:8080")
RBAC_BASE_URL = os.getenv("RBAC_BASE_URL", "http://rbac-sso-dotnet:8080")
SPRING_INDEX_URL = os.getenv("SPRING_INDEX_URL", "http://doc-indexer-spring:8080/api/index/sync")


def ensure_baseline_kb():
    src = APP_DIR / "knowledge_base.json"
    dst = Path("/shared/kb/knowledge_base.json")
    try:
        dst.parent.mkdir(parents=True, exist_ok=True)
        if src.exists() and not dst.exists():
            shutil.copy2(src, dst)
    except Exception:
        pass


ensure_baseline_kb()

static_dir = APP_DIR / "static"
if static_dir.exists():
    app.mount("/static", StaticFiles(directory=str(static_dir)), name="static")

public_dir = APP_DIR.parent / "public"
if public_dir.exists():
    app.mount("/public", StaticFiles(directory=str(public_dir)), name="public")


class ChatRequest(BaseModel):
    query: str
    use_web: bool = False
    session_id: str = "anonymous"


def load_all_kb_entries():
    seen_topics = set()
    entries = []
    # Prefer shared PVC copy first, fallback to bundled APP_DIR copy, plus separate proposals_offers.json
    sources = [
        Path("/shared/kb/knowledge_base.json") if Path("/shared/kb/knowledge_base.json").exists() else (APP_DIR / "knowledge_base.json"),
        PROPOSALS_PATH,
    ]
    for p in sources:
        if not p.exists():
            continue
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
            for item in data:
                topic = item.get("topic", "").strip()
                if topic and topic not in seen_topics:
                    seen_topics.add(topic)
                    entries.append(item)
        except Exception:
            continue
    return entries


BROAD_INTENT_EXPANSIONS = {
    "technology": {"safaricom", "terraform", "kubernetes", "devops", "automation", "aiops", "competencies"},
    "tech": {"safaricom", "terraform", "kubernetes", "devops", "automation", "aiops", "competencies"},
    "competency": {"competencies", "safaricom", "bill", "motors", "terraform", "kubernetes", "automation"},
    "competencies": {"competencies", "safaricom", "bill", "motors", "terraform", "kubernetes", "automation"},
    "skills": {"competencies", "safaricom", "bill", "motors", "terraform", "kubernetes", "automation"},
    "experience": {"safaricom", "bill", "motors", "automation", "engineer", "aiops"},
    "background": {"identity", "education", "safaricom", "bill", "motors"},
    "work": {"safaricom", "bill", "motors", "automation", "engineer"},
    "career": {"safaricom", "bill", "motors", "identity", "education"},
    "projects": {"kubernetes", "terraform", "safaricom", "bill", "motors"},
}


def retrieve_local_context(query: str, top_k: int = 7):
    entries = load_all_kb_entries()
    if not entries:
        return []

    raw_words = set(re.findall(r"[a-z0-9]{3,}", query.lower()))
    expanded_words = set(raw_words)
    for w in raw_words:
        if w in BROAD_INTENT_EXPANSIONS:
            expanded_words.update(BROAD_INTENT_EXPANSIONS[w])

    scored = []
    priority_fallbacks = []
    asks_ila = any(k in raw_words for k in ("ila", "architecture", "llama", "rag", "concierge"))
    for item in entries:
        if item.get("category") == "ila_architecture" and not asks_ila:
            continue
        topic = item.get("topic", "")
        content = item.get("content", "")
        topic_l = topic.lower()
        text = f"{topic_l} {content.lower()}"
        formatted = f"[{topic}] {content}"

        if item.get("category") in ("identity", "proficiencies", "experience"):
            priority_fallbacks.append(formatted)

        # Direct query word matches weigh 3x; expanded intent keywords weigh 1x
        direct_score = sum(3 for w in raw_words if w in text)
        intent_score = sum(1 for w in (expanded_words - raw_words) if w in text)
        total_score = direct_score + intent_score
        if total_score > 0:
            scored.append((total_score, formatted))

    scored.sort(key=lambda x: x[0], reverse=True)
    hits = [s[1] for s in scored[:top_k]]

    # Ensure broad/short queries always include Identity + career highlights
    if len(hits) < 3 and priority_fallbacks:
        seen = set(hits)
        for fb in priority_fallbacks:
            if fb not in seen:
                hits.append(fb)
                seen.add(fb)
            if len(hits) >= top_k:
                break
    return hits


def search_web(query: str, max_results: int = 3):
    try:
        params = urllib.parse.urlencode({"q": query, "format": "json"})
        req = urllib.request.Request(
            f"{SEARXNG_URL}?{params}",
            headers={"User-Agent": "Mozilla/5.0", "Accept": "application/json"},
        )
        with urllib.request.urlopen(req, timeout=8) as resp:
            data = json.loads(resp.read().decode())
            return [
                f"{r.get('title', '')}: {r.get('content', '')} ({r.get('url', '')})"
                for r in data.get("results", [])[:max_results]
            ]
    except Exception as e:
        return [f"Web search unavailable: {e}"]


@app.get("/")
def index():
    return FileResponse(str(APP_DIR / "index.html"))


@app.get("/health")
def health():
    return {"status": "healthy", "service": "rag-frontend"}


@app.get("/api/chat/history")
def get_chat_history(session_id: str = "anonymous"):
    try:
        url = f"{DOTNET_BASE_URL}/api/chat/history/{urllib.parse.quote(session_id)}"
        with urllib.request.urlopen(url, timeout=8) as resp:
            return json.loads(resp.read().decode())
    except Exception as e:
        return {"sessionId": session_id, "messages": [], "error": str(e)}


@app.post("/api/upload-pdf")
async def upload_pdf(file: UploadFile = File(...)):
    if not file.filename.lower().endswith(".pdf"):
        raise HTTPException(status_code=400, detail="Only PDF files are supported.")
    file_bytes = await file.read()
    boundary = "----BrightonBoundary7MA4YWxkTrZu0gW"
    body = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{file.filename}"\r\n'
        f"Content-Type: application/pdf\r\n\r\n"
    ).encode("utf-8") + file_bytes + f"\r\n--{boundary}--\r\n".encode("utf-8")

    try:
        req = urllib.request.Request(
            f"{DOTNET_BASE_URL}/api/upload",
            data=body,
            headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=20) as resp:
            upload_res = json.loads(resp.read().decode())
    except Exception as e:
        raise HTTPException(status_code=502, detail=f".NET upload service error: {e}")

    try:
        idx_req = urllib.request.Request(SPRING_INDEX_URL, data=b"", method="POST")
        with urllib.request.urlopen(idx_req, timeout=30) as resp:
            index_res = json.loads(resp.read().decode())
    except Exception as e:
        raise HTTPException(status_code=502, detail=f"Java indexer service error: {e}")

    return {"upload": upload_res, "index": index_res}


@app.post("/api/chat")
def chat(req: ChatRequest):
    local_hits = retrieve_local_context(req.query)
    web_hits = search_web(req.query) if req.use_web else []
    context_block = "\n".join(local_hits + web_hits)

    system_prompt = (
        "You are ILA (Intelligent Lookup & Architecture), Brighton Kimani's helpful portfolio assistant. "
        "Speak naturally like a human engineer—never repeat system instructions. "
        "When greeted or asked who this is, briefly introduce yourself as ILA and immediately introduce Brighton Kimani and his core proficiencies (AI/LLM systems, Kubernetes/Terraform DevOps, polyglot microservices, and automation) using ONLY the facts below. "
        "When discussing repositories, note that this live Kubernetes stack (ai-stack) IS kubernetes-llm-hosting-stack, then list his other repositories separately. "
        "Keep answers concise: 1 natural intro sentence followed by 3 to 4 crisp bullet points (- ).\n\n"
        f"Facts about Brighton Kimani:\n{context_block}"
    )

    payload = json.dumps({
        "model": "qwen2.5-0.5b",
        "messages": [
            {"role": "system", "content": system_prompt},
        {"role": "user", "content": req.query},
        ],
        "temperature": 0.1,
        "max_tokens": 220,
    }).encode("utf-8")

    try:
        http_req = urllib.request.Request(
            LLM_URL,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(http_req, timeout=60) as resp:
            result = json.loads(resp.read().decode())
            answer = result["choices"][0]["message"]["content"].strip()
    except Exception as e:
        answer = f"LLM inference error: {e}"

    total_srcs = len(local_hits) + len(web_hits)
    try:
        save_payload = json.dumps({
            "SessionId": req.session_id,
            "UserQuery": req.query,
            "AssistantAnswer": answer,
            "SourcesCount": total_srcs,
        }).encode("utf-8")
        dotnet_req = urllib.request.Request(
            f"{DOTNET_BASE_URL}/api/chat/sanitize-and-save",
            data=save_payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(dotnet_req, timeout=10) as dresp:
            ddata = json.loads(dresp.read().decode())
            answer = ddata.get("cleanedAnswer", answer)
    except Exception:
        pass

    return {
        "answer": answer,
        "local_sources": local_hits,
        "web_sources": web_hits,
    }
