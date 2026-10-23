import sys, io, json, threading, queue, signal, traceback, builtins, _thread

_NONCE = sys.argv[1]
_SENTINEL = "\x1e" + _NONCE + "\x1e"
_out, _err, _in = sys.__stdout__, sys.__stderr__, sys.stdin
for _f in (_out, _err):
    try:
        _f.reconfigure(newline="\n")   # Windows would otherwise turn \n into \r\n
    except Exception:
        pass
sys.stdin = io.StringIO()          # user input() gets EOF instead of eating protocol lines
sys.argv = [""]
_elock = threading.Lock()

def _emit(line):
    with _elock:
        _err.write(line + "\n")
        _err.flush()

def _esc(s):
    return s.replace("\\", "\\\\").replace("\r", "\\r").replace("\n", "\\n")

def progress(task,percent=None):
    """progress("foo",42) reports foo 42%; progress("foo") or a negative value reports movement only."""
    if percent is None or percent < 0:
        _emit(f"[progress: {task}]")
    else:
        _emit(f"[progress: {task} {int(min(percent, 100))}%]")

def completed(task):
    """completion of the task."""
    _emit(f"[completed {task}]")

class CancelledByHost(BaseException):
    """Raised inside running code when the C# CancellationToken is canceled."""

class _State:
    current = None        # id of the call being executed
    in_user = False       # True only while user code runs
    cancel_req = False
_st = _State()
_slock = threading.Lock()
_pre_canceled = set()
_q = queue.Queue()
_main_ident = threading.main_thread().ident
_cancel_evt = threading.Event()     # set when a cancel arrives; wakes the patched sleep below

if not hasattr(signal, "pthread_kill"):
    # Windows: interrupt_main() only takes effect between bytecodes, so a time.sleep() in the
    # main thread would not notice it. Replace time.sleep with a wait that a cancel can wake.
    import time as _time
    _real_sleep = _time.sleep
    def _sleep(secs):
        if threading.current_thread() is not threading.main_thread():
            return _real_sleep(secs)
        if secs < 0:
            raise ValueError("sleep length must be non-negative")
        if _cancel_evt.wait(secs):
            _real_sleep(0)          # let the pending CancelledByHost fire inside user code
    _sleep.__doc__ = _real_sleep.__doc__
    _time.sleep = _sleep

def _on_sigint(signum, frame):
    # Only our own cancel requests interrupt; a stray Ctrl+C from the parent console is ignored.
    if _st.in_user and _st.cancel_req:
        raise CancelledByHost()
signal.signal(signal.SIGINT, _on_sigint)

def _interrupt_main():
    if hasattr(signal, "pthread_kill"):
        signal.pthread_kill(_main_ident, signal.SIGINT)   # real signal: also breaks out of sleep()/blocking IO on POSIX
    else:
        _thread.interrupt_main()                          # Windows: runs the handler at the next bytecode

def _reader():
    for raw in _in:
        try:
            msg = json.loads(raw)
        except Exception:
            continue
        if msg.get("op") == "cancel":
            with _slock:
                if _st.current == msg.get("id"):
                    _st.cancel_req = True
                    _cancel_evt.set()
                    _interrupt_main()
                else:
                    _pre_canceled.add(msg.get("id"))
        else:
            _q.put(msg)
    _q.put({"op": "exit"})

threading.Thread(target=_reader, daemon=True, name="pyhost-stdin").start()

_g = {"__name__": "__main__", "__builtins__": builtins,
      "progress": progress, "completed": completed,
      "CancelledByHost": CancelledByHost}

_emit("[ready: %s]" % _NONCE)

while True:
    msg = _q.get()
    op = msg.get("op")
    if op == "exit":
        break
    if op != "exec":
        continue
    eid = msg.get("id")
    with _slock:
        skip = eid in _pre_canceled
        _pre_canceled.discard(eid)
        if not skip:
            _st.current, _st.cancel_req = eid, False
            _cancel_evt.clear()
    status, err = "canceled", None
    if not skip:
        try:
            try:
                code = compile(msg.get("code", ""), "<cell %s>" % eid, "exec")
                # persistent (default): run in the session globals. Otherwise run in a shallow copy that
                # is thrown away, so the code can read session state but its new names don't stick.
                globs = _g if msg.get("persistent", True) else dict(_g)
                _st.in_user = True
                exec(code, globs)
                _st.in_user = False
                status = "ok 0"
            finally:
                _st.in_user = False
                globs = None
        except CancelledByHost:
            status = "canceled"
        except SystemExit as e:
            c = e.code
            if c is None:
                c = 0
            elif not isinstance(c, int):
                print(c, file=sys.stderr)
                c = 1
            status = "ok %d" % c
        except BaseException as e:
            _st.in_user = False
            if _st.cancel_req and isinstance(e, KeyboardInterrupt):
                status = "canceled"
            else:
                tb = e.__traceback__.tb_next if e.__traceback__ else None   # drop the host's frame
                err = ("%s: %s" % (type(e).__name__, e), type(e).__name__,
                       "".join(traceback.format_exception(type(e), e, tb)))
                status = "error"
        with _slock:
            _st.in_user = False
            _st.current, _st.cancel_req = None, False
    for f in (sys.stdout, _out):
        try:
            f.flush()
        except Exception:
            pass
    _out.write(_SENTINEL)
    _out.flush()
    if err:
        _emit("[error: %s]" % _esc(err[0]))
        _emit("[error-type: %s]" % err[1])
        _emit("[traceback: %s]" % _esc(err[2]))
    _emit("[done: %s %s]" % (_NONCE, status))

sys.exit(0)