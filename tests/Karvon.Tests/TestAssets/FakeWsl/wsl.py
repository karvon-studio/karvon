#!/usr/bin/env python3
"""A stand-in for wsl.exe --exec, for the Docker tests: it runs a program of FAKE_WSL_PATH (its "distribution").

As the real one's, what it starts is not its child, has only its input and output, and stays when wsl.exe is ended: its
input closes then. It says a note of its own beside the program's words, and its processes' ids go to FAKE_WSL_PIDS.
"""
import os
import sys
import threading

args = sys.argv[1:]
sys.stderr.write("wsl: A localhost proxy configuration was detected but not mirrored into WSL.\n")
sys.stderr.flush()
if args[:1] != ["--exec"] or os.environ.get("WSL_UTF8") != "1":
    sys.exit("fake wsl: no --exec, or its words not in UTF-8")
command = args[1:]
path = os.environ["FAKE_WSL_PATH"] + ":/usr/bin:/bin"
# Its docker is its own, never this computer's.
if command[0] == "docker" and not os.access(os.path.join(os.environ["FAKE_WSL_PATH"], "docker"), os.X_OK):
    sys.exit("<3>WSL (42 - Relay) ERROR: CreateProcessCommon:818: execvpe(%s) failed: No such file or directory" % command[0])

in_read, in_write = os.pipe()
out_read, out_write = os.pipe()
err_read, err_write = os.pipe()
code_read, code_write = os.pipe()
if os.fork() == 0:
    # Its parent ends at once, so it is no descendant of wsl.exe's.
    if os.fork() != 0:
        os._exit(0)
    os.setsid()
    for fd in (in_write, out_read, err_read, code_read):
        os.close(fd)
    child = os.fork()
    if child == 0:
        os.close(code_write)
        for fd, to in ((in_read, 0), (out_write, 1), (err_write, 2)):
            os.dup2(fd, to)
            os.close(fd)
        os.execvpe(command[0], command, dict(os.environ, PATH=path))
    with open(os.environ["FAKE_WSL_PIDS"], "a") as pids:
        pids.write("%d\n" % child)
    for fd in (in_read, out_write, err_write, 0, 1, 2):
        os.close(fd)
    status = os.waitstatus_to_exitcode(os.waitpid(child, 0)[1])
    try:
        os.write(code_write, str(status).encode())
    except OSError:
        pass
    os._exit(0)
os.wait()
for fd in (in_read, out_write, err_write, code_write):
    os.close(fd)


def relay(source, target, close=False):
    try:
        while data := os.read(source, 65536):
            os.write(target, data)
    except OSError:
        pass
    if close:
        os.close(target)


threading.Thread(target=relay, args=(0, in_write, True), daemon=True).start()
outputs = [threading.Thread(target=relay, args=pair) for pair in ((out_read, 1), (err_read, 2))]
for thread in outputs:
    thread.start()
code = os.read(code_read, 16)
for thread in outputs:
    thread.join()
os._exit(int(code or b"1") & 0xFF)
