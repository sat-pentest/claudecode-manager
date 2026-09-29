"""CCM README 모션 에셋 생성기.

GitHub README 는 <img> 로 SVG 를 싣기 때문에 SVG 안의 <script> 는 실행되지 않는다.
CSS 애니메이션과 SMIL 은 동작하므로 모든 움직임은 그 둘로만 만든다.
폰트는 임베드하지 않고 글리프를 패스로 변환한다 — 뷰어 환경에 상관없이 같게 보이고
용량도 서브셋 폰트보다 작다.
"""
import os
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen

FONTDIR = r"D:\10. RED_TEAM\AI\AI Control\src\ClaudeCodeManager.App\Fonts"
OUT     = r"D:\10. RED_TEAM\AI\AI Control\docs\assets"

PIXEL = os.path.join(FONTDIR, "PressStart2P-Regular.ttf")
PLEX  = os.path.join(FONTDIR, "IBMPlexSansKR-Medium.ttf")
CRT   = os.path.join(FONTDIR, "VT323-Regular.ttf")

# RustTheme.xaml 원본값
BG, PANEL, SUNKEN = "#0A0908", "#14110F", "#050403"
RUST, EMBER, RUSTDIM = "#D9441C", "#F26A2E", "#7A2812"
BORDER, SOFT = "#4A1F0E", "#2A1408"
TEXT, MUTED, DIM = "#E8DDD0", "#9A8A78", "#5A4F44"
SUCCESS, WARN = "#6FA844", "#D9A02C"

_cache = {}


def textpath(s, fontfile, size, x=0, y=0, letter=0.0):
    """문자열을 단일 SVG path d 로. (d, 그려진 폭) 반환."""
    if fontfile not in _cache:
        _cache[fontfile] = TTFont(fontfile)
    f = _cache[fontfile]
    upm = f["head"].unitsPerEm
    scale = size / upm
    cmap = f.getBestCmap()
    gs = f.getGlyphSet()
    hmtx = f["hmtx"]
    out, pen_x = [], 0.0
    for ch in s:
        gn = cmap.get(ord(ch))
        if gn is None:
            pen_x += size * 0.6 + letter
            continue
        pen = SVGPathPen(gs)
        gs[gn].draw(pen)
        d = pen.getCommands()
        if d:
            # y 축 뒤집기(폰트 좌표계는 위가 +)
            # clipPath 자식으로 <g> 를 쓰면 SVG 규격상 무시된다 —
            # transform 을 path 에 직접 걸어야 클립과 일반 렌더 양쪽에서 동작한다.
            out.append(
                f'<path transform="translate({x + pen_x:.2f},{y:.2f}) '
                f'scale({scale:.5f},{-scale:.5f})" d="{d}"/>'
            )
        pen_x += hmtx[gn][0] * scale + letter
    return "".join(out), pen_x


def measure(s, fontfile, size, letter=0.0):
    return textpath(s, fontfile, size, letter=letter)[1]


def scanlines(w, h, op=0.05, step=3):
    return (
        f'<pattern id="scan" width="{step}" height="{step}" patternUnits="userSpaceOnUse">'
        f'<rect width="{step}" height="1" fill="{EMBER}" opacity="{op}"/></pattern>'
    )


