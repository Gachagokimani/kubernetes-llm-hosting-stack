# Brighton Kimani Portfolio — UI Design System Specification

## 1. Brand Color Tokens (Single Source of Truth)
- **Base Canvas (`--bg-jungle-deep`):** `#091913`
- **Primary Surface (`--bg-jungle-surface`):** `#10281f`
- **Elevated Surface (`--bg-jungle-elevated`):** `#17382b`
- **Structural Accent (`--jungle-mint` / `--jungle-highlight`):** `#74c69d` / `#40916c`
- **Primary Action (`--crimson`):** `#e11d48`
- **Interactive / AI Accent (`--turquoise`):** `#06b6d4`

## 2. Iconography Policy (Strict Zero-Emoji Rule)
- **Allowed Libraries:** Lucide Icons (`<i data-lucide="..."></i>`) and Material UI / Google Material Symbols (`<span class="material-symbols-outlined">...</span>`) ONLY.
- **Prohibited:** Raw Unicode emojis or HTML entity pictographs (`&#128205;`, `&#9889;`, `&#9733;`) are strictly prohibited across headings, status pills, badges, buttons, and contact metadata.

## 3. Motion & Hover Choreography
- **Easing Curve (`--ease-out-expo`):** `cubic-bezier(0.22, 1, 0.36, 1)` over `250ms` (`--transition-base`).
- **Surface Card Hover:** `translateY(-4px)` elevation lift with border illumination (`--jungle-mint` or `--crimson`).
- **Button Hover:** `translateY(-2px)` lift with directional glow bloom and icon nudge (`translateX(3px)`).
- **Timeline Node Hover:** `translateX(4px)` shift with crimson node box-shadow pulse.

## 4. Human-Centric Call-to-Action (CTA) Standard
- Pair a clear action verb with an explicit user benefit (e.g., *"Explore My Engineering Work"*, *"Inspect Architecture & Source"*, *"Browse All Repositories on GitHub"*, *"Ask the Live AI Concierge"*).
