using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PyTest
{
    /// <summary>
    /// A TextWriter that forwards everything to two inner writers.
    /// Every operation is attempted on both writers; if either fails, the first
    /// exception is rethrown after both have been tried (sync and async alike).
    /// Not thread-safe; wrap with TextWriter.Synchronized if needed.
    /// </summary>
    internal class DualTextWriter : TextWriter
    {
        private readonly TextWriter _inner1;
        private readonly TextWriter _inner2;
        private readonly bool _leaveOpen1;
        private readonly bool _leaveOpen2;
        private bool _disposed;

        public DualTextWriter(TextWriter inner1, TextWriter inner2, bool leaveOpen1 = false, bool leaveOpen2 = false)
            : base(inner1?.FormatProvider)
        {
            ArgumentNullException.ThrowIfNull(inner1);
            ArgumentNullException.ThrowIfNull(inner2);
            _inner1 = inner1;
            _inner2 = inner2;
            _leaveOpen1 = leaveOpen1;
            _leaveOpen2 = leaveOpen2;
        }

        public override Encoding Encoding => _inner1.Encoding;

        public override IFormatProvider FormatProvider => _inner1.FormatProvider;

        [AllowNull]
        public override string NewLine
        {
            get => base.NewLine;
            set
            {
                base.NewLine = value;
                _inner1.NewLine = value;
                _inner2.NewLine = value;
            }
        }

        // ---------- Sync writes ----------

        public override void Write(char value) =>
            Both(value, static (w, v) => w.Write(v));

        public override void Write(string? value) =>
            Both(value, static (w, v) => w.Write(v));

        public override void Write(char[] buffer, int index, int count) =>
            Both((buffer, index, count), static (w, s) => w.Write(s.buffer, s.index, s.count));

        public override void Write(ReadOnlySpan<char> buffer)
        {
            // Spans can't be captured in lambdas, so this one is spelled out.
            ThrowIfDisposed();
            ExceptionDispatchInfo? error = null;
            try { _inner1.Write(buffer); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            try { _inner2.Write(buffer); } catch (Exception ex) { error ??= ExceptionDispatchInfo.Capture(ex); }
            error?.Throw();
        }

        public override void WriteLine() =>
            Both<object?>(null, static (w, _) => w.WriteLine());

        public override void WriteLine(string? value) =>
            Both(value, static (w, v) => w.WriteLine(v));

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            ThrowIfDisposed();
            ExceptionDispatchInfo? error = null;
            try { _inner1.WriteLine(buffer); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            try { _inner2.WriteLine(buffer); } catch (Exception ex) { error ??= ExceptionDispatchInfo.Capture(ex); }
            error?.Throw();
        }

        public override void Flush() =>
            Both<object?>(null, static (w, _) => w.Flush());

        // ---------- Async writes ----------

        public override Task WriteAsync(char value) =>
            BothAsync(value, static (w, v) => w.WriteAsync(v));

        public override Task WriteAsync(string? value) =>
            BothAsync(value, static (w, v) => w.WriteAsync(v));

        public override Task WriteAsync(char[] buffer, int index, int count) =>
            BothAsync((buffer, index, count), static (w, s) => w.WriteAsync(s.buffer, s.index, s.count));

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            BothAsync((buffer, cancellationToken), static (w, s) => w.WriteAsync(s.buffer, s.cancellationToken));

        public override Task WriteLineAsync() =>
            BothAsync<object?>(null, static (w, _) => w.WriteLineAsync());

        public override Task WriteLineAsync(char value) =>
            BothAsync(value, static (w, v) => w.WriteLineAsync(v));

        public override Task WriteLineAsync(string? value) =>
            BothAsync(value, static (w, v) => w.WriteLineAsync(v));

        public override Task WriteLineAsync(char[] buffer, int index, int count) =>
            BothAsync((buffer, index, count), static (w, s) => w.WriteLineAsync(s.buffer, s.index, s.count));

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            BothAsync((buffer, cancellationToken), static (w, s) => w.WriteLineAsync(s.buffer, s.cancellationToken));

        public override Task FlushAsync() => FlushAsync(CancellationToken.None);

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            BothAsync(cancellationToken, static (w, ct) => w.FlushAsync(ct));

        // ---------- Disposal ----------
        // No Close() override: the base Close() calls Dispose(true), which handles everything.

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                ExceptionDispatchInfo? error = null;
                try { Release(_inner1, _leaveOpen1); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                try { Release(_inner2, _leaveOpen2); } catch (Exception ex) { error ??= ExceptionDispatchInfo.Capture(ex); }
                base.Dispose(disposing);
                error?.Throw();
                return;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await Task.WhenAll(
                    ReleaseAsync(_inner1, _leaveOpen1),
                    ReleaseAsync(_inner2, _leaveOpen2)).ConfigureAwait(false);
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }

        // ---------- Helpers ----------

        private static void Release(TextWriter writer, bool leaveOpen)
        {
            if (leaveOpen)
                writer.Flush();
            else
                writer.Dispose();
        }

        private static Task ReleaseAsync(TextWriter writer, bool leaveOpen)
        {
            try
            {
                return leaveOpen ? writer.FlushAsync() : writer.DisposeAsync().AsTask();
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        /// <summary>Runs the action on both writers, then rethrows the first failure (if any).</summary>
        private void Both<TState>(TState state, Action<TextWriter, TState> action)
        {
            ThrowIfDisposed();
            ExceptionDispatchInfo? error = null;
            try { action(_inner1, state); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            try { action(_inner2, state); } catch (Exception ex) { error ??= ExceptionDispatchInfo.Capture(ex); }
            error?.Throw();
        }

        /// <summary>Starts the action on both writers concurrently; awaiting the result rethrows the first failure.</summary>
        private Task BothAsync<TState>(TState state, Func<TextWriter, TState, Task> action)
        {
            ThrowIfDisposed();
            return Task.WhenAll(Start(_inner1), Start(_inner2));

            Task Start(TextWriter writer)
            {
                // A synchronous throw from one writer must not prevent the other from starting.
                try { return action(writer, state); }
                catch (Exception ex) { return Task.FromException(ex); }
            }
        }
    }
}