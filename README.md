<div align="center">

<img src="docs/assets/hero.svg" alt="ClaudeCode Manager" width="100%">

<br>

![version](https://img.shields.io/badge/version-v1.7-D9441C?style=flat-square&labelColor=14110F)
![dotnet](https://img.shields.io/badge/.NET-8.0-F26A2E?style=flat-square&labelColor=14110F)
![ui](https://img.shields.io/badge/UI-WPF-7A2812?style=flat-square&labelColor=14110F)
![platform](https://img.shields.io/badge/platform-Windows%20x64-9A8A78?style=flat-square&labelColor=14110F)
![modules](https://img.shields.io/badge/modules-15-6FA844?style=flat-square&labelColor=14110F)

</div>

<br>

Claude Code의 운영 자산은 전부 홈 디렉터리 아래 흩어진 파일입니다. 스킬은 폴더마다 `SKILL.md`,
메모리는 마크다운, MCP·훅·권한은 각각 다른 JSON에 있습니다. 무엇이 설치돼 있는지 보려면 에디터를 열고,
동작을 확인하려면 CLI를 두드리고, 비용은 또 다른 경로를 뒤져야 합니다.

**CCM은 그 파일들을 읽고 · 고치고 · 되돌리는 한 곳으로 모읍니다.** 편집은 원본 파일에 그대로 반영되고,
별도 DB나 동기화 계층을 두지 않습니다.

<br>

## Modules

<img src="docs/assets/modules.svg" alt="15 modules" width="100%">

<br>

## Workflows

워크플로 스크립트를 파이프라인 도식으로 펼쳐, 어떤 단계가 어떤 순서로 도는지 보여줍니다.
`LIVE` 탭은 실행 중인 워크플로를 실시간으로, `SAFETY` 탭은 실행 범위를 제한하는 안전 계층을 GUI로 통제합니다.

<img src="docs/assets/pipeline.svg" alt="workflow pipeline" width="100%">

<br>

## 무엇이 들어 있나

<table>
<tr>
<td width="50%" valign="top">

### 🧠 Memory Link Graph

메모리 문서끼리 `[[위키 링크]]`로 서로를 참조하지만, 목록 화면으로는 그 구조가 보이지 않습니다.
링크를 파싱해 힘 기반 레이아웃으로 배치하고, 세 가지로 나눠 칠합니다.

| 분류 | 뜻 | 대응 |
|---|---|---|
| **연결됨** | 대상 파일이 있음 | — |
| **오타** | 철자만 다르고 파일은 있음 | 고칠 것 |
| **미작성** | 아직 없는 문서 | 써야 할 것 |

오타와 미작성을 구분하는 게 핵심입니다. 대응이 완전히 다릅니다.

</td>
<td width="50%" valign="top">

### 🛡 Harness & Snapshots

`HARNESS`는 실행 트리와 저장소·보존 정책을 보여주고,
`SNAPSHOTS`는 설정 전체를 시점 단위로 묶어 저장합니다.

설정을 크게 바꾸기 전에 스냅샷을 찍어두면 **어떤 파일이 바뀌었는지 모르는 상태에서도** 통째로 되돌릴 수 있습니다.

### 💰 Cost & Diagnostics

`COST`는 모델별·일자별·세션별로 사용량을 집계해 어디서 비용이 나는지 드러냅니다.
`DIAGNOSTICS`는 설정 충돌과 깨진 참조를 찾아 조용히 고장 나 있던 항목을 표면으로 올립니다.

</td>
</tr>
</table>

<br>

## 설계 원칙

| | |
|---|---|
| **파일이 곧 진실** | 중간 캐시가 아닌 원본을 직접 읽고 씀 |
| **되돌릴 수 있게** | `SNAPSHOTS`로 설정 전체를 시점 복원 |
| **상태가 보이게** | 활성·비활성·오류를 목록에서 바로 구분 |
| **읽는 화면은 가볍게** | 모듈 전환 시 불필요한 재스캔 차단 |

<br>

## 실행

[Releases](../../releases/latest)에서 `ClaudeCodeManager.exe`를 받아 그대로 실행합니다.
자체 포함 단일 실행 파일이라 .NET 런타임 설치가 필요 없습니다.

## 빌드

```powershell
.\build\publish.ps1
```

`dist\ClaudeCodeManager.exe` 가 만들어집니다.

```
-c Release  -r win-x64  --self-contained
-p:PublishSingleFile=true
-p:EnableCompressionInSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true
```

<br>

## 구조

```
src/
  ClaudeCodeManager.App/      WPF 셸 · 15개 모듈 View/ViewModel
    Themes/RustTheme.xaml     RUST 팔레트 · 타이포 · 글로우
    Themes/Controls.xaml      공통 컨트롤 스타일
    Fonts/                    Press Start 2P · VT323 · IBM Plex Sans KR
  ClaudeCodeManager.Core/     파일 탐색 · 파싱 · 캐시 · 그래프 빌더
build/publish.ps1             단일 EXE 퍼블리시
docs/assets/                  README 모션 에셋 (생성물)
```

<br>

## 디자인

앱과 README는 같은 토큰을 씁니다. 색은 [`RustTheme.xaml`](src/ClaudeCodeManager.App/Themes/RustTheme.xaml)이 단일 출처입니다.

<div align="center">

<img src="https://img.shields.io/badge/%230A0908-BG-E8DDD0?style=for-the-badge&labelColor=0A0908&color=0A0908" height="34">
<img src="https://img.shields.io/badge/%2314110F-PANEL-E8DDD0?style=for-the-badge&labelColor=14110F&color=14110F" height="34">
<img src="https://img.shields.io/badge/%23D9441C-RUST-0A0908?style=for-the-badge&labelColor=D9441C&color=D9441C" height="34">
<img src="https://img.shields.io/badge/%23F26A2E-EMBER-0A0908?style=for-the-badge&labelColor=F26A2E&color=F26A2E" height="34">
<img src="https://img.shields.io/badge/%234A1F0E-BORDER-E8DDD0?style=for-the-badge&labelColor=4A1F0E&color=4A1F0E" height="34">
<img src="https://img.shields.io/badge/%23E8DDD0-TEXT-0A0908?style=for-the-badge&labelColor=E8DDD0&color=E8DDD0" height="34">

</div>

README 애니메이션은 SVG의 CSS 애니메이션으로만 만들어져 있습니다 — GitHub은 `<img>`로 실린 SVG 안의
`<script>`를 실행하지 않기 때문입니다. 글리프는 폰트를 임베드하지 않고 패스로 변환했고,
모듈 아이콘은 각 ViewModel의 `Glyph` 정의에서 그대로 추출합니다.
`prefers-reduced-motion`을 켠 환경에서는 모든 움직임이 멈춥니다.
