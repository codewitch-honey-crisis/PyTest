// PySession.cs — persistent python3 executor for .NET 8.
//
// One long-lived python3 process per session. Code is sent over stdin and run with exec()
// in a single globals dict, so variables, imports, functions and classes survive between calls.
// Nothing is written to disk: the host script is sent over stdin at startup.
//
// Python -> C# (stderr, one per line):
//   [progress: task 95%]   percentage          -> IProgress<PyProgress>.Report({PyProgressKind.Bounded,"task",95})
//   [progress: task]        "still moving"      -> IProgress<PyProgress>.Report({PyProgressKind.Unbounded,"task",-1})
//   [completed: task]        "completed"      -> IProgress<PyProgress>.Report({PyProgressKind.Completed,"task",-1})
//   [error: msg] / [error-type: Name] / [traceback: ...]   (\n escaped)  -> PythonException
//   [done: <nonce> ok <code>|error|canceled]               (internal)
//   any other stderr line                  -> PySessionOptions.StandardErrorLine
// C# -> Python (stdin, JSON lines): {"op":"exec",...}, {"op":"cancel","id":n}, {"op":"exit"}
//
// Inside Python code the helpers below are pre-defined:
//   progress(42)       report 42%
//   progress()         report movement with no percentage (also any negative value)
//   completed()             report the end of the progress reported task
//   CancelledByHost    BaseException raised when the C# token is canceled (catch it to clean up, then re-raise)
//   sys.exit(n)        ends the current call only; ExecuteAsync returns n and the session keeps running
//
// ExecuteAsync(..., persistent: false) runs against a throwaway copy of the globals: the code can read
// everything already defined, but names it creates or reassigns are discarded when the call ends.
//
// stdout of each call goes to the TextWriter passed to ExecuteAsync (pass a StringWriter to collect it)
// and, line by line, to the OutputDataReceived event.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PyTest;

public enum PyProgressKind
{
    None = 0,
    Bounded = 1,
    Unbounded = 2,
    Completed = 3
}
public record PyProgress(PyProgressKind Kind, string Task, int Value = -1);
public sealed class PySessionOptions
{
    /// <summary>Interpreter to run. Point this at a venv's python to isolate packages.</summary>
    public string PythonPath { get; init; } = "python3";
    /// <summary>uv executable for package installs. When null, "&lt;python&gt; -m pip install" is used instead.</summary>
    public string? UvPath { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>How long Python gets to honour a cancel before the process is killed.</summary>
    public TimeSpan CancelGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Extra environment variables for the Python process (null value removes one).</summary>
    public IDictionary<string, string?>? Environment { get; init; }
    /// <summary>Receives stderr lines that are not part of the protocol (warnings, user prints to stderr).</summary>
    public Action<string>? StandardErrorLine { get; init; }
    /// <summary>Receives stdout written while no call is running (e.g. by background threads).</summary>
    public Action<string>? OrphanedOutput { get; init; }
}

public class PyException(string message, string? pythonType = null, string? traceback = null,
                       int? processExitCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Python exception type name, e.g. "FileNotFoundError".</summary>
    public string? PythonType { get; } = pythonType;
    /// <summary>Formatted Python traceback (host frames removed).</summary>
    public string? PythonTraceback { get; } = traceback;
    /// <summary>Set when the python process itself exited or was killed.</summary>
    public int? ProcessExitCode { get; } = processExitCode;

    public override string ToString() =>
        PythonTraceback is null ? base.ToString() : base.ToString() + Environment.NewLine + "--- Python traceback ---" + Environment.NewLine + PythonTraceback;
}

/// <summary>One line of stdout from the running call, without its line terminator.</summary>
public sealed class PyDataReceivedEventArgs(string data) : EventArgs
{
    public string Data { get; } = data;
}

public sealed class PySession : IAsyncDisposable
{
    // ------------------------------------------------------------------ embedded Python host
    private const string Bootstrap = "import sys,json;exec(json.loads(sys.stdin.readline()))";

    private const string HostScript = "HostScript.py";
    private const string InstallTask = "Running PIP install";

