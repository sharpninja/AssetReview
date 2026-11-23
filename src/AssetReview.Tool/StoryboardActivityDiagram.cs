using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SharpNinja.AssetReview.Tool;

/// <summary>
/// Generates a PlantUML activity diagram and renders it with a local PlantUML installation.
/// 2026-11-23: FR-ASSETREVIEW-019 and TR-ASSETREVIEW-STORYBOARD-002, AC-complete-activity
/// and AC-renderer-recovery cover exact storyboard topology and recoverable rendering errors.
/// </summary>
internal static class StoryboardActivityDiagram
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly XName SvgElement = XName.Get("svg", "http://www.w3.org/2000/svg");

    internal static string CreateSource(StoryboardSequence sequence)
    {
        var source = new StringBuilder("@startuml\n");
        if (sequence.Graph is { } graph)
        {
            AppendGraph(source, graph);
        }
        else
        {
            source.Append("(*)");
            for (int index = 0; index < sequence.Frames.Count; index++)
                source.Append(" --> ").AppendLine(DescribeFrame(sequence.Frames[index], $"Frame {index + 1}", index));
            source.AppendLine(" --> (*)");
        }

        return source.AppendLine("@enduml").ToString();
    }

    private static void AppendGraph(StringBuilder source, StoryboardGraph graph)
    {
        var frames = graph.Nodes.Select((node, index) => (Node: node, Index: index))
            .ToDictionary(frame => frame.Node.Id, StringComparer.OrdinalIgnoreCase);
        var outgoing = graph.Transitions.ToLookup(edge => edge.FromFrameId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        string FirstReference(string id)
        {
            var frame = frames[id];
            if (!seen.Add(id))
                return $"f{frame.Index}";

            pending.Enqueue(id);
            return DescribeFrame(frame.Node.Path, frame.Node.Id, frame.Index);
        }

        source.Append("(*) --> ").AppendLine(FirstReference(graph.EntryFrameId!));
        while (pending.TryDequeue(out string? id))
        {
            int index = frames[id].Index;
            foreach (StoryboardGraphTransition edge in outgoing[id])
            {
                source.Append('f').Append(index).Append(edge.Kind == "loop" ? " -up->" : " -->");
                if (edge.Label is not null)
                    source.Append('[').Append(EscapeText(edge.Label)).Append(']');
                source.Append(' ').AppendLine(FirstReference(edge.ToFrameId));
            }

            if (!outgoing.Contains(id))
                source.Append('f').Append(index).AppendLine(" --> (*)");
        }
    }

    private static string DescribeFrame(string path, string id, int index) =>
        $"\"{EscapeText(path)}\\n{EscapeText(id)}\" as f{index}";

    private static string EscapeText(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (Rune character in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(character) || character.Value is ' ' or '.' or ',')
                escaped.Append(character);
            else if (Rune.IsWhiteSpace(character) || Rune.IsControl(character))
                escaped.Append(' ');
            else
                escaped.Append($"<U+{character.Value:X4}>");
        }

        return escaped.ToString();
    }

    internal static async Task<(string? Svg, string? Error)> RenderAsync(string source, CancellationToken cancellationToken)
    {
        string runtimeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetReview");
        string jar = Environment.GetEnvironmentVariable("ASSET_REVIEW_PLANTUML_JAR") ?? Path.Combine(runtimeDirectory, "plantuml.jar");
        if (!File.Exists(jar))
            return (null, "PlantUML is unavailable. Install PlantUML or set ASSET_REVIEW_PLANTUML_JAR to its JAR file.");

        string javaName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        string privateJava = Path.Combine(runtimeDirectory, "jre", "bin", javaName);
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        string java = !string.IsNullOrWhiteSpace(javaHome)
            ? Path.Combine(javaHome, "bin", javaName)
            : File.Exists(privateJava) ? privateJava : javaName;

        var startInfo = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (string argument in new[]
        {
            "-Djava.awt.headless=true", "-DPLANTUML_SECURITY_PROFILE=SANDBOX", "-jar", Path.GetFullPath(jar),
            "-pipe", "-tsvg", "-charset", "UTF-8", "-nometadata",
        })
            startInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return (null, "PlantUML could not start Java.");
        }
        catch (Win32Exception)
        {
            return (null, "PlantUML could not start Java. Check JAVA_HOME or install Java.");
        }

        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(source.AsMemory(), timeout.Token);
            await process.StandardInput.DisposeAsync();
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));

            string svg = await output;
            if (process.ExitCode != 0 || XDocument.Parse(svg).Root?.Name != SvgElement)
                return (null, "PlantUML could not render this activity diagram. Verify that Java and Graphviz are available.");

            return (svg, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "PlantUML took too long to render this activity diagram.");
        }
        catch (Exception exception) when (exception is IOException or XmlException)
        {
            return (null, "PlantUML did not return an activity diagram. Verify that Java and Graphviz are available.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The renderer exited between the status check and the kill request.
                }
            }
        }
    }
}
