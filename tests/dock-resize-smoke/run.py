#!/usr/bin/env python3
"""dock-resize-smoke 러너: pty 안에서 --child 실행(크기 시퀀스 적용) 후 --verify 로 검증.

성장 모델 A(하단 고정)·B(상단 고정) 각각 실행: 자식의 CPR 프로브와 검증 재생이 같은
SMOKE_MODEL 환경변수를 공유한다.
"""
import os
import fcntl
import pty
import select
import struct
import subprocess
import sys
import termios
import time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BIN = os.path.join(
    REPO, "tests", "dock-resize-smoke", "bin", "Debug", "net10.0",
    "dock-resize-smoke")

# (rows) 시퀀스: 자식이 WaitHeight 로 순서대로 기다린다
SIZES = [24, 30, 18, 30, 40, 24, 24, 30, 24]
MARKS = len(SIZES) - 1   # install 포함 총 MARK 수


def set_size(fd, rows, cols=100):
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))


def run_model(model):
    rec = f"/tmp/dock-resize-rec-{model}.txt"
    if os.path.exists(rec):
        os.remove(rec)

    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["SMOKE_MODEL"] = model
        os.execv(BIN, [BIN, "--child", rec])

    set_size(fd, SIZES[0])
    idx = 1
    deadline = time.time() + 30
    try:
        while idx < len(SIZES) and time.time() < deadline:
            r, _, _ = select.select([fd], [], [], 0.2)
            if r:
                try:
                    os.read(fd, 65536)
                except OSError:
                    break
            time.sleep(0.3)
            marks = 0
            if os.path.exists(rec):
                with open(rec, "rb") as f:
                    marks = sum(1 for line in f if line.startswith(b"MARK "))
            if marks >= idx:
                set_size(fd, SIZES[idx])
                idx += 1
    finally:
        try:
            os.close(fd)
        except OSError:
            pass
        try:
            os.waitpid(pid, 0)
        except ChildProcessError:
            pass

    time.sleep(0.3)
    env = dict(os.environ, SMOKE_MODEL=model)
    r = subprocess.run([BIN, "--verify", rec], capture_output=True, text=True, env=env)
    print(r.stdout, end="")
    if r.stderr:
        print(r.stderr, file=sys.stderr, end="")
    return r.returncode


def main():
    rc = 0
    for model in ("A", "B"):
        rc |= run_model(model)
    print("ALL PASS" if rc == 0 else "SOME FAILED")
    return rc


if __name__ == "__main__":
    sys.exit(main())