    // ------------------------------------------------------------------ protocol parsing
    private static readonly Regex ProgressRx = PyRegexAttributes.ProgressRegex();
    private static readonly Regex CompletedRx = PyRegexAttributes.CompletedRegex();
    private static readonly Regex ErrorRx = PyRegexAttributes.ErrorRegex();
    private static readonly Regex ErrorTypeRx = PyRegexAttributes.ErrorTypeRegex();
    private static readonly Regex TracebackRx = PyRegexAttributes.TracebackRegex();
    private static readonly Regex DoneRx = PyRegexAttributes.DoneRegex();
    private static readonly Regex ReadyRx = PyRegexAttributes.ReadyRegex();

    private readonly PySessionOptions _options;
    private readonly Process _proc;
    private readonly string _nonce;
    private readonly string _sentinel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stdinLock = new();
    private readonly Lock _stateLock = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _recentStderr = new();
    private Call? _active;
    private int _nextId;
    private Exception? _deathReason;
    private bool _disposed;
    private Task _stdoutPump = Task.CompletedTask, _stderrPump = Task.CompletedTask;

    /// <summary>
    /// Raised for each line a call writes to stdout, like <see cref="Process.OutputDataReceived"/>.
    /// A final line without a trailing newline is raised when the call finishes. Raised on a background
    /// thread; a slow handler slows Python down, and an exception thrown here faults the call.
    /// </summary>
    public event EventHandler<PyDataReceivedEventArgs>? OutputDataReceived;

    /// <summary>False once the process has exited or been killed (e.g. after a forced cancel).</summary>
    public bool IsAlive { get { lock (_stateLock) return _deathReason is null && !_disposed; } }

    private PySession(PySessionOptions options)
    {
        _options = options;
        _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _sentinel = "\u001e" + _nonce + "\u001e";

        var psi = new ProcessStartInfo(options.PythonPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (options.WorkingDirectory is not null) { psi.WorkingDirectory = options.WorkingDirectory; }

        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Bootstrap);
        psi.ArgumentList.Add(_nonce);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        if (options.Environment is not null)
        {
            foreach (var (k, v) in options.Environment)
            {
                if (v is null) { psi.Environment.Remove(k); }
                else { psi.Environment[k] = v; }
            }
        }

        _proc = new Process { StartInfo = psi };
    }

