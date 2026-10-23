using PyTest;

using static PyTest.PyDom;


#region Progress
const char _block = '■';
const string _back = "\b\b\b\b\b\b\b\b\b\b\b\b\b\b\b\b\b";
const string _twirl = "-\\|/";

/// <summary>Writes a progress bar like [■■■■      ]  40%.</summary>
/// <param name="percent">The percentage from 0 to 100.</param>
/// <param name="update">False for the first call, true to overwrite the previous bar.</param>
/// <param name="writer">The writer to use; defaults to standard error.</param>
void WriteProgressBar(int percent, bool update = true,TextWriter? writer = null)
{
    if (writer == null) writer = global::System.Console.Error;
    if (update) writer.Write(_back);
    writer.Write("[");
    var p = (int)((percent / 10f) + .5f);
    for (var i = 0; i < 10; ++i)
    {
        writer.Write(i >= p ? ' ' : _block);
    }
    writer.Write("] {0,3:##0}%", percent);
}

/// <summary>Writes a spinning progress indicator for work of unknown length.</summary>
/// <param name="progress">A counter you keep incrementing as work progresses.</param>
/// <param name="update">False for the first call, true to overwrite the previous character.</param>
/// <param name="writer">The writer to use; defaults to standard error.</param>
static void WriteProgress(int progress, bool update = false, TextWriter? writer = null)
{
    if (writer == null) writer = global::System.Console.Error;
    if (update) writer.Write("\b");
    writer.Write(_twirl[progress % _twirl.Length]);
}
#endregion

Console.OutputEncoding = System.Text.Encoding.UTF8;
var opts = new PySessionOptions
{
    //PythonPath = args.Length > 0 ? args[0] : "python3",
    UvPath = "uv",
    CancelGracePeriod = TimeSpan.FromSeconds(2)
};
PyProgress? oldReport = null;
int progCounter = 0;
using var cts = new CancellationTokenSource();
var cancellationToken = cts.Token;

void ReportConsole(PyProgress progress)
{
    if (oldReport==null || progress.Task != oldReport.Task)
    {
        if (oldReport != null && oldReport.Kind!=PyProgressKind.Completed) Console.Error.WriteLine();
        Console.Error.Write(progress.Task + ": ");
    }
    
    switch(progress.Kind)
    {
        case PyProgressKind.Completed:
            Console.Error.WriteLine(" Done!");
            break;
        case PyProgressKind.Unbounded:
            WriteProgress(progCounter++,oldReport!=null, Console.Error);
            break;
        case PyProgressKind.Bounded:
            WriteProgressBar(progress.Value, oldReport!=null, Console.Error);
            break;

    }
    oldReport = progress;
}

Console.CancelKeyPress += (sender, e) =>
{
    if (!cts.IsCancellationRequested)
    {
        e.Cancel = true;   // don't kill the process; let the code clean up
        cts.Cancel();      // signal the token
    }
    // A second Ctrl+C leaves e.Cancel false, so the process exits immediately
};
cancellationToken.Register(() => { });
try
{
    await using (var py = await PySession.StartAsync(opts))
    {
        var progress = new SyncProgress(ReportConsole);
        await py.InstallPackagesAsync(["selenium"], progress, cancellationToken);
        var sw = new StringWriter();
        var emit = new PyEmitter( new DualTextWriter(Console.Out, sw));
        emit.Import("Firefox");
        await py.ExecuteAsync(sw.ToString(), Console.Out, progress, cancellationToken, false);
        Console.ReadLine();
    }
}
catch(OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Operation was canceled by user");
}


sealed class SyncProgress(Action<PyProgress> a) : IProgress<PyProgress> { public void Report(PyProgress v) => a(v); }