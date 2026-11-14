@echo off
setlocal
cd /d "%~dp0"

set "MODEL_REPO=Qwen/Qwen2.5-0.5B-Instruct-GGUF"
set "MODEL_FILE=qwen2.5-0.5b-instruct-q4_k_m.gguf"

echo === AI Stack Bootstrapper for Windows CMD ===

python --version >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Python 3 not found. Install it from python.org and tick Add to PATH.
    exit /b 1
)

if not exist ".venv\Scripts\activate.bat" (
    python -m venv .venv || goto :fail
)
call ".venv\Scripts\activate.bat"
python -m pip install --quiet --upgrade pip || goto :fail
python -m pip install --quiet -r requirements.txt || goto :fail

if not exist "models\%MODEL_FILE%" (
    echo Downloading %MODEL_FILE% ...
    python -c "from huggingface_hub import hf_hub_download; hf_hub_download('%MODEL_REPO%', '%MODEL_FILE%', local_dir='models')" || goto :fail
)

if "%LLAMA_URL%"=="" (
    where llama-server >nul 2>nul
    if errorlevel 1 (
        echo [ERROR] llama-server.exe not in PATH and LLAMA_URL is not set.
        echo         Get the Windows build from the llama.cpp releases page, or set LLAMA_URL.
        exit /b 1
    )
    echo Starting llama-server on 127.0.0.1:8080 ...
    start "LlamaServer" /min llama-server -m "models\%MODEL_FILE%" --host 127.0.0.1 --port 8080 -c 2048
    set "LLAMA_URL=http://127.0.0.1:8080/v1/chat/completions"
    for /l %%i in (1,1,60) do (
        curl -fs http://127.0.0.1:8080/health >nul 2>nul && goto :llama_ready
        timeout /t 2 /nobreak >nul
    )
    echo [ERROR] llama-server did not become healthy.
    exit /b 1
)
:llama_ready

if "%SEARXNG_URL%"=="" echo [WARN] SEARXNG_URL not set. Web Search will not work until you set it.

echo Starting UI at http://127.0.0.1:8000 ...
echo When finished, stop the model server with: taskkill /im llama-server.exe /f
uvicorn rag_app:app --app-dir app --host 127.0.0.1 --port 8000
exit /b %errorlevel%

:fail
echo [ERROR] A setup step failed. See the messages above.
exit /b 1