    /// <summary>Starts python3 and waits until the host is ready.</summary>
    public static async Task<PySession> StartAsync(PySessionOptions? options = null, CancellationToken ct = default)
    {
        var s = new PySession(options ?? new PySessionOptions());
        try
        {
            s._proc.Start();
        }
        catch (Exception ex)
        {
            throw new PyException($"Could not start '{s._options.PythonPath}': {ex.Message}", inner: ex);
        }
        s._stdoutPump = Task.Run(s.PumpStdoutAsync, ct);
        s._stderrPump = Task.Run(s.PumpStderrAsync, ct);
        try
        {
            var ns = typeof(PySession).Namespace;

            using var stm = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{ns}.{HostScript}") ??
                throw new InvalidProgramException("An embedded resource could not be found");

            var stmReader = new StreamReader(stm);

            s.SendRaw(JsonSerializer.Serialize(stmReader.ReadToEnd()));

            await s._ready.Task.WaitAsync(s._options.StartupTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return s;
    }

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Queues <paramref name="code"/> to run in the session. Calls run one at a time in the order they were made.
    /// </summary>
    /// <param name="output">
    /// Receives the call's stdout as it is produced (null discards it). Written from a background thread and
    /// flushed when the call finishes; it is not disposed. If it throws, Python keeps running to the end of
    /// the call and the task then faults with that exception.
    /// </param>
    /// <param name="persistent">
    /// True (default): variables, functions, classes and imports the code defines stay in the session.
    /// False: the code runs against a throwaway copy of the session globals. It can read everything already
    /// defined, but names it creates or reassigns are discarded when the call ends. Objects it mutates
    /// (e.g. appending to a session list) stay changed, and imported modules stay loaded.
    /// </param>
    /// <returns>0, or n if the code called sys.exit(n).</returns>
    /// <exception cref="PyException">The Python code raised, or the process died.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public Task<int> ExecuteAsync(string code, TextWriter? output = null, IProgress<PyProgress>? progress = null,
                                  CancellationToken ct = default, bool persistent = true)
    {
        ArgumentNullException.ThrowIfNull(code);
        return RunCallAsync(new Call(Interlocked.Increment(ref _nextId), output, progress, ct), code, persistent);
    }

    /// <summary>
    /// Installs packages from requirements.txt-style text ("numpy>=1.26", "--index-url ...", comments, etc.)
    /// with "&lt;python&gt; -m pip install" (or "uv pip install" when <see cref="PySessionOptions.UvPath"/> is set).
    /// No file is written. Progress reports -1 per output line, then Completed.
    /// Packages already installed through this class for the same interpreter are skipped silently when the
    /// requirement is the same, and rejected with a <see cref="PyException"/> when it differs (see
    /// <see cref="InstallPackagesAsync"/>). Returns the installer's combined output, or "" if nothing needed installing.
    /// </summary>
    public Task<string> InstallRequirementsAsync(string requirementsText, IProgress<PyProgress>? progress = null,
                                                 CancellationToken ct = default, params string[] extraPipArgs)
        => InstallPackagesAsync(ParseRequirements(requirementsText).Concat(extraPipArgs), progress, ct);

    /// <summary>
    /// Runs "&lt;python&gt; -m pip install &lt;args&gt;" (or "uv pip install --python &lt;python&gt; &lt;args&gt;" when
    /// <see cref="PySessionOptions.UvPath"/> is set) for this session's interpreter, then refreshes import caches.
    /// <para>
    /// Every named requirement is remembered per <see cref="PySessionOptions.PythonPath"/> once the install succeeds.
    /// Asking for the same package with the same requirement again (e.g. "numpy==2.0" twice, or "Foo_Bar" after
    /// "foo-bar") is skipped silently. Asking for it with a different requirement ("numpy==2.1" after "numpy==2.0",
    /// or "numpy" after "numpy>=1.26") throws <see cref="PyException"/> before anything runs, because an already
    /// imported package cannot be swapped inside a running Python process.
    /// </para>
    /// Paths, wheel files, "-r" and "-e" arguments and other options are passed through untracked.
    /// Returns the installer's output, or "" if every requirement was already installed.
    /// </summary>
    public async Task<string> InstallPackagesAsync(IEnumerable<string> pipArgs, IProgress<PyProgress>? progress = null,
                                                   CancellationToken ct = default)
    {
        var output = await InstallTrackedAsync(_options, pipArgs, progress, ct).ConfigureAwait(false);
        if (output is null) { return ""; }

        if (IsAlive)
            await ExecuteAsync("import importlib; importlib.invalidate_caches()", ct: ct, persistent: false).ConfigureAwait(false);
        return output;
    }

    /// <summary>Same as the instance method, for use before a session exists. Shares the same installed-package record.</summary>
    public static async Task<string> InstallRequirementsAsync(PySessionOptions options, string requirementsText,
                                                              IProgress<PyProgress>? progress = null, CancellationToken ct = default)
        => await InstallTrackedAsync(options, ParseRequirements(requirementsText), progress, ct).ConfigureAwait(false) ?? "";

    /// <summary>
    /// Packages installed through this class for <paramref name="pythonPath"/> in this .NET process:
    /// normalized name -> requirement as it was given (e.g. "numpy" -> "numpy==2.0").
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetInstalledPackages(string pythonPath = "python3")
    {
        if (!PipRegistries.TryGetValue(pythonPath, out var reg)) { return new Dictionary<string, string>(); }
        lock (reg.Installed) { return reg.Installed.ToDictionary(kv => kv.Key, kv => kv.Value.Requirement); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try
        {
            if (!_proc.HasExited)
            {
                try { SendRaw("{\"op\":\"exit\"}"); _proc.StandardInput.Close(); } catch { /* already gone */ }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { TryKill(); }
            }
        }
        catch (InvalidOperationException) { /* never started */ }
        Fail(new ObjectDisposedException(nameof(PySession)));
        try { await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _proc.Dispose();
        _gate.Dispose();
    }

    // ------------------------------------------------------------------ call lifecycle

    private sealed class Call(int id, TextWriter? writer, IProgress<PyProgress>? progress, CancellationToken token)
    {
        public readonly int Id = id;
        public readonly TextWriter? Writer = writer;
        public readonly IProgress<PyProgress>? Progress = progress;
        public readonly CancellationToken Token = token;
        public readonly TaskCompletionSource<int> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly StringBuilder LineBuffer = new();
        public Exception? OutputError;          // first exception from Writer or an OutputDataReceived handler
        public bool StdoutDone;
        public string? DoneStatus; public int ExitCode;
        public string? ErrorMessage, ErrorType, Traceback;
    }

    private async Task<int> RunCallAsync(Call call, string code, bool persistent)
    {
        await _gate.WaitAsync(call.Token).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, nameof(PySession));

                if (_deathReason is not null)
                {
                    throw new PyException("The Python session is no longer running.", inner: _deathReason);
                }

                call.Token.ThrowIfCancellationRequested();
                _active = call;
            }

            try
            {
                SendRaw(JsonSerializer.Serialize(new { op = "exec", id = call.Id, code, persistent }));
            }

            catch (IOException ex)
            {
                Fail(new PyException("Lost connection to the Python process.", inner: ex));
            }

            using (call.Token.Register(() => OnCancelRequested(call)))
                return await call.Tcs.Task.ConfigureAwait(false);
        }

        finally
        {
            lock (_stateLock)
            {
                if (_active == call)
                {
                    _active = null;
                }
            }

            try { _gate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void OnCancelRequested(Call call)
    {
        try { SendRaw(JsonSerializer.Serialize(new { op = "cancel", id = call.Id })); }
        catch { /* process gone; the pumps will fail the call */ }

        _ = Task.Delay(_options.CancelGracePeriod).ContinueWith(_ =>
        {
            if (call.Tcs.Task.IsCompleted) return;
            // Python ignored the cancel (blocking C call, swallowed exception...). Kill it; state is lost.
            Fail(new PyException("Python did not stop after cancellation and was killed; the session is no longer usable."),
                 canceledCall: call);
            TryKill();
        }, TaskScheduler.Default);
    }

    private static void TryFinish(Call call)
    {
        if (!call.StdoutDone || call.DoneStatus is null) return;
        if (call.OutputError is not null && call.DoneStatus != "canceled")
        {
            call.Tcs.TrySetException(call.OutputError);
            return;
        }
        switch (call.DoneStatus)
        {
            case "ok": call.Tcs.TrySetResult(call.ExitCode); break;
            case "canceled":
                call.Tcs.TrySetCanceled(call.Token.IsCancellationRequested ? call.Token : new CancellationToken(true));
                break;
            default:
                call.Tcs.TrySetException(new PyException(call.ErrorMessage ?? "Python error", call.ErrorType, call.Traceback));
                break;
        }
    }

    /// <summary>Marks the session dead and fails whatever is running.</summary>
    private void Fail(Exception reason, Call? canceledCall = null)
    {
        Call? active;
        lock (_stateLock)
        {
            _deathReason ??= reason;
            active = _active;
        }
        _ready.TrySetException(reason);
        if (active is null) return;
        if (canceledCall == active)
            active.Tcs.TrySetCanceled(active.Token);
        else
            active.Tcs.TrySetException(reason);
    }

    private void TryKill()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
    }

    private void SendRaw(string jsonLine)
    {
        lock (_stdinLock)
        {
            _proc.StandardInput.Write(jsonLine);
            _proc.StandardInput.Write('\n');
            _proc.StandardInput.Flush();
        }
    }

    // ------------------------------------------------------------------ pumps

    private async Task PumpStdoutAsync()
    {
        var reader = _proc.StandardOutput;
        var buf = new char[8192];
        var pending = new StringBuilder();
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
            {
                pending.Append(buf, 0, n);
                var text = pending.ToString();
                pending.Clear();

                int idx;
                while ((idx = text.IndexOf(_sentinel, StringComparison.Ordinal)) >= 0)
                {
                    DeliverStdout(text[..idx]);
                    Call? call;
                    lock (_stateLock) call = _active;
                    if (call is not null)
                    {
                        EndStdout(call);
                        lock (call) { call.StdoutDone = true; TryFinish(call); }
                    }
                    text = text[(idx + _sentinel.Length)..];
                }

                // Hold back a tail that could be the start of a sentinel split across reads.
                int keep = 0;
                for (int k = Math.Min(_sentinel.Length - 1, text.Length); k > 0; k--)
                {
                    if (text.AsSpan(text.Length - k).SequenceEqual(_sentinel.AsSpan(0, k)))
                    {
                        keep = k;
                        break;
                    }
                }

                DeliverStdout(text[..^keep]);
                pending.Append(text, text.Length - keep, keep);
            }
            DeliverStdout(pending.ToString());
        }
        catch (Exception) { /* fall through to process-exit handling */ }
        await OnProcessGoneAsync().ConfigureAwait(false);
    }

    private void DeliverStdout(string s)
    {
        if (s.Length == 0) { return; }

        Call? call;

        lock (_stateLock) { call = _active; }

        if (call is not null && !call.StdoutDone) { WriteToCall(call, s); }

        else { _options.OrphanedOutput?.Invoke(s); }
    }

    private void WriteToCall(Call call, string s)
    {
        if (call.Writer is not null && call.OutputError is null)
        {
            try { call.Writer.Write(s); }
            catch (Exception ex) { call.OutputError = ex; }
        }
        // Split into lines for OutputDataReceived; "\r\n" and "\n" both end a line.
        int start = 0, nl;

        while ((nl = s.IndexOf('\n', start)) >= 0)
        {
            call.LineBuffer.Append(s, start, nl - start);

            if (call.LineBuffer.Length > 0 && call.LineBuffer[^1] == '\r')
            {
                call.LineBuffer.Length--;
            }

            RaiseLine(call, call.LineBuffer.ToString());
            call.LineBuffer.Clear();
            start = nl + 1;
        }

        call.LineBuffer.Append(s, start, s.Length - start);
    }

    /// <summary>The call's stdout is complete: raise any unterminated last line and flush the writer.</summary>
    private void EndStdout(Call call)
    {
        if (call.LineBuffer.Length > 0)
        {
            if (call.LineBuffer[^1] == '\r') { call.LineBuffer.Length--; }

            RaiseLine(call, call.LineBuffer.ToString());
            call.LineBuffer.Clear();
        }

        if (call.Writer is not null && call.OutputError is null)
        {
            try { call.Writer.Flush(); }
            catch (Exception ex) { call.OutputError = ex; }
        }
    }

    private void RaiseLine(Call call, string line)
    {
        var handler = OutputDataReceived;
        if (handler is null) { return; }

        try { handler(this, new PyDataReceivedEventArgs(line)); }
        catch (Exception ex) { call.OutputError ??= ex; }
    }

    private async Task PumpStderrAsync()
    {
        var reader = _proc.StandardError;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                HandleStderrLine(line);
            }
        }
        catch (Exception) { }
        await OnProcessGoneAsync().ConfigureAwait(false);
    }

