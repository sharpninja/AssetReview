using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Playwright;

namespace AssetReview.AiUnit.Tests;

internal sealed class ReviewSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly IBrowserContext _context;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();

    private ReviewSession(
        Process process,
        IBrowserContext context,
        IPage page,
        string url,
        AssetWorkspace workspace)
    {
        _process = process;
        _context = context;
        Page = page;
        Url = url;
        Workspace = workspace;
    }

    public IPage Page { get; }

    public string Url { get; }

    public AssetWorkspace Workspace { get; }

    public string StandardOutput
    {
        get { lock (_stdout) return _stdout.ToString(); }
    }

    public string StandardError
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public static async Task<ReviewSession> StartAsync(
        PlaywrightBrowserFixture browser,
        AssetWorkspace workspace,
        string title,
        CancellationToken cancellationToken)
    {
        var port = ReservePort();
        var process = new Process
        {
            StartInfo = ToolProcess.Create(
            [
                "--server-only",
                "--port",
                port.ToString(CultureInfo.InvariantCulture),
                "--workspace",
                workspace.Workspace,
                "--asset-root",
                workspace.AssetRoot,
                "--feedback-file",
                workspace.FeedbackFile,
                "--title",
                title,
            ],
            workspace.Workspace),
            EnableRaisingEvents = true,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stdout)
                stdout.AppendLine(e.Data);
            const string marker = "Asset Review running at ";
            var index = e.Data.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
                ready.TrySetResult(e.Data[(index + marker.Length)..].Trim());
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderr)
                stderr.AppendLine(e.Data);
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start asset-review.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(ready.Task, exitTask).WaitAsync(TimeSpan.FromSeconds(40), cancellationToken);
            if (completed != ready.Task || ready.Task.IsCanceled || ready.Task.IsFaulted)
            {
                throw new InvalidOperationException(
                    "asset-review exited before it reported a URL. " +
                    $"exit={(process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "running")}\n" +
                    $"stdout:\n{stdout}\nstderr:\n{stderr}");
            }

            var url = await ready.Task;
            if (!url.EndsWith('/'))
                url += "/";

            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Assertions.Expect(page.Locator("#title")).ToHaveTextAsync(title);

            var session = new ReviewSession(process, context, page, url, workspace);
            lock (session._stdout)
                session._stdout.Append(stdout);
            lock (session._stderr)
                session._stderr.Append(stderr);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (session._stdout)
                    session._stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (session._stderr)
                    session._stderr.AppendLine(e.Data);
            };
            return session;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process never started.
            }

            process.Dispose();
            throw;
        }
    }

    public async Task WaitForOutputAsync(string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (StandardOutput.Contains(text, StringComparison.Ordinal))
                return;
            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for '{text}'.\nstdout:\n{StandardOutput}\nstderr:\n{StandardError}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _context.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // The page may already be closed.
        }

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static int ReservePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
