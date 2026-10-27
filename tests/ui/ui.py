"""RecipeSong UI 시험 도구 (Windows UI Automation, pywinauto).

쓰고 있는 앱과 따로, 시험용 앱을 별도 데이터 폴더(RECIPESONG_DATA)로 띄워 조작하고 화면을 찍는다.
설정·캐시는 복사본을 쓰므로 시험 중 누른 좋아요·차단·칩 순서가 원래 데이터에 남지 않는다.
소리 분석 데이터(ml/data)는 같이 쓴다 (모델·임베딩 캐시 — 새로 분석한 신보가 더해질 뿐).

    python tests/ui/ui.py setup              시험 데이터 폴더 만들기 (지금 설정·캐시 복사) + 빌드
    python tests/ui/ui.py start [--en]       시험용 앱 띄우기 (--en: 영어 화면)
    python tests/ui/ui.py shot <이름>        창 캡처 → <데이터>/shots/<이름>.png
    python tests/ui/ui.py tree [깊이]        보이는 컨트롤 이름 목록 (누를 이름 찾기)
    python tests/ui/ui.py click <이름> [--right] [--index N]   버튼 누르기 (invoke, 오른쪽은 마우스)
    python tests/ui/ui.py menu <이름>        열린 우클릭 메뉴 항목 누르기
    python tests/ui/ui.py size <폭> <높이>   창 크기 바꾸기 (좁은 화면 시험)
    python tests/ui/ui.py wait <초>          기다리기
    python tests/ui/ui.py stop               시험용 앱만 닫기

주의: invoke 로 누르는 버튼은 마우스·키보드를 쓰지 않는다. 우클릭(--right)과 메뉴는 실제 마우스를 움직이고 창을 앞으로 가져온다.
ml/.venv 의 python 으로 실행한다 (pywinauto 는 dev 의존성).
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

REPO = Path(__file__).resolve().parents[2]
DATA = Path(os.environ.get("LOCALAPPDATA", "")) / "RecipeSong-uitest"
BIN = DATA / "bin"
EXE = BIN / "RecipeSong.Wpf.exe"
PID_FILE = DATA / "pid"
SHOTS = DATA / "shots"


def user_dirs() -> tuple[Path, Path]:
    """쓰고 있는 앱의 설정 파일과 캐시 폴더"""
    settings = Path(os.environ["APPDATA"]) / "RecipeSong" / "settings.json"
    cache = Path(os.environ["LOCALAPPDATA"]) / "RecipeSong"
    if settings.exists():
        c = json.loads(settings.read_text(encoding="utf-8-sig")).get("CacheDirectory")
        if c:
            cache = Path(c)
    return settings, cache


def setup() -> None:
    settings, cache = user_dirs()
    (DATA / "cache").mkdir(parents=True, exist_ok=True)
    s = json.loads(settings.read_text(encoding="utf-8-sig")) if settings.exists() else {}
    s["MlDirectory"] = str(REPO / "ml")      # 시험 빌드는 저장소 밖에 있어 ml 폴더를 못 찾는다
    s["CacheDirectory"] = None
    s["AutoRetrain"] = False                 # 시험 중에 라이브러리 재학습을 돌리지 않는다
    (DATA / "settings.json").write_text(json.dumps(s, ensure_ascii=False, indent=2), encoding="utf-8")
    n = 0
    for f in cache.glob("*"):
        if f.is_file() and f.suffix in (".json", ".log"):
            shutil.copy2(f, DATA / "cache" / f.name)
            n += 1
    print(f"시험 데이터: {DATA} (캐시 {n}개 복사)")
    build()


def build() -> None:
    r = subprocess.run(["dotnet", "build", str(REPO / "src/RecipeSong.Wpf/RecipeSong.Wpf.csproj"), "-c", "Debug", "-o", str(BIN)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    for p in (REPO / "src/RecipeSong.Wpf").glob("*_wpftmp.csproj"):
        p.unlink(missing_ok=True)
    errors = [l for l in r.stdout.splitlines() if " error " in l]
    print("빌드 " + ("성공" if r.returncode == 0 else "실패"), *sorted(set(errors))[:10], sep="\n")


def app():
    from pywinauto import Application
    pid = int(PID_FILE.read_text())
    return Application(backend="uia").connect(process=pid, timeout=20)


def window():
    w = app().window(title="RecipeSong")
    w.wait("exists ready", timeout=60)
    return w


def start(english: bool = False) -> None:
    env = dict(os.environ, RECIPESONG_DATA=str(DATA))
    if english:
        env["RECIPESONG_LANG"] = "en"
    p = subprocess.Popen([str(EXE)], env=env, cwd=str(BIN))
    PID_FILE.write_text(str(p.pid))
    window()
    print(f"시험용 앱 pid {p.pid}")


def stop() -> None:
    if PID_FILE.exists():
        subprocess.run(["taskkill", "/PID", PID_FILE.read_text().strip(), "/T", "/F"], capture_output=True)
        PID_FILE.unlink()
        print("시험용 앱을 닫았어요")


def shot(name: str) -> None:
    SHOTS.mkdir(parents=True, exist_ok=True)
    out = SHOTS / f"{name}.png"
    # PrintWindow: 다른 창에 가려져 있어도 이 창만 찍는다 (capture_as_image 는 화면 영역을 찍어 가린 창이 찍힌다)
    import ctypes

    import win32gui
    import win32ui
    from PIL import Image
    hwnd = window().handle
    l, t, r, b = win32gui.GetWindowRect(hwnd)
    w, h = r - l, b - t
    src = win32ui.CreateDCFromHandle(win32gui.GetWindowDC(hwnd))
    dc = src.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    bmp.CreateCompatibleBitmap(src, w, h)
    dc.SelectObject(bmp)
    ctypes.windll.user32.PrintWindow(hwnd, dc.GetSafeHdc(), 2)  # 2 = PW_RENDERFULLCONTENT
    info = bmp.GetInfo()
    Image.frombuffer("RGB", (info["bmWidth"], info["bmHeight"]), bmp.GetBitmapBits(True), "raw", "BGRX", 0, 1).save(out)
    win32gui.DeleteObject(bmp.GetHandle())
    dc.DeleteDC()
    src.DeleteDC()
    print(out)


def tree(depth: int = 6) -> None:
    seen = set()
    for c in window().descendants():
        try:
            n, t = c.window_text(), c.element_info.control_type
        except Exception:
            continue
        if n and (n, t) not in seen and c.is_visible():
            seen.add((n, t))
            print(f"{t:10s} {n}")


def find(name: str, index: int = 0):
    w = window()
    hits = [c for c in w.descendants() if c.window_text() == name and c.is_visible()]
    if not hits:  # 부분 일치
        hits = [c for c in w.descendants() if name in c.window_text() and c.is_visible()]
    if len(hits) <= index:
        raise SystemExit(f"'{name}' 을(를) 찾지 못했어요 ({len(hits)}개)")
    return hits[index]


def click(name: str, right: bool = False, index: int = 0) -> None:
    c = find(name, index)
    if right:
        c.click_input(button="right")
    else:
        try:
            c.invoke()
        except Exception:
            try:
                c.select()
            except Exception:
                c.click_input()
    print(f"눌렀어요: {c.element_info.control_type} '{c.window_text()}'")


def menu(name: str) -> None:
    from pywinauto import Desktop
    for m in Desktop(backend="uia").windows(process=int(PID_FILE.read_text())):
        for c in m.descendants(control_type="MenuItem"):
            if name in c.window_text():
                c.click_input()
                print(f"메뉴: '{c.window_text()}'")
                return
    raise SystemExit(f"메뉴 '{name}' 없음")


def size(w: int, h: int) -> None:
    import win32con
    import win32gui
    hwnd = window().handle
    win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)  # 최대화돼 있으면 풀고
    x, y, _, _ = win32gui.GetWindowRect(hwnd)
    win32gui.MoveWindow(hwnd, x, y, w, h, True)


def main() -> None:
    a = sys.argv[1:]
    if not a:
        print(__doc__)
        return
    cmd, rest = a[0], a[1:]
    flag = lambda f: f in rest  # noqa: E731
    pos = [x for x in rest if not x.startswith("--")]
    idx = int(rest[rest.index("--index") + 1]) if "--index" in rest else 0
    if cmd == "setup":
        setup()
    elif cmd == "build":
        build()
    elif cmd == "start":
        start(flag("--en"))
    elif cmd == "stop":
        stop()
    elif cmd == "shot":
        shot(pos[0] if pos else time.strftime("%H%M%S"))
    elif cmd == "tree":
        tree()
    elif cmd == "click":
        click(pos[0], flag("--right"), idx)
    elif cmd == "menu":
        menu(pos[0])
    elif cmd == "size":
        size(int(pos[0]), int(pos[1]))
    elif cmd == "wait":
        time.sleep(float(pos[0]))
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