    private void HandleStderrLine(string line)
    {
        Call? call;
        lock (_stateLock) call = _active;
        Match m;

        if (line.Length > 0 && line[0] == '[')
        {
            if ((m = ProgressRx.Match(line)).Success)
            {
                if (call?.Progress is { } p)
                {
                    string task = "";
                    if (m.Groups.Count > 1 && m.Groups[1].Success)
                    {
                        task = m.Groups[1].Value;
                        int value = -1;
                        if (m.Groups.Count > 2 && m.Groups[2].Success && double.TryParse(
                            m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                            d >= 0
                        ) { value = (int)Math.Min(d, 100); }

                        try { p.Report(new PyProgress(value == -1 ? PyProgressKind.Unbounded : PyProgressKind.Bounded, task, value)); } catch { /* never let a progress handler kill the pump */ }
                    }

                }
                return;
            }
            if ((m = CompletedRx.Match(line)).Success)
            {
                if (call?.Progress is { } p && m.Groups.Count > 1 && m.Groups[1].Success)
                {
                    try { p.Report(new PyProgress(PyProgressKind.Completed, m.Groups[1].Value)); } catch { /* never let a progress handler kill the pump */ }
                }
                return;
            }

            if ((m = DoneRx.Match(line)).Success && m.Groups[1].Value == _nonce)
            {

                if (call is null) { return; }

                lock (call)
                {
                    if (m.Groups[2].Success)
                    {
                        call.DoneStatus = "ok";
                        call.ExitCode = int.Parse(m.Groups[2].Value);
                    }

                    else { call.DoneStatus = m.Groups[3].Success ? "error" : "canceled"; }

                    TryFinish(call);
                }
                return;
            }
            if (call is not null)
            {
                if ((m = ErrorRx.Match(line)).Success)
                {
                    call.ErrorMessage = Unescape(m.Groups[1].Value);
                    return;
                }

                if ((m = ErrorTypeRx.Match(line)).Success)
                {
                    call.ErrorType = m.Groups[1].Value;
                    return;
                }

                if ((m = TracebackRx.Match(line)).Success)
                {
                    call.Traceback = Unescape(m.Groups[1].Value);
                    return;
                }
            }

            if ((m = ReadyRx.Match(line)).Success && m.Groups[1].Value == _nonce)
            {
                _ready.TrySetResult();
                return;
            }
        }

        lock (_recentStderr)
        {
            _recentStderr.Enqueue(line);
            while (_recentStderr.Count > 20)
            {
                _recentStderr.Dequeue();
            }
        }
        try { _options.StandardErrorLine?.Invoke(line); } catch { }
    }

    private async Task OnProcessGoneAsync()
    {
        int? code = null;
        try
        {
            await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            code = _proc.ExitCode;
        }
        catch { }

        string tail;

        lock (_recentStderr) tail = string.Join(Environment.NewLine, _recentStderr);

        var msg = $"The Python process exited unexpectedly{(code is null ? "" : $" (exit code {code})")}.";

        if (tail.Length > 0) { msg += Environment.NewLine + tail; }

        Fail(new PyException(msg, processExitCode: code));
    }

    // ------------------------------------------------------------------ helpers

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char c = s[++i];
                sb.Append(c switch { 'n' => '\n', 'r' => '\r', _ => c });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Turns requirements.txt text into pip arguments (comments, blank lines, continuations, option lines).</summary>
    public static IEnumerable<string> ParseRequirements(string text)
    {
        var joined = PyRegexAttributes.NewLineRegex().Replace(text, " ");
        foreach (var raw in joined.Split('\n'))
        {
            var line = PyRegexAttributes.WhitespaceRegex().Replace(raw, "").Trim();

            if (line.Length == 0) { continue; }

            if (line.StartsWith('-'))
            {
                // Option lines like "--index-url https://..." or "-e ./pkg" become separate argv items.
                foreach (var part in line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return part;
                }
            }
            else
            {
                yield return line;   // keep "pkg>=1.0 ; python_version>'3.8'" as one argument
            }
        }
    }

    // ------------------------------------------------------------------ installed-package tracking

    private readonly record struct PipEntry(string Key, string Requirement);

    /// <summary>What has been installed for one interpreter. Gate serializes installs so check + pip + record is atomic.</summary>
    private sealed class PipRegistry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Dictionary<string, PipEntry> Installed = new(StringComparer.Ordinal);
    }

