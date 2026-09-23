using Compositor.App.Diagnostics;
using Compositor.App.ViewModels;
using Compositor.Core.Document;

namespace Compositor.App.Tests;

public sealed class LoggingTests
{
    [Fact]
    public async Task LogCapturesCommandLifetimeAndFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"compositor-logs-{Guid.NewGuid():N}");
        try
        {
            AppLog.Initialize(directory);
            var observed = false;
            var command = new AsyncRelayCommand(() => throw new InvalidOperationException("diagnostic probe"), onError: _ => observed = true, name: "Test.Probe");
            await command.ExecuteAsync();
            var session = new EditorSession();
            session.CreateDocument(32, 24, emptyLayer: true);
            session.Undo();
            var path = Assert.IsType<string>(AppLog.CurrentPath);
            AppLog.Shutdown();

            var contents = File.ReadAllText(path);
            Assert.True(observed);
            Assert.Contains("Begin Test.Probe", contents);
            Assert.Contains("Failed Test.Probe", contents);
            Assert.Contains("diagnostic probe", contents);
            Assert.Contains("Edit begin: New Canvas", contents);
            Assert.Contains("History undo: New Canvas", contents);
        }
        finally
        {
            AppLog.Shutdown();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