# ══════════════════════════════════════════════════════════════════
# 1. HERO — 앱 타이틀바의 ember 스캔 연출을 그대로
# ══════════════════════════════════════════════════════════════════
def hero():
    W, H = 1280, 420
    t1, s1 = "CLAUDECODE", 64
    t2, s2 = "MANAGER", 64
    x0, y1, y2 = 78, 200, 274
    p1, w1 = textpath(t1, PIXEL, s1, x0, y1)
    p2, w2 = textpath(t2, PIXEL, s2, x0, y2)
    tagw = max(w1, w2)

    tag, _ = textpath("Claude Code 운영 자산을 한 화면에서", PLEX, 21, x0 + 3, 334)
    meta, _ = textpath("v1.7   .NET 8 · WPF   15 MODULES   WINDOWS", CRT, 26, x0 + 3, 374, letter=1.2)
    kicker, _ = textpath("INTERNAL TOOL", PIXEL, 13, x0 + 3, 104, letter=1.5)

    # 오른쪽 글리프 성좌 (모듈 아이콘 원본 패스)
    from glyphs import GLYPHS
    cells = []
    for i, d in enumerate(GLYPHS):
        cx = 905 + (i % 3) * 118
        cy = 104 + (i // 3) * 64
        hot = (i == 6)
        cells.append(
            f'<g class="gl g{i}" transform="translate({cx},{cy}) scale(2.05)">'
            f'<path d="{d}" fill="none" stroke="{EMBER if hot else RUSTDIM}" '
            f'stroke-width="1.5" stroke-linecap="square"/></g>'
        )
    glyphs = "".join(cells)
    stagger = "\n".join(
        f".g{i}{{animation-delay:{i*0.16:.2f}s}}" for i in range(len(GLYPHS))
    )

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="ClaudeCode Manager">
<defs>
  <linearGradient id="sweep" x1="0" x2="1">
    <stop offset="0%"   stop-color="{EMBER}" stop-opacity="0"/>
    <stop offset="45%"  stop-color="#FFC9A0" stop-opacity="1"/>
    <stop offset="55%"  stop-color="#FFC9A0" stop-opacity="1"/>
    <stop offset="100%" stop-color="{EMBER}" stop-opacity="0"/>
  </linearGradient>
  <radialGradient id="amb" cx="78%" cy="50%" r="62%">
    <stop offset="0%"   stop-color="{RUST}" stop-opacity=".20"/>
    <stop offset="45%"  stop-color="{RUST}" stop-opacity=".06"/>
    <stop offset="100%" stop-color="{RUST}" stop-opacity="0"/>
  </radialGradient>
  <filter id="glow" x="-40%" y="-60%" width="180%" height="220%">
    <feGaussianBlur stdDeviation="4.5" result="b"/>
    <feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge>
  </filter>
  <filter id="softglow" x="-60%" y="-60%" width="220%" height="220%">
    <feGaussianBlur stdDeviation="3"/>
  </filter>
  <clipPath id="titleclip">{p1}{p2}</clipPath>
  {scanlines(W, H)}
<style><![CDATA[
  .title    {{ fill:{RUST}; filter:url(#glow); animation:breathe 4.2s ease-in-out infinite }}
  .sweepbar {{ animation:travel 5.2s cubic-bezier(.5,0,.5,1) infinite }}
  .gl       {{ opacity:0; animation:pop 5.2s ease-out infinite }}
  .rule     {{ animation:rulegrow 5.2s ease-out infinite }}
  .cursor   {{ animation:blink 1.06s steps(1) infinite }}
  @keyframes breathe  {{ 0%,100%{{opacity:.93}} 50%{{opacity:1}} }}
  @keyframes travel   {{ 0%{{transform:translateX(-460px)}} 62%,100%{{transform:translateX(1180px)}} }}
  @keyframes pop      {{ 0%{{opacity:0;transform:scale(.6)}} 12%{{opacity:1;transform:scale(1)}}
                         78%{{opacity:1}} 100%{{opacity:.85}} }}
  @keyframes rulegrow {{ 0%{{transform:scaleX(0)}} 30%,100%{{transform:scaleX(1)}} }}
  @keyframes blink    {{ 0%,50%{{opacity:1}} 51%,100%{{opacity:0}} }}
  @media (prefers-reduced-motion:reduce){{
    .title,.sweepbar,.gl,.rule,.cursor{{animation:none}} .gl{{opacity:1}}
  }}
]]></style>
</defs>

<rect width="{W}" height="{H}" fill="{BG}"/>
<rect width="{W}" height="{H}" fill="url(#amb)"/>
<g opacity=".55">{glyphs}</g>
<rect width="{W}" height="{H}" fill="url(#scan)"/>

<g fill="{EMBER}" opacity=".9">{kicker}</g>
<rect x="{x0+3}" y="113" width="9" height="13" fill="{EMBER}" class="cursor"/>

<g class="title">{p1}{p2}</g>
<g clip-path="url(#titleclip)">
  <rect class="sweepbar" x="0" y="120" width="460" height="200" fill="url(#sweep)" opacity=".85"/>
</g>

<rect class="rule" x="{x0+3}" y="302" width="{tagw*0.86:.0f}" height="2" fill="{RUST}"
      style="transform-origin:{x0+3}px 303px"/>
<g fill="{TEXT}" opacity=".93">{tag}</g>
<g fill="{MUTED}">{meta}</g>

<style>{stagger}</style>
</svg>'''



def modules():
    from glyphs import GLYPHS, NAMES
    W, COLS = 1280, 5
    CW, CH, GAP = 236, 86, 12
    PAD = (W - (COLS * CW + (COLS - 1) * GAP)) // 2
    ROWS = (len(GLYPHS) + COLS - 1) // COLS
    H = PAD + ROWS * (CH + GAP) - GAP + PAD

    cards, delays = [], []
    for i, (d, nm) in enumerate(zip(GLYPHS, NAMES)):
        cx = PAD + (i % COLS) * (CW + GAP)
        cy = PAD + (i // COLS) * (CH + GAP)
        label, _ = textpath(nm, PIXEL, 11, cx + 60, cy + 38, letter=1.1)
        cards.append(
            f'<g class="card c{i}">'
            f'<rect x="{cx}" y="{cy}" width="{CW}" height="{CH}" fill="{PANEL}" '
            f'stroke="{BORDER}" class="cbox"/>'
            f'<g transform="translate({cx+18},{cy+26}) scale(1.6)">'
            f'<path d="{d}" fill="none" stroke="{EMBER}" stroke-width="1.6" '
            f'stroke-linecap="square" class="ico"/></g>'
            f'<g fill="{EMBER}" class="lbl">{label}</g>'
            f'<rect x="{cx}" y="{cy}" width="3" height="{CH}" fill="{RUST}" class="edge"/>'
            f'</g>'
        )
        # 좌→우, 위→아래 순서로 훑기
        delays.append(f".c{i}{{--d:{(i % COLS) * 0.22 + (i // COLS) * 0.9:.2f}s}}")

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="15 modules">
<defs>{scanlines(W, H, .04)}
<style><![CDATA[
  /* animation-delay 는 상속되지 않으므로 카드마다 --d 를 주고 자식이 같이 읽는다 */
  .card {{ animation:wash 6.6s ease-in-out infinite var(--d,0s) }}
  .ico  {{ stroke-dasharray:240; stroke-dashoffset:240;
           animation:draw 6.6s ease-out infinite var(--d,0s) }}
  .edge {{ opacity:0; animation:edge 6.6s ease-in-out infinite var(--d,0s) }}
  @keyframes wash {{ 0%,6%{{opacity:.45}} 14%{{opacity:1}} 34%{{opacity:.92}} 100%{{opacity:.92}} }}
  @keyframes draw {{ 0%{{stroke-dashoffset:240}} 18%{{stroke-dashoffset:0}} 100%{{stroke-dashoffset:0}} }}
  @keyframes edge {{ 0%,7%{{opacity:0}} 13%{{opacity:1}} 30%{{opacity:.28}} 100%{{opacity:.28}} }}
  @media (prefers-reduced-motion:reduce){{
    .card,.ico,.edge{{animation:none}} .ico{{stroke-dashoffset:0}} .edge{{opacity:.28}}
  }}
]]></style></defs>
<rect width="{W}" height="{H}" fill="{BG}"/>
{"".join(cards)}
<rect width="{W}" height="{H}" fill="url(#scan)"/>
<style>{chr(10).join(delays)}</style>
</svg>'''


# ══════════════════════════════════════════════════════════════════
# 3. PIPELINE — WORKFLOWS 모듈의 파이프라인 스윕 (이미 켜진 LED 가 더 밝아짐)
# ══════════════════════════════════════════════════════════════════
def pipeline():
    PHASES = ["Scope Check", "Recon Deep", "Surface Map", "Content Discovery",
              "Vuln Hunt", "Automated Scanning", "Chain Exploration"]
    W, H = 1280, 150
    n = len(PHASES)
    CW, GAP = 156, 22
    total = n * CW + (n - 1) * GAP
    x0 = (W - total) // 2
    CY, CH = 40, 66
    dur = 5.6

    parts, delays = [], []
    for i, ph in enumerate(PHASES):
        cx = x0 + i * (CW + GAP)
        num, _ = textpath(f"{i+1:02d}", CRT, 22, cx + 13, CY + 26)
        # 긴 이름은 두 줄
        words = ph.split()
        if len(ph) > 12 and len(words) > 1:
            l1, _ = textpath(words[0], PIXEL, 9, cx + 13, CY + 44, letter=.8)
            l2, _ = textpath(" ".join(words[1:]), PIXEL, 9, cx + 13, CY + 57, letter=.8)
            lab = l1 + l2
        else:
            lab, _ = textpath(ph, PIXEL, 9, cx + 13, CY + 46, letter=.8)
        parts.append(
            f'<g class="st s{i}">'
            f'<rect x="{cx}" y="{CY}" width="{CW}" height="{CH}" fill="{PANEL}" stroke="{BORDER}"/>'
            f'<rect x="{cx}" y="{CY}" width="{CW}" height="{CH}" fill="{RUST}" class="flash"/>'
            f'<g fill="{RUSTDIM}" class="num">{num}</g>'
            f'<g fill="{MUTED}" class="lab">{lab}</g>'
            f'<rect x="{cx}" y="{CY+CH-2}" width="{CW}" height="2" fill="{EMBER}" class="bar"/>'
            f'</g>'
        )
        if i < n - 1:
            ax = cx + CW + 4
            parts.append(
                f'<path class="arw a{i}" d="M{ax},{CY+CH/2} h10 m-4,-4 l4,4 l-4,4" '
                f'fill="none" stroke="{RUSTDIM}" stroke-width="1.6"/>'
            )
            delays.append(f".a{i}{{--d:{i*(dur/n)+0.35:.2f}s}}")
        delays.append(f".s{i}{{--d:{i*(dur/n):.2f}s}}")

    c1, w1c = textpath("WORKFLOWS", PIXEL, 11, x0, 22, letter=1.4)
    c2, _ = textpath("PIPELINE", PIXEL, 11, x0 + w1c + 26, 22, letter=1.4)
    tri = (f'<path d="M{x0+w1c+11},16 l7,3.5 l-7,3.5 z" fill="{RUSTDIM}"/>')
    cap = c1 + tri + c2

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="workflow pipeline">
<defs>{scanlines(W, H, .035)}
<style><![CDATA[
  /* 꺼진 LED 가 켜지는 게 아니라, 켜진 것이 한 번 더 강조된다 */
  .flash {{ opacity:0;      animation:flash {dur}s ease-out infinite var(--d,0s) }}
  .bar   {{ opacity:.30;    animation:bar   {dur}s ease-out infinite var(--d,0s) }}
  .num   {{                 animation:num   {dur}s ease-out infinite var(--d,0s) }}
  .arw   {{ opacity:.35;    animation:arw   {dur}s ease-out infinite var(--d,0s) }}
  @keyframes flash {{ 0%{{opacity:0}} 5%{{opacity:.16}} 16%{{opacity:0}} 100%{{opacity:0}} }}
  @keyframes bar   {{ 0%{{opacity:.30}} 5%{{opacity:1}} 22%{{opacity:.30}} 100%{{opacity:.30}} }}
  @keyframes num   {{ 0%{{fill:{RUSTDIM}}} 5%{{fill:{EMBER}}} 24%{{fill:{RUSTDIM}}} 100%{{fill:{RUSTDIM}}} }}
  @keyframes arw   {{ 0%{{opacity:.35}} 6%{{opacity:1}} 26%{{opacity:.35}} 100%{{opacity:.35}} }}
  @media (prefers-reduced-motion:reduce){{ .flash,.bar,.num,.arw{{animation:none}} }}
]]></style></defs>
<rect width="{W}" height="{H}" fill="{BG}"/>
<g fill="{EMBER}" opacity=".85">{cap}</g>
{"".join(parts)}
<rect width="{W}" height="{H}" fill="url(#scan)"/>
<style>{chr(10).join(delays)}</style>
</svg>'''



if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, fn in (("hero", hero), ("modules", modules), ("pipeline", pipeline)):
        path = os.path.join(OUT, name + ".svg")
        open(path, "w", encoding="utf-8").write(fn())
        print(f"{name}.svg  {os.path.getsize(path):>7,} bytes")


# ══════════════════════════════════════════════════════════════════
# 2. MODULES — 글리프가 선을 그리며 나타나고, 하이라이트가 카드를 훑는다
# ══════════════════════════════════════════════════════════════════