    private static readonly ConcurrentDictionary<string, PipRegistry> PipRegistries = new(StringComparer.Ordinal);

    /// <summary>pip install options whose value is the next argument ("--index-url URL"); "--opt=value" needs no lookahead.</summary>
    private static readonly HashSet<string> PipOptionsWithValue = new(StringComparer.Ordinal)
    {
        "-i", "--index-url", "--extra-index-url", "-f", "--find-links", "-r", "--requirement",
        "-c", "--constraint", "-e", "--editable", "-t", "--target", "--prefix", "--root", "--src",
        "--platform", "--python-version", "--implementation", "--abi", "--trusted-host",
        "--upgrade-strategy", "--progress-bar", "--cache-dir", "--log", "--proxy", "--retries",
        "--timeout", "--exists-action", "--cert", "--client-cert", "--no-binary", "--only-binary",
        "-C", "--config-settings", "--global-option", "--report", "--root-user-action",
    };

    /// <summary>Options that themselves name something to install, so pip must still run for them.</summary>
    private static readonly string[] PipInstallTargetOptions = ["-r", "--requirement", "-e", "--editable"];

    // PEP 508-ish: name, optional [extras], then the rest (specifiers, "; marker" or "@ url").
    private static readonly Regex RequirementRx = new(
        @"^\s*([A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)\s*(?:\[([^\]]*)\])?\s*(.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex NameSeparatorRx = new("[-_.]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnyWhitespaceRx = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a requirement into a normalized name ("Foo_Bar" -> "foo-bar") and a comparison key for what was asked
    /// for (extras, specifiers in a fixed order, marker). Returns false for paths, URLs and wheel/sdist files.
    /// </summary>
    private static bool TryParseRequirement(string arg, out string name, out string key)
    {
        name = key = "";
        var trimmed = arg.Trim();
        bool isDirectRef = trimmed.Contains('@');
        if (!isDirectRef &&
            (trimmed.Contains('/') || trimmed.Contains('\\') ||
             trimmed.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) ||
             trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
             trimmed.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
             trimmed.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var m = RequirementRx.Match(trimmed);
        if (!m.Success) { return false; }

        var rest = AnyWhitespaceRx.Replace(m.Groups[3].Value, "");
        if (rest.Length > 0 && "<>=!~@;".IndexOf(rest[0]) < 0) { return false; }   // not a requirement we understand

        name = NameSeparatorRx.Replace(m.Groups[1].Value, "-").ToLowerInvariant();

        var extras = m.Groups[2].Success
            ? string.Join(",", m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(e => NameSeparatorRx.Replace(e, "-").ToLowerInvariant())
                                .Order(StringComparer.Ordinal))
            : "";

        string spec, marker = "";
        if (rest.StartsWith('@'))
        {
            spec = rest;   // direct reference: compare the URL as written
        }
        else
        {
            int semi = rest.IndexOf(';');
            if (semi >= 0) { marker = rest[semi..].ToLowerInvariant(); rest = rest[..semi]; }
            spec = string.Join(",", rest.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        }

        key = $"[{extras}]{spec}{marker}";
        return true;
    }

    /// <summary>
    /// Filters out already-installed requirements, throws on version conflicts, runs pip for what is left and
    /// records it. Returns null when pip was not needed.
    /// </summary>
    private static async Task<string?> InstallTrackedAsync(PySessionOptions options, IEnumerable<string> pipArgs,
                                                           IProgress<PyProgress>? progress, CancellationToken ct)
    {
        var args = pipArgs.ToList();
        if (args.Count == 0) { return null; }

        var reg = PipRegistries.GetOrAdd(options.PythonPath, _ => new PipRegistry());
        await reg.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var toRun = new List<string>(args.Count);
            var pending = new Dictionary<string, PipEntry>(StringComparer.Ordinal);
            bool hasTarget = false;

            for (int i = 0; i < args.Count; i++)
            {
                var arg = args[i];

                if (arg.StartsWith('-'))
                {
                    toRun.Add(arg);
                    var optName = arg.Split('=', 2)[0];
                    if (PipInstallTargetOptions.Contains(optName)) { hasTarget = true; }
                    if (!arg.Contains('=') && PipOptionsWithValue.Contains(arg) && i + 1 < args.Count) { toRun.Add(args[++i]); }
                    continue;
                }

                if (!TryParseRequirement(arg, out var name, out var key))
                {
                    toRun.Add(arg);   // path, URL or file: pip decides
                    hasTarget = true;
                    continue;
                }

                PipEntry existing;
                bool installed;
                lock (reg.Installed) { installed = reg.Installed.TryGetValue(name, out existing); }

                if (installed)
                {
                    if (existing.Key == key) { continue; }   // same package, same requirement: ignore
                    throw new PyException(
                        $"Cannot install '{arg.Trim()}': '{existing.Requirement}' is already installed for '{options.PythonPath}'. " +
                        "A different version cannot be loaded into a running Python process; start a new session " +
                        "(or a separate interpreter/venv) to change it.");
                }

                if (pending.TryGetValue(name, out existing))
                {
                    if (existing.Key == key) { continue; }
                    throw new PyException(
                        $"'{existing.Requirement}' and '{arg.Trim()}' were both requested in the same install with different requirements.");
                }

                pending[name] = new PipEntry(key, arg.Trim());
                toRun.Add(arg);
                hasTarget = true;
            }

            if (!hasTarget) { return null; }

            var output = await RunPipAsync(options, toRun, progress, ct).ConfigureAwait(false);

            lock (reg.Installed)
            {
                foreach (var (name, entry) in pending) { reg.Installed[name] = entry; }
            }
            return output;
        }
        finally
        {
            reg.Gate.Release();
        }
    }
    private static async Task<string> RunPipAsync(PySessionOptions options, IReadOnlyList<string> args,
                                              IProgress<PyProgress>? progress, CancellationToken ct)
    {
        bool useUv = !string.IsNullOrEmpty(options.UvPath);
        string exe = useUv ? options.UvPath! : options.PythonPath;
        string tool = useUv ? "uv pip install" : "pip install";

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (options.WorkingDirectory is not null)
        {
            psi.WorkingDirectory = options.WorkingDirectory;
        }

        string[] prefix = useUv
            ? ["pip", "install", "--python", options.PythonPath]
            : ["-m", "pip", "install", "--disable-pip-version-check", "--no-input"];

        foreach (var arg in prefix) { psi.ArgumentList.Add(arg); }
        foreach (var arg in args) { psi.ArgumentList.Add(arg); }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        if (options.Environment is not null)
        {
            foreach (var (k, v) in options.Environment)
            {
                if (v is null) { psi.Environment.Remove(k); }
                else { psi.Environment[k] = v; }
            }
        }

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();


        void OnLine(string? l)
        {
            if (l is null) { return; }
            lock (sb) { sb.AppendLine(l); }

            try { progress?.Report(new PyProgress(PyProgressKind.Unbounded, InstallTask, -1)); }
            catch { }
        }

        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

        try { proc.Start(); }
        catch (Exception ex)
        {
            throw new PyException($"Could not start '{exe}': {ex.Message}", inner: ex);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }

        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        proc.WaitForExit(); // drain async output events
        string output; lock (sb) output = sb.ToString();

        if (proc.ExitCode != 0)
        {
            throw new PyException($"{tool} failed (exit code {proc.ExitCode}).{System.Environment.NewLine}{output}",
                                  processExitCode: proc.ExitCode);
        }

        try { progress?.Report(new PyProgress(PyProgressKind.Completed, InstallTask)); } catch { }
        return output;
    }
}

public partial class PyRegexAttributes
{
    [GeneratedRegex(@"^\[progress: (.+?)(?:\s+(-?\d+(?:\.\d+)?)\s*%)?\]$", RegexOptions.Compiled)]
    public static partial Regex ProgressRegex();
    [GeneratedRegex(@"^\[completed ([^\]]*)\]$", RegexOptions.Compiled)]
    public static partial Regex CompletedRegex();

    [GeneratedRegex(@"^\[error: (.*)\]$", RegexOptions.Compiled)]
    public static partial Regex ErrorRegex();

    [GeneratedRegex(@"^\[error-type: (.*)\]$", RegexOptions.Compiled)]
    public static partial Regex ErrorTypeRegex();

    [GeneratedRegex(@"^\[traceback: (.*)\]$", RegexOptions.Compiled)]
    public static partial Regex TracebackRegex();

    [GeneratedRegex(@"^\[done: (\S+) (?:ok (-?\d+)|(error)|(canceled))\]$", RegexOptions.Compiled)]
    public static partial Regex DoneRegex();

    [GeneratedRegex(@"^\[ready: (\S+)\]$", RegexOptions.Compiled)]
    public static partial Regex ReadyRegex();




    [GeneratedRegex(@"\\\r?\n")]
    public static partial Regex NewLineRegex();


    [GeneratedRegex(@"(^|\s)#.*$")]
    public static partial Regex WhitespaceRegex();
}